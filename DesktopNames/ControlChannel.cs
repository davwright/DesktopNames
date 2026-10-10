using System.Text.Json;

namespace DesktopNames;

/// <summary>
/// Lets an agent drive and see DesktopNames without touching the user's keyboard or mouse. Arrives
/// on the alert pipe as one JSON line <c>{"type":"control","method":…,"params":{…}}</c> and gets one
/// reply line <c>{"ok":true,"result":…}</c> / <c>{"ok":false,"error":"…"}</c>. Methods are commands
/// in their own registry, so their params are checked the same strict way: an unknown key is an
/// error that lists the accepted ones. Everything runs on the UI thread.
/// </summary>
internal sealed class ControlChannel
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HostForm _host;
    private readonly CommandRegistry _methods = new();

    public ControlChannel(HostForm host)
    {
        _host = host;
        string[] none = Array.Empty<string>();
        _methods.Add(new("commands", "List every command with its params", none, "",
            _ => _host.Commands.All.Select(c => new { c.Id, c.Label, @params = c.ParamsDoc })));
        _methods.Add(new("execute", "Run one command", new[] { "command", "params" }, "command: id; params: object",
            a => _host.Commands.Execute(a.RequiredStr("command"), a.Raw("params"))));
        _methods.Add(new("inspect", "Desktops, Claude sessions and open windows", none, "", _ => Inspect()));
        _methods.Add(new("ui.open", "Open a window", new[] { "window" }, "window: projects", a =>
        {
            RequireWindowName(a);
            return _host.Commands.Execute("projects.open", (JsonElement?)null);
        }));
        _methods.Add(new("ui.close", "Close a window", new[] { "window" }, "window: projects", a =>
        {
            RequireWindowName(a);
            ProjectsWindow().Close();
            return null;
        }));
        _methods.Add(new("ui.rows", "The rows the DesktopNames window shows", none, "", _ => ProjectsWindow().VisibleRows()));
        _methods.Add(new("ui.hover", "Hover a cell or a column header of the DesktopNames window",
            new[] { "row", "column", "header" }, "row: 0-based visible row + column: header text, or header: header text", a =>
            {
                var w = ProjectsWindow();
                if (a.Str("header") is { } header) return w.HoverHeader(header);
                return new { tooltip = w.HoverCell(a.RequiredInt("row"), a.RequiredStr("column")) };
            }));
        _methods.Add(new("ui.screenshot", "PNG of a window, captured by its handle",
            new[] { "window", "path" }, "window: projects | screen-popup | overlay; path: absolute .png", a =>
            {
                string path = a.RequiredStr("path");
                if (!Path.IsPathRooted(path)) throw new ArgumentException("\"path\" must be absolute");
                IntPtr hwnd = a.RequiredStr("window") switch
                {
                    "projects" => ProjectsWindow().Handle,
                    "screen-popup" => ProjectsWindow().ScreenPopupHandle,
                    "overlay" => _host.Overlays.FirstOrDefault()?.Handle ?? throw new InvalidOperationException("no overlay"),
                    var other => throw new ArgumentException($"unknown window \"{other}\"; expected: projects, screen-popup, overlay"),
                };
                var size = Capture(hwnd, path);
                return new { path, width = size.Width, height = size.Height };
            }));
    }

    /// <summary>Handle one control request line; never throws — failures become the error reply.</summary>
    public string Handle(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            string method = root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()! : throw new ArgumentException("missing \"method\"");
            JsonElement? p = root.TryGetProperty("params", out var pe) ? pe : null;
            object? result = null;
            _host.Invoke(() => result = _methods.Execute(method, p));
            Log.Pipe($"control {method} ok");
            return JsonSerializer.Serialize(new { ok = true, result }, Json);
        }
        catch (Exception ex)
        {
            // Invoke wraps the UI thread's exception; report the real one.
            var real = ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;
            Log.Pipe($"control failed: {real.Message}");
            return JsonSerializer.Serialize(new { ok = false, error = real.Message }, Json);
        }
    }

    public static bool IsControl(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "control";
        }
        catch (JsonException) { return false; }
    }

    private static void RequireWindowName(CommandArgs a)
    {
        string w = a.RequiredStr("window");
        if (w != "projects") throw new ArgumentException($"unknown window \"{w}\"; expected: projects");
    }

    private ProjectsDialog ProjectsWindow() =>
        _host.Projects?.Dialog ?? throw new InvalidOperationException("the DesktopNames window is not open; call ui.open first");

    private object Inspect()
    {
        var sessions = _host.SessionState;
        return new
        {
            version = Program.GetBuildStamp(),
            screenSetup = _host.Settings.Layout().DisplayName,
            desktops = _host.DesktopService.GetDesktops().Select(d => new
            {
                number = d.Index + 1,
                d.Name,
                current = d.IsCurrent,
                blue = _host.Settings.IsDesktopHighlighted(d.Id),
                state = sessions?.GetAggregate(d.Id).state.ToString(),
                sessions = sessions?.GetSessions(d.Id).Select(s => new { s.SessionId, s.Label, state = s.State.ToString(), s.Cwd }),
            }),
            projectsWindowOpen = _host.Projects?.Dialog != null,
        };
    }

    /// <summary>Render a window into a PNG with PrintWindow: no focus, no screen coordinates, any DPI.</summary>
    private static Size Capture(IntPtr hwnd, string path)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var r)) throw new InvalidOperationException("GetWindowRect failed");
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) throw new InvalidOperationException("window has no size (hidden?)");
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            bool ok = NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT);
            g.ReleaseHdc(hdc);
            if (!ok) throw new InvalidOperationException("PrintWindow failed");
        }
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return new Size(w, h);
    }
}
