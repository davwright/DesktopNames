using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNames;

/// <summary>
/// Named-pipe server at <c>\\.\pipe\DesktopNames</c>. Accepts one JSON line per
/// connection, resolves which desktop the source belongs to, and applies a state
/// transition to <see cref="SessionState"/>. Reply is one JSON line, then close.
///
/// Wire format documented in <c>DESKTOPNAMES-INTEGRATION.md</c> (sibling Claude-Alert repo).
/// </summary>
internal sealed class AlertPipeServer : IDisposable
{
    public const string PipeName = "DesktopNames";

    private readonly HostForm _host;
    private readonly SessionState _state;
    private readonly Settings _settings;
    private readonly DesktopService _desktop;
    private readonly WorkspaceFolderIndex _wsIndex = new();
    private Thread? _thread;
    private NamedPipeServerStream? _current;
    private volatile bool _stopping;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AlertPipeServer(HostForm host, SessionState state, Settings settings, DesktopService desktop)
    {
        _host = host;
        _state = state;
        _settings = settings;
        _desktop = desktop;
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "DesktopNames-AlertPipe" };
        _thread.Start();
    }

    public void Stop()
    {
        _stopping = true;
        try { _current?.Dispose(); } catch { }
        try { _thread?.Join(500); } catch { }
    }

    public void Dispose() => Stop();

    private void Loop()
    {
        while (!_stopping)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                // Per-user pipe: default DACL on a named pipe in the user's session grants
                // access only to that user, which is the threat model we want.
                pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                _current = pipe;
                pipe.WaitForConnection();
                HandleConnection(pipe);
            }
            catch (ObjectDisposedException) { /* Stop() disposed us — exit loop */ break; }
            catch (IOException) { /* client disconnect mid-handshake — keep going */ }
            catch
            {
                // Any other failure (bind, permission, etc.) — back off so we don't burn CPU.
                if (!_stopping) Thread.Sleep(500);
            }
            finally
            {
                try { pipe?.Dispose(); } catch { }
                if (ReferenceEquals(_current, pipe)) _current = null;
            }
        }
    }

    private void HandleConnection(NamedPipeServerStream pipe)
    {
        // One JSON line in, one JSON line out. Stream is half-duplex from the client's
        // perspective: it writes its message, reads our reply, closes.
        string? line;
        using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true))
        {
            line = reader.ReadLine();
        }
        if (string.IsNullOrWhiteSpace(line))
        {
            Log.Pipe("rejected: empty-message");
            WriteReply(pipe, new ReplyJson { Ok = false, Error = "empty-message" });
            return;
        }

        AlertMessage? msg;
        try { msg = JsonSerializer.Deserialize<AlertMessage>(line!, JsonOpts); }
        catch (JsonException ex)
        {
            Log.Pipe($"rejected: bad-json: {ex.Message}");
            WriteReply(pipe, new ReplyJson { Ok = false, Error = $"bad-json: {ex.Message}" });
            return;
        }
        if (msg == null || string.IsNullOrEmpty(msg.SessionId))
        {
            Log.Pipe("rejected: missing-fields");
            WriteReply(pipe, new ReplyJson { Ok = false, Error = "missing-fields" });
            return;
        }
        // 2026-05-20: classification moved from a pre-mapped `state` field to raw `hookEvent`.
        // DN now decides which color each Claude hook event maps to. Falls back to the
        // legacy `state` field if `hookEvent` is missing, so older callers / synthetic
        // tests still work during the transition.
        if (!TryClassify(msg, out var stateKind))
        {
            Log.Pipe($"rejected: unclassifiable hookEvent={msg.HookEvent ?? "<null>"} state={msg.State ?? "<null>"}");
            WriteReply(pipe, new ReplyJson { Ok = false, Error = "unclassifiable" });
            return;
        }
        string extras = "";
        if (!string.IsNullOrEmpty(msg.NotificationKind)) extras += $" notif={msg.NotificationKind}";
        if (!string.IsNullOrEmpty(msg.ErrorType))        extras += $" err={msg.ErrorType}";
        Log.Pipe($"IN  session={msg.SessionId} hook={msg.HookEvent ?? "-"} -> {stateKind} cwd={msg.Cwd} vscodePid={msg.VsCodePid} parentPid={msg.ParentPid} walk={msg.WalkOutcome ?? "-"}{extras}");

        // Resolution + state mutation must run on the UI thread.
        ReplyJson reply = new();
        ManualResetEventSlim done = new(false);
        try
        {
            _host.BeginInvoke(new Action(() =>
            {
                try
                {
                    _host.RecordEvent(msg.SessionId ?? "", EventLabel(msg), msg.HookEvent ?? msg.State ?? "?", msg.ToolName ?? "");
                    var (target, userHint, viaSession) = ResolveDesktop(msg);
                    if (target == Guid.Empty && stateKind != StateKind.None)
                    {
                        // Park the session on the unresolved sentinel desktop so the "?"
                        // button in the overlay surfaces it. Reply is still ok:false so
                        // ClaudeHook gets the actionable userMessage for its log.
                        // Same livenessPid rule as the resolved path: reject pid<=4 (System,
                        // Idle, Git Bash MSYS PPID=1) so we don't reap within 5s.
                        int livenessPidUnres = msg.ParentPid > 4 ? msg.ParentPid : 0;
                        _state.Apply(
                            SessionState.UnresolvedDesktopId,
                            msg.Source ?? "unknown",
                            msg.SessionId!,
                            stateKind,
                            msg.Title ?? "",
                            string.IsNullOrEmpty(msg.Cwd) ? (msg.Body ?? "") : msg.Cwd!,
                            livenessPidUnres,
                            msg.VsCodePid,
                            msg.HookEvent,
                            msg.ToolName,
                            msg.Cwd ?? "");
                        reply.Ok = false;
                        reply.Error = "vscode-window-not-found";
                        reply.UserMessage = userHint;
                        Log.State($"UNRESOLVED session={msg.SessionId} state={stateKind} cwd={msg.Cwd}");
                    }
                    else
                    {
                        // parentPid is claude.exe (long-lived) — the only useful liveness signal.
                        // sessionPid is ClaudeHook.exe which exits immediately, so it's never
                        // a liveness target. Reject parentPid <= 4 (System / Idle / Git Bash's
                        // MSYS PPID=1 quirk — pid=1 isn't a real process on Windows and was
                        // causing every entry to be reaped within 5s of arriving).
                        int livenessPid = msg.ParentPid > 4 ? msg.ParentPid : 0;
                        // Prefer the most specific human-readable line ClaudeHook can give us so
                        // the hover identifies which Claude this is. These are event-exclusive:
                        //   PreToolUse → toolDescription ("Edit Program.cs"); pre-truncated + word-
                        //                safe, so display verbatim.
                        //   Stop       → lastMessageTail (Claude's final ~200 chars); flatten
                        //                newlines so it stays on the session's single hover line.
                        // Otherwise fall back to ClaudeHook's body ("Running {tool}", "Ready", …).
                        string body = msg.Body ?? "";
                        if (!string.IsNullOrEmpty(msg.ToolDescription))
                            body = msg.ToolDescription!;
                        if (!string.IsNullOrEmpty(msg.LastMessageTail))
                            body = msg.LastMessageTail!.Replace("\r", " ").Replace("\n", " ").Trim();
                        _state.Apply(
                            target,
                            msg.Source ?? "unknown",
                            msg.SessionId!,
                            stateKind,
                            msg.Title ?? "",
                            body,
                            livenessPid,
                            msg.VsCodePid,
                            msg.HookEvent,
                            msg.ToolName,
                            msg.Cwd ?? "");

                        // Learn from this resolution so siblings + future hooks resolve faster.
                        // Idle should drop sticky bindings; everything else cements them.
                        if (stateKind == StateKind.None)
                            _state.ClearSticky(msg.SessionId ?? "", msg.TranscriptPath);
                        else if (target != Guid.Empty)
                            // When resolved via the session pin, the cwd may have wandered into
                            // another repo — don't feed that stray cwd into the learned-path
                            // index (pass null) or it would mis-bind other sessions. The sticky
                            // session/transcript TTL is still refreshed so the pin stays alive.
                            _state.RecordResolution(msg.SessionId ?? "", msg.TranscriptPath, viaSession ? null : msg.Cwd, target);

                        var info = _desktop.GetDesktops().FirstOrDefault(d => d.Id == target);
                        reply.Ok = true;
                        reply.DesktopIndex = info?.Index ?? -1;
                        reply.DesktopName = info?.Name ?? "";
                        Log.State($"applied session={msg.SessionId} state={stateKind} livenessPid={livenessPid} desktop={target} idx={reply.DesktopIndex} name='{reply.DesktopName}'");
                    }
                }
                catch (Exception ex)
                {
                    reply.Ok = false;
                    reply.Error = "server-error: " + ex.Message;
                }
                finally { done.Set(); }
            }));
            done.Wait(TimeSpan.FromMilliseconds(800));
        }
        catch (InvalidOperationException)
        {
            // BeginInvoke failed (form closing). Tell the client.
            reply.Ok = false;
            reply.Error = "server-shutting-down";
            done.Set();
        }

        WriteReply(pipe, reply);
    }

    /// <summary>Short, recognizable label for the tray event feed: cwd basename, else title,
    /// else a truncated session id.</summary>
    private static string EventLabel(AlertMessage msg)
    {
        if (!string.IsNullOrEmpty(msg.Cwd))
        {
            string p = msg.Cwd!.Replace('/', '\\').TrimEnd('\\', ' ');
            int slash = p.LastIndexOf('\\');
            if (slash >= 0 && slash < p.Length - 1) return p[(slash + 1)..];
            if (p.Length > 0) return p;
        }
        if (!string.IsNullOrEmpty(msg.Title)) return msg.Title!;
        var id = msg.SessionId ?? "";
        return id.Length > 8 ? id[..8] : id;
    }

    private static void WriteReply(NamedPipeServerStream pipe, ReplyJson reply)
    {
        try
        {
            var json = JsonSerializer.Serialize(reply, JsonOpts);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(json);
        }
        catch { /* client gone — nothing to do */ }
    }

    private static bool TryParseState(string s, out StateKind state)
    {
        switch (s.Trim().ToLowerInvariant())
        {
            case "busy":   state = StateKind.Busy;   return true;
            case "asking": state = StateKind.Asking; return true;
            case "ready":  state = StateKind.Ready;  return true;
            case "error":  state = StateKind.Error;  return true;
            case "idle":   state = StateKind.None;   return true;
        }
        state = StateKind.None;
        return false;
    }

    /// <summary>
    /// Tools whose entire purpose is to block on user input. PreToolUse for these is
    /// rendered as Asking (yellow) instead of Busy (orange). ClaudeHook leaves the events
    /// untagged so DN owns the list; add new entries here when Anthropic adds new blocking
    /// tools. See DESKTOPNAMES-INTEGRATION.md.
    /// </summary>
    private static readonly HashSet<string> BlockingTools = new(StringComparer.Ordinal)
    {
        "AskUserQuestion",
        "ExitPlanMode",
    };

    /// <summary>
    /// Maps a Claude hook event to a StateKind. Source of truth for the color semantics
    /// the user wants: yellow > orange > red > green (Asking > Busy > Error > Ready).
    /// Falls back to the legacy <c>state</c> field if <c>hookEvent</c> isn't supplied.
    /// </summary>
    private static bool TryClassify(AlertMessage msg, out StateKind state)
    {
        string? ev = msg.HookEvent;
        if (!string.IsNullOrEmpty(ev))
        {
            switch (ev)
            {
                case "SessionStart":
                case "UserPromptSubmit":
                    state = StateKind.Busy; return true;
                case "PreToolUse":
                    // Tools whose semantics are "block on user" map to Asking, not Busy.
                    if (!string.IsNullOrEmpty(msg.ToolName) && BlockingTools.Contains(msg.ToolName!))
                    { state = StateKind.Asking; return true; }
                    state = StateKind.Busy; return true;
                case "PermissionRequest":
                    // Inline y/n permission dialog — primary signal for "Claude is blocked
                    // on user". Stays yellow until the user answers.
                    state = StateKind.Asking; return true;
                case "Notification":
                    // permission_prompt / idle_prompt → asking. auth_success is suppressed
                    // by ClaudeHook before send so we shouldn't see it; treat as no-op if it
                    // ever arrives. Unknown subtypes default to asking (be loud, not silent).
                    if (string.Equals(msg.NotificationKind, "auth_success", StringComparison.Ordinal))
                    { state = StateKind.None; return true; }
                    state = StateKind.Asking; return true;
                case "Stop":
                    // Stop-with-question detection: ClaudeHook peeks the transcript and
                    // sets lastMessageEndsWithQuestion=true when Claude's final assistant
                    // text ends with "?". Treat that as Asking (yellow) even though
                    // technically the turn finished. null/false → ordinary Ready (green).
                    if (msg.LastMessageEndsWithQuestion == true)
                    { state = StateKind.Asking; return true; }
                    state = StateKind.Ready; return true;
                case "StopFailure":
                    state = StateKind.Error; return true;
                case "SessionEnd":
                    state = StateKind.None; return true;
                default:
                    state = StateKind.None; return false; // unknown hookEvent — refuse
            }
        }
        // Legacy callers (synthetic tests, older builds) that still send `state`.
        if (!string.IsNullOrEmpty(msg.State) && TryParseState(msg.State!, out state)) return true;
        state = StateKind.None;
        return false;
    }

    /// <summary>
    /// Resolution order (from authoritative-now to learned-history):
    /// 1. vscodePid — exact window on the current process tree.
    /// 2. cwd walk-up against tracked workspace rootNames.
    /// 3. learned-path index from prior successful resolutions (sibling cwds, etc.).
    /// 4. sticky session/transcript cache (works after the originating window closed).
    /// Returns Guid.Empty + a human-actionable hint when nothing matches.
    /// </summary>
    private (Guid desktopId, string? userMessage, bool viaSession) ResolveDesktop(AlertMessage msg)
    {
        if (msg.VsCodePid > 0)
        {
            var g = ResolveByVsCodePid(msg.VsCodePid);
            if (g != Guid.Empty) { Log.Resolver($"  via=vscodePid({msg.VsCodePid}) -> {g}"); return (g, null, false); }
        }

        // Pin-to-window: once a session has resolved to a desktop, keep it there even if its
        // cwd later wanders into another repo (e.g. a skill cd's into a different folder).
        // Checked BEFORE cwd-walkup so the feed doesn't migrate desktops mid-session. The
        // binding is established on the first hook via the cwd path below. viaSession=true so
        // the caller doesn't re-record the (now-wandered) cwd into the learned-path index.
        if (!string.IsNullOrEmpty(msg.SessionId) || !string.IsNullOrEmpty(msg.TranscriptPath))
        {
            if (_state.TryStickyLookup(msg.SessionId ?? "", msg.TranscriptPath, out var sd))
            { Log.Resolver($"  via=sticky-pin(session={msg.SessionId}) -> {sd}"); return (sd, null, true); }
        }

        if (!string.IsNullOrEmpty(msg.Cwd))
        {
            var g = ResolveByCwdWalkUp(msg.Cwd!);
            if (g != Guid.Empty) { Log.Resolver($"  via=cwd-walkup({msg.Cwd}) -> {g}"); return (g, null, false); }

            // Workspace-folder lookup: scan %APPDATA%\Code\User\workspaceStorage on demand.
            // Closes the .code-workspace gap where rootName ("ev.exe") differs from any
            // folder basename in cwd ("EvolxCli"). A single folder may appear under
            // multiple rootNames (FOLDER mode + member of a .code-workspace) — we iterate
            // candidates and pick the first one VsCodeTracker has currently bound.
            var candidates = _wsIndex.FindRootNameCandidates(msg.Cwd!);
            if (candidates.Count > 0)
            {
                foreach (var rootName in candidates)
                {
                    if (_settings.VsCodeWorkspaceDesktops.TryGetValue(rootName, out var loc) &&
                        loc.DesktopId != Guid.Empty)
                    {
                        Log.Resolver($"  via=workspace-index({msg.Cwd}) -> rootName={rootName} -> {loc.DesktopId}");
                        return (loc.DesktopId, null, false);
                    }
                }
                Log.Resolver($"  workspace-index found candidates [{string.Join(",", candidates)}] but none in VsCodeWorkspaceDesktops");
            }

            if (_state.TryLookupLearnedPath(msg.Cwd!, out var lp))
            { Log.Resolver($"  via=learned-path({msg.Cwd}) -> {lp}"); return (lp, null, false); }
        }

        // Nothing resolved. Build a hint the user can act on.
        string cwdPart = !string.IsNullOrEmpty(msg.Cwd) ? msg.Cwd! : "this Claude session";
        string hint = $"No open VSCode window matches {cwdPart}. Open the folder in VSCode to enable desktop indicators for this session.";
        Log.Resolver($"  via=NONE cwd={msg.Cwd} vscodePid={msg.VsCodePid} session={msg.SessionId} transcript={msg.TranscriptPath}");
        return (Guid.Empty, hint, false);
    }

    /// <summary>
    /// Resolve a desktop from a reported VS Code PID — but only when it's unambiguous.
    /// VS Code runs all its windows under a single Electron *main* process, and on Windows
    /// the top-level window HWND is owned by that main process (not the per-window renderer).
    /// So a PID that hosts windows on several desktops cannot tell us which window *this*
    /// session belongs to. The old "pick the longest-title window" heuristic returned an
    /// arbitrary (and time-varying) window, which dragged unrelated sessions onto whichever
    /// desktop happened to win — see the 2026-05-27 analysis. We now collect the desktops of
    /// every genuine VS Code workspace window owned by the PID and only trust the result when
    /// they're unanimous; otherwise we decline so cwd-walkup / workspace-index can resolve.
    /// </summary>
    /// <summary>
    /// rootName candidates for a cwd, used by the overlay's reassign command to locate the
    /// session's VS Code window: every ancestor folder basename (covers plain-folder
    /// workspaces where rootName == folder name) plus any .code-workspace rootNames the
    /// workspace index maps for this cwd.
    /// </summary>
    public IReadOnlyCollection<string> RootNameCandidatesFor(string? cwd)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(cwd)) return set;

        string current = cwd!.Replace('/', '\\').TrimEnd('\\', ' ');
        while (current.Length > 0)
        {
            int slash = current.LastIndexOf('\\');
            string name = slash >= 0 ? current[(slash + 1)..] : current;
            if (name.Length > 0) set.Add(name);
            if (slash <= 2) break;
            current = current[..slash];
        }
        foreach (var n in _wsIndex.FindRootNameCandidates(cwd!)) set.Add(n);
        return set;
    }

    private Guid ResolveByVsCodePid(int pid)
    {
        var desktops = new HashSet<Guid>();
        int windowCount = 0;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint procId);
            if ((int)procId != pid) return true;

            var clsBuf = new char[64];
            int clsLen = NativeMethods.GetClassName(hwnd, clsBuf, clsBuf.Length);
            if (new string(clsBuf, 0, clsLen) != "Chrome_WidgetWin_1") return true;

            // Only genuine workspace windows (title resolves to a rootName) — skips DevTools,
            // tooltips and other Chromium helper windows owned by the same main process.
            if (VsCodeTracker.ExtractWorkspace(GetWindowTitle(hwnd)) == null) return true;

            var d = _desktop.GetDesktopForWindow(hwnd);
            if (d != Guid.Empty) { desktops.Add(d); windowCount++; }
            return true;
        }, IntPtr.Zero);

        if (desktops.Count == 1) return desktops.First();
        if (desktops.Count > 1)
            Log.Resolver($"  vscodePid({pid}) ambiguous: {windowCount} VS Code windows across {desktops.Count} desktops -- declining, deferring to cwd");
        return Guid.Empty;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int len = NativeMethods.GetWindowTextLength(hwnd);
        if (len == 0) return "";
        var sb = new System.Text.StringBuilder(len + 1);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// Walk cwd upward dir-by-dir, trying each basename against VsCodeTracker's rootName
    /// map. Solves "claude was launched from a subfolder of an open workspace whose folder
    /// basename matches its rootName". Does NOT solve the .code-workspace case where the
    /// rootName differs from the folder name — that still needs the learned-paths fallback
    /// or vscodePid.
    /// </summary>
    private Guid ResolveByCwdWalkUp(string cwd)
    {
        string current = cwd.Replace('/', '\\').TrimEnd('\\', ' ');
        while (current.Length > 0)
        {
            int slash = current.LastIndexOf('\\');
            string name = slash >= 0 ? current[(slash + 1)..] : current;
            if (name.Length > 0 &&
                _settings.VsCodeWorkspaceDesktops.TryGetValue(name, out var loc) &&
                loc.DesktopId != Guid.Empty)
            {
                return loc.DesktopId;
            }
            if (slash <= 2) break; // hit drive root ("c:" or "c:\")
            current = current[..slash];
        }
        return Guid.Empty;
    }

    private sealed class AlertMessage
    {
        // core
        public string? Type { get; set; }
        public string? Source { get; set; }
        public string? State { get; set; }
        public string? Title { get; set; }
        public string? Body { get; set; }

        // identity
        public string? SessionId { get; set; }
        public string? Cwd { get; set; }
        public string? TranscriptPath { get; set; }
        public string? Hostname { get; set; }

        // hook context
        public string? HookEvent { get; set; }
        public string? HookSource { get; set; }
        public string? ToolName { get; set; }
        // PreToolUse only (nullable): a more specific, pre-truncated human-readable line than
        // toolName alone — e.g. "Edit Program.cs" instead of "Edit". Null on all other events.
        public string? ToolDescription { get; set; }
        public string? Message { get; set; }

        // Sent by ClaudeHook (2026-05-20+):
        // - ErrorType: kind of StopFailure (e.g. "rate_limit", "authentication_failed").
        // - NotificationKind: which Notification matcher fired ("permission_prompt",
        //   "idle_prompt", "auth_success"). auth_success is suppressed by ClaudeHook
        //   before send — we won't see it. See ../Claude-Alert/DESKTOPNAMES-INTEGRATION.md.
        public string? ErrorType { get; set; }
        public string? NotificationKind { get; set; }

        // Sent by ClaudeHook on Stop events. ClaudeHook peeks the transcript JSONL to
        // detect whether Claude finished its turn with a question for the user.
        //   true  → treat as Asking (yellow), not Ready (green).
        //   false → treat as Ready.
        //   null  → peek failed / schema break — treat as Ready (fail-closed).
        public bool? LastMessageEndsWithQuestion { get; set; }
        public string? LastMessageTail { get; set; }

        // process tree
        public int VsCodePid { get; set; }
        public int SessionPid { get; set; }
        public int ParentPid { get; set; }
        public List<ParentChainEntry>? ParentChain { get; set; }
        public string? WalkOutcome { get; set; }
    }

    private sealed class ParentChainEntry
    {
        public int Pid { get; set; }
        public string? Name { get; set; }
    }

    private sealed class ReplyJson
    {
        public bool Ok { get; set; }
        public int? DesktopIndex { get; set; }
        public string? DesktopName { get; set; }
        public string? Error { get; set; }
        public string? UserMessage { get; set; }
    }
}
