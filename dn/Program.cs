// dn — drive the running DesktopNames over its pipe, print the reply, exit.
//   dn run <command> [key=value ...]     run one command (dn commands lists them)
//   dn <method> [key=value ...]          any control method (commands, inspect, ui.screenshot, …)
//   dn <method> '<params-json>'          the same with JSON (PowerShell 5.1 strips its quotes: use key=value there)
// Exit 0 ok, 1 the request failed, 2 usage error.
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length == 0)
{
    Console.Error.WriteLine("""
        usage: dn run <command> [key=value ...]   e.g. dn run window.place window=msedge desktop=3 screen=2 state=max
               dn <method> [params-json]          e.g. dn commands | dn inspect | dn run window.list
        """);
    return 2;
}

bool run = args[0] == "run";
if (run && args.Length < 2) { Console.Error.WriteLine("usage: dn run <command> [key=value ...]"); return 2; }
var rest = args.Skip(run ? 2 : 1).ToArray();
JsonNode? p;
if (rest.Length == 1 && rest[0].TrimStart().StartsWith('{'))
{
    try { p = JsonNode.Parse(rest[0]); }
    catch (JsonException e) { Console.Error.WriteLine($"params are not JSON: {e.Message} (from PowerShell use key=value)"); return 2; }
}
else
{
    var o = new JsonObject();
    foreach (var kv in rest)
    {
        int eq = kv.IndexOf('=');
        if (eq <= 0) { Console.Error.WriteLine($"\"{kv}\" is not key=value"); return 2; }
        string value = kv[(eq + 1)..];
        o[kv[..eq]] = long.TryParse(value, out long n) ? JsonValue.Create(n)
                    : value is "true" or "false" ? JsonValue.Create(value == "true")
                    : JsonValue.Create(value);
    }
    p = rest.Length == 0 ? null : o;
}
string method = run ? "execute" : args[0];
JsonNode? parameters = run ? new JsonObject { ["command"] = args[1], ["params"] = p ?? new JsonObject() } : p;

using var pipe = new NamedPipeClientStream(".", "DesktopNames", PipeDirection.InOut);
try { pipe.Connect(2000); }
catch (TimeoutException) { Console.Error.WriteLine("DesktopNames pipe: no answer in 2 s (is DesktopNames running?)"); return 1; }

var request = new JsonObject { ["type"] = "control", ["method"] = method, ["params"] = parameters };
var bytes = Encoding.UTF8.GetBytes(request.ToJsonString() + "\n");
pipe.Write(bytes);
pipe.Flush();

using var reader = new StreamReader(pipe, Encoding.UTF8);
var line = reader.ReadLine() ?? throw new IOException("DesktopNames closed the pipe without replying");
var reply = JsonNode.Parse(line)!;
Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine(reply.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
return reply["ok"]?.GetValue<bool>() == true ? 0 : 1;
