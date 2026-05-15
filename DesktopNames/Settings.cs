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

    // VSCode workspace → desktop GUID. Always tracked passively. The auto-move side
    // uses an undocumented COM API that can fault on certain Windows builds — opt-in only.
    public bool VsCodeAutoMove { get; set; } = false;
    public Dictionary<string, Guid> VsCodeWorkspaceDesktops { get; set; } = new();

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
