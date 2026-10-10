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
        bool VsCodeOpen, StateKind ClaudeState, int ClaudeCount, string? Glyph);

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
        MessageBox.Show(ex.Message, "DesktopNames — Projects", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        var result = new List<Project>(folders.Count);
        foreach (var (folder, lastUsed) in folders)
        {
            string name = Path.GetFileName(folder);
            var window = windows.FirstOrDefault(w => w.Workspace.Equals(name, StringComparison.OrdinalIgnoreCase));
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
            result.Add(new Project(folder, name, lastUsed, desktop, window.Hwnd != IntPtr.Zero,
                state, count, glyph));
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
        IntPtr hwnd = HostForm.FindVsCodeWindow(new[] { project.Name });
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
        IntPtr hwnd = HostForm.FindVsCodeWindow(new[] { project.Name });

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
/// The Win+J switcher. Each row shows whether VS Code is open, the Claude state painted the same
/// way as the taskbar tabs, and the desktop as its tab label. Type to filter; Enter opens the
/// selection, Ctrl+Enter creates a new project folder; F2 renames the desktop; Del removes
/// projects from the list. Every column sorts on a header click.
/// </summary>
internal sealed class ProjectsDialog : Form
{
    private enum Col { Number, Desktop, Project, Claude, VsCode, Folder, LastUsed }
    private static readonly string[] Headers = { "#", "Desktop", "Project", "Claude", "", "Folder", "Last used" };

    private readonly ProjectSwitcher _switcher;
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
    private Col _sortCol = Col.Number;
    private bool _sortDesc = false;

    /// <summary>Set when the user picked an existing project.</summary>
    public string? ChosenFolder { get; private set; }
    /// <summary>Set when the user asked for a new project with this folder name.</summary>
    public string? NewProjectName { get; private set; }

    public ProjectsDialog(ProjectSwitcher switcher, string projectsRoot)
    {
        _switcher = switcher;
        _folders = switcher.ListFolders();
        Text = "Projects";
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;

        // Primary screen, whatever desktop or monitor the user is on.
        var work = Screen.PrimaryScreen!.WorkingArea;
        Size = new Size(1040, 600);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(work.Left + (work.Width - Width) / 2, work.Top + (work.Height - Height) / 2);

        int[] widths = { 50, 160, 170, 135, 30, 300, 80 };
        for (int i = 0; i < Headers.Length; i++) _list.Columns.Add(Headers[i], widths[i]);
        // Row height comes from the small image list; 26px fits the tab-style pills.
        _list.SmallImageList = new ImageList { ImageSize = new Size(1, 26) };
        _list.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        // Folder takes the remaining width, so there is never a horizontal scrollbar.
        _list.ClientSizeChanged += (_, _) =>
            _list.Columns[(int)Col.Folder].Width = Math.Max(120, _list.ClientSize.Width - widths.Where((_, i) => i != (int)Col.Folder).Sum());
        _list.DrawSubItem += DrawSubItem;
        _list.ColumnClick += (_, e) => SortBy((Col)e.Column);
        _list.DoubleClick += (_, _) => Accept();
        _list.KeyDown += OnListKeyDown;
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        // Drag a row with an open VS Code onto another project's row to move the window to that desktop.
        _list.AllowDrop = true;
        _list.ItemDrag += OnItemDrag;
        _list.DragOver += OnDragOver;
        _list.DragDrop += OnDragDrop;
        _list.DragLeave += (_, _) => SetDropIndex(-1);

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
        CancelButton = cancel;

        _search.TextChanged += (_, _) => Fill();
        _search.KeyDown += OnSearchKeyDown;
        _open.Click += (_, _) => Accept();
        _create.Click += (_, _) => CreateNew();
        _rename.Click += (_, _) => RenameDesktop();
        _hide.Click += (_, _) => HideSelected();
        Shown += (_, _) => { Activate(); _search.Focus(); };

        RefreshState();
    }

    private static Icon LoadIcon(string name)
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        string res = asm.GetManifestResourceNames().Single(n => n.EndsWith("." + name, StringComparison.OrdinalIgnoreCase));
        using var s = asm.GetManifestResourceStream(res)!;
        return new Icon(s, 16, 16);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _vscodeIcon.Dispose(); _claudeIcon.Dispose(); }
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
        string q = _search.Text.Trim();
        var rows = _projects.Where(p => q.Length == 0 || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                        || (p.Desktop?.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        rows = _sortDesc ? rows.OrderByDescending(SortKey) : rows.OrderBy(SortKey);

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var p in rows)
        {
            var cells = new string[Headers.Length];
            cells[(int)Col.Number] = p.Desktop != null ? $"{p.Desktop.Index + 1}" : "";
            cells[(int)Col.Desktop] = p.Desktop?.Name ?? "";
            cells[(int)Col.Project] = p.Name;
            cells[(int)Col.Claude] = cells[(int)Col.VsCode] = "";
            cells[(int)Col.Folder] = p.Folder;
            cells[(int)Col.LastUsed] = SessionState.FormatAge(p.LastUsedUtc);
            var item = new ListViewItem(cells) { Tag = p };
            item.Selected = selected.Contains(p.Folder);
            _list.Items.Add(item);
        }
        if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0) _list.Items[0].Selected = true;
        for (int i = 0; i < Headers.Length; i++)
            _list.Columns[i].Text = Headers[i] + ((Col)i == _sortCol ? (_sortDesc ? " ▼" : " ▲") : "");
        _list.EndUpdate();

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

    private IComparable SortKey(ProjectSwitcher.Project p) => _sortCol switch
    {
        Col.VsCode   => p.VsCodeOpen,
        // Asking first when descending, matching the taskbar's priority.
        Col.Claude   => p.ClaudeCount == 0 ? 0 : p.ClaudeState switch
                        { StateKind.Asking => 5, StateKind.Error => 4, StateKind.Busy => 3, StateKind.Ready => 2, _ => 1 },
        Col.Project  => p.Name.ToLowerInvariant(),
        Col.Number   => p.Desktop?.Index ?? int.MaxValue,
        Col.Desktop  => p.Desktop?.Name.ToLowerInvariant() ?? "",
        Col.LastUsed => p.LastUsedUtc,
        _            => p.Folder.ToLowerInvariant(),
    };

    private void SortBy(Col col)
    {
        if (_sortCol == col) _sortDesc = !_sortDesc;
        else { _sortCol = col; _sortDesc = col is Col.VsCode or Col.Claude or Col.LastUsed; }
        Fill();
    }

    private void DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        var p = (ProjectSwitcher.Project)e.Item!.Tag!;
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var r = e.Bounds;
        bool sel = e.Item.Selected;
        using (var bg = new SolidBrush(sel ? SystemColors.Highlight : _list.BackColor)) g.FillRectangle(bg, r);
        var fg = sel ? SystemColors.HighlightText : _list.ForeColor;
        const TextFormatFlags Text = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

        switch ((Col)e.ColumnIndex)
        {
            case Col.VsCode:
                if (p.VsCodeOpen) g.DrawIcon(_vscodeIcon, new Rectangle(r.X + (r.Width - 16) / 2, r.Y + (r.Height - 16) / 2, 16, 16));
                break;

            case Col.Claude:
                if (p.ClaudeCount == 0) break;
                g.DrawIcon(_claudeIcon, new Rectangle(r.X + 4, r.Y + (r.Height - 16) / 2, 16, 16));
                var pill = new Rectangle(r.X + 24, r.Y + 3, r.Width - 28, r.Height - 6);
                string word = p.ClaudeState == StateKind.None ? "idle" : p.ClaudeState.ToString().ToLowerInvariant();
                DrawPill(g, pill, $"{word}{(p.Glyph is { } gl && p.ClaudeState == StateKind.Busy ? " " + gl : "")}",
                    p.ClaudeState == StateKind.None ? null : SessionFlyout.StateColor(p.ClaudeState), fg);
                if (p.ClaudeCount >= 2) TaskbarOverlay.DrawCountBadge(g, pill, p.ClaudeCount);
                break;

            default:
                var font = (Col)e.ColumnIndex == Col.Project ? new Font(_list.Font, FontStyle.Bold) : _list.Font;
                TextRenderer.DrawText(g, e.SubItem!.Text, font, Rectangle.Inflate(r, -4, 0), fg, Text);
                if (font != _list.Font) font.Dispose();
                break;
        }

        if (e.ItemIndex == _dropIndex)
        {
            using var ring = new Pen(SystemColors.Highlight, 2f);
            g.DrawLine(ring, r.Left, r.Top + 1, r.Right, r.Top + 1);
            g.DrawLine(ring, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
        }
    }

    private int _dropIndex = -1;

    private void SetDropIndex(int index)
    {
        if (_dropIndex == index) return;
        _dropIndex = index;
        _list.Invalidate();
    }

    private void OnItemDrag(object? sender, ItemDragEventArgs e)
    {
        var p = (ProjectSwitcher.Project)((ListViewItem)e.Item!).Tag!;
        if (!p.VsCodeOpen) return;
        _list.DoDragDrop(p, DragDropEffects.Move);
        SetDropIndex(-1);
    }

    /// <summary>The row under the cursor, if it is a project on a different desktop than the dragged one.</summary>
    private (int index, DesktopInfo desktop)? DropTarget(DragEventArgs e)
    {
        if (e.Data?.GetData(typeof(ProjectSwitcher.Project)) is not ProjectSwitcher.Project source) return null;
        var item = _list.GetItemAt(5, _list.PointToClient(new Point(e.X, e.Y)).Y);
        if (item?.Tag is not ProjectSwitcher.Project { Desktop: { } d } || d.Id == source.Desktop?.Id) return null;
        return (item.Index, d);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var t = DropTarget(e);
        e.Effect = t != null ? DragDropEffects.Move : DragDropEffects.None;
        SetDropIndex(t?.index ?? -1);
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        var t = DropTarget(e);
        SetDropIndex(-1);
        if (t == null) return;
        try { _switcher.MoveVsCode((ProjectSwitcher.Project)e.Data!.GetData(typeof(ProjectSwitcher.Project))!, t.Value.desktop); }
        catch (Exception ex) { ProjectSwitcher.ReportError(ex); }
        RefreshState();
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
        next.EnsureVisible();
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
}
