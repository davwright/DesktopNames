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
                    _desktop.MoveWindowToDesktop(hwnd, saved.DesktopId);
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

        // VS Code default pattern: "${activeEditor} - ${rootName}"
        // Take the part after the last " - " — that's the workspace.
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
