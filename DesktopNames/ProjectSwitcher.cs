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
    public sealed record Project(string Folder, string Name, DateTime LastUsedUtc, DesktopInfo? Desktop);

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
            using (_dialog = new ProjectsDialog(ListProjects(), _settings.ProjectsRoot))
            {
                if (_dialog.ShowDialog() != DialogResult.OK) return;
                if (_dialog.NewProjectName != null) Open(CreateFolder(_dialog.NewProjectName));
                else Open(_dialog.ChosenFolder!);
            }
        }
        catch (Exception ex)
        {
            Log.Projects($"switcher failed: {ex}");
            MessageBox.Show(ex.Message, "DesktopNames — Projects", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { _dialog = null; }
    }

    private List<Project> ListProjects()
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

        var desktops = _desktop.GetDesktops();
        return byFolder
            .Select(kv =>
            {
                string name = Path.GetFileName(kv.Key);
                return new Project(kv.Key, name, kv.Value,
                    desktops.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
            })
            .OrderByDescending(p => p.LastUsedUtc)
            .ToList();
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
    /// Bring the project's desktop and VS Code window together: the desktop named after the
    /// folder (appended at the end if missing), the open VS Code window moved onto it, or VS
    /// Code launched there if none is open.
    /// </summary>
    private void Open(string folder)
    {
        string name = Path.GetFileName(folder);
        IntPtr hwnd = HostForm.FindVsCodeWindow(new[] { name });

        var named = _desktop.GetDesktops().FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        bool created = named == null;
        Guid target = named?.Id ?? _desktop.CreateNamedDesktop(name);

        if (hwnd != IntPtr.Zero && _desktop.GetDesktopForWindow(hwnd) != target && !_desktop.MoveWindowToDesktop(hwnd, target))
            throw new InvalidOperationException($"Could not move the '{name}' VS Code window to its desktop.");

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
        var hasVsCode = _tracker.EnumerateOpenWorkspaceWindows().Select(w => w.DesktopId).ToHashSet();

        foreach (var d in _desktop.GetDesktops())
        {
            bool candidate = d.Index > 0 && !d.IsCurrent && hadVsCode.Contains(d.Id) && !_settings.IsDesktopHighlighted(d.Id);
            bool inUse = hasVsCode.Contains(d.Id) || (_sessions?.LiveCount(d.Id) ?? 0) > 0;
            if (!candidate || inUse) { _idleSince.Remove(d.Id); continue; }
            if (!_idleSince.TryGetValue(d.Id, out var since)) { _idleSince[d.Id] = now; continue; }
            if (now - since < TimeSpan.FromMinutes(minutes)) continue;

            _desktop.RemoveDesktop(d.Id);
            _idleSince.Remove(d.Id);
            var pins = _settings.FolderDesktops.Where(kv => kv.Value == d.Id).Select(kv => kv.Key).ToList();
            foreach (var k in pins) _settings.FolderDesktops.Remove(k);
            if (pins.Count > 0) _settings.Save();
            Log.Projects($"recycled desktop '{d.Name}' {d.Id} (no VS Code / Claude since {since.ToLocalTime():HH:mm}) unpinned=[{string.Join(";", pins)}]");
        }
    }
}

/// <summary>
/// The Win+J switcher: type to filter the project list, Enter opens the selection, or — when
/// nothing matches — creates a new project folder with the typed name under the projects root.
/// </summary>
internal sealed class ProjectsDialog : Form
{
    private readonly List<ProjectSwitcher.Project> _projects;
    private readonly TextBox _search = new() { Dock = DockStyle.Top, Font = new Font("Segoe UI", 12f), PlaceholderText = "Type to filter, or a new project name" };
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    private readonly Button _open = new() { Text = "Open", Width = 90 };
    private readonly Button _create = new() { Text = "New project", Width = 110 };

    /// <summary>Set when the user picked an existing project.</summary>
    public string? ChosenFolder { get; private set; }
    /// <summary>Set when the user asked for a new project with this folder name.</summary>
    public string? NewProjectName { get; private set; }

    public ProjectsDialog(List<ProjectSwitcher.Project> projects, string projectsRoot)
    {
        _projects = projects;
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
        Size = new Size(760, 560);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(work.Left + (work.Width - Width) / 2, work.Top + (work.Height - Height) / 2);

        _list.Columns.Add("Project", 200);
        _list.Columns.Add("Desktop", 90);
        _list.Columns.Add("Last used", 90);
        _list.Columns.Add("Folder", 340);

        var cancel = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        bar.Controls.AddRange(new Control[] { cancel, _open, _create });
        var root = new Label { Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(6, 4, 0, 0), ForeColor = SystemColors.GrayText,
            Text = $"Enter opens the selected project · Ctrl+Enter creates a new one in {projectsRoot}" };

        Controls.Add(_list);
        Controls.Add(_search);
        Controls.Add(root);
        Controls.Add(bar);
        CancelButton = cancel;

        _search.TextChanged += (_, _) => Fill();
        _search.KeyDown += OnSearchKeyDown;
        _list.DoubleClick += (_, _) => Accept();
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { Accept(); e.SuppressKeyPress = true; } };
        _open.Click += (_, _) => Accept();
        _create.Click += (_, _) => CreateNew();
        Shown += (_, _) => { Activate(); _search.Focus(); };

        Fill();
    }

    private void Fill()
    {
        string q = _search.Text.Trim();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var p in _projects)
        {
            if (q.Length > 0 && !p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            var item = new ListViewItem(new[]
            {
                p.Name,
                p.Desktop != null ? $"{p.Desktop.Index + 1}" : "",
                SessionState.FormatAge(p.LastUsedUtc),
                p.Folder,
            }) { Tag = p };
            if (p.Desktop != null) item.Font = new Font(_list.Font, FontStyle.Bold);
            _list.Items.Add(item);
        }
        if (_list.Items.Count > 0) _list.Items[0].Selected = true;
        _list.EndUpdate();

        bool exact = _projects.Any(p => p.Name.Equals(q, StringComparison.OrdinalIgnoreCase));
        _create.Enabled = q.Length > 0 && !exact;
        _open.Enabled = _list.Items.Count > 0;
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Down or Keys.Up && _list.Items.Count > 0)
        {
            int i = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : 0;
            i = Math.Clamp(i + (e.KeyCode == Keys.Down ? 1 : -1), 0, _list.Items.Count - 1);
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
