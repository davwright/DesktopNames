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

    // VSCode workspace → desktop GUID. Tracked + auto-restored by VsCodeTracker.
    public Dictionary<string, Guid> VsCodeWorkspaceDesktops { get; set; } = new();

    // Hotkey bindings. Strings parsed by HotkeyParser. Set a value to "" to disable a hotkey.
    public Dictionary<string, string> Hotkeys { get; set; } = new()
    {
        ["MoveDesktopLeft"]  = "Win+Alt+Left",
        ["MoveDesktopRight"] = "Win+Alt+Right",
        ["MoveDesktopFirst"] = "Win+Alt+Home",
        ["MoveDesktopLast"]  = "Win+Alt+End",
        ["ToggleHide"]       = "Win+Alt+H",
    };

    public event Action? Changed;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopNames", "settings.json");

    public static Settings Load()
    {
        Settings s;
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                s = JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
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
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
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
