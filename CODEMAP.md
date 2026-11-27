# CODEMAP

Read this before searching code. Update it when files are added, moved or removed.

## Root
- `Tessera.cs` - the whole app, single C# file (~2200 lines), compiled by build.cmd.
- `build.cmd` - builds Tessera.exe with csc.
- `Tessera.exe` - build output (gitignored); `Tessera.old-*.exe` are swapped-out copies.
- `Tessera.ico`, `make-icon.ps1` - app icon and its generator.
- `categories.txt` - user-defined category tiles and their apps.
- `settings.ini` - user settings (written by the settings page).
- `usage.txt` - launch counts for ranking (gitignored). `picks.txt` - learned search picks (gitignored).
- `install-mod.cmd`, `install-mod.ps1` - install the Windhawk mod.
- `tessera-start-button.wh.cpp` - Windhawk mod that redirects the Start button to Tessera.
- `README.md` - user-facing overview. `HANDOFF.md` - long-form working context.
- `CLAUDE.md` - rules for Claude sessions. `handoff/` - short .md files passed between threads.

## Tessera.cs (search by class name)
- `AppEntry` - one launchable app/shortcut.
- `Shell` - Start-menu/app enumeration and launching helpers.
- `FileSearch` - Windows Search / PATH / App Paths file lookup for the search box.
- `Matcher` - search match tiers (exact, prefix, word start, initials, any-order words, contains, fuzzy, typo).
- `Picks` - learns which result you launch for each typed query (picks.txt, gitignored).
- `Calc` - calculator answers in the search box.
- `Extras` - Settings pages, Control Panel items, power actions, app keywords (+ keywords.txt), open windows, recent files, browser bookmarks.
- `Everything` - file/program search through voidtools Everything (es.exe) when it runs; Windows index otherwise.
- `Convert2`, `Rates` - unit and currency conversion (rates cached in rates.txt, gitignored).
- `UsageInfo`, `Usage` - launch-frequency tracking.
- `BitTools` - small bit helpers.
- `Category`, `Config` - categories.txt model and loader.
- `Settings` - settings.ini model.
- `Hooks` - global hooks (Start key/button, focus, dismissal).
- `Bezier` - easing for slide animation.
- `ShadowWindow` - drop shadow behind the panel.
- `StartWindow` - main UI: tiles, search, footer menu, settings page, animation, multi-monitor.
- `Styles` - shared brushes/styles.
- `Program` - entry point.

## tools/
- `check-health.ps1` - post-reboot health check.
- `debug-output-listener.ps1` - captures OutputDebugString from Tessera.
- `download-pdb.ps1` - fetches symbols for debugging.
- `measure-slide.ps1`, `trace-focus-and-slide.ps1`, `test-open-close.ps1`, `refocus-claude.ps1` - animation/focus test helpers.
