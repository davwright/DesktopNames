using System.Diagnostics;
using System.Reflection;

namespace DesktopNames;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Replace any existing instance. Settings live in %APPDATA%\DesktopNames\settings.json
        // so nothing needs to be handed over — the old process can just die.
        KillExistingInstances();

        var settings = Settings.Load();

        // Locate and load VirtualDesktopAccessor.dll before DesktopService starts —
        // VdaDll P/Invokes are no-ops until this succeeds. If the DLL is missing, prompt
        // the user to pick it (and persist the chosen path so we don't ask again).
        if (!VdaDll.Initialize(settings.VdaDllPath))
        {
            string? picked = PromptForVdaDll(VdaDll.LoadError);
            if (picked == null) return;     // user cancelled — abort startup
            settings.VdaDllPath = picked;
            settings.Save();
            if (!VdaDll.Initialize(picked))
            {
                MessageBox.Show($"Could not load VirtualDesktopAccessor.dll from\n{picked}\n\n{VdaDll.LoadError}",
                    "DesktopNames", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }

        // Gate destructive operations on a build-versioned read-only self-test. If the
        // Windows build hasn't changed since last successful test, trust. Otherwise re-run.
        // If the test fails, disable AutoMove until the user (or a newer DLL) resolves it.
        _buildVerificationHint = RunBuildVerification(settings);

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

        // VS Code workspaces submenu — populated lazily on DropDownOpening so it
        // always reflects the current set of open VS Code windows.
        var vscodeMenu = new ToolStripMenuItem("VS Code workspaces");
        vscodeMenu.DropDownOpening += (_, _) => BuildVsCodeWorkspacesMenu(vscodeMenu, desktopService, settings);
        // Seed with a placeholder so the arrow renders before first open.
        vscodeMenu.DropDownItems.Add("(loading...)").Enabled = false;
        trayIcon.ContextMenuStrip.Items.Add(vscodeMenu);

        trayIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        trayIcon.ContextMenuStrip.Items.Add("Exit", null, (_, _) => hostForm.Close());

        hostForm.FormClosing += (_, _) =>
        {
            // Single cleanup site. Don't call Application.Exit() — Form.Close + Application.Run exit is enough.
            trayIcon.Visible = false;
            trayIcon.Dispose();
            desktopService.Dispose();
        };

        // Surface visibility state at startup so the overlay can never silently "not appear"
        // (the most likely causes: Hidden toggle, OnlyOnMainDesktop, no taskbar found).
        hostForm.Shown += (_, _) => ShowStartupBalloon(trayIcon, hostForm, settings);

        Application.Run(hostForm);
    }

    /// <summary>
    /// Repopulate the "VS Code workspaces" submenu from currently-open VS Code windows.
    /// Each item is checked iff the workspace is pinned; clicking toggles the pin (pinning
    /// to the window's current desktop). Footer item opens a full bindings overview.
    /// </summary>
    private static void BuildVsCodeWorkspacesMenu(ToolStripMenuItem root, DesktopService desktopService, Settings settings)
    {
        root.DropDownItems.Clear();
        var workspaces = VsCodeTracker.EnumerateOpenWorkspaces();
        if (workspaces.Count == 0)
        {
            root.DropDownItems.Add("(no VS Code windows open)").Enabled = false;
        }
        else
        {
            var desktops = desktopService.GetDesktops().ToDictionary(d => d.Id, d => d.Name);
            foreach (var (workspace, hwnd) in workspaces.OrderBy(w => w.Workspace, StringComparer.OrdinalIgnoreCase))
            {
                bool pinned = settings.VsCodePinnedDesktops.TryGetValue(workspace, out var pinnedGuid);
                string label;
                if (pinned)
                {
                    desktops.TryGetValue(pinnedGuid, out var deskName);
                    label = $"{workspace}  —  pinned to {deskName ?? "?"}";
                }
                else
                {
                    var current = desktopService.GetDesktopForWindow(hwnd);
                    desktops.TryGetValue(current, out var deskName);
                    label = $"{workspace}  (on {deskName ?? "?"})";
                }

                var item = new ToolStripMenuItem(label) { Checked = pinned, CheckOnClick = false };
                item.Click += (_, _) => TogglePin(workspace, hwnd, desktopService, settings);
                root.DropDownItems.Add(item);
            }
        }

        root.DropDownItems.Add(new ToolStripSeparator());
        root.DropDownItems.Add("Show all bindings...", null, (_, _) => ShowAllBindings(desktopService, settings));
    }

    private static void TogglePin(string workspace, IntPtr hwnd, DesktopService desktopService, Settings settings)
    {
        if (settings.VsCodePinnedDesktops.Remove(workspace))
        {
            // Was pinned → now unpinned. Done.
        }
        else
        {
            var current = desktopService.GetDesktopForWindow(hwnd);
            if (current == Guid.Empty) return;     // can't pin a window whose desktop we can't read
            settings.VsCodePinnedDesktops[workspace] = current;
        }
        settings.Save();
    }

    private static void ShowAllBindings(DesktopService desktopService, Settings settings)
    {
        var desktops = desktopService.GetDesktops().ToDictionary(d => d.Id, d => d.Name);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Pinned (authoritative)");
        sb.AppendLine("──────────────────────────────────────────");
        if (settings.VsCodePinnedDesktops.Count == 0) sb.AppendLine("  (none)");
        else foreach (var kv in settings.VsCodePinnedDesktops.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            desktops.TryGetValue(kv.Value, out var deskName);
            sb.AppendLine($"  {kv.Key,-40}  →  {deskName ?? "(unknown desktop)"}");
        }
        sb.AppendLine();
        sb.AppendLine("Observed (passive, may be overwritten by tracker)");
        sb.AppendLine("──────────────────────────────────────────");
        if (settings.VsCodeWorkspaceDesktops.Count == 0) sb.AppendLine("  (none)");
        else foreach (var kv in settings.VsCodeWorkspaceDesktops.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            desktops.TryGetValue(kv.Value, out var deskName);
            sb.AppendLine($"  {kv.Key,-40}  →  {deskName ?? "(unknown desktop)"}");
        }

        MessageBox.Show(sb.ToString(), $"DesktopNames v{GetAppVersion()} — VS Code bindings",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // Set by RunBuildVerification when the self-test fails; surfaced by ShowStartupBalloon
    // so the user sees one balloon, not two.
    private static string? _buildVerificationHint;

    /// <summary>
    /// Compare current Windows build against settings.VerifiedBuild and run VdaDll.SelfTest
    /// if it changed (or if the last test failed). On pass, record the new build. On fail,
    /// flip VsCodeAutoMove off so destructive calls don't fire against a wrong-layout DLL.
    /// Returns a one-line hint to show in the startup balloon, or null on clean pass.
    /// </summary>
    private static string? RunBuildVerification(Settings settings)
    {
        var currentBuild = ReadCurrentWindowsBuild();
        bool buildMatches = !string.IsNullOrEmpty(settings.VerifiedBuild) &&
                            string.Equals(settings.VerifiedBuild, currentBuild, StringComparison.Ordinal);

        if (buildMatches && settings.LastTestedBuildOk) return null;   // already trusted

        var result = VdaDll.SelfTest();
        if (result.Passed)
        {
            settings.VerifiedBuild = currentBuild;
            settings.LastTestedBuildOk = true;
            settings.Save();
            return buildMatches ? null : $"Windows build {currentBuild}: VDA self-test passed";
        }

        settings.LastTestedBuildOk = false;
        if (settings.VsCodeAutoMove)
        {
            settings.VsCodeAutoMove = false;   // safer default until the DLL is updated
            settings.Save();
            return $"VDA self-test failed on Windows {currentBuild}: {result.FailureReason}. " +
                   "Auto-move disabled. Update VirtualDesktopAccessor.dll " +
                   "(github.com/Ciantic/VirtualDesktopAccessor/releases).";
        }
        settings.Save();
        return $"VDA self-test failed on Windows {currentBuild}: {result.FailureReason}.";
    }

    private static string ReadCurrentWindowsBuild()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key == null) return "?";
            var build = key.GetValue("CurrentBuild") as string ?? "?";
            var ubr = key.GetValue("UBR")?.ToString() ?? "?";
            return $"{build}.{ubr}";
        }
        catch { return "?"; }
    }

    /// <summary>
    /// Ask the user to locate VirtualDesktopAccessor.dll when auto-discovery fails.
    /// Returns the chosen path, or null if the user cancels.
    /// </summary>
    private static string? PromptForVdaDll(string? loadError)
    {
        var prompt = "DesktopNames needs VirtualDesktopAccessor.dll to move windows between virtual desktops.\n\n" +
                     "Auto-discovery looked next to DesktopNames.exe and in your AutoHotkey folder under Documents/Dokumente.\n\n" +
                     (loadError ?? "") + "\n\n" +
                     "Click OK to browse to the DLL, or Cancel to exit.\n" +
                     "(Get it from https://github.com/Ciantic/VirtualDesktopAccessor/releases)";
        var r = MessageBox.Show(prompt, "DesktopNames — DLL not found",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (r != DialogResult.OK) return null;

        using var ofd = new OpenFileDialog
        {
            Title = "Select VirtualDesktopAccessor.dll",
            Filter = "VirtualDesktopAccessor.dll|VirtualDesktopAccessor.dll|DLL files (*.dll)|*.dll",
            CheckFileExists = true
        };
        return ofd.ShowDialog() == DialogResult.OK ? ofd.FileName : null;
    }

    private static void KillExistingInstances()
    {
        var me = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(me.ProcessName))
        {
            if (p.Id == me.Id) { p.Dispose(); continue; }
            try
            {
                p.Kill(entireProcessTree: false);
                p.WaitForExit(2000);
            }
            catch { /* already gone, access denied, or zombie — proceed regardless */ }
            finally { p.Dispose(); }
        }
    }

    private static void ShowStartupBalloon(NotifyIcon tray, HostForm host, Settings settings)
    {
        // Build-verification result takes precedence: a failed self-test is more
        // urgent than overlay-visibility hints.
        string? hint = _buildVerificationHint;
        var icon = ToolTipIcon.Info;
        if (hint != null && !settings.LastTestedBuildOk) icon = ToolTipIcon.Warning;

        if (hint == null)
        {
            if (settings.Hidden) hint = "Overlay is hidden (Win+Alt+H to show)";
            else if (host.OverlayCount == 0) hint = "No taskbars found — overlay has nowhere to draw";
            else if (settings.OnlyOnMainDesktop) hint = "Overlay shows only on main desktop (right-click tray to change)";
        }

        if (hint == null) return;
        try
        {
            tray.BalloonTipTitle = $"DesktopNames v{GetAppVersion()}";
            tray.BalloonTipText = hint;
            tray.BalloonTipIcon = icon;
            tray.ShowBalloonTip(icon == ToolTipIcon.Warning ? 8000 : 4000);
        }
        catch { }
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

        // RenameDesktop / CreateDesktop / RemoveDesktop / MoveDesktop all fire this.
        // Without it, a rename succeeds in the OS but the overlay keeps showing the
        // old label until the next 1.5s safety-net refresh.
        _desktopService.DesktopsChanged += () =>
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(RefreshAllOverlays);
            else RefreshAllOverlays();
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
    public int OverlayCount => _overlays.Count;

    private void RegisterHotkeys()
    {
        _hotkeys = new HotkeyManager(this);
        // Hotkey actions run on the UI/STA thread (we're inside WndProc), so direct COM calls are safe.
        TryRegister("MoveDesktopLeft",  "Move desktop left",  () => { _desktopService.MoveCurrentDesktopBy(-1); RefreshAllOverlays(); });
        TryRegister("MoveDesktopRight", "Move desktop right", () => { _desktopService.MoveCurrentDesktopBy(1);  RefreshAllOverlays(); });
        TryRegister("MoveDesktopFirst", "Make desktop first", () => { _desktopService.MoveCurrentDesktopToFirst(); RefreshAllOverlays(); });
        TryRegister("MoveDesktopLast",  "Make desktop last",  () => { _desktopService.MoveCurrentDesktopToLast();  RefreshAllOverlays(); });
        TryRegister("ToggleHide",       "Toggle overlay hide", () => { _settings.Hidden = !_settings.Hidden; _settings.Save(); });

        // Switch-to-desktop hotkeys. Loop variable must be captured into a local
        // so each handler closure binds its own index.
        for (int i = 1; i <= 20; i++)
        {
            int idx = i;
            TryRegister($"SwitchToDesktop{idx}", $"Switch to desktop {idx}", () =>
            {
                var list = _desktopService.GetDesktops();
                if (idx - 1 < list.Count)
                {
                    _desktopService.SwitchToDesktop(list[idx - 1]);
                    RefreshAllOverlays();
                }
            });
        }
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
