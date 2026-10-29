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
    private int BoxH => 10 + 6 * Line;

    // TextRenderer ignores the Graphics transform unless told to: without this the text stayed put while the boxes scrolled.
    private const TextFormatFlags Flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis
                                          | TextFormatFlags.PreserveGraphicsTranslateTransform;
    private int ChildH => 6 + 2 * Line;
    private const int Gap = 12, ChildGap = 6, Pad = 16, BandGap = 44;

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
        _shimmer.Tick += (_, _) => { foreach (var live in _live) Invalidate(new Rectangle(live.X + AutoScrollPosition.X, live.Y + AutoScrollPosition.Y, live.Width, live.Height)); };
        // A popup is its own top-level window: it goes when the tab hides or the window closes, not only when the mouse leaves.
        VisibleChanged += (_, _) => { UpdateShimmer(); if (!Visible) ClosePopup(); };
        Disposed += (_, _) => { ClosePopup(); _sessions.Updated -= OnUpdated; _tip.Dispose(); _iconFont.Dispose(); _shimmer.Dispose(); };
        Reflow();
    }

    // What is happening right now — busy sessions' current action, their subagents' current tool call —
    // carries a highlight sweeping across it, so the eye goes there. It is animation, so it runs on a
    // timer, but only while this tab is visible and something is busy, and repaints only those strips.
    private readonly System.Windows.Forms.Timer _shimmer = new() { Interval = 40 };
    private List<Rectangle> _live = new();
    private const int ShimmerMs = 1800;

    /// <summary>Animate only while this tab is the one showing (a control in a hidden tab page still says Visible).</summary>
    public void UpdateShimmer() =>
        _shimmer.Enabled = _live.Count > 0 && Visible && (Parent is not TabPage page || page.Parent is not TabControl tabs || tabs.SelectedTab == page);

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
        var desktops = _desktops.GetDesktops();
        int Order(Guid id) => desktops.FindIndex(d => d.Id == id) is int i and >= 0 ? i : int.MaxValue;
        string Label(Guid id) => desktops.FirstOrDefault(d => d.Id == id) is { } d ? $"{d.Index + 1} · {d.Name}" : "? · no desktop";
        // The messages each session sent: its newest few, under its agents and shells.
        var sent = _graph.Edges.GroupBy(e => e.FromId).ToDictionary(g => g.Key, g => g.OrderBy(e => e.AtUtc).ToList());
        List<SessionState.AgentEdge> SentBy(SessionState.AgentNode n) => sent.TryGetValue(n.SessionId, out var list) ? list : new();
        int StackHeight(SessionState.AgentNode n) => BoxH + (n.Children.Count + SentBy(n).Count) * (ChildH + ChildGap);

        int width = Math.Max(ClientSize.Width, 400);
        int maxCols = _columns;
        BoxW = (width - 2 * Pad - (_columns - 1) * Gap) / _columns - 2 * GroupPad;
        ScaleText(BoxW / 300f);
        int x = Pad, top = Pad, bandHeight = 0;
        var placed = new List<Placed>();
        var groups = new List<(Guid, string, Rectangle)>();
        // Every desktop that no longer exists is one "no desktop" frame.
        Guid GroupOf(Guid id) => Order(id) == int.MaxValue ? Guid.Empty : id;
        // Desktops where something is happening first, then the ones open to input, the greyed ones last; desktop order within each.
        int Tier(IEnumerable<SessionState.AgentNode> ns) =>
            ns.Any(n => Working(n) || n.State is StateKind.Asking or StateKind.Error) ? 0
            : ns.Any(n => n.State != StateKind.None && DateTime.UtcNow - n.LastActiveUtc <= Stale) ? 1 : 2;
        foreach (var group in _graph.Nodes.GroupBy(n => GroupOf(n.DesktopId)).OrderBy(g => Tier(g)).ThenBy(g => Order(g.Key)))
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
        Reflow();
        Invalidate();
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
                DrawChild(g, child, r, Fill(p.Node), live: p.Node.State == StateKind.Busy);
            }
            foreach (var (message, r) in p.Messages)
            {
                int spine = p.Box.Left + (BoxW - ChildW) / 2;
                g.DrawLines(connector, new[] { new Point(spine, p.Box.Bottom), new Point(spine, r.Top + r.Height / 2), new Point(r.Left, r.Top + r.Height / 2) });
                DrawMessage(g, message, r, p);
            }
            DrawSession(g, p);
        }
        // Hovering a message tag outlines the session it went to.
        if (_placed.FirstOrDefault(p => p.Node.SessionId == _highlight) is { } to)
        {
            using var outline = new Pen(Color.FromArgb(0, 120, 215), 3f);
            using var path = TaskbarOverlay.RoundedRect(Rectangle.Inflate(to.Box, 3, 3), 8);
            g.DrawPath(outline, path);
        }
    }

    /// <summary>Idle this long, a session is a leftover rather than live work: drawn faded.</summary>
    private static readonly TimeSpan Stale = TimeSpan.FromHours(1);

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
        int lx = p.Box.Left + 8, w = BoxW - 16, y = p.Box.Top + 5;
        // Line 1: the title VS Code lists it under; top right, last active / started, ⏳ while background work runs.
        string corner = (n.Usage is { } model ? ModelName(model.Model) + " · " : "") + (n.BackgroundActive ? "⏳ " : "") + SessionState.FormatAge(n.LastActiveUtc)
                      + (n.StartedUtc is { } s ? "/" + SessionState.FormatAge(s) : "");
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
        TextRenderer.DrawText(g, n.Title, bold, new Rectangle(lx + badge, y, w - cornerW - 4 - badge, Line), ink, Flags);
        TextRenderer.DrawText(g, corner, bold, new Rectangle(lx + w - cornerW, y, cornerW, Line), ink, Flags);
        // Line 2: the name sessions message it by (when the title isn't it) and its folder.
        string id = n.Name == n.Title ? "" : n.Name + " · ";
        TextRenderer.DrawText(g, id + ShortFolder(n.Cwd), Font, new Rectangle(lx, y + Line, w, Line), dim, Flags);
        // Lines 3-4: the last prompt — the user's, or another session's message to it.
        if (n.Prompt is { } prompt)
            DrawCommand(g, prompt.FromPeer ? IconClaude : IconUser,
                (prompt.FromPeer ? $"{prompt.From}: " : "") + Flatten(prompt.Text), Font, ink, new Rectangle(lx, y + 2 * Line, w, 2 * Line));
        // Lines 5-6: what Claude did last, behind the icon of its kind. The state is the colour.
        using var italic = new Font(Font, FontStyle.Italic);
        if (n.State == StateKind.Busy) DrawShimmer(g, ActionZone(p.Box));
        DrawCommand(g, ActivityIcon(n.HookEvent, n.ToolName), Flatten(n.Doing), italic, ink, new Rectangle(lx, y + 4 * Line, w, 2 * Line));
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
    private void DrawChild(Graphics g, SessionState.AgentChild c, Rectangle r, Color parentFill, bool live)
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
            DrawCommand(g, last == null ? ToolIcon(c.ActivityTool) : last.Tool.Length == 0 ? IconReply : ToolIcon(last.Tool), state, Font, Color.FromArgb(60, 60, 60), new Rectangle(lx, y + Line, w, Line));
        }
        else
            TextRenderer.DrawText(g, $"{KindLabel(c.Kind)} · {state}", Font, new Rectangle(lx + Line + 2, y + Line, w - Line - 2, Line), Color.FromArgb(60, 60, 60), Flags);
    }

    // A leading cd into the working folder: the same for every call, it hides what the command does.
    private static readonly System.Text.RegularExpressions.Regex LeadingCd = new(@"^\s*(cd|Set-Location)\s+(""[^""]*""|'[^']*'|\S+)\s*(;|&&)\s*");

    /// <summary>A command line on one line, without its leading cd.</summary>
    private static string Shown(string command) => LeadingCd.Replace(command.ReplaceLineEndings(" ").Trim(), "");

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

    /// <summary>Who a message went to: the session's title, or for one of the sender's own subagents, what that agent does.</summary>
    private string Recipient(SessionState.AgentEdge e, Placed from) =>
        e.ToSessionId != null ? _graph.Nodes.FirstOrDefault(n => n.SessionId == e.ToSessionId)?.Title ?? e.To
        : from.Children.FirstOrDefault(c => c.Child.Id == e.To).Child is { } agent ? $"subagent: {agent.Description}" : $"subagent {e.To}";

    /// <summary>
    /// A message the session sent, drawn like its agents and shells: line 1 the send icon and who it
    /// went to, in the corner when; line 2 its summary, and that the sender waits on a reply.
    /// </summary>
    private void DrawMessage(Graphics g, SessionState.AgentEdge e, Rectangle r, Placed from)
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
        string second = (e.Waiting ? "waiting for a reply · " : "") + (e.Summary.Length > 0 ? e.Summary : "message");
        TextRenderer.DrawText(g, second, Font, new Rectangle(lx + Line + 2, y + Line, w - Line - 2, Line), Color.FromArgb(60, 60, 60), Flags);
    }

    private Rectangle DismissZone(Rectangle message) => new(message.Right - Line - 6, message.Top + 2, Line + 4, Line + 2);

    private string MessageTip(SessionState.AgentEdge e, Placed from) =>
        $"to {Recipient(e, from)}, sent {SessionState.FormatAge(e.AtUtc)} ago"
        + (e.Waiting ? "\nthe sender waits on a reply: it ends when the recipient's turn ends" : "")
        + (e.Summary.Length > 0 ? "\n\n" + e.Summary : "");

    // The box's two prompt lines and two action lines (DrawSession's lines 3-4 and 5-6).
    private Rectangle PromptZone(Rectangle box) => new(box.Left, box.Top + 5 + 2 * Line, box.Width, 2 * Line);
    private Rectangle ActionZone(Rectangle box) => new(box.Left, box.Top + 5 + 4 * Line, box.Width, 2 * Line);

    private DetailPopup? _popup;
    private string? _popupFor;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var at = new Point(e.X - AutoScrollPosition.X, e.Y - AutoScrollPosition.Y);
        // Over a prompt or the actions: the full message, or the recent actions, in a popup.
        foreach (var p in _placed)
        {
            string? zone = PromptZone(p.Box).Contains(at) && p.Node.Prompt != null ? "prompt" : ActionZone(p.Box).Contains(at) ? "actions" : null;
            if (zone == null) continue;
            string key = zone + "/" + p.Node.SessionId;
            if (key != _popupFor)
            {
                ClosePopup();
                _tipFor = null;
                _tip.Hide(this);
                _popupFor = key;
                _popup = new DetailPopup(zone == "prompt" ? PromptRows(p.Node) : ActionRows(p.Node), Cursor.Position);
                _popup.Show();
            }
            return;
        }
        ClosePopup();

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
                if (r.Contains(at)) { key2 = p.Node.SessionId + "/msg/" + m.To + m.AtUtc.Ticks; text = MessageTip(m, p); highlight = m.ToSessionId; }
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
        ClosePopup();
        _popupFor = zone + "/" + p.Node.SessionId;
        var corner = PointToScreen(new Point(p.Box.Left + AutoScrollPosition.X, (zone == "prompt" ? PromptZone(p.Box) : ActionZone(p.Box)).Top + AutoScrollPosition.Y));
        _popup = new DetailPopup(zone == "prompt" ? PromptRows(p.Node) : ActionRows(p.Node), corner);
        _popup.Show();
        _popup.Refresh(); // painted before this returns, so a screenshot right after shows it
    }

    public IntPtr? PopupHandle => _popup is { IsDisposed: false } popup ? popup.Handle : null;

    public void ClosePopup()
    {
        _popup?.Close();
        _popup = null;
        _popupFor = null;
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
            : n.Actions.Select(a => new DetailPopup.Row(a.Tool.Length == 0 ? IconReply : ToolIcon(a.Tool),
                  TaskbarOverlay.Truncate(a.Text.Trim(), 400), SessionState.FormatAge(a.AtUtc), null)).ToList();

    /// <summary>Click a session: go to its desktop, taking this window along. Right-click a session:
    /// close it. Right-click elsewhere: close the long-idle ones.</summary>
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var at = new Point(e.X - AutoScrollPosition.X, e.Y - AutoScrollPosition.Y);
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
        ClosePopup();
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
/// A hover popup of rows — an icon, wrapped text and a small note (who, when), or an image —
/// divided by dotted lines. Never takes focus; images it is given are disposed with it.
/// </summary>
internal sealed class DetailPopup : Form
{
    /// <summary><see cref="Age"/> ("2m") ends the first line in bold, the text wrapping around it;
    /// a row with the same age as the one before it doesn't repeat it.</summary>
    public sealed record Row(char? Icon, string Text, string? Age, Image? Image);

    private const int Pad = 8, RowGap = 6, MaxWidth = 520, MaxImageHeight = 420;
    private readonly List<(Row Row, Rectangle Text, Rectangle Image, string? Age, string First, string Remainder)> _laid = new();
    private readonly Font _font, _iconFont, _ageFont;

    private const TextFormatFlags Wrap = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak;

    public DetailPopup(List<Row> rows, Point screenAt)
    {
        // Its own 9 pt text, whatever the Agents tab is zoomed to.
        _font = new Font("Segoe UI", 9f);
        _iconFont = new Font("Segoe MDL2 Assets", 9f);
        _ageFont = new Font("Segoe UI", 9f, FontStyle.Bold);
        var font = _font;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = TipCard.Back(false);
        DoubleBuffered = true;

        int iconW = font.Height + 4, textW = MaxWidth - 2 * Pad - iconW;
        int y = Pad;
        string? previousAge = null;
        foreach (var row in rows)
        {
            if (row.Image is { } image)
            {
                float scale = Math.Min(1f, Math.Min((float)(MaxWidth - 2 * Pad) / image.Width, (float)MaxImageHeight / image.Height));
                var r = new Rectangle(Pad, y, (int)(image.Width * scale), (int)(image.Height * scale));
                _laid.Add((row, Rectangle.Empty, r, null, "", ""));
                y = r.Bottom + RowGap * 2;
                continue;
            }
            string? age = row.Age == previousAge ? null : row.Age;
            previousAge = row.Age ?? previousAge;
            int ageW = age != null ? TextRenderer.MeasureText(age, _ageFont, Size.Empty, Line).Width : 0;
            var (first, rest) = age != null ? SplitFirstLine(row.Text, font, textW - ageW - 6) : ("", row.Text);
            int textH = (age != null ? font.Height : 0)
                      + (rest.Length > 0 || age == null ? TextRenderer.MeasureText(rest, font, new Size(textW, int.MaxValue), Wrap).Height : 0);
            _laid.Add((row, new Rectangle(Pad + iconW, y, textW, textH), Rectangle.Empty, age, first, rest));
            y += textH + RowGap * 2;
        }
        var work = Screen.FromPoint(screenAt).WorkingArea;
        Size = new Size(MaxWidth, Math.Min(y - RowGap + Pad, work.Height - 20));
        Location = new Point(Math.Clamp(screenAt.X + 14, work.Left, work.Right - Width), Math.Clamp(screenAt.Y + 18, work.Top, work.Bottom - Height));
        FormClosed += (_, _) =>
        {
            foreach (var laid in _laid) laid.Row.Image?.Dispose();
            _font.Dispose();
            _iconFont.Dispose();
            _ageFont.Dispose();
        };
    }

    private const TextFormatFlags Line = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

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
        using (var border = new Pen(TipCard.Border(false))) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        using var dotted = new Pen(TipCard.Border(false)) { DashStyle = DashStyle.Dot };
        for (int i = 0; i < _laid.Count; i++)
        {
            var (row, text, image, age, first, rest) = _laid[i];
            if (i > 0)
            {
                int top = (row.Image != null ? image.Top : text.Top) - RowGap;
                g.DrawLine(dotted, Pad, top, Width - Pad, top);
            }
            if (row.Image is { } img) { g.DrawImage(img, image); continue; }
            if (row.Icon == AgentsView.IconClaude)
                g.DrawIcon(AgentsView.ClaudeIcon, new Rectangle(Pad, text.Top + 1, _font.Height - 2, _font.Height - 2));
            else if (row.Icon is { } icon)
                TextRenderer.DrawText(g, icon.ToString(), _iconFont, new Point(Pad, text.Top + 1), SystemColors.ControlText, TextFormatFlags.NoPrefix);
            int restTop = text.Top;
            if (age != null)
            {
                int ageW = TextRenderer.MeasureText(age, _ageFont, Size.Empty, Line).Width;
                TextRenderer.DrawText(g, age, _ageFont, new Point(text.Right - ageW, text.Top), SystemColors.ControlText, Line);
                TextRenderer.DrawText(g, first, _font, new Point(text.Left, text.Top), SystemColors.ControlText, Line);
                restTop += _font.Height;
            }
            TextRenderer.DrawText(g, rest, _font, new Rectangle(text.Left, restTop, text.Width, text.Bottom - restTop), SystemColors.ControlText, Wrap);
        }
    }
}

