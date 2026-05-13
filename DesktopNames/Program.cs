using System.Reflection;

namespace DesktopNames;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var mutex = new Mutex(true, "DesktopNames_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("DesktopNames is already running.", "DesktopNames",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var settings = Settings.Load();

        var desktopService = new DesktopService();
        if (!desktopService.Initialize())
        {
            MessageBox.Show("Failed to initialize virtual desktop service.",
                "DesktopNames", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var appIcon = LoadAppIcon() ?? SystemIcons.Application;

        // Hidden host form owns: message loop, hotkeys, WinEventHook, taskbar overlays.
        var hostForm = new HostForm(desktopService, settings) { Icon = appIcon };
        Host = hostForm;

        var trayIcon = new NotifyIcon
        {
            Text = $"DesktopNames v{GetAppVersion()}",
            Icon = appIcon,
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };
        trayIcon.ContextMenuStrip.Items.Add("Toggle hide (Win+Alt+H)", null,
            (_, _) => { settings.Hidden = !settings.Hidden; settings.Save(); });
        trayIcon.ContextMenuStrip.Items.Add("Keyboard shortcuts...", null, (_, _) => ShowShortcuts());
        trayIcon.ContextMenuStrip.Items.Add("Open settings.json", null, (_, _) => OpenSettingsFile());
        trayIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        trayIcon.ContextMenuStrip.Items.Add("Exit", null, (_, _) => hostForm.Close());

        hostForm.FormClosing += (_, _) =>
        {
            // Single cleanup site. Don't call Application.Exit() — Form.Close + Application.Run exit is enough.
            trayIcon.Visible = false;
            trayIcon.Dispose();
            desktopService.Dispose();
        };

        Application.Run(hostForm);
    }

    private static Icon? LoadAppIcon()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                          .FirstOrDefault(n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));
            if (name is null) return null;
            using var s = asm.GetManifestResourceStream(name);
            return s is null ? null : new Icon(s);
        }
        catch { return null; }
    }

    public static string GetAppVersion()
    {
        // Prefer informational (full SemVer); fall back to file version.
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // strip +commitsha suffix that SourceLink appends
            int plus = info.IndexOf('+');
            return plus >= 0 ? info.Substring(0, plus) : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "?";
    }

    public static HostForm? Host { get; set; }

    public static void OpenSettingsFile()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopNames", "settings.json");

        if (!File.Exists(path))
        {
            try { Settings.Load().Save(); } catch { }
        }

        // Open Explorer with the file highlighted — works regardless of .json file associations.
        // User can then double-click to open in their preferred editor.
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open {path}\n\n{ex.Message}", "DesktopNames",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public static void ShowShortcuts()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("DesktopNames hotkeys");
        sb.AppendLine("(✓ registered  ✗ another app owns this combo or key not recognised)");
        sb.AppendLine("──────────────────────────────────────────────────────");
        var regs = Host?.Hotkeys?.Registrations;
        bool anyFailed = false;
        if (regs is { Count: > 0 })
        {
            foreach (var r in regs)
            {
                string mark = r.Success ? "✓" : "✗";
                string suffix = r.Success ? "" :
                    r.LastError switch
                    {
                        1409 => "   (Win32 1409: another running app already holds this combo)",
                        -1   => "",
                        _    => $"   (Win32 error {r.LastError})"
                    };
                sb.Append("  ").Append(mark).Append("  ").Append(r.Description).AppendLine(suffix);
                if (!r.Success) anyFailed = true;
            }
        }
        else sb.AppendLine("  (none registered yet)");

        if (anyFailed)
        {
            sb.AppendLine();
            sb.AppendLine("Rebind blocked hotkeys by editing settings.json");
            sb.AppendLine("(\"Open settings.json\" in the tray menu, then restart DesktopNames).");
            sb.AppendLine("Format examples: \"Win+Alt+Left\", \"Ctrl+Shift+F11\", \"Win+Ctrl+Alt+H\".");
            sb.AppendLine("Set a binding to \"\" to disable it.");
        }

        sb.AppendLine();
        sb.AppendLine("Windows built-ins");
        sb.AppendLine("──────────────────────────────────────────────────────");
        sb.AppendLine("  Win + Ctrl + ← / →   Switch to adjacent desktop");
        sb.AppendLine("  Win + Ctrl + D       Create a new desktop");
        sb.AppendLine("  Win + Ctrl + F4      Close current desktop");
        sb.AppendLine("  Win + Tab            Task View");

        MessageBox.Show(sb.ToString(),
            $"DesktopNames v{GetAppVersion()} — Keyboard shortcuts",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}

/// <summary>
/// Hidden message-pump host. Owns all overlays, the global hotkey registrations,
/// and the WinEventHook used for fast fullscreen + desktop-change detection.
/// </summary>
internal sealed class HostForm : Form
{
    private readonly DesktopService _desktopService;
    private readonly Settings _settings;
    private readonly List<TaskbarOverlay> _overlays = new();
    private HotkeyManager? _hotkeys;
    private VsCodeTracker? _vscodeTracker;
    private IntPtr _foregroundHook;
    private IntPtr _locationHook;
    private NativeMethods.WinEventDelegate? _foregroundDelegate;
    private NativeMethods.WinEventDelegate? _locationDelegate;
    private IntPtr _trackedForeground;
    private Guid _lastDesktopId;
    private readonly System.Windows.Forms.Timer _desktopPoll;

    public HostForm(DesktopService desktopService, Settings settings)
    {
        _desktopService = desktopService;
        _settings = settings;

        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Minimized;
        Opacity = 0;
        Visible = false;

        Load += (_, _) =>
        {
            BuildOverlays();
            RegisterHotkeys();
            InstallWinEventHooks();
            _lastDesktopId = _desktopService.GetCurrentDesktopId();
            _vscodeTracker = new VsCodeTracker(_desktopService, _settings);
        };

        // Quick poll for virtual-desktop switches (registry has no notification surface
        // we can hook from C# easily). Fires only when CurrentVirtualDesktop changes.
        _desktopPoll = new System.Windows.Forms.Timer { Interval = 200 };
        _desktopPoll.Tick += (_, _) =>
        {
            var id = _desktopService.GetCurrentDesktopId();
            if (id != _lastDesktopId)
            {
                _lastDesktopId = id;
                RefreshAllOverlays();
            }
        };
        _desktopPoll.Start();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    private void BuildOverlays()
    {
        var taskbars = TaskbarInfo.FindAllTaskbars();
        foreach (var tb in taskbars)
            AddOverlay(tb);
    }

    private void AddOverlay(TaskbarData tb)
    {
        var overlay = new TaskbarOverlay(tb, _desktopService, _settings);
        _overlays.Add(overlay);
        overlay.Show();
        overlay.RefreshDesktops();
    }

    public HotkeyManager? Hotkeys => _hotkeys;

    private void RegisterHotkeys()
    {
        _hotkeys = new HotkeyManager(this);
        // Hotkey actions run on the UI/STA thread (we're inside WndProc), so direct COM calls are safe.
        TryRegister("MoveDesktopLeft",  "Move desktop left",  () => { _desktopService.MoveCurrentDesktopBy(-1); RefreshAllOverlays(); });
        TryRegister("MoveDesktopRight", "Move desktop right", () => { _desktopService.MoveCurrentDesktopBy(1);  RefreshAllOverlays(); });
        TryRegister("MoveDesktopFirst", "Make desktop first", () => { _desktopService.MoveCurrentDesktopToFirst(); RefreshAllOverlays(); });
        TryRegister("MoveDesktopLast",  "Make desktop last",  () => { _desktopService.MoveCurrentDesktopToLast();  RefreshAllOverlays(); });
        TryRegister("ToggleHide",       "Toggle overlay hide", () => { _settings.Hidden = !_settings.Hidden; _settings.Save(); });
    }

    private void TryRegister(string key, string description, Action handler)
    {
        _settings.Hotkeys.TryGetValue(key, out var binding);
        binding = (binding ?? "").Trim();
        if (binding.Length == 0) return; // disabled

        var parsed = HotkeyParser.Parse(binding);
        string fullDesc = $"{binding,-18}  {description}";
        if (parsed is null)
        {
            // Surface the parse failure as a record with LastError = -1.
            _hotkeys!.RecordPlaceholder(fullDesc + "   (unrecognised key — edit settings.json)");
            return;
        }
        _hotkeys!.Register(parsed.Value.mods, parsed.Value.vk, fullDesc, handler);
    }

    private void InstallWinEventHooks()
    {
        // Foreground-window changes: re-evaluate fullscreen state on every overlay.
        _foregroundDelegate = OnForegroundChanged;
        _foregroundHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _foregroundDelegate, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);

        // Location changes on the foreground window: catches the fullscreen *transition*
        // (e.g., a video player going F11). Filtered to the tracked foreground window only.
        _locationDelegate = OnLocationChanged;
        _locationHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _locationDelegate, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);
    }

    private void OnForegroundChanged(IntPtr hHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        _trackedForeground = hwnd;
        BeginInvoke(RefreshAllOverlays);
    }

    private void OnLocationChanged(IntPtr hHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd != _trackedForeground || idObject != 0) return; // OBJID_WINDOW = 0
        BeginInvoke(RefreshAllOverlays);
    }

    private void RefreshAllOverlays()
    {
        foreach (var overlay in _overlays.ToArray())
        {
            if (overlay.IsDisposed) { _overlays.Remove(overlay); continue; }
            overlay.RefreshDesktops();
        }
    }

    private void RebuildOverlays()
    {
        // Dispose existing, re-discover taskbars (handles monitor add/remove, taskbar restart).
        foreach (var o in _overlays) { try { o.Close(); o.Dispose(); } catch { } }
        _overlays.Clear();
        BuildOverlays();
    }

    protected override void WndProc(ref Message m)
    {
        if (_hotkeys != null && _hotkeys.HandleMessage(ref m)) return;

        if (m.Msg == NativeMethods.WM_DISPLAYCHANGE)
        {
            BeginInvoke(RebuildOverlays);
        }
        else if (m.Msg == NativeMethods.WM_SETTINGCHANGE)
        {
            BeginInvoke(RefreshAllOverlays);
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _desktopPoll.Stop();
        _desktopPoll.Dispose();
        _vscodeTracker?.Dispose();
        _hotkeys?.Dispose();
        if (_foregroundHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_foregroundHook);
        if (_locationHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_locationHook);
        foreach (var o in _overlays) { try { o.Close(); o.Dispose(); } catch { } }
        _overlays.Clear();
        base.OnFormClosing(e);
    }
}
