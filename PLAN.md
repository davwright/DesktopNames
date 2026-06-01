# PLAN — Claude state indicators in DesktopNames

Implementation plan for the DesktopNames side of the [Claude-Alert integration](../../../../Claude-Alert/DESKTOPNAMES-INTEGRATION.md). Owns: the named-pipe listener, per-desktop session state, taskbar button coloring/animation, and a global hotkey to jump to the next waiting Claude.

The Claude-Alert side (hook wiring, parent-walker for `vscodePid`, fallback toast) is the other Claude's work — this plan assumes that contract holds.

---

## Goal

Turn the existing taskbar overlay into a glanceable status bar for every Claude session running across desktops:

- **Orange** button → Claude is busy on that desktop.
- **Yellow** button (breathing) → Claude is asking the user a question.
- **Green** button → Claude is ready / waiting for the next prompt.
- A **2.5s pulse** plays on every state change so transitions catch the eye.
- **Win+\`** jumps to the next desktop with a Claude waiting (asking first, then ready).

When DesktopNames isn't running, Claude-Alert falls back to a Windows toast — that's outside this plan.

---

## Wire contract (authoritative — must match Claude-Alert)

Named pipe `\\.\pipe\DesktopNames`, per-user scope. Client writes one UTF-8 JSON line, server replies one line, then closes.

**Request:**
```jsonc
{
  "type": "state",
  "source": "claude-code",
  "state": "busy" | "asking" | "ready" | "idle",
  "title": "Claude — Adv",
  "body": "Running Edit on VsCodeTracker.cs",
  "vscodePid": 12345,
  "sessionPid": 67890,
  "cwd": "C:/git/tools/Adv",
  "sessionId": "abc12345"
}
```

**Reply:**
```jsonc
{ "ok": true, "desktopIndex": 2, "desktopName": "Code" }
{ "ok": false, "error": "vscode-window-not-found" }
```

`ok:false` ⇒ client falls back to toast. The server **never** flashes the current desktop as a guess.

---

## File inventory

### New files

| File | Purpose |
|------|---------|
| `AlertPipeServer.cs` | `NamedPipeServerStream` loop on a background thread; `System.Text.Json` parse; dispatch to UI thread via `HostForm.BeginInvoke`. |
| `SessionState.cs` | `Dictionary<Guid, List<SessionEntry>>` keyed by desktop GUID. `GetAggregate(Guid)` returns `(StateKind, count, tooltip)`. Liveness sweep timer (1Hz). Fires `Changed` event when any desktop's aggregate changes. |
| `AlertPulse.cs` | Single shared `Timer` (~16ms tick) animating button backgrounds when a desktop's aggregate state changes. 2.5s window, 200ms period. |

### Modified files

| File | Change |
|------|--------|
| `Settings.cs` | Add `AlertListenerEnabled`, `AlertPulseEnabled`, three color fields, and `Hotkeys["NextWaitingDesktop"]`. |
| `Program.cs` (`HostForm`) | Start `AlertPipeServer` in `Load`; stop in `OnFormClosing`; own the `SessionState` instance. Add `NextWaitingDesktop` hotkey to `RegisterHotkeys`. Liveness sweep on existing `_desktopPoll` cadence (200ms is fine; 1Hz is enough but sharing the timer keeps it simple). |
| `TaskbarOverlay.cs` | Subscribe to `SessionState.Changed`; resolve per-button `StateKind` from `SessionState.GetAggregate(desktopId)`; in `OnPaint`, replace `activeBgColor` with state color when set; render badge (count) on top-right of button. Tooltip on hover (new). |
| `HotkeyParser.cs` | Add `oem3`/`backtick`/`tilde` → `0xC0` (`VK_OEM_3`). One-line change so the new default hotkey parses. |
| `Program.cs` (tray menu) | Add "Claude status" submenu: dynamic summary line + `Test alert → busy/asking/ready/idle` items. |

Target: ~450 LOC new, ~80 LOC modified. No new dependencies (`System.Text.Json` is already pulled in by `Settings.cs`).

---

## Phase 1 — Pipe server + state model (no UI yet)

Goal: a synthetic `state` message arrives, gets parsed, mutates `SessionState`, and the existing log/debug path confirms it. Render unchanged.

1. **`AlertPipeServer.cs`**
   - Constructor takes `HostForm host, SessionState state, Settings settings, DesktopService desktop`.
   - `Start()` launches a `Thread { IsBackground=true }` running the accept loop. Use `PipeOptions.Asynchronous`.
   - `PipeSecurity` granting `WellKnownSidType.CurrentUserSid` only.
   - Each connection: `ReadLine()` → `JsonSerializer.Deserialize<AlertMessage>` → resolve target desktop (see §Desktop resolution) → `host.BeginInvoke(() => state.Apply(...))` → write `{ok:true, ...}` or `{ok:false, error:...}` → close.
   - `Stop()` disposes the current `NamedPipeServerStream`; swallow `ObjectDisposedException` in the loop.
   - Internal record `AlertMessage` mirrors the wire JSON exactly.

2. **`SessionState.cs`**
   - `enum StateKind { None, Ready, Busy, Asking }` (declared low→high priority for trivial `Max`).
   - `record SessionEntry(string Source, string SessionId, StateKind State, string Title, string Body, int SessionPid, int VsCodePid, DateTime LastSeenUtc)`.
   - `Dictionary<Guid, List<SessionEntry>> _byDesktop` — UI-thread only, no locks.
   - `void Apply(Guid desktopId, AlertMessage msg)`:
     - Locate or insert the `SessionEntry` for `(source, sessionId)` across **all** desktops (a session might have moved). Remove from prior desktop's list if found.
     - If `state == "idle"`: remove and return.
     - Otherwise replace/insert with `LastSeenUtc = UtcNow`.
     - Compute prior + new aggregate for any affected desktops; if changed, raise `Changed(Guid)`.
   - `(StateKind state, int count, string tooltip) GetAggregate(Guid desktopId)`.
   - `void Sweep()`: iterate, drop entries where `Process.GetProcessById(SessionPid)` throws, or where `State==Busy && now - LastSeenUtc > 60s`. Raise `Changed` per affected desktop. Called from `_desktopPoll`.
   - `void Consume(Guid desktopId)`: marks all entries on that desktop as "consumed" (a flag on `SessionEntry`). Aggregate treats consumed entries as `None`. A subsequent non-`idle` `Apply` clears the flag. Wired to user actions (button click, desktop switch).

3. **Desktop resolution** (helper inside `AlertPipeServer`):
   - **By `vscodePid`**: `NativeMethods.EnumWindows`, filter by `GetWindowThreadProcessId`==pid + class `Chrome_WidgetWin_1` + longest title; `_desktopService.GetDesktopForWindow(hwnd)`.
   - **By `cwd`**: `VsCodeTracker.ExtractWorkspace`-style basename → `_settings.VsCodeWorkspaceDesktops[name].DesktopId`.
   - **Otherwise**: `ok:false`. No current-desktop guess.

4. **Hook into `HostForm.Load`**:
   ```csharp
   if (_settings.AlertListenerEnabled)
   {
       _sessionState = new SessionState();
       _alertServer = new AlertPipeServer(this, _sessionState, _settings, _desktopService);
       _alertServer.Start();
       _desktopPoll.Tick += (_, _) => _sessionState.Sweep();
   }
   ```
   And dispose in `OnFormClosing`.

**Verification for phase 1:** add the "Test alert" tray submenu items now (they're just `_sessionState.Apply` calls with a synthetic message). Confirm state dictionary mutates correctly via the debugger. UI still doesn't know about state — that's phase 2.

---

## Phase 2 — Rendering: colors + pulse-on-change

Goal: clicking the test-alert menu changes the button color visibly, with a brief pulse.

1. **`TaskbarOverlay`** subscribes to `_sessionState.Changed` in its constructor; on `BeginInvoke(Invalidate)`. Constructor signature gains `SessionState? sessionState` (nullable so a future "no listener" config still works).

2. **In `OnPaint`**, for each button:
   - `var (s, count, _) = _sessionState?.GetAggregate(desktop.Id) ?? default;`
   - If `s == None`: keep existing rendering (current accent for active, hover background otherwise).
   - Else: pick color from `Settings.Alert{Busy|Asking|Ready}Color` (`ColorTranslator.FromHtml`). Override `activeBgColor` / fill the button regardless of `IsCurrent`.
   - If `count > 1`: draw a small numeric badge top-right (use existing `Segoe UI Variable` font at 8pt bold, white-on-state-color circle 14px diameter).
   - If `count == 1` and `s != None`: small dot badge same position (gives a consistent "there is something here" affordance without text).

3. **`AlertPulse`** (new): owned by `HostForm`. Tracks `Dictionary<Guid, PulseState>` where `PulseState` is `(DateTime startUtc, StateKind targetState)`. On `SessionState.Changed(Guid)`, start/restart a 2.5s pulse for that desktop. Single `Timer` at 16ms iterates and calls `overlay.InvalidateButton(desktopId)` on every overlay. Animation interpolates between the state color and a +25% luminance variant via `Math.Sin(elapsed / 200.0 * Math.PI)`. Honors `Settings.AlertPulseEnabled`.

4. **`asking` breathe**: at rest (no pulse active), if state is `asking`, run a slow 1.5s breathe. Same code path, lower amplitude (15% vs 25%), period 1500ms. Implemented as an "always-on" pulse for any desktop whose aggregate is `asking`; pulse manager checks aggregate when a pulse window ends and continues if state still demands attention.

5. **Tooltip**: extend `OnMouseMove` to set a `ToolTip` (new `ToolTip _stateTip` field) showing `SessionState.GetAggregate(...).tooltip` while hovering a state-colored button.

6. **Consume hooks**:
   - On left-click switch (existing `_pendingSingleClickAction` path), after the desktop change, call `_sessionState.Consume(desktopId)`.
   - On desktop-switch detected by `_desktopPoll` (the `id != _lastDesktopId` branch in `HostForm`), call `_sessionState.Consume(id)` for the new current desktop.

**Verification for phase 2:** test menu items now produce visible color changes + pulse. Multi-session aggregation: send two `busy` to different sessions on one desktop, confirm count badge shows `2`.

---

## Phase 3 — `Win+\`` hotkey

1. **`HotkeyParser.cs`**: add three aliases to `ParseKey`:
   ```csharp
   "oem3" or "backtick" or "tilde" or "`" => 0xC0,   // VK_OEM_3
   ```

2. **`Settings.cs`**: in `BuildDefaultHotkeys()`, add `["NextWaitingDesktop"] = "Win+Oem3"`.

3. **`HostForm.RegisterHotkeys`**: add another `TryRegister`:
   ```csharp
   TryRegister("NextWaitingDesktop", "Jump to next waiting Claude", JumpToNextWaiting);
   ```

4. **`JumpToNextWaiting()`**: build candidate list using `_sessionState.GetAggregate` for every desktop in `_desktopService.GetDesktops()`. Order: all `Asking` desktops first (by index, starting after current+1, wrapping), then all `Ready`. Pick first. If candidates empty, no-op (optional: tray icon flash via `NotifyIcon.ShowBalloonTip` for 800ms with text "No Claudes waiting").

5. **Self-test integration**: `HotkeyManager.Registrations` already surfaces success/failure in the existing "Keyboard shortcuts..." dialog and tray balloon path. No extra wiring needed — if `Win+\`` collides, the existing UX handles it.

**Verification for phase 3:** with three desktops (one asking, one ready, one busy), press hotkey from "busy" desktop → lands on asking. Press again → lands on ready. Press again → back to asking. All `idle` → no-op.

---

## Phase 4 — Tray menu polish + startup self-test

1. **Tray "Claude status" submenu** added in `Program.Main`:
   - Header item (disabled): dynamic label `3 sessions: 1 asking · 1 busy · 1 ready` computed on submenu open.
   - `Jump to next waiting (Win+\`)` → calls the same handler.
   - Separator.
   - `Test alert → busy / asking / ready / idle` (four items) — send synthetic messages with `sessionId = "test"`, `vscodePid = 0`, `cwd = <current cwd>` so the resolver falls back through `cwd`.
   - Separator.
   - `Enable state listener` (checkbox bound to `Settings.AlertListenerEnabled` — restart required, surface that in tooltip).

2. **Startup self-test** in `Program.Main`, after `BuildVerification`:
   - Spin a temporary client, send a `{ source:"self-test", state:"idle", sessionId:"self-test" }`, expect any reply within 500ms.
   - On failure, set a startup-balloon hint string ("Claude state listener failed to start: <err>") similar to the existing `_buildVerificationHint` pattern.

---

## Test plan

Unit-test candidates (none currently exist in this repo — keep it light):

- `SessionState.Apply` priority math (`Asking > Busy > Ready > None`) — straight method test, no UI.
- `SessionState.Apply` session-moved-between-desktops (same `sessionId` arrives with a different resolved desktop) — old desktop should clear, new should add, both should fire `Changed`.
- `SessionState.Sweep` liveness — inject a fake `Process.HasExited`-like check via constructor function pointer for testability.
- `HotkeyParser.Parse("Win+Oem3")` returns `(MOD_WIN, 0xC0)`.

Manual scenarios (must pass before declaring done):

1. Single Claude on desktop 2, run /loop with a debounce on Stop+SessionStart. Confirm green→orange→green cycling, badge stays at 1.
2. Two Claudes on the same desktop: one asking, one busy. Aggregate yellow, badge `2`, tooltip lists both.
3. Kill a Claude with Task Manager mid-`busy`. Liveness sweep clears within 60s.
4. DesktopNames restart while a Claude is in `ready`: green vanishes (state is in-memory). Next `Stop` heartbeat re-establishes it.
5. `Win+\`` cycle across 4 desktops with mixed states.
6. Hotkey collision: bind `Win+\`` to something else first, restart DesktopNames, confirm shortcuts dialog reports ✗ with the right Win32 error.
7. Pipe-permission: confirm a different user (admin runas) can't connect.

---

## Risks / open questions

1. **Hotkey collision on `Win+\``** — some Windows setups bind it to terminal/PowerToys. Spec'd a graceful failure via existing `Registrations` reporting. If common, default to `Win+F11` instead and document `Oem3` as recommended rebind.
2. **`Process.GetProcessById` cost on Sweep** — once a second across <20 entries is fine. If it ever shows up in a profile, cache the `Process` handle.
3. **Cross-monitor rendering for the same desktop's button** — each `TaskbarOverlay` reads `SessionState.GetAggregate(desktopId)` independently, so both monitors color in sync automatically. Pulse animation uses the same shared timer, so phases align. No extra work.
4. **`asking` breathe being annoying** — keep amplitude low (15%) and the period slow (1.5s). If users complain, expose `AlertAskingBreatheEnabled` as a setting.
5. **Multi-session de-dup of `sessionId`** — server treats `sessionId` as opaque per `(source, vscodePid)` rather than globally. Two Claudes in one VSCode window with the same 8-char prefix is theoretical; revisit if it happens.

---

## Done means

- All four phases verified manually with the scenarios above.
- New self-test passes on startup; failure surfaces in the existing startup balloon.
- `Open settings.json` shows the new fields populated with defaults.
- Build is clean; existing `VdaDll.SelfTest` still passes; AutoMove is untouched.

Estimated effort: a long evening for phases 1–3, a second pass for phase 4 + polish. Animation tuning will eat more time than the protocol.
