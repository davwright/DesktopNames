using System.Runtime.InteropServices;

namespace DesktopNames;

/// <summary>
/// Global hotkeys through a low-level keyboard hook, not RegisterHotKey: Windows reserves many
/// Win chords (Win+1..0, Win+Ctrl+1..0, Win+Alt+arrows ...) and RegisterHotKey can never get
/// them. A matching chord is swallowed, so Windows and other apps never see it, and its handler
/// runs on the host's UI thread. The hook lives on its own thread with its own message loop:
/// Windows silently drops a low-level hook whose thread stalls, which the UI thread can do.
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    // An unassigned virtual key. Tapping it while Win or Alt is held after a swallowed chord
    // keeps Windows from treating the modifier release as "open Start" / "focus the menu bar".
    private const byte VK_MASK = 0xE8;

    private readonly Form _host;
    private readonly object _lock = new();
    private readonly Dictionary<(uint mods, uint vk), Action> _handlers = new();
    private readonly HashSet<uint> _swallowedDown = new();   // hook thread only
    private readonly List<Registration> _registrations = new();
    private readonly NativeMethods.LowLevelKeyboardProc _proc;   // kept alive: the hook holds a raw pointer to it
    private readonly Thread _thread;
    private uint _threadId;
    private IntPtr _hook;

    public IReadOnlyList<Registration> Registrations => _registrations;

    public HotkeyManager(Form host)
    {
        _host = host;
        _proc = HookProc;
        Exception? failure = null;
        using var started = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
                failure = new InvalidOperationException($"SetWindowsHookEx failed (Win32 {Marshal.GetLastWin32Error()})");
            started.Set();
            if (failure != null) return;
            while (NativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            NativeMethods.UnhookWindowsHookEx(_hook);
        }) { IsBackground = true, Name = "HotkeyHook" };
        _thread.Start();
        started.Wait();
        if (failure != null) throw failure;
    }

    /// <summary>Bind a chord. Fails only when another binding already uses the same chord.</summary>
    public bool Register(uint modifiers, uint vk, string description, Action handler)
    {
        bool ok;
        lock (_lock) ok = _handlers.TryAdd((modifiers, vk), handler);
        _registrations.Add(new Registration(description, modifiers, vk, ok, ok ? 0 : -2));
        return ok;
    }

    public void RecordPlaceholder(string description)
    {
        _registrations.Add(new Registration(description, 0, 0, false, -1));
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var kb = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool down = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
            bool up = msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;

            // The key-up of a swallowed chord, and its auto-repeats, are swallowed too.
            if (up && _swallowedDown.Remove(kb.vkCode)) return 1;
            if (down && _swallowedDown.Contains(kb.vkCode)) return 1;

            if (down)
            {
                uint mods = CurrentModifiers();
                Action? handler;
                lock (_lock) _handlers.TryGetValue((mods, kb.vkCode), out handler);
                if (handler != null)
                {
                    _swallowedDown.Add(kb.vkCode);
                    if ((mods & (NativeMethods.MOD_WIN | NativeMethods.MOD_ALT)) != 0)
                    {
                        NativeMethods.keybd_event(VK_MASK, 0, 0, UIntPtr.Zero);
                        NativeMethods.keybd_event(VK_MASK, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    }
                    _host.BeginInvoke(() =>
                    {
                        try { handler(); }
                        catch (Exception ex) { Log.Ui($"hotkey handler failed: {ex}"); }
                    });
                    return 1;
                }
            }
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static uint CurrentModifiers()
    {
        static bool Down(int vk) => NativeMethods.GetAsyncKeyState(vk) < 0;
        uint m = 0;
        if (Down(0x5B) || Down(0x5C)) m |= NativeMethods.MOD_WIN;      // VK_LWIN / VK_RWIN
        if (Down(0x11)) m |= NativeMethods.MOD_CONTROL;                // VK_CONTROL
        if (Down(0x12)) m |= NativeMethods.MOD_ALT;                    // VK_MENU
        if (Down(0x10)) m |= NativeMethods.MOD_SHIFT;                  // VK_SHIFT
        return m;
    }

    public void Dispose()
    {
        NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join();
    }

    /// <summary>LastError: 0 ok, -1 unparseable binding, -2 chord already bound to another action.</summary>
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
            "oem3" or "backtick" or "tilde" or "`" => 0xC0,   // VK_OEM_3 — `~ key
            "oem1" or "oemsemicolon" or "semicolon" or ";" => 0xBA, // ;: key
            "oem2" or "oemquestion" or "slash" or "/" => 0xBF, // /? key
            "oem4" or "oemopenbrackets" or "openbracket" or "[" => 0xDB, // [{ key
            "oem5" or "oempipe" or "backslash" or "\\" => 0xDC, // \| key
            "oem6" or "oemclosebrackets" or "closebracket" or "]" => 0xDD, // ]} key
            "oem7" or "oemquotes" or "quote" or "'" => 0xDE, // '" key
            "oemplus" or "plus" or "=" => 0xBB, // =+ key
            "oemminus" or "minus" or "-" => 0xBD, // -_ key
            "oemcomma" or "comma" or "," => 0xBC, // ,< key
            "oemperiod" or "period" or "." => 0xBE, // .> key
            _ => 0u
        };
    }
}
