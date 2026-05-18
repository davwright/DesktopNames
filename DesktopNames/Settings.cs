using System.Text.Json;

namespace DesktopNames;

/// <summary>
/// User-editable settings persisted to %APPDATA%\DesktopNames\settings.json.
/// Hidden = global toggle (Win+Alt+H). OnlyOnMainDesktop = show only when current desktop is index 0.
/// HiddenDesktopGuids = desktops on which the overlay is suppressed.
/// </summary>
internal sealed class Settings
{
    public bool Hidden { get; set; }
    public bool OnlyOnMainDesktop { get; set; }
    public List<Guid> HiddenDesktopGuids { get; set; } = new();

    // VSCode workspace → last observed location (desktop + monitor). Always tracked
    // passively: every scan CRUDs the entry to match where the window currently lives,
    // so manual moves via the user's AHK script (Win+Ctrl+N) become the new binding.
    // Auto-move on first sight restores from this map.
    public bool VsCodeAutoMove { get; set; } = false;
    public Dictionary<string, WorkspaceLocation> VsCodeWorkspaceDesktops { get; set; } = new();

    /// <summary>
    /// Override path to VirtualDesktopAccessor.dll. Empty/null = auto-discover.
    /// Set this if the DLL lives somewhere VdaDll.ResolveDllPath() doesn't search.
    /// </summary>
    public string? VdaDllPath { get; set; }

    /// <summary>
    /// Windows build (CurrentBuild.UBR, e.g. "26200.8457") that the VDA self-test
    /// last passed on. If the current build differs at startup, VdaDll.SelfTest()
    /// runs again before destructive operations are trusted.
    /// </summary>
    public string? VerifiedBuild { get; set; }

    /// <summary>
    /// True if the VDA self-test passed on <see cref="VerifiedBuild"/>. Falsified
    /// when a self-test fails so the next startup retries even if the build hasn't
    /// changed (transient failures get a second chance).
    /// </summary>
    public bool LastTestedBuildOk { get; set; }

    // Hotkey bindings. Strings parsed by HotkeyParser. Set a value to "" to disable a hotkey.
    public Dictionary<string, string> Hotkeys { get; set; } = BuildDefaultHotkeys();

    private static Dictionary<string, string> BuildDefaultHotkeys()
    {
        var d = new Dictionary<string, string>
        {
            ["MoveDesktopLeft"]  = "Win+Alt+Left",
            ["MoveDesktopRight"] = "Win+Alt+Right",
            ["MoveDesktopFirst"] = "Win+Alt+Home",
            ["MoveDesktopLast"]  = "Win+Alt+End",
            ["ToggleHide"]       = "Win+Alt+H",
        };
        // Win+Ctrl+1..9,0 → desktops 1..10. Add Shift → desktops 11..20.
        for (int i = 1; i <= 20; i++)
        {
            int digit = i % 10; // 1..9 then 0 for 10, 1..9 then 0 for 20
            string shift = i > 10 ? "Shift+" : "";
            d[$"SwitchToDesktop{i}"] = $"Win+Ctrl+{shift}{digit}";
        }
        return d;
    }

    public event Action? Changed;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopNames", "settings.json");

    private static JsonSerializerOptions JsonOptions()
    {
        var o = new JsonSerializerOptions { WriteIndented = true };
        o.Converters.Add(new WorkspaceLocationJsonConverter());
        return o;
    }

    public static Settings Load()
    {
        Settings s;
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                s = JsonSerializer.Deserialize<Settings>(json, JsonOptions()) ?? new Settings();
            }
            else s = new Settings();
        }
        catch { s = new Settings(); }

        // Fill in any hotkey keys the user's older settings file didn't have.
        var defaults = new Settings().Hotkeys;
        foreach (var kvp in defaults)
            if (!s.Hotkeys.ContainsKey(kvp.Key))
                s.Hotkeys[kvp.Key] = kvp.Value;

        return s;
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, JsonOptions());
            File.WriteAllText(FilePath, json);
        }
        catch { }
        Changed?.Invoke();
    }

    public bool IsDesktopHidden(Guid desktopId) => HiddenDesktopGuids.Contains(desktopId);

    public void ToggleDesktopHidden(Guid desktopId)
    {
        if (!HiddenDesktopGuids.Remove(desktopId))
            HiddenDesktopGuids.Add(desktopId);
        Save();
    }
}
