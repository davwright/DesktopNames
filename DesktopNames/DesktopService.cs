using System.Runtime.InteropServices;

namespace DesktopNames;

/// <summary>
/// Virtual desktop management. Three layers:
///   1. <see cref="VdaDll"/> — flat-C P/Invoke to Ciantic's VirtualDesktopAccessor.dll.
///      Used for everything risky (cross-process moves on Chromium views especially).
///      Kept current upstream so vtable drift across Windows builds is upstream's problem.
///   2. <see cref="VirtualDesktopInterop"/> — hand-rolled C# COM. Used only for the
///      handful of operations VDA doesn't cover well: SetDesktopName (Unicode names —
///      VDA's API is ANSI) and MoveDesktop (reorder — VDA has no equivalent).
///   3. Registry — fallback for desktop names and current-id when the COM path fails.
/// </summary>
internal sealed class DesktopService : IDisposable
{
    private IVirtualDesktopManagerInternal? _manager;   // only used for SetDesktopName + MoveDesktop (reorder)

    public event Action? DesktopsChanged;

    public bool Initialize()
    {
        // The interop COM object is still needed for the two operations VDA doesn't cover.
        // VDA itself is initialized by Program.Main before this method runs.
        try { _manager = VirtualDesktopInterop.GetManagerInternal(); } catch { }
        return VdaDll.IsLoaded;
    }

    public void CreateDesktop()
    {
        if (!VdaDll.IsLoaded) return;
        try { VdaDll.CreateDesktop(); DesktopsChanged?.Invoke(); } catch { }
    }

    public void RemoveCurrentDesktop()
    {
        if (!VdaDll.IsLoaded) return;
        try
        {
            int count = VdaDll.GetDesktopCount();
            if (count <= 1) return;
            int currentIdx = VdaDll.GetCurrentDesktopNumber();
            int fallbackIdx = currentIdx > 0 ? currentIdx - 1 : 1;
            VdaDll.RemoveDesktop(currentIdx, fallbackIdx);
            DesktopsChanged?.Invoke();
        }
        catch { }
    }

    public void RenameDesktop(Guid desktopId, string newName)
    {
        // VDA's SetDesktopName takes ANSI — would mangle "Mobilität" etc. So we keep this
        // one operation on the hand-rolled COM. The slot for SetDesktopName hasn't been
        // observed to AV; only MoveViewToDesktop has.
        if (_manager == null) return;
        try
        {
            var target = _manager.FindDesktop(ref desktopId);
            if (target == null) return;
            _manager.SetDesktopName(target, newName);
            DesktopsChanged?.Invoke();
        }
        catch { }
    }

    /// <summary>
    /// Move an arbitrary window to a given virtual desktop via VDA.
    /// Routed through VirtualDesktopAccessor.dll because the equivalent hand-rolled COM
    /// call (MoveViewToDesktop) AVs deterministically on Chromium views on Win11 26200+.
    /// </summary>
    public bool MoveWindowToDesktop(IntPtr hwnd, Guid desktopId)
    {
        if (!VdaDll.IsLoaded || desktopId == Guid.Empty || hwnd == IntPtr.Zero)
            return false;
        try
        {
            int targetIdx = IndexFromGuid(desktopId);
            if (targetIdx < 0) return false;
            return VdaDll.MoveWindowToDesktopNumber(hwnd, targetIdx) != 0;
        }
        catch { return false; }
    }

    public Guid GetDesktopForWindow(IntPtr hwnd)
    {
        if (!VdaDll.IsLoaded || hwnd == IntPtr.Zero) return Guid.Empty;
        try { return VdaDll.GetWindowDesktopId(hwnd); }
        catch { return Guid.Empty; }
    }

    public Guid GetCurrentDesktopId()
    {
        if (VdaDll.IsLoaded)
        {
            try
            {
                int idx = VdaDll.GetCurrentDesktopNumber();
                if (idx >= 0) return VdaDll.GetDesktopIdByNumber(idx);
            }
            catch { }
        }
        // Registry fallback (also handles VDA not loaded case).
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops");
            if (key?.GetValue("CurrentVirtualDesktop") is byte[] bytes && bytes.Length == 16)
                return new Guid(bytes);
        }
        catch { }
        return Guid.Empty;
    }

    /// <summary>
    /// Translate a desktop GUID to its zero-based index via VDA.
    /// Returns -1 if not found.
    /// </summary>
    private static int IndexFromGuid(Guid id)
    {
        if (!VdaDll.IsLoaded) return -1;
        try
        {
            int count = VdaDll.GetDesktopCount();
            for (int i = 0; i < count; i++)
                if (VdaDll.GetDesktopIdByNumber(i) == id) return i;
        }
        catch { }
        return -1;
    }

    public int GetCurrentDesktopIndexPublic()
    {
        if (VdaDll.IsLoaded)
        {
            try { return VdaDll.GetCurrentDesktopNumber(); } catch { }
        }
        return GetCurrentDesktopIndex();
    }

    public void MoveCurrentDesktopBy(int delta)
    {
        if (_manager == null || delta == 0) return;
        try
        {
            int count = VdaDll.IsLoaded ? VdaDll.GetDesktopCount() : _manager.GetCount();
            int currentIdx = GetCurrentDesktopIndexPublic();
            if (currentIdx < 0) return;
            int target = Math.Clamp(currentIdx + delta, 0, count - 1);
            if (target == currentIdx) return;
            MoveCurrentDesktopToIndex(target);
        }
        catch { }
    }

    public void MoveCurrentDesktopToFirst() => MoveCurrentDesktopToIndex(0);

    public void MoveCurrentDesktopToLast()
    {
        if (_manager == null) return;
        try
        {
            int count = VdaDll.IsLoaded ? VdaDll.GetDesktopCount() : _manager.GetCount();
            MoveCurrentDesktopToIndex(count - 1);
        }
        catch { }
    }

    private void MoveCurrentDesktopToIndex(int targetIndex)
    {
        // Reorder isn't exposed by VDA, so this stays on the hand-rolled COM path.
        // The MoveDesktop slot hasn't been observed to AV; only MoveViewToDesktop has.
        if (_manager == null) return;
        try
        {
            var current = _manager.GetCurrentDesktop();
            _manager.MoveDesktop(current, targetIndex);
            DesktopsChanged?.Invoke();
        }
        catch { }
    }

    public void FocusTopmostWindowOnCurrentDesktop()
    {
        try
        {
            IntPtr top = NativeMethods.GetTopWindow(IntPtr.Zero);
            while (top != IntPtr.Zero)
            {
                if (IsCandidateWindow(top) && IsOnCurrentDesktop(top))
                {
                    ForceForegroundWindow(top);
                    return;
                }
                top = NativeMethods.GetWindow(top, NativeMethods.GW_HWNDNEXT);
            }
        }
        catch { }
    }

    private static bool IsOnCurrentDesktop(IntPtr hwnd)
    {
        if (!VdaDll.IsLoaded) return true;
        try { return VdaDll.IsWindowOnCurrentVirtualDesktop(hwnd) != 0; }
        catch { return false; }
    }

    private static bool IsCandidateWindow(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindowVisible(hwnd)) return false;

        int style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE);
        if ((style & NativeMethods.WS_CHILD) != 0) return false;

        int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0 &&
            (ex & NativeMethods.WS_EX_APPWINDOW) == 0) return false;

        // Skip cloaked windows (UWP suspended, on another desktop, etc.)
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
            && cloaked != 0) return false;

        if (NativeMethods.GetWindowTextLength(hwnd) == 0) return false;

        var cls = new char[256];
        int len = NativeMethods.GetClassName(hwnd, cls, cls.Length);
        var name = new string(cls, 0, len);
        if (name is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW") return false;

        return true;
    }

    private static void ForceForegroundWindow(IntPtr hwnd)
    {
        uint currentThread = NativeMethods.GetCurrentThreadId();
        uint targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        bool attached = false;
        try
        {
            if (currentThread != targetThread)
                attached = NativeMethods.AttachThreadInput(currentThread, targetThread, true);
            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
                NativeMethods.AttachThreadInput(currentThread, targetThread, false);
        }
    }

    public List<DesktopInfo> GetDesktops()
    {
        if (VdaDll.IsLoaded)
        {
            try { return GetDesktopsViaVda(); }
            catch { }
        }
        return GetDesktopsViaRegistry();
    }

    private static List<DesktopInfo> GetDesktopsViaVda()
    {
        var result = new List<DesktopInfo>();
        int count = VdaDll.GetDesktopCount();
        int currentIdx = -1;
        try { currentIdx = VdaDll.GetCurrentDesktopNumber(); } catch { }

        // Names: VDA's GetDesktopName is ANSI and would mangle Unicode. Read from registry
        // (same source the desktop control panel writes to) for correctness on names like
        // "Mobilität". Falls back to "Desktop N" if registry doesn't have a name.
        var registryNames = ReadDesktopNamesFromRegistry();

        for (int i = 0; i < count; i++)
        {
            try
            {
                var id = VdaDll.GetDesktopIdByNumber(i);
                string name = $"Desktop {i + 1}";
                if (registryNames.TryGetValue(id, out var regName) && !string.IsNullOrEmpty(regName))
                    name = regName;

                result.Add(new DesktopInfo
                {
                    Index = i,
                    Id = id,
                    Name = name,
                    IsCurrent = i == currentIdx
                });
            }
            catch { }
        }
        return result;
    }

    private static Dictionary<Guid, string> ReadDesktopNamesFromRegistry()
    {
        var names = new Dictionary<Guid, string>();
        try
        {
            using var namesKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops\Desktops");
            if (namesKey == null) return names;

            foreach (var subKeyName in namesKey.GetSubKeyNames())
            {
                using var desktopKey = namesKey.OpenSubKey(subKeyName);
                if (desktopKey == null) continue;
                var name = desktopKey.GetValue("Name") as string;
                if (string.IsNullOrEmpty(name)) continue;

                // subKeyName is "{GUID}" format
                var guidStr = subKeyName.Trim('{', '}');
                if (Guid.TryParse(guidStr, out var guid))
                    names[guid] = name;
            }
        }
        catch { }
        return names;
    }

    private List<DesktopInfo> GetDesktopsViaRegistry()
    {
        var result = new List<DesktopInfo>();

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops");
            if (key == null) return result;

            var currentDesktopId = key.GetValue("CurrentVirtualDesktop") as byte[];
            Guid currentGuid = currentDesktopId != null && currentDesktopId.Length == 16
                ? new Guid(currentDesktopId) : Guid.Empty;

            var desktopIds = key.GetValue("VirtualDesktopIDs") as byte[];
            if (desktopIds == null || desktopIds.Length == 0) return result;

            int count = desktopIds.Length / 16;

            using var namesKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops\Desktops");

            for (int i = 0; i < count; i++)
            {
                byte[] guidBytes = new byte[16];
                Array.Copy(desktopIds, i * 16, guidBytes, 0, 16);
                var guid = new Guid(guidBytes);

                string name = $"Desktop {i + 1}";
                if (namesKey != null)
                {
                    using var desktopKey = namesKey.OpenSubKey($"{{{guid}}}") ??
                                           namesKey.OpenSubKey(guid.ToString());
                    if (desktopKey != null)
                    {
                        var regName = desktopKey.GetValue("Name") as string;
                        if (!string.IsNullOrEmpty(regName))
                            name = regName;
                    }
                }

                result.Add(new DesktopInfo
                {
                    Index = i,
                    Id = guid,
                    Name = name,
                    IsCurrent = guid == currentGuid
                });
            }
        }
        catch { }

        if (result.Count == 0)
        {
            result.Add(new DesktopInfo { Index = 0, Id = Guid.Empty, Name = "Desktop 1", IsCurrent = true });
        }

        return result;
    }

    public void SwitchToDesktop(DesktopInfo desktop)
    {
        // VDA path — instant, no animation. Same call AHK uses.
        if (VdaDll.IsLoaded)
        {
            try
            {
                VdaDll.GoToDesktopNumber(desktop.Index);
                FocusTopmostWindowOnCurrentDesktop();
                return;
            }
            catch { }
        }

        // Fallback: keyboard simulation
        SwitchViaKeyboard(desktop.Index);
        FocusTopmostWindowOnCurrentDesktop();
    }

    private static void SwitchViaKeyboard(int targetIndex)
    {
        int currentIndex = GetCurrentDesktopIndex();
        if (currentIndex < 0 || currentIndex == targetIndex) return;

        int diff = targetIndex - currentIndex;
        bool goRight = diff > 0;
        int steps = Math.Abs(diff);

        byte arrowKey = goRight ? NativeMethods.VK_RIGHT : NativeMethods.VK_LEFT;
        int inputSize = Marshal.SizeOf<NativeMethods.INPUT>();

        int totalInputs = 2 + (steps * 2) + 2;
        var inputs = new NativeMethods.INPUT[totalInputs];
        int idx = 0;

        inputs[idx++] = NativeMethods.CreateKeyInput(NativeMethods.VK_LCONTROL, false);
        inputs[idx++] = NativeMethods.CreateKeyInput(NativeMethods.VK_LWIN, false);

        for (int i = 0; i < steps; i++)
        {
            inputs[idx++] = NativeMethods.CreateKeyInput(arrowKey, false);
            inputs[idx++] = NativeMethods.CreateKeyInput(arrowKey, true);
        }

        inputs[idx++] = NativeMethods.CreateKeyInput(NativeMethods.VK_LWIN, true);
        inputs[idx++] = NativeMethods.CreateKeyInput(NativeMethods.VK_LCONTROL, true);

        NativeMethods.SendInput((uint)totalInputs, inputs, inputSize);
    }

    private static int GetCurrentDesktopIndex()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops");
            if (key == null) return -1;

            var currentId = key.GetValue("CurrentVirtualDesktop") as byte[];
            var allIds = key.GetValue("VirtualDesktopIDs") as byte[];
            if (currentId == null || allIds == null) return -1;

            int count = allIds.Length / 16;
            for (int i = 0; i < count; i++)
            {
                bool match = true;
                for (int j = 0; j < 16; j++)
                {
                    if (allIds[i * 16 + j] != currentId[j]) { match = false; break; }
                }
                if (match) return i;
            }
        }
        catch { }
        return -1;
    }

    internal void OnDesktopChanged()
    {
        DesktopsChanged?.Invoke();
    }

    public void Dispose() { }
}

internal sealed class DesktopInfo
{
    public int Index { get; set; }
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsCurrent { get; set; }
}
