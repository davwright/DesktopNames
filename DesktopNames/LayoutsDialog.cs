namespace DesktopNames;

/// <summary>
/// Remembered screen layouts (one per set of screens and their arrangement) with a to-scale map
/// of the selected one, and every monitor those layouts know about. Layouts and screens can be
/// renamed and deleted; every change runs as a command.
/// </summary>
internal sealed class LayoutsDialog : Form
{
    private readonly Settings _settings;
    private readonly CommandRegistry _commands;
    private readonly ListView _layouts = NewList();
    private readonly ListView _screens = NewList();
    private readonly MapPanel _map = new() { Dock = DockStyle.Right, Width = 440 };
    private readonly Button _renameLayout = new() { Text = "Rename layout", AutoSize = true };
    private readonly Button _deleteLayout = new() { Text = "Delete layout", AutoSize = true };
    private readonly Button _renameScreen = new() { Text = "Rename screen", AutoSize = true };
    private readonly Button _deleteScreen = new() { Text = "Delete screen", AutoSize = true };

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
        Size = new Size(1150, 720);

        _layouts.Columns.Add("Layout", 220);
        _layouts.Columns.Add("Screens", 80);
        _layouts.Columns.Add("VS Code windows", 130);
        _layouts.Columns.Add("Last used", 110);
        _screens.Columns.Add("Screen", 180);
        _screens.Columns.Add("Model", 360);
        _screens.Columns.Add("Size", 110);
        _screens.Columns.Add("In layouts", 90);
        _screens.Columns.Add("Last seen", 110);
        _screens.Columns.Add("Status", 220);

        _layouts.SelectedIndexChanged += (_, _) => { UpdateButtons(); _map.Invalidate(); };
        _screens.SelectedIndexChanged += (_, _) => { UpdateButtons(); _map.Invalidate(); };
        _layouts.DoubleClick += (_, _) => RenameLayout();
        _screens.DoubleClick += (_, _) => RenameScreen();
        _renameLayout.Click += (_, _) => RenameLayout();
        _deleteLayout.Click += (_, _) => DeleteLayouts();
        _renameScreen.Click += (_, _) => RenameScreen();
        _deleteScreen.Click += (_, _) => DeleteScreen();
        _map.Paint += (_, e) => PaintMap(e.Graphics);

        var close = new Button { Text = "Close (Esc)", AutoSize = true, DialogResult = DialogResult.Cancel };
        var layoutArea = new Panel { Dock = DockStyle.Fill };
        layoutArea.Controls.Add(_layouts);
        layoutArea.Controls.Add(_map);

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(6) };
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(Heading("Layouts — one per set of screens and how they are arranged. The one in use is highlighted."));
        grid.Controls.Add(layoutArea);
        grid.Controls.Add(Buttons(_renameLayout, _deleteLayout));
        grid.Controls.Add(Heading("Screens — every monitor a layout remembers. Deleting one deletes the layouts it is part of."));
        grid.Controls.Add(_screens);
        var bottom = Buttons(_renameScreen, _deleteScreen);
        bottom.Controls.Add(close);
        grid.Controls.Add(bottom);
        Controls.Add(grid);
        CancelButton = close;

        // F2 and Del act on whichever list has the focus.
        KeyDown += (_, e) =>
        {
            bool screens = ActiveControl == _screens;
            if (e.KeyCode == Keys.F2) { if (screens) RenameScreen(); else RenameLayout(); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete) { if (screens) DeleteScreen(); else DeleteLayouts(); e.Handled = true; }
        };
        Fill();
    }

    private static ListView NewList() => new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true };

    private static Label Heading(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(0, 6, 0, 4), ForeColor = SystemColors.GrayText };

    private static FlowLayoutPanel Buttons(params Control[] buttons)
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        bar.Controls.AddRange(buttons);
        return bar;
    }

    /// <summary>What both lists show, for the control channel.</summary>
    public object Rows() => new
    {
        layouts = _layouts.Items.Cast<ListViewItem>().Select(i => new
        {
            key = (string)i.Tag!, name = i.SubItems[0].Text, screens = i.SubItems[1].Text, windows = i.SubItems[2].Text,
            lastUsed = i.SubItems[3].Text, selected = i.Selected,
        }),
        screens = _screens.Items.Cast<ListViewItem>().Select(i => new
        {
            key = ((KnownScreen)i.Tag!).Key, name = i.SubItems[0].Text, model = i.SubItems[1].Text, size = i.SubItems[2].Text,
            layouts = i.SubItems[3].Text, lastSeen = i.SubItems[4].Text, status = i.SubItems[5].Text, selected = i.Selected,
        }),
    };

    private void Fill()
    {
        string current = _settings.CurrentLayoutKey;
        var keepLayouts = SelectedLayoutKeys().ToHashSet();
        var keepScreens = SelectedScreens().Select(s => s.Key).ToHashSet();

        _layouts.BeginUpdate();
        _layouts.Items.Clear();
        foreach (var (key, l) in _settings.VsCodeLayouts.OrderByDescending(kv => kv.Key == current).ThenByDescending(kv => kv.Value.LastUsedUtc ?? DateTime.MinValue))
            _layouts.Items.Add(new ListViewItem(new[]
            {
                string.IsNullOrWhiteSpace(l.Name) ? "(unnamed)" : l.Name!,
                l.Screens.Count.ToString(),
                l.Workspaces.Count.ToString(),
                key == current ? "in use" : Age(l.LastUsedUtc),
            }) { Tag = key, Selected = keepLayouts.Contains(key), BackColor = key == current ? SystemColors.Info : _layouts.BackColor });
        if (_layouts.SelectedItems.Count == 0 && _layouts.Items.Count > 0) _layouts.Items[0].Selected = true;
        _layouts.EndUpdate();

        _screens.BeginUpdate();
        _screens.Items.Clear();
        foreach (var k in _settings.KnownScreens())
            _screens.Items.Add(new ListViewItem(new[]
            {
                k.Name ?? "(unnamed)",
                k.Latest.Model ?? "",
                $"{k.Latest.Width}×{k.Latest.Height}",
                k.Layouts.ToString(),
                k.Connected ? "now" : Age(k.LastUsedUtc),
                string.Join(", ", new[] { k.Connected ? $"connected (screen {k.Latest.Number})" : "", k.OverlayHidden ? "overlay off" : "" }.Where(s => s.Length > 0)),
            }) { Tag = k, Selected = keepScreens.Contains(k.Key) });
        _screens.EndUpdate();
        UpdateButtons();
        _map.Invalidate();
    }

    private static string Age(DateTime? utc) => utc is { } at ? SessionState.FormatAge(at) + " ago" : "unknown";

    private List<string> SelectedLayoutKeys() => _layouts.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToList();

    private List<KnownScreen> SelectedScreens() => _screens.SelectedItems.Cast<ListViewItem>().Select(i => (KnownScreen)i.Tag!).ToList();

    private void UpdateButtons()
    {
        var layouts = SelectedLayoutKeys();
        var screens = SelectedScreens();
        _renameLayout.Enabled = layouts.Count == 1;
        _deleteLayout.Enabled = layouts.Count > 0 && !layouts.Contains(_settings.CurrentLayoutKey);
        _renameScreen.Enabled = screens.Count == 1;
        _deleteScreen.Enabled = screens.Count == 1 && !screens[0].Connected;
    }

    /// <summary>The selected layout (or the one in use) to scale; the selected screen highlighted.</summary>
    private void PaintMap(Graphics g)
    {
        var key = SelectedLayoutKeys().FirstOrDefault() ?? _settings.CurrentLayoutKey;
        if (!_settings.VsCodeLayouts.TryGetValue(key, out var layout)) return;
        var focus = SelectedScreens().Select(s => s.Key).ToHashSet();
        ScreenMap.Draw(g, Rectangle.Inflate(_map.ClientRectangle, -10, -10), _map.Font, layout.Screens.Select(s => (
            new Rectangle(s.X, s.Y, s.Width, s.Height),
            $"{s.Label(_settings.ScreenName(s.Key))}\n{(s.Number > 0 ? $"Windows #{s.Number}" : "")}{(s.IsPrimary ? " ★" : "")}",
            focus.Contains(s.Key))).ToList());
    }

    private void RenameLayout()
    {
        var sel = SelectedLayoutKeys();
        if (sel.Count != 1) return;
        string? name = InputDialog.Show("Screen layout", "Name for this screen layout (e.g. office, home, laptop):", _settings.VsCodeLayouts[sel[0]].Name ?? "", this);
        if (name != null) Run("layouts.rename", new { layout = sel[0], name });
    }

    private void DeleteLayouts()
    {
        var sel = SelectedLayoutKeys();
        if (sel.Count == 0 || sel.Contains(_settings.CurrentLayoutKey)) return;
        var names = sel.Select(k => _settings.VsCodeLayouts[k].DisplayName);
        if (MessageBox.Show(this, $"Delete {sel.Count} screen layout(s) and their remembered VS Code positions?\n\n{string.Join("\n", names)}",
                "Screen layouts", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        foreach (var key in sel) Run("layouts.delete", new { layout = key });
    }

    private void RenameScreen()
    {
        var sel = SelectedScreens();
        if (sel.Count != 1) return;
        string? name = InputDialog.Show("Screen", $"Name for {sel[0].Latest.Model} {sel[0].Latest.Width}×{sel[0].Latest.Height} (e.g. office left, home TV):", sel[0].Name ?? "", this);
        if (name != null) Run("screens.rename", new { screen = sel[0].Key, name });
    }

    private void DeleteScreen()
    {
        var sel = SelectedScreens();
        if (sel.Count != 1 || sel[0].Connected) return;
        var s = sel[0];
        var layouts = _settings.VsCodeLayouts.Values.Where(l => l.Screens.Any(x => x.Key == s.Key)).Select(l => l.DisplayName).ToList();
        if (MessageBox.Show(this, $"Forget {s.Name ?? s.Latest.Model} {s.Latest.Width}×{s.Latest.Height}?\n\n" +
                                  $"This deletes the {layouts.Count} layout(s) it is part of:\n{string.Join("\n", layouts)}",
                "Delete screen", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        Run("screens.delete", new { screen = s.Key });
    }

    private void Run(string command, object args)
    {
        try { _commands.Execute(command, CommandRegistry.Args(command, _commands, args)); }
        catch (Exception ex) { ProjectSwitcher.ReportError(ex); }
        Fill();
    }

    private sealed class MapPanel : Panel
    {
        public MapPanel()
        {
            DoubleBuffered = true;
            BackColor = SystemColors.Window;
            BorderStyle = BorderStyle.FixedSingle;
        }
    }
}

/// <summary>Monitors drawn to scale, as Windows' display settings show them.</summary>
internal static class ScreenMap
{
    public static void Draw(Graphics g, Rectangle area, Font font, IReadOnlyList<(Rectangle Bounds, string Label, bool Focus)> screens)
    {
        if (screens.Count == 0) return;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int minX = screens.Min(s => s.Bounds.Left), minY = screens.Min(s => s.Bounds.Top);
        int maxX = screens.Max(s => s.Bounds.Right), maxY = screens.Max(s => s.Bounds.Bottom);
        float scale = Math.Min(area.Width / (float)(maxX - minX), area.Height / (float)(maxY - minY));
        float ox = area.X + (area.Width - (maxX - minX) * scale) / 2, oy = area.Y + (area.Height - (maxY - minY) * scale) / 2;
        foreach (var s in screens)
        {
            var r = Rectangle.Round(new RectangleF(ox + (s.Bounds.Left - minX) * scale, oy + (s.Bounds.Top - minY) * scale,
                s.Bounds.Width * scale, s.Bounds.Height * scale));
            r.Inflate(-2, -2);
            using (var fill = new SolidBrush(s.Focus ? SystemColors.Highlight : SystemColors.Control)) g.FillRectangle(fill, r);
            using (var pen = new Pen(SystemColors.ControlDarkDark)) g.DrawRectangle(pen, r);
            TextRenderer.DrawText(g, s.Label, font, r, s.Focus ? SystemColors.HighlightText : SystemColors.ControlText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
    }
}
