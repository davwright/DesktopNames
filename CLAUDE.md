# DesktopNames — working notes for Claude

WinForms tray app (.NET 8): named virtual desktops on the taskbar, Claude session status per
desktop, and the DesktopNames window (Win+J) for projects, VS Code windows and screens.
House practices: the `building-apps` skill (`~/.claude/skills/wiki/building-apps/README.md`).

## Rules
- **Never test by injecting keys or mouse** into the user's desktop. Drive and look through the
  control channel (below). Simulated input has typed into the user's VS Code and browser before.
- **Every user action is a command** in `HostForm.BuildCommands` (`Program.cs`); hotkeys bind
  settings keys to command ids. Add a command, then bind it — never a hotkey-only lambda.
- **Fail loud**: no new empty `catch { }`; `scripts/check.mjs` ratchets their count down.
- Settings: `Settings.Load` throws on a corrupt file and `Save` writes temp-then-rename — keep both.

## Loop
1. `node scripts/check.mjs` — must pass before you start and before you commit.
2. Change code.
3. `powershell -NoProfile -ExecutionPolicy Bypass -File publish.ps1` — bumps, publishes to
   `%APPDATA%\DesktopNames`, relaunches.
4. Drive and look: `node scripts/dn.mjs …` (below); read screenshots you took.
5. Commit to main and push; say what was verified and what was not run.

## Control channel (`node scripts/dn.mjs <method> [params-json]`)
| Method | Params |
|---|---|
| `commands` / `execute` | — / `{"command":"desktop.toggleBlue","params":{"desktop":3}}` |
| `inspect` | desktops, Claude sessions, screen setup, window open? |
| `ui.open` / `ui.close` | `{"window":"projects"}` |
| `ui.rows` | what each visible row of the DesktopNames window shows |
| `ui.hover` | `{"row":1,"column":"Claude"}` → tooltip text, or `{"header":"Screen 1"}` → layout popup |
| `ui.screenshot` | `{"window":"projects"\|"screen-popup"\|"overlay","path":"C:/abs.png"}` — by handle, any DPI |

Unknown params are errors that list the accepted ones. `ui.open` shows the window on the user's
screen: say so when you use it. Commands like `desktop.switch` and `window.moveToDesktop` change
the user's desktop — only run them when the task is about them.

## Logs
`%APPDATA%\DesktopNames\desktopnames.log` — `startup`, `pipe` (incl. `control …`), `resolver`,
`projects`, `ui`. Hotkeys are a low-level keyboard hook (`HotkeyManager`), not RegisterHotKey.
