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

    private static string ClaudeDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>
    /// Scan sessions\*.json for the file whose sessionId matches. Callers send a truncated id
    /// (Claude-Alert reports the leading 8 hex chars), so match on prefix — but only accept a
    /// unique hit, so a prefix collision falls through to cwd resolution instead of guessing.
    /// Returns 0 if none.
    /// </summary>
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

    private static List<int> ListeningPorts(int pid)
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
                if (Marshal.ReadInt32(row, 20) != pid) continue;
                // dwLocalPort is network byte order in the low two bytes.
                int raw = Marshal.ReadInt32(row, 8);
                ports.Add(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return ports;
    }
}
