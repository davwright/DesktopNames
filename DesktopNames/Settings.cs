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

    /// <summary>
    /// Desktops the user has manually marked blue (right-click → Blue highlight). A persistent
    /// user marker, distinct from the transient Claude status colors; survives restarts. A live
    /// Claude status color takes visual precedence over it.
    /// </summary>
    public List<Guid> HighlightDesktopGuids { get; set; } = new();

    /// <summary>
    /// Free-text reminders ("what am I doing on this desktop"). Shown in the tooltip
    /// when hovering the button. Empty/missing means no note. Key is desktop GUID.
    /// </summary>
    public Dictionary<Guid, string> DesktopNotes { get; set; } = new();

    /// <summary>
    /// Workspace folder (full path, lower-case) → desktop, set by dragging a session in the
    /// reassign flyout. Outranks the rootName-keyed <see cref="ScreenLayout.Workspaces"/>, which
    /// can't tell c:\git\evolx\osis_fixes from c:\git\osis-dev\osis_fixes.
    /// </summary>
    public Dictionary<string, Guid> FolderDesktops { get; set; } = new();

    public static string FolderKey(string folder) => folder.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

    /// <summary>Where the project switcher (Win+J) creates new project folders and lists existing ones.</summary>
    public string ProjectsRoot { get; set; } = @"C:\git\projects";

    /// <summary>
    /// Projects removed from the switcher list (<see cref="FolderKey"/> → when). A project
    /// reappears once it is used again after that, so removal only clears out the ancient ones.
    /// </summary>
    public Dictionary<string, DateTime> HiddenProjects { get; set; } = new();

    /// <summary>
    /// A project desktop (one VS Code has lived on) is removed once it has had no VS Code window
    /// and no Claude session for this long. 0 disables recycling.
    /// </summary>
    public int ProjectRecycleMinutes { get; set; } = 20;

    // VSCode workspace → last observed location (desktop + monitor + snap position), kept per
    // monitor arrangement. Always tracked passively: every scan CRUDs the entry to match where
    // the window currently lives, so manual moves via the user's AHK script (Win+Ctrl+N) become
    // the new binding. Auto-move on first sight restores from the current arrangement's layout.
    public bool VsCodeAutoMove { get; set; } = false;

    /// <summary>Screen-setup signature (<see cref="ScreenSetup.Signature"/>) → remembered layout.</summary>
    public Dictionary<string, ScreenLayout> VsCodeLayouts { get; set; } = new();

    /// <summary>Pre-multi-setup files stored one flat map. Migrated into <see cref="VsCodeLayouts"/> on load.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("VsCodeWorkspaceDesktops")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, WorkspaceLocation>? LegacyWorkspaceDesktops { get; set; }

    private ScreenLayout? _layout;
    private DateTime _layoutStamp;
    private static readonly TimeSpan LayoutCacheTtl = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The remembered layout for the monitor arrangement plugged in right now, created on first
    /// use. Callers just ask every time instead of tracking display changes themselves; the
    /// answer is cached briefly because resolving it enumerates monitors and their device ids.
    /// </summary>
    public ScreenLayout Layout()
    {
        if (_layout != null && DateTime.UtcNow - _layoutStamp < LayoutCacheTtl) return _layout;

        var screens = MonitorRef.EnumerateAll();
        string sig = ScreenSetup.SignatureOf(screens);
        bool created = !VsCodeLayouts.TryGetValue(sig, out var layout);
        if (created)
        {
            layout = AdoptOrSeed(screens, sig);
            VsCodeLayouts[sig] = layout;
        }
        layout!.Describe = ScreenSetup.DescribeOf(screens);
        layout.Screens = screens.Select(ScreenInfo.From).ToList();
        layout.DeviceSignature = ScreenSetup.DeviceSignatureOf(screens);
        _layout = layout;
        _layoutStamp = DateTime.UtcNow;
        // Persist a newly-seen arrangement right away, so its key doesn't depend on some later
        // window move happening to dirty the file. Re-entry is safe: the cache is already warm.
        if (created) Save();
        return layout;
    }

    /// <summary>
    /// First sight of an arrangement. Rather than starting blank, inherit from the closest
    /// previous one: the same physical monitors at different coordinates (you rearranged them
    /// in Windows) carry their window positions over as a starting point, which you can then
    /// change independently.
    /// </summary>
    private ScreenLayout AdoptOrSeed(List<MonitorDescriptor> screens, string sig)
    {
        // One-time upgrade: layouts written before coordinates were part of the key have no
        // recorded screens. If exactly one exists it was recorded on whatever is plugged in
        // now, so adopt it outright instead of orphaning its remembered positions.
        var stale = VsCodeLayouts.Where(kv => kv.Value.Screens.Count == 0).ToList();
        if (stale.Count == 1)
        {
            VsCodeLayouts.Remove(stale[0].Key);
            Log.Screens($"arrangement {sig} adopts pre-coordinate layout {stale[0].Key} ({stale[0].Value.Workspaces.Count} windows)");
            return stale[0].Value;
        }

        string family = ScreenSetup.DeviceSignatureOf(screens);
        var source = VsCodeLayouts.Values.FirstOrDefault(l => l.DeviceSignature == family);
        var seeded = new ScreenLayout { Name = source?.Name };
        if (source != null)
            foreach (var kvp in source.Workspaces) seeded.Workspaces[kvp.Key] = kvp.Value.Clone();

        Log.Screens($"new arrangement {sig} [{ScreenSetup.DescribeOf(screens)}]" +
                    (source != null ? $" seeded from same monitors rearranged ({seeded.Workspaces.Count} windows)" : " (nothing to seed from)"));
        return seeded;
    }

    /// <summary>Drop the cached layout so the next <see cref="Layout"/> re-resolves — called on
    /// WM_DISPLAYCHANGE so a dock/undock switches layouts immediately.</summary>
    public void InvalidateLayout() => _layout = null;

    /// <summary>Shorthand for the current arrangement's workspace → location map.</summary>
    public Dictionary<string, WorkspaceLocation> Workspaces => Layout().Workspaces;

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

    /// <summary>
    /// Master switch for the named-pipe listener that accepts Claude (and other apps)
    /// state updates to color taskbar buttons. Disable to opt out entirely.
    /// </summary>
    public bool AlertListenerEnabled { get; set; } = true;

    /// <summary>
    /// When false, state colors switch instantly without a 2.5s pulse-on-change animation.
    /// The slow Asking breathe also stops. Useful on slower machines or for users who
    /// find motion distracting.
    /// </summary>
    public bool AlertPulseEnabled { get; set; } = true;

    /// <summary>
    /// Play a short DTMF tone when a desktop enters the Asking state (a Claude is waiting
    /// for an answer). The dialled digit is the desktop's number, so the pitch tells you
    /// which desktop to switch to without looking. Set false to silence.
    /// </summary>
    public bool AlertAskingChimeEnabled { get; set; } = true;

    /// <summary>
    /// Which chime to play. "notes" = a pleasant ascending pentatonic rise of N notes for
    /// desktop N (countable by ear). "dtmf" = a single phone-keypad tone whose pitch encodes
    /// the desktop number. Anything else falls back to "notes".
    /// </summary>
    public string AlertAskingChimeStyle { get; set; } = "notes";

    /// <summary>Button background when a session is busy. Pale peach — soft enough to
    /// not yell, distinct enough from the other two states.</summary>
    public string AlertBusyColor { get; set; } = "#F4B483";

    /// <summary>
    /// Button background when a session is asking. Pale yellow — chosen for visible
    /// contrast against the busy peach so the user can glance and distinguish
    /// "Claude is working" from "needs your input".
    /// </summary>
    public string AlertAskingColor { get; set; } = "#FFE680";

    /// <summary>Button background when a session is ready and none higher. Pale mint.</summary>
    public string AlertReadyColor { get; set; } = "#A8D8B0";

    /// <summary>Background for a manually highlighted desktop. Medium blue — clearly a user
    /// marker, not one of the muted-pastel status colors.</summary>
    public string AlertHighlightColor { get; set; } = "#5B9BD5";

    /// <summary>
    /// Button background when Claude stopped with an error (rate-limit, auth, billing,
    /// network). Pale coral red — clearly distinct from the busy peach but in the same
    /// muted family. The errorType field on the wire carries the specific cause and is
    /// surfaced via the tooltip body.
    /// </summary>
    public string AlertErrorColor { get; set; } = "#E08585";

    /// <summary>
    /// Append per-message diagnostics to %APPDATA%\DesktopNames\desktopnames.log. Useful
    /// while the integration is still maturing — pair with claudehook.log to debug
    /// resolver mismatches. Off by default once shipped.
    /// </summary>
    public bool AlertLogEnabled { get; set; } = true;

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
            ["OpenCurrentDesktopMenu"] = "Win+Insert",  // opens the per-desktop context menu
            ["ToggleBlueHighlight"] = "Win+Shift+Insert",     // current desktop; Shift+click does it on any tab
            ["ClearHighlight"]      = "Win+Ctrl+Shift+Insert", // current desktop; Ctrl+Shift+click does it on any tab
            ["PrevWaitingDesktop"] = "Win+Oem4",   // Win+[ — previous desktop with an asking/ready Claude
            ["NextWaitingDesktop"] = "Win+Oem6",   // Win+] — next desktop with an asking/ready Claude
            ["OpenProjects"]       = "Win+J",      // project switcher: open / create a project on its own desktop
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

        // Migration: the flat workspace map predates per-screen-setup layouts. Adopt it as the
        // layout for whatever arrangement is plugged in now — that's the setup it was recorded on.
        if (s.LegacyWorkspaceDesktops is { Count: > 0 })
        {
            var layout = s.Layout();
            foreach (var kvp in s.LegacyWorkspaceDesktops)
                if (!layout.Workspaces.ContainsKey(kvp.Key))
                    layout.Workspaces[kvp.Key] = kvp.Value;
        }
        s.LegacyWorkspaceDesktops = null;

        // Migration: an earlier build defaulted NextWaitingDesktop to "Win+Oem3", which
        // collides with Windows Terminal's quake-mode. Wipe that specific stale value so
        // the current default (Win+]) takes effect for upgrading users.
        if (s.Hotkeys.TryGetValue("NextWaitingDesktop", out var oldNwd) && oldNwd == "Win+Oem3")
            s.Hotkeys.Remove("NextWaitingDesktop");

        // Migration: state colors have been re-defaulted twice (saturated → vivid → pale).
        // Replace any of the previous defaults with the current pale palette; user-customized
        // values are left alone.
        if (s.AlertBusyColor   is "#D97A1F") s.AlertBusyColor   = "#F4B483";
        if (s.AlertAskingColor is "#E8C547" or "#FFD93B") s.AlertAskingColor = "#FFE680";
        if (s.AlertReadyColor  is "#3FA34D") s.AlertReadyColor  = "#A8D8B0";

        // Migration: the Win+Insert hotkey was repurposed from inline-rename to
        // "open the per-desktop context menu". Move any custom binding from the old key
        // to the new key so user customisations survive the rename.
        if (s.Hotkeys.TryGetValue("RenameCurrentDesktop", out var oldRcd))
        {
            if (!s.Hotkeys.ContainsKey("OpenCurrentDesktopMenu"))
                s.Hotkeys["OpenCurrentDesktopMenu"] = oldRcd;
            s.Hotkeys.Remove("RenameCurrentDesktop");
        }

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

    public bool IsDesktopHighlighted(Guid desktopId) => HighlightDesktopGuids.Contains(desktopId);

    public void ToggleDesktopHighlight(Guid desktopId)
    {
        if (!HighlightDesktopGuids.Remove(desktopId))
            HighlightDesktopGuids.Add(desktopId);
        Save();
    }

    public bool IsDesktopHidden(Guid desktopId) => HiddenDesktopGuids.Contains(desktopId);

    public void ToggleDesktopHidden(Guid desktopId)
    {
        if (!HiddenDesktopGuids.Remove(desktopId))
            HiddenDesktopGuids.Add(desktopId);
        Save();
    }

    public string GetDesktopNote(Guid desktopId)
        => DesktopNotes.TryGetValue(desktopId, out var n) ? n : "";

    public void SetDesktopNote(Guid desktopId, string note)
    {
        note = (note ?? "").Trim();
        if (note.Length == 0) DesktopNotes.Remove(desktopId);
        else DesktopNotes[desktopId] = note;
        Save();
    }
}
