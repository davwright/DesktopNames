namespace DesktopNames;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Prevent multiple instances
        using var mutex = new Mutex(true, "DesktopNames_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("DesktopNames is already running.", "DesktopNames",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Initialize virtual desktop service
        var desktopService = new DesktopService();
        if (!desktopService.Initialize())
        {
            MessageBox.Show("Failed to initialize virtual desktop service.",
                "DesktopNames", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Find taskbars on all monitors and create overlays
        var taskbars = TaskbarInfo.FindAllTaskbars();
        var overlays = new List<TaskbarOverlay>();

        foreach (var taskbar in taskbars)
        {
            var overlay = new TaskbarOverlay(taskbar, desktopService);
            overlays.Add(overlay);
            overlay.Show();
            overlay.RefreshDesktops();
        }

        if (overlays.Count == 0)
        {
            MessageBox.Show("Could not find any taskbars.",
                "DesktopNames", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Subscribe to desktop change events for all overlays
        desktopService.DesktopsChanged += () =>
        {
            foreach (var overlay in overlays)
            {
                if (overlay.InvokeRequired)
                    overlay.BeginInvoke(overlay.RefreshDesktops);
                else
                    overlay.RefreshDesktops();
            }
        };

        // Load the embedded app icon for tray + form identity
        var appIcon = LoadAppIcon() ?? SystemIcons.Application;

        // Create a system tray icon for exit
        var trayIcon = new NotifyIcon
        {
            Text = "DesktopNames - Right-click to exit",
            Icon = appIcon,
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };

        trayIcon.ContextMenuStrip.Items.Add("Exit", null, (_, _) =>
        {
            trayIcon.Visible = false;
            trayIcon.Dispose();
            desktopService.Dispose();
            Application.Exit();
        });

        // Run a hidden form as the message loop owner
        var hiddenForm = new Form
        {
            ShowInTaskbar = false,
            WindowState = FormWindowState.Minimized,
            FormBorderStyle = FormBorderStyle.None,
            Opacity = 0,
            Icon = appIcon
        };

        hiddenForm.FormClosing += (_, _) =>
        {
            trayIcon.Visible = false;
            trayIcon.Dispose();
            desktopService.Dispose();
        };

        Application.Run(hiddenForm);
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
}
