# DesktopNames

A lightweight Windows 11 utility that displays your virtual desktop names directly on the taskbar as clickable buttons. Switch desktops with a single click.

## Features

- **Taskbar overlay** - Desktop names appear right on the taskbar, not hidden in the system tray
- **Multi-monitor** - Overlay appears on the correct edge of every connected monitor's taskbar (top or bottom), derived from the monitor work area so it never lands on the wrong edge
- **Compact two-row layout** - Buttons are half the taskbar height and wrap into two rows, keeping the block tight and flush-left
- **Click to switch** - Click any desktop name to switch to it instantly
- **Live updates** - Automatically reflects desktop creation, removal, renaming, and switching; repositions if you move the taskbar to a different edge
- **Win11 styled** - Matches your taskbar's dark/light theme with Segoe UI Variable font, rounded corners, and accent-colored active indicator
- **Non-intrusive** - Doesn't steal focus, hidden from Alt+Tab

## Requirements

- Windows 11
- [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (or SDK to build from source)

## Running

Download the latest release from the [Releases page](https://github.com/davwright/DesktopNames/releases), unzip, and run `DesktopNames.exe`.

Or from source:

```
cd DesktopNames
dotnet run
```

Or publish and run the executable directly:

```
dotnet publish .\DesktopNames\DesktopNames.csproj -c Release -o publish
.\publish\DesktopNames.exe
```

## Auto-start on login

To have DesktopNames start automatically when you sign in:

1. Press `Win+R`, type `shell:startup`, press Enter. This opens your user's Startup folder.
2. Right-click in the folder → **New → Shortcut**.
3. For the location, browse to your `DesktopNames.exe` (e.g., the `release\` folder from this repo, or wherever you unzipped the release).
4. Name the shortcut `DesktopNames` and finish.

It will now launch silently at each login. There is no window — it only draws on the taskbar, with a system tray icon for exit.

## Exiting

Right-click the system tray icon and select **Exit**.

## How it works

The app uses undocumented Windows COM interfaces (`IVirtualDesktopManagerInternal`) to enumerate desktops, read their names, and switch between them. If the COM interfaces aren't available (GUIDs change between Windows builds), it falls back to reading desktop info from the registry and switching via keyboard simulation (Ctrl+Win+Arrow).

The overlay is a borderless, topmost WinForms window positioned over each taskbar using `Shell_TrayWnd` / `Shell_SecondaryTrayWnd` window detection.

## Companion: Win+1..9 hotkeys via AutoHotkey

DesktopNames shows you which virtual desktop you're on. Pair it with a small AutoHotkey v2 script to jump between desktops with `Win+1..9` and move the active window with `Win+Ctrl+1..9`. Windows 11 itself does not ship these hotkeys.

### Setup

1. Install [AutoHotkey v2](https://www.autohotkey.com/).
2. Download `VirtualDesktopAccessor.dll` for your Windows build from the [VirtualDesktopAccessor releases](https://github.com/Ciantic/VirtualDesktopAccessor/releases) and place it next to the script.
3. Save the script below as `desktops.ahk` in the same folder as the DLL, then double-click it to run.
4. To auto-start at login, put a shortcut to the `.ahk` file in `shell:startup` (press `Win+R`, type `shell:startup`, press Enter).

### `desktops.ahk`

```ahk
#Requires AutoHotkey v2.0
#SingleInstance Force

; Load VirtualDesktopAccessor.dll from same directory as script
dllPath := A_ScriptDir "\VirtualDesktopAccessor.dll"
hVDA := DllCall("LoadLibrary", "Str", dllPath, "Ptr")
if !hVDA {
    MsgBox "Failed to load VirtualDesktopAccessor.dll`nMake sure it's in: " A_ScriptDir
    ExitApp
}

GoToDesktop(n) {
    global dllPath
    DllCall(dllPath "\GoToDesktopNumber", "Int", n)
}

MoveToDesktop(n) {
    global dllPath
    hwnd := WinGetID("A")
    DllCall(dllPath "\MoveWindowToDesktopNumber", "Ptr", hwnd, "Int", n)
}

; Win+1..0 — switch to desktop
#1::GoToDesktop(0)
#2::GoToDesktop(1)
#3::GoToDesktop(2)
#4::GoToDesktop(3)
#5::GoToDesktop(4)
#6::GoToDesktop(5)
#7::GoToDesktop(6)
#8::GoToDesktop(7)
#9::GoToDesktop(8)
#0::GoToDesktop(9)

; Win+Ctrl+1..0 — move focused window to desktop
#^1::MoveToDesktop(0)
#^2::MoveToDesktop(1)
#^3::MoveToDesktop(2)
#^4::MoveToDesktop(3)
#^5::MoveToDesktop(4)
#^6::MoveToDesktop(5)
#^7::MoveToDesktop(6)
#^8::MoveToDesktop(7)
#^9::MoveToDesktop(8)
#^0::MoveToDesktop(9)
```

Desktops are zero-indexed in the DLL but one-indexed in the hotkeys — `Win+1` switches to the first desktop.

## Troubleshooting

| Issue | Cause | Fix |
|-------|-------|-----|
| Shows "Desktop 1, Desktop 2..." instead of custom names | COM interface GUIDs don't match your Windows build | The app falls back to registry; names should still appear if you've set them in Task View |
| Clicking doesn't switch desktops | COM switch failed, keyboard fallback may be slow | Ensure no other app is intercepting Ctrl+Win+Arrow |
| Overlay not visible | Taskbar detection failed or overlay is behind taskbar | Try restarting the app; check if your taskbar is in an unusual configuration |

## License

[MIT](LICENSE).
