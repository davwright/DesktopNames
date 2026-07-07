namespace DesktopNames;

/// <summary>
/// Modal grid for placing open VS Code windows: rows = virtual desktops, columns = physical
/// monitors. Each open workspace is a draggable chip sitting in the cell it will land in
/// (seeded from its remembered association, falling back to where it currently is). The user
/// drags chips between cells; on OK each window is maximized on its chosen monitor, moved to
/// its chosen desktop, and the association is saved back to <see cref="Settings.VsCodeWorkspaceDesktops"/>.
/// </summary>
internal sealed class ArrangeWindowsDialog : Form
{
    private sealed record ChipInfo(IntPtr Hwnd, string Workspace);

    private readonly DesktopService _desktop;
    private readonly Settings _settings;
    private readonly List<DesktopInfo> _desktops;
    private readonly List<MonitorDescriptor> _screens;
    private readonly List<(System.Windows.Forms.FlowLayoutPanel panel, DesktopInfo desktop, MonitorDescriptor screen)> _bodyCells = new();
    private Label? _dragChip;

    public ArrangeWindowsDialog(DesktopService desktop, Settings settings, VsCodeTracker tracker)
    {
        _desktop = desktop;
        _settings = settings;
        _desktops = desktop.GetDesktops();
        _screens = MonitorRef.EnumerateAll();

        Text = "Arrange VS Code windows — drag each to a desktop / screen";
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        Size = new Size(Math.Min(260 + _screens.Count * 220, 1400), 700);
        MinimumSize = new Size(520, 320);
        Font = new Font("Segoe UI", 9f);

        var windows = tracker.EnumerateOpenWorkspaceWindows();

        var bottom = BuildButtonBar(out var ok);
        Controls.Add(BuildGrid(windows));
        Controls.Add(bottom);

        if (_screens.Count == 0) ok.Enabled = false;
    }

    private Panel BuildButtonBar(out Button ok)
    {
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 48 };
        var okBtn = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90, Height = 30, Anchor = AnchorStyles.Right | AnchorStyles.Top };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 30, Anchor = AnchorStyles.Right | AnchorStyles.Top };
        okBtn.Location = new Point(bar.Width - 200, 9);
        cancel.Location = new Point(bar.Width - 100, 9);
        // keep them anchored to the right as the form resizes
        bar.Resize += (_, _) => { okBtn.Left = bar.Width - 200; cancel.Left = bar.Width - 100; };
        okBtn.Click += (_, _) => Apply();
        AcceptButton = okBtn;
        CancelButton = cancel;
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

        const int labelCol = 160, screenCol = 210, headerRow = 44, bodyRow = 88;

        var grid = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1 + _screens.Count,
            RowCount = 1 + _desktops.Count,
            Margin = new Padding(8),
            CellBorderStyle = TableLayoutPanelCellBorderStyle.Single,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelCol));
        for (int i = 0; i < _screens.Count; i++)
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, screenCol));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, headerRow));
        for (int r = 0; r < _desktops.Count; r++)
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, bodyRow));

        // Corner + monitor headers.
        grid.Controls.Add(new Label { Text = "Desktop \\ Screen", TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Fill, ForeColor = Color.Gray }, 0, 0);
        for (int c = 0; c < _screens.Count; c++)
        {
            var s = _screens[c];
            string label = $"Screen {c + 1}\n{s.Width}×{s.Height}{(s.IsPrimary ? " (primary)" : "")}";
            grid.Controls.Add(new Label { Text = label, TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold) }, c + 1, 0);
        }

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
                var cell = new System.Windows.Forms.FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    AllowDrop = true,
                    WrapContents = true,
                    AutoScroll = true,
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
            int deskIdx = InitialDesktopIndex(w);
            int scrIdx = InitialScreenIndex(w);
            var cell = _bodyCells.First(bc => bc.desktop.Id == _desktops[deskIdx].Id && ReferenceEquals(bc.screen, _screens[scrIdx])).panel;
            cell.Controls.Add(MakeChip(w.Hwnd, w.Workspace));
        }

        scroll.Controls.Add(grid);
        return scroll;
    }

    private Label MakeChip(IntPtr hwnd, string workspace)
    {
        var chip = new Label
        {
            Text = workspace,
            Tag = new ChipInfo(hwnd, workspace),
            AutoSize = false,
            Size = new Size(186, 26),
            Margin = new Padding(3),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(213, 232, 253),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
            AutoEllipsis = true,
            Cursor = Cursors.Hand,
        };
        chip.MouseDown += (s, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _dragChip = (Label)s!;
            try { chip.DoDragDrop(chip, DragDropEffects.Move); }
            finally { _dragChip = null; }
        };
        return chip;
    }

    private void Cell_DragEnter(object? sender, DragEventArgs e)
        => e.Effect = _dragChip != null ? DragDropEffects.Move : DragDropEffects.None;

    private void Cell_DragDrop(object? sender, DragEventArgs e)
    {
        if (_dragChip == null || sender is not System.Windows.Forms.FlowLayoutPanel cell) return;
        cell.Controls.Add(_dragChip); // reparenting moves it out of the old cell
    }

    private int InitialDesktopIndex(VsCodeTracker.OpenWindow w)
    {
        Guid want = _settings.VsCodeWorkspaceDesktops.TryGetValue(w.Workspace, out var loc) ? loc.DesktopId : Guid.Empty;
        int idx = _desktops.FindIndex(d => d.Id == want);
        if (idx < 0) idx = _desktops.FindIndex(d => d.Id == w.DesktopId);
        return idx < 0 ? 0 : idx;
    }

    private int InitialScreenIndex(VsCodeTracker.OpenWindow w)
    {
        int idx = -1;
        if (_settings.VsCodeWorkspaceDesktops.TryGetValue(w.Workspace, out var loc))
            idx = MatchScreen(loc.MonitorDeviceId, loc.MonitorX, loc.MonitorWidth, loc.MonitorHeight);
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
                if (c.Tag is not ChipInfo info) continue;
                _desktop.MaximizeOnMonitorThenMoveToDesktop(info.Hwnd, screen.Work, desktop.Id);
                _settings.VsCodeWorkspaceDesktops[info.Workspace] = new WorkspaceLocation
                {
                    DesktopId = desktop.Id,
                    MonitorDeviceId = screen.DeviceId,
                    MonitorX = screen.Monitor.Left,
                    MonitorY = screen.Monitor.Top,
                    MonitorWidth = screen.Width,
                    MonitorHeight = screen.Height,
                    WindowMaximized = true,
                    WindowOffsetX = 0,
                    WindowOffsetY = 0,
                    WindowWidth = screen.Work.Right - screen.Work.Left,
                    WindowHeight = screen.Work.Bottom - screen.Work.Top,
                };
            }
        }
        _settings.Save();
    }
}
