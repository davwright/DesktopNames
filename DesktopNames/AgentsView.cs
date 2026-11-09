using System.Drawing.Drawing2D;

namespace DesktopNames;

/// <summary>
/// The Agents tab: a box per live Claude session in its state colour, and under it its background
/// agents and shells and the messages it sent (SendMessage, last half hour) — solid while it waits
/// on the recipient's reply. Redraws on every session update, no timer.
/// </summary>
internal sealed class AgentsView : ScrollableControl
{
    // Heights follow the font, which scales with the screen: fixed pixels clipped the last line at 125%.
    private int Line => Font.Height + 1;
    private int BoxH => 10 + 5 * Line;

    // TextRenderer ignores the Graphics transform unless told to: without this the text stayed put while the boxes scrolled.
    private const TextFormatFlags Flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis
                                          | TextFormatFlags.PreserveGraphicsTranslateTransform;
    private int ChildH => 6 + 2 * Line;
    private const int Gap = 12, ChildGap = 6, Pad = 16, BandGap = 12;

    // Boxes per row (2–6, Ctrl+wheel) set the box width; the text scales with it.
    private int _columns;
    // Shift+wheel: the text 10% larger or smaller per step, on top of that.
    private int _textStep;
    private int BoxW = 300;
    private int ChildW => BoxW - 20;
    private Font _iconFont = new("Segoe MDL2 Assets", 9f);

    private readonly SessionState _sessions;
    private readonly DesktopService _desktops;
    private readonly ToolTip _tip = new() { InitialDelay = 300 };
    private SessionState.AgentGraph _graph = new(Array.Empty<SessionState.AgentNode>(), Array.Empty<SessionState.AgentEdge>());
    private List<Placed> _placed = new();
    private string? _tipFor;

    private sealed record Placed(SessionState.AgentNode Node, string Desktop, Rectangle Box,
                                 List<(SessionState.AgentChild Child, Rectangle Box)> Children,
                                 List<(SessionState.AgentEdge Message, Rectangle Box)> Messages);


    public AgentsView(SessionState sessions, DesktopService desktops)
    {
        _sessions = sessions;
        _desktops = desktops;
        DoubleBuffered = true;
        AutoScroll = true;
        BackColor = SystemColors.Window;
        _columns = Math.Clamp(Program.Host!.Settings.AgentsColumns, 2, 6);
        _textStep = Math.Clamp(Program.Host.Settings.AgentsTextStep, -15, 15);
        _sessions.Updated += OnUpdated;
        _clock.Tick += (_, _) => RefreshBrowse();
        _shimmer.Tick += (_, _) => { foreach (var live in _live) Invalidate(new Rectangle(live.X + AutoScrollPosition.X, live.Y + AutoScrollPosition.Y, live.Width, live.Height)); };
        // A popup is its own top-level window: it goes when the tab hides or the window closes, not only when the mouse leaves.
        VisibleChanged += (_, _) => { UpdateShimmer(); if (!Visible) ClosePopup(); };
        Disposed += (_, _) => { ClosePopup(); _sessions.Updated -= OnUpdated; _tip.Dispose(); _iconFont.Dispose(); _shimmer.Dispose(); _clock.Dispose(); };
        Reflow();
    }

    // What is happening right now — busy sessions' current action, their subagents' current tool call —
    // carries a highlight sweeping across it, so the eye goes there. It is animation, so it runs on a
    // timer, but only while this tab is visible and something is busy, and repaints only those strips.
    private readonly System.Windows.Forms.Timer _shimmer = new() { Interval = 40 };
    private List<Rectangle> _live = new();
    private const int ShimmerMs = 1800;

    /// <summary>Animate only while this page is the one showing.</summary>
    public void UpdateShimmer() =>
        _shimmer.Enabled = _live.Count > 0 && Visible;

    private void DrawShimmer(Graphics g, Rectangle r)
    {
        float phase = Environment.TickCount64 % ShimmerMs / (float)ShimmerMs;
        int band = Math.Max(40, r.Width / 3);
        int x = r.Left - band + (int)((r.Width + band) * phase);
        var clip = g.Clip;
        g.SetClip(r, CombineMode.Intersect);
        using (var brush = new LinearGradientBrush(new Rectangle(x, r.Top, band, r.Height), Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal))
        {
            brush.InterpolationColors = new ColorBlend
            {
                Colors = new[] { Color.FromArgb(0, Color.White), Color.FromArgb(170, Color.White), Color.FromArgb(0, Color.White) },
                Positions = new[] { 0f, 0.5f, 1f },
            };
            g.FillRectangle(brush, x, r.Top, band, r.Height);
        }
        g.Clip = clip;
    }

    private void OnUpdated()
    {
        Reflow();
        Invalidate();
        RefreshBrowse();
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
        animating = _shimmer.Enabled,
        open = _tiles.Select(t => t.Shown?.Node.Name),
        nodes = _placed.Select(p => new
        {
            name = p.Node.Name, session = p.Node.SessionId, desktop = p.Desktop,
            state = p.Node.State.ToString().ToLowerInvariant(), doing = p.Node.Doing, topic = p.Node.Topic,
            lastActive = SessionState.FormatAge(p.Node.LastActiveUtc), background = p.Node.BackgroundActive, usage = p.Node.Usage,
            title = p.Node.Title, remoteControl = p.Node.RemoteControl,
            children = p.Children.Select(c => new { kind = c.Child.Kind, description = c.Child.Description, activity = c.Child.Activity,
                shellPid = c.Child.Shell?.Pid, shellMatches = c.Child.Shell?.Matches, lastOutput = c.Child.LastOutputUtc?.ToLocalTime(), tip = ChildTip(c.Child) }),
        }),
        edges = _graph.Edges.Select(e => new
        {
            from = NameOf(e.FromId), to = e.ToSessionId != null ? NameOf(e.ToSessionId) : e.To,
            summary = e.Summary, waiting = e.Waiting, at = e.AtUtc.ToLocalTime().ToString("HH:mm"),
        }),
    };

    private string NameOf(string sessionId) => _placed.FirstOrDefault(p => p.Node.SessionId == sessionId)?.Node.Name ?? sessionId;

    // InnerGap = Gap + 2 * GroupPad: boxes inside a frame sit on the same columns as single-box frames.
    private const int GroupPad = 8, InnerGap = Gap + 2 * GroupPad;
    private int GroupTitle => Font.Height + 8;
    private List<(Guid Desktop, string Label, Rectangle Frame)> _groups = new();

    /// <summary>
    /// One frame per desktop, in desktop order, flowing left to right and wrapping into bands. Inside
    /// a frame its sessions sit side by side (wrapping when wider than the view), each with its
    /// background agents and shells stacked under it.
    /// </summary>
    private void Reflow()
    {
        _graph = _sessions.Graph();
        foreach (var ended in _tiles.Where(t => _graph.Nodes.All(n => n.SessionId != t.SessionId)).ToList()) CloseTile(ended);
        var desktops = _desktops.GetDesktops();
        int Order(Guid id) => desktops.FindIndex(d => d.Id == id) is int i and >= 0 ? i : int.MaxValue;
        string Label(Guid id) => desktops.FirstOrDefault(d => d.Id == id) is { } d ? $"{d.Index + 1} · {d.Name}" : "? · no desktop";
        // The messages each session sent: its newest few, under its agents and shells.
        var sent = _graph.Edges.GroupBy(e => e.FromId).ToDictionary(g => g.Key, g => g.OrderBy(e => e.AtUtc).ToList());
        List<SessionState.AgentEdge> SentBy(SessionState.AgentNode n) => sent.TryGetValue(n.SessionId, out var list) ? list : new();
        int StackHeight(SessionState.AgentNode n) => BoxH + (n.Children.Count + SentBy(n).Count) * (ChildH + ChildGap);

        int width = Math.Max(ClientSize.Width, 400);
        // Each open tile takes one column of the window: the rest flow in the others.
        int maxCols = Math.Max(1, _columns - _tiles.Count);
        BoxW = (width - 2 * Pad - (maxCols - 1) * Gap) / maxCols - 2 * GroupPad;
        ScaleText(BoxW / 300f);
        int x = Pad, top = Pad, bandHeight = 0;
        var placed = new List<Placed>();
        var groups = new List<(Guid, string, Rectangle)>();
        // Every desktop that no longer exists is one "no desktop" frame.
        Guid GroupOf(Guid id) => Order(id) == int.MaxValue ? Guid.Empty : id;
        // Desktops active in the last two hours first, then the rest; desktop order within each. Not by
        // state: a desktop going busy or ready moved it, so frames kept changing place under the user.
        int Tier(IEnumerable<SessionState.AgentNode> ns) => ns.Any(n => DateTime.UtcNow - n.LastActiveUtc <= Recent) ? 0 : 1;
        foreach (var group in _graph.Nodes.Where(n => !IsOpen(n.SessionId)).GroupBy(n => GroupOf(n.DesktopId)).OrderBy(g => Tier(g)).ThenBy(g => Order(g.Key)))
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
                    var messages = new List<(SessionState.AgentEdge, Rectangle)>();
                    foreach (var message in SentBy(row[c]))
                    {
                        messages.Add((message, new Rectangle(box.Right - ChildW, cy, ChildW, ChildH)));
                        cy += ChildH + ChildGap;
                    }
                    placed.Add(new Placed(row[c], Label(group.Key), box, children, messages));
                }
                rowTop += row.Max(StackHeight) + InnerGap;
            }
            var frame = new Rectangle(x, top, frameW, rowTop - InnerGap + GroupPad - top);
            groups.Add((group.Key, Label(group.Key), frame));
            bandHeight = Math.Max(bandHeight, frame.Height);
            x += frameW + Gap;
        }
        _placed = placed;
        _groups = groups;
        _live = placed.Where(p => Working(p.Node))
            .SelectMany(p => p.Children.Where(c => c.Child.Activity.Length > 0)
                .Select(c => new Rectangle(c.Box.Left, c.Box.Top + 4 + Line, c.Box.Width, Line))
                .Concat(p.Node.State == StateKind.Busy ? new[] { ActionZone(p.Box) } : Array.Empty<Rectangle>())).ToList();
        UpdateShimmer();
        AutoScrollMinSize = new Size(0, top + bandHeight + Pad);
        foreach (var tile in _tiles) PlaceTile(tile, _graph.Nodes.First(n => n.SessionId == tile.SessionId));
    }

    /// <summary>Text grows and shrinks with the boxes (300 px wide = the window's 9 pt).</summary>
    private void ScaleText(float scale)
    {
        float size = Math.Clamp(9f * scale * MathF.Pow(1.1f, _textStep), 5f, 30f);
        if (Math.Abs(Font.Size - size) < 0.1f) return;
        Font = new Font("Segoe UI", size);
        _iconFont.Dispose();
        _iconFont = new Font("Segoe MDL2 Assets", size);
    }

    /// <summary>Ctrl+wheel: fewer, larger boxes per row, or more, smaller ones.</summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (ModifierKeys == Keys.Shift) { SetTextStep(_textStep + (e.Delta > 0 ? 1 : -1)); return; }
        if (ModifierKeys != Keys.Control) { base.OnMouseWheel(e); return; }
        SetColumns(_columns + (e.Delta > 0 ? -1 : 1));
    }

    /// <summary>Text size steps of 10%, -15 to +15, remembered.</summary>
    public void SetTextStep(int step)
    {
        step = Math.Clamp(step, -15, 15);
        if (step == _textStep) return;
        _textStep = step;
        Program.Host!.Settings.AgentsTextStep = step;
        Program.Host.Settings.Save();
        Reflow();
        Invalidate();
    }

    /// <summary>Boxes per row, 2–6, remembered.</summary>
    public void SetColumns(int columns)
    {
        columns = Math.Clamp(columns, 2, 6);
        if (columns == _columns) return;
        _columns = columns;
        Program.Host!.Settings.AgentsColumns = columns;
        Program.Host.Settings.Save();
        if (_tiles.Count > 0) Parent!.PerformLayout(); // the open tiles' columns follow the column width
        Reflow();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
        if (_placed.Count == 0 && _tiles.Count == 0)
        {
            TextRenderer.DrawText(g, "No Claude sessions are running.", Font, new Point(Pad, Pad), SystemColors.GrayText, Flags);
            return;
        }
        Guid here = _desktops.GetCurrentDesktopId();
        using (var frameFill = new SolidBrush(Color.FromArgb(246, 246, 248)))
        using (var framePen = new Pen(Color.FromArgb(205, 205, 212)))
        using (var hereFill = new SolidBrush(Color.FromArgb(225, 236, 250)))
        using (var herePen = new Pen(Color.FromArgb(0, 120, 215), 2f))
        using (var title = new Font(Font, FontStyle.Bold))
            foreach (var (desktop, label, frame) in _groups)
            {
                // The desktop the user is on stands out.
                using var path = TaskbarOverlay.RoundedRect(frame, 8);
                g.FillPath(desktop == here ? hereFill : frameFill, path);
                g.DrawPath(desktop == here ? herePen : framePen, path);
                TextRenderer.DrawText(g, label, title, new Rectangle(frame.Left + GroupPad, frame.Top + 3, frame.Width - 2 * GroupPad, Font.Height + 2),
                    SystemColors.GrayText, Flags);
            }
        using var connector = new Pen(SystemColors.ControlDark, 1f);
        foreach (var p in _placed)
        {
            foreach (var (child, r) in p.Children)
            {
                int spine = p.Box.Left + (BoxW - ChildW) / 2;
                g.DrawLines(connector, new[] { new Point(spine, p.Box.Bottom), new Point(spine, r.Top + r.Height / 2), new Point(r.Left, r.Top + r.Height / 2) });
                DrawChild(g, child, r, Fill(p.Node), live: p.Node.State == StateKind.Busy, open: false);
            }
            foreach (var (message, r) in p.Messages)
            {
                int spine = p.Box.Left + (BoxW - ChildW) / 2;
                g.DrawLines(connector, new[] { new Point(spine, p.Box.Bottom), new Point(spine, r.Top + r.Height / 2), new Point(r.Left, r.Top + r.Height / 2) });
                DrawMessage(g, message, r, p, open: _browseFor == MessageKey(p, message));
            }
            DrawSession(g, p);
        }
        // Hovering a message tag outlines the session it went to, or the sender's subagent (session/agent id).
        Rectangle? target = _placed.FirstOrDefault(p => p.Node.SessionId == _highlight)?.Box
            ?? _placed.SelectMany(p => p.Children.Where(c => p.Node.SessionId + "/" + c.Child.Id == _highlight).Select(c => (Rectangle?)c.Box)).FirstOrDefault();
        if (target is { } box)
        {
            using var outline = new Pen(Color.FromArgb(0, 120, 215), 3f);
            using var path = TaskbarOverlay.RoundedRect(Rectangle.Inflate(box, 3, 3), 8);
            g.DrawPath(outline, path);
        }
    }

    /// <summary>Idle this long, a session is a leftover rather than live work: drawn faded.</summary>
    private static readonly TimeSpan Stale = TimeSpan.FromHours(1);

    /// <summary>A desktop active within this long keeps its place among the recent ones, in desktop order.</summary>
    private static readonly TimeSpan Recent = TimeSpan.FromHours(2);

    private void DrawSession(Graphics g, Placed p)
    {
        var n = p.Node;
        bool stale = DateTime.UtcNow - n.LastActiveUtc > Stale;
        Color fill = Fill(n);
        Color ink = stale ? SystemColors.GrayText : Color.Black, dim = stale ? SystemColors.GrayText : Color.FromArgb(60, 60, 60);
        using (var path = TaskbarOverlay.RoundedRect(p.Box, 6))
        using (var brush = new SolidBrush(fill))
        using (var border = new Pen(ControlPaint.Dark(fill, 0.1f), 1.2f))
        {
            g.FillPath(brush, path);
            if (n.Usage is { } u) DrawContext(g, path, p.Box, fill, u);
            g.DrawPath(border, path);
        }
        int lx = p.Box.Left + 8, w = p.Box.Width - 16, y = p.Box.Top + 5;
        // Line 1: the title VS Code lists it under; top right, the model, ⏳ while background work runs, and how long ago it started.
        string corner = (n.Usage is { } model ? ModelName(model.Model) + " · " : "") + (n.BackgroundActive ? "⏳ " : "") + (n.StartedUtc is { } s ? SessionState.FormatAge(s) : "");
        using var bold = new Font(Font, FontStyle.Bold);
        int cornerW = TextRenderer.MeasureText(corner, bold).Width;
        // A blue badge: Remote Control (claude.ai) can see this session.
        int badge = 0;
        if (n.RemoteControl)
        {
            int d = Line * 3 / 5;
            using var blue = new SolidBrush(Color.FromArgb(0, 120, 215));
            g.FillEllipse(blue, lx, y + (Line - d) / 2, d, d);
            badge = d + 5;
        }
        // Then, after the title, the name sessions message it by (when the title isn't it) and its folder;
        // the corner, and the chevron at the far right.
        int room = w - Line - 6 - cornerW - 4 - badge;
        int titleW = Math.Min(TextRenderer.MeasureText(n.Title, bold).Width, room);
        TextRenderer.DrawText(g, n.Title, bold, new Rectangle(lx + badge, y, titleW, Line), ink, Flags);
        string id = n.Name == n.Title ? "" : n.Name + " · ";
        if (room - titleW > 2 * Line)
            TextRenderer.DrawText(g, id + ShortFolder(n.Cwd), Font, new Rectangle(lx + badge + titleW + 4, y, room - titleW - 4, Line), dim, Flags);
        TextRenderer.DrawText(g, corner, bold, new Rectangle(lx + w - Line - 6 - cornerW, y, cornerW, Line), ink, Flags);
        bool open = IsOpen(n.SessionId);
        DrawChevron(g, SessionChevron(p.Box), open);
        // Open, the conversation below its title takes the place of its last prompt and action.
        if (open) return;
        // Lines 2-3: the last prompt — the user's, or another session's message to it.
        // Each of the two ends with its age at the right of its first line, as an agent's card does.
        if (n.Prompt is { } prompt)
        {
            int promptW = DrawAge(g, prompt.AtUtc, new Rectangle(lx, y + Line, w, Line), dim);
            DrawCommand(g, prompt.FromPeer ? IconClaude : IconUser,
                (prompt.FromPeer ? $"{prompt.From}: " : "") + Flatten(prompt.Text), Font, ink, new Rectangle(lx, y + Line, w - promptW, 2 * Line));
        }
        else if (n.TranscriptPath.Length > 0 && n.Actions.Count == 0)
            DrawCommand(g, IconUser, "no prompt yet: it waits for its first command", Font, dim, new Rectangle(lx, y + Line, w, Line));
        // Lines 4-5: what Claude did last, behind the icon of its kind. The state is the colour.
        using var italic = new Font(Font, FontStyle.Italic);
        if (n.State == StateKind.Busy) DrawShimmer(g, ActionZone(p.Box));
        int doingW = DrawAge(g, n.DoingUtc, new Rectangle(lx, y + 3 * Line, w, Line), dim);
        DrawCommand(g, ActivityIcon(n.HookEvent, n.ToolName), Flatten(n.Doing), italic, ink, new Rectangle(lx, y + 3 * Line, w - doingW, 2 * Line));
    }

    /// <summary>"2m" at the right of <paramref name="line"/>; returns the width it takes, gap included.</summary>
    private int DrawAge(Graphics g, DateTime atUtc, Rectangle line, Color color)
    {
        string age = SessionState.FormatAge(atUtc);
        using var bold = new Font(Font, FontStyle.Bold);
        int ageW = TextRenderer.MeasureText(age, bold).Width;
        TextRenderer.DrawText(g, age, bold, new Rectangle(line.Right - ageW, line.Top, ageW, Line), color, Flags);
        return ageW + 4;
    }

    /// <summary>
    /// The latest call's tokens as two full-height shades over the box colour, the box's width being
    /// the model's window: a light one for the context it sent, a darker one for the part read from
    /// the prompt cache. Two vertical edges: a cache that had expired leaves the darker edge far
    /// left of the lighter one (KPMG: 912k sent, 30k cached).
    /// </summary>
    private static void DrawContext(Graphics g, GraphicsPath box, Rectangle r, Color fill, ClaudeSessionRegistry.Usage u)
    {
        long window = ContextWindow(u.Model);
        int used = (int)(r.Width * Math.Min(1.0, (double)u.Context / window));
        int cached = (int)(r.Width * Math.Min(1.0, (double)u.CacheRead / window));
        var clip = g.Clip;
        g.SetClip(box, CombineMode.Intersect);
        using (var tokens = new SolidBrush(Blend(fill, Color.Black, 0.08f))) g.FillRectangle(tokens, r.Left, r.Top, used, r.Height);
        using (var cache = new SolidBrush(Blend(fill, Color.Black, 0.17f))) g.FillRectangle(cache, r.Left, r.Top, cached, r.Height);
        g.Clip = clip;
    }

    /// <summary>Tokens a model holds before Claude Code compacts. 1M for Opus, Sonnet and Fable as run
    /// here (contexts of 914k are in the transcripts); Haiku has 200k.</summary>
    private static long ContextWindow(string model) => model.Contains("haiku", StringComparison.Ordinal) ? 200_000 : 1_000_000;

    /// <summary>"claude-opus-5-5" → "Opus 5.5", "claude-fable-5-1" → "Fable 5.1"; anything else as it is.</summary>
    private static string ModelName(string model)
    {
        var m = System.Text.RegularExpressions.Regex.Match(model, @"^claude-([a-z]+)-(\d+)-(\d+)");
        return m.Success ? char.ToUpperInvariant(m.Groups[1].Value[0]) + m.Groups[1].Value[1..] + " " + m.Groups[2].Value + "." + m.Groups[3].Value : model;
    }

    private static string Flatten(string text) => string.Join(" ", text.Split((char[])['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    // Segoe MDL2 Assets glyphs: who spoke, or which kind of tool ran.
    /// <summary>Stands for the Claude logo, drawn from claude.ico: input from another Claude session.</summary>
    internal const char IconClaude = (char)0xF8FF;
    internal static readonly Icon ClaudeIcon = ProjectsDialog.LoadIcon("claude.ico");

    private const char IconUser = '', IconPeer = '', IconShell = '', IconRead = '', IconEdit = '',
                       IconSearch = '', IconWeb = '', IconAgent = '', IconMcp = '', IconReply = '',
                       IconAsk = '', IconThink = '', IconTool = '';

    private static char ToolIcon(string tool) => tool switch
    {
        "Bash" or "PowerShell" or "BashOutput" or "KillShell" or "Monitor" => IconShell,
        "Read" or "NotebookRead" => IconRead,
        "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => IconEdit,
        "Grep" or "Glob" or "ToolSearch" => IconSearch,
        "WebFetch" or "WebSearch" => IconWeb,
        "Agent" or "Task" => IconAgent,
        "SendMessage" => IconPeer,
        _ when tool.StartsWith("mcp__", StringComparison.Ordinal) => IconMcp,
        "" => IconThink,
        _ => IconTool,
    };

    private static char ActivityIcon(string hookEvent, string tool) => hookEvent switch
    {
        "Stop" or "SubagentStop" => IconReply,
        "Notification" or "PermissionRequest" => IconAsk,
        "PreToolUse" or "PostToolUse" => ToolIcon(tool),
        "PreCompact" or "PostCompact" => IconCompact,
        _ => IconThink,
    };


    /// <summary>An icon, then the text wrapped into the rest of the box, cut with … on its last line.</summary>
    private void DrawCommand(Graphics g, char icon, string text, Font font, Color ink, Rectangle r)
    {
        int IconW = Line + 2;
        if (icon == IconClaude) g.DrawIcon(ClaudeIcon, new Rectangle(r.Left, r.Top + 1, Line - 2, Line - 2));
        else TextRenderer.DrawText(g, icon.ToString(), _iconFont, new Rectangle(r.Left, r.Top + 1, IconW, r.Height), ink, Flags);
        TextRenderer.DrawText(g, text, font, new Rectangle(r.Left + IconW, r.Top, r.Width - IconW, r.Height), ink,
            TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.EndEllipsis
            | TextFormatFlags.PreserveGraphicsTranslateTransform);
    }

    /// <summary>A session's colour: its state, faded when it is a leftover.</summary>
    private static Color Fill(SessionState.AgentNode n)
    {
        Color fill = n.State == StateKind.None ? SystemColors.Control : SessionFlyout.StateColor(Working(n) ? StateKind.Busy : n.State);
        return DateTime.UtcNow - n.LastActiveUtc > Stale ? Blend(fill, SystemColors.Control, 0.65f) : fill;
    }

    /// <summary>Busy, or its turn ended while a subagent it started still works: it waits on that agent, not on the user.</summary>
    private static bool Working(SessionState.AgentNode n) =>
        n.State == StateKind.Busy || (n.State == StateKind.Ready && n.Children.Any(c => c.Kind == "agent"));

    private static Color Blend(Color a, Color b, float toB) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * toB), (int)(a.G + (b.G - a.G) * toB), (int)(a.B + (b.B - a.B) * toB));

    /// <summary>The last two segments of a folder: evolx\osis_fixes.</summary>
    private static string ShortFolder(string cwd)
    {
        var parts = cwd.Replace('/', '\\').TrimEnd('\\').Split('\\');
        return string.Join("\\", parts.Skip(Math.Max(0, parts.Length - 2)));
    }

    private static string KindLabel(string kind) => kind switch { "shell" => "background shell", "agent" => "subagent", _ => kind };

    /// <summary>
    /// A background agent or shell, drawn like its session in a lighter tint of its colour. Line 1:
    /// what it does, and in the corner last output / started, as the session shows last active /
    /// started. Line 2: a subagent's current tool call, or a shell's process.
    /// </summary>
    private void DrawChild(Graphics g, SessionState.AgentChild c, Rectangle r, Color parentFill, bool live, bool open)
    {
        Color fill = Blend(parentFill, Color.White, 0.6f);
        using (var path = TaskbarOverlay.RoundedRect(r, 5))
        using (var brush = new SolidBrush(fill))
        using (var border = new Pen(ControlPaint.Dark(parentFill, 0.1f)))
        {
            g.FillPath(brush, path);
            g.DrawPath(border, path);
        }
        int lx = r.Left + 8, w = r.Width - 16, y = r.Top + 4;
        DrawChevron(g, ChildChevron(r), open);
        string corner = (c.LastOutputUtc is { } o ? SessionState.FormatAge(o) : "–")
                      + (c.StartedUtc is { } s ? "/" + SessionState.FormatAge(s) : "");
        if (c.Activity.Length > 0) corner = c.StartedUtc is { } started ? SessionState.FormatAge(started) : "";
        using var bold = new Font(Font, FontStyle.Bold);
        int cornerW = corner.Length > 0 ? TextRenderer.MeasureText(corner, bold).Width : 0;
        DrawCommand(g, c.Kind == "shell" ? IconShell : IconAgent, c.Description, bold, Color.Black, new Rectangle(lx, y, w - cornerW - 4, Line));
        if (corner.Length > 0) TextRenderer.DrawText(g, corner, bold, new Rectangle(lx + w - cornerW, y, cornerW, Line), Color.Black, Flags);
        var last = c.Actions.Count > 0 ? c.Actions[^1] : null;
        string state = last != null ? (last.Command is { } cmd ? Shown(cmd) : last.Text.ReplaceLineEndings(" ").Trim()) : c.Activity.Length > 0 ? c.Activity : c.Shell switch
        {
            { Pid: int pid } => $"running · pid {pid}",
            { Matches: 0 } => "ended",
            { Matches: var n } => $"{n} processes run this command",
            null => c.LastOutputUtc == null ? "output file gone" : "process not known yet",
        };
        if (last != null || c.Activity.Length > 0)
        {
            if (live) DrawShimmer(g, new Rectangle(r.Left, y + Line, r.Width, Line));
            // Before the chevron: how long ago its latest action was, so a quiet agent shows as one.
            string latest = last != null ? SessionState.FormatAge(last.AtUtc) : "";
            int latestW = latest.Length > 0 ? TextRenderer.MeasureText(latest, bold).Width : 0;
            int lineW = w - Line - 6 - latestW;
            DrawCommand(g, last == null ? ToolIcon(c.ActivityTool) : last.Tool.Length == 0 ? IconReply : ToolIcon(last.Tool), state, Font, Color.FromArgb(60, 60, 60), new Rectangle(lx, y + Line, lineW, Line));
            if (latestW > 0) TextRenderer.DrawText(g, latest, bold, new Rectangle(lx + lineW, y + Line, latestW, Line), Color.FromArgb(60, 60, 60), Flags);
        }
        else
            TextRenderer.DrawText(g, $"{KindLabel(c.Kind)} · {state}", Font, new Rectangle(lx + Line + 2, y + Line, w - 2 * Line - 8, Line), Color.FromArgb(60, 60, 60), Flags);
    }

    // A leading cd into the working folder: the same for every call, it hides what the command does.
    private static readonly System.Text.RegularExpressions.Regex LeadingCd = new(@"^\s*(cd|Set-Location)\s+(""[^""]*""|'[^']*'|\S+)\s*(;|&&|\r?\n)\s*");

    /// <summary>A command line on one line, without its leading cd.</summary>
    private static string Shown(string command) => LeadingCd.Replace(command.Trim(), "").ReplaceLineEndings(" ").Trim();

    /// <summary>The child's own hover text: everything known about it, in full.</summary>
    private static string ChildTip(SessionState.AgentChild c)
    {
        var lines = new List<string> { c.Description };
        lines.Add(KindLabel(c.Kind) + (c.StartedUtc is { } s ? $", started {SessionState.FormatAge(s)} ago" : ""));
        if (c.Actions.Count > 0)
        {
            lines.Add("");
            foreach (var a in c.Actions)
            {
                lines.Add($"{SessionState.FormatAge(a.AtUtc)}  {(a.Tool.Length == 0 ? "said: " : "")}{TaskbarOverlay.Truncate(a.Command != null && a.Command == a.Text ? Shown(a.Command) : a.Text.ReplaceLineEndings(" ").Trim(), 120)}");
                if (a.Command != null && a.Command != a.Text) lines.Add("        " + TaskbarOverlay.Truncate(Shown(a.Command), 160));
            }
        }
        else if (c.Activity.Length > 0) lines.Add($"now: {c.Activity}");
        if (c.Shell is { } shell)
            lines.Add(shell switch
            {
                { Pid: int pid } => $"running as bash pid {pid}",
                { Matches: 0 } => "ended: no bash under its Claude runs this command any more",
                { Matches: var n } => $"{n} bash processes run this command",
            });
        if (c.Kind == "shell")
            lines.Add(c.LastOutputUtc is { } o ? $"last output {SessionState.FormatAge(o)} ago" : "its output file is gone");
        if (c.Command is { Length: > 0 } command) { lines.Add(""); lines.Add(command); }
        return string.Join(Environment.NewLine, lines);
    }

    private const char IconCompact = '';
    private const char IconSent = '';

    /// <summary>Who a message went to: the session's title, or for one of the sender's own subagents, what that agent does;
    /// else the address as sent (a name or pipe no live session has).</summary>
    private string Recipient(SessionState.AgentEdge e, Placed from) =>
        e.ToSessionId != null ? _graph.Nodes.FirstOrDefault(n => n.SessionId == e.ToSessionId)?.Title ?? e.To
        : from.Children.FirstOrDefault(c => c.Child.Id == e.To).Child is { } agent ? $"subagent: {agent.Description}" : e.To;

    /// <summary>
    /// A message the session sent, drawn like its agents and shells: line 1 the send icon and who it
    /// went to, in the corner when; line 2 its summary, and that the sender waits on a reply.
    /// </summary>
    private void DrawMessage(Graphics g, SessionState.AgentEdge e, Rectangle r, Placed from, bool open)
    {
        Color parentFill = Fill(from.Node);
        using (var path = TaskbarOverlay.RoundedRect(r, 5))
        using (var brush = new SolidBrush(Blend(parentFill, Color.White, e.Waiting ? 0.35f : 0.75f)))
        using (var border = new Pen(ControlPaint.Dark(parentFill, 0.1f)) { DashStyle = e.Waiting ? DashStyle.Solid : DashStyle.Dash })
        {
            g.FillPath(brush, path);
            g.DrawPath(border, path);
        }
        // Top right: an X dismisses the tag.
        TextRenderer.DrawText(g, "✕", Font, DismissZone(r), Color.FromArgb(60, 60, 60),
            (Flags & ~TextFormatFlags.EndEllipsis) | TextFormatFlags.HorizontalCenter);
        int lx = r.Left + 8, w = r.Width - 16 - Line, y = r.Top + 4;
        string corner = SessionState.FormatAge(e.AtUtc);
        using var bold = new Font(Font, FontStyle.Bold);
        int cornerW = TextRenderer.MeasureText(corner, bold).Width;
        DrawCommand(g, IconSent, "to " + Recipient(e, from), bold, Color.Black, new Rectangle(lx, y, w - cornerW - 4, Line));
        TextRenderer.DrawText(g, corner, bold, new Rectangle(lx + w - cornerW, y, cornerW, Line), Color.Black, Flags);
        string second = (e.Failed != null ? "not delivered · " : e.Waiting ? "waiting for a reply · " : "") + (e.Summary.Length > 0 ? e.Summary : "message");
        TextRenderer.DrawText(g, second, Font, new Rectangle(lx + Line + 2, y + Line, w - Line - 2, Line), e.Failed != null ? Color.FromArgb(180, 0, 0) : Color.FromArgb(60, 60, 60), Flags);
        DrawChevron(g, ChildChevron(r), open);
    }

    private static string MessageKey(Placed from, SessionState.AgentEdge e) => from.Node.SessionId + "/msg/" + e.To + e.AtUtc.Ticks;

    /// <summary>A sent message in full, as ClaudeHook forwarded it when the call was made, rendered as markdown.</summary>
    private List<DetailPopup.Row> MessageRows(Placed from, SessionState.AgentEdge e) => new()
    {
        new(IconSent, (e.Failed != null ? "not delivered: " + e.Failed + "\n" : e.Waiting ? "waiting for a reply · " : "") + "to " + Recipient(e, from), SessionState.FormatAge(e.AtUtc), null),
        new(null, e.Body.Length > 0 ? e.Body : throw new InvalidOperationException($"ClaudeHook sent no text for the message \"{e.Summary}\" (a ClaudeHook older than sendBody?)"),
            null, null, Markdown: true),
    };

    private Rectangle DismissZone(Rectangle message) => new(message.Right - Line - 6, message.Top + 2, Line + 4, Line + 2);

    private string MessageTip(SessionState.AgentEdge e, Placed from) =>
        $"to {Recipient(e, from)}, sent {SessionState.FormatAge(e.AtUtc)} ago"
        + (e.Failed != null ? $"\nnot delivered: {e.Failed}" : "")
        + (e.Waiting ? "\nthe sender waits on a reply: it ends when the recipient's turn ends" : "")
        + (e.Summary.Length > 0 ? "\n\n" + e.Summary : "");

    // The box's two prompt lines and two action lines (DrawSession's lines 2-3 and 4-5).
    private Rectangle PromptZone(Rectangle box) => new(box.Left, box.Top + 5 + Line, box.Width, 2 * Line);
    private Rectangle ActionZone(Rectangle box) => new(box.Left, box.Top + 5 + 3 * Line, box.Width, 2 * Line);

    private DetailPopup? _popup;
    private string? _popupFor;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var at = new Point(e.X - AutoScrollPosition.X, e.Y - AutoScrollPosition.Y);
        Cursor = ChevronAt(at) != null || _placed.Any(p => SessionChevron(p.Box).Contains(at) || p.Children.Any(c => ChildChevron(c.Box).Contains(at)))
            ? Cursors.Hand : Cursors.Default;
        // Over a prompt or the actions: the full message, or the recent actions, in a popup.
        // Not while a browse panel is open: it shows the same and more.
        foreach (var p in _browse == null ? _placed : [])
        {
            string? zone = PromptZone(p.Box).Contains(at) && p.Node.Prompt != null ? "prompt" : ActionZone(p.Box).Contains(at) ? "actions" : null;
            if (zone == null) continue;
            string key = zone + "/" + p.Node.SessionId;
            if (key != _popupFor)
            {
                CloseHover();
                _tipFor = null;
                _tip.Hide(this);
                _popupFor = key;
                _popup = new DetailPopup(zone == "prompt" ? PromptRows(p.Node) : ActionRows(p.Node), Cursor.Position);
                _popup.Show();
            }
            return;
        }
        CloseHover();

        string? key2 = null, text = null, highlight = null;
        foreach (var p in _placed)
        {
            if (p.Box.Contains(at))
            {
                key2 = p.Node.SessionId;
                string usage = p.Node.Usage is { } u
                    ? $"{u.Model}, at its last call ({SessionState.FormatAge(p.Node.LastActiveUtc)} ago): sent {u.Context:N0} tokens ({u.CacheRead:N0} read from cache, {u.CacheWrite:N0} written to it, {u.Input:N0} uncached), got {u.Output:N0}\n\n" : "";
                text = (p.Node.RemoteControl ? "Remote Control (claude.ai) can see this session.\n\n" : "") + usage
                     + SessionState.StripTipMarkers(_sessions.DescribeSession(p.Node.Source, p.Node.SessionId));
            }
            foreach (var (c, r) in p.Children)
                if (r.Contains(at)) { key2 = p.Node.SessionId + "/" + c.Id; text = ChildTip(c); }
            foreach (var (m, r) in p.Messages)
                if (r.Contains(at)) { key2 = p.Node.SessionId + "/msg/" + m.To + m.AtUtc.Ticks; text = MessageTip(m, p); highlight = m.ToSessionId ?? p.Node.SessionId + "/" + m.To; }
        }
        SetHighlight(highlight);
        if (key2 == _tipFor) return;
        _tipFor = key2;
        if (text == null) _tip.Hide(this);
        else _tip.Show(text, this, e.X + 12, e.Y + 16);
    }

    /// <summary>For the control channel: hover a session's prompt or actions, as the mouse does.</summary>
    public void HoverZone(string session, string zone)
    {
        var p = _placed.FirstOrDefault(x => x.Node.SessionId == session || x.Node.Name == session)
            ?? throw new ArgumentException($"no session \"{session}\" in the Agents tab");
        if (zone is not ("prompt" or "actions")) throw new ArgumentException("zone must be prompt or actions");
        if (zone == "prompt" && p.Node.Prompt == null) throw new InvalidOperationException($"{p.Node.Name} has no prompt yet");
        CloseHover();
        _popupFor = zone + "/" + p.Node.SessionId;
        var corner = PointToScreen(new Point(p.Box.Left + AutoScrollPosition.X, (zone == "prompt" ? PromptZone(p.Box) : ActionZone(p.Box)).Top + AutoScrollPosition.Y));
        _popup = new DetailPopup(zone == "prompt" ? PromptRows(p.Node) : ActionRows(p.Node), corner);
        _popup.Show();
        _popup.Refresh(); // painted before this returns, so a screenshot right after shows it
    }

    public IntPtr? PopupHandle => _popup is { IsDisposed: false } popup ? popup.Handle : null;

    /// <summary>Close the hover popup and the browse panel.</summary>
    public void ClosePopup()
    {
        CloseHover();
        CloseBrowse();
        foreach (var tile in _tiles)
        {
            tile.Conversation?.ClosePreview();
            tile.Expansion?.ClosePreview();
        }
    }

    private void CloseHover()
    {
        _popup?.Close();
        _popup = null;
        _popupFor = null;
    }

    private DetailPopup? _browse;
    private string? _browseFor;

    // The chevron at the right of a box's or card's second line: opens its input and recent actions.
    private Rectangle SessionChevron(Rectangle box) => new(box.Right - Line - 8, box.Top + 5, Line + 4, Line);
    private Rectangle ChildChevron(Rectangle card) => new(card.Right - Line - 8, card.Top + 4 + Line, Line + 4, Line);

    private void DrawChevron(Graphics g, Rectangle r, bool open) =>
        TextRenderer.DrawText(g, open ? "\uE70D" : "\uE76C", _iconFont, r, Color.FromArgb(60, 60, 60),
            Flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

    /// <summary>The chevron under <paramref name="at"/>: the key of what it opens, where it is, and its rows.</summary>
    private (string Key, Rectangle Zone, Func<List<DetailPopup.Row>> Rows)? ChevronAt(Point at)
    {
        foreach (var chevron in Chevrons(_placed))
            if (chevron.Zone.Contains(at)) return chevron;
        return null;
    }

    /// <summary>The message chevrons of these sessions: the key of what each opens, where it is (in the grid's or its
    /// tile's coordinates), and its rows as they are now. Agents' and shells' chevrons expand them in their tile.</summary>
    private IEnumerable<(string Key, Rectangle Zone, Func<List<DetailPopup.Row>> Rows)> Chevrons(IEnumerable<Placed> sessions)
    {
        foreach (var p in sessions)
        {
            foreach (var (m, r) in p.Messages)
                yield return (MessageKey(p, m), ChildChevron(r), () => MessageRows(p, m));
        }
    }

    // The open panel's ages count up while it is open: a display clock, not a wait.
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };

    /// <summary>The open panel shows its rows as they are now; it closes when what it shows is gone.</summary>
    private void RefreshBrowse()
    {
        if (_browse == null) return;
        foreach (var chevron in Chevrons(_placed.Concat(_tiles.Select(t => t.Shown).OfType<Placed>())))
            if (chevron.Key == _browseFor) { _browse.SetRows(chevron.Rows()); return; }
        CloseBrowse();
    }

    // DetailPopup sits 14, 18 right of and below the point it is given: these put it at the chevron.
    private Point GridChevron(Rectangle zone) => PointToScreen(new Point(zone.Right - 14 + AutoScrollPosition.X, zone.Top - 18 + AutoScrollPosition.Y));
    private static Point TileChevron(Tile tile, Rectangle zone) => tile.PointToScreen(new Point(zone.Right - 14, zone.Top - 18));

    private void OpenBrowse(string key, Point screenAt, List<DetailPopup.Row> rows)
    {
        CloseBrowse();
        CloseHover();
        _tipFor = null;
        _tip.Hide(this);
        var popup = new DetailPopup(rows, screenAt);
        // Showing it deactivates this window: only then is it the open panel, or that would close it mid-show.
        popup.Show();
        _browse = popup;
        _browseFor = key;
        _clock.Start();
        var form = FindForm()!;
        form.Deactivate -= OnFormDeactivate;
        form.Deactivate += OnFormDeactivate;
        Invalidate();
    }

    // Another app came to the front (no form of ours is active): the panel goes with this window.
    private void OnFormDeactivate(object? sender, EventArgs e)
    {
        if (Form.ActiveForm != null) return;
        CloseBrowse();
        foreach (var tile in _tiles)
        {
            tile.Conversation?.ClosePreview();
            tile.Expansion?.ClosePreview();
        }
    }

    private void CloseBrowse()
    {
        if (_browse == null) return;
        _clock.Stop();
        _browse.Close();
        _browse = null;
        _browseFor = null;
        Invalidate();
    }

    // The open tiles: each session whose chevron was clicked, in a column of its own at the left of the
    // tab, left to right in the order opened, the height of the window, its conversation inside it.
    // They stay put while the other tiles scroll.
    private readonly List<Tile> _tiles = new();

    private sealed class Tile : Panel
    {
        public readonly string SessionId;
        public Placed? Shown;
        public DetailPopup? Conversation;
        /// <summary>The agent or shell expanded under its card, and what it shows.</summary>
        public string? Expanded;
        public DetailPopup? Expansion;
        public Tile(string sessionId) { SessionId = sessionId; DoubleBuffered = true; ResizeRedraw = true; Dock = DockStyle.Left; }
    }

    private bool IsOpen(string sessionId) => _tiles.Any(t => t.SessionId == sessionId);

    /// <summary>Open a session's tile beside those already open, with <paramref name="expand"/> (an agent's or shell's id) expanded.</summary>
    private Tile OpenTile(string sessionId, string? expand)
    {
        CloseBrowse();
        CloseHover();
        _tipFor = null;
        _tip.Hide(this);
        var tile = new Tile(sessionId) { BackColor = BackColor, Expanded = expand };
        tile.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (tile.Shown is not { } shown) return;
            DrawSession(e.Graphics, shown);
            foreach (var (c, r) in shown.Children) DrawChild(e.Graphics, c, r, Fill(shown.Node), live: false, open: tile.Expanded == c.Id);
            foreach (var (m, r) in shown.Messages) DrawMessage(e.Graphics, m, r, shown, open: _browseFor == MessageKey(shown, m));
        };
        tile.MouseUp += (_, e) => OnTileMouseUp(tile, e);
        tile.MouseMove += (_, e) => tile.Cursor = tile.Shown is { } shown && (SessionChevron(shown.Box).Contains(e.Location)
            || shown.Children.Any(c => ChildChevron(c.Box).Contains(e.Location)) || shown.Messages.Any(m => ChildChevron(m.Box).Contains(e.Location)))
            ? Cursors.Hand : Cursors.Default;
        // Sized as the page lays out, before it docks: a width set during the docking (from this view's
        // resize) went unapplied, and this view kept overlapping the column.
        var page = Parent!;
        LayoutEventHandler size = (_, _) => tile.Width = ColumnWidth();
        page.Layout += size;
        tile.Disposed += (_, _) => page.Layout -= size;
        tile.Width = ColumnWidth();
        _tiles.Add(tile);
        page.Controls.Add(tile);
        // Docking runs back to front: this view (in front) takes what the tiles leave, and the newest
        // tile, just behind it, docks after the others, so to their right.
        page.Controls.SetChildIndex(tile, 0);
        BringToFront();
        page.PerformLayout();
        var form = FindForm()!;
        form.Deactivate -= OnFormDeactivate;
        form.Deactivate += OnFormDeactivate;
        Reflow();
        Invalidate();
        return tile;
    }

    private void CloseTile(Tile tile)
    {
        _tiles.Remove(tile);
        tile.Conversation?.Close();
        tile.Expansion?.Close();
        tile.Dispose();
        Parent?.PerformLayout();
    }

    /// <summary>One of the window's columns, narrower when more tiles are open than leave one for the rest.</summary>
    private int ColumnWidth() => Parent!.ClientSize.Width / Math.Max(_columns, _tiles.Count + 1);

    /// <summary>Lay the open tile in its column: its title, then its agents' and shells' cards (the expanded
    /// one with what it did under it, half the tile high) and the messages it sent, then its conversation.</summary>
    private void PlaceTile(Tile tile, SessionState.AgentNode node)
    {
        var box = new Rectangle(Pad, Pad, tile.ClientSize.Width - Pad, tile.ClientSize.Height - 2 * Pad);
        int x = box.Left + 6, w = box.Width - 12, y = box.Top + 5 + Line + 3;
        var children = new List<(SessionState.AgentChild, Rectangle)>();
        var expanded = node.Children.FirstOrDefault(c => c.Id == tile.Expanded);
        Rectangle? expansion = null;
        foreach (var child in node.Children.OrderBy(c => c.StartedUtc ?? DateTime.MaxValue))
        {
            children.Add((child, new Rectangle(x, y, w, ChildH)));
            y += ChildH + ChildGap;
            if (child != expanded) continue;
            expansion = new Rectangle(x, y, w, box.Height / 2);
            y += box.Height / 2 + ChildGap;
        }
        var messages = new List<(SessionState.AgentEdge, Rectangle)>();
        foreach (var m in _graph.Edges.Where(e => e.FromId == node.SessionId).OrderBy(e => e.AtUtc))
        {
            messages.Add((m, new Rectangle(x, y, w, ChildH)));
            y += ChildH + ChildGap;
        }
        tile.Shown = new Placed(node, "", box, children, messages);
        if (expanded == null)
        {
            tile.Expanded = null; // it finished, or none was
            tile.Expansion?.Close();
            tile.Expansion = null;
        }
        else if (tile.Expansion == null) tile.Expansion = new DetailPopup(CardRows(expanded), tile, expansion!.Value);
        else tile.Expansion.Place(expansion!.Value, CardRows(expanded));
        var inside = new Rectangle(x, y, w, Math.Max(3 * Line, box.Bottom - 6 - y));
        if (tile.Conversation == null) tile.Conversation = new DetailPopup(ConversationRows(node), tile, inside);
        else tile.Conversation.Place(inside, ConversationRows(node));
        tile.Invalidate();
    }

    /// <summary>What an expanded card shows: an agent's task and actions, the one running now turning; a shell's command and output.</summary>
    private static List<DetailPopup.Row> CardRows(SessionState.AgentChild c)
    {
        var rows = ChildBrowseRows(c);
        if (c.Kind == "agent" && c.Activity.Length > 0 && c.Actions.Count > 0) rows[^1] = rows[^1] with { Running = true };
        return rows;
    }

    private void OnTileMouseUp(Tile tile, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || tile.Shown is not { } shown) return;
        var at = e.Location;
        if (SessionChevron(shown.Box).Contains(at))
        {
            CloseTile(tile);
            Reflow();
            Invalidate();
        }
        else if (shown.Children.FirstOrDefault(c => ChildChevron(c.Box).Contains(at)) is { Child: not null } card)
        {
            tile.Expanded = tile.Expanded == card.Child.Id ? null : card.Child.Id;
            Reflow();
        }
        else if (shown.Messages.FirstOrDefault(m => DismissZone(m.Box).Contains(at)) is { Message: not null } dismissed)
            Run("message.dismiss", new { session = dismissed.Message.FromId, to = dismissed.Message.To });
        else if (Chevrons([shown]).FirstOrDefault(c => c.Zone.Contains(at)) is { Key: not null } chevron)
        {
            if (_browseFor == chevron.Key) CloseBrowse();
            else
            {
                try { OpenBrowse(chevron.Key, TileChevron(tile, chevron.Zone), chevron.Rows()); }
                catch (Exception ex) { ProjectSwitcher.ReportError(ex); }
            }
            tile.Invalidate();
        }
        else if (tile.Shown.Box.Contains(e.Location))
        {
            try { GoTo(tile.Shown.Node); }
            catch (Exception ex) { ProjectSwitcher.ReportError(ex); }
        }
    }

    /// <summary>What the session was told and did, in order: each prompt (the user's shaded), then Claude's actions and replies under it.</summary>
    private static List<DetailPopup.Row> ConversationRows(SessionState.AgentNode n)
    {
        var facts = n.TranscriptPath.Length > 0 ? ClaudeSessionRegistry.Transcript(n.TranscriptPath) : null;
        if (facts == null) return new() { new(null, "No transcript yet.", null, null) };
        var rows = facts.Prompts
            .Select(p => (p.Offset, Row: PromptRow(n.TranscriptPath, p, p.FromPeer ? IconClaude : IconUser, p.FromPeer ? $"{p.From}: " : "") with { Mine = !p.FromPeer }))
            .Concat(facts.Actions.Select(a => (a.Offset, Row: ActionRow(n.TranscriptPath, a))))
            .OrderBy(x => x.Offset).ToList();
        // While the session works, its newest action is the step running now.
        if (n.State == StateKind.Busy && rows.Count > 0 && facts.Actions.Count > 0 && rows[^1].Offset == facts.Actions[^1].Offset)
            rows[^1] = (rows[^1].Offset, rows[^1].Row with { Running = true });
        return rows.Count > 0 ? rows.Select(x => x.Row).ToList() : new() { new(null, "Nothing recorded yet.", null, null) };
    }

    /// <summary>For the control channel: open a session's tile, or one of its agents' or shells' browse panel, as its chevron does.</summary>
    public void Browse(string session, string? child)
    {
        bool Is(Placed x) => x.Node.SessionId == session || x.Node.Name == session;
        var tile = _tiles.FirstOrDefault(t => t.Shown is { } shown && Is(shown));
        var p = tile?.Shown ?? _placed.FirstOrDefault(Is) ?? throw new ArgumentException($"no session \"{session}\" in the Agents tab");
        if (child != null && p.Messages.FirstOrDefault(x => child == "to:" + x.Message.To) is { Box.Width: > 0 } m)
        {
            OpenBrowse(MessageKey(p, m.Message), tile != null ? TileChevron(tile, ChildChevron(m.Box)) : GridChevron(ChildChevron(m.Box)), MessageRows(p, m.Message));
            _browse!.Refresh();
            return;
        }
        var c = child == null ? null : p.Node.Children.FirstOrDefault(x => x.Id == child || x.Description == child)
            ?? throw new ArgumentException($"{p.Node.Name} has no agent or shell \"{child}\"; it has: {string.Join(", ", p.Node.Children.Select(x => x.Description))}");
        if (tile == null) tile = OpenTile(p.Node.SessionId, c?.Id);
        else if (c != null)
        {
            tile.Expanded = c.Id;
            Reflow();
        }
        tile.Refresh();
        tile.Conversation!.Refresh();
        tile.Expansion?.Refresh();
    }

    /// <summary>For the control channel: hover a row of the open browse panel, opening its preview.</summary>
    public void BrowseHover(int row) =>
        (_browse ?? _tiles.LastOrDefault()?.Expansion ?? _tiles.LastOrDefault()?.Conversation ?? throw new InvalidOperationException("no browse panel or tile is open; call ui.agentsBrowse first")).HoverRow(row);

    public IntPtr? BrowseHandle => _browse is { IsDisposed: false } b ? b.Handle : _tiles.LastOrDefault()?.Handle;
    public IntPtr? PreviewHandle => (_browse ?? _tiles.LastOrDefault()?.Expansion ?? _tiles.LastOrDefault()?.Conversation) is { IsDisposed: false } b ? b.PreviewHandle : null;

    /// <summary>A subagent's task and recent actions; a shell's command and output.</summary>
    private static List<DetailPopup.Row> ChildBrowseRows(SessionState.AgentChild c)
    {
        var rows = new List<DetailPopup.Row>();
        if (c.Transcript is { } transcript)
        {
            if (c.Prompt is { } p) rows.Add(PromptRow(transcript, p, IconClaude, "task: "));
            rows.AddRange(c.Actions.Select(a => ActionRow(transcript, a)));
        }
        if (c.Command is { } command)
            rows.Add(new(IconShell, TaskbarOverlay.Truncate(Shown(command), 160), c.StartedUtc is { } s ? SessionState.FormatAge(s) : null, null,
                () => new() { new(null, command, null, null, Shell: true) }));
        if (c.OutputFile is { } file)
            rows.Add(new(IconRead, "its output", c.LastOutputUtc is { } o ? SessionState.FormatAge(o) : null, null,
                () => new() { new(null, OutputTail(file), null, null, Mono: true) }));
        return rows.Count > 0 ? rows : new() { new(null, "Nothing recorded yet.", null, null) };
    }

    private static DetailPopup.Row PromptRow(string transcript, ClaudeSessionRegistry.Prompt prompt, char icon, string from) =>
        new(icon, TaskbarOverlay.Truncate(from + Flatten(prompt.Text), 160), SessionState.FormatAge(prompt.AtUtc), null, () =>
        {
            var (text, images) = ClaudeSessionRegistry.PromptDetail(transcript, prompt);
            return DetailRows(new(text, true, images));
        });

    // A tool call reads as its verb in bold, then what it did: a shell call's description (its command line is in
    // its preview), the file read or edited, the pattern searched. Claude's own replies are in italics.
    private static DetailPopup.Row ActionRow(string transcript, ClaudeSessionRegistry.ClaudeAction a)
    {
        string? lead = null, text = Flatten(a.Text);
        if (a.Command != null) lead = a.Tool;
        else if (VerbFirst.Contains(a.Tool) && text.IndexOf(' ') is int space and > 0)
            (lead, text) = (text[..space].TrimEnd(':'), text[(space + 1)..]);
        return new(a.Tool.Length == 0 ? IconReply : ToolIcon(a.Tool), TaskbarOverlay.Truncate(text, 160),
            SessionState.FormatAge(a.AtUtc), null, () => DetailRows(ClaudeSessionRegistry.ActionDetail(transcript, a)),
            Lead: lead, Italic: a.Tool.Length == 0);
    }

    // Tools whose summary starts with its verb ("Read MainPage.cs", "Agent: …"); see ClaudeSessionRegistry.ToolSummary.
    private static readonly HashSet<string> VerbFirst = new() { "Read", "Edit", "Write", "MultiEdit", "NotebookEdit", "Grep", "Glob", "WebFetch", "WebSearch", "Agent", "Task", "SendMessage", "Skill" };

    private static List<DetailPopup.Row> DetailRows(ClaudeSessionRegistry.Detail d)
    {
        var rows = new List<DetailPopup.Row>();
        if (d.Command != null) rows.Add(new(null, d.Command, null, null, Shell: true));
        if (d.Text.Trim().Length > 0) rows.Add(new(null, d.Text.Trim(), null, null, Markdown: d.Markdown, Mono: !d.Markdown));
        if (d.Diff != null) rows.Add(new(null, d.Diff, null, null, Diff: true));
        rows.AddRange(d.Images.Select(Picture));
        return rows.Count > 0 ? rows : new() { new(null, "(empty)", null, null) };
    }

    private static DetailPopup.Row Picture(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var decoded = Image.FromStream(stream);
        return new(null, "", null, new Bitmap(decoded));
    }

    /// <summary>The end of a shell's output file.</summary>
    private static string OutputTail(string file)
    {
        if (!File.Exists(file)) return "(its output file is gone)";
        const int Tail = 30_000;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        bool cut = stream.Length > Tail;
        if (cut) stream.Seek(-Tail, SeekOrigin.End);
        return (cut ? "… (earlier output cut)\n" : "") + new StreamReader(stream).ReadToEnd();
    }

    /// <summary>The last prompt in full, read again from the transcript, with the images pasted into it.</summary>
    private static List<DetailPopup.Row> PromptRows(SessionState.AgentNode n)
    {
        var prompt = n.Prompt!;
        var (text, images) = ClaudeSessionRegistry.PromptDetail(n.TranscriptPath, prompt);
        var rows = new List<DetailPopup.Row> { new(prompt.FromPeer ? IconClaude : IconUser, (prompt.FromPeer ? $"{prompt.From}: " : "") + text.Trim(),
            SessionState.FormatAge(prompt.AtUtc), null) };
        foreach (var bytes in images)
        {
            using var stream = new MemoryStream(bytes);
            using var decoded = Image.FromStream(stream);
            rows.Add(new(null, "", null, new Bitmap(decoded)));
        }
        return rows;
    }

    /// <summary>Claude's recent tool calls and replies, oldest first, each behind the icon of its kind.</summary>
    private static List<DetailPopup.Row> ActionRows(SessionState.AgentNode n) =>
        n.Actions.Count == 0
            ? new() { new(ActivityIcon(n.HookEvent, n.ToolName), n.Doing, null, null) }
            : n.Actions.TakeLast(15).Select(a => new DetailPopup.Row(a.Tool.Length == 0 ? IconReply : ToolIcon(a.Tool),
                  TaskbarOverlay.Truncate(a.Text.Trim(), 400), SessionState.FormatAge(a.AtUtc), null)).ToList();

    /// <summary>Click a session: go to its desktop, taking this window along. Right-click a session:
    /// close it. Right-click elsewhere: close the long-idle ones.</summary>
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var at = new Point(e.X - AutoScrollPosition.X, e.Y - AutoScrollPosition.Y);
        if (e.Button == MouseButtons.Left && _placed.SelectMany(p => p.Children.Select(c => (p, c.Child, c.Box))).FirstOrDefault(x => ChildChevron(x.Box).Contains(at)) is { p: not null } card)
        {
            try { OpenTile(card.p.Node.SessionId, card.Child.Id); }
            catch (Exception ex) { ProjectSwitcher.ReportError(ex); }
            return;
        }
        if (e.Button == MouseButtons.Left && _placed.FirstOrDefault(p => SessionChevron(p.Box).Contains(at)) is { } opened)
        {
            try { OpenTile(opened.Node.SessionId, null); }
            catch (Exception ex) { ProjectSwitcher.ReportError(ex); }
            return;
        }
        if (e.Button == MouseButtons.Left && ChevronAt(at) is { } chevron)
        {
            if (_browseFor == chevron.Key) CloseBrowse();
            else
            {
                try { OpenBrowse(chevron.Key, GridChevron(chevron.Zone), chevron.Rows()); }
                catch (Exception ex) { ProjectSwitcher.ReportError(ex); }
            }
            return;
        }
        if (e.Button == MouseButtons.Left) CloseBrowse();
        if (e.Button == MouseButtons.Left && _placed.SelectMany(p => p.Messages).FirstOrDefault(m => DismissZone(m.Box).Contains(at)) is { Box.Width: > 0 } dismissed)
        {
            Run("message.dismiss", new { session = dismissed.Message.FromId, to = dismissed.Message.To });
            return;
        }
        if (e.Button == MouseButtons.Left && _placed.FirstOrDefault(p => p.Box.Contains(at)) is { } clicked)
        {
            try { GoTo(clicked.Node); }
            catch (Exception ex) { ProjectSwitcher.ReportError(ex); }
            return;
        }
        if (e.Button != MouseButtons.Right) return;
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

    private void GoTo(SessionState.AgentNode n)
    {
        var target = _desktops.GetDesktops().FirstOrDefault(d => d.Id == n.DesktopId)
            ?? throw new InvalidOperationException($"\"{n.Title}\" is on a desktop that no longer exists.");
        IntPtr window = FindForm()!.Handle;
        if (_desktops.GetDesktopForWindow(window) != target.Id && !_desktops.MoveWindowToDesktop(window, target.Id))
            throw new InvalidOperationException($"Could not take the DesktopNames window to desktop {target.Index + 1}.");
        Program.Host!.Commands.Execute("desktop.switch", System.Text.Json.JsonSerializer.SerializeToElement(new { n = target.Index + 1 }));
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
        CloseHover();
        SetHighlight(null);
    }

    private string? _highlight;

    private void SetHighlight(string? sessionId)
    {
        if (sessionId == _highlight) return;
        _highlight = sessionId;
        Invalidate();
    }
}

/// <summary>
/// A popup of rows — an icon, wrapped text and a small note (who, when), or an image — divided by
/// dotted lines. Never takes focus; images it is given are disposed with it. A row with a
/// <see cref="Row.Preview"/> opens that preview beside the popup while the mouse is over it; the
/// wheel scrolls the preview when one is open, else the popup.
/// </summary>
internal sealed class DetailPopup : Form
{
    /// <summary><see cref="Age"/> ("2m") ends the first line in bold, the text wrapping around it;
    /// a row with the same age as the one before it doesn't repeat it. <see cref="Markdown"/>: the
    /// text is drawn as markdown; <see cref="Mono"/>: as plain text in a fixed-width font; <see cref="Shell"/>:
    /// as a PowerShell or Bash command, highlighted. <see cref="Mine"/>: the user's own message — shaded, and it stays at
    /// the top while the rows under it scroll away, until the next one of the user's pushes it off. <see cref="Running"/>:
    /// the step running now — a turning Claude icon in place of its own. <see cref="Diff"/>: the text is a line diff
    /// ("- " removed, "+ " added, "  " kept), drawn in red and green as git shows it.</summary>
    public sealed record Row(char? Icon, string Text, string? Age, Image? Image,
                             Func<List<Row>>? Preview = null, bool Markdown = false, bool Mono = false, bool Shell = false, string? Lead = null, bool Italic = false,
                             bool Mine = false, bool Running = false, bool Diff = false);

    private sealed record Block(string Text, Font Font, int Indent, int Top, int Height, Color? Back = null, Color? Fore = null);

    private static readonly Color RemovedBack = Color.FromArgb(255, 215, 213), RemovedFore = Color.FromArgb(150, 0, 20),
                                  AddedBack = Color.FromArgb(204, 255, 212), AddedFore = Color.FromArgb(0, 110, 30),
                                  KeptFore = Color.FromArgb(110, 110, 110);
    private sealed record Run(string Text, Color Color, Point At);
    private sealed record Laid(Row Row, Rectangle Bounds, Rectangle Text, Rectangle Image, string? Age, string First, string Remainder, List<Block>? Blocks, List<Run>? Runs = null);

    // Rows sit 2 px above and below their dividers: vertical space is what the open tiles are short of.
    private const int Pad = 8, RowGap = 2;
    private readonly int _maxImageHeight;
    private readonly List<Laid> _laid = new();
    private readonly Font _font, _italicFont, _iconFont, _ageFont, _monoFont, _h1Font, _h2Font;
    private int _width;
    // Embedded in an open Agents tile, not a popup of its own: the tile sizes it, and the wheel scrolls
    // its rows, not the open preview (the preview scrolls under the mouse).
    private readonly bool _embedded;
    private int _contentHeight;
    private int _scroll;
    private DetailPopup? _preview;
    private int _previewRow = -1;

    private const TextFormatFlags Wrap = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.PreserveGraphicsClipping;
    private const TextFormatFlags Line = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.PreserveGraphicsClipping;

    public DetailPopup(List<Row> rows, Point screenAt, int width = 520) : this(width)
    {
        StartPosition = FormStartPosition.Manual;
        LayOut(rows, screenAt);
        var work = Screen.FromPoint(screenAt).WorkingArea;
        Location = new Point(Math.Clamp(screenAt.X + 14, work.Left, work.Right - Width), Math.Clamp(screenAt.Y + 18, work.Top, work.Bottom - Height));
    }

    /// <summary>Rows inside <paramref name="host"/> at <paramref name="bounds"/>, scrolled to the newest.</summary>
    public DetailPopup(List<Row> rows, Control host, Rectangle bounds) : this(bounds.Width)
    {
        _embedded = true;
        TopLevel = false;
        Bounds = bounds;
        host.Controls.Add(this);
        LayOut(rows, host.PointToScreen(bounds.Location));
        _scroll = Math.Max(0, _contentHeight - ClientSize.Height);
        Show();
    }

    private DetailPopup(int width)
    {
        // Its own 9 pt text, whatever the Agents tab is zoomed to.
        _font = new Font("Segoe UI", 9f);
        _iconFont = new Font("Segoe MDL2 Assets", 9f);
        _ageFont = new Font("Segoe UI", 9f, FontStyle.Bold);
        _monoFont = new Font("Consolas", 8.5f);
        _h1Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        _h2Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _italicFont = new Font("Segoe UI", 9f, FontStyle.Italic);
        _maxImageHeight = width * 4 / 5;
        _width = width;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = TipCard.Back(false);
        DoubleBuffered = true;
        _spin.Tick += (_, _) =>
        {
            foreach (var laid in _laid.Where(l => l.Row.Running))
            {
                var r = SpinBox(laid.Text);
                Invalidate(new Rectangle(r.X - 3, r.Y - 3 - _scroll, r.Width + 6, r.Height + 6));
            }
        };
        FormClosed += (_, _) =>
        {
            _spin.Dispose();
            _preview?.Close();
            foreach (var laid in _laid) laid.Row.Image?.Dispose();
            _font.Dispose();
            _iconFont.Dispose();
            _ageFont.Dispose();
            _monoFont.Dispose();
            _h1Font.Dispose();
            _h2Font.Dispose();
            _italicFont.Dispose();
        };
    }

    /// <summary>Show these rows instead, in place: the ages move on, a new entry appears. A preview stays
    /// open while the row it belongs to is still listed.</summary>
    public void SetRows(List<Row> rows)
    {
        string? previewed = _previewRow >= 0 && _previewRow < _laid.Count ? _laid[_previewRow].Row.Text : null;
        bool atEnd = _scroll >= _contentHeight - ClientSize.Height;
        foreach (var laid in _laid) laid.Row.Image?.Dispose();
        LayOut(rows, PointToScreen(Point.Empty));
        var work = Screen.FromControl(this).WorkingArea;
        if (!_embedded && Bottom > work.Bottom) Top = Math.Max(work.Top, work.Bottom - Height);
        if (atEnd) _scroll = int.MaxValue;
        _previewRow = previewed == null ? -1 : _laid.FindIndex(l => l.Row.Text == previewed);
        if (_previewRow < 0)
        {
            _preview?.Close();
            _preview = null;
        }
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _contentHeight - ClientSize.Height));
        Invalidate();
    }

    /// <summary>Embedded: move to <paramref name="bounds"/>, laying the rows out again at its width.</summary>
    public void Place(Rectangle bounds, List<Row> rows)
    {
        // Whether it showed the newest rows is judged at its old size: shrunk, it would seem scrolled up.
        bool atEnd = _scroll >= _contentHeight - ClientSize.Height;
        _width = bounds.Width;
        Bounds = bounds;
        SetRows(rows);
        if (atEnd) ScrollBy(int.MaxValue / 2);
    }

    /// <summary>Lay the rows out and size the window to them, at most the height of the screen at <paramref name="at"/>
    /// (embedded: only the rows; its tile sets its size).</summary>
    private void LayOut(List<Row> rows, Point at)
    {
        var font = _font;
        int width = _width;
        _laid.Clear();
        bool icons = rows.Any(r => r.Icon != null);
        int iconW = icons ? font.Height + 4 : 0, textW = width - 2 * Pad - iconW;
        int y = Pad;
        string? previousAge = null;
        foreach (var row in rows)
        {
            if (row.Image is { } image)
            {
                float scale = Math.Min(1f, Math.Min((float)(width - 2 * Pad) / image.Width, (float)_maxImageHeight / image.Height));
                var r = new Rectangle(Pad, y, (int)(image.Width * scale), (int)(image.Height * scale));
                _laid.Add(new(row, new Rectangle(0, y - RowGap, width, r.Height + 2 * RowGap), Rectangle.Empty, r, null, "", "", null));
                y = r.Bottom + RowGap * 2;
                continue;
            }
            if (row.Shell)
            {
                var runs = LayOutShell(row.Text, textW, out int h);
                _laid.Add(new(row, new Rectangle(0, y - RowGap, width, h + 2 * RowGap), new Rectangle(Pad + iconW, y, textW, h), Rectangle.Empty, null, "", "", null, runs));
                y += h + RowGap * 2;
                continue;
            }
            if (row.Markdown || row.Mono || row.Diff)
            {
                var blocks = row.Diff ? LayOutDiff(row.Text, textW) : row.Mono ? new List<Block> { Measure(row.Text, _monoFont, 0, 0, textW) } : LayOutMarkdown(row.Text, textW);
                int h = blocks.Count == 0 ? 0 : blocks[^1].Top + blocks[^1].Height;
                _laid.Add(new(row, new Rectangle(0, y - RowGap, width, h + 2 * RowGap), new Rectangle(Pad + iconW, y, textW, h), Rectangle.Empty, null, "", "", blocks));
                y += h + RowGap * 2;
                continue;
            }
            string? age = row.Age == previousAge ? null : row.Age;
            previousAge = row.Age ?? previousAge;
            int ageW = age != null ? TextRenderer.MeasureText(age, _ageFont, Size.Empty, Line).Width : 0;
            int leadW = LeadWidth(row);
            bool split = age != null || row.Lead != null;
            var rowFont = row.Italic ? _italicFont : font;
            var (first, rest) = split ? SplitFirstLine(row.Text, rowFont, textW - ageW - 6 - leadW) : ("", row.Text);
            int textH = (split ? font.Height : 0)
                      + (rest.Length > 0 || !split ? TextRenderer.MeasureText(rest, rowFont, new Size(textW, int.MaxValue), Wrap).Height : 0);
            _laid.Add(new(row, new Rectangle(0, y - RowGap, width, textH + 2 * RowGap), new Rectangle(Pad + iconW, y, textW, textH), Rectangle.Empty, age, first, rest, null));
            y += textH + RowGap * 2;
        }
        _contentHeight = y - RowGap + Pad;
        _spin.Enabled = _laid.Any(l => l.Row.Running);
        if (!_embedded) Size = new Size(width, Math.Min(_contentHeight, Screen.FromPoint(at).WorkingArea.Height - 20));
    }

    /// <summary>The width of a row's bold lead ("Bash") and the space after it.</summary>
    private int LeadWidth(Row row) => row.Lead is { } lead ? TextRenderer.MeasureText(lead + " ", _ageFont, Size.Empty, Line).Width : 0;

    private static Block Measure(string text, Font font, int indent, int top, int width) =>
        new(text, font, indent, top, TextRenderer.MeasureText(text.Length == 0 ? " " : text, font, new Size(width - indent, int.MaxValue), Wrap).Height);

    private List<Block> LayOutDiff(string diff, int width)
    {
        var blocks = new List<Block>();
        int top = 0;
        foreach (var line in diff.Split('\n'))
        {
            var (back, fore) = line.StartsWith("- ") ? (RemovedBack, RemovedFore) : line.StartsWith("+ ") ? (AddedBack, AddedFore) : ((Color?)null, KeptFore);
            var b = Measure(line, _monoFont, 0, top, width) with { Back = back, Fore = fore };
            blocks.Add(b);
            top += b.Height;
        }
        return blocks;
    }

    private static readonly System.Text.RegularExpressions.Regex Link = new(@"\[([^\]]+)\]\([^)]+\)");

    /// <summary>Plain markdown, line by line: headings, bullets, code blocks and tables in a fixed
    /// font, a gap for a blank line; inline marks (**, `, links) are dropped for their text.</summary>
    private List<Block> LayOutMarkdown(string text, int width)
    {
        var blocks = new List<Block>();
        int top = 0;
        void Add(string t, Font f, int indent)
        {
            var b = Measure(t, f, indent, top, width);
            blocks.Add(b);
            top += b.Height;
        }
        string Inline(string s) => Link.Replace(s, "$1").Replace("**", "").Replace("__", "").Replace("`", "");
        var code = new List<string>();
        bool fenced = false;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            if (raw.TrimStart().StartsWith("```"))
            {
                if (fenced) { Add(string.Join("\n", code), _monoFont, 8); top += 4; code.Clear(); }
                fenced = !fenced;
                continue;
            }
            if (fenced) { code.Add(raw); continue; }
            string line = raw.TrimEnd();
            string trimmed = line.TrimStart();
            if (trimmed.Length == 0) top += 6;
            else if (trimmed.StartsWith("# ")) { top += 4; Add(Inline(trimmed[2..]), _h1Font, 0); top += 2; }
            else if (trimmed.StartsWith('#')) { top += 4; Add(Inline(trimmed.TrimStart('#').Trim()), _h2Font, 0); top += 2; }
            else if (trimmed.StartsWith('|')) Add(line, _monoFont, 0);
            else if (trimmed is "---" or "***") top += 8;
            else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ")) Add("•  " + Inline(trimmed[2..]), _font, 12 + (line.Length - trimmed.Length) * 4);
            else if (trimmed.StartsWith("> ")) Add(Inline(trimmed[2..]), _font, 12);
            else Add(Inline(line), _font, 0);
        }
        if (code.Count > 0) Add(string.Join("\n", code), _monoFont, 8);
        return blocks;
    }

    private const TextFormatFlags Code = Line | TextFormatFlags.NoPadding;

    /// <summary>A command in the fixed-width font, token by token in its colour, wrapped at the width.</summary>
    private List<Run> LayOutShell(string command, int width, out int height)
    {
        int charW = TextRenderer.MeasureText(new string('M', 20), _monoFont, Size.Empty, Code).Width / 20;
        int cols = Math.Max(20, width / charW), lineH = _monoFont.Height;
        var runs = new List<Run>();
        int col = 0, row = 0;
        foreach (var (token, color) in ShellTokens(command.Replace("\r", "").Trim()))
        {
            var lines = token.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) { row++; col = 0; }
                string rest = lines[i];
                while (rest.Length > 0)
                {
                    if (col == cols) { row++; col = 0; }
                    int take = Math.Min(rest.Length, cols - col);
                    runs.Add(new(rest[..take], color, new Point(col * charW, row * lineH)));
                    col += take;
                    rest = rest[take..];
                }
            }
        }
        height = (row + 1) * lineH;
        return runs;
    }

    private static readonly System.Text.RegularExpressions.Regex ShellToken = new(
        @"(?<comment>(?<=^|\s)#[^\n]*)|(?<string>'[^']*'|""(?:[^""\\`]|[\\`].)*"")|(?<variable>\$\{?[\w:]+\}?)|(?<op>\|\||&&|[|;&<>()={}])|(?<space>\s+)|(?<param>(?<=\s)--?[A-Za-z][\w-]*)|(?<number>\b\d+(?:\.\d+)?\b)|(?<word>[^\s'""$|;&<>()={}]+)|(?<other>.)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Singleline);

    private static readonly HashSet<string> ShellKeywords = new(StringComparer.OrdinalIgnoreCase)
        { "if", "then", "else", "elif", "fi", "for", "foreach", "in", "do", "done", "while", "until", "case", "esac", "function", "return", "try", "catch", "finally", "param" };

    private static readonly Color CommandColor = Color.FromArgb(121, 94, 38), KeywordColor = Color.FromArgb(175, 0, 219),
        ParamColor = Color.FromArgb(0, 92, 197), StringColor = Color.FromArgb(163, 21, 21), VariableColor = Color.FromArgb(0, 16, 128),
        CommentColor = Color.FromArgb(0, 128, 0), NumberColor = Color.FromArgb(9, 134, 88), OpColor = Color.FromArgb(110, 110, 110);

    /// <summary>A command's tokens with their colours: the first word of each statement is the command.</summary>
    private static IEnumerable<(string Text, Color Color)> ShellTokens(string command)
    {
        bool commandNext = true;
        foreach (System.Text.RegularExpressions.Match m in ShellToken.Matches(command))
        {
            string t = m.Value;
            if (m.Groups["space"].Success)
            {
                if (t.Contains('\n')) commandNext = true;
                yield return (t, SystemColors.ControlText);
            }
            else if (m.Groups["comment"].Success) yield return (t, CommentColor);
            else if (m.Groups["string"].Success) { yield return (t, StringColor); commandNext = false; }
            else if (m.Groups["variable"].Success) { yield return (t, VariableColor); commandNext = false; }
            else if (m.Groups["op"].Success)
            {
                if (t is "|" or "||" or "&&" or ";" or "(" or "{" or "&") commandNext = true;
                yield return (t, OpColor);
            }
            else if (m.Groups["param"].Success) yield return (t, ParamColor);
            else if (m.Groups["number"].Success && !commandNext) yield return (t, NumberColor);
            else if (ShellKeywords.Contains(t)) { yield return (t, KeywordColor); commandNext = t is not ("in" or "function" or "param"); }
            else if (commandNext) { yield return (t, CommandColor); commandNext = false; }
            else yield return (t, SystemColors.ControlText);
        }
    }

    /// <summary>The words of the text's first line that fit <paramref name="width"/>, and the rest.</summary>
    private static (string First, string Remainder) SplitFirstLine(string text, Font font, int width)
    {
        int newline = text.IndexOf('\n');
        string line = newline >= 0 ? text[..newline].TrimEnd('\r') : text;
        int fit = line.Length;
        while (fit > 0 && TextRenderer.MeasureText(line[..fit], font, Size.Empty, Line).Width > width)
            fit = line.LastIndexOf(' ', fit - 1) is int space and > 0 ? space : fit - 1;
        string rest = line[fit..].TrimStart() + (newline >= 0 ? "\n" + text[(newline + 1)..] : "");
        return (line[..fit], rest.Trim());
    }

    public IntPtr? PreviewHandle => _preview is { IsDisposed: false } p ? p.Handle : null;

    /// <summary>Open row <paramref name="index"/>'s preview, as the mouse over it does.</summary>
    public void HoverRow(int index)
    {
        if (index < 0 || index >= _laid.Count) throw new ArgumentException($"row must be 0-{_laid.Count - 1}");
        if (_laid[index].Row.Preview == null) throw new InvalidOperationException($"row {index} has nothing to preview");
        ShowPreview(index);
        _preview!.Refresh();
    }

    private void ShowPreview(int index)
    {
        _preview?.Close();
        _preview = null;
        _previewRow = index;
        Invalidate();
        if (_laid[index].Row.Preview is not { } preview) return;
        var work = Screen.FromControl(this).WorkingArea;
        var on = RectangleToScreen(ClientRectangle);
        var popup = new DetailPopup(preview(), on.Location, 640);
        int x = on.Right + 4 + popup.Width <= work.Right ? on.Right + 4 : on.Left - 4 - popup.Width;
        int y = Math.Clamp(on.Top + _laid[index].Bounds.Top - _scroll, work.Top, work.Bottom - popup.Height);
        popup.Location = new Point(x, y);
        if (_embedded) popup.MouseLeave += OnPreviewMouseLeave;
        popup.Show();
        _preview = popup;
    }

    private int RowAt(int y) => _laid.FindIndex(l => l.Bounds.Top <= y + _scroll && y + _scroll < l.Bounds.Bottom);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = StickyAt(e.Y) ?? RowAt(e.Y);
        if (index >= 0 && index != _previewRow && _laid[index].Row.Preview != null) ShowPreview(index);
    }

    // Embedded, the preview goes when the mouse leaves the tile, except into the preview (to scroll it),
    // and when it leaves the preview, except back into the tile.
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_embedded && _preview is { IsDisposed: false } preview && !preview.Bounds.Contains(Cursor.Position)) ClosePreview();
    }

    private void OnPreviewMouseLeave(object? sender, EventArgs e)
    {
        if (!RectangleToScreen(ClientRectangle).Contains(Cursor.Position)) ClosePreview();
    }

    // A tile's popup goes with the tile; the preview it opened is a window of its own and stayed on screen.
    protected override void OnHandleDestroyed(EventArgs e)
    {
        _preview?.Close();
        base.OnHandleDestroyed(e);
    }

    public void ClosePreview()
    {
        _preview?.Close();
        _preview = null;
        _previewRow = -1;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_embedded && _preview is { IsDisposed: false } preview) preview.ScrollBy(-e.Delta);
        else ScrollBy(-e.Delta);
    }

    // The user's message the top of the view is in, held there, a few lines at most; their next one pushes it up.
    private const int StickyLines = 4;

    private (int Index, int Top, int Height)? Sticky()
    {
        int index = _laid.FindLastIndex(l => l.Row.Mine && l.Bounds.Top < _scroll);
        if (index < 0) return null;
        int height = Math.Min(_laid[index].Bounds.Height, StickyLines * _font.Height + 2 * RowGap);
        int next = _laid.FindIndex(index + 1, l => l.Row.Mine);
        int top = next >= 0 ? Math.Min(0, _laid[next].Bounds.Top - _scroll - height) : 0;
        return top + height > 0 ? (index, top, height) : null;
    }

    private int? StickyAt(int y) => Sticky() is { } s && y >= s.Top && y < s.Top + s.Height ? s.Index : null;

    public void ScrollBy(int pixels)
    {
        _scroll = Math.Clamp(_scroll + pixels, 0, Math.Max(0, _contentHeight - ClientSize.Height));
        Invalidate();
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

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        bool more = _scroll < _contentHeight - ClientSize.Height;
        using (var border = new Pen(TipCard.Border(false))) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        if (more)
            TextRenderer.DrawText(g, "▼ wheel for more", _font, new Rectangle(0, Height - _font.Height - 3, Width - Pad, _font.Height), SystemColors.GrayText, TextFormatFlags.Right | TextFormatFlags.NoPrefix);
        g.SetClip(new Rectangle(1, 1, Width - 2, Height - 2 - (more ? _font.Height + 4 : 0)));
        g.TranslateTransform(0, -_scroll);
        using var dotted = new Pen(TipCard.Border(false)) { DashStyle = DashStyle.Dot };
        using (var mine = new SolidBrush(MineBack))
            foreach (var laid in _laid.Where(l => l.Row.Mine)) g.FillRectangle(mine, laid.Bounds);
        if (_previewRow >= 0 && _previewRow < _laid.Count)
            using (var hover = new SolidBrush(Color.FromArgb(40, SystemColors.Highlight))) g.FillRectangle(hover, _laid[_previewRow].Bounds);
        for (int i = 0; i < _laid.Count; i++) DrawRow(g, i, dotted);
        if (Sticky() is { } sticky)
        {
            // The same row again over the others, at the top, cut to its few lines, a line under it.
            var bounds = _laid[sticky.Index].Bounds;
            g.ResetTransform();
            g.SetClip(new Rectangle(1, Math.Max(1, sticky.Top), Width - 2, sticky.Height + Math.Min(0, sticky.Top - 1)));
            g.TranslateTransform(0, sticky.Top - bounds.Top);
            using (var mine = new SolidBrush(MineBack)) g.FillRectangle(mine, bounds);
            DrawRow(g, sticky.Index, null);
            g.ResetTransform();
            g.ResetClip();
            using var edge = new Pen(TipCard.Border(false));
            g.DrawLine(edge, 1, sticky.Top + sticky.Height - 1, Width - 2, sticky.Top + sticky.Height - 1);
        }
    }

    private static readonly Color MineBack = Color.FromArgb(222, 234, 250);

    // The running step's Claude icon turns: an animation, so a display timer, on only while a row runs.
    private readonly System.Windows.Forms.Timer _spin = new() { Interval = 40 };
    private const int SpinMs = 1600;
    private static readonly Bitmap SpinImage = new Icon(AgentsView.ClaudeIcon, 32, 32).ToBitmap();

    private Rectangle SpinBox(Rectangle text) => new(Pad, text.Top + 1, _font.Height - 2, _font.Height - 2);

    private static void DrawSpinning(Graphics g, Rectangle r)
    {
        var state = g.Save();
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TranslateTransform(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        g.RotateTransform(360f * (Environment.TickCount64 % SpinMs) / SpinMs);
        g.DrawImage(SpinImage, -r.Width / 2f, -r.Height / 2f, r.Width, r.Height);
        g.Restore(state);
    }

    private void DrawRow(Graphics g, int i, Pen? dotted)
    {
        var (row, _, text, image, age, first, rest, blocks, runs) = _laid[i];
        if (i > 0 && dotted != null)
        {
            int top = (row.Image != null ? image.Top : text.Top) - RowGap;
            g.DrawLine(dotted, Pad, top, Width - Pad, top);
        }
        if (row.Image is { } img) { g.DrawImage(img, image); return; }
        if (row.Running) DrawSpinning(g, SpinBox(text));
        else if (row.Icon == AgentsView.IconClaude)
            g.DrawImage(SpinImage, SpinBox(text)); // DrawIcon ignores the scroll transform: the icon went missing once scrolled
        else if (row.Icon is { } icon)
            TextRenderer.DrawText(g, icon.ToString(), _iconFont, new Point(Pad, text.Top + 1), SystemColors.ControlText, Line);
        if (runs != null)
        {
            foreach (var run in runs)
                TextRenderer.DrawText(g, run.Text, _monoFont, new Point(text.Left + run.At.X, text.Top + run.At.Y), run.Color, Code);
            return;
        }
        if (blocks != null)
        {
            foreach (var b in blocks)
            {
                var r = new Rectangle(text.Left + b.Indent, text.Top + b.Top, text.Width - b.Indent, b.Height);
                if (b.Back is { } back) { using var fill = new SolidBrush(back); g.FillRectangle(fill, r); }
                TextRenderer.DrawText(g, b.Text, b.Font, r, b.Fore ?? SystemColors.ControlText, Wrap);
            }
            return;
        }
        int restTop = text.Top;
        if (age != null || row.Lead != null)
        {
            if (age != null)
            {
                int ageW = TextRenderer.MeasureText(age, _ageFont, Size.Empty, Line).Width;
                TextRenderer.DrawText(g, age, _ageFont, new Point(text.Right - ageW, text.Top), SystemColors.ControlText, Line);
            }
            if (row.Lead != null) TextRenderer.DrawText(g, row.Lead, _ageFont, new Point(text.Left, text.Top), SystemColors.ControlText, Line);
            TextRenderer.DrawText(g, first, row.Italic ? _italicFont : _font, new Point(text.Left + LeadWidth(row), text.Top), SystemColors.ControlText, Line);
            restTop += _font.Height;
        }
        TextRenderer.DrawText(g, rest, row.Italic ? _italicFont : _font, new Rectangle(text.Left, restTop, text.Width, text.Bottom - restTop), SystemColors.ControlText, Wrap);
    }
}
