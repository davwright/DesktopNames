using System.Diagnostics;

namespace DesktopNames;

/// <summary>
/// One project = one folder = one virtual desktop with the folder's name. The hotkey (Win+J)
/// opens <see cref="ProjectsDialog"/> to pick or create a project; opening one puts its VS Code
/// window on its desktop, creating the desktop at the end of the list if needed. A desktop VS
/// Code has lived on is removed once it has had no VS Code window and no Claude session for
/// <see cref="Settings.ProjectRecycleMinutes"/>; the project itself stays listed, because the
/// list comes from the folders VS Code has opened plus <see cref="Settings.ProjectsRoot"/>.
/// </summary>
internal sealed class ProjectSwitcher : IDisposable
{
    /// <summary>
    /// What runs where: one row per open VS Code window, per Claude session no window claims, and
    /// per project folder with neither. The chain is desktop → VS Code window → its Claude
    /// sessions → its folders. <see cref="Folders"/> empty means the window or session could not
    /// be linked to a folder; <see cref="FoldersAmbiguous"/> means two windows share a title and
    /// nothing tells which folder is which.
    /// </summary>
    public sealed record Row(
        DesktopInfo? Desktop, bool Unresolved,
        IntPtr VsCodeHwnd, string? WindowTitle, MonitorDescriptor? Screen, SnapMode Snap,
        IReadOnlyList<SessionState.SessionRef> Sessions, string? Glyph,
        IReadOnlyList<string> Folders, bool FoldersAmbiguous, DateTime LastUsedUtc)
    {
        public bool VsCodeOpen => VsCodeHwnd != IntPtr.Zero;
        /// <summary>The folder Open, Remove and pins act on.</summary>
        public string? Folder => Folders.Count > 0 ? Folders[0] : null;
        public string Name => WindowTitle ?? (Folder != null ? Path.GetFileName(Folder) : "");
        public int ClaudeCount => Sessions.Count;
        /// <summary>Most urgent state among this row's sessions, the taskbar's order.</summary>
        public StateKind ClaudeState =>
            Sessions.Any(s => s.State == StateKind.Asking) ? StateKind.Asking
            : Sessions.Any(s => s.State == StateKind.Error) ? StateKind.Error
            : Sessions.Any(s => s.State == StateKind.Busy) ? StateKind.Busy
            : Sessions.Any(s => s.State == StateKind.Ready) ? StateKind.Ready
            : StateKind.None;
    }

    private readonly DesktopService _desktop;
    private readonly Settings _settings;
    private readonly VsCodeTracker _tracker;
    private readonly SessionState? _sessions;
    private readonly System.Windows.Forms.Timer _recycleTimer;
    private readonly Dictionary<Guid, DateTime> _idleSince = new();
    private ProjectsDialog? _dialog;

    public ProjectSwitcher(DesktopService desktop, Settings settings, VsCodeTracker tracker, SessionState? sessions)
    {
        _desktop = desktop;
        _settings = settings;
        _tracker = tracker;
        _sessions = sessions;
        _recycleTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _recycleTimer.Tick += (_, _) => RecycleTick();
        _recycleTimer.Start();
    }

    public void Dispose()
    {
        _recycleTimer.Stop();
        _recycleTimer.Dispose();
    }

    /// <summary>Hotkey handler: show the switcher on the primary screen, or bring it forward if open.</summary>
    public void ShowDialog()
    {
        if (_dialog != null) { _dialog.Activate(); return; }
        try
        {
            using (_dialog = new ProjectsDialog(this, _settings.ProjectsRoot))
            {
                if (_sessions != null) _sessions.Changed += OnSessionsChanged;
                try
                {
                    if (_dialog.ShowDialog() != DialogResult.OK) return;
                }
                finally { if (_sessions != null) _sessions.Changed -= OnSessionsChanged; }
                if (_dialog.NewProjectName != null) OpenNew(CreateFolder(_dialog.NewProjectName));
                else Open(_dialog.ChosenRow!);
            }
        }
        catch (Exception ex) { ReportError(ex); }
        finally { _dialog = null; }
    }

    private void OnSessionsChanged(Guid _) => _dialog?.RefreshState();

    public static void ReportError(Exception ex)
    {
        Log.Projects($"switcher failed: {ex}");
        MessageBox.Show(ex.Message, "DesktopNames", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>
    /// Every project folder with when it was last used: subfolders of the projects root plus
    /// every folder VS Code has opened, minus those removed from the list and not used since.
    /// </summary>
    public List<(string folder, DateTime lastUsedUtc)> ListFolders()
    {
        // Root first so its on-disk casing wins over VS Code's lower-cased drive letters.
        var byFolder = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        void Add(string folder, DateTime t)
        {
            if (!byFolder.TryGetValue(folder, out var cur) || t > cur) byFolder[folder] = t;
        }
        foreach (var dir in new DirectoryInfo(_settings.ProjectsRoot).GetDirectories())
            if (!dir.Name.StartsWith('.')) Add(dir.FullName, dir.LastWriteTimeUtc);
        foreach (var (folder, t) in WorkspaceFolderIndex.AllOpenedFolders()) Add(folder, t);

        return byFolder
            .Where(kv => !(_settings.HiddenProjects.TryGetValue(Settings.FolderKey(kv.Key), out var hiddenAt) && kv.Value <= hiddenAt))
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    private sealed class RowBuilder
    {
        public DesktopInfo? Desktop;
        public bool Unresolved;
        public VsCodeTracker.OpenWindow? Window;
        public List<string> Folders = new();
        public bool Ambiguous;
        public List<SessionState.SessionRef> Sessions = new();
    }

    private static bool SameFolder(string a, string b) => Settings.FolderKey(a) == Settings.FolderKey(b);

    private static bool IsUnder(string path, string folder)
    {
        string p = Settings.FolderKey(path), f = Settings.FolderKey(folder);
        return p == f || p.StartsWith(f + "\\", StringComparison.Ordinal);
    }

    /// <summary>
    /// Build the rows from what is actually running. 1: every open VS Code window, with the root
    /// folders of the workspace its title names. 2: every Claude session, onto the window row on
    /// its desktop whose folders its own window has (exact, via the ide lock), else whose folder
    /// its cwd is in; a session no window claims gets its own row on its desktop. 3: every
    /// project folder nothing above has, on the desktop named after it if there is one.
    /// </summary>
    public List<Row> Describe(List<(string folder, DateTime lastUsedUtc)> folders)
    {
        var host = Program.Host!;
        var desktops = _desktop.GetDesktops();
        var windows = _tracker.EnumerateOpenWorkspaceWindows();
        var screens = MonitorRef.EnumerateAll();

        var sessions = new List<(SessionState.SessionRef s, Guid desktop, IReadOnlyList<string> folders)>();
        if (_sessions != null)
            foreach (var id in desktops.Select(d => d.Id).Append(SessionState.UnresolvedDesktopId))
                foreach (var s in _sessions.GetSessions(id))
                    sessions.Add((s, id, host.SessionWorkspaceFolders(s.SessionId)));

        var rows = new List<RowBuilder>();
        foreach (var w in windows)
        {
            var indexed = host.FoldersForWindowTitle(w.Workspace).ToList();
            var named = folders.Where(f => Path.GetFileName(f.folder).Equals(w.Workspace, StringComparison.OrdinalIgnoreCase))
                               .Select(f => f.folder).ToList();
            int sameTitle = windows.Count(o => o.Workspace == w.Workspace);
            List<string> candidates;
            bool ambiguous;
            if (sameTitle == 1)
            {
                // The index knows the workspace's root folders (several for a multi-folder workspace);
                // without it, folders of that name — more than one of those is a guess.
                candidates = indexed.Count > 0 ? indexed : named;
                ambiguous = indexed.Count == 0 && named.Count > 1;
            }
            else
            {
                // Same title on several windows (one folder name in several repos). Only a Claude
                // session on this window's desktop can say which folder this one has, and only if
                // no other window with that title shares the desktop.
                var pool = indexed.Concat(named).DistinctBy(Settings.FolderKey).ToList();
                var onDesktop = sessions.Where(x => x.desktop == w.DesktopId).SelectMany(x => x.folders).ToList();
                var picked = pool.Where(c => onDesktop.Any(f => SameFolder(f, c))).ToList();
                bool alone = windows.Count(o => o.Workspace == w.Workspace && o.DesktopId == w.DesktopId) == 1;
                candidates = alone && picked.Count == 1 ? picked : pool;
                ambiguous = candidates.Count > 1;
            }
            rows.Add(new RowBuilder { Desktop = desktops.FirstOrDefault(d => d.Id == w.DesktopId), Window = w, Folders = candidates, Ambiguous = ambiguous });
        }

        foreach (var (s, id, sf) in sessions)
        {
            var onDesktop = rows.Where(r => r.Window != null && r.Desktop?.Id == id).ToList();
            var row = onDesktop.FirstOrDefault(r => r.Folders.Any(f => sf.Any(x => SameFolder(x, f))))
                   ?? onDesktop.FirstOrDefault(r => s.Cwd.Length > 0 && r.Folders.Any(f => IsUnder(s.Cwd, f)));
            if (row == null)
            {
                string? folder = sf.Count > 0 ? sf[0] : null;
                row = rows.FirstOrDefault(r => r.Window == null && (r.Desktop?.Id ?? SessionState.UnresolvedDesktopId) == id
                                               && (folder == null ? r.Folders.Count == 0 : r.Folders.Any(f => SameFolder(f, folder))));
                if (row == null)
                {
                    row = new RowBuilder
                    {
                        Desktop = desktops.FirstOrDefault(d => d.Id == id),
                        Unresolved = id == SessionState.UnresolvedDesktopId,
                        Folders = sf.ToList(),
                    };
                    rows.Add(row);
                }
            }
            row.Sessions.Add(s);
        }

        foreach (var (folder, _) in folders)
            if (!rows.Any(r => r.Folders.Any(f => SameFolder(f, folder))))
            {
                string name = Path.GetFileName(folder);
                rows.Add(new RowBuilder { Desktop = desktops.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)), Folders = { folder } });
            }

        var lastUsed = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, t) in folders) lastUsed[Settings.FolderKey(folder)] = t;

        return rows.Select(r =>
        {
            MonitorDescriptor? screen = null;
            SnapMode snap = SnapMode.Free;
            if (r.Window is { } w)
            {
                IntPtr mon = NativeMethods.MonitorFromWindow(w.Hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
                screen = screens.FirstOrDefault(s => s.Handle == mon);
                if (screen != null) snap = SnapGeometry.Detect(w.Hwnd, screen.Work);
            }
            // The desktop's glyph belongs to this row only when the row holds all of that desktop's sessions.
            string? glyph = r.Desktop != null && _sessions != null && r.Sessions.Count > 0
                            && r.Sessions.Count == _sessions.GetSessions(r.Desktop.Id).Count ? _sessions.GetGlyph(r.Desktop.Id) : null;
            var used = r.Folders.Select(f => lastUsed.TryGetValue(Settings.FolderKey(f), out var t) ? t : DateTime.MinValue)
                                .DefaultIfEmpty(DateTime.MinValue).Max();
            if (used == DateTime.MinValue && (r.Window != null || r.Sessions.Count > 0)) used = DateTime.UtcNow;
            return new Row(r.Desktop, r.Unresolved, r.Window?.Hwnd ?? IntPtr.Zero, r.Window?.Workspace, screen, snap,
                r.Sessions, glyph, r.Folders, r.Ambiguous, used);
        }).ToList();
    }

    public void HideProjects(IEnumerable<string> folders)
    {
        foreach (var f in folders) _settings.HiddenProjects[Settings.FolderKey(f)] = DateTime.UtcNow;
        _settings.Save();
    }

    /// <summary>Move a project's open VS Code window to another desktop (dragged in the switcher).</summary>
    public void MoveVsCode(Row project, DesktopInfo target)
    {
        IntPtr hwnd = project.VsCodeHwnd;
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"The '{project.Name}' VS Code window is not open.");
        if (!_desktop.MoveWindowToDesktop(hwnd, target.Id))
            throw new InvalidOperationException($"Could not move the '{project.Name}' VS Code window to desktop {target.Index + 1}.");
        FollowPin(project.Folder, target.Id);
        Log.Projects($"moved VS Code '{project.Name}' -> desktop {target.Index + 1} '{target.Name}'");
    }

    /// <summary>Reorder: put <paramref name="source"/> at <paramref name="targetIndex"/> (dragged # cell).</summary>
    public void MoveDesktop(DesktopInfo source, int targetIndex)
    {
        _desktop.MoveDesktopToIndex(source.Id, targetIndex);
        Log.Projects($"moved desktop '{source.Name}' {source.Index + 1} -> {targetIndex + 1}");
    }

    /// <summary>Reassign a row's Claude sessions to <paramref name="target"/> (dragged Claude
    /// cell) — the same correction the taskbar flyout's drag makes.</summary>
    public void ReassignClaude(IReadOnlyCollection<SessionState.SessionRef> sessions, DesktopInfo target)
    {
        foreach (var s in sessions) Program.Host!.ReassignSession(s.Source, s.SessionId, s.Cwd, target.Id);
        Log.Projects($"reassigned {sessions.Count} Claude session(s) [{string.Join(", ", sessions.Select(s => s.Label))}] -> desktop {target.Index + 1}");
    }

    /// <summary>What the taskbar tooltip says about one session, plus its recent messages.</summary>
    public string DescribeSession(SessionState.SessionRef s) => _sessions?.DescribeSession(s.Source, s.SessionId) ?? "";

    /// <summary>A desktop for a row that has none (a VS Code dropped on it): named after it,
    /// placed after the active desktops.</summary>
    public DesktopInfo CreateDesktopFor(Row row)
    {
        Guid id = CreateProjectDesktop(row.Name);
        return _desktop.GetDesktops().First(d => d.Id == id);
    }

    /// <summary>
    /// Put a project's VS Code window on <paramref name="screen"/> in <paramref name="snap"/>,
    /// keeping its desktop, and remember it for this screen setup. Pinned, because it was chosen
    /// by hand: the tracker's scan must not talk it back to wherever the window ends up.
    /// </summary>
    public void PlaceVsCode(Row p, MonitorDescriptor screen, SnapMode snap)
    {
        var loc = new WorkspaceLocation();
        MonitorRef.CaptureWindowPlacement(p.VsCodeHwnd, loc);
        loc.DesktopId = _desktop.GetDesktopForWindow(p.VsCodeHwnd);
        loc.MonitorDeviceId = screen.DeviceId;
        loc.MonitorX = screen.Monitor.Left;
        loc.MonitorY = screen.Monitor.Top;
        loc.MonitorWidth = screen.Width;
        loc.MonitorHeight = screen.Height;
        loc.Snap = snap;
        loc.Pinned = true;
        _desktop.PlaceWindow(p.VsCodeHwnd, loc, Guid.Empty);
        _settings.Workspaces[p.WindowTitle!] = loc;
        _settings.Save();
        Log.Projects($"placed VS Code '{p.Name}' on screen {screen.Number} {snap}");
    }

    /// <summary>Move every open VS Code window onto the desktop with exactly its workspace's name, if there is one.</summary>
    public int AutoAssignByName()
    {
        var desktops = _desktop.GetDesktops();
        int moved = 0;
        foreach (var w in _tracker.EnumerateOpenWorkspaceWindows())
        {
            var d = desktops.FirstOrDefault(d => d.Name.Equals(w.Workspace, StringComparison.OrdinalIgnoreCase));
            if (d == null || d.Id == w.DesktopId) continue;
            if (!_desktop.MoveWindowToDesktop(w.Hwnd, d.Id))
                throw new InvalidOperationException($"Could not move the '{w.Workspace}' VS Code window to desktop {d.Index + 1}.");
            moved++;
        }
        Log.Projects($"auto-assign by name: {moved} window(s) moved");
        return moved;
    }

    /// <summary>Name the current monitor arrangement ("work", "home") — its layout is remembered separately.</summary>
    public string ScreenSetupName => _settings.Layout().DisplayName;

    public void RenameScreenSetup(string name)
    {
        _settings.Layout().Name = name;
        _settings.Save();
    }

    public void RenameDesktop(Guid desktopId, string name)
    {
        _desktop.RenameDesktop(desktopId, name);
        Log.Projects($"renamed desktop {desktopId} -> '{name}'");
    }

    /// <summary>
    /// Desktops with nothing happening: no VS Code window and no Claude session. Never the first
    /// desktop, the current one, or one the user marked blue.
    /// </summary>
    public List<DesktopInfo> IdleDesktops() => IdleDesktops(OpenVsCodeDesktops());

    private List<DesktopInfo> IdleDesktops(HashSet<Guid> hasVsCode) =>
        _desktop.GetDesktops()
            .Where(d => d.Index > 0 && !d.IsCurrent && !_settings.IsDesktopHighlighted(d.Id)
                        && !hasVsCode.Contains(d.Id) && (_sessions?.LiveCount(d.Id) ?? 0) == 0)
            .ToList();

    private HashSet<Guid> OpenVsCodeDesktops() =>
        _tracker.EnumerateOpenWorkspaceWindows().Select(w => w.DesktopId).ToHashSet();

    /// <summary>Remove a desktop and the folder pins pointing at it (they would route Claude sessions nowhere).</summary>
    public void RemoveDesktop(DesktopInfo d, string why)
    {
        _desktop.RemoveDesktop(d.Id);
        _idleSince.Remove(d.Id);
        var pins = _settings.FolderDesktops.Where(kv => kv.Value == d.Id).Select(kv => kv.Key).ToList();
        foreach (var k in pins) _settings.FolderDesktops.Remove(k);
        if (pins.Count > 0) _settings.Save();
        Log.Projects($"removed desktop '{d.Name}' {d.Id} ({why}) unpinned=[{string.Join(";", pins)}]");
    }

    private string CreateFolder(string name)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"\"{name}\" is not a valid folder name.");
        string folder = Path.Combine(_settings.ProjectsRoot, name);
        Directory.CreateDirectory(folder);
        Log.Projects($"created folder {folder}");
        return folder;
    }

    /// <summary>A new project folder: open it the same way as a listed one.</summary>
    private void OpenNew(string folder) =>
        Open(Describe(new() { (folder, DateTime.UtcNow) }).First(r => r.Folders.Any(f => SameFolder(f, folder))));

    /// <summary>
    /// Bring a row's desktop and VS Code window together: its desktop (the one its window is on,
    /// else the one named after it, else a new one after the active desktops), the open VS Code
    /// window moved onto it, or VS Code launched there on its folder if none is open.
    /// </summary>
    private void Open(Row project)
    {
        if (project.Desktop == null && project.Folder == null)
            throw new InvalidOperationException($"'{project.Name}' has neither a desktop nor a folder to open.");
        IntPtr hwnd = project.VsCodeHwnd;

        bool created = project.Desktop == null;
        Guid target = project.Desktop?.Id ?? CreateProjectDesktop(project.Name);

        if (hwnd != IntPtr.Zero && _desktop.GetDesktopForWindow(hwnd) != target && !_desktop.MoveWindowToDesktop(hwnd, target))
            throw new InvalidOperationException($"Could not move the '{project.Name}' VS Code window to its desktop.");

        _desktop.SwitchToDesktop(_desktop.GetDesktops().First(d => d.Id == target));

        // New windows open on the current desktop, so launch only after the switch.
        if (hwnd == IntPtr.Zero && project.Folder != null)
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c code \"{project.Folder}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("Could not start VS Code.");
        }

        FollowPin(project.Folder, target);
        _idleSince.Remove(target);
        Log.Projects($"open '{project.Name}' {project.Folder ?? "(no folder)"} desktop={target}{(created ? " (created)" : "")} window={(hwnd != IntPtr.Zero ? "existing" : project.Folder != null ? "launched" : "none")}");
    }

    /// <summary>
    /// A new desktop named <paramref name="name"/>, slotted in right after the last desktop with a
    /// coloured tab (a Claude state or the blue marker), not behind the idle ones at the end.
    /// </summary>
    private Guid CreateProjectDesktop(string name)
    {
        Guid target = _desktop.CreateNamedDesktop(name);
        var desktops = _desktop.GetDesktops();
        int lastActive = desktops
            .Where(d => d.Id != target && ((_sessions?.GetAggregate(d.Id).state ?? StateKind.None) != StateKind.None
                                           || _settings.IsDesktopHighlighted(d.Id)))
            .Select(d => d.Index).DefaultIfEmpty(0).Max();
        if (lastActive + 1 < desktops.Count - 1) _desktop.MoveDesktopToIndex(target, lastActive + 1);
        Log.Projects($"created desktop '{name}' at {Math.Min(lastActive + 2, desktops.Count)}");
        return target;
    }

    /// <summary>A reassign pin for this folder must follow it, or its Claude sessions resolve to the old desktop.</summary>
    private void FollowPin(string? folder, Guid desktop)
    {
        if (folder == null) return;
        string key = Settings.FolderKey(folder);
        if (!_settings.FolderDesktops.ContainsKey(key)) return;
        _settings.FolderDesktops[key] = desktop;
        _settings.Save();
    }

    private void RecycleTick()
    {
        try { Recycle(); }
        catch (Exception ex)
        {
            // Stop rather than retry every minute against whatever broke; the log says why.
            _recycleTimer.Stop();
            Log.Projects($"recycling stopped: {ex}");
        }
    }

    private void Recycle()
    {
        int minutes = _settings.ProjectRecycleMinutes;
        if (minutes <= 0) return;

        var now = DateTime.UtcNow;
        var hadVsCode = _settings.VsCodeLayouts.Values.SelectMany(l => l.Workspaces.Values).Select(l => l.DesktopId).ToHashSet();
        var idle = IdleDesktops(OpenVsCodeDesktops()).Where(d => hadVsCode.Contains(d.Id)).ToList();

        foreach (var id in _idleSince.Keys.Where(id => !idle.Any(d => d.Id == id)).ToList()) _idleSince.Remove(id);
        foreach (var d in idle)
        {
            if (!_idleSince.TryGetValue(d.Id, out var since)) { _idleSince[d.Id] = now; continue; }
            if (now - since >= TimeSpan.FromMinutes(minutes))
                RemoveDesktop(d, $"recycled, idle since {since.ToLocalTime():HH:mm}");
        }
    }
}

/// <summary>
/// The DesktopNames window (Win+J): one row per project with its desktop, Claude state, and a
/// column per screen showing which screen its VS Code window is on and in which snap position.
/// Type to filter; Enter opens the selection, Ctrl+Enter creates a new project folder; F2
/// renames the desktop; Del removes projects from the list. Drag the # cell to reorder
/// desktops, the Claude cell to reassign its sessions, a VS Code chip to another desktop row
/// and/or screen column. Click a chip to change its snap position. Hovering a screen header
/// shows the monitor arrangement. Every column sorts on a header click.
/// </summary>
internal sealed class ProjectsDialog : Form
{
    private enum Col { Number, Desktop, Screen, Claude, Project, Folder, LastUsed }

    /// <summary>One list column: its kind, and for a screen column the monitor it stands for.</summary>
    private sealed record ColDef(Col Kind, string Header, int Width, MonitorDescriptor? Screen = null);

    /// <summary>What is being dragged: which row, which kind of cell the drag started on, and for
    /// a Claude pill the one session it stands for.</summary>
    private sealed record DragItem(ProjectSwitcher.Row Row, Col Kind, SessionState.SessionRef? Session = null);

    private readonly ProjectSwitcher _switcher;
    private readonly List<MonitorDescriptor> _screens;
    private readonly List<ColDef> _cols;
    private List<(string folder, DateTime lastUsedUtc)> _folders;
    private List<ProjectSwitcher.Row> _projects = new();
    private readonly TextBox _search = new() { Dock = DockStyle.Top, Font = new Font("Segoe UI", 12f), PlaceholderText = "Type to filter, or a new project name" };
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false, OwnerDraw = true };
    // Buttons size to their text: fixed sizes clip at display scaling above 100%.
    private readonly Button _open = new() { Text = "Open (Enter)", AutoSize = true };
    private readonly Button _create = new() { Text = "New project (Ctrl+Enter)", AutoSize = true };
    private readonly Button _rename = new() { Text = "Rename desktop (F2)", AutoSize = true };
    private readonly Button _hide = new() { Text = "Remove from list (Del)", AutoSize = true };
    private readonly Icon _vscodeIcon = LoadIcon("vscode.ico");
    private readonly Icon _claudeIcon = LoadIcon("claude.ico");
    private readonly ScreenLayoutPopup _screenPopup;
    private HeaderHover? _headerHover;
    private int _sortCol;
    private bool _sortDesc;
    private int _pressCol = -1;
    private SessionState.SessionRef? _pressSession;
    private readonly ToolTip _tip = new() { UseAnimation = false, UseFading = false };
    private string? _tipKey;
    private int _dropIndex = -1, _dropCol = -1;

    /// <summary>Set when the user picked an existing row.</summary>
    public ProjectSwitcher.Row? ChosenRow { get; private set; }
    /// <summary>Set when the user asked for a new project with this folder name.</summary>
    public string? NewProjectName { get; private set; }

    public ProjectsDialog(ProjectSwitcher switcher, string projectsRoot)
    {
        _switcher = switcher;
        _folders = switcher.ListFolders();
        // Left to right as they physically stand.
        _screens = MonitorRef.EnumerateAll().OrderBy(s => s.Monitor.Left).ThenBy(s => s.Monitor.Top).ToList();
        _screenPopup = new ScreenLayoutPopup(_screens);
        // Left to right the way things hang together: desktop → VS Code window (per screen) →
        // its Claude sessions → the folders that window has open.
        _cols = new()
        {
            new(Col.Number, "#", 50),
            new(Col.Desktop, "Desktop", 140),
        };
        for (int i = 0; i < _screens.Count; i++)
            _cols.Add(new(Col.Screen, _screens[i].Number > 0 ? $"Screen {_screens[i].Number}" : $"Screen #{i + 1}", 160, _screens[i]));
        _cols.Add(new(Col.Claude, "Claude", 170));
        _cols.Add(new(Col.Project, "Project", 150));
        _cols.Add(new(Col.Folder, "Folder", 280));
        _cols.Add(new(Col.LastUsed, "Last used", 80));

        UpdateTitle();
        Icon = Program.Host!.Icon;
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;

        // Primary screen, whatever desktop or monitor the user is on.
        var work = Screen.PrimaryScreen!.WorkingArea;
        Size = new Size(Math.Min(work.Width - 40, _cols.Sum(c => c.Width) + 60), 640);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(work.Left + (work.Width - Width) / 2, work.Top + (work.Height - Height) / 2);

        foreach (var c in _cols) _list.Columns.Add(c.Header, c.Width);
        // Row height comes from the small image list; 26px fits the tab-style pills.
        _list.SmallImageList = new ImageList { ImageSize = new Size(1, 26) };
        _list.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        // Folder takes the remaining width, so there is never a horizontal scrollbar.
        int folderIdx = _cols.FindIndex(c => c.Kind == Col.Folder);
        _list.ClientSizeChanged += (_, _) =>
            _list.Columns[folderIdx].Width = Math.Max(120, _list.ClientSize.Width - _cols.Where((_, i) => i != folderIdx).Sum(c => c.Width));
        _list.DrawSubItem += DrawSubItem;
        _list.ColumnClick += (_, e) => SortBy(e.Column);
        _list.DoubleClick += (_, _) => { if (KindAt(_pressCol) != Col.Screen) Accept(); };
        _list.KeyDown += OnListKeyDown;
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        // What a drag does depends on the cell it starts on (see OnItemDrag).
        _list.AllowDrop = true;
        _list.MouseDown += (_, e) => { _pressCol = ColumnAt(e.Location); _pressSession = PillAt(e.Location)?.session; };
        _list.MouseMove += (_, e) => UpdateTip(e.Location);
        _list.MouseLeave += (_, _) => { _tipKey = null; _tip.Hide(_list); };
        _list.MouseClick += (_, e) => { if (KindAt(ColumnAt(e.Location)) == Col.Screen) ShowSnapPicker(e.Location); };
        _list.ItemDrag += OnItemDrag;
        _list.DragOver += OnDragOver;
        _list.DragDrop += OnDragDrop;
        _list.DragLeave += (_, _) => SetDropTarget(-1, -1);

        var cancel = new Button { Text = "Cancel (Esc)", AutoSize = true, DialogResult = DialogResult.Cancel };
        var closeIdle = new Button { Text = "Close idle desktops…", AutoSize = true };
        closeIdle.Click += (_, _) => CloseIdleDesktops();
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        bar.Controls.AddRange(new Control[] { cancel, _open, _create, _rename, _hide, closeIdle });
        var hint = new Label { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6, 4, 0, 0), ForeColor = SystemColors.GrayText,
            Text = $"New projects are created in {projectsRoot}" };

        Controls.Add(_list);
        Controls.Add(_search);
        Controls.Add(hint);
        Controls.Add(bar);
        var menu = BuildMenu(closeIdle);
        Controls.Add(menu);
        MainMenuStrip = menu;
        CancelButton = cancel;

        _search.TextChanged += (_, _) => Fill();
        _search.KeyDown += OnSearchKeyDown;
        _open.Click += (_, _) => Accept();
        _create.Click += (_, _) => CreateNew();
        _rename.Click += (_, _) => RenameDesktop();
        _hide.Click += (_, _) => HideSelected();
        Shown += (_, _) =>
        {
            Activate();
            _search.Focus();
            // Only now are the list's handles final; filling it earlier creates and then recreates them.
            _headerHover = new HeaderHover(NativeMethods.SendMessage(_list.Handle, NativeMethods.LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero), OnHeaderHover);
        };

        RefreshState();
    }

    /// <summary>The title names the screen setup DesktopNames recognises from the monitors plugged in.</summary>
    private void UpdateTitle() => Text = $"DesktopNames — {_switcher.ScreenSetupName}";

    private static Icon LoadIcon(string name)
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        string res = asm.GetManifestResourceNames().Single(n => n.EndsWith("." + name, StringComparison.OrdinalIgnoreCase));
        using var s = asm.GetManifestResourceStream(res)!;
        return new Icon(s, 16, 16);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _headerHover?.ReleaseHandle();
            _screenPopup.Dispose();
            _tip.Dispose();
            _vscodeIcon.Dispose();
            _claudeIcon.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Re-read desktops, windows and Claude states (cheap); the folder list stays as loaded.</summary>
    public void RefreshState()
    {
        _projects = _switcher.Describe(_folders);
        Fill();
    }

    private void Fill()
    {
        var selected = SelectedProjects().Select(Key).ToHashSet();
        // Rebuilding the items resets the scroll; put the same row back at the top afterwards.
        int top = _list.TopItem?.Index ?? 0;
        string q = _search.Text.Trim();
        var rows = _projects.Where(p => q.Length == 0 || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                        || (p.Desktop?.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                                        || p.Folders.Any(f => Path.GetFileName(f).Contains(q, StringComparison.OrdinalIgnoreCase)));
        rows = _sortDesc ? rows.OrderByDescending(SortKey) : rows.OrderBy(SortKey);

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var p in rows)
        {
            var cells = _cols.Select(c => c.Kind switch
            {
                Col.Number => p.Desktop != null ? $"{p.Desktop.Index + 1}" : p.Unresolved ? "?" : "",
                Col.Desktop => p.Desktop?.Name ?? (p.Unresolved ? "unresolved" : ""),
                Col.Project => p.Name,
                Col.Folder => p.Folders.Count == 0 ? (p.VsCodeOpen || p.ClaudeCount > 0 ? "not linked" : "")
                            : p.FoldersAmbiguous ? string.Join(" | ", p.Folders) + "  (can't tell which)"
                            : string.Join("; ", p.Folders),
                Col.LastUsed => SessionState.FormatAge(p.LastUsedUtc),
                _ => "",
            }).ToArray();
            _list.Items.Add(new ListViewItem(cells) { Tag = p, Selected = selected.Contains(Key(p)) });
        }
        if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0) _list.Items[0].Selected = true;
        for (int i = 0; i < _cols.Count; i++)
            _list.Columns[i].Text = _cols[i].Header + (i == _sortCol ? (_sortDesc ? " ▼" : " ▲") : "");
        _list.EndUpdate();
        if (top > 0 && _list.Items.Count > 0) _list.TopItem = _list.Items[Math.Min(top, _list.Items.Count - 1)];

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        string q = _search.Text.Trim();
        var sel = SelectedProjects();
        _create.Enabled = q.Length > 0 && !_projects.Any(p => p.Name.Equals(q, StringComparison.OrdinalIgnoreCase)
                                                            || p.Folders.Any(f => Path.GetFileName(f).Equals(q, StringComparison.OrdinalIgnoreCase)));
        _open.Enabled = sel.Count == 1 && (sel[0].Desktop != null || sel[0].Folder != null);
        _rename.Enabled = sel.Count == 1 && sel[0].Desktop != null;
        _hide.Enabled = sel.Any(p => p.Folders.Count > 0);
    }

    /// <summary>Identity of a row across refreshes, for keeping the selection.</summary>
    private static string Key(ProjectSwitcher.Row p) => $"{p.VsCodeHwnd}|{p.Folder}|{p.Desktop?.Id}";

    private IComparable SortKey(ProjectSwitcher.Row p)
    {
        var c = _cols[_sortCol];
        return c.Kind switch
        {
            // Asking first when descending, matching the taskbar's priority.
            Col.Claude   => p.ClaudeCount == 0 ? 0 : p.ClaudeState switch
                            { StateKind.Asking => 5, StateKind.Error => 4, StateKind.Busy => 3, StateKind.Ready => 2, _ => 1 },
            Col.Project  => p.Name.ToLowerInvariant(),
            Col.Number   => p.Desktop?.Index ?? int.MaxValue,
            Col.Desktop  => p.Desktop?.Name.ToLowerInvariant() ?? "",
            // Windows on this screen first.
            Col.Screen   => p.Screen?.Handle == c.Screen!.Handle ? 0 : 1,
            Col.LastUsed => p.LastUsedUtc,
            _            => (p.Folder ?? "").ToLowerInvariant(),
        };
    }

    private void SortBy(int col)
    {
        if (_sortCol == col) _sortDesc = !_sortDesc;
        else { _sortCol = col; _sortDesc = _cols[col].Kind is Col.Claude or Col.LastUsed; }
        Fill();
    }

    private void DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        var p = (ProjectSwitcher.Row)e.Item!.Tag!;
        var c = _cols[e.ColumnIndex];
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var r = e.Bounds;
        bool sel = e.Item.Selected;
        using (var bg = new SolidBrush(sel ? SystemColors.Highlight : _list.BackColor)) g.FillRectangle(bg, r);
        var fg = sel ? SystemColors.HighlightText : _list.ForeColor;
        const TextFormatFlags Text = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

        switch (c.Kind)
        {
            case Col.Claude:
                if (p.ClaudeCount == 0) break;
                g.DrawIcon(_claudeIcon, new Rectangle(r.X + 4, r.Y + (r.Height - 16) / 2, 16, 16));
                // One pill per session, so two Claudes in one window are two things to hover and drag.
                var pills = ClaudePills(r, p.Sessions.Count);
                for (int i = 0; i < pills.Count; i++)
                {
                    var st = p.Sessions[i].State;
                    string word = st == StateKind.None ? "idle" : st.ToString().ToLowerInvariant();
                    string label = pills[i].Width >= 46
                        ? word + (p.Sessions.Count == 1 && st == StateKind.Busy && p.Glyph is { } gl ? " " + gl : "")
                        : word[..1].ToUpperInvariant();
                    DrawPill(g, pills[i], label, st == StateKind.None ? null : SessionFlyout.StateColor(st), fg);
                }
                break;

            case Col.Screen:
                // The VS Code chip sits in the column of the screen its window is on.
                if (!p.VsCodeOpen || p.Screen?.Handle != c.Screen!.Handle) break;
                var chip = new Rectangle(r.X + 3, r.Y + 3, r.Width - 6, r.Height - 6);
                using (var path = TaskbarOverlay.RoundedRect(chip, 6))
                using (var pen = new Pen(Color.FromArgb(140, 0, 122, 204)))
                    g.DrawPath(pen, path);
                g.DrawIcon(_vscodeIcon, new Rectangle(chip.X + 4, chip.Y + (chip.Height - 16) / 2, 16, 16));
                TextRenderer.DrawText(g, SnapGeometry.Label(p.Snap), _list.Font,
                    new Rectangle(chip.X + 24, chip.Y, chip.Width - 26, chip.Height), fg, Text);
                break;

            case Col.Folder when p.Folders.Count == 0:
                using (var italic = new Font(_list.Font, FontStyle.Italic))
                    TextRenderer.DrawText(g, e.SubItem!.Text, italic, Rectangle.Inflate(r, -4, 0), sel ? fg : SystemColors.GrayText, Text);
                break;

            default:
                var font = c.Kind == Col.Project ? new Font(_list.Font, FontStyle.Bold) : _list.Font;
                TextRenderer.DrawText(g, e.SubItem!.Text, font, Rectangle.Inflate(r, -4, 0), fg, Text);
                if (font != _list.Font) font.Dispose();
                break;
        }

        if (e.ItemIndex == _dropIndex)
        {
            using var ring = new Pen(SystemColors.Highlight, 2f);
            if (e.ColumnIndex == _dropCol) g.DrawRectangle(ring, Rectangle.Inflate(r, -1, -1));
            else
            {
                g.DrawLine(ring, r.Left, r.Top + 1, r.Right, r.Top + 1);
                g.DrawLine(ring, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
            }
        }
    }

    private void SetDropTarget(int index, int col)
    {
        if (_dropIndex == index && _dropCol == col) return;
        _dropIndex = index;
        _dropCol = col;
        _list.Invalidate();
    }

    private int ColumnAt(Point clientPoint)
    {
        var hit = _list.HitTest(clientPoint);
        if (hit.Item == null || hit.SubItem == null) return -1;
        return hit.Item.SubItems.IndexOf(hit.SubItem);
    }

    private Col? KindAt(int col) => col >= 0 ? _cols[col].Kind : null;

    /// <summary>
    /// A drag means what its starting cell shows: a VS Code chip moves that window (to another
    /// desktop row and/or screen column), the # reorders the desktop, the Claude cell reassigns
    /// that desktop's sessions. Any other cell doesn't drag.
    /// </summary>
    private void OnItemDrag(object? sender, ItemDragEventArgs e)
    {
        var p = (ProjectSwitcher.Row)((ListViewItem)e.Item!).Tag!;
        var kind = KindAt(_pressCol);
        bool draggable = kind switch
        {
            Col.Screen => p.VsCodeOpen && p.Screen?.Handle == _cols[_pressCol].Screen!.Handle,
            Col.Number => p.Desktop != null,
            Col.Claude => p.Desktop != null && p.ClaudeCount > 0,
            _ => false,
        };
        if (!draggable) return;
        _tipKey = null;
        _tip.Hide(_list);
        _list.DoDragDrop(new DragItem(p, kind!.Value, kind == Col.Claude ? _pressSession : null), DragDropEffects.Move);
        SetDropTarget(-1, -1);
    }

    /// <summary>
    /// Where a drop would land: the row's desktop, plus — for a VS Code chip over a screen
    /// column — that screen. Null when it would change nothing.
    /// </summary>
    private (DragItem drag, int index, int col, ProjectSwitcher.Row row, DesktopInfo? desktop, MonitorDescriptor? screen)? DropTarget(DragEventArgs e)
    {
        if (e.Data?.GetData(typeof(DragItem)) is not DragItem drag) return null;
        var pt = _list.PointToClient(new Point(e.X, e.Y));
        var item = _list.GetItemAt(5, pt.Y);
        if (item?.Tag is not ProjectSwitcher.Row row) return null;
        var d = row.Desktop;
        // A VS Code dropped on a project that has no desktop gets one created for it.
        if (d == null && !(drag.Kind == Col.Screen && row.Folder != null)) return null;
        int col = ColumnAt(pt);
        var screen = drag.Kind == Col.Screen && KindAt(col) == Col.Screen ? _cols[col].Screen : null;
        bool newDesktop = d == null || d.Id != drag.Row.Desktop?.Id;
        bool newScreen = screen != null && screen.Handle != drag.Row.Screen?.Handle;
        if (!newDesktop && !newScreen) return null;
        return (drag, item.Index, screen != null ? col : -1, row, d, screen);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var t = DropTarget(e);
        e.Effect = t != null ? DragDropEffects.Move : DragDropEffects.None;
        SetDropTarget(t?.index ?? -1, t?.col ?? -1);
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        var t = DropTarget(e);
        SetDropTarget(-1, -1);
        if (t is not { } target) return;
        var p = target.drag.Row;
        try
        {
            switch (target.drag.Kind)
            {
                case Col.Screen:
                    var desk = target.desktop ?? _switcher.CreateDesktopFor(target.row);
                    if (desk.Id != p.Desktop?.Id) _switcher.MoveVsCode(p, desk);
                    if (target.screen != null && target.screen.Handle != p.Screen?.Handle) _switcher.PlaceVsCode(p, target.screen, p.Snap);
                    break;
                case Col.Number: _switcher.MoveDesktop(p.Desktop!, target.desktop!.Index); break;
                case Col.Claude:
                    _switcher.ReassignClaude(target.drag.Session is { } one ? new[] { one } : p.Sessions.ToArray(), target.desktop!);
                    break;
            }
        }
        catch (Exception ex) { ProjectSwitcher.ReportError(ex); }
        RefreshState();
    }

    /// <summary>Where each of a row's session pills sits inside its Claude cell, after the icon.</summary>
    private static List<Rectangle> ClaudePills(Rectangle cell, int n)
    {
        const int gap = 3;
        var rects = new List<Rectangle>(n);
        if (n == 0) return rects;
        int x0 = cell.X + 24, avail = cell.Right - 3 - x0;
        int w = Math.Max(12, (avail - gap * (n - 1)) / n);
        for (int i = 0; i < n; i++) rects.Add(new Rectangle(x0 + i * (w + gap), cell.Y + 3, w, cell.Height - 6));
        return rects;
    }

    /// <summary>The Claude session pill under a point in the list, if any.</summary>
    private (ProjectSwitcher.Row row, SessionState.SessionRef session)? PillAt(Point pt)
    {
        var hit = _list.HitTest(pt);
        if (hit.Item?.Tag is not ProjectSwitcher.Row row || hit.SubItem == null) return null;
        if (_cols[hit.Item.SubItems.IndexOf(hit.SubItem)].Kind != Col.Claude) return null;
        int i = ClaudePills(hit.SubItem.Bounds, row.Sessions.Count).FindIndex(r => r.Contains(pt));
        return i < 0 ? null : (row, row.Sessions[i]);
    }

    /// <summary>Hovering a Claude pill shows that session's state, message, background work and recent messages.</summary>
    private void UpdateTip(Point pt)
    {
        var hit = PillAt(pt);
        string? key = hit is { } h ? h.session.Source + "|" + h.session.SessionId : null;
        if (key == _tipKey) return;
        _tipKey = key;
        string text = hit is { } x ? SessionState.StripTipMarkers(_switcher.DescribeSession(x.session)) : "";
        if (text.Length == 0) { _tip.Hide(_list); return; }
        _tip.Show(text, _list, pt.X + 16, pt.Y + 20, 30_000);
    }

    /// <summary>Click a VS Code chip: pick its snap position on that screen, applied at once.</summary>
    private void ShowSnapPicker(Point at)
    {
        var item = _list.GetItemAt(5, at.Y);
        var screen = _cols[ColumnAt(at)].Screen!;
        if (item?.Tag is not ProjectSwitcher.Row { VsCodeOpen: true } p || p.Screen?.Handle != screen.Handle) return;

        var menu = new ContextMenuStrip();
        foreach (var snap in SnapGeometry.All)
            menu.Items.Add(new ToolStripMenuItem(SnapGeometry.Label(snap), null, (_, _) =>
            {
                try { _switcher.PlaceVsCode(p, screen, snap); }
                catch (Exception ex) { ProjectSwitcher.ReportError(ex); }
                RefreshState();
            }) { Checked = snap == p.Snap });
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(_list, at);
    }

    /// <summary>Mouse over the column header (x in header pixels), or null when it left.
    /// Over a screen column, show the monitor arrangement with that screen highlighted.</summary>
    private void OnHeaderHover(int? x)
    {
        int col = -1;
        if (x is int px)
        {
            int left = 0;
            for (int i = 0; i < _list.Columns.Count; i++)
            {
                int w = _list.Columns[i].Width;
                if (px >= left && px < left + w) { col = i; break; }
                left += w;
            }
        }
        if (col < 0 || _cols[col].Kind != Col.Screen) { _screenPopup.Hide(); return; }

        int cellLeft = 0;
        for (int i = 0; i < col; i++) cellLeft += _list.Columns[i].Width;
        // Just under the header's real bottom edge, so the popup never covers the header and ends its own hover.
        NativeMethods.GetWindowRect(_headerHover!.Handle, out var header);
        _screenPopup.ShowFor(_cols[col].Screen!, new Point(header.Left + cellLeft, header.Bottom + 2));
    }

    private MenuStrip BuildMenu(Button closeIdle)
    {
        var menu = new MenuStrip { Dock = DockStyle.Top };

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add("Open settings.json", null, (_, _) => Program.OpenSettingsFile());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("E&xit DesktopNames", null, (_, _) =>
        {
            // After this dialog's modal loop has ended, not from inside it.
            Close();
            Program.Host!.BeginInvoke(() => Program.Host.Close());
        });

        var windows = new ToolStripMenuItem("&Windows");
        var moveAll = new ToolStripMenuItem("Move all VS Code windows to");
        var restore = new ToolStripMenuItem("Restore VS Code layout", null, (_, _) => { Program.Host!.RestoreVsCodeLayout(); RefreshState(); });
        windows.DropDownOpening += (_, _) =>
        {
            moveAll.DropDownItems.Clear();
            for (int i = 0; i < _screens.Count; i++)
            {
                var s = _screens[i];
                moveAll.DropDownItems.Add($"{s.Caption(i)}  ({s.Width}×{s.Height}{(s.IsPrimary ? ", primary" : "")})", null,
                    (_, _) => { Program.Host!.MoveAllVsCodeToScreen(s); RefreshState(); });
            }
            restore.Text = $"Restore VS Code layout  ({_switcher.ScreenSetupName})";
        };
        windows.DropDownItems.Add(moveAll);
        windows.DropDownItems.Add(restore);
        windows.DropDownItems.Add("Put each VS Code on the desktop with its name", null, (_, _) =>
        {
            try { _switcher.AutoAssignByName(); }
            catch (Exception ex) { ProjectSwitcher.ReportError(ex); }
            RefreshState();
        });
        windows.DropDownItems.Add("Rename this screen setup…", null, (_, _) =>
        {
            string? name = InputDialog.Show("Screen setup", "Name for this monitor arrangement (e.g. work, kitchen, bedroom):", _switcher.ScreenSetupName, this);
            if (string.IsNullOrWhiteSpace(name)) return;
            _switcher.RenameScreenSetup(name.Trim());
            UpdateTitle();
        });
        windows.DropDownItems.Add(new ToolStripSeparator());
        windows.DropDownItems.Add("Close idle desktops…", null, (_, _) => closeIdle.PerformClick());

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add("Keyboard shortcuts…", null, (_, _) => Program.ShowShortcuts());
        help.DropDownItems.Add("How this window works…", null, (_, _) => MessageBox.Show(this,
            "One row per open VS Code window, per Claude session no window claims, and per project folder with neither. " +
            "Columns run desktop → VS Code (one column per screen) → its Claude sessions → its folders. " +
            "\"not linked\" means the window or session could not be tied to a folder.\n\n" +
            "Type to filter. Enter opens the selected row on its desktop; Ctrl+Enter creates a new project folder.\n\n" +
            "Drag the # cell onto another row to reorder desktops.\n" +
            "Drag a VS Code chip onto another row to move the window to that desktop, and/or into another screen column to move it to that screen.\n" +
            "Click a VS Code chip to change its snap position (full screen, halves, quarters).\n" +
            "Each Claude session is a pill: hover it for its recent messages, drag it onto another row to reassign that session.\n" +
            "Dropping a VS Code chip on a project with no desktop creates that desktop.\n" +
            "Hover a screen column's header to see the monitor arrangement.\n\n" +
            "F2 renames the desktop, Del removes projects from the list. Click a column header to sort.\n" +
            "The title shows the screen setup (Windows → Rename this screen setup).\n" +
            "Closing this window keeps DesktopNames running; File → Exit quits it.",
            "DesktopNames", MessageBoxButtons.OK, MessageBoxIcon.Information));
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add("About DesktopNames…", null, (_, _) => MessageBox.Show(this,
            $"DesktopNames {Program.GetBuildStamp()}\n\nNamed virtual desktops on the taskbar, Claude session status, and one desktop per project.",
            "About DesktopNames", MessageBoxButtons.OK, MessageBoxIcon.Information));

        menu.Items.AddRange(new ToolStripItem[] { file, windows, help });
        return menu;
    }

    private void DrawPill(Graphics g, Rectangle rect, string label, Color? fill, Color plainText)
    {
        using var path = TaskbarOverlay.RoundedRect(rect, 6);
        if (fill is Color c)
        {
            using var b = new SolidBrush(c);
            g.FillPath(b, path);
        }
        else
        {
            using var pen = new Pen(Color.FromArgb(120, 128, 128, 128));
            g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, label, _list.Font, rect, fill is Color f ? TaskbarOverlay.PickContrastText(f) : plainText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private List<ProjectSwitcher.Row> SelectedProjects() =>
        _list.SelectedItems.Cast<ListViewItem>().Select(i => (ProjectSwitcher.Row)i.Tag!).ToList();

    private void RenameDesktop()
    {
        var sel = SelectedProjects();
        if (sel.Count != 1 || sel[0].Desktop is not { } d) return;
        string? name = InputDialog.Show("Rename desktop", $"New name for desktop {d.Index + 1}:", d.Name, this);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == d.Name) return;
        try { _switcher.RenameDesktop(d.Id, name.Trim()); }
        catch (Exception ex) { ProjectSwitcher.ReportError(ex); }
        RefreshState();
    }

    private void HideSelected()
    {
        var sel = SelectedProjects();
        if (sel.Count == 0) return;
        var gone = sel.SelectMany(p => p.Folders).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (gone.Count == 0) return;
        _switcher.HideProjects(gone);
        _folders = _folders.Where(f => !gone.Contains(f.folder)).ToList();

        // Keep the cursor where it was (now on the next row), so Del can clear a run of rows.
        int at = _list.SelectedIndices.Cast<int>().Min();
        RefreshState();
        if (_list.Items.Count == 0) return;
        _list.SelectedItems.Clear();
        var next = _list.Items[Math.Min(at, _list.Items.Count - 1)];
        next.Selected = true;
        next.Focused = true;
    }

    private void CloseIdleDesktops()
    {
        var idle = _switcher.IdleDesktops();
        if (idle.Count == 0)
        {
            MessageBox.Show(this, "No idle desktops — every desktop has VS Code or a Claude session, or is the first, current or blue one.",
                "Close idle desktops", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string names = string.Join("\n", idle.Select(d => $"  {d.Index + 1}. {d.Name}"));
        if (MessageBox.Show(this, $"Close {idle.Count} desktop(s) with no VS Code and no Claude session?\n\n{names}\n\n" +
                                  "Any other windows on them move to desktop 1.",
                "Close idle desktops", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        try
        {
            foreach (var d in idle) _switcher.RemoveDesktop(d, "closed from the switcher");
        }
        catch (Exception ex) { ProjectSwitcher.ReportError(ex); }
        RefreshState();
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Enter: Accept(); break;
            case Keys.F2: RenameDesktop(); break;
            case Keys.Delete: HideSelected(); break;
            default: return;
        }
        e.SuppressKeyPress = true;
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Down or Keys.Up && _list.Items.Count > 0)
        {
            int i = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : 0;
            i = Math.Clamp(i + (e.KeyCode == Keys.Down ? 1 : -1), 0, _list.Items.Count - 1);
            _list.SelectedItems.Clear();
            _list.Items[i].Selected = true;
            _list.EnsureVisible(i);
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Enter)
        {
            if (e.Control) { if (_create.Enabled) CreateNew(); }
            else if (_list.SelectedItems.Count > 0) Accept();
            else if (_create.Enabled) CreateNew();
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.F2) { RenameDesktop(); e.SuppressKeyPress = true; }
    }

    private void Accept()
    {
        if (_list.SelectedItems.Count == 0) return;
        var row = (ProjectSwitcher.Row)_list.SelectedItems[0].Tag!;
        if (row.Desktop == null && row.Folder == null) return;
        ChosenRow = row;
        DialogResult = DialogResult.OK;
    }

    private void CreateNew()
    {
        NewProjectName = _search.Text.Trim();
        DialogResult = DialogResult.OK;
    }

    /// <summary>
    /// Watches the list's column header (a separate native window) for the mouse, so hovering a
    /// screen column can show the monitor arrangement. Reports the x position, or null on leave.
    /// </summary>
    private sealed class HeaderHover : NativeWindow
    {
        private readonly Action<int?> _onHover;
        private bool _tracking;

        public HeaderHover(IntPtr header, Action<int?> onHover)
        {
            _onHover = onHover;
            AssignHandle(header);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_MOUSEMOVE)
            {
                if (!_tracking)
                {
                    // WM_MOUSELEAVE arrives only after asking for it, once per entry.
                    var tme = new NativeMethods.TRACKMOUSEEVENT
                    {
                        cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.TRACKMOUSEEVENT>(),
                        dwFlags = NativeMethods.TME_LEAVE,
                        hwndTrack = Handle,
                    };
                    _tracking = NativeMethods.TrackMouseEvent(ref tme);
                }
                _onHover((short)(m.LParam.ToInt64() & 0xFFFF));
            }
            else if (m.Msg == NativeMethods.WM_MOUSELEAVE)
            {
                _tracking = false;
                _onHover(null);
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>
    /// Miniature of the physical monitor arrangement drawn from the real monitor coordinates,
    /// with one screen highlighted and its pixels, position, Windows display number, model and
    /// device id spelled out. Shown without taking focus.
    /// </summary>
    private sealed class ScreenLayoutPopup : Form
    {
        private readonly List<MonitorDescriptor> _screens;
        private MonitorDescriptor? _focus;

        public ScreenLayoutPopup(List<MonitorDescriptor> screens)
        {
            _screens = screens;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = SystemColors.Window;
            Font = new Font("Segoe UI", 9f);
            Size = new Size(480, 300);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        public void ShowFor(MonitorDescriptor screen, Point at)
        {
            if (_focus == screen && Visible) return;
            _focus = screen;
            Location = at;
            if (!Visible) Show();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var border = new Pen(SystemColors.ControlDark)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            if (_focus == null || _screens.Count == 0) return;

            // Scale the union of all monitor rects into the map area, keeping proportions.
            var map = new Rectangle(12, 12, Width - 24, 150);
            int minX = _screens.Min(s => s.Monitor.Left), minY = _screens.Min(s => s.Monitor.Top);
            int maxX = _screens.Max(s => s.Monitor.Right), maxY = _screens.Max(s => s.Monitor.Bottom);
            float scale = Math.Min(map.Width / (float)(maxX - minX), map.Height / (float)(maxY - minY));
            float ox = map.X + (map.Width - (maxX - minX) * scale) / 2, oy = map.Y + (map.Height - (maxY - minY) * scale) / 2;

            foreach (var s in _screens)
            {
                var r = Rectangle.Round(new RectangleF(ox + (s.Monitor.Left - minX) * scale, oy + (s.Monitor.Top - minY) * scale,
                    s.Width * scale, s.Height * scale));
                r.Inflate(-2, -2);
                bool focus = s.Handle == _focus.Handle;
                using (var fill = new SolidBrush(focus ? SystemColors.Highlight : SystemColors.Control)) g.FillRectangle(fill, r);
                using (var pen = new Pen(SystemColors.ControlDarkDark)) g.DrawRectangle(pen, r);
                TextRenderer.DrawText(g, $"{(s.Number > 0 ? s.Number.ToString() : "?")}{(s.IsPrimary ? " ★" : "")}\n{s.Width}×{s.Height}",
                    Font, r, focus ? SystemColors.HighlightText : SystemColors.ControlText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            }

            var f = _focus;
            string details =
                $"Screen {(f.Number > 0 ? f.Number.ToString() : "?")}{(f.IsPrimary ? " (primary)" : "")}{(f.Model.Length > 0 ? " — " + f.Model : "")}\n" +
                $"{f.Width}×{f.Height} pixels at ({f.Monitor.Left}, {f.Monitor.Top}); work area {f.Work.Right - f.Work.Left}×{f.Work.Bottom - f.Work.Top}\n" +
                $"GDI device: {f.Device}\n" +
                $"Device id: {f.DeviceId ?? "(none)"}";
            TextRenderer.DrawText(g, details, Font, new Rectangle(12, map.Bottom + 10, Width - 24, Height - map.Bottom - 16),
                SystemColors.WindowText, TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
        }
    }
}
