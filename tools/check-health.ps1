# Checks that Tessera is up: the two scheduled tasks, the menu and helper processes,
# the named objects that link them to the Windhawk mod, and the monitors Windows reports.
# Read-only: it starts, stops and changes nothing, and needs no admin rights.
#
#   powershell -ExecutionPolicy Bypass -File tools\check-health.ps1
#
# Run it right after signing in following a reboot. Every line should say OK.
# The exit code is the number of problems found (0 = healthy).

Add-Type -TypeDefinition @'
using System; using System.Threading; using System.Runtime.InteropServices; using System.Collections.Generic;
public static class TH {
  public static string Mutex(string name) {
    try { System.Threading.Mutex.OpenExisting(name).Dispose(); return "present"; }
    catch (WaitHandleCannotBeOpenedException) { return "missing"; }
    catch (UnauthorizedAccessException) { return "present"; }   // owned by the elevated helper
  }
  public static string Event(string name) {
    try { var e = EventWaitHandle.OpenExisting(name); bool set = e.WaitOne(0); e.Dispose(); return set ? "set" : "reset"; }
    catch (WaitHandleCannotBeOpenedException) { return "missing"; }
    catch (UnauthorizedAccessException) { return "present"; }
  }
  [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int l, t, r, b; }
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct MI {
    public int cb; public RECT mon, work; public uint flags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dev; }
  delegate bool MEP(IntPtr h, IntPtr dc, ref RECT r, IntPtr d);
  [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MEP f, IntPtr d);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr h, ref MI mi);
  [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr h, int type, out uint x, out uint y);
  public static List<string> Monitors() {
    SetProcessDPIAware();
    var list = new List<string>();
    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref RECT r, IntPtr d) => {
      var mi = new MI(); mi.cb = Marshal.SizeOf(typeof(MI)); GetMonitorInfo(h, ref mi);
      uint dx = 0, dy; try { GetDpiForMonitor(h, 0, out dx, out dy); } catch { }
      string edge = mi.work.b < mi.mon.b ? "bottom" : mi.work.t > mi.mon.t ? "top" : mi.work.l > mi.mon.l ? "left" : mi.work.r < mi.mon.r ? "right" : "hidden/auto-hide";
      list.Add(string.Format("{0}{1}: {2}x{3} at {4},{5}, scale {6}%, taskbar {7}",
        mi.dev, (mi.flags & 1) != 0 ? " (primary)" : "", mi.mon.r - mi.mon.l, mi.mon.b - mi.mon.t, mi.mon.l, mi.mon.t,
        dx == 0 ? "?" : (dx * 100 / 96).ToString(), edge));
      return true; }, IntPtr.Zero);
    return list;
  }
}
'@

$problems = 0
function Line($ok, $text) {
  if ($ok) { Write-Host ("  OK    " + $text) -ForegroundColor Green }
  else { Write-Host ("  FAIL  " + $text) -ForegroundColor Red; $script:problems++ }
}
function Note($text) { Write-Host ("  NOTE  " + $text) -ForegroundColor Yellow }

$boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
Write-Host ("Tessera health check, " + (Get-Date -Format 'yyyy-MM-dd HH:mm') + " (last boot " + $boot.ToString('yyyy-MM-dd HH:mm') + ")")

Write-Host "`nScheduled tasks"
foreach ($name in 'Tessera', 'Tessera Helper') {
  $t = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
  if (-not $t) { Line $false "$name task is missing"; continue }
  $i = $t | Get-ScheduledTaskInfo
  $logon = @($t.Triggers | Where-Object { $_.CimClass.CimClassName -eq 'MSFT_TaskLogonTrigger' }).Count -gt 0
  Line $logon ("$name task has an At logon trigger")
  # 267009 = still running, 0 = finished fine. The menu and helper normally keep running.
  $rc = $i.LastTaskResult
  $ranSinceBoot = $i.LastRunTime -gt $boot
  $msg = '{0} last ran {1}, result 0x{2:X}, state {3}' -f $name, $i.LastRunTime.ToString('yyyy-MM-dd HH:mm'), $rc, $t.State
  Line ($ranSinceBoot -and ($rc -eq 0 -or $rc -eq 267009)) $msg
}

Write-Host "`nProcesses"
$procs = @(Get-CimInstance Win32_Process -Filter "Name = 'Tessera.exe'")
# An elevated helper's command line is unreadable from a normal shell, so a null one counts as the helper.
$menu = @($procs | Where-Object { $_.CommandLine -and $_.CommandLine -notmatch '--hook' })
$helper = @($procs | Where-Object { -not $_.CommandLine -or $_.CommandLine -match '--hook' })
Line ($menu.Count -eq 1) ("menu process running: " + $menu.Count + $(if ($menu.Count) { " (pid " + ($menu.ProcessId -join ', ') + ")" }))
Line ($helper.Count -eq 1) ("helper process running: " + $helper.Count + $(if ($helper.Count) { " (pid " + ($helper.ProcessId -join ', ') + ")" }))
if ($procs.Count) {
  $withPath = @($procs | Where-Object { $_.ExecutablePath })
  $exe = $null
  if ($withPath.Count) { $exe = $withPath[0].ExecutablePath }
  if ($exe) { Note ("running " + $exe + ", version " + (Get-Item $exe).VersionInfo.FileVersion) }
}

Write-Host "`nNamed objects"
Line ([TH]::Mutex('Local\Tessera.Instance') -eq 'present') ('Local\Tessera.Instance mutex (menu): ' + [TH]::Mutex('Local\Tessera.Instance'))
Line ([TH]::Mutex('Local\Tessera.Helper') -eq 'present') ('Local\Tessera.Helper mutex (helper): ' + [TH]::Mutex('Local\Tessera.Helper'))
Line ([TH]::Mutex('Local\Tessera.Mod') -eq 'present') ('Local\Tessera.Mod mutex (Windhawk mod in explorer): ' + [TH]::Mutex('Local\Tessera.Mod'))
$en = [TH]::Event('Local\Tessera.Enabled')
if ($en -eq 'set') { Line $true 'takeover is on (Local\Tessera.Enabled is set)' }
elseif ($en -eq 'reset') { Note 'takeover is off: Win and the Start button open the real Start. Switch it on from the footer menu.' }
elseif ($en -eq 'present') { Line $true 'Local\Tessera.Enabled event exists (owned by another process, state not readable)' }
else { Line $false 'Local\Tessera.Enabled event is missing' }

Write-Host "`nMonitors"
$mons = [TH]::Monitors()
foreach ($m in $mons) { Write-Host ("        " + $m) }
if ($mons.Count -gt 1) {
  Note 'more than one monitor: see "Multi-monitor test" in HANDOFF.md'
  if (@($mons | ForEach-Object { ($_ -split 'scale ')[1].Split('%')[0] } | Sort-Object -Unique).Count -gt 1) {
    Note 'monitors use different scaling: the menu will look slightly soft on the non-primary scale (expected for now)'
  }
  if (@($mons | Where-Object { $_ -notmatch 'taskbar (bottom|hidden)' }).Count) {
    Note 'a taskbar is on the top or side of a monitor: Tessera still slides up from the bottom there (known limit)'
  }
}

Write-Host ""
if ($problems -eq 0) { Write-Host "All checks passed." -ForegroundColor Green }
else { Write-Host "$problems problem(s) found." -ForegroundColor Red }
exit $problems
