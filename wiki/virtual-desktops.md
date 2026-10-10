# Windows 11 Virtual Desktops — the working knowledge

Everything learned building DesktopNames about driving Win11 virtual desktops from
.NET: the three access layers, why each exists, the per-build fragility, and the exact
calls to create / name / move / remove desktops and place windows. Other tools
(DevPulse playpens, AutoHotkey scripts) reuse this — treat this page as the reference.

## TL;DR — what actually works

- **Use Ciantic's `VirtualDesktopAccessor.dll` (VDA) for everything risky.** It ships
  per-Windows-build vtable definitions for the undocumented COM and is kept current
  upstream, so vtable drift is upstream's problem, not yours.
- **Hand-rolled C# COM (`IVirtualDesktopManagerInternal`) is a fallback for the two
  things VDA does poorly**, and even those drift between builds. Prefer VDA.
- **Registry is the last-resort fallback** for reading the current desktop id + names.
- **Desktops are addressed by zero-based INDEX in VDA, but persist by GUID.** Create →
  get index; convert index→GUID for durable storage; convert GUID→index to act later.
- **Never hand-roll `MoveViewToDesktop` for Chromium/WebView2 windows** — the COM call
  AV-crashes deterministically on Win11 26200+. Route window moves through VDA's
  `MoveWindowToDesktopNumber`.

## The three layers (and why)

1. **VDA DLL** (`VdaDll.cs`) — flat-C P/Invoke to `VirtualDesktopAccessor.dll`. Used for
   everything risky: cross-process window moves (especially Chromium views), create,
   remove, name, enumerate. Upstream tracks the COM vtable per Windows build.
2. **Internal COM** (`VirtualDesktopInterop.cs`) — hand-rolled `IVirtualDesktopManagerInternal`
   via the ImmersiveShell service provider. Historically used for `SetDesktopName`
   (Unicode/HString) and `MoveDesktop` (reorder — VDA has no equivalent). **Caveat: the
   internal-COM `SetDesktopName` was observed silently no-op'ing on a recent build
   (vtable slot drift), so name via the VDA DLL instead.**
3. **Registry** — `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops`,
   `CurrentVirtualDesktop` = 16-byte GUID. Fallback for current-id + names when COM fails
   or VDA isn't loaded.

## VDA exports (the ones that matter)

```csharp
[DllImport(DLL)] static extern int  GetDesktopCount();
[DllImport(DLL)] static extern int  GetCurrentDesktopNumber();
[DllImport(DLL)] static extern int  GetWindowDesktopNumber(IntPtr hwnd);
[DllImport(DLL)] static extern Guid GetWindowDesktopId(IntPtr hwnd);
[DllImport(DLL)] static extern Guid GetDesktopIdByNumber(int n);          // index → persistent GUID
[DllImport(DLL)] static extern int  MoveWindowToDesktopNumber(IntPtr hwnd, int n);
[DllImport(DLL)] static extern void GoToDesktopNumber(int n);
[DllImport(DLL)] static extern int  CreateDesktop();                      // returns new desktop INDEX
[DllImport(DLL)] static extern void RemoveDesktop(int removeIdx, int fallbackIdx);
[DllImport(DLL)] static extern int  SetDesktopName(int desktopIndex, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);  // UTF-8 — handles "Mobilität"
[DllImport(DLL)] static extern void PinWindow(IntPtr hwnd);               // show on every desktop
[DllImport(DLL)] static extern void UnPinWindow(IntPtr hwnd);
[DllImport(DLL)] static extern int  IsPinnedWindow(IntPtr hwnd);
```

`SetDesktopName` takes an **index** and is **UTF-8** (not the COM HString path). `Pin*`
return void upstream; an `int` return mismatch is harmless on x64.

## Loading the DLL

Pre-load with `NativeLibrary.SetDllImportResolver` so `[DllImport]` binds to your chosen
copy regardless of CWD/PATH, then **smoke-test with `GetDesktopCount()`** (read-only,
cheap; throws / returns implausible values on wrong-arch or broken DLL). Resolution
order DesktopNames uses: explicit settings override → beside the exe (the project ships its own copy).

## Vtable drift — the core fragility

The COM GUIDs and vtable slot layout of `IVirtualDesktopManagerInternal` change between
Windows 11 builds (22H2/23H2/24H2/26200+ all differ). Symptoms of a mismatch: a slot
returns `Guid.Empty`, duplicate GUIDs across desktops, or a method silently no-ops
(what hit internal-COM `SetDesktopName`). **Self-test before trusting destructive ops:**
enumerate all desktops and assert each has a distinct non-empty GUID (exercises the same
vtable slots). DesktopNames gates AutoMove on this self-test. VDA absorbs most drift
because upstream ships per-build definitions — which is the whole reason to prefer it.

## Recipes

**Create a named desktop and move a window to it** (the playpen pattern):

```csharp
int idx = VdaDll.CreateDesktop();                 // new desktop's index
VdaDll.SetDesktopName(idx, workspaceName);         // UTF-8, by index
Guid id = VdaDll.GetDesktopIdByNumber(idx);        // persist THIS (index shifts as desktops come/go)
VdaDll.MoveWindowToDesktopNumber(hwnd, idx);       // route window moves through VDA, never hand-rolled COM
```

**Act on a stored desktop later** — index can shift, so resolve GUID→index each time:

```csharp
int IndexFromGuid(Guid id) {
    int count = VdaDll.GetDesktopCount();
    for (int i = 0; i < count; i++) if (VdaDll.GetDesktopIdByNumber(i) == id) return i;
    return -1;   // desktop was removed
}
```

**Remove a desktop** (teardown): `RemoveDesktop(removeIdx, fallbackIdx)` — fallback is
where windows/focus land; use `currentIdx-1` (or 1 if at 0). Refuse if count ≤ 1.

**Read current desktop id** (with registry fallback):

```csharp
if (VdaDll.IsLoaded) { int i = VdaDll.GetCurrentDesktopNumber(); if (i>=0) return VdaDll.GetDesktopIdByNumber(i); }
using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops");
if (key?.GetValue("CurrentVirtualDesktop") is byte[] b && b.Length == 16) return new Guid(b);
```

## Naming + DesktopNames association

OS-level desktop names (set via `SetDesktopName`, the same store Task View writes) are
what **DesktopNames** renders on the taskbar — it is **read-only display**, so setting a
name from another tool (e.g. DevPulse naming a playpen desktop after its workspace) just
shows up; they don't fight. VS Code's window title can be set via the workspace's
`window.title` setting, so a playpen whose desktop name == workspace name == VS-Code
title is fully cross-associable.

## The WebView2 / Chromium AV crash

Hand-rolled internal-COM `IApplicationViewCollection::GetViewForHwnd` →
`IVirtualDesktopManagerInternal::MoveViewToDesktop` **access-violates deterministically
on Chromium-backed windows (WebView2, VS Code, browsers) on Win11 26200+.** This is the
single biggest reason DesktopNames (and any tool moving such windows) must route through
VDA's `MoveWindowToDesktopNumber` instead.
