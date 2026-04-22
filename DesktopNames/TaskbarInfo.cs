using System.Runtime.InteropServices;

namespace DesktopNames;

/// <summary>
/// Finds taskbar windows on all monitors.
/// </summary>
internal static class TaskbarInfo
{
    public static List<TaskbarData> FindAllTaskbars()
    {
        var taskbars = new List<TaskbarData>();

        // Primary taskbar
        var hPrimary = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (hPrimary != IntPtr.Zero)
        {
            var data = BuildTaskbarData(hPrimary, isPrimary: true);
            if (data != null) taskbars.Add(data);
        }

        // Secondary taskbars (multi-monitor)
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            var className = new char[256];
            int len = NativeMethods.GetClassName(hWnd, className, className.Length);
            var name = new string(className, 0, len);

            if (name == "Shell_SecondaryTrayWnd")
            {
                var data = BuildTaskbarData(hWnd, isPrimary: false);
                if (data != null) taskbars.Add(data);
            }
            return true;
        }, IntPtr.Zero);

        return taskbars;
    }

    private static TaskbarData? BuildTaskbarData(IntPtr hWnd, bool isPrimary)
    {
        var hMonitor = NativeMethods.MonitorFromWindow(hWnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(hMonitor, ref mi)) return null;

        // Derive the taskbar's visible strip from the difference between
        // the monitor's full bounds and its work area. This is authoritative —
        // GetWindowRect on the tray hwnd can return the whole monitor rect
        // on Win11, which we cannot trust to infer edge or position.
        var mon = mi.rcMonitor;
        var work = mi.rcWork;
        NativeMethods.RECT strip;
        uint edge;

        if (work.Bottom < mon.Bottom)
        {
            edge = NativeMethods.ABE_BOTTOM;
            strip = new NativeMethods.RECT { Left = mon.Left, Top = work.Bottom, Right = mon.Right, Bottom = mon.Bottom };
        }
        else if (work.Top > mon.Top)
        {
            edge = NativeMethods.ABE_TOP;
            strip = new NativeMethods.RECT { Left = mon.Left, Top = mon.Top, Right = mon.Right, Bottom = work.Top };
        }
        else if (work.Left > mon.Left)
        {
            edge = NativeMethods.ABE_LEFT;
            strip = new NativeMethods.RECT { Left = mon.Left, Top = mon.Top, Right = work.Left, Bottom = mon.Bottom };
        }
        else if (work.Right < mon.Right)
        {
            edge = NativeMethods.ABE_RIGHT;
            strip = new NativeMethods.RECT { Left = work.Right, Top = mon.Top, Right = mon.Right, Bottom = mon.Bottom };
        }
        else
        {
            // Work area equals monitor — taskbar is auto-hidden or unreservered.
            // Fall back to GetWindowRect and default to bottom edge.
            NativeMethods.GetWindowRect(hWnd, out strip);
            edge = NativeMethods.ABE_BOTTOM;
        }

        return new TaskbarData
        {
            Handle = hWnd,
            Bounds = strip,
            MonitorHandle = hMonitor,
            IsPrimary = isPrimary,
            Edge = edge
        };
    }
}

internal sealed class TaskbarData
{
    public IntPtr Handle { get; set; }
    public NativeMethods.RECT Bounds { get; set; }
    public IntPtr MonitorHandle { get; set; }
    public bool IsPrimary { get; set; }
    public uint Edge { get; set; }
}
