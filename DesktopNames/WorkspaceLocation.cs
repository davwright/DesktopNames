using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNames;

/// <summary>
/// What we remember about a VS Code workspace's last-observed location.
/// Replaces the old "Dictionary&lt;workspace, Guid&gt;" value type so we can record both
/// the virtual desktop and the physical monitor.
///
/// Serialization is backwards-compatible: <see cref="WorkspaceLocationJsonConverter"/>
/// reads legacy entries that are just a bare GUID string (pre-monitor-tracking).
/// </summary>
internal sealed class WorkspaceLocation
{
    public Guid DesktopId { get; set; }

    /// <summary>Hardware-stable monitor id (EnumDisplayDevices DeviceID). Null if unknown.</summary>
    public string? MonitorDeviceId { get; set; }

    /// <summary>Monitor rect at observation time. Fallback identifier when the device id
    /// isn't found in the current setup (different physical monitors at home vs. work).</summary>
    public int MonitorX { get; set; }
    public int MonitorY { get; set; }
    public int MonitorWidth { get; set; }
    public int MonitorHeight { get; set; }

    /// <summary>
    /// Window position relative to the source monitor's *work area* top-left.
    /// On restore: target_monitor.work.Left + WindowOffsetX, target_monitor.work.Top + WindowOffsetY.
    /// Using relative-to-work-area (not raw screen coords) keeps the data portable
    /// across machines with different monitor positions in the virtual screen.
    /// </summary>
    public int WindowOffsetX { get; set; }
    public int WindowOffsetY { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }

    /// <summary>
    /// How the window occupied its monitor: full screen, a snapped half/quarter, or Free
    /// (use the offset/size above). Replaces the older WindowMaximized bool, which is still
    /// read from legacy settings files and mapped onto <see cref="SnapMode.Max"/>.
    /// </summary>
    public SnapMode Snap { get; set; }

    public WorkspaceLocation Clone() => (WorkspaceLocation)MemberwiseClone();
}

/// <summary>
/// Everything remembered for one monitor arrangement (see <see cref="ScreenSetup"/>): the
/// user's label for it plus the per-workspace positions. Keyed in
/// <see cref="Settings.VsCodeLayouts"/> by the arrangement's signature, so the layout you
/// use docked at work isn't overwritten by the one you use on the laptop alone.
/// </summary>
internal sealed class ScreenLayout
{
    /// <summary>User-supplied label ("work", "home", "single"). Falls back to a generated description.</summary>
    public string? Name { get; set; }

    /// <summary>Auto-generated description of the arrangement, e.g. "2 screens · 3840×2160 + 1920×1080".</summary>
    public string? Describe { get; set; }

    /// <summary>
    /// The monitors this arrangement consists of, with the coordinates they sat at — the array
    /// the arrangement key is built from. Rewritten every time the arrangement is resolved, so
    /// it also records what "screen 2" meant for the positions stored below.
    /// </summary>
    public List<ScreenInfo> Screens { get; set; } = new();

    /// <summary>Position-independent key for <see cref="Screens"/>: which monitors, ignoring
    /// where they sit. Lets a rearranged setup start from the layout of the same monitors.</summary>
    public string? DeviceSignature { get; set; }

    public Dictionary<string, WorkspaceLocation> Workspaces { get; set; } = new();

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? (Describe ?? "unknown setup") : Name!;
}

/// <summary>One monitor as it stood when an arrangement was recorded.</summary>
internal sealed class ScreenInfo
{
    public string? DeviceId { get; set; }
    public string? Model { get; set; }
    /// <summary>Windows' Settings → Display number.</summary>
    public int Number { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsPrimary { get; set; }

    public static ScreenInfo From(MonitorDescriptor m) => new()
    {
        DeviceId = m.DeviceId,
        Model = m.Model,
        Number = m.Number,
        X = m.Monitor.Left,
        Y = m.Monitor.Top,
        Width = m.Width,
        Height = m.Height,
        IsPrimary = m.IsPrimary,
    };
}

/// <summary>
/// Accepts either a bare GUID string (legacy) or an object with DesktopId + monitor fields.
/// Always writes the object form.
/// </summary>
internal sealed class WorkspaceLocationJsonConverter : JsonConverter<WorkspaceLocation>
{
    public override WorkspaceLocation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            return new WorkspaceLocation { DesktopId = Guid.TryParse(s, out var g) ? g : Guid.Empty };
        }
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"Unexpected token {reader.TokenType} for WorkspaceLocation");

        var result = new WorkspaceLocation();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return result;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var prop = reader.GetString();
            reader.Read();
            switch (prop)
            {
                case "DesktopId":       result.DesktopId       = Guid.Parse(reader.GetString()!); break;
                case "MonitorDeviceId": result.MonitorDeviceId = reader.TokenType == JsonTokenType.Null ? null : reader.GetString(); break;
                case "MonitorX":        result.MonitorX        = reader.GetInt32(); break;
                case "MonitorY":        result.MonitorY        = reader.GetInt32(); break;
                case "MonitorWidth":    result.MonitorWidth    = reader.GetInt32(); break;
                case "MonitorHeight":   result.MonitorHeight   = reader.GetInt32(); break;
                case "WindowOffsetX":   result.WindowOffsetX   = reader.GetInt32(); break;
                case "WindowOffsetY":   result.WindowOffsetY   = reader.GetInt32(); break;
                case "WindowWidth":     result.WindowWidth     = reader.GetInt32(); break;
                case "WindowHeight":    result.WindowHeight    = reader.GetInt32(); break;
                case "Snap":            result.Snap            = Enum.TryParse<SnapMode>(reader.GetString(), out var sm) ? sm : SnapMode.Free; break;
                // Legacy: pre-snap files only recorded "was it maximized".
                case "WindowMaximized": if (reader.GetBoolean() && result.Snap == SnapMode.Free) result.Snap = SnapMode.Max; break;
                default:                reader.Skip();         break;
            }
        }
        throw new JsonException("Unexpected end of stream in WorkspaceLocation");
    }

    public override void Write(Utf8JsonWriter writer, WorkspaceLocation value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("DesktopId", value.DesktopId.ToString());
        if (value.MonitorDeviceId != null) writer.WriteString("MonitorDeviceId", value.MonitorDeviceId);
        if (value.MonitorWidth > 0 || value.MonitorHeight > 0)
        {
            writer.WriteNumber("MonitorX", value.MonitorX);
            writer.WriteNumber("MonitorY", value.MonitorY);
            writer.WriteNumber("MonitorWidth", value.MonitorWidth);
            writer.WriteNumber("MonitorHeight", value.MonitorHeight);
        }
        if (value.WindowWidth > 0 || value.WindowHeight > 0)
        {
            writer.WriteNumber("WindowOffsetX", value.WindowOffsetX);
            writer.WriteNumber("WindowOffsetY", value.WindowOffsetY);
            writer.WriteNumber("WindowWidth",   value.WindowWidth);
            writer.WriteNumber("WindowHeight",  value.WindowHeight);
        }
        if (value.Snap != SnapMode.Free) writer.WriteString("Snap", value.Snap.ToString());
        writer.WriteEndObject();
    }
}
