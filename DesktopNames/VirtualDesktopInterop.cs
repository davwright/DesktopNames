using System.Runtime.InteropServices;

namespace DesktopNames;

// COM interface definitions for Windows 11 Virtual Desktop management.
// Based on Ciantic/VirtualDesktopAccessor source code.
// GUIDs target Windows 11 22H2/23H2/24H2.

internal static class VirtualDesktopInterop
{
    private static readonly Guid CLSID_ImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid CLSID_VirtualDesktopManagerInternal = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");

    public static IVirtualDesktopManagerInternal? GetManagerInternal()
    {
        var shell = (IServiceProvider10?)Activator.CreateInstance(
            Type.GetTypeFromCLSID(CLSID_ImmersiveShell)!);
        if (shell == null) return null;

        var iid = typeof(IVirtualDesktopManagerInternal).GUID;
        var clsid = CLSID_VirtualDesktopManagerInternal;
        shell.QueryService(ref clsid, ref iid, out var obj);
        return obj as IVirtualDesktopManagerInternal;
    }
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
internal interface IServiceProvider10
{
    [PreserveSig]
    int QueryService(ref Guid guidService, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
}

// IVirtualDesktop - matches VDA vtable for Win11 22H2+
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("3F07F4BE-B107-441A-AF0F-39D82529072C")]
internal interface IVirtualDesktop
{
    bool IsViewVisible(object pView);
    Guid GetID();
    IntPtr GetMonitor();
    [return: MarshalAs(UnmanagedType.HString)]
    string GetName();
    [return: MarshalAs(UnmanagedType.HString)]
    string GetWallpaperPath();
    bool IsRemote();
}

// IVirtualDesktopManagerInternal - exact vtable matching VDA source
// SwitchDesktop (slot 6) is INSTANT - no animation
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("53F5CA0B-158F-4124-900C-057158060B27")]
internal interface IVirtualDesktopManagerInternal
{
    // [0]
    int GetCount();
    // [1]
    void MoveViewToDesktop(object pView, IVirtualDesktop desktop);
    // [2]
    bool CanViewMoveDesktops(object pView);
    // [3]
    IVirtualDesktop GetCurrentDesktop();
    // [4]
    IObjectArray GetDesktops();
    // [5]
    [PreserveSig]
    int GetAdjacentDesktop(IVirtualDesktop pDesktopReference, int uDirection, out IVirtualDesktop ppAdjacentDesktop);
    // [6] - INSTANT switch, no animation
    void SwitchDesktop(IVirtualDesktop desktop);
    // [7]
    void SwitchDesktopAndMoveForegroundView(IVirtualDesktop desktop);
    // [8]
    IVirtualDesktop CreateDesktop();
    // [9]
    void MoveDesktop(IVirtualDesktop desktop, int nIndex);
    // [10]
    void RemoveDesktop(IVirtualDesktop pRemove, IVirtualDesktop pFallbackDesktop);
    // [11]
    IVirtualDesktop FindDesktop(ref Guid desktopId);
    // [12]
    void GetDesktopSwitchIncludeExcludeViews(IVirtualDesktop desktop, out object o1, out object o2);
    // [13]
    void SetDesktopName(IVirtualDesktop desktop, [MarshalAs(UnmanagedType.HString)] string name);
    // [14]
    void SetDesktopWallpaper(IVirtualDesktop desktop, [MarshalAs(UnmanagedType.HString)] string path);
    // [15]
    void UpdateWallpaperPathForAllDesktops([MarshalAs(UnmanagedType.HString)] string path);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
internal interface IObjectArray
{
    uint GetCount();
    [return: MarshalAs(UnmanagedType.Interface)]
    object GetAt(uint index, ref Guid riid);
}
