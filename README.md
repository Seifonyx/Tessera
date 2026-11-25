# Tessera

**A translucent Start menu that groups your apps into category tiles.**

Windows 11 can show your apps by category, but it won't let you choose the
categories. Tessera does: you decide the groups, and each one appears as a
frosted tile showing its first few app icons. Click a tile to see everything
in it.

## Features

- **Your categories.** Groups come from a plain text file, `categories.txt`. You
  can also edit them by right-clicking a card or an app in the menu.
- **Opens like the real Start.** It opens with the Windows key, Ctrl+Esc and the
  taskbar Start button, and slides up from behind the taskbar.
- **Translucent glass look.** A light blur with a faint tint, frosted tiles,
  15 px corners and a soft drop shadow, matching the Windhawk
  *TranslucentStartMenu* theme.
- **Settings inside the menu.** Tint strength, tile size, the shadow and the
  tray icon, all applied straight away.
- **Several monitors.** The Windows key and Ctrl+Esc open it on the monitor
  you're working on. The Start button opens it on the monitor of the taskbar
  you clicked.
- **Search.** Start typing as soon as the menu opens.
  Search also finds programs on PATH (exact and prefix matches), App Paths registry
  entries, .exe/.bat/.cmd/.com/.msc files in the Windows Search index, and typed
  paths. File results show their folder on the right and have Open, Run as
  administrator, Open file location and Copy path in the right-click menu. Which
  files Windows indexes depends on its indexed locations (Settings > Privacy &
  security > Searching Windows).
- **Windows Start is still there.** The ⊞ button in the footer opens the normal
  Windows Start menu.
- **Works over admin windows.** A small helper keeps the Windows key working
  even when an elevated window such as Task Manager is in front.
- **Fast.** The menu window stays loaded, so opening it is just the slide. The
  slide starts about 3 ms after the key press and moves once per screen
  refresh.

## Using it

Right-click the footer (the strip with your name and the power button) for
Tessera's own options. There's no tray icon unless you turn it on in Settings.

| To | Do |
|---|---|
| Open or close | Windows key, Ctrl+Esc, or the taskbar Start button |
| Open the normal Windows Start | ⊞ in the footer |
| Change categories | Right-click a card or an app, or footer → *Edit categories* |
| Change the look | Footer → *Settings* |
| Turn the takeover off | Footer → untick *Open with Windows key and Start button* |
| Version and location | Footer → *About Tessera* |
| Quit | Footer → *Exit Tessera* |
| Start it again after quitting | Run `Tessera.exe`, or `schtasks /Run /tn Tessera` |

While the takeover is off, the Windows key opens Windows' own Start. You can
still open Tessera by running `Tessera.exe` (for example from Windows Start
search), then switch the takeover back on from its footer.

## Settings

Footer → *Settings* opens a page inside the menu. Changes apply straight away
and are saved to `settings.ini` next to `Tessera.exe`.

| Setting | What it does | Default |
|---|---|---|
| Tint strength | How dark the glass is, 0 to 60% | 15%, the theme's own tint |
| Tile size | Small, Medium or Large tiles and cards. The menu gets narrower or wider to match, and icons are fetched again at the new size so they stay sharp. | Medium |
| Shadow | A soft shadow around the menu | On |
| Tray icon | Shows Tessera's icon in the notification area, with the same options as the footer | Hidden |

`settings.ini` is a plain text file:

```ini
# TintAlpha: 0-255; 37 is about 15%
TintAlpha=37
# TileSize: Small, Medium or Large
TileSize=Medium
ShowTrayIcon=0
Shadow=1
```

Comments go on their own lines.

## categories.txt

```ini
# Lines starting with # are comments.
# [Pinned] is the top row, [Hidden] is never shown,
# every other [Section] is a category tile, in this order.
[Pinned]
Brave
Spotify

[Games]
Steam
Rocket League

[Hidden]
Some Uninstaller
```

- List apps by the name shown in the menu. If two apps share a name, use the
  app's AppID instead.
- Apps not listed anywhere go to **Other**.
- Changes show up the next time the menu opens.

## How it runs

Two scheduled tasks start Tessera when you sign in:

| Task | Runs | Role |
|---|---|---|
| `Tessera` | `Tessera.exe --background`, normal rights | The menu itself. Apps you open from it don't run as admin. |
| `Tessera Helper` | `Tessera.exe --hook`, highest rights | Catches the Windows key and Ctrl+Esc, including over admin windows, and tells the menu to open. It shows nothing itself. |

You can stop or start either task with `schtasks /End` and `schtasks /Run`;
neither needs an admin prompt.

### Windhawk mod (optional)

`tessera-start-button.wh.cpp` is a Windhawk mod for explorer. It makes the
taskbar Start button, and anything else that asks Windows to show Start, open
Tessera from inside explorer. Without it, Tessera watches for Start button
clicks with a mouse hook.

The Windows key always goes through the helper's keyboard hook. When Windows
sees a lone Win key press, it hands the keyboard to its own Start search before
explorer is told, so the key has to be caught before Windows sees it.

- `install-mod.cmd` builds and enables the mod, and removes older builds of it
  once Windhawk has let go of them.
- `install-mod.cmd -Logging` does the same, with the mod's log switched on.
- `install-mod.cmd -Uninstall` removes it.

If Tessera isn't running, the mod lets Windows open its own Start menu.

## Building

```
build.cmd
```

This uses the C# compiler that ships with Windows (.NET Framework 4.8), so no
SDK is needed. The source is C# 5 only. You can run it while Tessera is
running: the running copy is renamed aside, and both processes restart into the
new build on their own.

## Files

| File | What it is |
|---|---|
| `Tessera.cs` | All of the app's source code |
| `Tessera.exe` | The app, built by `build.cmd` |
| `Tessera.ico` | App icon, generated by `make-icon.ps1` |
| `categories.txt` | Your categories |
| `settings.ini` | Your settings (tint, tile size, shadow, tray icon) |
| `tessera-start-button.wh.cpp` | Windhawk mod source, installed by `install-mod.cmd` |
| `disabled.flag` | Only present while the takeover is switched off |
| `tools\` | Test and measuring scripts (see `HANDOFF.md`) |

## For developers: keeping it smooth

These choices were each measured, so don't undo them:

- **No layered window.** The window isn't layered (no `AllowsTransparency`).
  Transparency comes from `DwmExtendFrameIntoClientArea(-1)` plus a transparent
  HwndSource background.
- **Blur.** The blur is `SetWindowCompositionAttribute` blur-behind (accent
  state 3), with the tint drawn by WPF. Acrylic (state 4) adds a frost layer and
  grain that can't be removed.
- **Corners.** The rounded corners come from `SetWindowRgn`, which is
  re-applied only when the size changes.
- **Shadow.** The region clips anything drawn outside the corners, so the
  shadow is a separate click-through layered window with its own thread. Its
  picture is drawn once per menu size, and the slide moves it together with
  the menu in one `DeferWindowPos` batch. Measured: no change to the slide.
- **Window stays loaded.** It's parked off-screen, never hidden. A dedicated
  thread moves it once per `DwmFlush`, just below the taskbar in z-order.
- **Focus.** The focus switch runs on a background thread. The search box only
  gets focus after the slide finishes, because focusing it costs about 70 ms.
- **Pre-compiled.** All methods are compiled ahead of time at startup.

`tools\measure-slide.ps1` measures the slide (first move, finish, longest gap
between frames). Run it before and after any change that touches opening or
closing.
