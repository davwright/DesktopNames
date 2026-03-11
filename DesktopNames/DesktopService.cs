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

    public event Action? DesktopsChanged;

    public bool Initialize()
    {
        try
        {
            _manager = VirtualDesktopInterop.GetManagerInternal();
        }
        catch
        {
            // COM not available, fall back to registry
        }
        return true;
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
                return;
            }
            catch { }
        }

        // Fallback: keyboard simulation
        SwitchViaKeyboard(desktop.Index);
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
