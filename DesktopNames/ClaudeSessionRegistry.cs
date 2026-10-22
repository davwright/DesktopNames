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

    // path → (bytes scanned, latest ai-title). Transcripts only grow, so each read scans what was appended.
    private static readonly Dictionary<string, (long Length, string? Title)> _transcripts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex AiTitle =
        new("\"type\":\"ai-title\",\"aiTitle\":(\"(?:[^\"\\\\]|\\\\.)*\")", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// What a transcript says about its session: the latest <c>ai-title</c> Claude Code gave it
    /// ("Osis dev privileges review") and when it was last written — the session's real last
    /// activity, which survives a DesktopNames restart. Null when the file is gone.
    /// </summary>
    public static (string? Title, DateTime LastWriteUtc)? Transcript(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) return null;
        _transcripts.TryGetValue(path, out var seen);
        // First sight: Claude Code repeats the ai-title through the transcript, so the tail usually
        // has one and a multi-megabyte file needn't be read whole (that made the window open in 3 s).
        const long Tail = 512 * 1024;
        if (seen.Length == 0 && file.Length > Tail)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(-Tail, SeekOrigin.End);
            var matches = AiTitle.Matches(new StreamReader(stream, System.Text.Encoding.UTF8).ReadToEnd());
            if (matches.Count > 0)
                _transcripts[path] = seen = (stream.Position, JsonSerializer.Deserialize<string>(matches[^1].Groups[1].Value));
        }
        if (file.Length != seen.Length)
        {
            long from = file.Length > seen.Length ? seen.Length : 0;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(from, SeekOrigin.Begin);
            string appended = new StreamReader(stream, System.Text.Encoding.UTF8).ReadToEnd();
            var matches = AiTitle.Matches(appended);
            string? title = matches.Count > 0 ? JsonSerializer.Deserialize<string>(matches[^1].Groups[1].Value) : seen.Title;
            _transcripts[path] = seen = (stream.Position, title);
        }
        return (seen.Title, file.LastWriteTimeUtc);
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
    public sealed record LiveSession(int Pid, string Entrypoint, string Cwd, string Name, string NameSource)
    {
        public bool IsTerminal => Entrypoint == "cli";
    }

    /// <summary>
    /// The full sessionId of the live session called <paramref name="name"/> ("evolxcli-67", the
    /// name SendMessage addresses another session by), or null when no session or several have it.
    /// </summary>
    public string? SessionIdByName(string name)
    {
        var dir = Path.Combine(ClaudeDir, "sessions");
        if (!Directory.Exists(dir)) return null;
        var hits = new List<string>();
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(file)); }
            catch (Exception ex) when (ex is IOException or JsonException) { continue; } // half-written mid-launch
            using (doc)
                if (doc.RootElement.TryGetProperty("name", out var n) && n.GetString() == name
                    && doc.RootElement.TryGetProperty("sessionId", out var sid) && sid.GetString() is { } full)
                    hits.Add(full);
        }
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>Where a terminal Claude is shown: a VS Code window's integrated terminal, or a
    /// window of its own (Windows Terminal, a console), found by walking up its parent processes.</summary>
    public sealed record TerminalHost(bool InVsCode, IntPtr Window);

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
                    r.TryGetProperty("nameSource", out var ns) ? ns.GetString() ?? "" : "");
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
