using System.Runtime.InteropServices;

namespace DesktopNames;

/// <summary>
/// Registers global hotkeys on a hidden message-only host form and routes
/// WM_HOTKEY into Action callbacks. RegisterHotKey is per-thread, so all
/// registrations must happen on the same UI thread that owns the host window.
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    private readonly Form _host;
    private readonly Dictionary<int, Action> _handlers = new();
    private readonly List<Registration> _registrations = new();
    private int _nextId = 0x9000;

    public IReadOnlyList<Registration> Registrations => _registrations;

    public HotkeyManager(Form host)
    {
        _host = host;
    }

    public bool Register(uint modifiers, uint vk, string description, Action handler)
    {
        int id = _nextId++;
        bool ok = NativeMethods.RegisterHotKey(_host.Handle, id,
            modifiers | NativeMethods.MOD_NOREPEAT, vk);
        int err = ok ? 0 : Marshal.GetLastWin32Error();
        _registrations.Add(new Registration(description, modifiers, vk, ok, err));
        if (!ok) return false;
        _handlers[id] = handler;
        return true;
    }

    public void RecordPlaceholder(string description)
    {
        _registrations.Add(new Registration(description, 0, 0, false, -1));
    }

    public bool HandleMessage(ref Message m)
    {
        if (m.Msg != NativeMethods.WM_HOTKEY) return false;
        int id = m.WParam.ToInt32();
        if (_handlers.TryGetValue(id, out var h))
        {
            try { h(); } catch { }
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        foreach (var id in _handlers.Keys)
        {
            try { NativeMethods.UnregisterHotKey(_host.Handle, id); } catch { }
        }
        _handlers.Clear();
    }

    public sealed record Registration(string Description, uint Modifiers, uint VirtualKey, bool Success, int LastError);
}

/// <summary>Parses "Win+Alt+Left", "Ctrl+Shift+F12" etc. into (modifiers, virtual-key).</summary>
internal static class HotkeyParser
{
    public static (uint mods, uint vk)? Parse(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        uint mods = 0, vk = 0;
        foreach (var raw in s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var n = raw.ToLowerInvariant();
            switch (n)
            {
                case "win": case "windows": case "lwin": case "rwin": mods |= NativeMethods.MOD_WIN; continue;
                case "ctrl": case "control": mods |= NativeMethods.MOD_CONTROL; continue;
                case "alt": case "menu": mods |= NativeMethods.MOD_ALT; continue;
                case "shift": mods |= NativeMethods.MOD_SHIFT; continue;
            }
            uint key = ParseKey(n);
            if (key == 0) return null;
            vk = key;
        }
        return vk == 0 ? null : (mods, vk);
    }

    private static uint ParseKey(string name)
    {
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return c;
        }
        if (name.Length > 1 && name[0] == 'f' &&
            uint.TryParse(name.AsSpan(1), out var fnum) && fnum is >= 1 and <= 24)
            return 0x6Fu + fnum; // VK_F1 = 0x70

        return name switch
        {
            "left"  => NativeMethods.VK_LEFT,
            "right" => NativeMethods.VK_RIGHT,
            "home"  => NativeMethods.VK_HOME,
            "end"   => NativeMethods.VK_END,
            "up"    => 0x26,
            "down"  => 0x28,
            "space" => 0x20,
            "tab"   => 0x09,
            "esc" or "escape" => 0x1B,
            "enter" or "return" => 0x0D,
            "backspace" => 0x08,
            "del" or "delete" => 0x2E,
            "ins" or "insert" => 0x2D,
            "pgup" or "pageup" => 0x21,
            "pgdn" or "pagedown" => 0x22,
            _ => 0u
        };
    }
}
