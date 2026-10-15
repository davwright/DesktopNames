using System.Drawing.Drawing2D;

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
    private readonly MapPanel _map = new() { Dock = DockStyle.Right, Width = 520 };
    private readonly Button _renameLayout = new() { Text = "Rename layout (F2)", AutoSize = true };
    private readonly Button _deleteLayout = new() { Text = "Delete layout (Del)", AutoSize = true };
    private readonly Button _renameScreen = new() { Text = "Rename screen (F2)", AutoSize = true };
    private readonly Button _deleteScreen = new() { Text = "Delete screen (Del)", AutoSize = true };
    private List<string> _mapKeys = new();
    private List<Rectangle> _mapRects = new();

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
        Size = new Size(1200, 760);

        _layouts.Columns.Add("Layout", 220);
        _layouts.Columns.Add("Screens", 80);
        _layouts.Columns.Add("VS Code windows", 130);
        _layouts.Columns.Add("Last used", 110);
        _screens.Columns.Add("Screen", 180);
        _screens.Columns.Add("Manufacturer", 110);
        _screens.Columns.Add("Model", 300);
        _screens.Columns.Add("Size", 110);
        _screens.Columns.Add("In layouts", 90);
        _screens.Columns.Add("Last seen", 110);
        _screens.Columns.Add("Status", 220);
        _layouts.LabelEdit = true;
        _screens.LabelEdit = true;

        _layouts.SelectedIndexChanged += (_, _) => { UpdateButtons(); _map.Invalidate(); };
        _screens.SelectedIndexChanged += (_, _) => { UpdateButtons(); _map.Invalidate(); };
        _layouts.AfterLabelEdit += (_, e) => OnRenamed(_layouts, e);
        _screens.AfterLabelEdit += (_, e) => OnRenamed(_screens, e);
        _renameLayout.Click += (_, _) => BeginRename(_layouts);
        _deleteLayout.Click += (_, _) => DeleteLayouts();
        _renameScreen.Click += (_, _) => BeginRename(_screens);
        _deleteScreen.Click += (_, _) => DeleteScreen();
        _map.Paint += (_, e) => PaintMap(e.Graphics);
        _map.MouseClick += (_, e) =>
        {
            int hit = _mapRects.FindIndex(r => r.Contains(e.Location));
            if (hit >= 0) SelectMapScreen(hit);
        };

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
        grid.Controls.Add(Heading("Layouts — one per set of screens and how they are arranged. The one in use is highlighted. Click a screen on the map to pick it below."));
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
            var list = ActiveControl == _screens ? _screens : _layouts;
            if (e.KeyCode == Keys.F2) { BeginRename(list); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete) { if (list == _screens) DeleteScreen(); else DeleteLayouts(); e.Handled = true; }
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
            key = ((KnownScreen)i.Tag!).Key, name = i.SubItems[0].Text, maker = i.SubItems[1].Text, model = i.SubItems[2].Text,
            size = i.SubItems[3].Text, layouts = i.SubItems[4].Text, lastSeen = i.SubItems[5].Text, status = i.SubItems[6].Text, selected = i.Selected,
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
                ScreenNaming.Maker(k.Latest.DeviceId),
                ScreenNaming.Model(k.Latest.DeviceId, k.Latest.Model, k.Latest.BuiltIn),
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
        _mapKeys = layout.Screens.Select(s => s.Key).ToList();
        _mapRects = ScreenMap.Draw(g, Rectangle.Inflate(_map.ClientRectangle, -12, -12), _map.Font, layout.Screens.Select(s => new ScreenMap.Tile(
            new Rectangle(s.X, s.Y, s.Width, s.Height), s.Number, s.IsPrimary, _settings.ScreenName(s.Key),
            ScreenNaming.Describe(s.DeviceId, s.Model, s.BuiltIn), focus.Contains(s.Key))).ToList());
    }

    /// <summary>A screen clicked on the map becomes the selected row of the screens list, ready for F2.</summary>
    public void SelectMapScreen(int index)
    {
        if (index < 0 || index >= _mapKeys.Count) throw new ArgumentException($"screen must be 0..{_mapKeys.Count - 1}");
        var item = _screens.Items.Cast<ListViewItem>().First(i => ((KnownScreen)i.Tag!).Key == _mapKeys[index]);
        _screens.SelectedItems.Clear();
        item.Selected = true;
        item.Focused = true;
        _screens.EnsureVisible(item.Index);
        _screens.Focus();
    }

    /// <summary>Rename in place, in the list. The command runs once the edit has closed: refilling
    /// the list inside its own label-edit event isn't allowed.</summary>
    private static void BeginRename(ListView list)
    {
        if (list.SelectedItems.Count != 1) return;
        list.Focus();
        list.SelectedItems[0].BeginEdit();
    }

    private void OnRenamed(ListView list, LabelEditEventArgs e)
    {
        e.CancelEdit = true;
        if (e.Label == null || e.Label == "(unnamed)") return;
        string name = e.Label.Trim();
        var tag = list.Items[e.Item].Tag!;
        BeginInvoke(() =>
        {
            if (tag is KnownScreen s) Run("screens.rename", new { screen = s.Key, name });
            else Run("layouts.rename", new { layout = (string)tag, name });
        });
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
            BackColor = ScreenMap.Background;
            Cursor = Cursors.Hand;
        }
    }
}

/// <summary>Monitors drawn to scale the way Windows' display settings draw them: a big number per
/// screen, its width along the top edge, its height up the left edge, name and model along the bottom.
/// The main screen has its own color.</summary>
internal static class ScreenMap
{
    public sealed record Tile(Rectangle Bounds, int Number, bool Primary, string? Name, string Model, bool Focus);

    public static readonly Color Background = Color.FromArgb(32, 32, 32);
    private static readonly Color TileFill = Color.FromArgb(52, 52, 52);
    private static readonly Color MainFill = Color.FromArgb(28, 84, 70);
    private static readonly Color Accent = Color.FromArgb(0, 120, 215);
    private static readonly Color EdgeText = Color.FromArgb(185, 185, 185);

    /// <summary>Draws the tiles and returns where each one landed, in input order (for hit-testing).</summary>
    public static List<Rectangle> Draw(Graphics g, Rectangle area, Font font, IReadOnlyList<Tile> screens)
    {
        var drawn = new List<Rectangle>();
        if (screens.Count == 0) return drawn;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        int minX = screens.Min(s => s.Bounds.Left), minY = screens.Min(s => s.Bounds.Top);
        int maxX = screens.Max(s => s.Bounds.Right), maxY = screens.Max(s => s.Bounds.Bottom);
        float scale = Math.Min(area.Width / (float)(maxX - minX), area.Height / (float)(maxY - minY));
        float ox = area.X + (area.Width - (maxX - minX) * scale) / 2, oy = area.Y + (area.Height - (maxY - minY) * scale) / 2;
        using var small = new Font(font.FontFamily, 7.5f);
        using var edge = new SolidBrush(EdgeText);
        foreach (var s in screens)
        {
            var r = Rectangle.Round(new RectangleF(ox + (s.Bounds.Left - minX) * scale, oy + (s.Bounds.Top - minY) * scale,
                s.Bounds.Width * scale, s.Bounds.Height * scale));
            r.Inflate(-3, -3);
            drawn.Add(r);
            using (var path = Rounded(r, 5))
            using (var fill = new SolidBrush(s.Focus ? Accent : s.Primary ? MainFill : TileFill))
                g.FillPath(fill, path);

            using (var big = new Font(font.FontFamily, Math.Max(10f, r.Height * 0.32f), GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, s.Number > 0 ? s.Number.ToString() : "?", big, r, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // Edge labels only where they fit; the hover popup's tiles are too small for them.
            int lh = small.Height;
            if (r.Height < lh * 4 || r.Width < 90) continue;
            TextRenderer.DrawText(g, s.Bounds.Width.ToString(), small, new Rectangle(r.X, r.Y + 2, r.Width, lh), EdgeText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
            var state = g.Save();
            g.TranslateTransform(r.X + 2, r.Bottom);
            g.RotateTransform(-90);
            g.DrawString(s.Bounds.Height.ToString(), small, edge, new RectangleF(0, 0, r.Height, lh),
                new StringFormat { Alignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap });
            g.Restore(state);
            string bottom = string.Join(" · ", new[] { s.Name, s.Model }.Where(t => !string.IsNullOrEmpty(t)));
            TextRenderer.DrawText(g, bottom, small, new Rectangle(r.X + lh + 2, r.Bottom - lh - 3, r.Width - lh - 6, lh), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        return drawn;
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
