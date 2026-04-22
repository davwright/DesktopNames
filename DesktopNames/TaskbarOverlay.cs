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
    private List<DesktopInfo> _desktops = new();
    private readonly List<DesktopButton> _buttons = new();
    private int _hoveredIndex = -1;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private bool _isDarkMode;
    private readonly ContextMenuStrip _contextMenu;

    // Win11 taskbar style constants
    private static readonly Font ButtonFont = new("Segoe UI Variable Text", 10f, FontStyle.Regular);
    private static readonly Font ButtonFontBold = new("Segoe UI Variable Text", 10f, FontStyle.Bold);
    private const int ButtonPaddingH = 6;
    private const int ButtonPaddingV = 3;
    private const int ButtonSpacing = 1;
    private const int ButtonRadius = 4;

    // Transparent background - use a color key for true transparency
    private static readonly Color TransparencyColor = Color.FromArgb(1, 1, 1);

    public TaskbarOverlay(TaskbarData taskbar, DesktopService desktopService)
    {
        _taskbar = taskbar;
        _desktopService = desktopService;
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

        // Polling timer - refreshes desktops and repositions
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _refreshTimer.Tick += (_, _) => RefreshDesktops();
        _refreshTimer.Start();

        _contextMenu = new ContextMenuStrip();
        _contextMenu.Items.Add("Exit DesktopNames", null, (_, _) => Application.Exit());

        MouseMove += OnMouseMove;
        MouseLeave += (_, _) => { _hoveredIndex = -1; Invalidate(); };
        MouseUp += OnMouseUp;
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
        // Hide if a fullscreen app is on this monitor
        bool shouldHide = IsFullscreenOnMonitor();
        if (shouldHide && Visible)
        {
            Visible = false;
            return;
        }
        if (!shouldHide && !Visible)
        {
            Visible = true;
        }

        var newDesktops = _desktopService.GetDesktops();
        bool desktopsChanged = DesktopsChanged(newDesktops);
        int prevStripHeight = _taskbar.Bounds.Height;

        // Always reposition — picks up taskbar edge/move changes without waiting for a desktop change.
        RepositionOnTaskbar();
        bool stripChanged = _taskbar.Bounds.Height != prevStripHeight;

        if (desktopsChanged || stripChanged)
        {
            _desktops = newDesktops;
            RecalculateLayout();
            RepositionOnTaskbar();
            Invalidate();
        }

        // Also check theme
        bool dark = DetectDarkMode();
        if (dark != _isDarkMode)
        {
            _isDarkMode = dark;
            Invalidate();
        }
    }

    private bool IsFullscreenOnMonitor()
    {
        var fgWnd = NativeMethods.GetForegroundWindow();
        if (fgWnd == IntPtr.Zero || fgWnd == Handle) return false;

        // Ignore desktop/shell windows
        var className = new char[256];
        int len = NativeMethods.GetClassName(fgWnd, className, className.Length);
        var name = new string(className, 0, len);
        if (name is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW")
            return false;

        // Check if the foreground window is on the same monitor
        var fgMonitor = NativeMethods.MonitorFromWindow(fgWnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (fgMonitor != _taskbar.MonitorHandle) return false;

        // Get the full monitor rect (including taskbar area)
        var mi = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfo(fgMonitor, ref mi);

        // Check if the foreground window covers the entire monitor
        NativeMethods.GetWindowRect(fgWnd, out var wndRect);
        return wndRect.Left <= mi.rcMonitor.Left &&
               wndRect.Top <= mi.rcMonitor.Top &&
               wndRect.Right >= mi.rcMonitor.Right &&
               wndRect.Bottom >= mi.rcMonitor.Bottom;
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

    private static string GetButtonLabel(DesktopInfo desktop)
    {
        return $"{desktop.Index + 1}. {desktop.Name}";
    }

    private void RecalculateLayout()
    {
        _buttons.Clear();

        int stripHeight = Math.Max(_taskbar.Bounds.Height, 24);
        // Each button is half the taskbar height so two rows fit stacked.
        int buttonHeight = Math.Max(stripHeight / 2, 14);

        // Measure each label's width (height is fixed at buttonHeight).
        var widths = new List<int>(_desktops.Count);
        foreach (var desktop in _desktops)
        {
            string label = GetButtonLabel(desktop);
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
                Bounds = new Rectangle(xStart, y, w, buttonHeight)
            });

            if (row == 0) row1Width += w + ButtonSpacing;
            else row2Width += w + ButtonSpacing;
        }

        int totalWidth = Math.Max(row1Width, row2Width);
        ClientSize = new Size(totalWidth, stripHeight);
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
            string label = GetButtonLabel(desktop);

            // Background - always draw for active/hovered, gives pill-shaped button look
            if (desktop.IsCurrent || isHovered)
            {
                using var bgBrush = new SolidBrush(desktop.IsCurrent ? activeBgColor : hoverBgColor);
                using var path = RoundedRect(rect, ButtonRadius);
                g.FillPath(bgBrush, path);
            }

            // Active desktop underline indicator
            if (desktop.IsCurrent)
            {
                int indicatorWidth = Math.Min(rect.Width - 16, 20);
                int indicatorX = rect.X + (rect.Width - indicatorWidth) / 2;
                int indicatorY = rect.Bottom - 3;
                using var indicatorBrush = new SolidBrush(activeIndicatorColor);
                using var indicatorPath = RoundedRect(new Rectangle(indicatorX, indicatorY, indicatorWidth, 3), 1);
                g.FillPath(indicatorBrush, indicatorPath);
            }

            // Text
            var font = desktop.IsCurrent ? ButtonFontBold : ButtonFont;
            var color = desktop.IsCurrent ? textColor : textDimColor;
            TextRenderer.DrawText(g, label, font, rect, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
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

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
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
            Invalidate();
        }
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            // Show context menu manually since WS_EX_NOACTIVATE suppresses it
            _contextMenu.Show(this, e.Location);
            return;
        }

        if (e.Button != MouseButtons.Left) return;

        for (int i = 0; i < _buttons.Count; i++)
        {
            if (_buttons[i].Bounds.Contains(e.Location))
            {
                var desktop = _buttons[i].Desktop;
                if (!desktop.IsCurrent)
                {
                    // Run switch on background thread to avoid blocking UI
                    Task.Run(() =>
                    {
                        _desktopService.SwitchToDesktop(desktop);
                        Thread.Sleep(300);
                        BeginInvoke(RefreshDesktops);
                    });
                }
                break;
            }
        }
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
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        base.OnFormClosing(e);
    }

    private sealed class DesktopButton
    {
        public DesktopInfo Desktop { get; set; } = null!;
        public Rectangle Bounds { get; set; }
    }
}
