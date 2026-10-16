namespace DesktopNames;

/// <summary>
/// The card shown while hovering a desktop button on the overlay: the desktop's note, then one
/// block per Claude session (header with state dot, message, background work, cwd, start, recent
/// actions). It sits right on top of the button, so the mouse can move into it, and stays while
/// the mouse is over the button or the card. Press a session's block and drag it onto any desktop
/// button to move that session there — the same drop the session flyout does.
/// </summary>
internal sealed class HoverCard : Form
{
    /// <summary>One block of the card; <see cref="Session"/> is set for a draggable Claude block.</summary>
    public sealed record Block(string Text, SessionState.SessionRef? Session);

    private const int MaxWidth = 720;
    private const int BlockGap = 8;
    private readonly List<Block> _blocks;
    private readonly List<(int Top, int Bottom)> _spans = new();
    private readonly Guid _desktopId;
    private readonly Rectangle _anchor;
    private readonly bool _dark;
    private int _hoverBlock = -1, _pressBlock = -1;
    private Point _pressPoint;
    private bool _dragging;
    private SessionFlyout.DragGhost? _ghost;

    public Guid DesktopId => _desktopId;

    public HoverCard(Guid desktopId, List<Block> blocks, Rectangle anchorScreen, bool dark)
    {
        _desktopId = desktopId;
        _blocks = blocks;
        _anchor = anchorScreen;
        _dark = dark;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = TipCard.Back(dark);

        // Lay the blocks out; a hint line goes first when something can be dragged.
        int y = TipCard.Pad, w = 0;
        if (_blocks.Any(b => b.Session != null)) y += TipCard.Font.Height + 4;
        foreach (var b in _blocks)
        {
            var size = TipCard.Measure(b.Text);
            _spans.Add((y - 3, y + size.Height + 3));
            y += size.Height + BlockGap;
            w = Math.Max(w, size.Width);
        }
        int width = Math.Min(MaxWidth, Math.Max(w, 240) + TipCard.Pad * 2), height = y - BlockGap + TipCard.Pad;

        // Touching the button's top edge (the taskbar is at the bottom), so the mouse can move
        // straight into the card; below it when there's no room above.
        var work = Screen.FromRectangle(anchorScreen).WorkingArea;
        int x = Math.Max(work.Left, Math.Min(anchorScreen.Left, work.Right - width));
        int top = anchorScreen.Top - height;
        if (top < work.Top) top = anchorScreen.Bottom;
        Bounds = new Rectangle(x, top, width, height);

        MouseMove += OnCardMouseMove;
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _pressBlock = BlockAt(e.Location); _pressPoint = e.Location; } };
        MouseUp += OnCardMouseUp;
        MouseLeave += (_, _) => { if (!_dragging && !_anchor.Contains(Cursor.Position)) Close(); };
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

    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }

    /// <summary>Whether the screen point is on the card — the overlay keeps it open then.</summary>
    public bool ContainsScreenPoint(Point screenPt) => !IsDisposed && Bounds.Contains(screenPt);

    private int BlockAt(Point p)
    {
        for (int i = 0; i < _spans.Count; i++)
            if (_blocks[i].Session != null && p.Y >= _spans[i].Top && p.Y < _spans[i].Bottom) return i;
        return -1;
    }

    private void OnCardMouseMove(object? sender, MouseEventArgs e)
    {
        if (_dragging)
        {
            _ghost!.MoveTo(Cursor.Position);
            Program.Host?.SetDropHighlight(SessionFlyout.HitTestOverlays(Cursor.Position)?.Id);
            return;
        }
        if (e.Button == MouseButtons.Left && _pressBlock >= 0)
        {
            var dz = SystemInformation.DragSize;
            if (Math.Abs(e.X - _pressPoint.X) > dz.Width / 2 || Math.Abs(e.Y - _pressPoint.Y) > dz.Height / 2)
            {
                _dragging = true;
                Capture = true;
                _ghost = new SessionFlyout.DragGhost(_blocks[_pressBlock].Session!.Value.Label, _dark);
                _ghost.Show();
                _ghost.MoveTo(Cursor.Position);
            }
            return;
        }
        int hover = BlockAt(e.Location);
        if (hover == _hoverBlock) return;
        _hoverBlock = hover;
        Cursor = hover >= 0 ? Cursors.SizeAll : Cursors.Default;
        Invalidate();
    }

    private void OnCardMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (!_dragging) { _pressBlock = -1; return; }
        _dragging = false;
        Capture = false;
        var target = SessionFlyout.HitTestOverlays(Cursor.Position);
        var s = _blocks[_pressBlock].Session!.Value;
        Program.Host?.SetDropHighlight(null);
        if (target != null && target.Id != _desktopId)
            Program.Host?.ReassignSession(s.Source, s.SessionId, s.Cwd, target.Id);
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var pen = new Pen(TipCard.Border(_dark))) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        if (_blocks.Any(b => b.Session != null))
            TextRenderer.DrawText(g, "Drag a Claude onto a desktop to move it there", TipCard.Font,
                new Point(TipCard.Pad, TipCard.Pad), TipCard.Dim(_dark), TipCard.Flags);
        for (int i = 0; i < _blocks.Count; i++)
        {
            if (i == _hoverBlock)
                using (var hi = new SolidBrush(TipCard.Hover(_dark)))
                    g.FillRectangle(hi, 4, _spans[i].Top, Width - 8, _spans[i].Bottom - _spans[i].Top);
            TipCard.Draw(g, _blocks[i].Text, new Point(TipCard.Pad, _spans[i].Top + 3), Width - TipCard.Pad, _dark);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _ghost?.Close();
        _ghost = null;
        Program.Host?.SetDropHighlight(null);
        base.OnFormClosed(e);
    }
}

/// <summary>
/// How a hover card or tooltip draws session text with the Tip* markers (see SessionState):
/// session header bold with a state dot, the Claude message italic, background work with run
/// times, the cwd dim. Unmarked lines (a desktop note, "Recent:") are plain.
/// </summary>
internal static class TipCard
{
    public const int Pad = 10;
    private const int Indent = 14;
    private const int RightGap = 18;
    public static readonly Font Font = (Font)(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont).Clone();
    private static readonly Font Italic = new(Font, FontStyle.Italic);
    private static readonly Font Bold = new(Font, FontStyle.Bold);
    public const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    public static Color Back(bool dark) => dark ? Color.FromArgb(40, 40, 40) : Color.FromArgb(248, 248, 248);
    public static Color Border(bool dark) => dark ? Color.FromArgb(90, 90, 90) : Color.FromArgb(190, 190, 190);
    public static Color Hover(bool dark) => dark ? Color.FromArgb(58, 58, 58) : Color.FromArgb(232, 238, 248);
    private static Color Fore(bool dark) => dark ? Color.White : Color.FromArgb(25, 25, 25);
    public static Color Dim(bool dark) => dark ? Color.FromArgb(170, 170, 170) : Color.FromArgb(110, 110, 110);

    private readonly record struct Line(string Text, string Right, Font Font, bool DimText, int Indent, StateKind? Dot);

    private static IEnumerable<Line> Lines(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            if (raw.Length == 0) { yield return new("", "", Font, false, 0, null); continue; }
            char m = raw[0];
            if (m == SessionState.TipHeader) yield return new(raw[2..], "", Bold, false, Indent, (StateKind)(raw[1] - '0'));
            else if (m == SessionState.TipItalic) yield return new(raw[1..], "", Italic, false, Indent, null);
            else if (m == SessionState.TipBgHead) yield return new(raw[1..], "", Font, false, Indent, null);
            else if (m == SessionState.TipBg)
            {
                int tab = raw.LastIndexOf('\t');
                yield return tab < 0 ? new(raw[1..], "", Font, false, Indent * 2, null)
                                     : new(raw[1..tab], raw[(tab + 1)..], Font, false, Indent * 2, null);
            }
            else if (m == SessionState.TipBgSub) yield return new(raw[1..], "", Italic, true, Indent * 3, null);
            else if (m == SessionState.TipDim) yield return new(raw[1..], "", Font, true, Indent, null);
            else yield return new(raw, "", Font, false, 0, null);
        }
    }

    private static Size MeasureLine(string text, Font font) =>
        TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), Flags);

    /// <summary>The size the text takes, without the card padding.</summary>
    public static Size Measure(string text)
    {
        int w = 0, h = 0;
        foreach (var l in Lines(text))
        {
            if (l.Text.Length == 0) { h += l.Font.Height / 2; continue; }
            var sz = MeasureLine(l.Text, l.Font);
            int right = l.Right.Length == 0 ? 0 : RightGap + MeasureLine(l.Right, l.Font).Width;
            w = Math.Max(w, l.Indent + sz.Width + right);
            h += sz.Height + 1;
        }
        return new Size(w, h);
    }

    /// <summary>Draw the text from <paramref name="at"/>; lines longer than the space end in an ellipsis.</summary>
    public static void Draw(Graphics g, string text, Point at, int rightEdge, bool dark)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int y = at.Y;
        foreach (var l in Lines(text))
        {
            if (l.Text.Length == 0) { y += l.Font.Height / 2; continue; }
            int lineH = MeasureLine(l.Text, l.Font).Height;
            if (l.Dot is { } state)
            {
                const int d = 8;
                using var dot = new SolidBrush(SessionFlyout.StateColor(state));
                g.FillEllipse(dot, at.X + (Indent - d) / 2 - 1, y + (lineH - d) / 2, d, d);
            }
            int rw = l.Right.Length > 0 ? MeasureLine(l.Right, l.Font).Width : 0;
            TextRenderer.DrawText(g, l.Text, l.Font, new Rectangle(at.X + l.Indent, y, rightEdge - at.X - l.Indent - (rw > 0 ? rw + RightGap : 0), lineH),
                l.DimText ? Dim(dark) : Fore(dark), Flags | TextFormatFlags.EndEllipsis);
            if (rw > 0) TextRenderer.DrawText(g, l.Right, l.Font, new Point(rightEdge - rw, y), Dim(dark), Flags);
            y += lineH + 1;
        }
    }
}
