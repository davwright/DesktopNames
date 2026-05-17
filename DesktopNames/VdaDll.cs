using System.Runtime.InteropServices;

namespace DesktopNames;

/// <summary>
/// P/Invoke wrappers for Ciantic's VirtualDesktopAccessor.dll
/// (https://github.com/Ciantic/VirtualDesktopAccessor).
///
/// The DLL ships per-build vtable definitions for the undocumented
/// IVirtualDesktopManagerInternal COM interface and is kept current upstream.
/// We delegate the Chromium-AV-prone operations (MoveWindowToDesktop especially)
/// to the DLL instead of hand-rolled C# interop in <see cref="VirtualDesktopInterop"/>.
///
/// Must be initialized via <see cref="Initialize"/> before any extern call.
/// </summary>
internal static class VdaDll
{
    private const string DLL_NAME = "VirtualDesktopAccessor.dll";

    public static string? LoadedFrom { get; private set; }
    public static string? LoadError { get; private set; }
    public static bool IsLoaded { get; private set; }

    /// <summary>
    /// Resolve and pre-load the DLL so subsequent [DllImport] calls bind to
    /// our chosen copy regardless of CWD/PATH. Returns true on success.
    /// </summary>
    public static bool Initialize(string? settingsOverride)
    {
        var path = ResolveDllPath(settingsOverride);
        if (path == null)
        {
            LoadError = $"{DLL_NAME} not found in any known location";
            return false;
        }

        try
        {
            NativeLibrary.SetDllImportResolver(typeof(VdaDll).Assembly, (name, asm, search) =>
            {
                if (string.Equals(name, DLL_NAME, StringComparison.OrdinalIgnoreCase) &&
                    NativeLibrary.TryLoad(path, out var handle))
                {
                    return handle;
                }
                return IntPtr.Zero;
            });

            // Smoke test. GetDesktopCount is read-only and cheap; if the DLL is
            // broken or the wrong arch, this will throw.
            int n = GetDesktopCount();
            if (n < 1 || n > 100)
            {
                LoadError = $"GetDesktopCount returned implausible value {n}";
                return false;
            }

            LoadedFrom = path;
            IsLoaded = true;
            return true;
        }
        catch (Exception ex)
        {
            LoadError = $"Failed to load {DLL_NAME} from '{path}': {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Search order:
    ///   1. Explicit override (typically from Settings.VdaDllPath)
    ///   2. Same directory as DesktopNames.exe (bundled copy)
    ///   3. %OneDriveCommercial%\Dokumente\AutoHotkey\ (German locale, this user's setup)
    ///   4. %OneDriveCommercial%\Documents\AutoHotkey\
    ///   5. %OneDrive%\Dokumente|Documents\AutoHotkey\
    ///   6. %USERPROFILE%\Documents\AutoHotkey\
    /// </summary>
    public static string? ResolveDllPath(string? settingsOverride)
    {
        if (!string.IsNullOrEmpty(settingsOverride) && File.Exists(settingsOverride))
            return settingsOverride;

        var local = Path.Combine(AppContext.BaseDirectory, DLL_NAME);
        if (File.Exists(local)) return local;

        foreach (var envName in new[] { "OneDriveCommercial", "OneDrive" })
        {
            var root = Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrEmpty(root)) continue;
            foreach (var docs in new[] { "Dokumente", "Documents" })
            {
                var p = Path.Combine(root, docs, "AutoHotkey", DLL_NAME);
                if (File.Exists(p)) return p;
            }
        }

        var myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var ahkLocal = Path.Combine(myDocs, "AutoHotkey", DLL_NAME);
        if (File.Exists(ahkLocal)) return ahkLocal;

        return null;
    }

    // --- Exports we use ---
    //
    // All marked SuppressUnmanagedCodeSecurity-equivalent by default (no managed
    // PreserveSig wrapping needed — VDA returns ints or void, no HRESULTs).
    // GUID is blittable so it marshals as a 16-byte struct.

    [DllImport(DLL_NAME)] public static extern int GetDesktopCount();
    [DllImport(DLL_NAME)] public static extern int GetCurrentDesktopNumber();
    [DllImport(DLL_NAME)] public static extern int GetWindowDesktopNumber(IntPtr hwnd);
    [DllImport(DLL_NAME)] public static extern Guid GetWindowDesktopId(IntPtr hwnd);
    [DllImport(DLL_NAME)] public static extern Guid GetDesktopIdByNumber(int n);
    [DllImport(DLL_NAME)] public static extern int IsWindowOnCurrentVirtualDesktop(IntPtr hwnd);
    [DllImport(DLL_NAME)] public static extern int MoveWindowToDesktopNumber(IntPtr hwnd, int n);
    [DllImport(DLL_NAME)] public static extern void GoToDesktopNumber(int n);
    [DllImport(DLL_NAME)] public static extern int CreateDesktop();
    [DllImport(DLL_NAME)] public static extern void RemoveDesktop(int removeIdx, int fallbackIdx);
}
