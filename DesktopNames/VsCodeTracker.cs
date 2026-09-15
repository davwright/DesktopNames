using System.Text;

namespace DesktopNames;

/// <summary>
/// Tracks {VS Code workspace name → virtual desktop GUID} so that when a VS Code window
/// reappears (after restart, or moved to wrong desktop) it can be auto-moved back.
///
/// Identifier: title's "rootName" segment (i.e. workspace folder name) — VS Code's default
/// title pattern is "${activeEditor} - ${rootName} - Visual Studio Code", so we strip the
/// app suffix, then the editor prefix, leaving the workspace.
/// </summary>
internal sealed class VsCodeTracker : IDisposable
{
    private static readonly string[] AppSuffixes =
    {
        " - Visual Studio Code",
        " - Visual Studio Code - Insiders",
        " - Cursor",
    };

    private readonly DesktopService _desktop;
    private readonly Settings _settings;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly HashSet<IntPtr> _established = new();
    private readonly Dictionary<IntPtr, Guid> _lastDesktop = new();
    private readonly Dictionary<IntPtr, string?> _lastMonitor = new();

    public VsCodeTracker(DesktopService desktop, Settings settings)
    {
        _desktop = desktop;
        _settings = settings;

        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += (_, _) => Scan();
        _timer.Start();
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }

    /// <summary>
    /// Put every currently-open VS Code workspace window back where the current screen setup
    /// remembers it: screen, snap position, then virtual desktop. Backs the overlay's
    /// "Restore VS Code layout" command. Best-effort per window; returns the number touched.
    /// </summary>
    public int MoveAllToRemembered()
    {
        int moved = 0;
        foreach (var w in EnumerateOpenWorkspaceWindows())
        {
            if (!_settings.Workspaces.TryGetValue(w.Workspace, out var saved))
            {
                Log.Tracker($"restore skip '{w.Workspace}' — no saved entry for this screen setup");
                continue;
            }
            bool ok = _desktop.PlaceWindow(w.Hwnd, saved, saved.DesktopId);
            Log.Tracker($"restore '{w.Workspace}' {w.DesktopId} -> {saved.DesktopId} {(ok ? "ok" : "failed")}");
            moved++;
        }
        Log.Tracker($"restore layout: {moved} window(s) placed");
        return moved;
    }

    /// <summary>
    /// Move every open VS Code window onto <paramref name="screen"/>, keeping each on its own
    /// virtual desktop and in its own snap position. Backs the overlay's "Move all VS Code
    /// windows to → Screen N" command. Returns the number of windows moved.
    /// </summary>
    public int MoveAllToScreen(MonitorDescriptor screen)
    {
        int moved = 0;
        foreach (var w in EnumerateOpenWorkspaceWindows())
        {
            WorkspaceLocation target;
            if (_settings.Workspaces.TryGetValue(w.Workspace, out var saved)) target = saved.Clone();
            else
            {
                // Never seen this workspace on this setup — carry over how it sits right now
                // (maximized / snapped half / free rect) so only the screen changes.
                target = new WorkspaceLocation();
                MonitorRef.CaptureWindowPlacement(w.Hwnd, target);
            }
            target.DesktopId = w.DesktopId;          // stays on the desktop it's on
            target.MonitorDeviceId = screen.DeviceId;
            target.MonitorX = screen.Monitor.Left;
            target.MonitorY = screen.Monitor.Top;
            target.MonitorWidth = screen.Width;
            target.MonitorHeight = screen.Height;
            target.Pinned = true;   // explicit, same as the arrange dialog — Scan must not undo it

            _desktop.PlaceWindow(w.Hwnd, target, Guid.Empty);
            _settings.Workspaces[w.Workspace] = target;
            moved++;
        }
        if (moved > 0) _settings.Save();
        return moved;
    }

    /// <summary>One open VS Code workspace window and where it currently lives.</summary>
    public readonly record struct OpenWindow(IntPtr Hwnd, string Workspace, Guid DesktopId, MonitorRef? Monitor);

    /// <summary>
    /// Snapshot every currently-open VS Code workspace window with its live desktop + monitor.
    /// Backs the "arrange windows" dialog. Same window/title filter as <see cref="Scan"/>.
    /// </summary>
    public List<OpenWindow> EnumerateOpenWorkspaceWindows()
    {
        var result = new List<OpenWindow>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            var clsBuf = new char[64];
            int clsLen = NativeMethods.GetClassName(hwnd, clsBuf, clsBuf.Length);
            if (new string(clsBuf, 0, clsLen) != "Chrome_WidgetWin_1") return true;
            string? workspace = ExtractWorkspace(GetWindowTitle(hwnd));
            if (workspace == null) return true;
            result.Add(new OpenWindow(hwnd, workspace, _desktop.GetDesktopForWindow(hwnd), MonitorRef.FromHwnd(hwnd)));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private void Scan()
    {
        bool dirty = false;
        var seenThisScan = new HashSet<IntPtr>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;

            // Class filter: all Chromium-based windows. Title suffix is the real discriminator.
            var clsBuf = new char[64];
            int clsLen = NativeMethods.GetClassName(hwnd, clsBuf, clsBuf.Length);
            if (new string(clsBuf, 0, clsLen) != "Chrome_WidgetWin_1") return true;

            string title = GetWindowTitle(hwnd);
            string? workspace = ExtractWorkspace(title);
            if (workspace == null) return true;

            seenThisScan.Add(hwnd);

            Guid currentDesktop = _desktop.GetDesktopForWindow(hwnd);
            if (currentDesktop == Guid.Empty) return true;

            bool firstSight = !_established.Contains(hwnd);

            // First sight with a saved entry: restore (monitor, snap/placement, desktop)
            // if AutoMove on. Each step is best-effort; failures don't gate the next.
            if (firstSight &&
                _settings.VsCodeAutoMove &&
                _settings.Workspaces.TryGetValue(workspace, out var saved))
            {
                // Re-read where it ended up so the UpdateEntry below records the post-move
                // location, not the stale pre-move one. Without this, the saved binding gets
                // clobbered with whatever desktop VS Code happened to restore the window on,
                // and every Claude hook firing in the ~2s before the next scan resolves to
                // the wrong place (and gets cemented into learned-paths).
                _desktop.PlaceWindow(hwnd, saved, saved.DesktopId);
                var after = _desktop.GetDesktopForWindow(hwnd);
                if (after != Guid.Empty) currentDesktop = after;
            }

            var currentMonitor = MonitorRef.FromHwnd(hwnd);

            // Only a move observed between two scans of the same window may change the
            // workspace's saved desktop. A window seen for the first time (VS Code restoring
            // after a reboot spawns every window on the current desktop) or one sitting still
            // keeps its saved binding, so "Restore VS Code layout" has something to restore.
            // Exception: a saved desktop that no longer exists is dead — adopt the live one.
            bool movedSinceLastScan = !firstSight &&
                _lastDesktop.TryGetValue(hwnd, out var prevDesktop) && prevDesktop != currentDesktop;
            bool screenChanged = !firstSight &&
                _lastMonitor.TryGetValue(hwnd, out var prevMonitor) &&
                !string.Equals(prevMonitor, currentMonitor?.DeviceId, StringComparison.OrdinalIgnoreCase);
            _lastDesktop[hwnd] = currentDesktop;
            _lastMonitor[hwnd] = currentMonitor?.DeviceId;

            _settings.Workspaces.TryGetValue(workspace, out var known);

            // An assignment made by hand in the arrange dialog outranks anything observed.
            // The desktop was already protected above, but the monitor, snap and placement
            // below were not: they were rewritten from the live window every 2s, so a screen
            // the window never actually reached (or a later nudge) silently undid the choice.
            // Physically moving the window retires the pin and passive learning resumes.
            if (known is { Pinned: true })
            {
                if (!movedSinceLastScan && !screenChanged) return true;
                known.Pinned = false;
                dirty = true;
            }

            Guid recordDesktop = currentDesktop;
            if (!movedSinceLastScan && known != null && _desktop.DesktopExists(known.DesktopId))
                recordDesktop = known.DesktopId;

            // CRUD the observation to the now-current (desktop, monitor, placement).
            // Manual moves via the user's AHK Win+Ctrl+N flow through this path.
            var observed = new WorkspaceLocation
            {
                DesktopId = recordDesktop,
                MonitorDeviceId = currentMonitor?.DeviceId,
                MonitorX = currentMonitor?.RectX ?? 0,
                MonitorY = currentMonitor?.RectY ?? 0,
                MonitorWidth = currentMonitor?.RectWidth ?? 0,
                MonitorHeight = currentMonitor?.RectHeight ?? 0
            };
            MonitorRef.CaptureWindowPlacement(hwnd, observed);
            if (UpdateEntry(workspace, observed)) dirty = true;

            return true;
        }, IntPtr.Zero);

        _established.Clear();
        foreach (var h in seenThisScan) _established.Add(h);
        foreach (var h in _lastDesktop.Keys.Where(h => !seenThisScan.Contains(h)).ToList())
        {
            _lastDesktop.Remove(h);
            _lastMonitor.Remove(h);
        }

        if (dirty) _settings.Save();
    }

    /// <summary>
    /// Replace the workspace's entry if the observed location differs from the saved one.
    /// Returns true if the map changed.
    /// </summary>
    private bool UpdateEntry(string workspace, WorkspaceLocation observed)
    {
        if (_settings.Workspaces.TryGetValue(workspace, out var saved) && SameLocation(saved, observed))
            return false;
        _settings.Workspaces[workspace] = observed;
        return true;
    }

    private static bool SameLocation(WorkspaceLocation a, WorkspaceLocation b) =>
        a.DesktopId == b.DesktopId
        && a.MonitorDeviceId == b.MonitorDeviceId
        && a.MonitorX == b.MonitorX
        && a.MonitorY == b.MonitorY
        && a.MonitorWidth == b.MonitorWidth
        && a.MonitorHeight == b.MonitorHeight
        && a.WindowOffsetX == b.WindowOffsetX
        && a.WindowOffsetY == b.WindowOffsetY
        && a.WindowWidth == b.WindowWidth
        && a.WindowHeight == b.WindowHeight
        && a.Snap == b.Snap;

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int len = NativeMethods.GetWindowTextLength(hwnd);
        if (len == 0) return "";
        var sb = new StringBuilder(len + 1);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // Cache of VSCode profile names — when the user is on a non-default profile, VSCode
    // inserts the profile name as an extra title segment ("<file> - <workspace> - <profile>
    // - Visual Studio Code"). Without this list we'd take the profile name as the workspace
    // and miss the real rootName entirely. Source: %APPDATA%\Code\User\globalStorage\storage.json
    // → userDataProfiles[].name. Refreshed every couple of minutes.
    private static readonly HashSet<string> KnownProfileNames = new(StringComparer.Ordinal);
    private static DateTime _profileScanUtc = DateTime.MinValue;
    private static readonly TimeSpan ProfileScanCooldown = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Strip the trailing profile-name segment from a title stripped of its app suffix.
    /// Matches the LONGEST suffix that equals a known profile name, so profile names with
    /// embedded " - " separators are handled correctly (e.g. profile "Dev - Test" against
    /// title "...workspace - Dev - Test"). Returns the input unchanged if no profile matches.
    /// </summary>
    private static string StripTrailingProfile(string stripped)
    {
        if (KnownProfileNames.Count == 0) return stripped;

        // Walk back from the end of the string finding " - " positions. At each position,
        // the suffix beyond it is a candidate profile name. Pick the longest match.
        int bestCutAt = -1;
        int searchEnd = stripped.Length;
        while (true)
        {
            int dash = stripped.LastIndexOf(" - ", searchEnd - 1, searchEnd, StringComparison.Ordinal);
            if (dash < 0) break;
            string candidate = stripped[(dash + 3)..];
            if (KnownProfileNames.Contains(candidate))
            {
                bestCutAt = dash; // longer candidates are reached as we walk left
            }
            searchEnd = dash;
        }
        return bestCutAt >= 0 ? stripped[..bestCutAt] : stripped;
    }

    private static void EnsureProfileCacheFresh()
    {
        if (DateTime.UtcNow - _profileScanUtc < ProfileScanCooldown) return;
        _profileScanUtc = DateTime.UtcNow;
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Code", "User", "globalStorage", "storage.json");
            if (!File.Exists(path)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("userDataProfiles", out var profiles)) return;
            if (profiles.ValueKind != System.Text.Json.JsonValueKind.Array) return;
            KnownProfileNames.Clear();
            foreach (var p in profiles.EnumerateArray())
            {
                if (p.TryGetProperty("name", out var n))
                {
                    var name = n.GetString();
                    if (!string.IsNullOrEmpty(name)) KnownProfileNames.Add(name);
                }
            }
        }
        catch { /* leave cache as-is on failure */ }
    }

    internal static string? ExtractWorkspace(string title)
    {
        if (string.IsNullOrEmpty(title)) return null;
        string? stripped = null;
        foreach (var suffix in AppSuffixes)
        {
            if (title.EndsWith(suffix, StringComparison.Ordinal))
            {
                stripped = title[..^suffix.Length];
                break;
            }
        }
        if (stripped == null) return null;

        // VS Code default title: "${activeEditor} - ${rootName}". On a non-default profile
        // it becomes "${activeEditor} - ${rootName} - ${profileName}". Detect the trailing
        // profile segment by matching against the user's known profile names. The match
        // is longest-suffix-wins so profile names containing " - " (e.g. "Dev - Test") are
        // handled — we try every increasingly-long suffix and strip the longest hit.
        EnsureProfileCacheFresh();
        stripped = StripTrailingProfile(stripped);

        int lastDash = stripped.LastIndexOf(" - ", StringComparison.Ordinal);
        var workspace = lastDash >= 0 ? stripped[(lastDash + 3)..] : stripped;

        // Strip unsaved-changes bullet ("● ") if it's leading.
        workspace = workspace.TrimStart('●', ' ').Trim();

        // Filter out non-workspace windows.
        if (workspace.Length == 0) return null;
        if (workspace is "Welcome" or "Get Started" or "Settings" or "Walkthrough") return null;

        return workspace;
    }
}
