using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace DesktopNames;

/// <summary>
/// Borderless overlay form that sits on the taskbar showing virtual desktop names.
/// Styled to match Windows 11 taskbar appearance.
/// </summary>
internal sealed class TaskbarOverlay : Form
{
    private readonly TaskbarData _taskbar;
    private readonly DesktopService _desktopService;
    private readonly Settings _settings;
    private readonly SessionState? _sessionState;
    private readonly AlertPulse? _alertPulse;
    private List<DesktopInfo> _desktops = new();
    private readonly List<DesktopButton> _buttons = new();
    private int _hoveredIndex = -1;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private bool _isDarkMode;
    private readonly ContextMenuStrip _contextMenu;
    // The overlay is WS_EX_NOACTIVATE, so the menu never owns the foreground and WinForms'
    // own click-outside dismissal never sees clicks landing in other apps. Watch for the
    // foreground window changing away from whatever was active when the menu opened and
    // close it ourselves — otherwise it hangs around on top of whatever you clicked next.
    private readonly System.Windows.Forms.Timer _menuBlurTimer = new() { Interval = 200 };
    private IntPtr _menuOpenForeground;
    private readonly ToolTip _stateTooltip = new() { InitialDelay = 400, ReshowDelay = 100, OwnerDraw = true };
    private Guid _lastTooltipDesktop = Guid.Empty;
    // Owner-drawn so the Claude message line renders italic and the cwd dim. _tooltipText holds
    // what's currently shown (set at every Show call) so Popup/Draw can measure and render it.
    private string _tooltipText = "";
    private readonly Font _ttFont = (Font)(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont).Clone();
    private Font _ttItalic = null!;
    private Font _ttBold = null!;
    private static readonly TextFormatFlags TipFlags =
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
    private const int TipIndent = 14;

    // Reassign-drag state. Plain press-drag on any desktop button starts a *reorder* drag
    // (moves the desktop to the drop position via DesktopService.MoveDesktopToIndex). A
    // long-press (~400ms) on a *lit* desktop opens the session flyout instead.
    // _dropHighlightDesktop rings the button currently under a session-reassign drag from
    // the flyout; _dropInsertionIndex is the slot for a reorder drag in progress.
    private Guid? _dropHighlightDesktop;
    private int _dragCandidateButton = -1;
    private Point _dragCandidatePoint;
    private bool _dragSuppressClick;
    private SessionFlyout? _flyout;
    private readonly System.Windows.Forms.Timer _longPressTimer = new() { Interval = 400 };
    private bool _reordering;
    private int _dropInsertionIndex = -1;

    // Defer single-click switching so a double-click can pre-empt it.
    private readonly System.Windows.Forms.Timer _singleClickTimer = new();
    private Action? _pendingSingleClickAction;


    public TaskbarData Taskbar => _taskbar;

    // Win11 taskbar style constants
    private static readonly Font ButtonFont = new("Segoe UI Variable Text", 9f, FontStyle.Regular);
    private static readonly Font ButtonFontBold = new("Segoe UI Variable Text", 9f, FontStyle.Bold);
    private const int ButtonPaddingH = 0;
    private const int ButtonPaddingV = 3;
    private const int ButtonSpacing = 0;
    private const int ButtonRadius = 4;

    // Transparent background - use a color key for true transparency
    private static readonly Color TransparencyColor = Color.FromArgb(1, 1, 1);

    public TaskbarOverlay(TaskbarData taskbar, DesktopService desktopService, Settings settings,
                          SessionState? sessionState, AlertPulse? alertPulse)
    {
        _taskbar = taskbar;
        _desktopService = desktopService;
        _settings = settings;
        _sessionState = sessionState;
        _alertPulse = alertPulse;
        _isDarkMode = DetectDarkMode();

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;

        // Use TransparencyKey so the background is fully transparent
        BackColor = TransparencyColor;
        TransparencyKey = TransparencyColor;

        // Polling timer - safety net. Real refreshes come from WinEventHook + WM_DISPLAYCHANGE.
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _refreshTimer.Tick += (_, _) => RefreshDesktops();
        _refreshTimer.Start();

        _ttItalic = new Font(_ttFont, FontStyle.Italic);
        _ttBold = new Font(_ttFont, FontStyle.Bold);
        _stateTooltip.Popup += OnTooltipPopup;
        _stateTooltip.Draw += OnTooltipDraw;

        _contextMenu = new ContextMenuStrip();
        _contextMenu.Opening += (_, _) =>
        {
            BuildContextMenu();
            _menuOpenForeground = NativeMethods.GetForegroundWindow();
            _menuBlurTimer.Start();
        };
        _contextMenu.Closed += (_, _) =>
        {
            _menuBlurTimer.Stop();
            var after = _afterMenuClosed;
            _afterMenuClosed = null;
            if (after != null) BeginInvoke(after);
        };
        _menuBlurTimer.Tick += (_, _) =>
        {
            var fg = NativeMethods.GetForegroundWindow();
            if (fg != _menuOpenForeground && fg != Handle && fg != _contextMenu.Handle)
                _contextMenu.Close(ToolStripDropDownCloseReason.AppFocusChange);
        };

        _settings.Changed += () =>
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(RefreshDesktops);
            else RefreshDesktops();
        };

        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeave += (_, _) =>
        {
            _hoveredIndex = -1;
            _stateTooltip.Hide(this);
            _lastTooltipDesktop = Guid.Empty;
            Invalidate();
        };
        MouseUp += OnMouseUp;
        MouseDoubleClick += OnMouseDoubleClick;

        _singleClickTimer.Tick += (_, _) =>
        {
            _singleClickTimer.Stop();
            var action = _pendingSingleClickAction;
            _pendingSingleClickAction = null;
            action?.Invoke();
        };

        _longPressTimer.Tick += OnLongPressTick;
    }

    private void OnMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        // Cancel any pending single-click switch — user is renaming, not switching.
        _singleClickTimer.Stop();
        _pendingSingleClickAction = null;
        // A double-click isn't a long-press or a reorder drag — kill those candidates too.
        _longPressTimer.Stop();
        _dragCandidateButton = -1;

        if (e.Button != MouseButtons.Left) return;
        foreach (var btn in _buttons)
        {
            if (btn.Bounds.Contains(e.Location))
            {
                if (btn.Desktop.Id == SessionState.UnresolvedDesktopId) break; // synthetic "?" — not renameable
                BeginInlineRename(btn);
                break;
            }
        }
    }

    /// <summary>
    /// Repaint only the button for this desktop. Driven by AlertPulse during animations
    /// and by SessionState.Changed for instant transitions. Cheap when the button isn't
    /// on this overlay (different monitor / no match).
    /// </summary>
    public void InvalidateForDesktop(Guid desktopId)
    {
        if (IsDisposed || !Visible) return;
        foreach (var btn in _buttons)
        {
            if (btn.Desktop.Id != desktopId) continue;
            // If the glyph changed the label's width, the whole strip must reflow so the
            // wider/narrower text stays exactly fitted (no clip, no slack). Pulse frames pass
            // the same label, so this short-circuits to a cheap per-button repaint.
            string live = GetButtonLabel(btn.Desktop, _sessionState?.GetGlyph(desktopId));
            if (live != btn.LaidOutLabel) { RecalculateLayout(); RepositionOnTaskbar(); Invalidate(); }
            else Invalidate(btn.Bounds);
            return;
        }
    }

    /// <summary>
    /// Hotkey entry point: open the per-desktop context menu for the currently active
    /// desktop. The right-clicked-target is pre-populated so the per-desktop section
    /// appears at the top of the menu. Returns true if this overlay handled it.
    /// </summary>
    public bool TryOpenContextMenuForCurrentDesktop()
    {
        foreach (var btn in _buttons)
        {
            if (btn.Desktop.IsCurrent)
            {
                _rightClickedDesktop = btn.Desktop;
                _contextMenu.Show(this, new Point(btn.Bounds.Left, btn.Bounds.Bottom));
                return true;
            }
        }
        return false;
    }

    private void BeginInlineRename(DesktopButton btn)
    {
        var desktop = btn.Desktop;
        var screenRect = RectangleToScreen(btn.Bounds);

        // Snapshot the desktop list at click time so validation doesn't shift
        // mid-edit if a refresh happens.
        var snapshot = _desktops;

        bool IsValid(string candidate)
        {
            var trimmed = (candidate ?? "").Trim();
            if (trimmed.Length == 0) return false;
            if (trimmed.Equals(desktop.Name, StringComparison.Ordinal)) return true;
            foreach (var d in snapshot)
            {
                if (d.Id == desktop.Id) continue;
                if (d.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        using var popup = new RenameEditPopup(desktop.Name, screenRect, IsValid, _isDarkMode);
        var result = popup.ShowDialog();
        Log.Ui($"rename '{desktop.Name}' -> {result} '{popup.Value}' ({popup.CloseReason})");
        if (result == DialogResult.OK)
        {
            var newName = popup.Value;
            if (newName.Length > 0 && newName != desktop.Name)
                _desktopService.RenameDesktop(desktop.Id, newName);
        }
    }

    private DesktopInfo? _rightClickedDesktop;
    private Action? _afterMenuClosed;

    private void BuildContextMenu()
    {
        _contextMenu.Items.Clear();

        // The synthetic "?" button has none of the real-desktop actions. Show a tiny
        // diagnostic menu, then stop — no point chaining the global section below.
        if (_rightClickedDesktop != null && _rightClickedDesktop.Id == SessionState.UnresolvedDesktopId)
        {
            BuildUnresolvedContextMenu();
            return;
        }

        // Per-desktop section: full operations on the right-clicked target.
        if (_rightClickedDesktop != null)
        {
            var d = _rightClickedDesktop;
            _contextMenu.Items.Add(new ToolStripMenuItem($"— {d.Name} —") { Enabled = false });

            if (!d.IsCurrent)
            {
                _contextMenu.Items.Add(
                    LabelWithShortcut("Select", $"SwitchToDesktop{d.Index + 1}"),
                    null, (_, _) => _desktopService.SwitchToDesktop(d));
            }
            // Item Click fires while the menu is still up; the popup must wait for it to close,
            // or the menu's teardown takes focus back and the popup cancels itself.
            _contextMenu.Items.Add("Rename...", null, (_, _) => _afterMenuClosed = () => PromptRename(d));

            // Make first / Make last operate on the right-clicked desktop. The COM API
            // exposes "move current desktop to index", so non-current targets are
            // handled by switching first — the user ends up on the moved desktop, which
            // matches the mental model of "this is the desktop I'm operating on".
            _contextMenu.Items.Add(
                LabelWithShortcut("Make first", d.IsCurrent ? "MoveDesktopFirst" : ""),
                null,
                (_, _) => { if (!d.IsCurrent) _desktopService.SwitchToDesktop(d); _desktopService.MoveCurrentDesktopToFirst(); });
            _contextMenu.Items.Add(
                LabelWithShortcut("Make last", d.IsCurrent ? "MoveDesktopLast" : ""),
                null,
                (_, _) => { if (!d.IsCurrent) _desktopService.SwitchToDesktop(d); _desktopService.MoveCurrentDesktopToLast(); });

            // Clear highlight: the lightweight "dormant" — dismiss this desktop's colour now;
            // it returns on the next state change, with no toggle to undo. Disabled when the
            // desktop has no active highlight to clear.
            var (clearState, _, _) = _sessionState?.GetAggregate(d.Id) ?? default;
            var clearItem = new ToolStripMenuItem("Clear highlight")
            {
                Enabled = clearState != StateKind.None || _settings.IsDesktopHighlighted(d.Id),
                ShortcutKeyDisplayString = "Ctrl+Shift+click",
                ToolTipText = "Dismiss this desktop's colour until Claude's next state change"
            };
            clearItem.Click += (_, _) => Program.Host?.ClearHighlight(d.Id);
            _contextMenu.Items.Add(clearItem);

            // Manual blue marker — a persistent user highlight, independent of Claude state.
            var highlightItem = new ToolStripMenuItem("Blue highlight")
            {
                Checked = _settings.IsDesktopHighlighted(d.Id),
                CheckOnClick = false,
                ShortcutKeyDisplayString = "Shift+click",
                ToolTipText = "Mark blue (clears the current Claude colour); auto-clears on Claude's next status"
            };
            highlightItem.Click += (_, _) => Program.Host?.ToggleBlueHighlight(d.Id);
            _contextMenu.Items.Add(highlightItem);

            var existingNote = _settings.GetDesktopNote(d.Id);
            var notesLabel = existingNote.Length > 0
                ? $"Notes...  (\"{Truncate(existingNote, 24)}\")"
                : "Notes...";
            _contextMenu.Items.Add(notesLabel, null, (_, _) => PromptNotes(d));

            var hideOnThis = new ToolStripMenuItem("Hide overlay on this desktop")
            {
                Checked = _settings.IsDesktopHidden(d.Id),
                CheckOnClick = false
            };
            hideOnThis.Click += (_, _) => _settings.ToggleDesktopHidden(d.Id);
            _contextMenu.Items.Add(hideOnThis);

            _contextMenu.Items.Add(new ToolStripSeparator());
        }

        // Global section: overlay-wide settings + Windows-built-in shortcuts.
        var hideItem = new ToolStripMenuItem(
            LabelWithShortcut(_settings.Hidden ? "Show overlay" : "Hide overlay", "ToggleHide"));
        hideItem.Click += (_, _) => { _settings.Hidden = !_settings.Hidden; _settings.Save(); };
        _contextMenu.Items.Add(hideItem);

        _contextMenu.Items.Add(new ToolStripSeparator());

        var allItem = new ToolStripMenuItem("Show on all desktops") { Checked = !_settings.OnlyOnMainDesktop, CheckOnClick = false };
        allItem.Click += (_, _) => { _settings.OnlyOnMainDesktop = false; _settings.Save(); };
        _contextMenu.Items.Add(allItem);

        var mainOnlyItem = new ToolStripMenuItem("Only on main desktop") { Checked = _settings.OnlyOnMainDesktop, CheckOnClick = false };
        mainOnlyItem.Click += (_, _) => { _settings.OnlyOnMainDesktop = true; _settings.Save(); };
        _contextMenu.Items.Add(mainOnlyItem);

        _contextMenu.Items.Add(new ToolStripSeparator());

        _contextMenu.Items.Add("New desktop  (Win+Ctrl+D)", null, (_, _) => _desktopService.CreateDesktop());
        _contextMenu.Items.Add("Close current desktop  (Win+Ctrl+F4)", null, (_, _) => _desktopService.RemoveCurrentDesktop());
        _contextMenu.Items.Add("Arrange VS Code windows…", null,
            (_, _) => Program.Host?.ShowArrangeWindowsDialog());

        var screens = MonitorRef.EnumerateAll();
        if (screens.Count > 1)
        {
            var moveAll = new ToolStripMenuItem("Move all VS Code windows to");
            for (int i = 0; i < screens.Count; i++)
            {
                var s = screens[i];
                moveAll.DropDownItems.Add($"{s.Caption(i)}  ({s.Width}×{s.Height}{(s.IsPrimary ? ", primary" : "")})", null,
                    (_, _) => Program.Host?.MoveAllVsCodeToScreen(s));
            }
            _contextMenu.Items.Add(moveAll);
        }
        _contextMenu.Items.Add($"Restore VS Code layout  ({_settings.Layout().DisplayName})", null,
            (_, _) => Program.Host?.RestoreVsCodeLayout());

        // Global rename when no per-desktop section already covers it.
        if (_rightClickedDesktop == null)
        {
            var renameCurrent = new ToolStripMenuItem("Rename current desktop...");
            renameCurrent.Click += (_, _) =>
            {
                var current = _desktops.FirstOrDefault(d => d.IsCurrent);
                if (current != null) _afterMenuClosed = () => PromptRename(current);
            };
            _contextMenu.Items.Add(renameCurrent);
        }

        _contextMenu.Items.Add(new ToolStripSeparator());

        _contextMenu.Items.Add(LabelWithShortcut("Move desktop left",  "MoveDesktopLeft"),  null, (_, _) => _desktopService.MoveCurrentDesktopBy(-1));
        _contextMenu.Items.Add(LabelWithShortcut("Move desktop right", "MoveDesktopRight"), null, (_, _) => _desktopService.MoveCurrentDesktopBy(1));
        if (_rightClickedDesktop == null)
        {
            _contextMenu.Items.Add(LabelWithShortcut("Make first", "MoveDesktopFirst"), null, (_, _) => _desktopService.MoveCurrentDesktopToFirst());
            _contextMenu.Items.Add(LabelWithShortcut("Make last",  "MoveDesktopLast"),  null, (_, _) => _desktopService.MoveCurrentDesktopToLast());
        }

        _contextMenu.Items.Add(new ToolStripSeparator());

        _contextMenu.Items.Add(LabelWithShortcut("Previous waiting Claude", "PrevWaitingDesktop"), null,
            (_, _) => Program.Host?.JumpToWaitingClaude(-1));
        _contextMenu.Items.Add(LabelWithShortcut("Next waiting Claude", "NextWaitingDesktop"), null,
            (_, _) => Program.Host?.JumpToWaitingClaude(+1));

        _contextMenu.Items.Add(new ToolStripSeparator());

        _contextMenu.Items.Add("Keyboard shortcuts...", null, (_, _) => Program.ShowShortcuts());
        _contextMenu.Items.Add("Open settings.json", null, (_, _) => Program.OpenSettingsFile());
        _contextMenu.Items.Add($"About DesktopNames v{Program.GetAppVersion()}").Enabled = false;
        _contextMenu.Items.Add("Exit DesktopNames", null, (_, _) => Program.Host?.Close());
    }

    /// <summary>Append a "(Win+Alt+H)"-style shortcut hint pulled from settings.</summary>
    private string LabelWithShortcut(string label, string actionKey)
    {
        if (string.IsNullOrEmpty(actionKey)) return label;
        if (_settings.Hotkeys.TryGetValue(actionKey, out var s) && !string.IsNullOrWhiteSpace(s))
            return $"{label}  ({PrettifyHotkey(s)})";
        return label;
    }

    /// <summary>
    /// Turn the binding strings parsed by HotkeyParser into typographically nicer labels.
    /// "Win+Oem4" → "Win+[", "Ctrl+Oem6" → "Ctrl+]", etc. The settings value stays the same;
    /// only the menu display changes.
    /// </summary>
    private static string PrettifyHotkey(string binding)
    {
        var parts = binding.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = parts[i].ToLowerInvariant() switch
            {
                "oem3" or "backtick" or "tilde" => "`",
                "oem1" or "oemsemicolon" or "semicolon" => ";",
                "oem2" or "oemquestion" or "slash" => "/",
                "oem4" or "oemopenbrackets" or "openbracket" => "[",
                "oem5" or "oempipe" or "backslash" => "\\",
                "oem6" or "oemclosebrackets" or "closebracket" => "]",
                "oem7" or "oemquotes" or "quote" => "'",
                "oemplus" or "plus" => "=",
                "oemminus" or "minus" => "-",
                "oemcomma" or "comma" => ",",
                "oemperiod" or "period" => ".",
                _ => parts[i]   // unchanged — letters, F-keys, arrows etc. stay readable
            };
        }
        return string.Join("+", parts);
    }

    internal static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    /// <summary>Right-click menu for the synthetic "?" unresolved button.</summary>
    private void BuildUnresolvedContextMenu()
    {
        var (_, _, tip) = _sessionState?.GetAggregate(SessionState.UnresolvedDesktopId) ?? default;
        int count = _sessionState?.LiveCount(SessionState.UnresolvedDesktopId) ?? 0;
        _contextMenu.Items.Add(new ToolStripMenuItem($"— {count} unresolved session{(count == 1 ? "" : "s")} —") { Enabled = false });
        if (!string.IsNullOrEmpty(tip))
        {
            foreach (var line in SessionState.StripTipMarkers(tip).Split('\n').Where(l => l.Length > 0))
                _contextMenu.Items.Add(new ToolStripMenuItem(line) { Enabled = false });
        }
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("Open diagnostic log...", null, (_, _) => OpenDiagnosticLog());
        _contextMenu.Items.Add("Clear all unresolved", null, (_, _) =>
        {
            _sessionState?.ClearUnresolved();
        });
    }

    /// <summary>Open %APPDATA%\DesktopNames\desktopnames.log in the user's default editor.</summary>
    private static void OpenDiagnosticLog()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopNames", "desktopnames.log");
        try
        {
            if (!File.Exists(path))
            {
                MessageBox.Show($"No log yet at:\n{path}\n\nEnable Settings.AlertLogEnabled and trigger an alert first.",
                    "DesktopNames", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not open log: " + ex.Message, "DesktopNames",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void PromptNotes(DesktopInfo d)
    {
        var existing = _settings.GetDesktopNote(d.Id);

        // Anchor above the right-clicked button on this overlay. Fall back to the
        // overlay's own screen rect if the button can't be found (shouldn't happen).
        Rectangle anchorScreen = RectangleToScreen(ClientRectangle);
        foreach (var btn in _buttons)
        {
            if (btn.Desktop.Id == d.Id)
            {
                anchorScreen = RectangleToScreen(btn.Bounds);
                break;
            }
        }

        var note = NotesDialog.Show("Notes", $"What are you working on at \"{d.Name}\"?", existing, anchorScreen);
        if (note != null) _settings.SetDesktopNote(d.Id, note);
    }

    private void PromptRename(DesktopInfo desktop)
    {
        // Prefer the anchored inline popup (same one double-click and Win+Ins use). The
        // screen-centered InputDialog could land on the wrong monitor and become a stuck
        // invisible modal — see DN incident log 2026-05-20.
        foreach (var btn in _buttons)
        {
            if (btn.Desktop.Id == desktop.Id)
            {
                BeginInlineRename(btn);
                return;
            }
        }
        // Fallback when the desktop isn't represented on this overlay (rare — e.g. a
        // global "Rename current desktop..." invoked while the current desktop is hidden).
        var newName = InputDialog.Show("Rename desktop", $"Rename \"{desktop.Name}\" to:", desktop.Name);
        if (!string.IsNullOrWhiteSpace(newName) && newName != desktop.Name)
            _desktopService.RenameDesktop(desktop.Id, newName);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            const int WS_EX_NOACTIVATE = 0x08000000;
            const int WS_EX_TOOLWINDOW = 0x00000080;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEACTIVATE = 0x0021;
        const int MA_NOACTIVATE = 3;

        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = (IntPtr)MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
    }

    public void RefreshDesktops()
    {
        EnsurePinned();
        var newDesktops = _desktopService.GetDesktops();

        // Append the synthetic "?" entry at the end of the list when there are sessions
        // DN couldn't bind to any real desktop. The button is virtual (no real desktop)
        // and clicking it opens the log; hover lists the unresolved sessions.
        if (_sessionState != null)
        {
            var (s, _, _) = _sessionState.GetAggregate(SessionState.UnresolvedDesktopId);
            if (s != StateKind.None)
            {
                newDesktops.Add(new DesktopInfo
                {
                    Index = int.MaxValue,
                    Id = SessionState.UnresolvedDesktopId,
                    Name = "?",
                    IsCurrent = false
                });
            }
        }

        // Resolve current desktop for settings-based visibility checks
        var currentDesktop = newDesktops.FirstOrDefault(d => d.IsCurrent);
        bool hiddenByCurrentDesktop = currentDesktop != null && _settings.IsDesktopHidden(currentDesktop.Id);
        bool hiddenByMainOnly = _settings.OnlyOnMainDesktop && currentDesktop != null && currentDesktop.Index != 0;
        bool hiddenByFullscreen = IsFullscreenOnMonitor();

        bool shouldHide = _settings.Hidden || hiddenByCurrentDesktop || hiddenByMainOnly || hiddenByFullscreen;

        // Keep desktop list in sync even while hidden (menu still uses it)
        bool desktopsChanged = DesktopsChanged(newDesktops);
        if (desktopsChanged) _desktops = newDesktops;

        if (shouldHide && Visible)
        {
            Visible = false;
            return;
        }
        if (!shouldHide && !Visible)
        {
            Visible = true;
        }
        if (shouldHide) return;
        int prevStripHeight = _taskbar.Bounds.Height;

        // Always reposition — picks up taskbar edge/move changes without waiting for a desktop change.
        RepositionOnTaskbar();
        bool stripChanged = _taskbar.Bounds.Height != prevStripHeight;

        if (desktopsChanged || stripChanged)
        {
            _desktops = newDesktops;
            RecalculateLayout();
            RepositionOnTaskbar();
        }

        // Always repaint while visible. This catches settings-only changes that don't
        // shift the layout but do change pixels — dormant toggle (italic), hidden-on-this
        // toggle, dark-mode flip, etc. Invalidate is cheap (just marks the form dirty).
        Invalidate();

        // Theme change still tracked so future paints pick up the new colors.
        bool dark = DetectDarkMode();
        if (dark != _isDarkMode) _isDarkMode = dark;
    }

    private bool IsFullscreenOnMonitor()
    {
        // Walk the global Z-order from top. The topmost candidate that sits on this
        // monitor decides. Cloaked windows (i.e. on another virtual desktop) are
        // filtered out, so we only consider what's visible on the *current* desktop.
        //
        // Using GetForegroundWindow alone is wrong: on a multi-monitor setup, focus
        // can be on monitor B while monitor A has a fullscreen app — monitor A's
        // overlay must still hide. Walking Z-order makes the check per-monitor.
        IntPtr hwnd = NativeMethods.GetTopWindow(IntPtr.Zero);
        while (hwnd != IntPtr.Zero)
        {
            if (hwnd != Handle && IsRelevantTopWindow(hwnd))
            {
                var mon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
                if (mon == _taskbar.MonitorHandle)
                    return CoversMonitor(hwnd, mon);
            }
            hwnd = NativeMethods.GetWindow(hwnd, NativeMethods.GW_HWNDNEXT);
        }
        return false;
    }

    private static bool IsRelevantTopWindow(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindowVisible(hwnd)) return false;

        int style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE);
        if ((style & NativeMethods.WS_CHILD) != 0) return false;

        int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0 &&
            (ex & NativeMethods.WS_EX_APPWINDOW) == 0) return false;

        // Cloaked = on a different virtual desktop (or UWP suspended). Must skip,
        // otherwise a fullscreen app on Desktop A would keep Desktop B's overlay hidden.
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
            && cloaked != 0) return false;

        var cls = new char[256];
        int len = NativeMethods.GetClassName(hwnd, cls, cls.Length);
        var name = new string(cls, 0, len);
        if (name is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW") return false;

        return true;
    }

    private static bool CoversMonitor(IntPtr hwnd, IntPtr mon)
    {
        var mi = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfo(mon, ref mi);
        NativeMethods.GetWindowRect(hwnd, out var r);
        // Compare against monitor rect (including taskbar area). A normal maximized
        // window's rect stops at the work area, so this correctly excludes them —
        // only true fullscreen (F11, exclusive, borderless) covers the whole monitor.
        return r.Left <= mi.rcMonitor.Left &&
               r.Top <= mi.rcMonitor.Top &&
               r.Right >= mi.rcMonitor.Right &&
               r.Bottom >= mi.rcMonitor.Bottom;
    }

    private bool DesktopsChanged(List<DesktopInfo> newDesktops)
    {
        if (newDesktops.Count != _desktops.Count) return true;
        for (int i = 0; i < newDesktops.Count; i++)
        {
            if (newDesktops[i].Name != _desktops[i].Name ||
                newDesktops[i].IsCurrent != _desktops[i].IsCurrent)
                return true;
        }
        return false;
    }

    private string GetButtonLabel(DesktopInfo desktop, string? glyph = null)
    {
        if (desktop.Id == SessionState.UnresolvedDesktopId) return "?";
        // The glyph replaces the period between number and name when a session state is
        // active on this desktop. E.g. "3. Mobilität" → "3? Mobilität" when asking,
        // "3E Mobilität" when running Edit, etc.
        string sep = string.IsNullOrEmpty(glyph) ? "." : glyph!;
        return $"{desktop.Index + 1}{sep} {desktop.Name}";
    }

    private void RecalculateLayout()
    {
        _buttons.Clear();

        int stripHeight = Math.Max(_taskbar.Bounds.Height, 24);
        // Each button is half the taskbar height so two rows fit stacked.
        int buttonHeight = Math.Max(stripHeight / 2, 14);

        // Measure each label at its *current* width — the actual glyph (or "." when idle),
        // not a worst-case placeholder. Reserving for the widest glyph left a visible sliver
        // of slack on every button; with centered text that slack split to both sides and read
        // as wide gaps. A button is re-laid-out when its glyph changes (InvalidateForDesktop
        // detects the label-width change), so a wider glyph never overflows.
        var widths = new List<int>(_desktops.Count);
        var labels = new List<string>(_desktops.Count);
        foreach (var desktop in _desktops)
        {
            string label = GetButtonLabel(desktop, _sessionState?.GetGlyph(desktop.Id));
            labels.Add(label);
            var textSize = TextRenderer.MeasureText(label, ButtonFontBold,
                new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            widths.Add(textSize.Width + ButtonPaddingH * 2);
        }

        // Split into two rows, front-loaded, so the total block width = max(row1, row2)
        // is as small as possible while keeping order. With N buttons, row1 gets
        // ceil(N/2), row2 gets the rest. This keeps the block leftmost and compact.
        int n = _desktops.Count;
        int row1Count = (n + 1) / 2;

        int row1Width = ButtonSpacing;
        int row2Width = ButtonSpacing;
        for (int i = 0; i < n; i++)
        {
            int w = widths[i];
            int row = i < row1Count ? 0 : 1;
            int xStart = row == 0 ? row1Width : row2Width;
            int y = row * buttonHeight;

            _buttons.Add(new DesktopButton
            {
                Desktop = _desktops[i],
                LaidOutLabel = labels[i],
                Bounds = new Rectangle(xStart, y, w, buttonHeight)
            });

            if (row == 0) row1Width += w + ButtonSpacing;
            else row2Width += w + ButtonSpacing;
        }

        int totalWidth = Math.Max(row1Width, row2Width);
        ClientSize = new Size(totalWidth, stripHeight);
    }

    /// <summary>
    /// Pin the overlay to every virtual desktop so it stays visible as the user switches
    /// desktops. Replaces the old implicit "SetWindowPos re-pull" which recent Win11 builds
    /// no longer honour (the window would get stuck on its home desktop — usually #1). Cheap
    /// and idempotent: re-affirms only when the OS has dropped the pin (e.g. after a taskbar
    /// or display-change rebuild gives us a fresh handle).
    /// </summary>
    private void EnsurePinned()
    {
        if (!VdaDll.IsLoaded || !IsHandleCreated) return;
        try { if (VdaDll.IsPinnedWindow(Handle) == 0) VdaDll.PinWindow(Handle); }
        catch { }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        EnsurePinned();
    }

    private void RepositionOnTaskbar()
    {
        // Re-derive the taskbar strip from monitor work area each refresh,
        // so changes to taskbar position (top/bottom) are picked up.
        var monInfo = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfo(_taskbar.MonitorHandle, ref monInfo);

        var mon = monInfo.rcMonitor;
        var work = monInfo.rcWork;
        NativeMethods.RECT strip = _taskbar.Bounds;
        uint edge = _taskbar.Edge;

        if (work.Bottom < mon.Bottom)
        {
            edge = NativeMethods.ABE_BOTTOM;
            strip = new NativeMethods.RECT { Left = mon.Left, Top = work.Bottom, Right = mon.Right, Bottom = mon.Bottom };
        }
        else if (work.Top > mon.Top)
        {
            edge = NativeMethods.ABE_TOP;
            strip = new NativeMethods.RECT { Left = mon.Left, Top = mon.Top, Right = mon.Right, Bottom = work.Top };
        }
        else if (work.Left > mon.Left)
        {
            edge = NativeMethods.ABE_LEFT;
            strip = new NativeMethods.RECT { Left = mon.Left, Top = mon.Top, Right = work.Left, Bottom = mon.Bottom };
        }
        else if (work.Right < mon.Right)
        {
            edge = NativeMethods.ABE_RIGHT;
            strip = new NativeMethods.RECT { Left = work.Right, Top = mon.Top, Right = mon.Right, Bottom = mon.Bottom };
        }

        _taskbar.Bounds = strip;
        _taskbar.Edge = edge;

        int overlayX = strip.Left;
        int overlayY = strip.Top;

        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST,
            overlayX, overlayY, Width, Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        // Colors
        var textColor = _isDarkMode ? Color.White : Color.FromArgb(30, 30, 30);
        var textDimColor = _isDarkMode ? Color.FromArgb(180, 180, 180) : Color.FromArgb(100, 100, 100);
        var activeIndicatorColor = Color.FromArgb(0, 120, 212); // Windows accent blue
        var hoverBgColor = _isDarkMode ? Color.FromArgb(55, 55, 55) : Color.FromArgb(220, 220, 220);
        var activeBgColor = _isDarkMode ? Color.FromArgb(50, 50, 50) : Color.FromArgb(230, 230, 230);

        for (int i = 0; i < _buttons.Count; i++)
        {
            var btn = _buttons[i];
            var rect = btn.Bounds;
            var desktop = btn.Desktop;
            bool isHovered = i == _hoveredIndex;

            var (stateKind, _, _) = _sessionState?.GetAggregate(desktop.Id) ?? default;
            int liveCount = _sessionState?.LiveCount(desktop.Id) ?? 0;
            Color? stateBg = stateKind == StateKind.None
                ? null
                : ApplyPulse(ResolveStateBaseColor(stateKind), desktop.Id, stateKind);
            string? glyph = _sessionState?.GetGlyph(desktop.Id);
            string label = GetButtonLabel(desktop, glyph);

            // Manual blue marker the user toggled on this desktop. A live Claude status color
            // still wins (an "asking" signal must never be hidden), but otherwise the blue
            // shows over the plain active/hover pill.
            Color? manualBg = (stateBg == null && _settings.IsDesktopHighlighted(desktop.Id))
                ? ParseColorOrFallback(_settings.AlertHighlightColor, Color.FromArgb(91, 155, 213))
                : null;

            // Background. State color wins over manual/active/hover so the indicator is
            // unmistakable. Without state, the manual marker wins over the active/hover pill.
            Color? bg = stateBg ?? manualBg ?? (desktop.IsCurrent ? activeBgColor
                                   : isHovered ? hoverBgColor
                                   : (Color?)null);
            if (bg.HasValue)
            {
                using var bgBrush = new SolidBrush(bg.Value);
                using var path = RoundedRect(rect, ButtonRadius);
                g.FillPath(bgBrush, path);
            }

            // Active desktop underline indicator — still drawn over state color so the
            // user can see which desktop is current even when colored. Full width of the
            // button minus a few px of inset so it reads as "this whole cell is active".
            if (desktop.IsCurrent)
            {
                const int inset = 4;
                int indicatorWidth = Math.Max(rect.Width - inset * 2, 8);
                int indicatorX = rect.X + inset;
                int indicatorY = rect.Bottom - 3;
                using var indicatorBrush = new SolidBrush(activeIndicatorColor);
                using var indicatorPath = RoundedRect(new Rectangle(indicatorX, indicatorY, indicatorWidth, 3), 1);
                g.FillPath(indicatorBrush, indicatorPath);
            }

            // Text — when state-colored, text contrast comes from the state background
            // (which is always saturated), so we use the bold text color regardless.
            bool bold = desktop.IsCurrent || stateBg.HasValue || manualBg.HasValue;
            var font = bold ? ButtonFontBold : ButtonFont;
            var color = (stateBg ?? manualBg) is Color fill ? PickContrastText(fill)
                       : desktop.IsCurrent ? textColor : textDimColor;
            TextRenderer.DrawText(g, label, font, rect, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

            // Count badge (top-right) — total live sessions on this desktop (matches the
            // flyout list), shown whenever more than one shares the colored button.
            if (stateBg.HasValue && liveCount >= 2)
            {
                DrawCountBadge(g, rect, liveCount);
            }

            // Drop-target ring while a session is being dragged over this button.
            if (_dropHighlightDesktop == desktop.Id)
            {
                using var ringPen = new Pen(activeIndicatorColor, 2f);
                using var ringPath = RoundedRect(Rectangle.Inflate(rect, -1, -1), ButtonRadius);
                g.DrawPath(ringPen, ringPath);
            }

            // Source button is dimmed while it's being dragged to a new slot.
            if (_reordering && i == _dragCandidateButton)
            {
                using var dimBrush = new SolidBrush(Color.FromArgb(140, 0, 0, 0));
                using var dimPath = RoundedRect(rect, ButtonRadius);
                g.FillPath(dimBrush, dimPath);
            }

            // Activity glyph is now inline in the label (above) — replaces the "." between
            // desktop number and name. No corner badge.
        }

        // Reorder insertion indicator: a fat blue bar at the seam where the source would land.
        if (_reordering && _dropInsertionIndex >= 0 && _buttons.Count > 0)
        {
            Rectangle ind;
            if (_dropInsertionIndex >= _buttons.Count)
            {
                var last = _buttons[_buttons.Count - 1].Bounds;
                ind = new Rectangle(last.Right - 2, last.Top, 4, last.Height);
            }
            else
            {
                var b = _buttons[_dropInsertionIndex].Bounds;
                ind = new Rectangle(b.Left - 2, b.Top, 4, b.Height);
            }
            using var brush = new SolidBrush(activeIndicatorColor);
            g.FillRectangle(brush, ind);
        }
    }

    private Color ResolveStateBaseColor(StateKind state) => state switch
    {
        StateKind.Asking => SafeFromHtml(_settings.AlertAskingColor, Color.FromArgb(255, 230, 128)),
        StateKind.Busy   => SafeFromHtml(_settings.AlertBusyColor,   Color.FromArgb(244, 180, 131)),
        StateKind.Error  => SafeFromHtml(_settings.AlertErrorColor,  Color.FromArgb(224, 133, 133)),
        StateKind.Ready  => SafeFromHtml(_settings.AlertReadyColor,  Color.FromArgb(168, 216, 176)),
        _ => Color.Gray
    };

    /// <summary>
    /// Adds fast-pulse (2.5s post-change, up to +25% brighter) and asking-breathe
    /// (continuous ±15%) to the base color. Both no-op when AlertPulseEnabled = false
    /// or AlertPulse is null.
    /// </summary>
    private Color ApplyPulse(Color baseColor, Guid desktopId, StateKind state)
    {
        if (_alertPulse == null) return baseColor;
        double fast = _alertPulse.GetFastPulseIntensity(desktopId);
        double breathe = _alertPulse.GetBreatheIntensity(state);
        // fast in [0..1] brightens by up to 25%. breathe in [-1..1] shifts by up to 15%.
        double t = fast * 0.25 + breathe * 0.15;
        if (Math.Abs(t) < 0.001) return baseColor;
        return t > 0 ? LerpToward(baseColor, Color.White, t)
                     : LerpToward(baseColor, Color.Black, -t);
    }

    private static Color SafeFromHtml(string html, Color fallback)
    {
        try { return ColorTranslator.FromHtml(html); }
        catch { return fallback; }
    }

    /// <summary>Shared helper so the tray legend renders the same colors the overlay paints.</summary>
    public static Color ParseColorOrFallback(string html, Color fallback) => SafeFromHtml(html, fallback);

    private static Color LerpToward(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            255,
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    /// <summary>Pick black or white text based on the perceived brightness of the background.</summary>
    internal static Color PickContrastText(Color bg)
    {
        // ITU-R BT.601 luma: works well for our saturated-color palette.
        double luma = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
        return luma > 0.55 ? Color.FromArgb(20, 20, 20) : Color.White;
    }

    internal static void DrawCountBadge(Graphics g, Rectangle btnRect, int count)
    {
        const int diameter = 14;
        var badgeRect = new Rectangle(btnRect.Right - diameter - 2, btnRect.Top + 2, diameter, diameter);
        using var bg = new SolidBrush(Color.FromArgb(220, 30, 30, 30));
        g.FillEllipse(bg, badgeRect);
        using var ring = new Pen(Color.FromArgb(220, 255, 255, 255), 1f);
        g.DrawEllipse(ring, badgeRect);

        var text = count > 9 ? "9+" : count.ToString();
        using var f = new Font("Segoe UI Variable Text", 7.5f, FontStyle.Bold);
        TextRenderer.DrawText(g, text, f, badgeRect, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    internal static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Remember a left-press on any (real) desktop button. The same press can
    /// resolve into three different gestures depending on what happens next: a quick release
    /// switches desktops, movement begins a reorder drag, holding ~400ms opens the session
    /// flyout (lit desktops only).</summary>
    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        _dragCandidateButton = -1;
        _longPressTimer.Stop();
        if (e.Button != MouseButtons.Left) return;
        for (int i = 0; i < _buttons.Count; i++)
        {
            if (!_buttons[i].Bounds.Contains(e.Location)) continue;
            var d = _buttons[i].Desktop;
            if (d.Id == SessionState.UnresolvedDesktopId) break;     // synthetic "?" — never a source
            _dragCandidateButton = i;
            _dragCandidatePoint = e.Location;
            _longPressTimer.Start();
            break;
        }
    }

    /// <summary>Long-press fired without movement: open the session flyout on a lit desktop.
    /// On unlit (no sessions to reassign), the timer is a no-op and the press can still resolve
    /// into a click or a reorder.</summary>
    private void OnLongPressTick(object? sender, EventArgs e)
    {
        _longPressTimer.Stop();
        if (_reordering) return;
        if (_dragCandidateButton < 0 || _dragCandidateButton >= _buttons.Count) return;
        if (_sessionState == null) return;
        var btn = _buttons[_dragCandidateButton];
        // Open for any desktop that has sessions — including ones whose colour has cleared
        // (all consumed). Those are exactly the stale leftovers the user wants to inspect/remove.
        if (_sessionState.GetSessions(btn.Desktop.Id).Count == 0) return;
        _dragCandidateButton = -1;
        _dragSuppressClick = true;
        _singleClickTimer.Stop();
        _pendingSingleClickAction = null;
        OpenReassignFlyout(btn.Desktop, RectangleToScreen(btn.Bounds));
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        // Press-and-drag begins a reorder. Long-press timer (if pending) gets cancelled here
        // so a moving press never spawns the flyout.
        if (e.Button == MouseButtons.Left && _dragCandidateButton >= 0 && _dragCandidateButton < _buttons.Count)
        {
            if (!_reordering)
            {
                var dz = SystemInformation.DragSize;
                if (Math.Abs(e.X - _dragCandidatePoint.X) > dz.Width / 2 ||
                    Math.Abs(e.Y - _dragCandidatePoint.Y) > dz.Height / 2)
                {
                    _longPressTimer.Stop();
                    _reordering = true;
                    _dragSuppressClick = true;
                    _singleClickTimer.Stop();
                    _pendingSingleClickAction = null;
                    Capture = true;
                }
            }
            if (_reordering)
            {
                int ins = ComputeInsertionIndex(e.Location);
                if (ins != _dropInsertionIndex) { _dropInsertionIndex = ins; Invalidate(); }
            }
            return;
        }

        int newHover = -1;
        for (int i = 0; i < _buttons.Count; i++)
        {
            if (_buttons[i].Bounds.Contains(e.Location))
            {
                newHover = i;
                break;
            }
        }

        if (newHover != _hoveredIndex)
        {
            _hoveredIndex = newHover;
            Cursor = newHover >= 0 ? Cursors.Hand : Cursors.Default;
            UpdateStateTooltip(newHover);
            Invalidate();
        }
    }

    /// <summary>Show note + state on hover. Tooltip is hidden when both are empty.</summary>
    private void UpdateStateTooltip(int hoveredIndex)
    {
        if (hoveredIndex < 0 || hoveredIndex >= _buttons.Count)
        {
            _stateTooltip.Hide(this);
            _lastTooltipDesktop = Guid.Empty;
            return;
        }
        var btn = _buttons[hoveredIndex];
        if (btn.Desktop.Id == _lastTooltipDesktop) return; // already showing for this desktop

        string text = BuildTooltipText(btn.Desktop.Id);
        if (text.Length == 0)
        {
            _stateTooltip.Hide(this);
            _lastTooltipDesktop = Guid.Empty;
            return;
        }
        _lastTooltipDesktop = btn.Desktop.Id;
        _tooltipText = text;
        _stateTooltip.Show(text, this, btn.Bounds.Left, btn.Bounds.Bottom + 2, 5000);
    }

    /// <summary>Combine the desktop note (if any) and Claude state tooltip (if any) into one string.</summary>
    private string BuildTooltipText(Guid desktopId)
    {
        var note = _settings.GetDesktopNote(desktopId);
        string stateTip = "";
        if (_sessionState != null)
        {
            var (state, _, tip) = _sessionState.GetAggregate(desktopId);
            if (state != StateKind.None) stateTip = tip;
        }
        if (note.Length == 0 && stateTip.Length == 0) return "";
        if (note.Length == 0) return stateTip;
        if (stateTip.Length == 0) return note;
        return note + "\n\n" + stateTip;
    }

    // Hover card palette — the same as the session flyout, so the two read as one design.
    private Color TipBack   => _isDarkMode ? Color.FromArgb(40, 40, 40)    : Color.FromArgb(248, 248, 248);
    private Color TipBorder => _isDarkMode ? Color.FromArgb(90, 90, 90)    : Color.FromArgb(190, 190, 190);
    private Color TipFore   => _isDarkMode ? Color.White                   : Color.FromArgb(25, 25, 25);
    private Color TipDimFg  => _isDarkMode ? Color.FromArgb(170, 170, 170) : Color.FromArgb(110, 110, 110);

    /// <summary>One styled line of the hover card. <c>Right</c> is drawn right-aligned (a run time);
    /// <c>Dot</c> paints a state-coloured dot before a session header.</summary>
    private readonly record struct TipLine(string Text, string Right, Font Font, Color Color, int Indent, StateKind? Dot);

    /// <summary>Split the current tooltip text into styled lines by their Tip* markers (see
    /// SessionState): session header bold with a state dot, Claude message italic, background
    /// work with run times, the cwd dim. Unmarked lines (the desktop note) are plain.</summary>
    private IEnumerable<TipLine> TipLines()
    {
        foreach (var raw in _tooltipText.Split('\n'))
        {
            if (raw.Length == 0) { yield return new("", "", _ttFont, Color.Empty, 0, null); continue; }
            char m = raw[0];
            if (m == SessionState.TipHeader)
                yield return new(raw[2..], "", _ttBold, TipFore, TipIndent, (StateKind)(raw[1] - '0'));
            else if (m == SessionState.TipItalic)
                yield return new(raw[1..], "", _ttItalic, TipFore, TipIndent, null);
            else if (m == SessionState.TipBgHead)
                yield return new(raw[1..], "", _ttFont, TipFore, TipIndent, null);
            else if (m == SessionState.TipBg)
            {
                int tab = raw.LastIndexOf('\t');
                yield return tab < 0 ? new(raw[1..], "", _ttFont, TipFore, TipIndent * 2, null)
                                     : new(raw[1..tab], raw[(tab + 1)..], _ttFont, TipFore, TipIndent * 2, null);
            }
            else if (m == SessionState.TipBgSub)
                yield return new(raw[1..], "", _ttItalic, TipDimFg, TipIndent * 3, null);
            else if (m == SessionState.TipDim)
                yield return new(raw[1..], "", _ttFont, TipDimFg, TipIndent, null);
            else
                yield return new(raw, "", _ttFont, TipFore, 0, null);
        }
    }

    private const int TipCardPad = 10;
    private const int TipRightGap = 18;

    private static Size TipMeasure(string text, Font font) =>
        TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), TipFlags);

    private void OnTooltipPopup(object? sender, PopupEventArgs e)
    {
        int w = 0, h = 0;
        foreach (var l in TipLines())
        {
            if (l.Text.Length == 0) { h += l.Font.Height / 2; continue; }
            var sz = TipMeasure(l.Text, l.Font);
            int right = l.Right.Length == 0 ? 0 : TipRightGap + TipMeasure(l.Right, l.Font).Width;
            w = Math.Max(w, l.Indent + sz.Width + right);
            h += sz.Height + 1;
        }
        e.ToolTipSize = new Size(w + TipCardPad * 2, h + TipCardPad * 2);
    }

    private void OnTooltipDraw(object? sender, DrawToolTipEventArgs e)
    {
        var g = e.Graphics;
        using (var back = new SolidBrush(TipBack)) g.FillRectangle(back, e.Bounds);
        using (var pen = new Pen(TipBorder))
            g.DrawRectangle(pen, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int left = e.Bounds.Left + TipCardPad, rightEdge = e.Bounds.Right - TipCardPad;
        int y = e.Bounds.Top + TipCardPad;
        foreach (var l in TipLines())
        {
            if (l.Text.Length == 0) { y += l.Font.Height / 2; continue; }
            int lineH = TipMeasure(l.Text, l.Font).Height;
            if (l.Dot is { } state)
            {
                const int d = 8;
                using var dot = new SolidBrush(SessionFlyout.StateColor(state));
                g.FillEllipse(dot, left + (TipIndent - d) / 2 - 1, y + (lineH - d) / 2, d, d);
            }
            TextRenderer.DrawText(g, l.Text, l.Font, new Point(left + l.Indent, y), l.Color, TipFlags);
            if (l.Right.Length > 0)
            {
                int rw = TipMeasure(l.Right, l.Font).Width;
                TextRenderer.DrawText(g, l.Right, l.Font, new Point(rightEdge - rw, y), TipDimFg, TipFlags);
            }
            y += lineH + 1;
        }
    }

    /// <summary>Show the per-desktop tooltip programmatically (e.g. after switching desktops).</summary>
    public void ShowDesktopTooltipFor(Guid desktopId, int durationMs)
    {
        foreach (var btn in _buttons)
        {
            if (btn.Desktop.Id != desktopId) continue;
            string text = BuildTooltipText(desktopId);
            if (text.Length == 0) return;
            _lastTooltipDesktop = desktopId;
            _tooltipText = text;
            _stateTooltip.Show(text, this, btn.Bounds.Left, btn.Bounds.Bottom + 2, durationMs);
            return;
        }
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            _rightClickedDesktop = null;
            foreach (var b in _buttons)
            {
                if (b.Bounds.Contains(e.Location)) { _rightClickedDesktop = b.Desktop; break; }
            }
            // Show context menu manually since WS_EX_NOACTIVATE suppresses it
            _contextMenu.Show(this, e.Location);
            return;
        }

        if (e.Button != MouseButtons.Left) return;

        // Reorder drag completed — move the source desktop to the drop slot.
        if (_reordering)
        {
            int sourceIdx = _dragCandidateButton >= 0 && _dragCandidateButton < _buttons.Count
                ? _buttons[_dragCandidateButton].Desktop.Index : -1;
            Guid sourceId = sourceIdx >= 0 ? _buttons[_dragCandidateButton].Desktop.Id : Guid.Empty;
            int insertion = _dropInsertionIndex;
            _reordering = false;
            _dropInsertionIndex = -1;
            _dragCandidateButton = -1;
            _dragSuppressClick = false;
            Capture = false;
            Invalidate();

            if (insertion >= 0 && sourceId != Guid.Empty)
            {
                // MoveDesktop is final-index semantics; adjust for the removal of the source
                // when the drop is to the right of where it started.
                int finalIdx = insertion > sourceIdx ? insertion - 1 : insertion;
                if (finalIdx != sourceIdx)
                    _desktopService.MoveDesktopToIndex(sourceId, finalIdx);
            }
            return;
        }

        // A long-press opened the reassign flyout — swallow this release so it doesn't switch desktop.
        if (_dragSuppressClick) { _dragSuppressClick = false; _dragCandidateButton = -1; return; }
        _longPressTimer.Stop();
        _dragCandidateButton = -1;
        // Second click of a double-click: ignore here, MouseDoubleClick handles it.
        if (e.Clicks >= 2) return;

        for (int i = 0; i < _buttons.Count; i++)
        {
            if (_buttons[i].Bounds.Contains(e.Location))
            {
                var desktop = _buttons[i].Desktop;

                // The "?" virtual button: open the reassign flyout listing the unmatched
                // sessions so the user can drag each onto a real desktop. (The diagnostic
                // log moves to the right-click menu.)
                if (desktop.Id == SessionState.UnresolvedDesktopId)
                {
                    OpenReassignFlyout(desktop, RectangleToScreen(_buttons[i].Bounds));
                    break;
                }

                if ((ModifierKeys & Keys.Shift) != 0)
                {
                    if ((ModifierKeys & Keys.Control) != 0) Program.Host?.ClearHighlight(desktop.Id);
                    else Program.Host?.ToggleBlueHighlight(desktop.Id);
                    break;
                }

                if (!desktop.IsCurrent)
                {
                    // Defer the switch by the system double-click time so that a
                    // pending double-click can cancel it. Otherwise the first click
                    // would switch desktops before MouseDoubleClick fires, opening
                    // rename on whatever desktop ended up under the cursor.
                    _pendingSingleClickAction = () =>
                    {
                        Task.Run(() =>
                        {
                            _desktopService.SwitchToDesktop(desktop);
                            Thread.Sleep(300);
                            if (!IsDisposed) BeginInvoke(RefreshDesktops);
                        });
                    };
                    _singleClickTimer.Interval = Math.Max(150, SystemInformation.DoubleClickTime);
                    _singleClickTimer.Stop();
                    _singleClickTimer.Start();
                }
                break;
            }
        }
    }

    /// <summary>Open the per-desktop session flyout — the drag source for reassigning
    /// individual sessions. Anchored to the button on this overlay; replaces any open flyout.</summary>
    private void OpenReassignFlyout(DesktopInfo desktop, Rectangle anchorScreen)
    {
        if (_sessionState == null) return;
        var sessions = _sessionState.GetSessions(desktop.Id);
        if (sessions.Count == 0) return;
        _flyout?.Close();
        _flyout = new SessionFlyout(desktop, sessions, anchorScreen, _isDarkMode);
        _flyout.FormClosed += (_, _) => _flyout = null;
        _flyout.Show();
    }

    /// <summary>Ring the given desktop's button as a drop target (null clears). Driven by the
    /// reassign flyout while a session is being dragged over this overlay.</summary>
    public void SetDropHighlight(Guid? desktopId)
    {
        if (_dropHighlightDesktop == desktopId) return;
        _dropHighlightDesktop = desktopId;
        if (!IsDisposed) Invalidate();
    }

    /// <summary>During a reorder drag, the slot the source would land in if released now.
    /// Returns the 0..N index in the rendered list (0 = first slot, N = past last). -1 when
    /// the cursor isn't over any button.</summary>
    private int ComputeInsertionIndex(Point clientPt)
    {
        for (int i = 0; i < _buttons.Count; i++)
        {
            var b = _buttons[i].Bounds;
            if (!b.Contains(clientPt)) continue;
            if (_buttons[i].Desktop.Id == SessionState.UnresolvedDesktopId) return -1;
            int mid = b.Left + b.Width / 2;
            return clientPt.X < mid ? i : i + 1;
        }
        return -1;
    }

    /// <summary>Map a screen point to the real desktop whose button contains it, or null
    /// (the synthetic "?" button is never a drop target).</summary>
    public DesktopInfo? DesktopAtScreenPoint(Point screenPt)
    {
        if (IsDisposed || !Visible) return null;
        var client = PointToClient(screenPt);
        foreach (var btn in _buttons)
        {
            if (!btn.Bounds.Contains(client)) continue;
            if (btn.Desktop.Id == SessionState.UnresolvedDesktopId) return null;
            return btn.Desktop;
        }
        return null;
    }

    private static bool DetectDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("SystemUsesLightTheme");
            return value is int i && i == 0;
        }
        catch { return true; }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _flyout?.Close();
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        _longPressTimer.Stop();
        _longPressTimer.Dispose();
        _menuBlurTimer.Stop();
        _menuBlurTimer.Dispose();
        _stateTooltip.Dispose();
        _ttFont.Dispose();
        _ttItalic.Dispose();
        _ttBold.Dispose();
        base.OnFormClosing(e);
    }

    private sealed class DesktopButton
    {
        public DesktopInfo Desktop { get; set; } = null!;
        public Rectangle Bounds { get; set; }
        /// <summary>The label this button's width was measured for. When the live label differs
        /// (a glyph appeared/changed), the strip must relayout so the wider text doesn't clip.</summary>
        public string LaidOutLabel { get; set; } = "";
    }
}
