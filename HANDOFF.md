# Tessera: handoff

Updated 2026-11-25 (version 1.1, docs update) so a fresh session (or person) can pick this up
cold. For the user-facing overview see `README.md`; this file is the working
context behind it.

## What the project is

Tessera is the user's own Start menu replacement for Windows 11 (build 26300,
Insider). Apps are grouped into category tiles that the user defines in
`categories.txt`, which Windows' own Category view doesn't allow. The look copies
the Windhawk theme the user liked on the real Start menu, *TranslucentStartMenu*:
blur, a faint tint, frosted tiles and 15 px corners.

It started on 2026-11-23 as "Custom Start" and was renamed to Tessera the same
day, when the user asked for a real name and a professional finish.

## Current state

**Latest build:** `Tessera.exe` 1.1.0.0, built 2026-11-25 13:13 from `Tessera.cs`.
Both the menu and the helper are running it.

### What 1.1 added (2026-11-25)

The user said "do all" to the 1.0 handoff's next steps. Done:

1. **Health check.** Both scheduled tasks exist and are set to *At logon time*.
   All three mutexes (`Local\Tessera.Instance`, `.Helper`, `.Mod`) were present.
   `schtasks` shows the tasks as *Ready*, not *Running*: the processes running
   now were started by the build's self-restart, not by the tasks. That's
   expected. A real reboot test is still open (see below).
2. **Leftover mod DLL removed.** Explorer was restarted and `install-mod.cmd`
   run. `local@custom-start-takeover_2.0_672740.dll` is gone, and the mod is
   loaded again as `local@tessera-start-button_1.0_200887.dll`.
   `install-mod.ps1` now also waits up to 15 s for Windhawk to unload the
   previous build of the mod and deletes it, so old builds no longer pile up.
3. **Tray icon replaced by a footer menu.** Right-clicking anywhere on the
   footer shows Settings, Edit categories, the *Open with Windows key and Start
   button* switch (with a check mark), About Tessera and Exit Tessera, styled
   like Tessera's other menus. The tray icon is hidden by default, and
   Settings → Tray icon can show it again. *Open Tessera* wasn't moved, because
   the menu is already open when you use the footer. Its replacement is running
   `Tessera.exe` again, or the tray icon if it's switched on.
4. **Polish:**
   - **Settings page** inside the menu (footer → Settings): Tint strength
     (slider), Tile size (Small/Medium/Large), Shadow (On/Off), Tray icon
     (Hidden/Shown). Changes apply live and are saved to `settings.ini` next to
     the exe. Tile size changes the icon fetch size (28/32/36 units), tile and
     card sizes, and the menu width (595/680/765 units), so icons stay sharp.
   - **About page** inside the menu (replaces the old MessageBox).
   - **Drop shadow**: a separate layered window (see Decisions). It was
     measured and has no cost to the slide, so it's on by default.
   - **Multi-monitor placement**: Win and Ctrl+Esc open on the monitor of the
     foreground window. The Start button (and any other pointer request) opens
     on the monitor of the taskbar under the pointer. The menu is centred above
     that monitor's work area and slides out from behind that monitor's
     taskbar. On the primary monitor it behaves exactly as before.
5. **Fixes found while testing:**
   - Separators in Tessera's right-click menus drew as a bright white line,
     because WPF styles menu separators with `MenuItem.SeparatorStyleKey`, not
     the plain Separator style. They're dark now. This also affected the
     existing app, card and power menus.
   - When Windows refused the plain focus switch on opening, the
     `AttachThreadInput` fallback caused a Deactivated event, so the menu closed
     itself at once and kept the keyboard. This was seen with a just-focused
     window in front, and it was already in 1.0. The fallback now holds
     `SuppressHide` while it runs.

### Tests

`tools/test-open-close.ps1`, run against the final build, with a visible
ordinary window in front:

| Action | Result |
|---|---|
| Win key | opens and closes the menu ✓ |
| Ctrl+Esc | opens and closes the menu ✓ |
| Taskbar Start button | opens and closes the menu (via the Windhawk mod) ✓ |
| Footer ⊞ button route (tagged Ctrl+Esc) | opens the real Windows Start ✓ |
| Every way of closing | focus returns to the app that was in front ✓ |

Also checked by hand with simulated clicks and screenshots:

- The footer menu.
- The Settings page: live tint, and live Large ↔ Medium (the window widens and
  re-centres).
- The About page.
- The takeover switch: it creates and deletes `disabled.flag` and resets and
  sets `Local\Tessera.Enabled`.
- Exit: the menu process ends. `schtasks /Run /tn Tessera` brings it back with
  all mutexes present.
- The shadow: pixels just outside the menu edge are about 9% darker with it
  on, and unchanged with it off.

Slide timing, `tools/measure-slide.ps1` (medians; one frame is ~16.7 ms):

| | First move after key | Finished | Longest gap between frames |
|---|---|---|---|
| Open, before (1.0) | 4.7 ms | 308 ms | 19.5 ms (worst 28.7) |
| Open, after (1.1) | 6.1 ms | 298 ms | 18.0 ms (worst 20.5) |
| Close, before (1.0) | 35.1 ms | 197 ms | 18.6 ms (worst 18.8) |
| Close, after (1.1) | 34.3 ms | 196 ms | 18.6 ms (worst 19.2) |

An in-app frame log confirmed the same with the shadow off, the shadow moved in
one batch, and the shadow moved with two calls. In all three, the first move
came ~2 ms after the toggle and the median gap was 18–19 ms.

## Version control

The project is in git and pushed to the private GitHub repo `Seifonyx/Tessera`.
The commits so far are "Tessera 1.0: initial import from the PC folder" and then
"Tessera 1.1.0.0 ...". The local folder `C:\Apps\Utilities\Customization\Tessera`
is the working copy.

`.gitignore` excludes `Tessera.exe`, `Tessera.old-*.exe`, `debug.log` and
`disabled.flag`, so the build output and runtime files never get committed.

Cloud Claude sessions work on branches named `claude/...` and open draft pull
requests. After the user merges one, they pull it into the PC folder with
`git pull` in that folder, and then run `build.cmd` if `Tessera.cs` changed.

## Where everything lives

### App folder: `C:\Apps\Utilities\Customization\Tessera`

| Path | Contents |
|---|---|
| `Tessera.cs` | The whole app: a single-file C# 5 WPF program |
| `Tessera.exe` | Build output |
| `Tessera.ico` | Generated by `make-icon.ps1`, embedded by `build.cmd` |
| `build.cmd` | Build script; see Build setup below |
| `categories.txt` | The user's categories. **User data, don't overwrite.** |
| `settings.ini` | The user's settings (new in 1.1). Written by the Settings page; created with defaults if missing. |
| `README.md` | User and developer documentation |
| `tessera-start-button.wh.cpp` | Windhawk mod source (unchanged in 1.1) |
| `install-mod.cmd` / `install-mod.ps1` | Install the mod; options `-Logging` and `-Uninstall` |
| `tools\` | Test and diagnostic scripts (table below) |

| Script in `tools\` | What it does |
|---|---|
| `test-open-close.ps1` | Simulates Win, Ctrl+Esc, Start button clicks and the ⊞ route, and reports whether the menu is open and which app has focus. Moves the mouse pointer briefly. |
| `measure-slide.ps1` | **New.** Presses Win `-Count` times to open and close, polls the menu position at sub-millisecond intervals, and prints first move, finish time, moves and the longest frame gap. |
| `trace-focus-and-slide.ps1` | Logs the menu's position and the focused window every few ms after a key press (`-Keys 0x5B -Ms 800`). Its 5 ms sleep can really be 15.6 ms (see Gotchas), so use `measure-slide.ps1` for timing. |
| `refocus-claude.ps1` | Gives focus back to the Claude window between tests |
| `debug-output-listener.ps1` | Captures the mod's debug output |
| `download-pdb.ps1` | Downloads a DLL's symbols from Microsoft's symbol server |
| `check-health.ps1` | **New.** Read-only health check: the two scheduled tasks (At logon trigger, ran since last boot, result code), the menu and helper processes, the three mutexes, the `Local\Tessera.Enabled` event, and the monitors with their scale and taskbar edge. Run it after signing in, for example `powershell -ExecutionPolicy Bypass -File tools\check-health.ps1`. Every line should say OK. The exit code is the number of problems. |

### Scheduled tasks

Both run at sign-in (trigger *At logon time*, user `seife`, interactive only).

| Task | Runs | Notes |
|---|---|---|
| `Tessera` | `Tessera.exe --background`, limited rights | The menu itself |
| `Tessera Helper` | `Tessera.exe --hook`, highest rights | Elevated low-level keyboard and mouse hooks. It masks the Win key and catches Ctrl+Esc, including over admin windows, then signals the menu. |

- `schtasks /End` and `schtasks /Run` work without admin rights.
- **Creating or deleting the Highest-level task needs a UAC prompt.**
- The task XMLs can be exported with `schtasks /query /tn "Tessera Helper" /xml`.

### Start menu shortcut

`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Customization\Tessera.lnk`

### Windhawk

Windhawk is a portable install at `C:\Apps\Utilities\Customization\Windhawk`.

- **Local mod:** `local@tessera-start-button` v1.0.
  - Config: `AppData\Engine\Mods\local@tessera-start-button.ini` (UTF-16 with BOM).
  - Compiled DLL: `AppData\Engine\Mods\64\` (currently `..._1.0_200887.dll`).
  - Copy of the source: `AppData\ModsSource\local@tessera-start-button.wh.cpp`.
- **Real-Start styler:** `windows-11-start-menu-styler` (the TranslucentStartMenu
  theme) is installed but **Disabled=1**. The user doesn't use the real Start
  any more. Its theme source is `AppData\ModsSource\windows-11-start-menu-styler.wh.cpp`,
  around line 563.

### Named objects

These link the menu, the helper and the mod:

| Object | Kind | Role |
|---|---|---|
| `Local\Tessera.Instance` | mutex | Single instance of the menu |
| `Local\Tessera.Helper` | mutex | Helper is running |
| `Local\Tessera.Mod` | mutex | Held by the mod in explorer; the menu's mouse hook stands down while it exists |
| `Local\Tessera.Show` | event | Open the menu (running `Tessera.exe` again sets it) |
| `Local\Tessera.Toggle` | event | Open or close the menu: Start button and other pointer requests (the mod, the mouse hook) |
| `Local\Tessera.ToggleKey` | event | **New in 1.1.** Open or close the menu from the keyboard (the helper's Win and Ctrl+Esc). It's kept separate so the menu knows to open on the foreground window's monitor. If the helper can't open it, it falls back to `Toggle`. |
| `Local\Tessera.Enabled` | manual-reset event | Set while the takeover option is on; the mod checks it |

The mod finds the menu window by its title, `Tessera`, and its class prefix,
`HwndWrapper[Tessera;`. That class comes from the assembly name. **Renaming the
exe or the window title breaks the mod.** The shadow window has class
`Tessera.Shadow` and no title, so neither the mod nor the test scripts can
mistake it for the menu.

### Memory

The Claude memory note is
`C:\Users\seife\.claude\projects\D--Game-Center-Games-Minecraft\memory\custom-start-menu.md`.

## Build setup

- Run `build.cmd` in the folder. It uses Windows' built-in .NET Framework 4.8
  compiler (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`), so no SDK
  is needed. From PowerShell, run it as `.\build.cmd` or with its full path.
- **The source must stay C# 5:** no `$""`, `?.`, `nameof`, expression-bodied
  members or `out var`.
- It's safe to run while Tessera is running. The build renames the running exe
  to `Tessera.old-NNNN.exe`, and both processes notice the new file within about
  3 seconds and restart into it. The helper keeps its admin rights, with no UAC
  prompt.
- To install the mod, run `install-mod.cmd`. It compiles with Windhawk's bundled
  clang, using the same flags as the Windhawk editor, then writes the UTF-16 ini,
  and Windhawk loads it into explorer by itself. The first load downloads
  explorer and twinui symbols, which are cached afterwards.
- `make-icon.ps1` regenerates `Tessera.ico` at 10 sizes, each drawn natively.
- The icon glyphs in `Tessera.cs` (back arrow, search, footer buttons) are
  private-use Unicode characters. Some editors and tools show them as empty
  strings, so an exact-match edit of those lines can fail. Use `"\uE72B"`
  escapes in new code.

## Decisions made, and why

1. **Smoothness architecture.** Each choice was measured; don't regress any of them:
   - The window isn't layered (no `AllowsTransparency`). Transparency comes from
     `DwmExtendFrameIntoClientArea(-1)` plus a transparent HwndSource background.
   - Rounded corners come from `SetWindowRgn`, re-applied only when the size changes.
   - The window stays shown but is parked off-screen. A dedicated thread moves it
     once per `DwmFlush`, inserted just below `Shell_TrayWnd`, and an HwndSource
     hook keeps it there while it slides. The result: the slide starts about 3 ms
     after the key press, then moves once per screen refresh.
   - The focus switch runs on a thread-pool thread.
   - The search TextBox gets focus only after the slide, because focusing it
     costs about 70 ms (TSF). Letters typed during the slide are appended through
     PreviewTextInput.
   - All methods are pre-compiled (pre-JIT) at startup.
   - A WindowChrome plus DWM system-backdrop attempt rendered a blank window.
     **Don't retry it.**
2. **Plain blur, not acrylic.** The blur is `SetWindowCompositionAttribute` accent
   state 3 (plain blur-behind), with the tint drawn by WPF (`#323232`, alpha from
   the Tint strength setting, default `0x25`). Acrylic (state 4) adds a frosted
   fill and grain that can't be turned off, and made the menu look solid grey.
   The user compared both side by side and chose plain blur.
3. **The Win key stays in the helper's hook, not in the Windhawk mod.** This was
   investigated in depth on build 26300. On a lone Win key press, Windows itself
   clears focus and hands it to a cloaked SearchHost window. That happens *before*
   explorer's `XamlLauncher::OnHotkeyInvoke` runs, so explorer never sees it in
   time. After that nothing can take focus back until a real click: not
   `SetForegroundWindow` from explorer, not `AllowSetForegroundWindow`, and not
   the Alt-tap trick. So the Win key has to be masked before Windows sees it.
   - The mod therefore takes only the Start button and other non-keyboard
     requests, through `XamlLauncher::ShowStartView`.
   - Its `OnHotkeyInvoke` hook marks keyboard requests and passes them through to
     the real Start.
4. **The ⊞ button sends a tagged Ctrl+Esc.** The `dwExtraInfo` tag `0x5C57A27`
   makes Tessera's hooks ignore it, so it reaches explorer, and the mod passes
   keyboard requests through. That opens the real Start.
5. **Focus goes back on close.** Closing with Win, Ctrl+Esc, Esc, Alt+F4 or the
   Start button returns focus to the window that had it when the menu opened
   (`prevFg`). If Windows refuses, which happens after a taskbar click, a tagged
   Alt tap makes Tessera the "last input" process, and then
   `SetForegroundWindow` works. Closing because an app is launched, or because
   another window was clicked, doesn't restore focus. A **minimized** previous
   window is skipped on purpose.
6. **Start button guard.** If a click on the Start button arrives within 400 ms
   of the menu auto-hiding (lost focus), it's ignored. Otherwise clicking Start to
   close the menu would immediately reopen it.
7. **Two processes.** The menu runs with normal rights, so apps launched from it
   aren't elevated. The helper is elevated only so the hooks work over admin
   windows.
8. **Shadow as its own window (1.1).** `SetWindowRgn` clips anything drawn
   outside the rounded corners, and the window can't be layered, so it can't
   draw its own shadow.
   - The shadow is a separate `WS_EX_LAYERED | TRANSPARENT | TOOLWINDOW |
     NOACTIVATE | TOPMOST` window (`ShadowWindow` class). It runs on its own
     thread with its own message loop, so moving it never waits on the WPF UI
     thread.
   - Its picture is drawn once per menu size, on a thread-pool thread, using
     two layers: σ 14 offset 8, and σ 2.5 offset 1.5 (in units × scale). It is
     applied with `UpdateLayeredWindow`.
   - It is empty where the menu is, because the menu's blur-behind would
     otherwise blur the shadow into the glass.
   - Every slide frame moves the menu and the shadow together in one
     `DeferWindowPos` batch, with the shadow inserted just behind the menu.
   - On the last frame of a close, the shadow is parked at once. Otherwise its
     top edge would show above the taskbar until the menu is parked.
   - Measured: no difference to the slide (see Tests).
9. **Which monitor (1.1).** Keyboard toggles arrive through
   `Local\Tessera.ToggleKey`, and everything else through `Local\Tessera.Toggle`.
   - **Keyboard:** the foreground window's monitor. If the foreground is the
     desktop (Progman/WorkerW), which spans all monitors, the pointer's monitor
     is used instead.
   - **Pointer:** if the pointer is over a taskbar (checked with taskbar
     rectangles, not `WindowFromPoint`, which can wait on other apps), that
     taskbar's monitor. Otherwise the same rule as the keyboard.
   - **Primary monitor:** uses `SystemParameters.WorkArea` and slides behind
     `Shell_TrayWnd` exactly as before.
   - **Other monitors:** use that monitor's `rcWork` and slide behind its
     `Shell_SecondaryTrayWnd`.
   - **Parking:** the parked position is below the lowest monitor (the virtual
     screen's bottom), not below the primary screen. With one monitor that's
     the same place.
10. **Settings live in `settings.ini`, not `categories.txt`.** That keeps the
    user's category file untouched by look-and-feel changes.

## Gotchas and rules

- **User preferences:**
  - Don't take over their screen for long tests. Brief open/close checks are
    fine, but say so first.
  - They may be using the menu while you test, which makes results unreliable.
  - No approval prompts for config or restart steps.
  - No `.bak` backups.
  - **Never touch anything under "Backups".**
- **Testing tips:**
  - Simulated input (`keybd_event`/`SendInput`) works for tests. A background
    process can't take focus on its own, so use `tools/refocus-claude.ps1`
    (Alt tap + `SetForegroundWindow`) to reset focus between tests.
  - **If the Claude window is minimized**, `refocus-claude.ps1` still makes it
    the foreground window, but Tessera deliberately doesn't return focus to a
    minimized window. Then the test table shows `focus=Tessera` after each
    close. That's not a bug. For the focus rows, put a visible ordinary window
    in front first (on 2026-11-25 a small WinForms window started from
    PowerShell was used).
  - After the "tagged Ctrl+Esc" row, the real Windows Start can stay open, and
    a cloaked SearchHost or StartMenuExperienceHost can hold focus. Then typing
    goes nowhere and focus can't be moved. Press Esc first. If that doesn't
    help, `Stop-Process -Name SearchHost -Force` clears it, and Windows
    relaunches SearchHost on its own.
  - **Timer resolution:** in a background PowerShell with no visible window,
    Windows 11 ignores `timeBeginPeriod(1)`, so `Thread.Sleep(1)` really
    sleeps about 15.6 ms. That made slides look like they skipped frames
    (31 ms gaps) when they didn't. `measure-slide.ps1` busy-waits instead.
- **Debug output:** Windhawk's `Wh_Log` goes to OutputDebugString. Read it with
  `tools/debug-output-listener.ps1 -Out <file> -Seconds <n>`, which keeps lines
  containing "tessera". Install the mod with `-Logging` first. SearchHost is
  sandboxed, so its debug output isn't visible this way. Tessera itself has
  `StartWindow.Log(...)`, which appends to `debug.log` next to the exe. Nothing
  calls it in the shipped build.
- **Windows PowerShell 5.1 quoting:** use `\"` inside a native argument (see
  `install-mod.ps1`). `Set-Content -Encoding UTF8` adds a BOM, and re-reading a
  UTF-8 file as ANSI garbles `⊞`. Use the Edit tool or explicit encodings.
  `Tessera.cs` is UTF-8 without a BOM.
- **Windhawk symbol strings** use the form
  `public: virtual long __cdecl Class::Method(enum X,enum Y)`, with no space after
  commas. `tools/download-pdb.ps1` fetches a DLL's PDB from Microsoft's symbol
  server so you can grep decorated names.
- **Windows updates:** if an update renames `XamlLauncher` symbols, the mod fails
  to load. That's safe: the `Local\Tessera.Mod` mutex is then missing, so
  Tessera's own mouse hook handles the Start button again. If the mod log says
  symbols weren't found, re-check the names with `tools/download-pdb.ps1`.

## Open bugs and loose ends

- **Multi-monitor is untested on real hardware.** This PC has one monitor
  (1920×1080 at 150%). The single-monitor path was tested and is unchanged.
  Things to check on a second monitor:
  - Win on a window there opens the menu there.
  - The Start button on the second taskbar opens it there.
  - It slides from behind that taskbar.
  - A monitor with a **different DPI** will show a bitmap-stretched (slightly
    soft) menu, because the process is system-DPI-aware, not per-monitor aware.
  - Taskbars on the top or side of a monitor aren't handled: the menu always
    slides up from the bottom of the work area.
- **Reboot test still pending.** Not done, at the user's request: they'll reboot
  themselves. After rebooting and signing in, run `tools\check-health.ps1`. If
  every line says OK, the reboot test has passed.
- **Click-then-Win edge case.** If you click another window and press Win within
  400 ms, the open is ignored because of the Start button guard. This is rare and
  hasn't been reported.
- **Focus after opening from the taskbar.** When the menu was opened from the
  Start button and is then closed with Win or Esc, focus goes back to whatever
  was active before the click. That could be the taskbar if it took focus. This
  hasn't been seen as a problem.
- **Alt-tap side effect.** The Alt tap used for the Start-button close is sent
  while Tessera is in front, so it shouldn't trigger anything in other apps. It's
  worth watching for menu bars flashing.
- **The Win key over an elevated window** with the new builds is still untested
  (it worked before the rename, and the code path is unchanged).
- With the takeover off and the tray icon hidden, the only ways back into
  Tessera are running `Tessera.exe` (for example via Windows Start search or the
  `Tessera.lnk` shortcut) or `schtasks /Run /tn Tessera`. Then switch the
  takeover on again from the footer.

### Multi-monitor test

Steps to follow by hand on a PC with two monitors:

1. Run `tools\check-health.ps1` and confirm both monitors are listed, with their
   scales.
2. Focus a window on monitor 2 and press Win. The menu should open centred on
   monitor 2 and slide up from behind its taskbar.
3. Press Win again. The menu should close and focus should return to that window.
4. Click the Start button on monitor 2's taskbar. The menu should open there.
5. Focus a window on monitor 2 and press Ctrl+Esc. The menu should open there.
6. Click the desktop of monitor 2, then press Win. The menu should open on the
   monitor the pointer is on.
7. Repeat Win on monitor 1 to confirm it behaves as before.
8. If the scales differ, expect slightly soft text on the monitor that doesn't
   use the primary scale. This is known: per-monitor DPI isn't done.
9. A taskbar on the top or side of a monitor isn't handled. This is known.

## Next steps, in priority order

1. **Reboot test.** Reboot, sign in, and run `tools\check-health.ps1`. All lines
   OK means it passed.
2. **Multi-monitor check** on a second monitor, if the user has one. Follow the
   "Multi-monitor test" subsection above.
3. **Ideas, if asked:**
   - Per-monitor DPI awareness, for menus on monitors with a different scaling.
   - Handling taskbars on the top or side of a monitor.
   - Making the tint colour itself (not just its strength) a setting.
