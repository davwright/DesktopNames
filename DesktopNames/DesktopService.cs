using System.Runtime.InteropServices;

namespace DesktopNames;

/// <summary>
/// Virtual desktop management using COM interfaces directly.
/// SwitchDesktop is instant (no animation) - same method as VirtualDesktopAccessor.dll uses.
/// Falls back to registry + keyboard if COM fails.
/// </summary>
internal sealed class DesktopService : IDisposable
{
    private IVirtualDesktopManagerInternal? _manager;
    private IVirtualDesktopManager? _managerPublic;
    private IApplicationViewCollection? _appViewCollection;

    public event Action? DesktopsChanged;

    public bool Initialize()
    {
        try { _manager = VirtualDesktopInterop.GetManagerInternal(); } catch { }
        try { _managerPublic = VirtualDesktopInterop.GetManagerPublic(); } catch { }
        try { _appViewCollection = VirtualDesktopInterop.GetAppViewCollection(); } catch { }
        return true;
    }

    public void CreateDesktop()
    {
        if (_manager == null) return;
        try { _manager.CreateDesktop(); DesktopsChanged?.Invoke(); } catch { }
    }

    public void RemoveCurrentDesktop()
    {
        if (_manager == null) return;
        try
        {
            int count = _manager.GetCount();
            if (count <= 1) return;
            var current = _manager.GetCurrentDesktop();
            var iidDesktop = typeof(IVirtualDesktop).GUID;
            IObjectArray desktops = _manager.GetDesktops();
            int currentIdx = GetCurrentDesktopIndex();
            int fallbackIdx = currentIdx > 0 ? currentIdx - 1 : 1;
            var fallback = (IVirtualDesktop)desktops.GetAt((uint)fallbackIdx, ref iidDesktop);
            _manager.RemoveDesktop(current, fallback);
            DesktopsChanged?.Invoke();
        }
        catch { }
    }

    public void RenameDesktop(Guid desktopId, string newName)
    {
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
    /// Move an arbitrary window to a given virtual desktop. Returns true on success.
    /// Uses IApplicationViewCollection + IVirtualDesktopManagerInternal::MoveViewToDesktop —
    /// the only path that works for windows we don't own.
    /// </summary>
    public bool MoveWindowToDesktop(IntPtr hwnd, Guid desktopId)
    {
        if (_manager == null || _appViewCollection == null || desktopId == Guid.Empty || hwnd == IntPtr.Zero)
            return false;
        try
        {
            int hr = _appViewCollection.GetViewForHwnd(hwnd, out var view);
            if (hr != 0 || view == null) return false;
            var target = _manager.FindDesktop(ref desktopId);
            if (target == null) return false;
            _manager.MoveViewToDesktop(view, target);
            return true;
        }
        catch { return false; }
    }

    public Guid GetDesktopForWindow(IntPtr hwnd)
    {
        if (_managerPublic == null || hwnd == IntPtr.Zero) return Guid.Empty;
        try
        {
            int hr = _managerPublic.GetWindowDesktopId(hwnd, out var id);
            return hr == 0 ? id : Guid.Empty;
        }
        catch { return Guid.Empty; }
    }

    public Guid GetCurrentDesktopId()
    {
        if (_manager != null)
        {
            try { return _manager.GetCurrentDesktop().GetID(); }
            catch { }
        }
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

    public int GetCurrentDesktopIndexPublic()
    {
        return GetCurrentDesktopIndex();
    }

    private IVirtualDesktop? GetCurrentDesktopCom()
    {
        if (_manager == null) return null;
        try { return _manager.GetCurrentDesktop(); }
        catch { return null; }
    }

    public void MoveCurrentDesktopBy(int delta)
    {
        if (_manager == null || delta == 0) return;
        try
        {
            int count = _manager.GetCount();
            int currentIdx = GetCurrentDesktopIndex();
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
        try { MoveCurrentDesktopToIndex(_manager.GetCount() - 1); }
        catch { }
    }

    private void MoveCurrentDesktopToIndex(int targetIndex)
    {
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

    private bool IsOnCurrentDesktop(IntPtr hwnd)
    {
        if (_managerPublic == null) return true;
        try
        {
            int hr = _managerPublic.IsWindowOnCurrentVirtualDesktop(hwnd, out bool on);
            return hr == 0 && on;
        }
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
        // Try COM first
        if (_manager != null)
        {
            try
            {
                return GetDesktopsViaCom();
            }
            catch { }
        }

        // Fall back to registry
        return GetDesktopsViaRegistry();
    }

    private List<DesktopInfo> GetDesktopsViaCom()
    {
        var result = new List<DesktopInfo>();
        int count = _manager!.GetCount();
        IObjectArray desktops = _manager.GetDesktops();

        Guid currentId = Guid.Empty;
        try
        {
            var current = _manager.GetCurrentDesktop();
            currentId = current.GetID();
        }
        catch { }

        // Read names from registry - COM GetName() vtable position varies by build
        var registryNames = ReadDesktopNamesFromRegistry();

        var iidDesktop = typeof(IVirtualDesktop).GUID;
        for (uint i = 0; i < count; i++)
        {
            try
            {
                var desktop = (IVirtualDesktop)desktops.GetAt(i, ref iidDesktop);
                var id = desktop.GetID();

                // Look up name from registry by GUID
                string name = $"Desktop {i + 1}";
                if (registryNames.TryGetValue(id, out var regName) && !string.IsNullOrEmpty(regName))
                    name = regName;

                result.Add(new DesktopInfo
                {
                    Index = (int)i,
                    Id = id,
                    Name = name,
                    IsCurrent = id == currentId
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
        // Try COM - instant switch, no animation
        if (_manager != null)
        {
            try
            {
                var iidDesktop = typeof(IVirtualDesktop).GUID;
                IObjectArray desktops = _manager.GetDesktops();
                var target = (IVirtualDesktop)desktops.GetAt((uint)desktop.Index, ref iidDesktop);
                _manager.SwitchDesktop(target); // Instant, no animation
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
