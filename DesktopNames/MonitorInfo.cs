namespace DesktopNames;

/// <summary>
/// Identifies a physical monitor in two forms:
///   - <see cref="DeviceId"/>: hardware-stable identifier (from EnumDisplayDevices with
///     EDD_GET_DEVICE_INTERFACE_NAME). Survives reboot, dock/undock, monitor reorder.
///   - <see cref="RectX"/>/<see cref="RectY"/>/<see cref="RectWidth"/>/<see cref="RectHeight"/>:
///     monitor rect at observation time. Fallback when device ID isn't available on the
///     current setup (e.g. moved between home and office with different monitors).
/// </summary>
internal sealed class MonitorRef
{
    public string? DeviceId { get; set; }
    public int RectX { get; set; }
    public int RectY { get; set; }
    public int RectWidth { get; set; }
    public int RectHeight { get; set; }

    /// <summary>Resolve the monitor that contains the given window (nearest if off-screen).</summary>
    public static MonitorRef? FromHwnd(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        IntPtr hMon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (hMon == IntPtr.Zero) return null;

        var mi = new NativeMethods.MONITORINFOEX
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
        };
        if (!NativeMethods.GetMonitorInfo(hMon, ref mi)) return null;

        return new MonitorRef
        {
            DeviceId = ResolveDeviceId(mi.szDevice),
            RectX = mi.rcMonitor.Left,
            RectY = mi.rcMonitor.Top,
            RectWidth = mi.rcMonitor.Right - mi.rcMonitor.Left,
            RectHeight = mi.rcMonitor.Bottom - mi.rcMonitor.Top
        };
    }

    /// <summary>
    /// Find an HMONITOR matching this reference on the current setup. Preference order:
    ///   1. By DeviceId (hardware-stable)
    ///   2. By rect position+size (fallback when DeviceId not present in this setup)
    /// Returns IntPtr.Zero if no match.
    /// </summary>
    public IntPtr ResolveCurrentHandle()
    {
        IntPtr byRectFallback = IntPtr.Zero;
        IntPtr matched = IntPtr.Zero;

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref NativeMethods.RECT rc, IntPtr lp) =>
        {
            var mi = new NativeMethods.MONITORINFOEX
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
            };
            if (!NativeMethods.GetMonitorInfo(hMon, ref mi)) return true;

            // Best: device ID match.
            if (!string.IsNullOrEmpty(DeviceId))
            {
                var dId = ResolveDeviceId(mi.szDevice);
                if (string.Equals(dId, DeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    matched = hMon;
                    return false;     // stop enumeration
                }
            }

            // Fallback: exact rect match.
            if (mi.rcMonitor.Left == RectX && mi.rcMonitor.Top == RectY &&
                mi.rcMonitor.Right - mi.rcMonitor.Left == RectWidth &&
                mi.rcMonitor.Bottom - mi.rcMonitor.Top == RectHeight)
            {
                byRectFallback = hMon;
            }
            return true;
        }, IntPtr.Zero);

        return matched != IntPtr.Zero ? matched : byRectFallback;
    }

    /// <summary>
    /// EnumDisplayDevices with EDD_GET_DEVICE_INTERFACE_NAME returns a hardware-stable
    /// identifier in DeviceID, e.g. "\\?\DISPLAY#GSM7780#...". szDevice from
    /// MONITORINFOEX is "\\.\DISPLAY1" form which reorders across reboots.
    /// </summary>
    private static string? ResolveDeviceId(string szDevice)
    {
        if (string.IsNullOrEmpty(szDevice)) return null;
        var dd = new NativeMethods.DISPLAY_DEVICE { cb = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>() };
        if (NativeMethods.EnumDisplayDevices(szDevice, 0, ref dd, NativeMethods.EDD_GET_DEVICE_INTERFACE_NAME))
            return string.IsNullOrEmpty(dd.DeviceID) ? null : dd.DeviceID;
        return null;
    }
}
