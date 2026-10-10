using System.Text.Json;

namespace DesktopNames;

/// <summary>
/// One user action: a stable id, a label, the params it accepts and what it does. Hotkeys and the
/// control channel both run actions through <see cref="CommandRegistry"/>, so an agent can do
/// anything a hotkey does, by the same code path.
/// </summary>
internal sealed record Command(string Id, string Label, string[] Params, string ParamsDoc, Func<CommandArgs, object?> Run);

/// <summary>Params of one command call, checked against what the command declares.</summary>
internal sealed class CommandArgs
{
    private readonly Dictionary<string, JsonElement> _values;

    private CommandArgs(Dictionary<string, JsonElement> values) => _values = values;

    public static readonly CommandArgs Empty = new(new());

    /// <summary>Parse a params object; an unknown key is an error that lists the accepted ones.</summary>
    public static CommandArgs Parse(Command cmd, JsonElement? json)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (json is { ValueKind: JsonValueKind.Object } obj)
            foreach (var p in obj.EnumerateObject())
            {
                if (!cmd.Params.Contains(p.Name))
                    throw new ArgumentException($"unknown argument \"{p.Name}\" for {cmd.Id}; expected: {(cmd.Params.Length == 0 ? "(none)" : string.Join(", ", cmd.Params))}");
                values[p.Name] = p.Value.Clone();
            }
        else if (json is { } other && other.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            throw new ArgumentException($"params for {cmd.Id} must be an object");
        return new CommandArgs(values);
    }

    /// <summary>Optional integer param; a present value of the wrong type is an error.</summary>
    public int? Int(string name)
    {
        if (!_values.TryGetValue(name, out var v)) return null;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int i))
            throw new ArgumentException($"\"{name}\" must be an integer");
        return i;
    }

    public int RequiredInt(string name) => Int(name) ?? throw new ArgumentException($"missing argument \"{name}\"");

    /// <summary>Optional string param; a present value of the wrong type is an error.</summary>
    public string? Str(string name)
    {
        if (!_values.TryGetValue(name, out var v)) return null;
        if (v.ValueKind != JsonValueKind.String) throw new ArgumentException($"\"{name}\" must be a string");
        return v.GetString();
    }

    public string RequiredStr(string name) => Str(name) ?? throw new ArgumentException($"missing argument \"{name}\"");

    /// <summary>A param passed through unparsed (e.g. the params of a nested command).</summary>
    public JsonElement? Raw(string name) => _values.TryGetValue(name, out var v) ? v : null;
}

internal sealed class CommandRegistry
{
    private readonly Dictionary<string, Command> _byId = new(StringComparer.Ordinal);

    public IEnumerable<Command> All => _byId.Values.OrderBy(c => c.Id, StringComparer.Ordinal);

    public void Add(Command c)
    {
        if (!_byId.TryAdd(c.Id, c)) throw new InvalidOperationException($"duplicate command id {c.Id}");
    }

    /// <summary>Run a command by id. Unknown ids and bad params throw with a message that says what is valid.</summary>
    public object? Execute(string id, JsonElement? json)
    {
        if (!_byId.TryGetValue(id, out var cmd))
            throw new ArgumentException($"unknown command \"{id}\"; run \"commands\" for the list");
        return cmd.Run(CommandArgs.Parse(cmd, json));
    }

    /// <summary>Run a command from a hotkey, with fixed params.</summary>
    public void Execute(string id, CommandArgs args) => _byId[id].Run(args);

    public static CommandArgs Args(string id, CommandRegistry r, object paramsObject) =>
        CommandArgs.Parse(r._byId[id], JsonSerializer.SerializeToElement(paramsObject));
}
