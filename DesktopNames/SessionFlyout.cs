using System.Drawing.Drawing2D;

namespace DesktopNames;

/// <summary>
/// Borderless popup listing the Claude sessions currently located on one desktop. Opened by
/// press-dragging a lit desktop button in the overlay. Each row is a drag source: press a row
/// and drag it onto any desktop button (on any overlay) to reassign that session there. The
/// drop calls back into <see cref="HostForm.ReassignSession"/>, which moves the VS Code window,
/// re-binds the live indicator, and remembers the correction.
///
/// Drag is implemented with mouse capture + screen-coordinate hit-testing rather than OLE
/// drag-drop, because the overlays are WS_EX_NOACTIVATE windows and OLE drop targets behave
/// unreliably against them.
/// </summary>
internal sealed class SessionFlyout : Form
{
    private const int RowHeight = 40;   // two lines: leaf label + full path
    private const int HeaderHeight = 20;
    private const int PadH = 10;
    private const int DotSize = 8;

    private static readonly Font RowFont = new("Segoe UI Variable Text", 9.5f, FontStyle.Regular);
    private static readonly Font PathFont = new("Segoe UI Variable Text", 8f, FontStyle.Regular);
    private static readonly Font HeaderFont = new("Segoe UI Variable Text", 8.5f, FontStyle.Regular);

    private readonly DesktopInfo _sourceDesktop;
    private readonly List<SessionState.SessionRef> _sessions;
    private readonly bool _isDark;
    private readonly System.Windows.Forms.Timer _closeTimer = new() { Interval = 2000 };

    private int _hoverRow = -1;
    private int _pressRow = -1;
    private Point _pressPoint;
    private bool _dragging;
    private SessionState.SessionRef _dragRef;
    private DragGhost? _ghost;

    public SessionFlyout(DesktopInfo sourceDesktop, List<SessionState.SessionRef> sessions,
                         Rectangle anchorScreen, bool isDark)
    {
        _sourceDesktop = sourceDesktop;
        _sessions = sessions;
        _isDark = isDark;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        StartPosition = FormStartPosition.Manual;

        int width = ComputeWidth();
        int height = HeaderHeight + RowHeight * sessions.Count + 6;

        // Sit above the anchored button (taskbar is usually at the bottom); drop below if there's
        // no room above. Clamp horizontally to the anchor's monitor.
        var work = Screen.FromRectangle(anchorScreen).WorkingArea;
        int x = Math.Max(work.Left, Math.Min(anchorScreen.Left, work.Right - width));
        int y = anchorScreen.Top - height - 2;
        if (y < work.Top) y = anchorScreen.Bottom + 2;
        Bounds = new Rectangle(x, y, width, height);

        MouseDown += OnFlyoutMouseDown;
        MouseMove += OnFlyoutMouseMove;
        MouseUp += OnFlyoutMouseUp;
        MouseLeave += (_, _) => { if (!_dragging) _closeTimer.Start(); };
        MouseEnter += (_, _) => _closeTimer.Stop();
        _closeTimer.Tick += (_, _) => { _closeTimer.Stop(); if (!_dragging) Close(); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */;
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Auto-dismiss if the user never engages — the cursor is over the overlay (not us)
        // at open time, so MouseLeave alone wouldn't arm the close. MouseEnter cancels it.
        _closeTimer.Start();
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEACTIVATE = 0x0021;
        const int MA_NOACTIVATE = 3;
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }

    private int ComputeWidth()
    {
        int textLeft = PadH + DotSize + 6;
        int max = TextRenderer.MeasureText($"{_sourceDesktop.Name} — drag a session onto a desktop", HeaderFont).Width;
        foreach (var s in _sessions)
        {
            int line1 = textLeft + TextRenderer.MeasureText(RowLabel(s), RowFont).Width + PadH;
            int line2 = textLeft + TextRenderer.MeasureText(SecondaryText(s), PathFont).Width + PadH;
            max = Math.Max(max, Math.Max(line1, line2));
        }
        return Math.Clamp(max + PadH, 220, 640);
    }

    private static string RowLabel(SessionState.SessionRef s)
        => $"{s.Label}   ({s.State.ToString().ToLowerInvariant()})";

    /// <summary>Second line — the full working directory (the real disambiguator when many
    /// sessions share a generic leaf name like "validation" or "src"). Falls back to the
    /// session id when no cwd was reported.</summary>
    private static string SecondaryText(SessionState.SessionRef s)
        => string.IsNullOrEmpty(s.Cwd)
            ? "session " + (s.SessionId.Length > 8 ? s.SessionId[..8] : s.SessionId)
            : s.Cwd;

    private int RowAt(Point client)
    {
        int y = client.Y - HeaderHeight;
        if (y < 0) return -1;
        int r = y / RowHeight;
        return r >= 0 && r < _sessions.Count ? r : -1;
    }

    private void OnFlyoutMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _pressRow = RowAt(e.Location);
        _pressPoint = e.Location;
    }

    private void OnFlyoutMouseMove(object? sender, MouseEventArgs e)
    {
        if (_dragging)
        {
            var pt = Control.MousePosition;
            _ghost?.MoveTo(pt);
            Program.Host?.SetDropHighlight(HitTestOverlays(pt)?.Id);
            return;
        }

        if (e.Button == MouseButtons.Left && _pressRow >= 0)
        {
            var dz = SystemInformation.DragSize;
            if (Math.Abs(e.X - _pressPoint.X) > dz.Width / 2 ||
                Math.Abs(e.Y - _pressPoint.Y) > dz.Height / 2)
            {
                BeginDrag(_pressRow);
            }
            return;
        }

        int row = RowAt(e.Location);
        if (row != _hoverRow) { _hoverRow = row; Invalidate(); }
    }

    private void OnFlyoutMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (!_dragging) { _pressRow = -1; return; }

        _dragging = false;
        Capture = false;
        var pt = Control.MousePosition;
        var target = HitTestOverlays(pt);
        Program.Host?.SetDropHighlight(null);
        _ghost?.Close();
        _ghost = null;

        if (target != null && target.Id != _sourceDesktop.Id)
            Program.Host?.ReassignSession(_dragRef.Source, _dragRef.SessionId, _dragRef.Cwd, target.Id);

        Close();
    }

    private void BeginDrag(int row)
    {
        _dragging = true;
        _dragRef = _sessions[row];
        _closeTimer.Stop();
        Capture = true;
        Cursor = Cursors.Hand;
        _ghost = new DragGhost(_dragRef.Label, _isDark);
        _ghost.Show();
        _ghost.MoveTo(Control.MousePosition);
    }

    private static DesktopInfo? HitTestOverlays(Point screenPt)
    {
        if (Program.Host == null) return null;
        foreach (var o in Program.Host.Overlays)
        {
            var d = o.DesktopAtScreenPoint(screenPt);
            if (d != null) return d;
        }
        return null;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var bg = _isDark ? Color.FromArgb(40, 40, 40) : Color.FromArgb(248, 248, 248);
        var border = _isDark ? Color.FromArgb(90, 90, 90) : Color.FromArgb(190, 190, 190);
        var fg = _isDark ? Color.White : Color.FromArgb(25, 25, 25);
        var dim = _isDark ? Color.FromArgb(170, 170, 170) : Color.FromArgb(110, 110, 110);
        var hover = _isDark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(225, 225, 225);

        using (var bgBrush = new SolidBrush(bg)) g.FillRectangle(bgBrush, ClientRectangle);
        using (var pen = new Pen(border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

        var headerRect = new Rectangle(PadH, 0, Width - PadH * 2, HeaderHeight);
        TextRenderer.DrawText(g, $"{_sourceDesktop.Name} — drag a session onto a desktop", HeaderFont,
            headerRect, dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        for (int i = 0; i < _sessions.Count; i++)
        {
            var s = _sessions[i];
            var rowRect = new Rectangle(0, HeaderHeight + i * RowHeight, Width, RowHeight);
            if (i == _hoverRow)
            {
                using var hb = new SolidBrush(hover);
                g.FillRectangle(hb, rowRect);
            }

            // Dot aligned to the first text line, not the row centre (rows are two lines tall).
            var dotRect = new Rectangle(PadH, rowRect.Top + 9, DotSize, DotSize);
            using (var dotBrush = new SolidBrush(StateColor(s.State)))
                g.FillEllipse(dotBrush, dotRect);

            int textLeft = PadH + DotSize + 6;
            int textWidth = Width - textLeft - PadH;
            var line1 = new Rectangle(textLeft, rowRect.Top + 4, textWidth, 18);
            var line2 = new Rectangle(textLeft, rowRect.Top + 21, textWidth, 15);
            TextRenderer.DrawText(g, RowLabel(s), RowFont, line1, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, SecondaryText(s), PathFont, line2, dim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                TextFormatFlags.PathEllipsis);
        }
    }

    private static Color StateColor(StateKind s)
    {
        var st = Program.Host?.Settings;
        return s switch
        {
            StateKind.Asking => TaskbarOverlay.ParseColorOrFallback(st?.AlertAskingColor ?? "", Color.FromArgb(255, 230, 128)),
            StateKind.Busy   => TaskbarOverlay.ParseColorOrFallback(st?.AlertBusyColor   ?? "", Color.FromArgb(244, 180, 131)),
            StateKind.Error  => TaskbarOverlay.ParseColorOrFallback(st?.AlertErrorColor  ?? "", Color.FromArgb(224, 133, 133)),
            StateKind.Ready  => TaskbarOverlay.ParseColorOrFallback(st?.AlertReadyColor  ?? "", Color.FromArgb(168, 216, 176)),
            _ => Color.Gray,
        };
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closeTimer.Stop();
        _closeTimer.Dispose();
        _ghost?.Close();
        _ghost = null;
        Program.Host?.SetDropHighlight(null);
        base.OnFormClosed(e);
    }

    /// <summary>Small translucent label that follows the cursor during a session drag.</summary>
    private sealed class DragGhost : Form
    {
        private static readonly Font GhostFont = new("Segoe UI Variable Text", 9.5f, FontStyle.Bold);
        private readonly string _text;
        private readonly Color _fore;

        public DragGhost(string text, bool isDark)
        {
            _text = text;
            _fore = isDark ? Color.White : Color.FromArgb(20, 20, 20);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            StartPosition = FormStartPosition.Manual;
            Opacity = 0.9;
            BackColor = isDark ? Color.FromArgb(55, 55, 55) : Color.FromArgb(238, 238, 238);
            var sz = TextRenderer.MeasureText(text, GhostFont);
            Size = new Size(sz.Width + 20, sz.Height + 10);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // No-activate + tool window + transparent (click-through — hit-testing is by coordinate).
                cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020;
                return cp;
            }
        }

        public void MoveTo(Point screenPt) => Location = new Point(screenPt.X + 12, screenPt.Y + 14);

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            using var pen = new Pen(Color.FromArgb(140, 128, 128, 128));
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(e.Graphics, _text, GhostFont, ClientRectangle, _fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
}
