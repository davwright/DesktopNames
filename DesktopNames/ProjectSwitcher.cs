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
    /// <summary>A project row: its folder plus the live state of its desktop, VS Code window and Claude sessions.</summary>
    public sealed record Project(
        string Folder, string Name, DateTime LastUsedUtc, DesktopInfo? Desktop,
        IntPtr VsCodeHwnd, MonitorDescriptor? Screen, SnapMode Snap,
        StateKind ClaudeState, int ClaudeCount, string? Glyph)
    {
        public bool VsCodeOpen => VsCodeHwnd != IntPtr.Zero;
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
                if (_dialog.NewProjectName != null) Open(CreateFolder(_dialog.NewProjectName));
                else Open(_dialog.ChosenFolder!);
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

    /// <summary>Attach the live desktop / VS Code / Claude state to each folder.</summary>
    public List<Project> Describe(List<(string folder, DateTime lastUsedUtc)> folders)
    {
        var desktops = _desktop.GetDesktops();
        var windows = _tracker.EnumerateOpenWorkspaceWindows();
        var screens = MonitorRef.EnumerateAll();
        var result = new List<Project>(folders.Count);
        foreach (var (folder, lastUsed) in folders)
        {
            string name = Path.GetFileName(folder);
            // Its own window (titled with the folder name), else a multi-folder workspace that contains it.
            var window = windows.FirstOrDefault(w => w.Workspace.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (window.Hwnd == IntPtr.Zero)
            {
                var containing = Program.Host!.WorkspaceRootNamesFor(folder);
                window = windows.FirstOrDefault(w => containing.Contains(w.Workspace));
            }
            // Where its VS Code window is, when open (so renames and drags are followed); else the desktop named after it.
            var desktop = (window.Hwnd != IntPtr.Zero ? desktops.FirstOrDefault(d => d.Id == window.DesktopId) : null)
                       ?? desktops.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            StateKind state = StateKind.None;
            int count = 0;
            string? glyph = null;
            if (desktop != null && _sessions != null)
            {
                state = _sessions.GetAggregate(desktop.Id).state;
                count = _sessions.LiveCount(desktop.Id);
                glyph = _sessions.GetGlyph(desktop.Id);
            }
            MonitorDescriptor? screen = null;
            SnapMode snap = SnapMode.Free;
            if (window.Hwnd != IntPtr.Zero)
            {
                IntPtr mon = NativeMethods.MonitorFromWindow(window.Hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
                screen = screens.FirstOrDefault(s => s.Handle == mon);
                if (screen != null) snap = SnapGeometry.Detect(window.Hwnd, screen.Work);
            }
            result.Add(new Project(folder, name, lastUsed, desktop, window.Hwnd, screen, snap, state, count, glyph));
        }
        return result;
    }

    public void HideProjects(IEnumerable<string> folders)
    {
        foreach (var f in folders) _settings.HiddenProjects[Settings.FolderKey(f)] = DateTime.UtcNow;
        _settings.Save();
    }

    /// <summary>Move a project's open VS Code window to another desktop (dragged in the switcher).</summary>
    public void MoveVsCode(Project project, DesktopInfo target)
    {
        IntPtr hwnd = project.VsCodeHwnd;
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"The '{project.Name}' VS Code window is not open.");
        if (!_desktop.MoveWindowToDesktop(hwnd, target.Id))
            throw new InvalidOperationException($"Could not move the '{project.Name}' VS Code window to desktop {target.Index + 1}.");
        // A reassign pin for this folder must follow it, or its Claude sessions resolve to the old desktop.
        string key = Settings.FolderKey(project.Folder);
        if (_settings.FolderDesktops.ContainsKey(key))
        {
            _settings.FolderDesktops[key] = target.Id;
            _settings.Save();
        }
        Log.Projects($"moved VS Code '{project.Name}' -> desktop {target.Index + 1} '{target.Name}'");
    }

    /// <summary>Reorder: put <paramref name="source"/> at <paramref name="targetIndex"/> (dragged # cell).</summary>
    public void MoveDesktop(DesktopInfo source, int targetIndex)
    {
        _desktop.MoveDesktopToIndex(source.Id, targetIndex);
        Log.Projects($"moved desktop '{source.Name}' {source.Index + 1} -> {targetIndex + 1}");
    }

    /// <summary>Reassign every Claude session on <paramref name="source"/> to <paramref name="target"/>
    /// (dragged Claude cell) — the same correction the taskbar flyout's drag makes.</summary>
    public void ReassignClaude(DesktopInfo source, DesktopInfo target)
    {
        var sessions = _sessions?.GetSessions(source.Id) ?? new();
        foreach (var s in sessions) Program.Host!.ReassignSession(s.Source, s.SessionId, s.Cwd, target.Id);
        Log.Projects($"reassigned {sessions.Count} Claude session(s) desktop {source.Index + 1} -> {target.Index + 1}");
    }

    /// <summary>
    /// Put a project's VS Code window on <paramref name="screen"/> in <paramref name="snap"/>,
    /// keeping its desktop, and remember it for this screen setup. Pinned, because it was chosen
    /// by hand: the tracker's scan must not talk it back to wherever the window ends up.
    /// </summary>
    public void PlaceVsCode(Project p, MonitorDescriptor screen, SnapMode snap)
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
        _settings.Workspaces[p.Name] = loc;
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

    /// <summary>
    /// Bring the project's desktop and VS Code window together: its desktop (by name, else the
    /// one its VS Code window is on, else a new one appended at the end), the open VS Code window
    /// moved onto it, or VS Code launched there if none is open.
    /// </summary>
    private void Open(string folder)
    {
        var project = Describe(new() { (folder, DateTime.UtcNow) })[0];
        IntPtr hwnd = project.VsCodeHwnd;

        bool created = project.Desktop == null;
        Guid target = project.Desktop?.Id ?? _desktop.CreateNamedDesktop(project.Name);
        if (created)
        {
            // Slot it in right after the last desktop with a coloured tab (a Claude state or the
            // blue marker), not behind the idle ones at the end.
            var desktops = _desktop.GetDesktops();
            int lastActive = desktops
                .Where(d => d.Id != target && ((_sessions?.GetAggregate(d.Id).state ?? StateKind.None) != StateKind.None
                                               || _settings.IsDesktopHighlighted(d.Id)))
                .Select(d => d.Index).DefaultIfEmpty(0).Max();
            if (lastActive + 1 < desktops.Count - 1) _desktop.MoveDesktopToIndex(target, lastActive + 1);
        }

        if (hwnd != IntPtr.Zero && _desktop.GetDesktopForWindow(hwnd) != target && !_desktop.MoveWindowToDesktop(hwnd, target))
            throw new InvalidOperationException($"Could not move the '{project.Name}' VS Code window to its desktop.");

        _desktop.SwitchToDesktop(_desktop.GetDesktops().First(d => d.Id == target));

        // New windows open on the current desktop, so launch only after the switch.
        if (hwnd == IntPtr.Zero)
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c code \"{folder}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("Could not start VS Code.");
        }

        // A reassign pin for this folder must follow it, or its Claude sessions resolve to the old desktop.
        string key = Settings.FolderKey(folder);
        if (_settings.FolderDesktops.ContainsKey(key))
        {
            _settings.FolderDesktops[key] = target;
            _settings.Save();
        }
        _idleSince.Remove(target);
        Log.Projects($"open {folder} desktop={target}{(created ? " (created)" : "")} window={(hwnd == IntPtr.Zero ? "launched" : "existing")}");
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
    private enum Col { Number, Desktop, Project, Claude, Screen, Folder, LastUsed }

    /// <summary>One list column: its kind, and for a screen column the monitor it stands for.</summary>
    private sealed record ColDef(Col Kind, string Header, int Width, MonitorDescriptor? Screen = null);

    /// <summary>What is being dragged: which project, and which kind of cell the drag started on.</summary>
    private sealed record DragItem(ProjectSwitcher.Project Project, Col Kind);

    private readonly ProjectSwitcher _switcher;
    private readonly List<MonitorDescriptor> _screens;
    private readonly List<ColDef> _cols;
    private List<(string folder, DateTime lastUsedUtc)> _folders;
    private List<ProjectSwitcher.Project> _projects = new();
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
    private int _dropIndex = -1, _dropCol = -1;

    /// <summary>Set when the user picked an existing project.</summary>
    public string? ChosenFolder { get; private set; }
    /// <summary>Set when the user asked for a new project with this folder name.</summary>
    public string? NewProjectName { get; private set; }

    public ProjectsDialog(ProjectSwitcher switcher, string projectsRoot)
    {
        _switcher = switcher;
        _folders = switcher.ListFolders();
        // Left to right as they physically stand.
        _screens = MonitorRef.EnumerateAll().OrderBy(s => s.Monitor.Left).ThenBy(s => s.Monitor.Top).ToList();
        _screenPopup = new ScreenLayoutPopup(_screens);
        _cols = new()
        {
            new(Col.Number, "#", 50),
            new(Col.Desktop, "Desktop", 150),
            new(Col.Project, "Project", 160),
            new(Col.Claude, "Claude", 135),
        };
        for (int i = 0; i < _screens.Count; i++)
            _cols.Add(new(Col.Screen, _screens[i].Number > 0 ? $"Screen {_screens[i].Number}" : $"Screen #{i + 1}", 125, _screens[i]));
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
        _list.MouseDown += (_, e) => _pressCol = ColumnAt(e.Location);
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
        var selected = SelectedProjects().Select(p => p.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Rebuilding the items resets the scroll; put the same row back at the top afterwards.
        int top = _list.TopItem?.Index ?? 0;
        string q = _search.Text.Trim();
        var rows = _projects.Where(p => q.Length == 0 || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                        || (p.Desktop?.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        rows = _sortDesc ? rows.OrderByDescending(SortKey) : rows.OrderBy(SortKey);

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var p in rows)
        {
            var cells = _cols.Select(c => c.Kind switch
            {
                Col.Number => p.Desktop != null ? $"{p.Desktop.Index + 1}" : "",
                Col.Desktop => p.Desktop?.Name ?? "",
                Col.Project => p.Name,
                Col.Folder => p.Folder,
                Col.LastUsed => SessionState.FormatAge(p.LastUsedUtc),
                _ => "",
            }).ToArray();
            _list.Items.Add(new ListViewItem(cells) { Tag = p, Selected = selected.Contains(p.Folder) });
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
        _create.Enabled = q.Length > 0 && !_projects.Any(p => p.Name.Equals(q, StringComparison.OrdinalIgnoreCase));
        _open.Enabled = sel.Count == 1;
        _rename.Enabled = sel.Count == 1 && sel[0].Desktop != null;
        _hide.Enabled = sel.Count > 0;
    }

    private IComparable SortKey(ProjectSwitcher.Project p)
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
            _            => p.Folder.ToLowerInvariant(),
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
        var p = (ProjectSwitcher.Project)e.Item!.Tag!;
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
                var pill = new Rectangle(r.X + 24, r.Y + 3, r.Width - 28, r.Height - 6);
                string word = p.ClaudeState == StateKind.None ? "idle" : p.ClaudeState.ToString().ToLowerInvariant();
                DrawPill(g, pill, $"{word}{(p.Glyph is { } gl && p.ClaudeState == StateKind.Busy ? " " + gl : "")}",
                    p.ClaudeState == StateKind.None ? null : SessionFlyout.StateColor(p.ClaudeState), fg);
                if (p.ClaudeCount >= 2) TaskbarOverlay.DrawCountBadge(g, pill, p.ClaudeCount);
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
        var p = (ProjectSwitcher.Project)((ListViewItem)e.Item!).Tag!;
        var kind = KindAt(_pressCol);
        bool draggable = kind switch
        {
            Col.Screen => p.VsCodeOpen && p.Screen?.Handle == _cols[_pressCol].Screen!.Handle,
            Col.Number => p.Desktop != null,
            Col.Claude => p.Desktop != null && p.ClaudeCount > 0,
            _ => false,
        };
        if (!draggable) return;
        _list.DoDragDrop(new DragItem(p, kind!.Value), DragDropEffects.Move);
        SetDropTarget(-1, -1);
    }

    /// <summary>
    /// Where a drop would land: the row's desktop, plus — for a VS Code chip over a screen
    /// column — that screen. Null when it would change nothing.
    /// </summary>
    private (DragItem drag, int index, int col, DesktopInfo desktop, MonitorDescriptor? screen)? DropTarget(DragEventArgs e)
    {
        if (e.Data?.GetData(typeof(DragItem)) is not DragItem drag) return null;
        var pt = _list.PointToClient(new Point(e.X, e.Y));
        var item = _list.GetItemAt(5, pt.Y);
        if (item?.Tag is not ProjectSwitcher.Project { Desktop: { } d }) return null;
        int col = ColumnAt(pt);
        var screen = drag.Kind == Col.Screen && KindAt(col) == Col.Screen ? _cols[col].Screen : null;
        bool newDesktop = d.Id != drag.Project.Desktop?.Id;
        bool newScreen = screen != null && screen.Handle != drag.Project.Screen?.Handle;
        if (!newDesktop && !newScreen) return null;
        return (drag, item.Index, screen != null ? col : -1, d, screen);
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
        var p = target.drag.Project;
        try
        {
            switch (target.drag.Kind)
            {
                case Col.Screen:
                    if (target.desktop.Id != p.Desktop?.Id) _switcher.MoveVsCode(p, target.desktop);
                    if (target.screen != null && target.screen.Handle != p.Screen?.Handle) _switcher.PlaceVsCode(p, target.screen, p.Snap);
                    break;
                case Col.Number: _switcher.MoveDesktop(p.Desktop!, target.desktop.Index); break;
                case Col.Claude: _switcher.ReassignClaude(p.Desktop!, target.desktop); break;
            }
        }
        catch (Exception ex) { ProjectSwitcher.ReportError(ex); }
        RefreshState();
    }

    /// <summary>Click a VS Code chip: pick its snap position on that screen, applied at once.</summary>
    private void ShowSnapPicker(Point at)
    {
        var item = _list.GetItemAt(5, at.Y);
        var screen = _cols[ColumnAt(at)].Screen!;
        if (item?.Tag is not ProjectSwitcher.Project { VsCodeOpen: true } p || p.Screen?.Handle != screen.Handle) return;

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
            "Type to filter. Enter opens the selected project on its desktop; Ctrl+Enter creates a new project folder.\n\n" +
            "Drag the # cell onto another row to reorder desktops.\n" +
            "Drag a VS Code chip onto another row to move the window to that desktop, and/or into another screen column to move it to that screen.\n" +
            "Click a VS Code chip to change its snap position (full screen, halves, quarters).\n" +
            "Drag the Claude cell onto another row to reassign that desktop's Claude sessions.\n" +
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

    private List<ProjectSwitcher.Project> SelectedProjects() =>
        _list.SelectedItems.Cast<ListViewItem>().Select(i => (ProjectSwitcher.Project)i.Tag!).ToList();

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
        _switcher.HideProjects(sel.Select(p => p.Folder));
        var gone = sel.Select(p => p.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
        ChosenFolder = ((ProjectSwitcher.Project)_list.SelectedItems[0].Tag!).Folder;
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
