using System.Runtime.InteropServices;
using System.Text.Json;

namespace DesktopNames;

/// <summary>
/// Resolves a Claude sessionId to the absolute workspace folders of the VS Code window that
/// launched it, by joining two registries Claude Code maintains under %USERPROFILE%\.claude:
///
/// <list type="number">
///   <item><c>sessions\&lt;pid&gt;.json</c> — one file per live CLI session, deleted on exit:
///     <c>{"pid":52628,"sessionId":"31d8…","cwd":"…","entrypoint":"claude-vscode"}</c>.</item>
///   <item><c>ide\&lt;port&gt;.lock</c> — one per VS Code window with the extension active:
///     <c>{"pid":47096,"workspaceFolders":["c:\\git\\tools\\DesktopNames"],…}</c>.</item>
/// </list>
///
/// The join is the extension's websocket port. A session's <c>claude.exe</c> is a child of its
/// window's extension host, and that extension host is the process *listening* on the port
/// named by the lock file. So: sessionId → pid → parent (extension host) → listening port →
/// lock → workspace folders. Exact, and immune to cwd wandering and title parsing.
///
/// Two details make this reliable. The lock's own <c>pid</c> field is useless — it names the
/// shared Electron main process, identical for every window. And <c>CLAUDE_CODE_SSE_PORT</c>
/// is no help either: the extension injects it into integrated *terminals* only, so a session
/// started in the Claude panel never sees it. Matching on who holds the listening socket
/// sidesteps both, and drops stale locks from closed windows for free.
///
/// What this cannot do is name a window. Every VS Code top-level HWND is owned by the shared
/// main process, so the caller still has to go workspace-folder → rootName → title, and two
/// windows open on the same folder remain indistinguishable.
/// </summary>
internal sealed class ClaudeSessionRegistry
{
    // A session's window never changes, so cache per sessionId. Negative results are cached
    // too — a terminal-launched or folderless session will never gain a workspace.
    private readonly Dictionary<string, string[]> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Absolute workspace folder paths of the VS Code window hosting <paramref name="sessionId"/>,
    /// or empty when the session is dead, terminal-launched, or its window has no folder open.
    /// </summary>
    public IReadOnlyList<string> WorkspaceFoldersFor(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return Array.Empty<string>();
        if (_cache.TryGetValue(sessionId!, out var cached)) return cached;

        var folders = Lookup(sessionId!);
        _cache[sessionId!] = folders;
        return folders;
    }

    private static string[] Lookup(string sessionId)
    {
        int pid = FindPid(sessionId);
        if (pid <= 0) { Log.Resolver($"  ide-lock: no live sessions\\*.json for session={sessionId}"); return Array.Empty<string>(); }

        int hostPid = GetParentPid(pid);
        if (hostPid <= 0) { Log.Resolver($"  ide-lock: no parent for pid={pid}"); return Array.Empty<string>(); }

        var ports = ListeningPorts(hostPid);
        foreach (var port in ports)
        {
            var folders = ReadLockFolders(port);
            if (folders.Length == 0) continue;
            Log.Resolver($"  ide-lock: session={sessionId} pid={pid} host={hostPid} port={port} folders=[{string.Join(",", folders)}]");
            return folders;
        }

        Log.Resolver($"  ide-lock: host={hostPid} listens on [{string.Join(",", ports)}] but no lock has folders");
        return Array.Empty<string>();
    }

    /// <summary>A live session's registry file as Claude Code writes it: <see cref="Status"/> is its
    /// own <c>idle</c> / <c>busy</c>, set at <see cref="StatusSinceUtc"/>.</summary>
    public sealed record Registered(string SessionId, int Pid, string Name, string Status, DateTime StatusSinceUtc);

    public static List<Registered> AllRegistered()
    {
        var dir = Path.Combine(ClaudeDir, "sessions");
        var result = new List<Registered>();
        if (!Directory.Exists(dir)) return result;
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(file)); }
            catch (Exception ex) when (ex is IOException or JsonException) { continue; } // half-written mid-launch
            using (doc)
            {
                var r = doc.RootElement;
                if (!r.TryGetProperty("sessionId", out var sid) || !r.TryGetProperty("pid", out var pid)) continue;
                result.Add(new Registered(sid.GetString() ?? "", pid.GetInt32(),
                    r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    r.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "",
                    r.TryGetProperty("statusUpdatedAt", out var at) ? DateTimeOffset.FromUnixTimeMilliseconds(at.GetInt64()).UtcDateTime : DateTime.MinValue));
            }
        }
        return result;
    }

    /// <summary>The one live session called <paramref name="nameOrId"/> (its name, or a prefix of its id);
    /// none or several is an error that lists them.</summary>
    public static Registered FindRegistered(string nameOrId)
    {
        var all = AllRegistered().Where(s => IsRunning(s.Pid)).ToList();
        var hits = all.Where(s => s.Name == nameOrId || (nameOrId.Length >= 4 && s.SessionId.StartsWith(nameOrId, StringComparison.OrdinalIgnoreCase))).ToList();
        return hits.Count == 1 ? hits[0]
            : throw new ArgumentException(hits.Count == 0
                ? $"no running Claude session is called \"{nameOrId}\"; running: {string.Join(", ", all.Select(s => s.Name))}"
                : $"\"{nameOrId}\" matches {hits.Count} sessions: {string.Join(", ", hits.Select(s => $"{s.Name} ({s.SessionId[..8]})"))}");
    }

    public static bool IsRunning(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; } // no such process
    }

    /// <summary>
    /// Ctrl+Break in the console of <paramref name="pid"/>. Measured on claude.exe in the VS Code
    /// extension's stream-json mode: Ctrl+C only interrupts (twice did nothing to an idle session);
    /// Ctrl+Break makes it run its SessionEnd hook, delete its sessions\*.json and exit 0.
    /// The event reaches every process on that console, the sender included: attached itself,
    /// DesktopNames was ended by it, handler or not. So <c>dn --ctrl-break</c>, a throwaway
    /// process next to this exe, attaches and sends it.
    /// </summary>
    public static void SendCtrlBreak(int pid)
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "dn.exe");
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(helper, $"--ctrl-break {pid}")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        }) ?? throw new InvalidOperationException($"could not start {helper}");
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        // 0: sent. 0xC000013A: sent, and the helper's own copy of the Ctrl+Break ended it.
        if (p.ExitCode is not (0 or unchecked((int)0xC000013A)))
            throw new InvalidOperationException($"Ctrl+Break to pid {pid} failed (dn --ctrl-break exit {p.ExitCode}) {error.Trim()}");
    }

    /// <summary>The transcript <c>projects\*\&lt;sessionId&gt;.jsonl</c> of a session known by its id
    /// prefix, or null when none or several match. For sessions restored from disk, whose hooks
    /// (which carry the path) haven't fired since.</summary>
    public static string? FindTranscript(string sessionIdPrefix)
    {
        var dir = Path.Combine(ClaudeDir, "projects");
        if (!Directory.Exists(dir)) return null;
        var hits = Directory.GetFiles(dir, sessionIdPrefix + "*.jsonl", SearchOption.AllDirectories)
            .Where(f => Path.GetDirectoryName(Path.GetDirectoryName(f)) == dir).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr handle, int infoClass, IntPtr info, int size, out int returned);

    /// <summary>A process's command line (ProcessCommandLineInformation), or null when it can't be read.</summary>
    private static string? CommandLine(int pid)
    {
        const int ProcessQueryLimitedInformation = 0x1000, ProcessCommandLineInformation = 60;
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        IntPtr buffer = Marshal.AllocHGlobal(32 * 1024);
        try
        {
            if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, 32 * 1024, out _) != 0) return null;
            // UNICODE_STRING { ushort Length; ushort MaximumLength; IntPtr Buffer }, its text in the same buffer.
            int bytes = Marshal.ReadInt16(buffer) & 0xFFFF;
            IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            return Marshal.PtrToStringUni(text, bytes / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            CloseHandle(handle);
        }
    }

    /// <summary>Where a background shell's process stands. <see cref="Pid"/> is set when exactly one matched.</summary>
    public sealed record ShellProcess(int? Pid, int Matches);

    // task id → the bash process found for it, with its start time so a reused pid isn't taken for it.
    private static readonly Dictionary<string, (int Pid, DateTime StartUtc)> _shells = new(StringComparer.Ordinal);

    /// <summary>
    /// The bash process running a background task: a child of the session's Claude process whose
    /// command line holds <c>eval '&lt;command&gt;'</c> — the command as the Stop lists it, with ' written
    /// as '\'' for bash and " as \" for the Windows command line. Matches = 0 means no such process
    /// any more: the shell has ended.
    /// </summary>
    public static ShellProcess FindShell(int claudePid, string taskId, string command)
    {
        if (_shells.TryGetValue(taskId, out var known))
        {
            if (StillRunning(known.Pid, known.StartUtc)) return new ShellProcess(known.Pid, 1);
            _shells.Remove(taskId);
            return new ShellProcess(null, 0);
        }
        string needle = "eval '" + command.Replace("'", "'\\''").Replace("\"", "\\\"") + "'";
        var hits = new List<(int, DateTime)>();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("bash"))
            using (p)
            {
                if (GetParentPid(p.Id) != claudePid || CommandLine(p.Id) is not { } line || !line.Contains(needle, StringComparison.Ordinal)) continue;
                hits.Add((p.Id, p.StartTime.ToUniversalTime()));
            }
        if (hits.Count == 1)
        {
            _shells[taskId] = hits[0];
            WatchExit(hits[0].Item1);
        }
        return new ShellProcess(hits.Count == 1 ? hits[0].Item1 : null, hits.Count);
    }

    /// <summary>A shell found by <see cref="FindShell"/> has exited (raised on the thread that found it).</summary>
    public static event Action? ShellExited;

    // Held so their Exited events keep firing.
    private static readonly Dictionary<int, System.Diagnostics.Process> _watched = new();

    /// <summary>Raise <see cref="ShellExited"/> when the process ends, on the calling (UI) thread.</summary>
    private static void WatchExit(int pid)
    {
        if (_watched.ContainsKey(pid)) return;
        var context = SynchronizationContext.Current ?? throw new InvalidOperationException("FindShell must run on the UI thread");
        var process = System.Diagnostics.Process.GetProcessById(pid);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => context.Post(_ =>
        {
            _watched.Remove(pid);
            process.Dispose();
            ShellExited?.Invoke();
        }, null);
        _watched[pid] = process;
    }

    /// <summary>The process <paramref name="pid"/> is still the one that started at <paramref name="startUtc"/>.</summary>
    private static bool StillRunning(int pid, DateTime startUtc)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited && p.StartTime.ToUniversalTime() == startUtc;
        }
        catch (ArgumentException) { return false; } // no process with that pid any more
    }

    // task id → its output file, once found (a task's file never moves).
    private static readonly Dictionary<string, string> _taskOutputs = new(StringComparer.Ordinal);

    /// <summary>
    /// When a background task last wrote output: Claude Code streams it to
    /// <c>%TEMP%\claude\&lt;project&gt;\&lt;sessionId&gt;\tasks\&lt;taskId&gt;.output</c>. Null when that file is
    /// not there (the task's session ended and Claude Code cleaned up).
    /// </summary>
    public static DateTime? TaskLastOutputUtc(string fullSessionId, string taskId)
    {
        if (!_taskOutputs.TryGetValue(taskId, out var file))
        {
            var root = Path.Combine(Path.GetTempPath(), "claude");
            if (!Directory.Exists(root)) return null;
            var found = Directory.GetDirectories(root)
                .Select(project => Path.Combine(project, fullSessionId, "tasks", taskId + ".output"))
                .FirstOrDefault(File.Exists);
            if (found == null) return null;
            _taskOutputs[taskId] = file = found;
        }
        return File.Exists(file) ? File.GetLastWriteTimeUtc(file) : null;
    }

    /// <summary>The token usage of a session's latest API call. <see cref="Context"/> is what it sent
    /// (the context window in use); <see cref="CacheRead"/> the part of it served from the prompt cache.</summary>
    public sealed record Usage(string Model, long Input, long CacheRead, long CacheWrite, long Output)
    {
        public long Context => Input + CacheRead + CacheWrite;
    }

    /// <summary>The last prompt a session got: from the user, or (<see cref="FromPeer"/>) a message
    /// another session sent it, then <see cref="From"/> is that session's name. <see cref="Offset"/>
    /// is where its line starts in the transcript, to read it again in full (<see cref="PromptDetail"/>).</summary>
    public sealed record Prompt(bool FromPeer, string From, string Text, DateTime AtUtc, long Offset);

    /// <summary>One thing Claude did: a tool call (<see cref="Tool"/> its name) or, with Tool empty, a reply.</summary>
    public sealed record ClaudeAction(string Tool, string Text, DateTime AtUtc);

    public sealed record TranscriptFacts(string? Title, DateTime LastWriteUtc, Usage? Usage, Prompt? Prompt, IReadOnlyList<ClaudeAction> Actions);

    private const int ActionsKept = 15;

    private sealed class Scanned
    {
        public long Length;
        public string? Title;
        public Usage? Usage;
        public Prompt? Prompt;
        public List<ClaudeAction> Actions = new();
    }

    // path → what its scan found so far. Transcripts only grow, so each read scans what was appended —
    // up to its last complete line, so no record is read half-written.
    private static readonly Dictionary<string, Scanned> _transcripts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex AiTitle =
        new("\"type\":\"ai-title\",\"aiTitle\":(\"(?:[^\"\\\\]|\\\\.)*\")", System.Text.RegularExpressions.RegexOptions.Compiled);
    // An assistant record: "model":"claude-…" … "usage":{"input_tokens":2,"cache_creation_input_tokens":…,"cache_read_input_tokens":…,"output_tokens":…
    private static readonly System.Text.RegularExpressions.Regex UsageRecord = new(
        "\"model\":\"(claude[^\"]*)\"[^\\n]*?\"usage\":\\{\"input_tokens\":(\\d+),\"cache_creation_input_tokens\":(\\d+),\"cache_read_input_tokens\":(\\d+),\"output_tokens\":(\\d+)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Fold a run of complete transcript lines, starting at byte <paramref name="offset"/>, into <paramref name="into"/>.</summary>
    private static void Scan(string text, long offset, Scanned into)
    {
        var titles = AiTitle.Matches(text);
        if (titles.Count > 0) into.Title = JsonSerializer.Deserialize<string>(titles[^1].Groups[1].Value);
        var usages = UsageRecord.Matches(text);
        if (usages.Count > 0)
        {
            var g = usages[^1].Groups;
            into.Usage = new Usage(g[1].Value, long.Parse(g[2].Value), long.Parse(g[4].Value), long.Parse(g[3].Value), long.Parse(g[5].Value));
        }
        foreach (var line in text.Split('\n'))
        {
            // A user record with an origin, or a queued_command: a message (the user's, or a peer
            // session's) that arrived while the session was busy and was taken in mid-turn.
            if (line.Contains("\"origin\":{\"kind\":\"", StringComparison.Ordinal)
                && (line.Contains("\"type\":\"user\"", StringComparison.Ordinal) || line.Contains("\"type\":\"queued_command\"", StringComparison.Ordinal))
                && PromptOf(line, offset) is { } p)
                into.Prompt = p;
            else if (line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal) && !line.Contains("\"isSidechain\":true", StringComparison.Ordinal))
                into.Actions.AddRange(ActionsOf(line));
            offset += System.Text.Encoding.UTF8.GetByteCount(line) + 1;
        }
        if (into.Actions.Count > ActionsKept) into.Actions.RemoveRange(0, into.Actions.Count - ActionsKept);
    }

    /// <summary>The tool calls and replies in one assistant record.</summary>
    private static IEnumerable<ClaudeAction> ActionsOf(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var r = doc.RootElement;
        if (!r.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return Array.Empty<ClaudeAction>();
        DateTime at = r.TryGetProperty("timestamp", out var ts) ? ts.GetDateTime().ToUniversalTime() : DateTime.MinValue;
        var actions = new List<ClaudeAction>();
        foreach (var block in content.EnumerateArray())
        {
            string type = block.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            if (type == "tool_use")
            {
                string tool = block.GetProperty("name").GetString() ?? "";
                actions.Add(new ClaudeAction(tool, ToolSummary(tool, block.TryGetProperty("input", out var input) ? input : default), at));
            }
            else if (type == "text" && block.GetProperty("text").GetString() is { Length: > 0 } reply)
                actions.Add(new ClaudeAction("", reply, at));
        }
        return actions;
    }

    /// <summary>What a tool call was about, from its input: a Bash call's own description, the file read or edited, the pattern searched.</summary>
    private static string ToolSummary(string tool, JsonElement input)
    {
        string? Get(string name) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return tool switch
        {
            "Bash" or "PowerShell" => Get("description") ?? Get("command") ?? tool,
            "Read" or "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => $"{tool} {Path.GetFileName(Get("file_path") ?? Get("notebook_path") ?? "")}",
            "Grep" or "Glob" => $"{tool} {Get("pattern")}",
            "WebFetch" => $"Fetch {Get("url")}",
            "WebSearch" => $"Search {Get("query")}",
            "Agent" or "Task" => $"Agent: {Get("description")}",
            "SendMessage" => $"Message {Get("to")}: {Get("summary")}",
            "Skill" => $"Skill {Get("skill")}",
            _ => Get("description") ?? tool,
        };
    }

    /// <summary>A user record with an origin: the human typing, or a peer session's message.</summary>
    private static Prompt? PromptOf(string line, long offset)
    {
        using var doc = JsonDocument.Parse(line);
        var r = doc.RootElement;
        bool queued = r.TryGetProperty("attachment", out var attachment);
        var origin = queued ? attachment.GetProperty("origin") : r.GetProperty("origin");
        DateTime at = r.TryGetProperty("timestamp", out var ts) ? ts.GetDateTime().ToUniversalTime() : DateTime.MinValue;
        switch (origin.GetProperty("kind").GetString())
        {
            case "peer":
                // The origin carries the text as body, except when the message reached an idle
                // session: then only the content has it, inside <cross-session-message …>…</…>.
                string body = origin.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                if (body.Length == 0)
                {
                    var content = queued ? attachment.GetProperty("prompt") : r.GetProperty("message").GetProperty("content");
                    string raw = content.ValueKind == JsonValueKind.String ? content.GetString() ?? "" : HumanText(content);
                    var inside = PeerMessage.Match(raw);
                    body = inside.Success ? inside.Groups[1].Value.Trim() : Tag.Replace(raw, "").Trim();
                }
                return new Prompt(true, origin.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", body, at, offset);
            case "human":
                // Claude Code's own injected text is marked isMeta. A peer's message taken in
                // mid-turn is too (Infoseite osis got Yaml's that way), so the check is for humans only.
                if (!queued && r.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) return null;
                break;
            default:
                return null; // task-notification: Claude Code telling the session a background task ended
        }
        // A queued prompt is a string, or blocks like a message's content when it carries images.
        string text = HumanText(queued ? attachment.GetProperty("prompt") : r.GetProperty("message").GetProperty("content"));
        return text.Trim().Length == 0 ? null : new Prompt(false, "", text, at, offset);
    }

    /// <summary>What the user wrote: VS Code adds its context (open file, selection) as blocks of
    /// their own, and pasted text comes wrapped in a tag.</summary>
    private static string HumanText(JsonElement content)
    {
        var blocks = content.ValueKind == JsonValueKind.String ? new[] { content.GetString() ?? "" }
            : content.EnumerateArray()
                .Where(c => c.TryGetProperty("type", out var t) && t.GetString() == "text")
                .Select(c => c.GetProperty("text").GetString() ?? "")
                .Where(s => !s.StartsWith("<ide_", StringComparison.Ordinal) && !s.StartsWith("<system-reminder", StringComparison.Ordinal))
                .ToArray();
        return Tag.Replace(string.Join("\n", blocks), "");
    }

    private static readonly System.Text.RegularExpressions.Regex PeerMessage = new(
        "<cross-session-message[^>]*>([\\s\\S]*?)</cross-session-message>", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex Tag = new("</?[A-Za-z_][^>\\n]*>", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>A prompt read again in full: its text, and the images pasted with it (PNG/JPEG bytes).</summary>
    public static (string Text, List<byte[]> Images) PromptDetail(string path, Prompt prompt)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(prompt.Offset, SeekOrigin.Begin);
        string line = new StreamReader(stream, System.Text.Encoding.UTF8).ReadLine()
            ?? throw new InvalidDataException($"{path}: no line at byte {prompt.Offset}");
        using var doc = JsonDocument.Parse(line);
        var r = doc.RootElement;
        if (prompt.FromPeer || r.TryGetProperty("attachment", out _)) return (prompt.Text, new List<byte[]>());
        var content = r.GetProperty("message").GetProperty("content");
        var images = content.ValueKind != JsonValueKind.Array ? new List<byte[]>()
            : content.EnumerateArray()
                .Where(c => c.TryGetProperty("type", out var t) && t.GetString() == "image"
                            && c.GetProperty("source").TryGetProperty("data", out _))
                .Select(c => Convert.FromBase64String(c.GetProperty("source").GetProperty("data").GetString()!))
                .ToList();
        return (HumanText(content), images);
    }

    /// <summary>Bytes [from, end) of a file, cut after its last newline — and, with <paramref name="skipPartial"/>,
    /// before its first, for a read that starts mid-line. Returns the text and the byte offset it starts at.</summary>
    private static (string Text, long Start, long End) CompleteLines(string path, long from, bool skipPartial)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(from, SeekOrigin.Begin);
        var bytes = new byte[stream.Length - from];
        stream.ReadExactly(bytes);
        int start = skipPartial ? Array.IndexOf(bytes, (byte)'\n') + 1 : 0;
        int end = Array.LastIndexOf(bytes, (byte)'\n') + 1;
        if (end < start) end = start;
        return (System.Text.Encoding.UTF8.GetString(bytes, start, end - start), from + start, from + end);
    }

    /// <summary>
    /// What a transcript says about its session: the latest <c>ai-title</c> Claude Code gave it
    /// ("Osis dev privileges review"), the latest call's token usage, the last prompt, Claude's
    /// recent actions, and when it was last written — the session's real last activity, which
    /// survives a DesktopNames restart. Null when the file is gone.
    /// </summary>
    public static TranscriptFacts? Transcript(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) return null;
        if (!_transcripts.TryGetValue(path, out var seen))
        {
            seen = new Scanned();
            // First sight: the tail usually holds a title, a usage and the last prompt, so a
            // multi-megabyte file needn't be read whole (that made the window open in 3 s).
            const long Tail = 512 * 1024;
            if (file.Length > Tail)
            {
                var (text, start, end) = CompleteLines(path, file.Length - Tail, skipPartial: true);
                var tail = new Scanned();
                Scan(text, start, tail);
                if (tail.Title != null && tail.Prompt != null)
                {
                    tail.Length = end;
                    seen = tail;
                }
            }
            _transcripts[path] = seen;
        }
        if (file.Length > seen.Length)
        {
            var (text, start, end) = CompleteLines(path, seen.Length, skipPartial: false);
            Scan(text, start, seen);
            seen.Length = end;
        }
        return new TranscriptFacts(seen.Title, file.LastWriteTimeUtc, seen.Usage, seen.Prompt, seen.Actions.ToList());
    }

    private static string ClaudeDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>
    /// Scan sessions\*.json for the file whose sessionId matches. Callers send a truncated id
    /// (Claude-Alert reports the leading 8 hex chars), so match on prefix — but only accept a
    /// unique hit, so a prefix collision falls through to cwd resolution instead of guessing.
    /// Returns 0 if none.
    /// </summary>
    /// <summary>A live session as its registry file describes it. <see cref="Entrypoint"/> is
    /// <c>cli</c> for a Claude started in a terminal, <c>claude-vscode</c> for the VS Code panel.</summary>
    /// <summary><see cref="NameSource"/> is <c>user</c> when the user renamed the session, <c>derived</c> otherwise.</summary>
    /// <see cref="RemoteControl"/>: it has a bridge session — Remote Control (claude.ai) can see and drive it.
    public sealed record LiveSession(int Pid, string Entrypoint, string Cwd, string Name, string NameSource, bool RemoteControl)
    {
        public bool IsTerminal => Entrypoint == "cli";
    }

    /// <summary>
    /// The live session a SendMessage <c>to</c> addresses — by name ("evolxcli-67") or by its
    /// messaging pipe ("uds:\\.\pipe\LOCAL\cc-msg-…", what a reply to a received message uses) —
    /// as its full sessionId and its name. Null when no session or several match.
    /// </summary>
    public (string SessionId, string Name)? SessionByAddress(string to)
    {
        var dir = Path.Combine(ClaudeDir, "sessions");
        if (!Directory.Exists(dir)) return null;
        var hits = new List<(string, string)>();
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(file)); }
            catch (Exception ex) when (ex is IOException or JsonException) { continue; } // half-written mid-launch
            using (doc)
            {
                var r = doc.RootElement;
                string name = r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string pipe = r.TryGetProperty("messagingSocketPath", out var m) ? "uds:" + m.GetString() : "";
                if ((name == to || pipe == to) && r.TryGetProperty("sessionId", out var sid) && sid.GetString() is { } full)
                    hits.Add((full, name));
            }
        }
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>Where a terminal Claude is shown: a VS Code window's integrated terminal, or a
    /// window of its own (Windows Terminal, a console), found by walking up its parent processes.</summary>
    public sealed record TerminalHost(bool InVsCode, IntPtr Window);

    /// <summary>One registry file: its session's full id and what it says, or null while it is
    /// half-written (Claude Code rewrites it on every status change; the next write is whole).</summary>
    public static (string SessionId, LiveSession Live)? ReadRegistryFile(string file)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(file)); }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
        using (doc)
        {
            var r = doc.RootElement;
            if (!r.TryGetProperty("sessionId", out var sid) || sid.GetString() is not { } full) return null;
            return (full, new LiveSession(
                r.TryGetProperty("pid", out var p) ? p.GetInt32() : 0,
                r.TryGetProperty("entrypoint", out var e) ? e.GetString() ?? "" : "",
                r.TryGetProperty("cwd", out var c) ? c.GetString() ?? "" : "",
                r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                r.TryGetProperty("nameSource", out var ns) ? ns.GetString() ?? "" : "",
                r.TryGetProperty("bridgeSessionId", out var bridge) && bridge.GetString() is { Length: > 0 }));
        }
    }

    public static string SessionsDir => Path.Combine(ClaudeDir, "sessions");

    /// <summary>The live registry entry for <paramref name="sessionId"/>, or null when it has none
    /// (ended, or the prefix is ambiguous). Not cached: sessions end.</summary>
    public LiveSession? Live(string sessionId)
    {
        var dir = Path.Combine(ClaudeDir, "sessions");
        if (!Directory.Exists(dir)) return null;
        LiveSession? found = null;
        int hits = 0;
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(file)); }
            catch (Exception ex) when (ex is IOException or JsonException) { continue; } // half-written mid-launch
            using (doc)
            {
                var r = doc.RootElement;
                if (!r.TryGetProperty("sessionId", out var sid) || sid.GetString() is not { } full
                    || !full.StartsWith(sessionId, StringComparison.OrdinalIgnoreCase)) continue;
                hits++;
                found = new LiveSession(
                    r.TryGetProperty("pid", out var p) ? p.GetInt32() : 0,
                    r.TryGetProperty("entrypoint", out var e) ? e.GetString() ?? "" : "",
                    r.TryGetProperty("cwd", out var c) ? c.GetString() ?? "" : "",
                    r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    r.TryGetProperty("nameSource", out var ns) ? ns.GetString() ?? "" : "",
                    r.TryGetProperty("bridgeSessionId", out var bridge) && bridge.GetString() is { Length: > 0 });
            }
        }
        return hits == 1 ? found : null;
    }

    /// <summary>
    /// Walk up from a terminal Claude's process: an ancestor that is VS Code means its integrated
    /// terminal; the first ancestor owning a visible titled top-level window is its own window.
    /// <paramref name="windowsByPid"/> maps process id to such a window.
    /// </summary>
    public static TerminalHost? FindTerminalHost(int pid, IReadOnlyDictionary<int, IntPtr> windowsByPid)
    {
        for (int depth = 0; pid > 0 && depth < 10; depth++)
        {
            string name;
            try { using var proc = System.Diagnostics.Process.GetProcessById(pid); name = proc.ProcessName; }
            catch (ArgumentException) { return null; } // exited
            if (name.Equals("Code", StringComparison.OrdinalIgnoreCase)) return new TerminalHost(true, IntPtr.Zero);
            if (windowsByPid.TryGetValue(pid, out var hwnd)) return new TerminalHost(false, hwnd);
            pid = GetParentPid(pid);
        }
        return null;
    }

    private static int FindPid(string sessionId)
    {
        var dir = Path.Combine(ClaudeDir, "sessions");
        if (!Directory.Exists(dir)) return 0;

        int found = 0, hits = 0;
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (!doc.RootElement.TryGetProperty("sessionId", out var sid)) continue;
                var full = sid.GetString();
                if (full == null || !full.StartsWith(sessionId, StringComparison.OrdinalIgnoreCase)) continue;
                hits++;
                if (doc.RootElement.TryGetProperty("pid", out var p)) found = p.GetInt32();
            }
            catch { /* half-written file mid-launch; skip */ }
        }

        if (hits > 1) { Log.Resolver($"  ide-lock: session prefix '{sessionId}' matches {hits} sessions -- declining"); return 0; }
        return found;
    }

    private static string[] ReadLockFolders(int port)
    {
        var path = Path.Combine(ClaudeDir, "ide", port + ".lock");
        if (!File.Exists(path)) return Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("workspaceFolders", out var wf) ||
                wf.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
            return wf.EnumerateArray()
                     .Select(e => e.GetString() ?? "")
                     .Where(s => s.Length > 0)
                     .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    // ---- parent pid ----------------------------------------------------------------------
    // PROCESS_BASIC_INFORMATION's last field is InheritedFromUniqueProcessId. Needs only
    // PROCESS_QUERY_LIMITED_INFORMATION and no memory read, so it beats a WMI round-trip.

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public UIntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr handle, int infoClass, ref ProcessBasicInformation info, int size, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>One process of a session's tree; <see cref="StartUtc"/> tells it from a later process that reuses its pid.</summary>
    public sealed record TreeProcess(int Pid, string Name, DateTime StartUtc);

    /// <summary>Every process descended from <paramref name="pid"/>: its MCP servers, background shells
    /// and what those started. A child must have started after its parent, or the parent link is a reused pid.</summary>
    public static List<TreeProcess> Descendants(int pid)
    {
        var all = new List<(int Pid, int Parent, string Name, DateTime Start)>();
        foreach (var p in System.Diagnostics.Process.GetProcesses())
            using (p)
            {
                DateTime start;
                try { start = p.StartTime.ToUniversalTime(); }
                // System processes deny it, and a process may exit mid-scan: neither belongs to a session.
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { continue; }
                all.Add((p.Id, GetParentPid(p.Id), p.ProcessName, start));
            }
        var result = new List<TreeProcess>();
        var frontier = new Queue<(int Pid, DateTime Start)>();
        if (all.FirstOrDefault(x => x.Pid == pid) is { Pid: > 0 } root) frontier.Enqueue((root.Pid, root.Start));
        while (frontier.Count > 0)
        {
            var (parent, parentStart) = frontier.Dequeue();
            foreach (var child in all.Where(x => x.Parent == parent && x.Start >= parentStart))
            {
                result.Add(new TreeProcess(child.Pid, child.Name, child.Start));
                frontier.Enqueue((child.Pid, child.Start));
            }
        }
        return result;
    }

    private static int GetParentPid(int pid)
    {
        const int ProcessQueryLimitedInformation = 0x1000;
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return 0;
        try
        {
            var pbi = new ProcessBasicInformation();
            if (NtQueryInformationProcess(handle, 0, ref pbi, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0)
                return 0;
            return (int)pbi.InheritedFromUniqueProcessId;
        }
        catch { return 0; }
        finally { CloseHandle(handle); }
    }

    // ---- listening ports by owning pid -----------------------------------------------------

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(
        IntPtr table, ref int size, bool order, int af, int tableClass, int reserved);

    /// <summary>
    /// The workspace folders of every open VS Code window that runs the Claude extension: one
    /// entry per lock file whose port is still listening (a closed window's lock stays behind).
    /// Says what is open, not which window has it — see the class remarks.
    /// </summary>
    public static List<string[]> OpenWindowFolders()
    {
        var dir = Path.Combine(ClaudeDir, "ide");
        if (!Directory.Exists(dir)) return new();
        var listening = ListeningPorts(null).ToHashSet();
        return Directory.GetFiles(dir, "*.lock")
            .Select(f => int.TryParse(Path.GetFileNameWithoutExtension(f), out int port) ? port : 0)
            .Where(listening.Contains)
            .Select(ReadLockFolders)
            .Where(f => f.Length > 0)
            .ToList();
    }

    /// <summary>Ports listening on IPv4, of one process or (null) of all.</summary>
    private static List<int> ListeningPorts(int? pid)
    {
        const int AfInet = 2;
        const int TcpTableOwnerPidListener = 3;
        var ports = new List<int>();

        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
        if (size <= 0) return ports;

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0) != 0)
                return ports;

            // MIB_TCPTABLE_OWNER_PID: DWORD count, then count * MIB_TCPROW_OWNER_PID (6 DWORDs).
            int count = Marshal.ReadInt32(buffer);
            for (int i = 0; i < count; i++)
            {
                IntPtr row = buffer + 4 + (i * 24);
                if (pid != null && Marshal.ReadInt32(row, 20) != pid) continue;
                // dwLocalPort is network byte order in the low two bytes.
                int raw = Marshal.ReadInt32(row, 8);
                ports.Add(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return ports;
    }
}
