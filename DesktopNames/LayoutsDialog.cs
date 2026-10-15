namespace DesktopNames;

/// <summary>
/// Every remembered screen layout (one per monitor arrangement): name, which screens, how many
/// VS Code positions it holds, when it was last plugged in. Rename and delete run as commands.
/// </summary>
internal sealed class LayoutsDialog : Form
{
    private readonly Settings _settings;
    private readonly CommandRegistry _commands;
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false };
    private readonly Button _rename = new() { Text = "Rename (F2)", AutoSize = true };
    private readonly Button _delete = new() { Text = "Delete (Del)", AutoSize = true };

    public LayoutsDialog(Settings settings, CommandRegistry commands)
    {
        _settings = settings;
        _commands = commands;
        Text = "Screen layouts";
        Font = new Font("Segoe UI", 10f);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        TopMost = true;
        MinimizeBox = false;
        KeyPreview = true;
        Size = new Size(1200, 420);

        _list.Columns.Add("Name", 180);
        _list.Columns.Add("Screens", 760);
        _list.Columns.Add("VS Code windows", 130);
        _list.Columns.Add("Last used", 90);
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.DoubleClick += (_, _) => Rename();

        var close = new Button { Text = "Close (Esc)", AutoSize = true, DialogResult = DialogResult.Cancel };
        _rename.Click += (_, _) => Rename();
        _delete.Click += (_, _) => Delete();
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        bar.Controls.AddRange(new Control[] { close, _delete, _rename });
        var hint = new Label { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6, 4, 0, 0), ForeColor = SystemColors.GrayText,
            Text = "A layout is remembered per set of screens. The one in use (highlighted) can't be deleted." };
        Controls.Add(_list);
        Controls.Add(hint);
        Controls.Add(bar);
        CancelButton = close;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F2) { Rename(); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete) { Delete(); e.Handled = true; }
        };
        Fill();
    }

    /// <summary>What each row shows, for the control channel.</summary>
    public object Rows() => _list.Items.Cast<ListViewItem>().Select(i => new
    {
        key = (string)i.Tag!,
        name = i.SubItems[0].Text,
        screens = i.SubItems[1].Text,
        windows = i.SubItems[2].Text,
        lastUsed = i.SubItems[3].Text,
        current = (string)i.Tag! == _settings.CurrentLayoutKey,
        selected = i.Selected,
    });

    private void Fill()
    {
        string current = _settings.CurrentLayoutKey;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var (key, l) in _settings.VsCodeLayouts.OrderByDescending(kv => kv.Value.LastUsedUtc ?? DateTime.MinValue))
        {
            var screens = l.Screens.Count == 0 ? l.Describe ?? ""
                : string.Join(" + ", l.Screens.OrderBy(s => s.X).Select(Describe));
            var item = new ListViewItem(new[]
            {
                string.IsNullOrWhiteSpace(l.Name) ? "(unnamed)" : l.Name!,
                screens,
                l.Workspaces.Count.ToString(),
                key == current ? "in use" : l.LastUsedUtc is { } at ? SessionState.FormatAge(at) + " ago" : "unknown",
            }) { Tag = key };
            if (key == current) item.BackColor = SystemColors.Info;
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        UpdateButtons();
    }

    /// <summary>Model and size; some panels' model names already end in their size.</summary>
    private static string Describe(ScreenInfo s)
    {
        string model = string.IsNullOrEmpty(s.Model) ? "screen" : s.Model!;
        return model.Contains($"{s.Width}x{s.Height}") ? model : $"{model} {s.Width}×{s.Height}";
    }

    private List<string> SelectedKeys() => _list.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToList();

    private void UpdateButtons()
    {
        var sel = SelectedKeys();
        _rename.Enabled = sel.Count == 1;
        _delete.Enabled = sel.Count > 0 && !sel.Contains(_settings.CurrentLayoutKey);
    }

    private void Rename()
    {
        var sel = SelectedKeys();
        if (sel.Count != 1) return;
        var layout = _settings.VsCodeLayouts[sel[0]];
        string? name = InputDialog.Show("Screen layout", "Name for this screen layout (e.g. office, home, laptop):", layout.Name ?? "", this);
        if (name == null) return;
        Run("layouts.rename", new { layout = sel[0], name });
    }

    private void Delete()
    {
        var sel = SelectedKeys();
        if (sel.Count == 0 || sel.Contains(_settings.CurrentLayoutKey)) return;
        var names = sel.Select(k => _settings.VsCodeLayouts[k].DisplayName);
        if (MessageBox.Show(this, $"Delete {sel.Count} screen layout(s) and their remembered VS Code positions?\n\n{string.Join("\n", names)}",
                "Screen layouts", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        foreach (var key in sel) Run("layouts.delete", new { layout = key });
    }

    private void Run(string command, object args)
    {
        try { _commands.Execute(command, CommandRegistry.Args(command, _commands, args)); }
        catch (Exception ex) { ProjectSwitcher.ReportError(ex); }
        Fill();
    }
}
