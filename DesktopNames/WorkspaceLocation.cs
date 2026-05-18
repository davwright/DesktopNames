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
        writer.WriteEndObject();
    }
}
