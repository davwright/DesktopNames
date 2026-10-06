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
    // A row is a stack of lines: the header, one line per recent message, and the cwd. Line
    // heights are measured from the fonts (+ leading) so lines don't overlap; the row grows to
    // fit however many message lines a session has.
    private const int Leading = 3;      // extra px between stacked lines
    private const int RowPadTop = 5;
    private const int RowPadBottom = 7;
    private const int HeaderHeight = 20;
    private const int PadH = 10;
    private const int DotSize = 8;
    private const int XColWidth = 24;   // right-hand column reserved for the remove "✕"
    private const int MaxMsgLines = 2;  // wrap each recent message across at most this many lines
    private const int MaxWidth = 720;   // flyout width cap; messages wrap only past this

    private static readonly Font RowFont = new("Segoe UI Variable Text", 9.5f, FontStyle.Regular);
    private static readonly Font MsgFont = new("Segoe UI Variable Text", 8.5f, FontStyle.Italic);
    private static readonly Font PathFont = new("Segoe UI Variable Text", 8f, FontStyle.Regular);

    private static readonly int HeaderLineH = RowFont.Height + Leading;
    private static readonly int MsgLineH = MsgFont.Height + Leading;
    private static readonly int PathLineH = PathFont.Height + Leading;
    private static readonly Font HeaderFont = new("Segoe UI Variable Text", 8.5f, FontStyle.Regular);
    private static readonly Font XFont = new("Segoe UI", 10f, FontStyle.Regular);

    private readonly DesktopInfo _sourceDesktop;
    private readonly List<SessionState.SessionRef> _sessions;
    private readonly bool _isDark;

    // Cumulative row tops, computed from each session's line count. _rowTops[i] is the Y of row i
    // (relative to below the flyout header); _rowTops[^1] is the total content height.
    private int[] _rowTops = System.Array.Empty<int>();
    // Per-session wrapped message lines (each recent body split to fit the content width, capped
    // at MaxMsgLines). Rebuilt whenever the width or session list changes.
    private List<string>[] _wrappedMsgs = System.Array.Empty<List<string>>();
    private readonly System.Windows.Forms.Timer _closeTimer = new() { Interval = 2000 };

    private int _hoverRow = -1;
    private int _hoverX = -1;     // row whose "✕" the cursor is over (-1 = none)
    private int _pressRow = -1;
    private int _pressXRow = -1;  // row whose "✕" the press landed on (suppresses drag)
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
        RecomputeLayout(width);
        int height = HeaderHeight + ContentHeight + 6;

        // Sit above the anchored button (taskbar is usually at the bottom); drop below if there's
        // no room above. Clamp horizontally to the anchor's monitor.
        var work = Screen.FromRectangle(anchorScreen).WorkingArea;
        // A tall flyout (many sessions, up to 5 message lines each) may not fit in the working
        // area — cap it so it never overflows the screen.
        height = Math.Min(height, work.Height);
        int x = Math.Max(work.Left, Math.Min(anchorScreen.Left, work.Right - width));
        int y = anchorScreen.Top - height - 2;
        // If it doesn't fit above the anchor, drop below; if it fits neither, clamp into the
        // working area (top-aligned) rather than flip off-screen.
        if (y < work.Top)
            y = anchorScreen.Bottom + 2 + height <= work.Bottom ? anchorScreen.Bottom + 2 : work.Top;
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

    /// <summary>Header line. The synthetic unresolved bucket gets a friendlier label than its
    /// raw "?" button name.</summary>
    private string HeaderText => _sourceDesktop.Id == SessionState.UnresolvedDesktopId
        ? "Unmatched sessions — drag onto a desktop"
        : $"{_sourceDesktop.Name} — drag a session onto a desktop";

    private int TextLeft => PadH + DotSize + 6;
    private int TextWidthFor(int flyoutWidth) => flyoutWidth - TextLeft - PadH - XColWidth;

    /// <summary>Wrap each session's recent messages to <paramref name="flyoutWidth"/> (so long
    /// messages read fully instead of ellipsizing) and recompute per-row tops from the resulting
    /// line counts. Call after the width or session list changes.</summary>
    private void RecomputeLayout(int flyoutWidth)
    {
        int textWidth = TextWidthFor(flyoutWidth);
        _wrappedMsgs = new List<string>[_sessions.Count];
        for (int i = 0; i < _sessions.Count; i++)
        {
            var lines = new List<string>();
            foreach (var m in RecentMessages(_sessions[i]))
                lines.AddRange(WrapText(m, MsgFont, textWidth, MaxMsgLines));
            _wrappedMsgs[i] = lines;
        }

        _rowTops = new int[_sessions.Count + 1];
        int y = 0;
        for (int i = 0; i < _sessions.Count; i++)
        {
            _rowTops[i] = y;
            y += RowPadTop + HeaderLineH + MsgLineH * _wrappedMsgs[i].Count + PathLineH + RowPadBottom;
        }
        _rowTops[^1] = y;
    }

    private int ContentHeight => _rowTops.Length > 0 ? _rowTops[^1] : 0;

    private int ComputeWidth()
    {
        int max = TextRenderer.MeasureText(HeaderText, HeaderFont).Width;
        foreach (var s in _sessions)
        {
            max = Math.Max(max, TextLeft + TextRenderer.MeasureText(RowLabel(s), RowFont).Width + PadH);
            // Let messages widen the flyout (up to MaxWidth) so most fit on one line; only what
            // still exceeds MaxWidth wraps.
            foreach (var m in RecentMessages(s))
                max = Math.Max(max, TextLeft + TextRenderer.MeasureText(m, MsgFont).Width + PadH);
            max = Math.Max(max, TextLeft + TextRenderer.MeasureText(SecondaryText(s), PathFont).Width + PadH);
        }
        return Math.Clamp(max + PadH + XColWidth, 220, MaxWidth);
    }

    /// <summary>Greedy word-wrap of <paramref name="text"/> into at most <paramref name="maxLines"/>
    /// lines fitting <paramref name="width"/>. On reaching the last allowed line, the remaining
    /// words are dumped onto it verbatim so the renderer's EndEllipsis truncates them.</summary>
    private static List<string> WrapText(string text, Font font, int width, int maxLines)
    {
        var words = text.Split(' ');
        var lines = new List<string>();
        var cur = "";
        for (int i = 0; i < words.Length; i++)
        {
            var trial = cur.Length == 0 ? words[i] : cur + " " + words[i];
            if (cur.Length == 0 || TextRenderer.MeasureText(trial, font).Width <= width)
            {
                cur = trial;
                continue;
            }
            lines.Add(cur);
            if (lines.Count == maxLines - 1)
            {
                // Last line: take the rest as-is (ellipsized on draw).
                cur = string.Join(" ", words.Skip(i));
                break;
            }
            cur = words[i];
        }
        if (cur.Length > 0) lines.Add(cur);
        return lines.Count == 0 ? new List<string> { text } : lines;
    }

    /// <summary>Header line — same shape as the taskbar hovertext: label, then a 2-char session
    /// id, state, and relative age in parens.</summary>
    private static string RowLabel(SessionState.SessionRef s)
    {
        string sid = s.SessionId.Length >= 2 ? s.SessionId[..2] : s.SessionId;
        return $"{s.Label} ({sid} · {s.State.ToString().ToLowerInvariant()}, {SessionState.FormatAge(s.LastSeenUtc)})";
    }

    /// <summary>The recent message lines shown under the header — the same bodies the hovertext
    /// carries, oldest first. Empty when the session never reported a message body.</summary>
    private static IReadOnlyList<string> RecentMessages(SessionState.SessionRef s)
        => s.RecentBodies ?? System.Array.Empty<string>();

    /// <summary>Square hit/draw region for a row's remove "✕", flush to the right edge, aligned
    /// to the row's header line.</summary>
    private Rectangle XRectFor(int row)
        => new(Width - XColWidth, HeaderHeight + _rowTops[row] + RowPadTop,
               XColWidth, XColWidth);

    private int XHitAt(Point client)
    {
        int row = RowAt(client);
        return row >= 0 && XRectFor(row).Contains(client) ? row : -1;
    }

    /// <summary>Bottom line — the full working directory (the real disambiguator when many
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
        for (int i = 0; i < _sessions.Count; i++)
            if (y >= _rowTops[i] && y < _rowTops[i + 1]) return i;
        return -1;
    }

    private void OnFlyoutMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _pressRow = RowAt(e.Location);
        _pressXRow = XHitAt(e.Location);
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

        // A press that started on the "✕" removes on release — never begins a drag.
        if (e.Button == MouseButtons.Left && _pressRow >= 0 && _pressXRow < 0)
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
        int xr = XHitAt(e.Location);
        if (row != _hoverRow || xr != _hoverX)
        {
            _hoverRow = row;
            _hoverX = xr;
            Cursor = xr >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    private void OnFlyoutMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        if (_pressXRow >= 0 && !_dragging)
        {
            int xTarget = _pressXRow;
            _pressXRow = _pressRow = -1;
            if (XHitAt(e.Location) == xTarget) RemoveSessionRow(xTarget);
            return;
        }

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

    /// <summary>Dismiss one session's indicator and shrink the flyout; close when none remain.</summary>
    private void RemoveSessionRow(int row)
    {
        if (row < 0 || row >= _sessions.Count) return;
        var s = _sessions[row];
        Program.Host?.RemoveSession(s.Source, s.SessionId);
        _sessions.RemoveAt(row);
        _hoverRow = _hoverX = -1;
        if (_sessions.Count == 0) { Close(); return; }
        RecomputeLayout(Width);
        Height = HeaderHeight + ContentHeight + 6;
        Invalidate();
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
        TextRenderer.DrawText(g, HeaderText, HeaderFont,
            headerRect, dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        int textLeft = PadH + DotSize + 6;
        int textWidth = Width - textLeft - PadH - XColWidth;

        for (int i = 0; i < _sessions.Count; i++)
        {
            var s = _sessions[i];
            int rowTop = HeaderHeight + _rowTops[i];
            var rowRect = new Rectangle(0, rowTop, Width, _rowTops[i + 1] - _rowTops[i]);
            if (i == _hoverRow)
            {
                using var hb = new SolidBrush(hover);
                g.FillRectangle(hb, rowRect);
            }

            int y = rowTop + RowPadTop;

            // Dot aligned to the header line.
            var dotRect = new Rectangle(PadH, y + (HeaderLineH - DotSize) / 2, DotSize, DotSize);
            using (var dotBrush = new SolidBrush(StateColor(s.State)))
                g.FillEllipse(dotBrush, dotRect);

            TextRenderer.DrawText(g, RowLabel(s), RowFont,
                new Rectangle(textLeft, y, textWidth, HeaderLineH), fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                TextFormatFlags.EndEllipsis);
            y += HeaderLineH;

            // Recent messages (italic) — the same bodies the taskbar hovertext carries, wrapped.
            foreach (var m in _wrappedMsgs[i])
            {
                TextRenderer.DrawText(g, m, MsgFont,
                    new Rectangle(textLeft, y, textWidth, MsgLineH), dim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                    TextFormatFlags.EndEllipsis);
                y += MsgLineH;
            }

            TextRenderer.DrawText(g, SecondaryText(s), PathFont,
                new Rectangle(textLeft, y, textWidth, PathLineH), dim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                TextFormatFlags.PathEllipsis);

            // Remove "✕" — dim by default, brightens to the foreground colour on hover.
            TextRenderer.DrawText(g, "✕", XFont, XRectFor(i), i == _hoverX ? fg : dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    internal static Color StateColor(StateKind s)
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
