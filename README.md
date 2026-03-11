# DesktopNames

A lightweight Windows 11 utility that displays your virtual desktop names directly on the taskbar as clickable buttons. Switch desktops with a single click.

## Features

- **Taskbar overlay** - Desktop names appear right on the taskbar, not hidden in the system tray
- **Multi-monitor** - Overlay appears on the taskbar of every connected monitor
- **Click to switch** - Click any desktop name to switch to it instantly
- **Live updates** - Automatically reflects desktop creation, removal, renaming, and switching
- **Win11 styled** - Matches your taskbar's dark/light theme with Segoe UI Variable font, rounded corners, and accent-colored active indicator
- **Non-intrusive** - Doesn't steal focus, hidden from Alt+Tab

## Requirements

- Windows 11
- [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (or SDK to build from source)

## Running

```
cd DesktopNames
dotnet run
```

Or publish and run the executable directly:

```
dotnet publish .\DesktopNames\DesktopNames.csproj -c Release -o publish
.\publish\DesktopNames.exe
```

## Exiting

Right-click the system tray icon and select **Exit**.

## How it works

The app uses undocumented Windows COM interfaces (`IVirtualDesktopManagerInternal`) to enumerate desktops, read their names, and switch between them. If the COM interfaces aren't available (GUIDs change between Windows builds), it falls back to reading desktop info from the registry and switching via keyboard simulation (Ctrl+Win+Arrow).

The overlay is a borderless, topmost WinForms window positioned over each taskbar using `Shell_TrayWnd` / `Shell_SecondaryTrayWnd` window detection.

## Troubleshooting

| Issue | Cause | Fix |
|-------|-------|-----|
| Shows "Desktop 1, Desktop 2..." instead of custom names | COM interface GUIDs don't match your Windows build | The app falls back to registry; names should still appear if you've set them in Task View |
| Clicking doesn't switch desktops | COM switch failed, keyboard fallback may be slow | Ensure no other app is intercepting Ctrl+Win+Arrow |
| Overlay not visible | Taskbar detection failed or overlay is behind taskbar | Try restarting the app; check if your taskbar is in an unusual configuration |
