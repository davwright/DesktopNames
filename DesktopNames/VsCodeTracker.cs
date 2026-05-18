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

            // First sight with a saved entry: restore to (desktop, monitor) if AutoMove on.
            // Both moves are best-effort; either no-ops if target isn't present in this setup
            // (deleted desktop, monitor missing at home).
            if (firstSight &&
                _settings.VsCodeAutoMove &&
                _settings.VsCodeWorkspaceDesktops.TryGetValue(workspace, out var saved))
            {
                if (saved.DesktopId != Guid.Empty && saved.DesktopId != currentDesktop)
                    _desktop.MoveWindowToDesktop(hwnd, saved.DesktopId);
                if (saved.MonitorDeviceId != null || saved.MonitorWidth > 0)
                    _desktop.MoveWindowToMonitor(hwnd, saved);
            }

            // CRUD the observation to the now-current location. Manual moves (Win+Ctrl+N
            // via the user's AHK) flow through this path and become the new binding.
            if (UpdateEntry(workspace, currentDesktop, currentMonitor)) dirty = true;

            return true;
        }, IntPtr.Zero);

        _established.Clear();
        foreach (var h in seenThisScan) _established.Add(h);

        if (dirty) _settings.Save();
    }

    /// <summary>
    /// Update (or insert) the workspace's entry to match the given desktop + monitor.
    /// Returns true if the map changed (so the caller knows to save).
    /// </summary>
    private bool UpdateEntry(string workspace, Guid desktop, MonitorRef? monitor)
    {
        bool existed = _settings.VsCodeWorkspaceDesktops.TryGetValue(workspace, out var saved);
        bool changed = !existed
            || saved!.DesktopId != desktop
            || saved.MonitorDeviceId != monitor?.DeviceId
            || saved.MonitorX != (monitor?.RectX ?? 0)
            || saved.MonitorY != (monitor?.RectY ?? 0)
            || saved.MonitorWidth != (monitor?.RectWidth ?? 0)
            || saved.MonitorHeight != (monitor?.RectHeight ?? 0);

        if (!changed) return false;

        _settings.VsCodeWorkspaceDesktops[workspace] = new WorkspaceLocation
        {
            DesktopId = desktop,
            MonitorDeviceId = monitor?.DeviceId,
            MonitorX = monitor?.RectX ?? 0,
            MonitorY = monitor?.RectY ?? 0,
            MonitorWidth = monitor?.RectWidth ?? 0,
            MonitorHeight = monitor?.RectHeight ?? 0
        };
        return true;
    }

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
