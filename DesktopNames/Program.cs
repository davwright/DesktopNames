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
        Log.Configure(settings.AlertLogEnabled);
        // First line after every restart says which build produced everything below it.
        // Without this, "the fix isn't taking" and "the publish hasn't run yet" look identical.
        Log.Startup($"DesktopNames {GetBuildStamp()} pid={Environment.ProcessId}");

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
            Text = $"DesktopNames v{GetBuildStamp()}",
            Icon = appIcon,
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };
        trayIcon.ContextMenuStrip.Items.Add("Toggle hide (Win+Alt+H)", null,
            (_, _) => { settings.Hidden = !settings.Hidden; settings.Save(); });
        trayIcon.ContextMenuStrip.Items.Add("Keyboard shortcuts...", null, (_, _) => ShowShortcuts());
        trayIcon.ContextMenuStrip.Items.Add("Open settings.json", null, (_, _) => OpenSettingsFile());
        if (settings.AlertListenerEnabled) AddClaudeStatusMenu(trayIcon, hostForm);
        trayIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        trayIcon.ContextMenuStrip.Items.Add("Exit", null, (_, _) => hostForm.Close());

        // Tray tooltip = version line + the most-recent hook events (newest first, live ages),
        // so hovering the icon shows the dynamics. NotifyIcon.Text caps at 127 chars; add whole
        // lines while they fit and crop the line that would overflow rather than chopping blind.
        hostForm.RecentEventsChanged += () =>
        {
            const int Cap = 127;
            var sb = new System.Text.StringBuilder($"DesktopNames v{GetAppVersion()}");
            foreach (var line in hostForm.RecentEventLines())
            {
                int remaining = Cap - sb.Length - 1; // budget after the '\n'
                if (remaining <= 1) break;
                sb.Append('\n');
                sb.Append(line.Length <= remaining ? line : line.Substring(0, remaining - 1) + "…");
                if (line.Length > remaining) break;
            }
            trayIcon.Text = sb.ToString();
        };

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

    /// <summary>
    /// Full build stamp — <c>1.2.19+a1b2c3d</c>, or <c>…+a1b2c3d.dirty</c> when publish.ps1
    /// built from a working tree that didn't match that commit. Unlike
    /// <see cref="GetAppVersion"/> this keeps the suffix: the suffix is the whole point.
    /// </summary>
    public static string GetBuildStamp()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(info) ? GetAppVersion() : info!;
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
                        // 1409 covers both cases: Windows returns it for its own reserved
                        // combos (Win+L, Win+Alt+arrows, ...) as well as for app collisions.
                        1409 => "   (Win32 1409: reserved by Windows, or held by another running app)",
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

    /// <summary>
    /// Add the "Claude status" submenu to the tray icon. The status header refreshes
    /// each time the submenu opens. Test items inject synthetic states directly into
    /// SessionState (no pipe), the "Echo via pipe" item does a real round-trip so the
    /// listener itself can be smoke-tested.
    /// </summary>
    private static void AddClaudeStatusMenu(NotifyIcon tray, HostForm host)
    {
        var settings = host.SessionState is null ? new Settings() : host.Settings;
        var menu = new ToolStripMenuItem("Claude status");
        var status = new ToolStripMenuItem("(loading...)") { Enabled = false };

        // Color legend — sits at top of the submenu so the user can glance and know what
        // each color means. Items are kept enabled (clickable) so the swatch Image renders
        // at full saturation — Windows desaturates images on disabled menu items, which
        // defeats the whole point of a color legend.
        Color busyC   = TaskbarOverlay.ParseColorOrFallback(settings.AlertBusyColor,   Color.FromArgb(244, 180, 131));
        Color askingC = TaskbarOverlay.ParseColorOrFallback(settings.AlertAskingColor, Color.FromArgb(255, 230, 128));
        Color errorC  = TaskbarOverlay.ParseColorOrFallback(settings.AlertErrorColor,  Color.FromArgb(224, 133, 133));
        Color readyC  = TaskbarOverlay.ParseColorOrFallback(settings.AlertReadyColor,  Color.FromArgb(168, 216, 176));
        var legendHeader = new ToolStripMenuItem("Color legend");
        var legendAsking = new ToolStripMenuItem("Asking — needs your input (act!)")  { Image = MakeColorSwatch(askingC) };
        var legendBusy   = new ToolStripMenuItem("Busy — Claude is working")          { Image = MakeColorSwatch(busyC) };
        var legendError  = new ToolStripMenuItem("Error — Claude stopped with an error") { Image = MakeColorSwatch(errorC) };
        var legendReady  = new ToolStripMenuItem("Ready — finished / awaiting prompt") { Image = MakeColorSwatch(readyC) };
        // No-op clicks — the legend is informational only.
        legendHeader.Click += (_, _) => { };
        legendAsking.Click += (_, _) => { };
        legendBusy.Click   += (_, _) => { };
        legendError.Click  += (_, _) => { };
        legendReady.Click  += (_, _) => { };

        var jumpNext = new ToolStripMenuItem("Next waiting (Win+])");
        jumpNext.Click += (_, _) => host.JumpToWaitingClaude(+1);
        var jumpPrev = new ToolStripMenuItem("Previous waiting (Win+[)");
        jumpPrev.Click += (_, _) => host.JumpToWaitingClaude(-1);

        ToolStripMenuItem TestItem(string label, StateKind state)
        {
            var item = new ToolStripMenuItem(label);
            item.Click += (_, _) => host.SendTestAlert(state);
            return item;
        }

        var echo = new ToolStripMenuItem("Echo via pipe...");
        echo.Click += (_, _) => RunPipeRoundtripTest();

        menu.DropDownItems.Add(legendHeader);
        menu.DropDownItems.Add(legendAsking);
        menu.DropDownItems.Add(legendBusy);
        menu.DropDownItems.Add(legendError);
        menu.DropDownItems.Add(legendReady);
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(status);
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(jumpPrev);
        menu.DropDownItems.Add(jumpNext);
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(TestItem("Test alert → asking", StateKind.Asking));
        menu.DropDownItems.Add(TestItem("Test alert → busy",   StateKind.Busy));
        menu.DropDownItems.Add(TestItem("Test alert → error",  StateKind.Error));
        menu.DropDownItems.Add(TestItem("Test alert → ready",  StateKind.Ready));
        menu.DropDownItems.Add(TestItem("Test alert → clear",  StateKind.None));
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(echo);

        menu.DropDownOpening += (_, _) =>
        {
            status.Text = host.SessionState?.BuildStatusSummary() ?? "Listener disabled";
        };

        tray.ContextMenuStrip!.Items.Add(menu);
    }

    /// <summary>Tiny 16x16 color square for tray-menu legend items.</summary>
    private static Bitmap MakeColorSwatch(Color c)
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        using var brush = new SolidBrush(c);
        g.FillRectangle(brush, 2, 2, 12, 12);
        using var pen = new Pen(Color.FromArgb(160, 0, 0, 0));
        g.DrawRectangle(pen, 2, 2, 12, 12);
        return bmp;
    }

    /// <summary>Manual pipe smoke test wired to the tray menu. Sends one idle and shows the reply.</summary>
    private static void RunPipeRoundtripTest()
    {
        try
        {
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(
                ".", AlertPipeServer.PipeName, System.IO.Pipes.PipeDirection.InOut);
            pipe.Connect(1000);
            var json = "{\"type\":\"state\",\"source\":\"echo\",\"state\":\"idle\",\"sessionId\":\"echo\"}";
            using (var writer = new StreamWriter(pipe, new System.Text.UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true })
            {
                writer.WriteLine(json);
            }
            using var reader = new StreamReader(pipe);
            var reply = reader.ReadLine();
            MessageBox.Show($"Sent: {json}\n\nReply: {reply}",
                "Pipe roundtrip", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Roundtrip failed: " + ex.Message,
                "Pipe roundtrip", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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
    private ProjectSwitcher? _projects;
    private IntPtr _foregroundHook;
    private IntPtr _locationHook;
    private NativeMethods.WinEventDelegate? _foregroundDelegate;
    private NativeMethods.WinEventDelegate? _locationDelegate;
    private IntPtr _trackedForeground;
    private Guid _lastDesktopId;
    private readonly System.Windows.Forms.Timer _desktopPoll;
    private SessionState? _sessionState;
    private AlertPulse? _alertPulse;
    private StateChime? _stateChime;
    private AlertPipeServer? _alertServer;
    private System.Windows.Forms.Timer? _sweepTimer;

    // Most-recent hook events, newest first — surfaced in the tray tooltip so the user can
    // watch the live dynamics. Capped; only the tail is kept. Timestamps are stored so the
    // tooltip shows live ages (recomputed each read), not frozen clock times.
    private readonly LinkedList<(DateTime whenUtc, string sid, string label, string ev)> _recentEvents = new();
    private const int RecentEventsMax = 5;

    /// <summary>Fires (UI thread) when the tray-tooltip feed should be rebuilt.</summary>
    public event Action? RecentEventsChanged;

    /// <summary>Record one received hook event for the tray tooltip feed. UI thread only.</summary>
    public void RecordEvent(string sessionId, string label, string hookEvent, string toolName)
    {
        string ev = hookEvent == "PreToolUse" && !string.IsNullOrEmpty(toolName) ? toolName : hookEvent;
        string sid = sessionId.Length >= 2 ? sessionId[..2] : sessionId;
        _recentEvents.AddFirst((DateTime.UtcNow, sid, label, ev));
        while (_recentEvents.Count > RecentEventsMax) _recentEvents.RemoveLast();
        RecentEventsChanged?.Invoke();
    }

    /// <summary>Newest-first event lines with live ages (e.g. "8m  15  md-tester  Bash").</summary>
    public IEnumerable<string> RecentEventLines()
    {
        foreach (var (whenUtc, sid, label, ev) in _recentEvents)
            yield return $"{SessionState.FormatAge(whenUtc)}  {sid}  {label}  {ev}";
    }

    /// <summary>Re-emit the feed so the tooltip's ages stay current. Called from the sweep timer.</summary>
    public void RefreshRecentEvents() { if (_recentEvents.Count > 0) RecentEventsChanged?.Invoke(); }

    public SessionState? SessionState => _sessionState;
    public AlertPulse? AlertPulse => _alertPulse;
    public DesktopService DesktopService => _desktopService;
    public Settings Settings => _settings;

    /// <summary>
    /// Inject a synthetic state transition into the in-memory session table for the
    /// current desktop. Bypasses the pipe so rendering can be exercised without a
    /// running Claude. <see cref="StateKind.None"/> clears all test-source entries.
    /// </summary>
    public void SendTestAlert(StateKind state)
    {
        if (_sessionState == null) return;
        if (state == StateKind.None) { _sessionState.RemoveBySource("test"); return; }
        var current = _desktopService.GetCurrentDesktopId();
        if (current == Guid.Empty) return;

        // Synthetic hook context so the glyph badge is visible too. Busy test alert
        // simulates an Edit tool call (glyph "E"); other states get the matching event.
        (string? hookEvent, string? toolName) = state switch
        {
            StateKind.Busy   => ("PreToolUse",   "Edit"),
            StateKind.Asking => ("Notification", null),
            StateKind.Ready  => ("Stop",         null),
            StateKind.Error  => ("StopFailure",  null),
            _ => (null, null),
        };
        string body = state switch
        {
            StateKind.Busy   => "Running Edit",
            StateKind.Asking => "Allow this Bash command?",
            StateKind.Ready  => "Ready",
            StateKind.Error  => "Stopped: rate_limit",
            _ => "",
        };

        // Asking is a pending question, not an activity level — inject it as one so the
        // test entries exercise the same path real events take.
        _sessionState.Apply(
            current,
            source: "test",
            sessionId: "t-" + Guid.NewGuid().ToString("N").Substring(0, 6),
            activity: state == StateKind.Asking ? null : state,
            ask: state == StateKind.Asking ? AskChange.Set : AskChange.Clear,
            remove: false,
            title: $"Test — {state.ToString().ToLowerInvariant()}",
            body: body,
            sessionPid: 0,
            vsCodePid: 0,
            hookEvent: hookEvent,
            toolName: toolName);
    }

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
            // SessionState + pulse must exist before overlays, because each overlay
            // captures both in its constructor for paint-time lookups.
            if (_settings.AlertListenerEnabled)
            {
                _sessionState = new SessionState();
                // A live Claude status arriving on a manually-blue desktop retires the blue:
                // the marker's job ("come back here") is done once Claude is active again.
                // Deferred via BeginInvoke so this runs AFTER the pulse + chime subscribers in
                // this same Changed dispatch — otherwise the removal's save/repaint (or a throw)
                // could swallow the very notification that's clearing the blue.
                _sessionState.Changed += g =>
                {
                    if (g == SessionState.UnresolvedDesktopId || !_settings.IsDesktopHighlighted(g)) return;
                    var (s, _, _) = _sessionState!.GetAggregate(g);
                    if (s != StateKind.None)
                        BeginInvoke(new Action(() => { try { _settings.ToggleDesktopHighlight(g); } catch { } }));
                };
                _alertPulse = new AlertPulse(_sessionState, _settings, InvalidateOverlaysForDesktop);
                _stateChime = new StateChime(_sessionState, _desktopService, _settings);
                _alertServer = new AlertPipeServer(this, _sessionState, _settings, _desktopService);
                _alertServer.Start();
                _sweepTimer = new System.Windows.Forms.Timer { Interval = 5000 };
                _sweepTimer.Tick += (_, _) => { _sessionState!.Sweep(); _alertPulse!.EnsureRunningIfAskingPresent(); RefreshRecentEvents(); };
                _sweepTimer.Start();
            }
            BuildOverlays();
            RegisterHotkeys();
            InstallWinEventHooks();
            _lastDesktopId = _desktopService.GetCurrentDesktopId();
            _vscodeTracker = new VsCodeTracker(_desktopService, _settings);
            _projects = new ProjectSwitcher(_desktopService, _settings, _vscodeTracker, _sessionState);
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
                // Color is a status indicator, not a notification — don't clear on switch.
                // It persists until the source sends `idle` (e.g. SessionEnd hook) or
                // the liveness sweep reaps a dead sessionPid.
                RefreshAllOverlays();
                // "What was I doing on this desktop?" reminder: show the note for a few
                // seconds. No-op when the desktop has no note configured.
                FlashNoteForDesktop(id);
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
        var overlay = new TaskbarOverlay(tb, _desktopService, _settings, _sessionState, _alertPulse);
        _overlays.Add(overlay);
        overlay.Show();
        overlay.RefreshDesktops();
    }

    /// <summary>Repaint the per-desktop button on every overlay. Cheap when no overlays exist.</summary>
    private void InvalidateOverlaysForDesktop(Guid desktopId)
    {
        // The unresolved sentinel button appears/disappears with state — that's a layout
        // change, not a pixel change. Trigger the heavier RefreshDesktops path so
        // RecalculateLayout adds/removes the "?" entry.
        if (desktopId == SessionState.UnresolvedDesktopId)
        {
            foreach (var o in _overlays.ToArray())
                if (!o.IsDisposed) o.RefreshDesktops();
            return;
        }
        foreach (var o in _overlays.ToArray())
        {
            if (!o.IsDisposed) o.InvalidateForDesktop(desktopId);
        }
    }

    /// <summary>
    /// Pop the per-desktop note/state tooltip for ~4 seconds on every visible overlay.
    /// Fired after a desktop switch so the user is reminded what they're doing here.
    /// </summary>
    private void FlashNoteForDesktop(Guid desktopId)
    {
        foreach (var o in _overlays.ToArray())
        {
            if (o.IsDisposed || !o.Visible) continue;
            o.ShowDesktopTooltipFor(desktopId, 4000);
        }
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
        TryRegister("OpenCurrentDesktopMenu", "Open menu for current desktop", () =>
        {
            // Pops the right-click context menu for the current desktop, with the
            // per-desktop section (Rename / Select / Make first / Notes / etc.) pre-populated.
            // Tries overlays in order; the first one with a button for the current desktop wins.
            foreach (var o in _overlays.ToArray())
            {
                if (!o.IsDisposed && o.TryOpenContextMenuForCurrentDesktop()) return;
            }
        });
        TryRegister("PrevWaitingDesktop", "Previous waiting Claude", () => JumpToWaitingClaude(-1));
        TryRegister("NextWaitingDesktop", "Next waiting Claude",     () => JumpToWaitingClaude(+1));
        // Deferred out of WndProc so the modal loop doesn't run inside the hotkey dispatch.
        TryRegister("OpenProjects", "Project switcher", () => BeginInvoke(() => _projects?.ShowDialog()));

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

    /// <summary>
    /// Hotkey + tray handler: switch to the next desktop with a Claude waiting.
    /// <paramref name="direction"/> +1 = forward (Win+]), -1 = backward (Win+[).
    /// Asking takes priority over Ready; within priority, wraps past the current desktop.
    /// No-op if nothing is waiting.
    /// </summary>
    public void JumpToWaitingClaude(int direction)
    {
        if (_sessionState == null) return;
        var desktops = _desktopService.GetDesktops();
        if (desktops.Count == 0) return;

        int currentIdx = -1;
        foreach (var d in desktops) if (d.IsCurrent) { currentIdx = d.Index; break; }

        var asking = new List<DesktopInfo>();
        var error  = new List<DesktopInfo>();
        var ready  = new List<DesktopInfo>();
        foreach (var d in desktops)
        {
            var (s, _, _) = _sessionState.GetAggregate(d.Id);
            if      (s == StateKind.Asking) asking.Add(d);
            else if (s == StateKind.Error)  error.Add(d);
            else if (s == StateKind.Ready)  ready.Add(d);
        }

        DesktopInfo? Pick(List<DesktopInfo> list)
        {
            if (list.Count == 0) return null;
            list.Sort((a, b) => a.Index.CompareTo(b.Index));
            if (direction >= 0)
            {
                foreach (var d in list) if (d.Index > currentIdx) return d;
                return list[0]; // wrap forward to lowest index
            }
            // Backward: largest index strictly less than current. List is ascending,
            // so the last entry satisfying d.Index < currentIdx wins.
            DesktopInfo? best = null;
            foreach (var d in list)
            {
                if (d.Index < currentIdx) best = d;
                else break;
            }
            return best ?? list[^1]; // wrap backward to highest index
        }

        // Priority order: yellow (asking, must act) > red (error, may need action) > green (ready).
        var target = Pick(asking) ?? Pick(error) ?? Pick(ready);
        if (target == null) return;
        _desktopService.SwitchToDesktop(target);
        RefreshAllOverlays();
    }

    /// <summary>
    /// Overlay command: open the arrange-windows grid (desktops × screens) so the user can
    /// drag each open VS Code window to where it should live, then OK to move + maximize +
    /// remember. Replaces the old one-shot "move all to remembered" with an editable view.
    /// </summary>
    public void ShowArrangeWindowsDialog()
    {
        if (_vscodeTracker == null) return;
        using var dlg = new ArrangeWindowsDialog(_desktopService, _settings, _vscodeTracker);
        if (dlg.ShowDialog() == DialogResult.OK) RefreshAllOverlays();
    }

    /// <summary>Overlay command: pull every open VS Code window onto one screen, each staying
    /// on its own virtual desktop and in its own snap position.</summary>
    public void MoveAllVsCodeToScreen(MonitorDescriptor screen)
    {
        _vscodeTracker?.MoveAllToScreen(screen);
        RefreshAllOverlays();
    }

    /// <summary>Overlay command: put every open VS Code window back where this screen setup
    /// remembers it (desktop + screen + snap position).</summary>
    public void RestoreVsCodeLayout()
    {
        _vscodeTracker?.MoveAllToRemembered();
        RefreshAllOverlays();
    }

    /// <summary>Live snapshot of the taskbar overlays — used by the reassign flyout for
    /// screen-point hit-testing of drop targets.</summary>
    public IReadOnlyList<TaskbarOverlay> Overlays => _overlays.ToArray();

    /// <summary>Paint a drop-target ring on the given desktop's button on every overlay
    /// (null clears). Driven by the reassign flyout during a drag.</summary>
    public void SetDropHighlight(Guid? desktopId)
    {
        foreach (var o in _overlays.ToArray())
            if (!o.IsDisposed) o.SetDropHighlight(desktopId);
    }

    /// <summary>
    /// Apply a user's manual correction from the reassign flyout: move the session's VS Code
    /// window to <paramref name="targetDesktop"/> (so the passive tracker remembers it
    /// durably), re-bind the live indicator immediately, and record the resolution so the next
    /// hook from this session resolves there before the tracker's next scan.
    /// </summary>
    public void ReassignSession(string source, string sessionId, string cwd, Guid targetDesktop)
    {
        if (targetDesktop == Guid.Empty) return;

        var candidates = _alertServer?.RootNameCandidatesFor(cwd) ?? Array.Empty<string>();
        IntPtr hwnd = FindVsCodeWindow(candidates);
        bool moved = false;
        if (hwnd != IntPtr.Zero && _desktopService.GetDesktopForWindow(hwnd) != targetDesktop)
            moved = _desktopService.MoveWindowToDesktop(hwnd, targetDesktop);

        _sessionState?.MoveSessionToDesktop(source, sessionId, targetDesktop);
        _sessionState?.RecordResolution(sessionId, null, cwd, targetDesktop);
        var pinned = _alertServer?.PinSessionFolders(sessionId, targetDesktop) ?? Array.Empty<string>();
        RefreshAllOverlays();
        Log.Resolver($"reassign session={sessionId} src={source} cwd={cwd} -> {targetDesktop} " +
                     $"window={(hwnd == IntPtr.Zero ? "not-found" : moved ? "moved" : "already-there")} " +
                     $"pinned=[{string.Join(";", pinned)}]");
    }

    /// <summary>Dismiss one session's indicator (the flyout's per-row "✕").</summary>
    public void RemoveSession(string source, string sessionId)
    {
        _sessionState?.RemoveSession(source, sessionId);
        RefreshAllOverlays();
    }

    /// <summary>Find an open VS Code workspace window whose rootName is in <paramref name="rootNames"/>.</summary>
    internal static IntPtr FindVsCodeWindow(IReadOnlyCollection<string> rootNames)
    {
        if (rootNames.Count == 0) return IntPtr.Zero;
        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            var clsBuf = new char[64];
            int clsLen = NativeMethods.GetClassName(hwnd, clsBuf, clsBuf.Length);
            if (new string(clsBuf, 0, clsLen) != "Chrome_WidgetWin_1") return true;
            int len = NativeMethods.GetWindowTextLength(hwnd);
            if (len == 0) return true;
            var sb = new System.Text.StringBuilder(len + 1);
            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
            var ws = VsCodeTracker.ExtractWorkspace(sb.ToString());
            if (ws != null && rootNames.Contains(ws)) { found = hwnd; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
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
            _settings.InvalidateLayout();   // different screen setup => different remembered layout
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
        _sweepTimer?.Stop();
        _sweepTimer?.Dispose();
        _alertServer?.Dispose();
        _alertPulse?.Dispose();
        _vscodeTracker?.Dispose();
        _projects?.Dispose();
        _hotkeys?.Dispose();
        if (_foregroundHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_foregroundHook);
        if (_locationHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_locationHook);
        foreach (var o in _overlays) { try { o.Close(); o.Dispose(); } catch { } }
        _overlays.Clear();
        base.OnFormClosing(e);
    }
}
