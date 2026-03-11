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
            NativeMethods.GetWindowRect(hPrimary, out var rect);
            var hMonitor = NativeMethods.MonitorFromWindow(hPrimary, NativeMethods.MONITOR_DEFAULTTONEAREST);
            taskbars.Add(new TaskbarData
            {
                Handle = hPrimary,
                Bounds = rect,
                MonitorHandle = hMonitor,
                IsPrimary = true,
                Edge = GetTaskbarEdge(rect, hMonitor)
            });
        }

        // Secondary taskbars (multi-monitor)
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            var className = new char[256];
            int len = NativeMethods.GetClassName(hWnd, className, className.Length);
            var name = new string(className, 0, len);

            if (name == "Shell_SecondaryTrayWnd")
            {
                NativeMethods.GetWindowRect(hWnd, out var rect);
                var hMonitor = NativeMethods.MonitorFromWindow(hWnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
                taskbars.Add(new TaskbarData
                {
                    Handle = hWnd,
                    Bounds = rect,
                    MonitorHandle = hMonitor,
                    IsPrimary = false,
                    Edge = GetTaskbarEdge(rect, hMonitor)
                });
            }
            return true;
        }, IntPtr.Zero);

        return taskbars;
    }

    private static uint GetTaskbarEdge(NativeMethods.RECT taskbarRect, IntPtr hMonitor)
    {
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfo(hMonitor, ref mi);

        // Determine edge based on taskbar position relative to monitor
        if (taskbarRect.Height < taskbarRect.Width)
        {
            // Horizontal taskbar
            return taskbarRect.Top <= mi.rcMonitor.Top + 10 ? NativeMethods.ABE_TOP : NativeMethods.ABE_BOTTOM;
        }
        else
        {
            // Vertical taskbar
            return taskbarRect.Left <= mi.rcMonitor.Left + 10 ? NativeMethods.ABE_LEFT : NativeMethods.ABE_RIGHT;
        }
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
