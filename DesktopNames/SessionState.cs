using System.Diagnostics;
using System.Text.Json;

namespace DesktopNames;

/// <summary>
/// Lifecycle state of one external "source" (a Claude session, eventually anything else).
/// Numeric order is priority — higher wins the aggregate color when multiple sessions
/// share a desktop. <see cref="None"/> means "no entry / nothing to show".
/// </summary>
/// <summary>
/// Lifecycle state ordered by user-attention priority — higher wins the aggregate color
/// when multiple sessions share a desktop. Asking demands action; Busy is active work;
/// Error is "Claude finished with a problem" (rate-limit/auth/billing); Ready is the
/// resting state. Order matches the user's preference: yellow > orange > red > green.
/// </summary>
internal enum StateKind { None = 0, Ready = 1, Error = 2, Busy = 3, Asking = 4 }

/// <summary>What an incoming event does to a session's pending question, independent of
/// what it does to the session's activity. See <see cref="SessionEntry.Ask"/>.</summary>
internal enum AskChange
{
    /// <summary>Event says nothing about a pending question (ordinary tool traffic).</summary>
    None,
    /// <summary>Claude is now blocked on the user (permission prompt, blocking tool, Stop-with-question).</summary>
    Set,
    /// <summary>Turn boundary — whatever was pending is resolved.</summary>
    Clear,
    /// <summary>A tool finished; clears the pending question only if it's the one that was gated.</summary>
    ClearIfMatches,
}

/// <summary>
/// A question Claude is blocked on. Identified by tool + description rather than a
/// tool_use_id because PermissionRequest doesn't carry one — but PreToolUse,
/// PermissionRequest and PostToolUse for the same call all produce the same pair, which
/// is what lets a completed tool clear the prompt it was gated by.
/// </summary>
internal sealed class PendingAsk
{
    public string ToolName { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>Text to show while the question is pending — kept separately so ordinary
    /// background tool traffic can update the entry's Body without losing the question.</summary>
    public string Body { get; init; } = "";
    public DateTime SinceUtc { get; init; }

    public bool Matches(string toolName, string description) =>
        ToolName == toolName &&
        (Description.Length == 0 || description.Length == 0 || Description == description);
}

internal sealed class SessionEntry
{
    /// <summary>How many recent message bodies to keep per session for the flyout.</summary>
    public const int RecentBodiesMax = 5;

    public string Source { get; init; } = "";
    public string SessionId { get; init; } = "";

    /// <summary>What the session is doing: Busy / Ready / Error. Never Asking — being
    /// blocked on the user is <see cref="Ask"/>, which is a separate fact that coexists
    /// with activity. A session can be running four parallel tools *and* waiting on a
    /// permission prompt; collapsing both into one field is what let background
    /// PreToolUse events repaint the desktop orange over a live question.</summary>
    public StateKind Activity { get; set; }

    /// <summary>Questions Claude is blocked on, oldest first. A list rather than one slot
    /// because prompts can queue: answering the second must not clear the first.</summary>
    public List<PendingAsk> Asks { get; } = new();

    /// <summary>Displayed state. A pending question outranks whatever else is happening.</summary>
    public StateKind State => Asks.Count > 0 ? StateKind.Asking : Activity;

    /// <summary>The question to show — the oldest one still unanswered.</summary>
    public PendingAsk? OldestAsk => Asks.Count > 0 ? Asks[0] : null;

    public string Title { get; set; } = "";
    public string Body { get; set; } = "";

    /// <summary>The last few <see cref="Body"/> values, oldest first, most recent last. The
    /// reassign flyout shows these so the user can identify a session by its recent activity,
    /// not just its current line. Capped at <see cref="RecentBodiesMax"/>.</summary>
    public List<string> RecentBodies { get; } = new();

    public int SessionPid { get; init; }
    public int VsCodePid { get; init; }
    public DateTime LastSeenUtc { get; set; }

    /// <summary>Working directory the hook reported. Used by the overlay's reassign flyout to
    /// locate this session's VS Code window and to remember a manual correction.</summary>
    public string Cwd { get; set; } = "";

    /// <summary>The hook event that produced the current state (PreToolUse, Notification, …).</summary>
    public string HookEvent { get; set; } = "";

    /// <summary>The tool name when <see cref="HookEvent"/> is PreToolUse/PostToolUse.</summary>
    public string ToolName { get; set; } = "";

    /// <summary>The turn ended (Ready, open to input) but background agents / shells / Monitors
    /// are still running and Claude will resume when they report. Shown as ⏳ on a green button.</summary>
    public bool BackgroundActive { get; set; }

    /// <summary>Background agents of this session that are still working: agentId → type and the
    /// last thing it did. Filled by their PreToolUse, dropped at SubagentStop or at a Stop with no
    /// background work left. Not persisted — a restart forgets in-flight agents.</summary>
    public Dictionary<string, (string Type, string Line)> Agents { get; } = new();

    /// <summary>
    /// True once the user has acted on this entry (clicked the button, switched to the desktop).
    /// Aggregation treats consumed entries as <see cref="StateKind.None"/> so the color clears
    /// until the next message updates state.
    /// </summary>
    public bool Consumed { get; set; }
}

/// <summary>
/// Per-desktop session aggregation. UI-thread only — all mutation comes through the
/// host form's <c>BeginInvoke</c>, so no locks. Fires <see cref="Changed"/> only when
/// a desktop's aggregate (state or count) actually changes; subscribers can use that
/// to drive invalidation and pulse animations cheaply.
/// </summary>
internal sealed class SessionState
{
    /// <summary>
    /// Sentinel desktop id for sessions DN couldn't match to any real desktop. Rendered
    /// as a virtual "?" button at the end of every overlay. Never equal to any real
    /// desktop GUID returned by Windows.
    /// </summary>
    public static readonly Guid UnresolvedDesktopId = new("ffffffff-ffff-ffff-ffff-ffffffffffff");

    /// <summary>Leading markers on tooltip lines telling the overlay's owner-drawn tooltip how to
    /// style them: <see cref="TipItalic"/> = the Claude message (italic), <see cref="TipDim"/> =
    /// the cwd sub-line (dim). <see cref="StripTipMarkers"/> removes them for plain-text consumers.</summary>
    internal const char TipItalic = (char)0x1F;
    internal const char TipDim = (char)0x1E;

    public static string StripTipMarkers(string s) =>
        s.Replace(TipItalic.ToString(), "").Replace(TipDim.ToString(), "    ");

    public event Action<Guid>? Changed;

    private readonly Dictionary<Guid, List<SessionEntry>> _byDesktop = new();
    private readonly Dictionary<(string source, string sessionId), Guid> _location = new();

    // Resolution-cache state. Both sticky maps survive a session whose VSCode window has
    // closed (Fix #4 from DESKTOPNAMES-INTEGRATION.md). Learned paths accumulate from any
    // successful resolution and let a sibling cwd resolve via walk-up next time.
    private readonly Dictionary<string, (Guid desktopId, DateTime lastSeen)> _stickyBySession = new();
    private readonly Dictionary<string, (Guid desktopId, DateTime lastSeen)> _stickyByTranscript = new();
    private readonly Dictionary<string, Guid> _learnedPaths = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan StickyTtl = TimeSpan.FromHours(1);

    private readonly Func<int, bool>? _isProcessAlive;

    // Persist live highlights so a DesktopNames restart (redeploy, autostart re-login) keeps
    // the colors instead of going dark until the next hook. Only the displayed entries are
    // saved; the resolver caches (sticky/learned) are cheap to rebuild and aren't persisted.
    private static string StatePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopNames", "sessions.json");
    private bool _loading;

    public SessionState(Func<int, bool>? isProcessAlive = null)
    {
        _isProcessAlive = isProcessAlive;
        LoadFromDisk();
        // Every aggregate change (apply / consume / sweep / move / clear) is a persistable
        // edit — one subscription covers them all. Pulse animation doesn't fire Changed, so
        // this only writes when the displayed state actually moves.
        Changed += _ => { if (!_loading) SaveToDisk(); };
    }

    private sealed class PersistedEntry
    {
        public Guid DesktopId { get; set; }
        public string Source { get; set; } = "";
        public string SessionId { get; set; } = "";
        public StateKind Activity { get; set; }
        public List<PendingAsk> Asks { get; set; } = new();
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public List<string> RecentBodies { get; set; } = new();
        public int SessionPid { get; set; }
        public int VsCodePid { get; set; }
        public string HookEvent { get; set; } = "";
        public string ToolName { get; set; } = "";
        public string Cwd { get; set; } = "";
        public bool BackgroundActive { get; set; }
        public bool Consumed { get; set; }
    }

    private void SaveToDisk()
    {
        try
        {
            var list = new List<PersistedEntry>();
            foreach (var (desktopId, entries) in _byDesktop)
                foreach (var e in entries)
                    list.Add(new PersistedEntry
                    {
                        DesktopId = desktopId, Source = e.Source, SessionId = e.SessionId,
                        Activity = e.Activity, Asks = new List<PendingAsk>(e.Asks),
                        Title = e.Title, Body = e.Body,
                        RecentBodies = new List<string>(e.RecentBodies),
                        SessionPid = e.SessionPid, VsCodePid = e.VsCodePid,
                        HookEvent = e.HookEvent, ToolName = e.ToolName, Cwd = e.Cwd,
                        BackgroundActive = e.BackgroundActive, Consumed = e.Consumed,
                    });
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(list));
        }
        catch { /* best-effort; a lost highlight cache is not worth crashing over */ }
    }

    private void LoadFromDisk()
    {
        _loading = true;
        try
        {
            if (!File.Exists(StatePath)) return;
            var list = JsonSerializer.Deserialize<List<PersistedEntry>>(File.ReadAllText(StatePath));
            if (list == null) return;
            var now = DateTime.UtcNow;
            foreach (var p in list)
            {
                // Drop sessions whose claude.exe is already gone — don't restore a stale
                // highlight for a session that ended while we were down. Entries with no
                // liveness pid (pid<=0, e.g. test injects) are kept.
                if (p.SessionPid > 0 && !IsAlive(p.SessionPid)) continue;
                if (!_byDesktop.TryGetValue(p.DesktopId, out var bucket))
                    _byDesktop[p.DesktopId] = bucket = new List<SessionEntry>();
                // Entries written by a pre-Activity/Asks build deserialize with neither set,
                // so they carry no state worth restoring — drop rather than resurrect a blank row.
                if (p.Activity == StateKind.None && (p.Asks == null || p.Asks.Count == 0)) continue;
                var restored = new SessionEntry
                {
                    Source = p.Source, SessionId = p.SessionId,
                    Activity = p.Activity,
                    Title = p.Title, Body = p.Body, SessionPid = p.SessionPid,
                    VsCodePid = p.VsCodePid, HookEvent = p.HookEvent, ToolName = p.ToolName,
                    Cwd = p.Cwd, BackgroundActive = p.BackgroundActive, Consumed = p.Consumed,
                    LastSeenUtc = now, // reset so the busy-stale sweep doesn't reap on restore
                };
                if (p.Asks != null) restored.Asks.AddRange(p.Asks);
                if (p.RecentBodies != null) restored.RecentBodies.AddRange(p.RecentBodies);
                bucket.Add(restored);
                _location[(p.Source, p.SessionId)] = p.DesktopId;
            }
        }
        catch { /* corrupt cache — start clean */ }
        finally { _loading = false; }
    }

    /// <summary>
    /// Apply an event. <paramref name="activity"/> is null when the event says nothing about
    /// what the session is doing (a permission prompt doesn't stop the work already running);
    /// <paramref name="remove"/> drops the session entirely (SessionEnd).
    /// Returns the session's effective state afterwards, so callers log what was actually
    /// stored rather than what they sent.
    /// </summary>
    public StateKind Apply(Guid desktopId, string source, string sessionId,
                           StateKind? activity, AskChange ask, bool remove,
                           string title, string body,
                           int sessionPid, int vsCodePid,
                           string? hookEvent = null, string? toolName = null,
                           string? toolDescription = null, string cwd = "",
                           bool backgroundActive = false)
    {
        var key = (source, sessionId);
        Guid prevDesktop = _location.TryGetValue(key, out var d) ? d : Guid.Empty;
        var prevPrevAgg = prevDesktop != Guid.Empty ? GetAggregate(prevDesktop) : default;
        var prevTargetAgg = GetAggregate(desktopId);
        var prevTargetGlyph = GetGlyph(desktopId);

        StateKind effective = StateKind.None;
        if (remove)
        {
            if (prevDesktop != Guid.Empty) RemoveEntry(prevDesktop, source, sessionId);
            _location.Remove(key);
        }
        else
        {
            // If it moved desktops, drop the old listing first.
            if (prevDesktop != Guid.Empty && prevDesktop != desktopId)
                RemoveEntry(prevDesktop, source, sessionId);

            effective = UpsertEntry(desktopId, source, sessionId, activity, ask, title, body,
                                    sessionPid, vsCodePid, hookEvent ?? "", toolName ?? "",
                                    toolDescription ?? "", cwd ?? "", backgroundActive);
            _location[key] = desktopId;
        }

        // Fire Changed for any desktop whose aggregate moved.
        if (prevDesktop != Guid.Empty && prevDesktop != desktopId)
        {
            var nowAgg = GetAggregate(prevDesktop);
            if (!AggregateEquals(prevPrevAgg, nowAgg)) Changed?.Invoke(prevDesktop);
        }
        var targetNow = GetAggregate(desktopId);
        // Ready ⏳ -> Ready (background work finished) keeps the aggregate but drops the glyph;
        // without this the hourglass would stay painted.
        // (Busy tool letters are repainted by the pulse animation, so only Ready needs it.)
        if (!AggregateEquals(prevTargetAgg, targetNow) ||
            (targetNow.state == StateKind.Ready && prevTargetGlyph != GetGlyph(desktopId)))
            Changed?.Invoke(desktopId);
        return effective;
    }

    /// <summary>Aggregate state, count of sessions at that state, and a tooltip body.</summary>
    public (StateKind state, int count, string tooltip) GetAggregate(Guid desktopId)
    {
        if (!_byDesktop.TryGetValue(desktopId, out var list) || list.Count == 0)
            return (StateKind.None, 0, "");

        StateKind max = StateKind.None;
        foreach (var e in list)
        {
            if (e.Consumed) continue;
            if (e.State > max) max = e.State;
        }
        if (max == StateKind.None) return (StateKind.None, 0, "");

        int count = 0;
        var lines = new List<string>();
        foreach (var e in list)
        {
            if (e.Consumed) continue;
            if (e.State == max) count++;
            var label = string.IsNullOrEmpty(e.Title) ? e.SessionId : e.Title;
            // First 2 chars of the sessionId disambiguate sessions whose cwd-basename labels
            // collide (two sessions in the same folder, or one whose cwd wandered there).
            var sid = e.SessionId.Length >= 2 ? e.SessionId[..2] : e.SessionId;
            // Three lines per session: a regular header, the Claude message italic on its own
            // line, then the full cwd dim below it (the real disambiguator when the title's leaf
            // name is generic). TipItalic/TipDim mark the latter two for the owner-drawn tooltip.
            string line = $"{label} ({sid} · {e.State.ToString().ToLowerInvariant()}, {FormatAge(e.LastSeenUtc)})";
            if (!string.IsNullOrEmpty(e.Body)) line += "\n" + TipItalic + e.Body;
            if (!string.IsNullOrEmpty(e.Cwd)) line += "\n" + TipDim + e.Cwd;
            lines.Add(line);
        }
        return (max, count, string.Join("\n", lines));
    }

    /// <summary>Compact relative age since last activity: "10s", "5m", "4h", "3d".</summary>
    public static string FormatAge(DateTime lastSeenUtc)
    {
        var d = DateTime.UtcNow - lastSeenUtc;
        if (d.TotalSeconds < 60) return $"{Math.Max(0, (int)d.TotalSeconds)}s";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m";
        if (d.TotalHours < 24)   return $"{(int)d.TotalHours}h";
        return $"{(int)d.TotalDays}d";
    }

    /// <summary>Count of live (non-consumed) sessions on a desktop — what the overlay's
    /// count badge shows. Differs from <see cref="GetAggregate"/>'s count, which counts only
    /// the sessions at the dominant state; this is the total the flyout lists.</summary>
    public int LiveCount(Guid desktopId)
    {
        if (!_byDesktop.TryGetValue(desktopId, out var list)) return 0;
        int n = 0;
        foreach (var e in list) if (!e.Consumed) n++;
        return n;
    }

    /// <summary>
    /// Single-character glyph indicating the dominant activity on this desktop:
    /// <c>?</c> asking, <c>!</c> error, <c>…</c> thinking (busy without a known tool),
    /// or the first letter of the running tool for acting. Null when nothing to show.
    /// </summary>
    public string? GetGlyph(Guid desktopId)
    {
        if (!_byDesktop.TryGetValue(desktopId, out var list) || list.Count == 0) return null;

        // Pick the highest-priority non-consumed entry; tie-break by most recent.
        SessionEntry? dominant = null;
        foreach (var e in list)
        {
            if (e.Consumed) continue;
            if (dominant == null ||
                e.State > dominant.State ||
                (e.State == dominant.State && e.LastSeenUtc > dominant.LastSeenUtc))
            {
                dominant = e;
            }
        }
        if (dominant == null) return null;

        switch (dominant.State)
        {
            case StateKind.Asking: return "?";
            case StateKind.Error:  return "!";
            case StateKind.Ready:  return dominant.BackgroundActive ? "⏳" : null; // open to input; ⏳ while background work runs
            case StateKind.Busy:
                // PreToolUse → acting (tool letter). UserPromptSubmit/SessionStart → thinking.
                if (dominant.HookEvent == "PreToolUse")
                    return ToolGlyph(dominant.ToolName);
                return "…";
            default: return null;
        }
    }

    private static string? ToolGlyph(string? toolName)
    {
        if (string.IsNullOrEmpty(toolName)) return "…";
        // MCP tool names look like mcp__server__action — strip to the action.
        int idx = toolName.LastIndexOf("__", StringComparison.Ordinal);
        string raw = idx >= 0 ? toolName[(idx + 2)..] : toolName;
        return raw.Length > 0 ? raw[..1].ToUpperInvariant() : "…";
    }

    /// <summary>Remove every entry from the given source (used by the tray "test clear" item).</summary>
    public void RemoveBySource(string source)
    {
        var changed = new HashSet<Guid>();
        foreach (var (desktopId, list) in _byDesktop)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].Source != source) continue;
                _location.Remove((list[i].Source, list[i].SessionId));
                list.RemoveAt(i);
                changed.Add(desktopId);
            }
        }
        foreach (var g in changed) Changed?.Invoke(g);
    }

    /// <summary>Drop a single session's indicator (the flyout's per-row "x"). The session may
    /// reappear if it's alive and speaks again; for a stale leftover it stays gone.</summary>
    public void RemoveSession(string source, string sessionId)
    {
        var key = (source, sessionId);
        if (!_location.TryGetValue(key, out var desktop)) return;
        RemoveEntry(desktop, source, sessionId);
        _location.Remove(key);
        Changed?.Invoke(desktop);
    }

    /// <summary>Wipe every unresolved-bucket entry (called from the "?" button's context menu).</summary>
    public void ClearUnresolved()
    {
        if (!_byDesktop.TryGetValue(UnresolvedDesktopId, out var list) || list.Count == 0) return;
        foreach (var e in list) _location.Remove((e.Source, e.SessionId));
        list.Clear();
        Changed?.Invoke(UnresolvedDesktopId);
    }

    /// <summary>Mark every entry on this desktop as consumed (clears the color until next update).</summary>
    public void Consume(Guid desktopId)
    {
        if (!_byDesktop.TryGetValue(desktopId, out var list)) return;
        var prev = GetAggregate(desktopId);
        bool any = false;
        foreach (var e in list) { if (!e.Consumed) { e.Consumed = true; any = true; } }
        if (!any) return;
        var now = GetAggregate(desktopId);
        if (!AggregateEquals(prev, now)) Changed?.Invoke(desktopId);
    }

    /// <summary>
    /// Drop dead sessions. Called from a ~1Hz timer on the UI thread.
    /// Entries are removed if their <c>SessionPid</c> no longer exists, or if a busy
    /// entry hasn't heartbeated in 60s (a busy Claude that crashed mid-tool).
    /// </summary>
    public void Sweep()
    {
        var now = DateTime.UtcNow;
        var changed = new HashSet<Guid>();
        foreach (var (desktopId, list) in _byDesktop)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var e = list[i];
                // SessionPid here is the *liveness* pid the caller chose to give us — for
                // claude-code that's parentPid (claude.exe). If 0, we have no liveness
                // signal and must wait for explicit idle / SessionEnd.
                bool dead = e.SessionPid > 0 && !IsAlive(e.SessionPid);
                // Busy stale = no heartbeat for 15 minutes. PreToolUse fires per tool call,
                // and Claude can generate prose for many minutes between tool calls; the
                // earlier 60s threshold was reaping live sessions prematurely.
                bool stale = e.State == StateKind.Busy && (now - e.LastSeenUtc).TotalMinutes > 15;
                // No-liveness reap: many sessions are launched via Git Bash, whose parent walk
                // lands on MSYS pid 1, so we get no liveness pid and can never detect death.
                // Those sessions would linger forever. Drop any non-Asking entry with no pid
                // after 15 min of silence (Asking must persist — the user has to act on it).
                // Sticky bindings outlive the reap, so if the session speaks again it
                // re-attaches to the same desktop.
                bool staleNoPid = e.SessionPid == 0 && e.State != StateKind.Asking
                                  && (now - e.LastSeenUtc).TotalMinutes > 15;
                if (!dead && !stale && !staleNoPid) continue;

                string reason = dead ? "dead-pid" : stale ? "stale-busy" : "stale-nopid";
                Log.Resolver($"sweep reap session={e.SessionId} src={e.Source} reason={reason} pid={e.SessionPid} desktop={desktopId}");
                list.RemoveAt(i);
                _location.Remove((e.Source, e.SessionId));
                changed.Add(desktopId);
            }
        }
        foreach (var g in changed) Changed?.Invoke(g);

        // Expire sticky-resolver entries beyond TTL. Learned paths don't expire — they're
        // useful even years later (the user's repo layout is stable).
        var stickyCutoff = now - StickyTtl;
        foreach (var k in _stickyBySession.Where(kv => kv.Value.lastSeen < stickyCutoff).Select(kv => kv.Key).ToList())
            _stickyBySession.Remove(k);
        foreach (var k in _stickyByTranscript.Where(kv => kv.Value.lastSeen < stickyCutoff).Select(kv => kv.Key).ToList())
            _stickyByTranscript.Remove(k);
    }

    /// <summary>
    /// Remember a successful resolution so future hooks from the same session resolve even
    /// after the originating VSCode window closes (sticky), and so sibling cwds resolve via
    /// the learned-path index. Should be called only on actual resolution success.
    /// </summary>
    public void RecordResolution(string sessionId, string? transcriptPath, string? cwd, Guid desktopId)
    {
        if (desktopId == Guid.Empty || string.IsNullOrEmpty(sessionId)) return;
        var now = DateTime.UtcNow;
        _stickyBySession[sessionId] = (desktopId, now);
        if (!string.IsNullOrEmpty(transcriptPath))
            _stickyByTranscript[transcriptPath] = (desktopId, now);
        if (!string.IsNullOrEmpty(cwd))
        {
            // Record only the exact cwd. Earlier this also walked up and recorded every
            // ancestor — but that meant the first session at c:\git\evolx\mobility_workflow
            // poisoned c:\git → Mobilität, and every other repo under c:\git resolved wrong.
            // Walk-up on lookup is enough; we don't need to pre-explode.
            _learnedPaths[NormalizePath(cwd)] = desktopId;
        }
    }

    /// <summary>Last known (session, transcript) → desktop within TTL.</summary>
    public bool TryStickyLookup(string sessionId, string? transcriptPath, out Guid desktopId)
    {
        var cutoff = DateTime.UtcNow - StickyTtl;
        if (!string.IsNullOrEmpty(sessionId) &&
            _stickyBySession.TryGetValue(sessionId, out var s) && s.lastSeen > cutoff)
        {
            desktopId = s.desktopId; return true;
        }
        if (!string.IsNullOrEmpty(transcriptPath) &&
            _stickyByTranscript.TryGetValue(transcriptPath, out var t) && t.lastSeen > cutoff)
        {
            desktopId = t.desktopId; return true;
        }
        desktopId = Guid.Empty;
        return false;
    }

    /// <summary>Walk cwd up against the learned-path index, deepest match wins.</summary>
    public bool TryLookupLearnedPath(string cwd, out Guid desktopId)
    {
        string norm = NormalizePath(cwd);
        while (norm.Length > 0)
        {
            if (_learnedPaths.TryGetValue(norm, out var d)) { desktopId = d; return true; }
            int slash = norm.LastIndexOf('\\');
            if (slash <= 2) break; // stop at drive root ("c:" or "c:\")
            norm = norm[..slash];
        }
        desktopId = Guid.Empty;
        return false;
    }

    /// <summary>Drop sticky entries for a session (called on idle / SessionEnd).</summary>
    public void ClearSticky(string sessionId, string? transcriptPath)
    {
        if (!string.IsNullOrEmpty(sessionId)) _stickyBySession.Remove(sessionId);
        if (!string.IsNullOrEmpty(transcriptPath)) _stickyByTranscript.Remove(transcriptPath);
    }

    private static string NormalizePath(string p) =>
        p.Replace('/', '\\').TrimEnd('\\', ' ');

    /// <summary>A session located on a desktop, with enough context for the reassign flyout.
    /// <paramref name="RecentBodies"/> is oldest-first; the flyout shows them so the row carries
    /// the same message context as the taskbar hovertext.</summary>
    public readonly record struct SessionRef(
        string Source, string SessionId, string Label, string Cwd, StateKind State,
        DateTime LastSeenUtc, IReadOnlyList<string> RecentBodies, int AgentCount);

    /// <summary>
    /// Record what a background agent of a known session is doing (or that it ended). Never
    /// touches the session's state or colour. Returns false when the session isn't tracked.
    /// </summary>
    public bool ApplyAgent(string source, string sessionId, string agentId, string agentType, string line, bool stop)
    {
        if (!_location.TryGetValue((source, sessionId), out var desktop) ||
            !_byDesktop.TryGetValue(desktop, out var list)) return false;
        var entry = list.FirstOrDefault(e => e.Source == source && e.SessionId == sessionId);
        if (entry == null) return false;
        if (stop) entry.Agents.Remove(agentId);
        else entry.Agents[agentId] = (agentType, line);
        return true;
    }

    /// <summary>
    /// Every session currently located on <paramref name="desktopId"/> (consumed or not),
    /// oldest-activity first so the flyout lists stale leftovers at the top. Label prefers the
    /// cwd basename (recognizable workspace name), falling back to the title then the session id.
    /// </summary>
    public List<SessionRef> GetSessions(Guid desktopId)
    {
        var result = new List<SessionRef>();
        if (!_byDesktop.TryGetValue(desktopId, out var list)) return result;
        foreach (var e in list.OrderBy(e => e.LastSeenUtc))
        {
            string label = !string.IsNullOrEmpty(e.Cwd) ? CwdBasename(e.Cwd)
                         : !string.IsNullOrEmpty(e.Title) ? e.Title
                         : e.SessionId;
            // The agents' current work follows the session's own recent lines, one line each.
            var lines = e.RecentBodies.Concat(e.Agents.Values.Select(a => $"⏳ {a.Type}: {a.Line}")).ToArray();
            result.Add(new SessionRef(e.Source, e.SessionId, label, e.Cwd, e.State, e.LastSeenUtc,
                                      lines, e.Agents.Count));
        }
        return result;
    }

    /// <summary>
    /// Relocate a single session's live indicator to <paramref name="newDesktop"/> without
    /// waiting for the next hook. Used when the user drags a session in the overlay; the
    /// caller is responsible for moving the actual window and recording the resolution so the
    /// move is durable. No-op if the session isn't located or is already there.
    /// </summary>
    public void MoveSessionToDesktop(string source, string sessionId, Guid newDesktop)
    {
        var key = (source, sessionId);
        if (!_location.TryGetValue(key, out var oldDesktop) || oldDesktop == newDesktop) return;
        if (!_byDesktop.TryGetValue(oldDesktop, out var list)) return;

        SessionEntry? entry = null;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Source == source && list[i].SessionId == sessionId)
            {
                entry = list[i];
                list.RemoveAt(i);
                break;
            }
        }
        if (entry == null) return;

        if (!_byDesktop.TryGetValue(newDesktop, out var newList))
        {
            newList = new List<SessionEntry>();
            _byDesktop[newDesktop] = newList;
        }
        entry.Consumed = false;
        newList.Add(entry);
        _location[key] = newDesktop;

        Changed?.Invoke(oldDesktop);
        Changed?.Invoke(newDesktop);
    }

    private static string CwdBasename(string cwd)
    {
        string p = cwd.Replace('/', '\\').TrimEnd('\\', ' ');
        int slash = p.LastIndexOf('\\');
        return slash >= 0 && slash < p.Length - 1 ? p[(slash + 1)..] : p;
    }

    /// <summary>Enumerates (desktopId, dominant state) for every desktop with an active aggregate.</summary>
    public IEnumerable<(Guid desktopId, StateKind state)> ActiveDesktops()
    {
        foreach (var (g, _) in _byDesktop)
        {
            var (s, _, _) = GetAggregate(g);
            if (s != StateKind.None) yield return (g, s);
        }
    }

    /// <summary>"4 sessions: 1 asking · 1 busy · 1 error · 1 ready" for tray status.</summary>
    public string BuildStatusSummary()
    {
        int asking = 0, busy = 0, error = 0, ready = 0, total = 0;
        foreach (var list in _byDesktop.Values)
        {
            foreach (var e in list)
            {
                if (e.Consumed) continue;
                total++;
                switch (e.State)
                {
                    case StateKind.Asking: asking++; break;
                    case StateKind.Busy:   busy++;   break;
                    case StateKind.Error:  error++;  break;
                    case StateKind.Ready:  ready++;  break;
                }
            }
        }
        if (total == 0) return "No Claude sessions";
        var parts = new List<string>();
        if (asking > 0) parts.Add($"{asking} asking");
        if (busy   > 0) parts.Add($"{busy} busy");
        if (error  > 0) parts.Add($"{error} error");
        if (ready  > 0) parts.Add($"{ready} ready");
        return $"{total} session{(total == 1 ? "" : "s")}: " + string.Join(" · ", parts);
    }

    private StateKind UpsertEntry(Guid desktopId, string source, string sessionId,
                                  StateKind? activity, AskChange ask, string title, string body,
                                  int sessionPid, int vsCodePid,
                                  string hookEvent, string toolName, string toolDescription, string cwd,
                                  bool backgroundActive)
    {
        if (!_byDesktop.TryGetValue(desktopId, out var list))
        {
            list = new List<SessionEntry>();
            _byDesktop[desktopId] = list;
        }
        SessionEntry? existing = null;
        foreach (var e in list)
        {
            if (e.Source == source && e.SessionId == sessionId) { existing = e; break; }
        }
        if (existing == null)
        {
            existing = new SessionEntry
            {
                Source = source,
                SessionId = sessionId,
                // A first sighting with no activity of its own (a bare PermissionRequest) is
                // still a live session — Busy is the honest floor under the question.
                Activity = activity ?? StateKind.Busy,
                SessionPid = sessionPid,
                VsCodePid = vsCodePid,
            };
            list.Add(existing);
        }
        else if (activity.HasValue)
        {
            existing.Activity = activity.Value;
        }

        ApplyAskChange(existing, ask, sessionId, toolName, toolDescription, body);

        existing.Title = title;
        // While a question is pending, the visible line stays the question. Background tools
        // keep reporting, and their descriptions are still recorded in RecentBodies, but they
        // don't get to overwrite what the user is being asked.
        existing.Body = existing.OldestAsk?.Body is { Length: > 0 } q ? q : body;
        existing.LastSeenUtc = DateTime.UtcNow;
        existing.Consumed = false;
        existing.HookEvent = hookEvent;
        existing.ToolName = toolName;
        existing.BackgroundActive = backgroundActive;
        if ((hookEvent == "Stop" && !backgroundActive) || hookEvent == "SessionStart") existing.Agents.Clear();
        if (!string.IsNullOrEmpty(cwd)) existing.Cwd = cwd;
        PushRecentBody(existing, body);
        return existing.State;
    }

    private static void ApplyAskChange(SessionEntry entry, AskChange ask, string sessionId,
                                       string toolName, string toolDescription, string body)
    {
        switch (ask)
        {
            case AskChange.Set:
                // Re-notification for the same prompt is common (Claude re-emits while it
                // waits), so only add one entry per distinct question.
                if (!entry.Asks.Any(a => a.Matches(toolName, toolDescription)))
                    entry.Asks.Add(new PendingAsk
                    {
                        ToolName = toolName,
                        Description = toolDescription,
                        Body = body,
                        SinceUtc = DateTime.UtcNow,
                    });
                break;

            case AskChange.Clear:
                if (entry.Asks.Count > 0)
                    Log.State($"ask cleared session={sessionId} (turn boundary, {entry.Asks.Count} pending)");
                entry.Asks.Clear();
                break;

            case AskChange.ClearIfMatches:
                // PostToolUse: the gated tool ran, so the user answered *that* prompt. A
                // different tool finishing means nothing — that's exactly the parallel
                // traffic that used to clobber the question.
                int i = entry.Asks.FindIndex(a => a.Matches(toolName, toolDescription));
                if (i >= 0)
                {
                    entry.Asks.RemoveAt(i);
                    Log.State($"ask answered session={sessionId} tool={toolName} ({entry.Asks.Count} still pending)");
                }
                break;
        }
    }

    /// <summary>Append a body to the rolling buffer, skipping empties and consecutive duplicates
    /// (PreToolUse repeats the same "Running {tool}" line), capped at <see cref="SessionEntry.RecentBodiesMax"/>.</summary>
    private static void PushRecentBody(SessionEntry entry, string body)
    {
        if (string.IsNullOrEmpty(body)) return;
        if (entry.RecentBodies.Count > 0 && entry.RecentBodies[^1] == body) return;
        entry.RecentBodies.Add(body);
        while (entry.RecentBodies.Count > SessionEntry.RecentBodiesMax)
            entry.RecentBodies.RemoveAt(0);
    }

    private void RemoveEntry(Guid desktopId, string source, string sessionId)
    {
        if (!_byDesktop.TryGetValue(desktopId, out var list)) return;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Source == source && list[i].SessionId == sessionId)
            {
                list.RemoveAt(i);
                return;
            }
        }
    }

    private bool IsAlive(int pid)
    {
        if (_isProcessAlive != null) return _isProcessAlive(pid);
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    private static bool AggregateEquals((StateKind state, int count, string tooltip) a,
                                        (StateKind state, int count, string tooltip) b)
        => a.state == b.state && a.count == b.count;
}
