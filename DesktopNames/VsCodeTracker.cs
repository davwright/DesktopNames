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
    /// Move every currently-open VS Code workspace window to the desktop last remembered for
    /// it in <see cref="Settings.VsCodeWorkspaceDesktops"/> (which the passive scan keeps in
    /// sync with real window positions). Backs the overlay's "Move all VS Code windows to
    /// remembered desktops" command. Best-effort per window; returns the number actually moved.
    /// </summary>
    public int MoveAllToRemembered()
    {
        int moved = 0;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;

            var clsBuf = new char[64];
            int clsLen = NativeMethods.GetClassName(hwnd, clsBuf, clsBuf.Length);
            if (new string(clsBuf, 0, clsLen) != "Chrome_WidgetWin_1") return true;

            string? workspace = ExtractWorkspace(GetWindowTitle(hwnd));
            if (workspace == null) return true;
            if (!_settings.VsCodeWorkspaceDesktops.TryGetValue(workspace, out var saved)) return true;
            if (saved.DesktopId == Guid.Empty) return true;

            if (_desktop.GetDesktopForWindow(hwnd) == saved.DesktopId) return true; // already there
            if (_desktop.MoveWindowToDesktop(hwnd, saved.DesktopId)) moved++;
            return true;
        }, IntPtr.Zero);
        return moved;
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

            var currentMonitor = MonitorRef.FromHwnd(hwnd);

            bool firstSight = !_established.Contains(hwnd);

            // First sight with a saved entry: restore (desktop, monitor, window placement)
            // if AutoMove on. Each step is best-effort; failures don't gate the next.
            if (firstSight &&
                _settings.VsCodeAutoMove &&
                _settings.VsCodeWorkspaceDesktops.TryGetValue(workspace, out var saved))
            {
                if (saved.DesktopId != Guid.Empty && saved.DesktopId != currentDesktop)
                {
                    // Re-sync currentDesktop on success so the UpdateEntry below records the
                    // post-move location, not the stale pre-move one. Without this, the saved
                    // binding gets clobbered with whatever desktop VS Code happened to restore
                    // the window on, and every Claude hook firing in the ~2s before the next
                    // scan resolves to the wrong place (and gets cemented into learned-paths).
                    if (_desktop.MoveWindowToDesktop(hwnd, saved.DesktopId))
                        currentDesktop = saved.DesktopId;
                }
                if (saved.MonitorDeviceId != null || saved.MonitorWidth > 0)
                    _desktop.MoveWindowToMonitor(hwnd, saved);
                if (saved.WindowWidth > 0 || saved.WindowHeight > 0 || saved.WindowMaximized)
                    MonitorRef.ApplyWindowPlacement(hwnd, saved);
            }

            // CRUD the observation to the now-current (desktop, monitor, placement).
            // Manual moves via the user's AHK Win+Ctrl+N flow through this path.
            var observed = new WorkspaceLocation
            {
                DesktopId = currentDesktop,
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

        if (dirty) _settings.Save();
    }

    /// <summary>
    /// Replace the workspace's entry if the observed location differs from the saved one.
    /// Returns true if the map changed.
    /// </summary>
    private bool UpdateEntry(string workspace, WorkspaceLocation observed)
    {
        if (_settings.VsCodeWorkspaceDesktops.TryGetValue(workspace, out var saved) && SameLocation(saved, observed))
            return false;
        _settings.VsCodeWorkspaceDesktops[workspace] = observed;
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
        && a.WindowMaximized == b.WindowMaximized;

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
