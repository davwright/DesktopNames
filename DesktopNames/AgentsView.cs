using System.Drawing.Drawing2D;

namespace DesktopNames;

/// <summary>
/// The Agents tab: a box per live Claude session in its state colour, its background agents and
/// shells under it, and arrows for SendMessage — solid while the sender waits on the recipient,
/// grey dashed for the last half hour's messages. Redraws on every session update, no timer.
/// </summary>
internal sealed class AgentsView : ScrollableControl
{
    // Heights follow the font, which scales with the screen: fixed pixels clipped the last line at 125%.
    private int Line => Font.Height + 1;
    private int BoxH => 10 + 3 * Line;

    // TextRenderer ignores the Graphics transform unless told to: without this the text stayed put while the boxes scrolled.
    private const TextFormatFlags Flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis
                                          | TextFormatFlags.PreserveGraphicsTranslateTransform;
    private int ChildH => 6 + 2 * Line;
    private const int BoxW = 300, ChildW = 270, Gap = 60, ChildGap = 6, Pad = 20, BandGap = 80;

    private readonly SessionState _sessions;
    private readonly DesktopService _desktops;
    private readonly ToolTip _tip = new() { InitialDelay = 300 };
    private SessionState.AgentGraph _graph = new(Array.Empty<SessionState.AgentNode>(), Array.Empty<SessionState.AgentEdge>());
    private List<Placed> _placed = new();
    private string? _tipFor;

    private sealed record Placed(SessionState.AgentNode Node, string Desktop, Rectangle Box,
                                 List<(SessionState.AgentChild Child, Rectangle Box)> Children);

    public AgentsView(SessionState sessions, DesktopService desktops)
    {
        _sessions = sessions;
        _desktops = desktops;
        DoubleBuffered = true;
        AutoScroll = true;
        BackColor = SystemColors.Window;
        _sessions.Updated += OnUpdated;
        Disposed += (_, _) => { _sessions.Updated -= OnUpdated; _tip.Dispose(); };
        Reflow();
    }

    private void OnUpdated()
    {
        Reflow();
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Reflow();
        Invalidate();
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
    }

    /// <summary>For the control channel: scroll as the wheel does.</summary>
    public void ScrollTo(int y)
    {
        AutoScrollPosition = new Point(0, y);
        Invalidate();
    }

    /// <summary>For the control channel: what the tab shows.</summary>
    public object Describe() => new
    {
        nodes = _placed.Select(p => new
        {
            name = p.Node.Name, session = p.Node.SessionId, desktop = p.Desktop,
            state = p.Node.State.ToString().ToLowerInvariant(), doing = p.Node.Doing, topic = p.Node.Topic,
            lastActive = SessionState.FormatAge(p.Node.LastActiveUtc), background = p.Node.BackgroundActive, usage = p.Node.Usage,
            children = p.Children.Select(c => new { kind = c.Child.Kind, description = c.Child.Description, activity = c.Child.Activity,
                shellPid = c.Child.Shell?.Pid, shellMatches = c.Child.Shell?.Matches, lastOutput = c.Child.LastOutputUtc?.ToLocalTime() }),
        }),
        edges = _graph.Edges.Select(e => new
        {
            from = NameOf(e.FromId), to = e.ToSessionId != null ? NameOf(e.ToSessionId) : e.To,
            summary = e.Summary, waiting = e.Waiting, at = e.AtUtc.ToLocalTime().ToString("HH:mm"),
        }),
    };

    private string NameOf(string sessionId) => _placed.FirstOrDefault(p => p.Node.SessionId == sessionId)?.Node.Name ?? sessionId;

    private const int InnerGap = 16, GroupPad = 10, GroupTitle = 26;
    private List<(string Label, Rectangle Frame)> _groups = new();

    /// <summary>
    /// One frame per desktop, in desktop order, flowing left to right and wrapping into bands. Inside
    /// a frame its sessions sit side by side (wrapping when wider than the view), each with its
    /// background agents and shells stacked under it.
    /// </summary>
    private void Reflow()
    {
        _graph = _sessions.Graph();
        var desktops = _desktops.GetDesktops();
        int Order(Guid id) => desktops.FindIndex(d => d.Id == id) is int i and >= 0 ? i : int.MaxValue;
        string Label(Guid id) => desktops.FirstOrDefault(d => d.Id == id) is { } d ? $"{d.Index + 1} · {d.Name}" : "? · no desktop";
        int StackHeight(SessionState.AgentNode n) => BoxH + n.Children.Count * (ChildH + ChildGap);

        int width = Math.Max(ClientSize.Width, BoxW + 2 * (Pad + GroupPad));
        int maxCols = Math.Max(1, (width - 2 * Pad - 2 * GroupPad + InnerGap) / (BoxW + InnerGap));
        int x = Pad, top = Pad + 30, bandHeight = 0;
        var placed = new List<Placed>();
        var groups = new List<(string, Rectangle)>();
        // Every desktop that no longer exists is one "no desktop" frame.
        Guid GroupOf(Guid id) => Order(id) == int.MaxValue ? Guid.Empty : id;
        foreach (var group in _graph.Nodes.GroupBy(n => GroupOf(n.DesktopId)).OrderBy(g => Order(g.Key)))
        {
            var nodes = group.OrderByDescending(n => n.LastActiveUtc).ToList();  // most recently active first
            int cols = Math.Min(nodes.Count, maxCols);
            int frameW = cols * BoxW + (cols - 1) * InnerGap + 2 * GroupPad;
            if (x + frameW > width - Pad && x > Pad)
            {
                x = Pad;
                top += bandHeight + BandGap;
                bandHeight = 0;
            }
            int rowTop = top + GroupTitle;
            for (int r = 0; r * cols < nodes.Count; r++)
            {
                var row = nodes.Skip(r * cols).Take(cols).ToList();
                for (int c = 0; c < row.Count; c++)
                {
                    var box = new Rectangle(x + GroupPad + c * (BoxW + InnerGap), rowTop, BoxW, BoxH);
                    var children = new List<(SessionState.AgentChild, Rectangle)>();
                    int cy = box.Bottom + ChildGap;
                    foreach (var child in row[c].Children.OrderBy(ch => ch.StartedUtc ?? DateTime.MaxValue))
                    {
                        children.Add((child, new Rectangle(box.Right - ChildW, cy, ChildW, ChildH)));
                        cy += ChildH + ChildGap;
                    }
                    placed.Add(new Placed(row[c], Label(group.Key), box, children));
                }
                rowTop += row.Max(StackHeight) + InnerGap;
            }
            var frame = new Rectangle(x, top, frameW, rowTop - InnerGap + GroupPad - top);
            groups.Add((Label(group.Key), frame));
            bandHeight = Math.Max(bandHeight, frame.Height);
            x += frameW + Gap;
        }
        _placed = placed;
        _groups = groups;
        AutoScrollMinSize = new Size(0, top + bandHeight + Pad);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
        if (_placed.Count == 0)
        {
            TextRenderer.DrawText(g, "No Claude sessions are running.", Font, new Point(Pad, Pad), SystemColors.GrayText, Flags);
            return;
        }
        using (var frameFill = new SolidBrush(Color.FromArgb(246, 246, 248)))
        using (var framePen = new Pen(Color.FromArgb(205, 205, 212)))
        using (var title = new Font(Font, FontStyle.Bold))
            foreach (var (label, frame) in _groups)
            {
                using var path = TaskbarOverlay.RoundedRect(frame, 8);
                g.FillPath(frameFill, path);
                g.DrawPath(framePen, path);
                TextRenderer.DrawText(g, label, title, new Rectangle(frame.Left + GroupPad, frame.Top + 4, frame.Width - 2 * GroupPad, 18),
                    SystemColors.GrayText, Flags);
            }
        using var connector = new Pen(SystemColors.ControlDark, 1f);
        foreach (var p in _placed)
        {
            foreach (var (child, r) in p.Children)
            {
                int spine = p.Box.Left + (BoxW - ChildW) / 2;
                g.DrawLines(connector, new[] { new Point(spine, p.Box.Bottom), new Point(spine, r.Top + r.Height / 2), new Point(r.Left, r.Top + r.Height / 2) });
                DrawChild(g, child, r);
            }
            DrawSession(g, p);
        }
        foreach (var edge in _graph.Edges) DrawEdge(g, edge);
    }

    /// <summary>Idle this long, a session is a leftover rather than live work: drawn faded.</summary>
    private static readonly TimeSpan Stale = TimeSpan.FromHours(1);

    private void DrawSession(Graphics g, Placed p)
    {
        var n = p.Node;
        bool stale = DateTime.UtcNow - n.LastActiveUtc > Stale;
        Color fill = n.State == StateKind.None ? SystemColors.Control : SessionFlyout.StateColor(n.State);
        if (stale) fill = Blend(fill, SystemColors.Control, 0.65f);
        Color ink = stale ? SystemColors.GrayText : Color.Black, dim = stale ? SystemColors.GrayText : Color.FromArgb(60, 60, 60);
        using (var path = TaskbarOverlay.RoundedRect(p.Box, 6))
        using (var brush = new SolidBrush(fill))
        using (var border = new Pen(ControlPaint.Dark(fill, 0.1f), 1.2f))
        {
            g.FillPath(brush, path);
            g.DrawPath(border, path);
        }
        int lx = p.Box.Left + 8, w = BoxW - 16, y = p.Box.Top + 5;
        // Line 1: the title VS Code lists it under; top right, last active / started, ⏳ while background work runs.
        string corner = (n.BackgroundActive ? "⏳ " : "") + SessionState.FormatAge(n.LastActiveUtc)
                      + (n.StartedUtc is { } s ? "/" + SessionState.FormatAge(s) : "");
        using var bold = new Font(Font, FontStyle.Bold);
        int cornerW = TextRenderer.MeasureText(corner, bold).Width;
        TextRenderer.DrawText(g, n.Title, bold, new Rectangle(lx, y, w - cornerW - 4, Line), ink, Flags);
        TextRenderer.DrawText(g, corner, bold, new Rectangle(lx + w - cornerW, y, cornerW, Line), ink, Flags);
        // Line 2: the name sessions message it by (when the title isn't it) and its folder.
        // Right: the context its latest call sent, and how much of it came from the prompt cache — a
        // low share means the cache had expired (KPMG: 912k sent, 30k read, 882k written again).
        string tokens = n.Usage is { Context: > 0 } u ? $"{Tokens(u.Context)} · {100 * u.CacheRead / u.Context}% cached" : "";
        int tokensW = tokens.Length > 0 ? TextRenderer.MeasureText(tokens, Font).Width : 0;
        string id = n.Name == n.Title ? "" : n.Name + " · ";
        TextRenderer.DrawText(g, id + ShortFolder(n.Cwd), Font, new Rectangle(lx, y + Line, w - tokensW - 4, Line), dim, Flags);
        if (tokens.Length > 0) TextRenderer.DrawText(g, tokens, Font, new Rectangle(lx + w - tokensW, y + Line, tokensW, Line), dim, Flags);
        // Line 3: its latest message or tool call. The state is the colour.
        using var italic = new Font(Font, FontStyle.Italic);
        TextRenderer.DrawText(g, n.Doing, italic, new Rectangle(lx, y + 2 * Line, w, Line), ink, Flags);
    }

    /// <summary>471k, 1.2M.</summary>
    private static string Tokens(long n) => n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M" : n >= 1000 ? $"{n / 1000}k" : n.ToString();

    private static Color Blend(Color a, Color b, float toB) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * toB), (int)(a.G + (b.G - a.G) * toB), (int)(a.B + (b.B - a.B) * toB));

    /// <summary>The last two segments of a folder: evolx\osis_fixes.</summary>
    private static string ShortFolder(string cwd)
    {
        var parts = cwd.Replace('/', '\\').TrimEnd('\\').Split('\\');
        return string.Join("\\", parts.Skip(Math.Max(0, parts.Length - 2)));
    }

    private void DrawChild(Graphics g, SessionState.AgentChild c, Rectangle r)
    {
        using (var path = TaskbarOverlay.RoundedRect(r, 4))
        using (var brush = new SolidBrush(SystemColors.Window))
        using (var border = new Pen(SystemColors.ControlDark))
        {
            g.FillPath(brush, path);
            g.DrawPath(border, path);
        }
        string kind = c.Kind switch { "shell" => "background shell", "agent" => "subagent", _ => c.Kind };
        string since = c.StartedUtc is { } s ? $" · {SessionState.FormatAge(s)}" : "";
        // A subagent reports each tool call; a shell reports nothing, but writes its output to a file.
        string output = c.LastOutputUtc is { } o ? $"last output {SessionState.FormatAge(o)} ago" : "output file gone";
        // A shell's process: found by its command under the session's Claude (see FindShell).
        string process = c.Shell switch
        {
            { Pid: int pid } => $"running · pid {pid} · ",
            { Matches: 0 } => "ended · ",
            { Matches: var n } => $"{n} processes run this command · ",
            null => "",
        };
        string second = c.Activity.Length > 0 ? c.Activity : process + output;
        TextRenderer.DrawText(g, $"{kind}{since} · {c.Description}", Font, new Rectangle(r.Left + 6, r.Top + 3, r.Width - 12, Line), SystemColors.ControlText, Flags);
        TextRenderer.DrawText(g, second, Font, new Rectangle(r.Left + 6, r.Top + 3 + Line, r.Width - 12, Line), SystemColors.GrayText, Flags);
    }

    /// <summary>A curve over the boxes from sender to recipient (or down the side to the sender's own
    /// subagent), labelled with the message summary. Skipped when an end is no longer shown.</summary>
    private void DrawEdge(Graphics g, SessionState.AgentEdge edge)
    {
        var from = _placed.FirstOrDefault(p => p.Node.SessionId == edge.FromId);
        if (from == null) return;
        Point start, end, c1, c2;
        if (edge.ToSessionId != null)
        {
            if (_placed.FirstOrDefault(p => p.Node.SessionId == edge.ToSessionId) is not { } to) return;
            start = new Point(from.Box.Left + BoxW / 2 - 20, from.Box.Top);
            end = new Point(to.Box.Left + BoxW / 2 + 20, to.Box.Top);
            int lift = 40 + Math.Abs(end.X - start.X) / 8;
            c1 = new Point(start.X, Math.Min(start.Y, end.Y) - lift);
            c2 = new Point(end.X, Math.Min(start.Y, end.Y) - lift);
        }
        else
        {
            int i = from.Children.FindIndex(c => c.Child.Id == edge.To);
            if (i < 0) return;
            var target = from.Children[i].Box;
            start = new Point(from.Box.Right, from.Box.Top + BoxH / 2);
            end = new Point(target.Right, target.Top + target.Height / 2);
            c1 = new Point(start.X + 36, start.Y);
            c2 = new Point(end.X + 36, end.Y);
        }

        using var pen = edge.Waiting
            ? new Pen(Color.FromArgb(40, 40, 40), 2f)
            : new Pen(Color.FromArgb(150, 150, 150), 1.3f) { DashStyle = DashStyle.Dash };
        pen.CustomEndCap = new AdjustableArrowCap(4, 5);
        g.DrawBezier(pen, start, c1, c2, end);

        string text = TaskbarOverlay.Truncate(edge.Summary.Length > 0 ? edge.Summary : "message", 40)
                    + (edge.Waiting ? " · waiting" : $" · {edge.AtUtc.ToLocalTime():HH:mm}");
        var mid = new Point((start.X + 3 * c1.X + 3 * c2.X + end.X) / 8, (start.Y + 3 * c1.Y + 3 * c2.Y + end.Y) / 8);
        var size = TextRenderer.MeasureText(text, Font);
        var label = new Rectangle(mid.X - size.Width / 2, mid.Y - size.Height / 2, size.Width, size.Height);
        using (var back = new SolidBrush(SystemColors.Window)) g.FillRectangle(back, label);
        TextRenderer.DrawText(g, text, Font, label, edge.Waiting ? SystemColors.ControlText : SystemColors.GrayText, Flags);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var at = new Point(e.X - AutoScrollPosition.X, e.Y - AutoScrollPosition.Y);
        string? key = null, text = null;
        foreach (var p in _placed)
        {
            if (p.Box.Contains(at))
            {
                key = p.Node.SessionId;
                string usage = p.Node.Usage is { } u
                    ? $"{u.Model}, at its last call ({SessionState.FormatAge(p.Node.LastActiveUtc)} ago): sent {u.Context:N0} tokens ({u.CacheRead:N0} read from cache, {u.CacheWrite:N0} written to it, {u.Input:N0} uncached), got {u.Output:N0}\n\n" : "";
                text = usage + SessionState.StripTipMarkers(_sessions.DescribeSession(p.Node.Source, p.Node.SessionId));
            }
            foreach (var (c, r) in p.Children)
                if (r.Contains(at)) { key = p.Node.SessionId + "/" + c.Id; text = $"{c.Kind}: {c.Description}\n{c.Activity}"
                    + (c.ListedUtc is { } listed ? $"\nlisted as running when its session last ended a turn, {SessionState.FormatAge(listed)} ago" : ""); }
        }
        if (key == _tipFor) return;
        _tipFor = key;
        if (text == null) _tip.Hide(this);
        else _tip.Show(text, this, e.X + 12, e.Y + 16);
    }

    /// <summary>Right-click a session: close it. Right-click elsewhere: close the long-idle ones.</summary>
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Right) return;
        var at = new Point(e.X - AutoScrollPosition.X, e.Y - AutoScrollPosition.Y);
        var menu = new ContextMenuStrip();
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        if (_placed.FirstOrDefault(p => p.Box.Contains(at)) is { } hit)
            menu.Items.Add($"Close \"{hit.Node.Title}\"…", null, (_, _) => Confirm(
                $"End the Claude session \"{hit.Node.Title}\" ({hit.Node.Name})?\n\nIt runs its SessionEnd hook and exits; /resume in VS Code brings it back. Only an idle session is closed.",
                () => Run("claude.close", new { session = hit.Node.SessionId })));
        else
            menu.Items.Add("Close sessions idle for over a day…", null, (_, _) =>
            {
                var idle = Program.Host!.Commands.Execute("claude.idle", System.Text.Json.JsonSerializer.SerializeToElement(new { hours = 24 }));
                var names = System.Text.Json.JsonSerializer.SerializeToElement(idle).EnumerateArray().Select(x => x.GetProperty("Name").GetString()).ToList();
                if (names.Count == 0) { MessageBox.Show(this, "No session has been idle for over a day.", "DesktopNames"); return; }
                Confirm($"End these {names.Count} Claude sessions, idle for over a day?\n\n{string.Join("\n", names)}\n\n/resume in VS Code brings any of them back.",
                    () => Run("claude.closeIdle", new { hours = 24 }));
            });
        menu.Show(this, e.Location);
    }

    private void Confirm(string question, Action act)
    {
        if (MessageBox.Show(this, question, "Close Claude sessions", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK) act();
    }

    private void Run(string command, object args)
    {
        try { Program.Host!.Commands.Execute(command, System.Text.Json.JsonSerializer.SerializeToElement(args)); }
        catch (Exception ex) { ProjectSwitcher.ReportError(ex); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _tipFor = null;
        _tip.Hide(this);
    }
}
