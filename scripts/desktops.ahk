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
