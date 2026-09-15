namespace DesktopNames;

/// <summary>
/// Modal grid for placing open VS Code windows: rows = virtual desktops, columns = physical
/// monitors. Each open workspace is a draggable chip sitting in the cell it will land in
/// (seeded from its remembered position for the current screen setup, falling back to where it
/// currently is). Each chip also carries a snap position — full screen, a half, a quarter, or
/// as-is. On OK every window is placed accordingly and the result is saved into the layout for
/// the monitor arrangement plugged in right now (<see cref="Settings.VsCodeLayouts"/>), so the
/// work / home / laptop-only arrangements each keep their own remembered positions.
/// </summary>
internal sealed class ArrangeWindowsDialog : Form
{
    private sealed class Chip
    {
        public required IntPtr Hwnd { get; init; }
        public required string Workspace { get; init; }
        public SnapMode Snap { get; set; }
        public required Label Label { get; init; }
    }

    private const int LabelCol = 170, ScreenCol = 220, HeaderRow = 74, ChipHeight = 26, ChipGap = 6;

    private readonly DesktopService _desktop;
    private readonly Settings _settings;
    private readonly ScreenLayout _layout;
    private readonly List<DesktopInfo> _desktops;
    private readonly List<MonitorDescriptor> _screens;
    private readonly List<(FlowLayoutPanel panel, DesktopInfo desktop, MonitorDescriptor screen)> _bodyCells = new();
    private readonly ContextMenuStrip _snapMenu = new();
    private readonly Label _setupLabel = new();
    private readonly ToolTip _tips = new();
    private TableLayoutPanel? _grid;
    private Panel? _bar;
    private Panel? _header;
    private Label? _hint;
    private Label? _dragChip;

    /// <summary>
    /// WS_EX_COMPOSITED: paint the whole child tree into one off-screen buffer. Without it a
    /// drop repaints each grid cell as its row height is recomputed, so the rows above the
    /// drop visibly flash one after the other.
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x02000000;
            return cp;
        }
    }

    public ArrangeWindowsDialog(DesktopService desktop, Settings settings, VsCodeTracker tracker)
    {
        _desktop = desktop;
        _settings = settings;
        _layout = settings.Layout();
        _desktops = desktop.GetDesktops();
        _screens = MonitorRef.EnumerateAll();

        Text = "Arrange VS Code windows — drag each to a desktop / screen";
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MinimumSize = new Size(480, 300);
        Font = new Font("Segoe UI", 9f);

        var windows = tracker.EnumerateOpenWorkspaceWindows();

        _bar = BuildButtonBar(out var ok);
        _header = BuildSetupHeader();
        Controls.Add(BuildGrid(windows));
        Controls.Add(_header);
        Controls.Add(_bar);

        if (_screens.Count == 0) ok.Enabled = false;
        else RelayoutRows();
    }

    /// <summary>
    /// Which monitor arrangement we're editing: a rename link so the user can call it
    /// "work" / "home" / "single", plus a miniature of the real physical arrangement so there's
    /// no guessing which column is which screen.
    /// </summary>
    private Panel BuildSetupHeader()
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = 172 };
        var row = new Panel { Dock = DockStyle.Top, Height = 28 };
        _setupLabel.AutoSize = true;
        _setupLabel.Location = new Point(10, 7);
        UpdateSetupLabel();

        var rename = new LinkLabel { Text = "rename setup…", AutoSize = true, Location = new Point(0, 7) };
        rename.Click += (_, _) =>
        {
            var name = InputDialog.Show("Screen setup",
                "Name this monitor arrangement (work, home, single…):", _layout.Name ?? "", this);
            if (name == null) return;
            _layout.Name = name.Trim().Length == 0 ? null : name.Trim();
            _settings.Save();
            UpdateSetupLabel();
        };
        row.Resize += (_, _) => rename.Left = Math.Max(_setupLabel.Right + 12, row.Width - rename.Width - 14);
        row.Controls.Add(_setupLabel);
        row.Controls.Add(rename);

        var map = new ScreenMap(_screens) { Dock = DockStyle.Fill };
        map.ScreenClicked += MoveAllToColumn;
        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 18,
            Text = "Your actual screen arrangement — click a screen to move every window onto it",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.Gray,
        };

        // Docked children are laid out in reverse add-order, so add Fill first.
        panel.Controls.Add(map);
        panel.Controls.Add(hint);
        panel.Controls.Add(row);
        return panel;
    }

    /// <summary>
    /// Miniature of the physical monitor arrangement, drawn from the real monitor coordinates
    /// the way Windows' own display settings draw it, labelled with Windows' display numbers.
    /// Clicking a screen moves every window onto it.
    /// </summary>
    private sealed class ScreenMap : Panel
    {
        private readonly List<MonitorDescriptor> _screens;
        private readonly List<Rectangle> _boxes = new();
        private int _hover = -1;

        public event Action<MonitorDescriptor>? ScreenClicked;

        public ScreenMap(List<MonitorDescriptor> screens)
        {
            _screens = screens;
            DoubleBuffered = true;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Recalc();
            Invalidate();
        }

        private void Recalc()
        {
            _boxes.Clear();
            if (_screens.Count == 0 || Width < 40 || Height < 30) return;

            int minX = _screens.Min(s => s.Monitor.Left), minY = _screens.Min(s => s.Monitor.Top);
            float bw = _screens.Max(s => s.Monitor.Right) - minX;
            float bh = _screens.Max(s => s.Monitor.Bottom) - minY;
            if (bw <= 0 || bh <= 0) return;

            const int pad = 6;
            float scale = Math.Min((Width - 2f * pad) / bw, (Height - 2f * pad) / bh);
            int ox = (int)((Width - bw * scale) / 2), oy = (int)((Height - bh * scale) / 2);
            foreach (var s in _screens)
                _boxes.Add(new Rectangle(
                    ox + (int)((s.Monitor.Left - minX) * scale),
                    oy + (int)((s.Monitor.Top - minY) * scale),
                    Math.Max(10, (int)(s.Width * scale)),
                    Math.Max(10, (int)(s.Height * scale))));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_boxes.Count != _screens.Count) Recalc();
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            for (int i = 0; i < _boxes.Count; i++)
            {
                var box = Rectangle.Inflate(_boxes[i], -2, -2);
                if (box.Width <= 0 || box.Height <= 0) continue;
                bool hot = i == _hover;
                using (var fill = new SolidBrush(hot ? Color.FromArgb(204, 228, 247) : Color.FromArgb(238, 238, 238)))
                    e.Graphics.FillRectangle(fill, box);
                using (var pen = new Pen(hot ? Color.FromArgb(0, 120, 212) : Color.FromArgb(154, 154, 154), hot ? 2f : 1f))
                    e.Graphics.DrawRectangle(pen, box);

                var s = _screens[i];
                string number = s.Number > 0 ? s.Number.ToString() : "?";
                float size = Math.Max(9f, Math.Min(box.Height * 0.42f, 20f));
                using (var f = new Font("Segoe UI", size, FontStyle.Regular))
                using (var brush = new SolidBrush(Color.FromArgb(40, 40, 40)))
                {
                    var numSize = e.Graphics.MeasureString(number, f);
                    e.Graphics.DrawString(number, f, brush,
                        box.Left + (box.Width - numSize.Width) / 2,
                        box.Top + (box.Height - numSize.Height) / 2 - (box.Height > 46 ? 7 : 0));
                }
                if (box.Height > 46)
                {
                    using var f2 = new Font("Segoe UI", 7f);
                    using var b2 = new SolidBrush(Color.FromArgb(110, 110, 110));
                    // Longest caption that fits inside the box; skip it entirely if none does.
                    foreach (var sub in new[] { $"{s.Width}×{s.Height}{(s.IsPrimary ? " · primary" : "")}", $"{s.Width}×{s.Height}" })
                    {
                        var subSize = e.Graphics.MeasureString(sub, f2);
                        if (subSize.Width > box.Width - 6) continue;
                        e.Graphics.DrawString(sub, f2, b2,
                            box.Left + (box.Width - subSize.Width) / 2, box.Bottom - subSize.Height - 4);
                        break;
                    }
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hit = _boxes.FindIndex(b => b.Contains(e.Location));
            if (hit == _hover) return;
            _hover = hit;
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover < 0) return;
            _hover = -1;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int hit = _boxes.FindIndex(b => b.Contains(e.Location));
            if (hit >= 0) ScreenClicked?.Invoke(_screens[hit]);
        }
    }

    private void UpdateSetupLabel()
    {
        string name = string.IsNullOrWhiteSpace(_layout.Name) ? "(unnamed)" : _layout.Name!;
        _setupLabel.Text = $"Screen setup: {name}   —   {ScreenSetup.DescribeOf(_screens)}";
    }

    private Panel BuildButtonBar(out Button ok)
    {
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 48 };
        var okBtn = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90, Height = 30, Anchor = AnchorStyles.Right | AnchorStyles.Top };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 30, Anchor = AnchorStyles.Right | AnchorStyles.Top };
        var auto = new Button { Text = "Auto-assign by name", Width = 140, Height = 30, Anchor = AnchorStyles.Right | AnchorStyles.Top };
        var hint = new Label
        {
            Text = "Drag chips between cells · right-click a chip for full screen / half / quarter",
            AutoEllipsis = true,
            ForeColor = Color.Gray,
            Location = new Point(12, 15),
            Height = 18,
        };
        _hint = hint;
        auto.Location = new Point(bar.Width - 350, 9);
        okBtn.Location = new Point(bar.Width - 200, 9);
        cancel.Location = new Point(bar.Width - 100, 9);
        // keep them anchored to the right as the form resizes; the hint takes what's left
        bar.Resize += (_, _) =>
        {
            auto.Left = bar.Width - 350;
            okBtn.Left = bar.Width - 200;
            cancel.Left = bar.Width - 100;
            hint.Width = Math.Max(0, auto.Left - 24);
        };
        auto.Click += (_, _) => AutoAssign();
        okBtn.Click += (_, _) => Apply();
        AcceptButton = okBtn;
        CancelButton = cancel;
        bar.Controls.Add(hint);
        bar.Controls.Add(auto);
        bar.Controls.Add(okBtn);
        bar.Controls.Add(cancel);
        ok = okBtn;
        return bar;
    }

    private Control BuildGrid(List<VsCodeTracker.OpenWindow> windows)
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };

        if (_screens.Count == 0)
        {
            scroll.Controls.Add(new Label { Text = "No monitors detected.", AutoSize = true, Location = new Point(16, 16) });
            return scroll;
        }

        var grid = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1 + _screens.Count,
            RowCount = 1 + _desktops.Count,
            Margin = new Padding(8),
            CellBorderStyle = TableLayoutPanelCellBorderStyle.Single,
        };
        _grid = grid;
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LabelCol));
        for (int i = 0; i < _screens.Count; i++)
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScreenCol));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, HeaderRow));
        for (int r = 0; r < _desktops.Count; r++)
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        // Corner + monitor headers.
        grid.Controls.Add(new Label { Text = "Desktop \\ Screen", TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Fill, ForeColor = Color.Gray }, 0, 0);
        for (int c = 0; c < _screens.Count; c++)
            grid.Controls.Add(BuildScreenHeader(c), c + 1, 0);

        // Desktop row labels + the droppable body cells.
        for (int r = 0; r < _desktops.Count; r++)
        {
            var d = _desktops[r];
            grid.Controls.Add(new Label
            {
                Text = $"{d.Index + 1}. {d.Name}",
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill,
                Padding = new Padding(6, 0, 0, 0),
                Font = d.IsCurrent ? new Font(Font, FontStyle.Bold) : Font,
            }, 0, r + 1);

            for (int c = 0; c < _screens.Count; c++)
            {
                var cell = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    AllowDrop = true,
                    WrapContents = false,
                    FlowDirection = FlowDirection.TopDown,
                    AutoScroll = false,
                    BackColor = SystemColors.Window,
                    Padding = new Padding(3),
                };
                cell.DragEnter += Cell_DragEnter;
                cell.DragDrop += Cell_DragDrop;
                grid.Controls.Add(cell, c + 1, r + 1);
                _bodyCells.Add((cell, d, _screens[c]));
            }
        }

        // Seed each open window into its target cell.
        foreach (var w in windows)
        {
            var remembered = _layout.Workspaces.TryGetValue(w.Workspace, out var loc) ? loc : null;
            int deskIdx = InitialDesktopIndex(w, remembered);
            int scrIdx = InitialScreenIndex(w, remembered);
            var cell = _bodyCells.First(bc => bc.desktop.Id == _desktops[deskIdx].Id && ReferenceEquals(bc.screen, _screens[scrIdx])).panel;
            var snap = remembered?.Snap ?? SnapGeometry.Detect(w.Hwnd);
            cell.Controls.Add(MakeChip(w.Hwnd, w.Workspace, snap));
        }

        scroll.Controls.Add(grid);
        return scroll;
    }

    /// <summary>Column header: which screen it is, plus a one-click "put every window on this
    /// screen" that keeps each window on its own virtual desktop.</summary>
    private Control BuildScreenHeader(int index)
    {
        var s = _screens[index];
        var panel = new Panel { Dock = DockStyle.Fill };
        var moveAll = new LinkLabel
        {
            Text = "move all here",
            Dock = DockStyle.Bottom,
            Height = 20,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        moveAll.Click += (_, _) => MoveAllToColumn(s);
        panel.Controls.Add(moveAll);
        // Just the number + resolution: the arrangement map above already says which physical
        // screen that is, and monitor model strings are long enough to wrap the header.
        panel.Controls.Add(new Label
        {
            Text = $"{(s.Number > 0 ? $"Screen {s.Number}" : $"Screen #{index + 1}")}\n" +
                   $"{s.Width}×{s.Height}{(s.IsPrimary ? " · primary" : "")}",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font, FontStyle.Bold),
        });
        return panel;
    }

    /// <summary>Reparent every chip into <paramref name="screen"/>'s column, each staying in
    /// the desktop row it's already in.</summary>
    private void MoveAllToColumn(MonitorDescriptor screen)
    {
        _grid?.SuspendLayout();
        foreach (var (panel, desktop, cellScreen) in _bodyCells.ToArray())
        {
            if (ReferenceEquals(cellScreen, screen)) continue;
            var target = _bodyCells.First(bc => bc.desktop.Id == desktop.Id && ReferenceEquals(bc.screen, screen)).panel;
            foreach (Control c in panel.Controls.Cast<Control>().ToArray())
                target.Controls.Add(c);
        }
        _grid?.ResumeLayout(false);
        RelayoutRows();
    }

    private enum MatchKind { None, Exact, Prefix, Fuzzy }

    /// <summary>
    /// How close a fuzzy hit has to be, as 1 - distance/length. 0.75 is the line between
    /// "incdients" ≈ "incidents" (0.78, a typo) and "dat" ≈ "dav" (0.67, a different word).
    /// </summary>
    private const double FuzzyFloor = 0.75;

    /// <summary>
    /// Pick the desktop whose name best fits a workspace name: an exact hit first, then the
    /// longest prefix relationship in either direction ("osis" ⊂ "osis_fixes", "mob" ⊂
    /// "mobility"), then the nearest Levenshtein neighbour if it clears <see cref="FuzzyFloor"/>.
    /// Case-insensitive throughout. Returns -1 when nothing is close enough to guess.
    /// </summary>
    private (int index, MatchKind kind) MatchDesktop(string workspace)
    {
        string w = workspace.ToLowerInvariant();

        int exact = _desktops.FindIndex(d => string.Equals(d.Name, w, StringComparison.OrdinalIgnoreCase));
        if (exact >= 0) return (exact, MatchKind.Exact);

        int best = -1, bestLen = 0;
        for (int i = 0; i < _desktops.Count; i++)
        {
            string n = _desktops[i].Name.ToLowerInvariant();
            if (n.Length == 0) continue;
            if (!w.StartsWith(n, StringComparison.Ordinal) && !n.StartsWith(w, StringComparison.Ordinal)) continue;
            int len = Math.Min(n.Length, w.Length);
            if (len > bestLen) { best = i; bestLen = len; }
        }
        if (best >= 0) return (best, MatchKind.Prefix);

        double bestScore = FuzzyFloor;
        for (int i = 0; i < _desktops.Count; i++)
        {
            string n = _desktops[i].Name.ToLowerInvariant();
            if (n.Length == 0) continue;
            double score = 1.0 - (double)Levenshtein(w, n) / Math.Max(w.Length, n.Length);
            if (score > bestScore) { bestScore = score; best = i; }
        }
        return best >= 0 ? (best, MatchKind.Fuzzy) : (-1, MatchKind.None);
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1),
                                  prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>
    /// Move every chip into the desktop row its workspace name points at, keeping the screen
    /// column it's already in. Chips with no confident match are left where they are. Like
    /// every other edit in here it's only a proposal until OK.
    /// </summary>
    private void AutoAssign()
    {
        // Snapshot first: chips moved into a not-yet-visited cell would otherwise be counted twice.
        var chips = _bodyCells
            .SelectMany(bc => bc.panel.Controls.Cast<Control>().Select(c => (ctl: c, bc.screen)))
            .Where(x => x.ctl.Tag is Chip)
            .ToList();

        var counts = new int[4];
        _grid?.SuspendLayout();
        foreach (var (ctl, screen) in chips)
        {
            var chip = (Chip)ctl.Tag!;
            var (idx, kind) = MatchDesktop(chip.Workspace);
            counts[(int)kind]++;
            _tips.SetToolTip(chip.Label, idx < 0
                ? $"{chip.Workspace} — no desktop name is close enough"
                : $"{chip.Workspace} → {_desktops[idx].Name} ({kind.ToString().ToLowerInvariant()} match)");
            if (idx < 0) continue;
            var target = _bodyCells.First(bc => bc.desktop.Id == _desktops[idx].Id && ReferenceEquals(bc.screen, screen)).panel;
            if (!ReferenceEquals(target, ctl.Parent)) target.Controls.Add(ctl);
        }
        _grid?.ResumeLayout(false);

        if (_hint != null)
            _hint.Text = $"Auto-assign: {counts[(int)MatchKind.Exact]} exact, {counts[(int)MatchKind.Prefix]} prefix, " +
                         $"{counts[(int)MatchKind.Fuzzy]} fuzzy, {counts[(int)MatchKind.None]} unmatched (hover a chip for why)";
        RelayoutRows();
    }

    private Label MakeChip(IntPtr hwnd, string workspace, SnapMode snap)
    {
        var label = new Label
        {
            AutoSize = false,
            Size = new Size(ScreenCol - 24, ChipHeight),
            Margin = new Padding(3),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(213, 232, 253),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
            AutoEllipsis = true,
            Cursor = Cursors.Hand,
        };
        var chip = new Chip { Hwnd = hwnd, Workspace = workspace, Snap = snap, Label = label };
        label.Tag = chip;
        UpdateChipText(chip);

        label.MouseDown += (s, e) =>
        {
            if (e.Button == MouseButtons.Right) { ShowSnapMenu(chip, (Control)s!, e.Location); return; }
            if (e.Button != MouseButtons.Left) return;
            _dragChip = label;
            try { label.DoDragDrop(label, DragDropEffects.Move); }
            finally { _dragChip = null; }
        };
        return label;
    }

    private static void UpdateChipText(Chip chip)
    {
        chip.Label.Text = $"{chip.Workspace}   · {SnapGeometry.Tag(chip.Snap)}";
        chip.Label.AccessibleDescription = SnapGeometry.Label(chip.Snap);
    }

    private void ShowSnapMenu(Chip chip, Control owner, Point at)
    {
        _snapMenu.Items.Clear();
        foreach (var mode in SnapGeometry.All)
        {
            var item = new ToolStripMenuItem(SnapGeometry.Label(mode)) { Checked = chip.Snap == mode };
            var captured = mode;
            item.Click += (_, _) => { chip.Snap = captured; UpdateChipText(chip); };
            _snapMenu.Items.Add(item);
        }
        _snapMenu.Show(owner, at);
    }

    private void Cell_DragEnter(object? sender, DragEventArgs e)
        => e.Effect = _dragChip != null ? DragDropEffects.Move : DragDropEffects.None;

    private void Cell_DragDrop(object? sender, DragEventArgs e)
    {
        if (_dragChip == null || sender is not FlowLayoutPanel cell) return;
        cell.Controls.Add(_dragChip); // reparenting moves it out of the old cell
        RelayoutRows();
    }

    /// <summary>
    /// Give every desktop row exactly the height its fullest cell needs, then resize the form
    /// to the grid (clamped to the screen it's on — beyond that the grid scrolls).
    /// </summary>
    private void RelayoutRows()
    {
        if (_grid == null) return;

        // Every RowStyles.Height assignment re-lays out the table on the spot, so the loop
        // below has to run inside one suspended transaction or the rows resize (and repaint)
        // one by one.
        SuspendLayout();
        _grid.SuspendLayout();

        int total = HeaderRow;
        for (int r = 0; r < _desktops.Count; r++)
        {
            int maxChips = 0;
            foreach (var (panel, desktop, _) in _bodyCells)
                if (desktop.Id == _desktops[r].Id)
                    maxChips = Math.Max(maxChips, panel.Controls.Count);
            // Desktops with nothing on them stay a slim (but still droppable) strip.
            int h = maxChips == 0 ? 34 : 12 + maxChips * (ChipHeight + ChipGap);
            if (_grid.RowStyles[r + 1].Height != h) _grid.RowStyles[r + 1].Height = h;
            total += h;
        }

        // + 1px per cell border line, + the grid's 8px margins.
        int wantW = LabelCol + _screens.Count * ScreenCol + _screens.Count + 2 + 20;
        int wantH = total + _desktops.Count + 2 + 20 + (_bar?.Height ?? 0) + (_header?.Height ?? 0);

        var wa = Screen.FromPoint(Location.IsEmpty ? Cursor.Position : new Point(Left + 8, Top + 8)).WorkingArea;
        var want = new Size(
            Math.Min(wantW, (int)(wa.Width * 0.95)),
            Math.Min(wantH, (int)(wa.Height * 0.92)));
        if (ClientSize != want) ClientSize = want;

        _grid.ResumeLayout(true);
        ResumeLayout(true);
    }

    private int InitialDesktopIndex(VsCodeTracker.OpenWindow w, WorkspaceLocation? remembered)
    {
        int idx = remembered != null ? _desktops.FindIndex(d => d.Id == remembered.DesktopId) : -1;
        if (idx < 0) idx = _desktops.FindIndex(d => d.Id == w.DesktopId);
        return idx < 0 ? 0 : idx;
    }

    private int InitialScreenIndex(VsCodeTracker.OpenWindow w, WorkspaceLocation? remembered)
    {
        int idx = -1;
        if (remembered != null)
            idx = MatchScreen(remembered.MonitorDeviceId, remembered.MonitorX, remembered.MonitorWidth, remembered.MonitorHeight);
        if (idx < 0 && w.Monitor != null)
            idx = MatchScreen(w.Monitor.DeviceId, w.Monitor.RectX, w.Monitor.RectWidth, w.Monitor.RectHeight);
        if (idx < 0) idx = _screens.FindIndex(s => s.IsPrimary);
        return idx < 0 ? 0 : idx;
    }

    private int MatchScreen(string? deviceId, int x, int w, int h)
    {
        if (!string.IsNullOrEmpty(deviceId))
        {
            int i = _screens.FindIndex(s => string.Equals(s.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
        }
        if (w > 0 || h > 0)
        {
            int i = _screens.FindIndex(s => s.Monitor.Left == x && s.Width == w && s.Height == h);
            if (i >= 0) return i;
        }
        return -1;
    }

    private void Apply()
    {
        foreach (var (panel, desktop, screen) in _bodyCells)
        {
            foreach (Control c in panel.Controls)
            {
                if (c.Tag is not Chip chip) continue;

                // Capture the live free-rect offsets first so "as-is" chips (and a later
                // un-maximize) have a sane restored position, then overlay the user's choices.
                var loc = new WorkspaceLocation();
                MonitorRef.CaptureWindowPlacement(chip.Hwnd, loc);
                loc.DesktopId = desktop.Id;
                loc.MonitorDeviceId = screen.DeviceId;
                loc.MonitorX = screen.Monitor.Left;
                loc.MonitorY = screen.Monitor.Top;
                loc.MonitorWidth = screen.Width;
                loc.MonitorHeight = screen.Height;
                loc.Snap = chip.Snap;
                // Chosen by hand, so the 2s scan must not talk it back down to wherever the
                // window actually ended up — see WorkspaceLocation.Pinned.
                loc.Pinned = true;

                _desktop.PlaceWindow(chip.Hwnd, loc, desktop.Id);
                _layout.Workspaces[chip.Workspace] = loc;
            }
        }
        _settings.Save();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _snapMenu.Dispose(); _tips.Dispose(); }
        base.Dispose(disposing);
    }
}
