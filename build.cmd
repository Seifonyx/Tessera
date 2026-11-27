@echo off
rem Builds Tessera.exe with the C# compiler that ships with Windows (.NET Framework 4.8). No SDK needed.
rem Safe to run while Tessera is running: the running copy is renamed aside, and the running
rem menu and helper notice the new build and restart into it by themselves.
set FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
set WPF=%FW%\WPF
cd /d "%~dp0"
del /q Tessera.old-*.exe 2>nul
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /out:Tessera.new.exe ^
  /win32icon:Tessera.ico /resource:Tessera.ico,Tessera.ico ^
  /r:"%WPF%\PresentationFramework.dll" /r:"%WPF%\PresentationCore.dll" /r:"%WPF%\WindowsBase.dll" ^
  /r:"%WPF%\UIAutomationClient.dll" /r:"%WPF%\UIAutomationTypes.dll" ^
  /r:System.Xaml.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Data.dll /r:System.Web.Extensions.dll ^
  Tessera.cs
if errorlevel 1 exit /b 1
if exist Tessera.exe ren Tessera.exe Tessera.old-%RANDOM%.exe
ren Tessera.new.exe Tessera.exe
