@echo off
rem Builds and enables Tessera's Windhawk mod (the taskbar Start button opens Tessera).
rem Options: -Logging (turn on the mod's log), -Uninstall (remove the mod).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-mod.ps1" %*
