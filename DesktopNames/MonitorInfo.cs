namespace DesktopNames;

/// <summary>One physical monitor on the current setup, as enumerated by <see cref="MonitorRef.EnumerateAll"/>.</summary>
internal sealed class MonitorDescriptor
{
    public IntPtr Handle { get; init; }
    public string? DeviceId { get; init; }
    public NativeMethods.RECT Monitor { get; init; }
    public NativeMethods.RECT Work { get; init; }
    public bool IsPrimary { get; init; }
    public int X => Monitor.Left;
    public int Width => Monitor.Right - Monitor.Left;
    public int Height => Monitor.Bottom - Monitor.Top;

    /// <summary>GDI device name, e.g. "\\.\DISPLAY10". Reorders across reboots — don't persist it.</summary>
    public string Device { get; init; } = "";

    /// <summary>Monitor model as Windows reports it, e.g. "T27h-30". Empty if unavailable.</summary>
    public string Model { get; init; } = "";

    /// <summary>The number Windows shows in Settings → Display (and via "Identify"). 0 if unknown.</summary>
    public int Number { get; init; }

    /// <summary>Stable-ish key for one monitor: hardware id when we have it, else its resolution.</summary>
    public string Key => !string.IsNullOrEmpty(DeviceId) ? DeviceId! : $"{Width}x{Height}";

    /// <summary>Column/menu caption: "Screen 3 — T27h-30 2560×1440".</summary>
    public string Caption(int fallbackIndex)
    {
        string n = Number > 0 ? Number.ToString() : $"#{fallbackIndex + 1}";
        string model = Model.Length > 22 ? Model[..22] + "…" : Model;
        return model.Length > 0 ? $"Screen {n} — {model}" : $"Screen {n}";
    }
}

/// <summary>
/// How a window occupies its monitor. <see cref="Free"/> means "use the remembered
/// offset/size"; everything else is a rect derived from the monitor's work area, mirroring
/// the Windows snap positions.
/// </summary>
internal enum SnapMode
{
    Free, Max, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight
}

/// <summary>
/// Snap-position geometry: turn a <see cref="SnapMode"/> into a rect on a work area,
/// recognise which mode a live window is currently sitting in, and apply one.
/// </summary>
internal static class SnapGeometry
{
    public static readonly SnapMode[] All =
    {
        SnapMode.Free, SnapMode.Max, SnapMode.Left, SnapMode.Right, SnapMode.Top, SnapMode.Bottom,
        SnapMode.TopLeft, SnapMode.TopRight, SnapMode.BottomLeft, SnapMode.BottomRight
    };

    public static string Label(SnapMode m) => m switch
    {
        SnapMode.Max => "Full screen",
        SnapMode.Left => "Left half",
        SnapMode.Right => "Right half",
        SnapMode.Top => "Top half",
        SnapMode.Bottom => "Bottom half",
        SnapMode.TopLeft => "Top-left quarter",
        SnapMode.TopRight => "Top-right quarter",
        SnapMode.BottomLeft => "Bottom-left quarter",
        SnapMode.BottomRight => "Bottom-right quarter",
        _ => "Free — keep size",
    };

    /// <summary>Compact tag shown on the arrange-dialog chips.</summary>
    public static string Tag(SnapMode m) => m switch
    {
        SnapMode.Max => "FULL",
        SnapMode.Left => "L",
        SnapMode.Right => "R",
        SnapMode.Top => "T",
        SnapMode.Bottom => "B",
        SnapMode.TopLeft => "TL",
        SnapMode.TopRight => "TR",
        SnapMode.BottomLeft => "BL",
        SnapMode.BottomRight => "BR",
        _ => "—",
    };

    public static NativeMethods.RECT Rect(NativeMethods.RECT work, SnapMode mode)
    {
        int mx = work.Left + work.Width / 2;
        int my = work.Top + work.Height / 2;
        return mode switch
        {
            SnapMode.Left        => Make(work.Left, work.Top, mx,          work.Bottom),
            SnapMode.Right       => Make(mx,        work.Top, work.Right,  work.Bottom),
            SnapMode.Top         => Make(work.Left, work.Top, work.Right,  my),
            SnapMode.Bottom      => Make(work.Left, my,       work.Right,  work.Bottom),
            SnapMode.TopLeft     => Make(work.Left, work.Top, mx,          my),
            SnapMode.TopRight    => Make(mx,        work.Top, work.Right,  my),
            SnapMode.BottomLeft  => Make(work.Left, my,       mx,          work.Bottom),
            SnapMode.BottomRight => Make(mx,        my,       work.Right,  work.Bottom),
            _                    => work,
        };
    }

    private static NativeMethods.RECT Make(int l, int t, int r, int b)
        => new() { Left = l, Top = t, Right = r, Bottom = b };

    /// <summary>
    /// Which snap position is this window in right now? Maximized is reported as
    /// <see cref="SnapMode.Max"/>; a half/quarter is recognised by comparing the window's
    /// *visible* frame (DWM bounds, so the invisible resize border doesn't skew it) against
    /// the candidate rects. Anything else is <see cref="SnapMode.Free"/>.
    /// </summary>
    public static SnapMode Detect(IntPtr hwnd, NativeMethods.RECT work)
    {
        var wp = new NativeMethods.WINDOWPLACEMENT { length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>() };
        if (NativeMethods.GetWindowPlacement(hwnd, ref wp) && wp.showCmd == NativeMethods.SW_MAXIMIZE)
            return SnapMode.Max;

        if (!TryVisibleBounds(hwnd, out var vis)) return SnapMode.Free;

        const int tol = 6;
        foreach (var m in All)
        {
            if (m is SnapMode.Free or SnapMode.Max) continue;
            var r = Rect(work, m);
            if (Math.Abs(r.Left - vis.Left) <= tol && Math.Abs(r.Top - vis.Top) <= tol &&
                Math.Abs(r.Right - vis.Right) <= tol && Math.Abs(r.Bottom - vis.Bottom) <= tol)
                return m;
        }
        return SnapMode.Free;
    }

    /// <summary>Detect the snap position against the window's own monitor.</summary>
    public static SnapMode Detect(IntPtr hwnd)
    {
        IntPtr hMon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (hMon == IntPtr.Zero) return SnapMode.Free;
        var mi = new NativeMethods.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        return NativeMethods.GetMonitorInfo(hMon, ref mi) ? Detect(hwnd, mi.rcWork) : SnapMode.Free;
    }

    /// <summary>
    /// Put the window into a snap position on the given work area. Uses SetWindowPlacement
    /// so it works without activation and on windows currently living on another virtual
    /// desktop. <see cref="SnapMode.Free"/> is a no-op (the caller restores offsets instead).
    /// </summary>
    public static bool Apply(IntPtr hwnd, NativeMethods.RECT work, SnapMode mode)
    {
        if (mode == SnapMode.Free) return false;
        if (mode == SnapMode.Max)
        {
            // A window that is ALREADY maximized ignores a fresh SW_MAXIMIZE: Windows keeps it
            // maximized on the monitor it's on and merely records the new restore rect, so the
            // window never crosses to the target screen. Restore it onto the target monitor
            // first — that move does happen — and only then maximize, which picks the monitor
            // containing the restored rect. The restored rect doubles as a sane un-maximize
            // target on the new screen.
            var normal = Rect(work, SnapMode.TopLeft);
            Place(hwnd, normal, maximize: false);
            return Place(hwnd, normal, maximize: true);
        }

        var target = Rect(work, mode);
        if (!Place(hwnd, target, maximize: false)) return false;

        // Chromium windows carry an invisible resize border, so the outer rect we just set
        // leaves a visible gap at the edges. Measure what actually landed and correct once.
        if (TryVisibleBounds(hwnd, out var vis))
        {
            int dl = vis.Left - target.Left, dt = vis.Top - target.Top;
            int dr = vis.Right - target.Right, db = vis.Bottom - target.Bottom;
            if (Within(dl) && Within(dt) && Within(dr) && Within(db) &&
                (Math.Abs(dl) > 2 || Math.Abs(dt) > 2 || Math.Abs(dr) > 2 || Math.Abs(db) > 2))
            {
                Place(hwnd, Make(target.Left - dl, target.Top - dt, target.Right - dr, target.Bottom - db), maximize: false);
            }
        }
        return true;
    }

    private static bool Within(int delta) => Math.Abs(delta) <= 40;

    private static bool Place(IntPtr hwnd, NativeMethods.RECT rc, bool maximize)
    {
        var wp = new NativeMethods.WINDOWPLACEMENT { length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>() };
        if (!NativeMethods.GetWindowPlacement(hwnd, ref wp)) return false;
        wp.rcNormalPosition = rc;
        wp.showCmd = maximize ? NativeMethods.SW_MAXIMIZE : NativeMethods.SW_SHOWNORMAL;
        return NativeMethods.SetWindowPlacement(hwnd, ref wp);
    }

    /// <summary>The window's visible frame (excludes the invisible resize border/shadow).</summary>
    private static bool TryVisibleBounds(IntPtr hwnd, out NativeMethods.RECT rc)
    {
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out rc, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>()) == 0
            && rc.Width > 0 && rc.Height > 0)
            return true;
        return NativeMethods.GetWindowRect(hwnd, out rc);
    }
}

/// <summary>
/// Works out the display numbers Windows shows in Settings → Display (the ones the "Identify"
/// button flashes on each screen). No API returns them, so we reproduce how Windows orders
/// displays: by graphics adapter in the order QueryDisplayConfig reports the active paths, then
/// by the monitor's target id (the physical connector/UID) within each adapter.
///
/// Deliberately NOT derived from screen coordinates or from the "\\.\DISPLAYn" number — both
/// disagree with Settings on a multi-adapter setup (e.g. a DisplayLink dock, where the monitor
/// on the left can be Windows' number 3 and the one on the right number 2).
/// </summary>
internal static class DisplayNumbering
{
    private const int ModeInfoSize = 64;   // sizeof(DISPLAYCONFIG_MODE_INFO)

    /// <summary>GDI device name ("\\.\DISPLAY10") → Windows display number. Empty on failure.</summary>
    public static Dictionary<string, int> Current()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        IntPtr modes = IntPtr.Zero;
        try
        {
            if (NativeMethods.GetDisplayConfigBufferSizes(NativeMethods.QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0)
                return result;
            if (np == 0) return result;

            var paths = new NativeMethods.DISPLAYCONFIG_PATH_INFO[np];
            modes = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)Math.Max(1, nm) * ModeInfoSize);
            if (NativeMethods.QueryDisplayConfig(NativeMethods.QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0)
                return result;

            // (adapter, targetId, gdiName) in path order, one entry per source.
            var entries = new List<(ulong adapter, uint target, string gdi)>();
            var adapterOrder = new List<ulong>();
            for (int i = 0; i < np; i++)
            {
                var req = new NativeMethods.DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new NativeMethods.DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = paths[i].sourceInfo.adapterId,
                        id = paths[i].sourceInfo.id,
                    }
                };
                if (NativeMethods.DisplayConfigGetDeviceInfo(ref req) != 0) continue;
                string gdi = req.viewGdiDeviceName ?? "";
                if (gdi.Length == 0 || entries.Any(e => e.gdi == gdi)) continue;   // skip clones

                ulong adapter = ((ulong)(uint)paths[i].sourceInfo.adapterId.HighPart << 32) | paths[i].sourceInfo.adapterId.LowPart;
                if (!adapterOrder.Contains(adapter)) adapterOrder.Add(adapter);
                entries.Add((adapter, paths[i].targetInfo.id, gdi));
            }

            int n = 0;
            foreach (var adapter in adapterOrder)
                foreach (var e in entries.Where(e => e.adapter == adapter).OrderBy(e => e.target))
                    result[e.gdi] = ++n;
        }
        catch { result.Clear(); }
        finally { if (modes != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(modes); }
        return result;
    }
}

/// <summary>
/// Identity of a whole monitor arrangement ("work" = laptop + two 4K, "home" = laptop + one
/// ultrawide, "single" = laptop alone). Window positions are remembered per arrangement so
/// docking somewhere else doesn't overwrite the layout you use at the other place.
/// </summary>
internal static class ScreenSetup
{
    /// <summary>
    /// Order-independent key for the current arrangement: which monitors, at which coordinates.
    /// Moving a monitor's position in Windows' display arrangement therefore yields a different
    /// arrangement (and its own remembered window positions), which is the point — a window
    /// "on the left screen" means something different after you swap the screens around.
    /// </summary>
    public static string SignatureOf(List<MonitorDescriptor> screens)
        => Hash(screens.Select(s => $"{s.Key}@{s.Monitor.Left},{s.Monitor.Top},{s.Width}x{s.Height}"), screens.Count);

    /// <summary>
    /// Order- and position-independent key: just which physical monitors are attached. Used to
    /// find the closest previous arrangement to seed from when the coordinates changed.
    /// </summary>
    public static string DeviceSignatureOf(List<MonitorDescriptor> screens)
        => Hash(screens.Select(s => s.Key), screens.Count);

    // FNV-1a — not string.GetHashCode, which is randomised per process and would not persist.
    private static string Hash(IEnumerable<string> parts, int count)
    {
        uint h = 2166136261;
        foreach (char c in string.Join("|", parts.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)))
        {
            h ^= char.ToLowerInvariant(c);
            h *= 16777619;
        }
        return $"{count}s-{h:x8}";
    }

    /// <summary>Default human label for an arrangement, e.g. "3 screens · 3840×2160 + 1920×1080 + 1920×1080".</summary>
    public static string DescribeOf(List<MonitorDescriptor> screens)
    {
        if (screens.Count == 0) return "no screens";
        var sizes = string.Join(" + ", screens.Select(s => $"{s.Width}×{s.Height}"));
        return $"{screens.Count} screen{(screens.Count == 1 ? "" : "s")} · {sizes}";
    }

    public static string Signature() => SignatureOf(MonitorRef.EnumerateAll());
    public static string Describe() => DescribeOf(MonitorRef.EnumerateAll());
}

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
    /// Capture window placement relative to its current monitor's work area.
    /// Writes offset/size/maximized fields onto the supplied location.
    /// </summary>
    public static void CaptureWindowPlacement(IntPtr hwnd, WorkspaceLocation loc)
    {
        IntPtr hMon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (hMon == IntPtr.Zero) return;
        var mi = new NativeMethods.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        if (!NativeMethods.GetMonitorInfo(hMon, ref mi)) return;

        var wp = new NativeMethods.WINDOWPLACEMENT { length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>() };
        if (!NativeMethods.GetWindowPlacement(hwnd, ref wp)) return;

        // rcNormalPosition is in workspace coordinates (relative to the work area of
        // the primary monitor — slightly weird but stable). Subtract the primary
        // work-area origin if needed; in practice on a multi-monitor setup the values
        // are screen-space, so subtracting the source monitor's work area gives offset.
        loc.Snap = SnapGeometry.Detect(hwnd, mi.rcWork);
        loc.WindowOffsetX = wp.rcNormalPosition.Left - mi.rcWork.Left;
        loc.WindowOffsetY = wp.rcNormalPosition.Top  - mi.rcWork.Top;
        loc.WindowWidth   = wp.rcNormalPosition.Right - wp.rcNormalPosition.Left;
        loc.WindowHeight  = wp.rcNormalPosition.Bottom - wp.rcNormalPosition.Top;
    }

    /// <summary>
    /// Apply a previously-captured window placement to the given window on its current
    /// monitor (caller has already moved it to the right monitor). Returns false if the
    /// location carries no placement data.
    /// </summary>
    public static bool ApplyWindowPlacement(IntPtr hwnd, WorkspaceLocation loc)
    {
        if (loc.WindowWidth <= 0 && loc.WindowHeight <= 0 && loc.Snap == SnapMode.Free) return false;

        IntPtr hMon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (hMon == IntPtr.Zero) return false;
        var mi = new NativeMethods.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        if (!NativeMethods.GetMonitorInfo(hMon, ref mi)) return false;

        if (loc.Snap != SnapMode.Free) return SnapGeometry.Apply(hwnd, mi.rcWork, loc.Snap);

        var wp = new NativeMethods.WINDOWPLACEMENT { length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>() };
        if (!NativeMethods.GetWindowPlacement(hwnd, ref wp)) return false;

        int left = mi.rcWork.Left + loc.WindowOffsetX;
        int top  = mi.rcWork.Top  + loc.WindowOffsetY;
        wp.rcNormalPosition.Left   = left;
        wp.rcNormalPosition.Top    = top;
        wp.rcNormalPosition.Right  = left + loc.WindowWidth;
        wp.rcNormalPosition.Bottom = top  + loc.WindowHeight;
        wp.showCmd = NativeMethods.SW_SHOWNORMAL;

        return NativeMethods.SetWindowPlacement(hwnd, ref wp);
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
    /// Enumerate every physical monitor on the current setup, left-to-right by screen X.
    /// Used by the "arrange windows" dialog to lay out the screen columns and to translate
    /// a dropped cell back into a concrete target monitor.
    /// </summary>
    public static List<MonitorDescriptor> EnumerateAll()
    {
        var numbers = DisplayNumbering.Current();
        var list = new List<MonitorDescriptor>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMon, IntPtr hdc, ref NativeMethods.RECT rc, IntPtr lp) =>
        {
            var mi = new NativeMethods.MONITORINFOEX
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
            };
            if (NativeMethods.GetMonitorInfo(hMon, ref mi))
            {
                list.Add(new MonitorDescriptor
                {
                    Handle = hMon,
                    DeviceId = ResolveDeviceId(mi.szDevice),
                    Device = mi.szDevice ?? "",
                    Model = ResolveModel(mi.szDevice),
                    Number = numbers.TryGetValue(mi.szDevice ?? "", out var n) ? n : 0,
                    Monitor = mi.rcMonitor,
                    Work = mi.rcWork,
                    IsPrimary = (mi.dwFlags & 1u) != 0   // MONITORINFOF_PRIMARY
                });
            }
            return true;
        }, IntPtr.Zero);
        // Ordered the way Windows numbers them, so the arrange grid's columns line up with
        // Settings → Display. Screen X position is NOT usable for this: two monitors on the
        // same adapter can be numbered right-to-left. Unnumbered monitors go last.
        list.Sort((a, b) =>
        {
            int an = a.Number > 0 ? a.Number : int.MaxValue;
            int bn = b.Number > 0 ? b.Number : int.MaxValue;
            return an != bn ? an.CompareTo(bn) : a.Monitor.Left.CompareTo(b.Monitor.Left);
        });
        return list;
    }

    /// <summary>Monitor model string, e.g. "T27h-30". Empty when Windows won't say.</summary>
    private static string ResolveModel(string? szDevice)
    {
        if (string.IsNullOrEmpty(szDevice)) return "";
        var dd = new NativeMethods.DISPLAY_DEVICE { cb = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>() };
        return NativeMethods.EnumDisplayDevices(szDevice, 0, ref dd, 0) ? dd.DeviceString ?? "" : "";
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
