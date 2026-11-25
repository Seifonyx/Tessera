// ==WindhawkMod==
// @id              tessera-start-button
// @name            Tessera: Start button
// @description     Makes the taskbar Start button open Tessera instead of the Windows Start menu
// @version         1.0
// @author          Tessera
// @include         explorer.exe
// @architecture    x86-64
// ==/WindhawkMod==

// ==WindhawkModReadme==
/*
# Tessera: Start button

Companion mod for **Tessera**, a translucent Start menu that groups your apps
into category tiles.

Every request to open the Windows Start menu other than the keyboard (the
taskbar Start button, and anything else that asks explorer to show Start) goes
through `XamlLauncher::ShowStartView` in twinui.pcshell.dll. This mod catches
it and tells Tessera to open (or close) instead.

The keyboard stays with Tessera's own keyboard hook. When Windows sees a lone
Win key press it hands the keyboard to its Start search (SearchHost) before
explorer is even told, so the Win key has to be hidden from Windows before that
happens. Win key / Ctrl+Esc presses that do reach explorer (for example from
Tessera's ⊞ button, which sends Ctrl+Esc) open the Windows Start menu as usual.

Safe by design:
- If Tessera isn't running, or its "Open with Windows key and Start button"
  option is off, the request goes to the Windows Start menu as usual.
- While this mod is loaded it holds the `Local\Tessera.Mod` mutex. Tessera sees
  it and switches off its own Start button mouse hook.

Source: `C:\Apps\Utilities\Customization\Tessera\tessera-start-button.wh.cpp`.
Rebuild and install it with `install-mod.cmd` in that folder.
*/
// ==/WindhawkModReadme==

#include <windhawk_utils.h>

static HANDLE g_modMutex;

// Tessera's menu window (a WPF window titled "Tessera", class "HwndWrapper[Tessera;...")
static HWND TesseraWindow() {
    HWND h = nullptr;
    while ((h = FindWindowExW(nullptr, h, nullptr, L"Tessera")) != nullptr) {
        WCHAR cls[64];
        if (GetClassNameW(h, cls, ARRAYSIZE(cls)) && wcsncmp(cls, L"HwndWrapper[Tessera;", 20) == 0) return h;
    }
    return nullptr;
}

// Returns true if Tessera took the request (so the Windows Start menu must stay closed)
static bool SendToTessera() {
    // exists only while Tessera runs; signaled while its takeover option is on
    HANDLE enabled = OpenEventW(SYNCHRONIZE, FALSE, L"Local\\Tessera.Enabled");
    if (!enabled) return false;
    bool on = WaitForSingleObject(enabled, 0) == WAIT_OBJECT_0;
    CloseHandle(enabled);
    if (!on) return false;

    HANDLE toggle = OpenEventW(EVENT_MODIFY_STATE, FALSE, L"Local\\Tessera.Toggle");
    if (!toggle) return false;
    // explorer just received the click, so it may pass focus on: let Tessera (and only it) take it
    HWND menu = TesseraWindow();
    if (menu) {
        DWORD pid = 0;
        GetWindowThreadProcessId(menu, &pid);
        AllowSetForegroundWindow(pid);
    }
    BOOL ok = SetEvent(toggle);
    CloseHandle(toggle);
    return ok != FALSE;
}

// set while a keyboard request is on its way to the Windows Start menu, so its ShowStartView call isn't caught
static thread_local bool t_keyboard;

using ShowStartView_t = HRESULT(WINAPI*)(void* pThis, int method, int flags);
static ShowStartView_t ShowStartView_Original;
static HRESULT WINAPI ShowStartView_Hook(void* pThis, int method, int flags) {
    bool taken = !t_keyboard && SendToTessera();
    Wh_Log(L"ShowStartView method=%d flags=0x%X keyboard=%d -> %s", method, flags, t_keyboard,
           taken ? L"Tessera" : L"Windows Start");
    if (taken) return S_OK;
    return ShowStartView_Original(pThis, method, flags);
}

// Win key / Ctrl+Esc that got past Tessera's keyboard hook: by now Windows has already given the keyboard to its own
// Start search, so only the Windows Start menu can open properly. Let it.
using OnHotkeyInvoke_t = HRESULT(WINAPI*)(void* pThis, int flags);
static OnHotkeyInvoke_t OnHotkeyInvoke_Original;
static HRESULT WINAPI OnHotkeyInvoke_Hook(void* pThis, int flags) {
    Wh_Log(L"OnHotkeyInvoke flags=0x%X -> Windows Start", flags);
    t_keyboard = true;
    HRESULT hr = OnHotkeyInvoke_Original(pThis, flags);
    t_keyboard = false;
    return hr;
}

BOOL Wh_ModInit() {
    HMODULE twinui = LoadLibraryExW(L"twinui.pcshell.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!twinui) {
        Wh_Log(L"twinui.pcshell.dll not loaded");
        return FALSE;
    }

    WindhawkUtils::SYMBOL_HOOK hooks[] = {
        {{LR"(public: virtual long __cdecl XamlLauncher::ShowStartView(enum IMMERSIVELAUNCHERSHOWMETHOD,enum IMMERSIVELAUNCHERSHOWFLAGS))"},
         &ShowStartView_Original, ShowStartView_Hook},
        {{LR"(private: long __cdecl XamlLauncher::OnHotkeyInvoke(enum IMMERSIVELAUNCHERSHOWFLAGS))"},
         &OnHotkeyInvoke_Original, OnHotkeyInvoke_Hook},
    };
    if (!WindhawkUtils::HookSymbols(twinui, hooks, ARRAYSIZE(hooks))) {
        Wh_Log(L"XamlLauncher symbols not found");
        return FALSE;
    }

    g_modMutex = CreateMutexW(nullptr, FALSE, L"Local\\Tessera.Mod");
    Wh_Log(L"hooked");
    return TRUE;
}

void Wh_ModUninit() {
    if (g_modMutex) {
        CloseHandle(g_modMutex);
        g_modMutex = nullptr;
    }
}
