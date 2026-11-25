# Measures the open and close slide: presses Win (untagged, so Tessera takes it) -Count times to open and close,
# polls the menu's position every millisecond and prints, for each slide, when the first move happened, when it
# finished, how many moves there were and the longest gap between two moves (one screen refresh is ~16.7 ms at 60 Hz).
# Takes the screen for about (Count * 2) seconds. Gives focus back to the Claude window at the end.
#   tools\measure-slide.ps1 -Count 5
param([int]$Count = 5)
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Diagnostics; using System.Text; using System.Collections.Generic; using System.Threading;
public static class MS {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint p);
  [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint p);
  [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  delegate bool EW(IntPtr h, IntPtr l); [DllImport("user32.dll")] static extern bool EnumWindows(EW f, IntPtr l);
  public struct RECT { public int l, t, r, b; }
  public static IntPtr Menu() {
    IntPtr found = IntPtr.Zero; var ids = new HashSet<uint>();
    foreach (var p in Process.GetProcessesByName("Tessera")) ids.Add((uint)p.Id);
    EnumWindows((h, l) => { uint pid; GetWindowThreadProcessId(h, out pid); var sb = new StringBuilder(128); GetClassName(h, sb, 128);
      if (ids.Contains(pid) && sb.ToString().StartsWith("HwndWrapper")) { RECT r; GetWindowRect(h, out r); if (r.r - r.l > 300) { found = h; return false; } }
      return true; }, IntPtr.Zero);
    return found;
  }
  // one slide: returns "first,done,moves,maxgap" in ms
  public static string Slide(IntPtr menu, int ms) {
    RECT r; GetWindowRect(menu, out r); int last = r.t;
    var sw = Stopwatch.StartNew();
    keybd_event(0x5B, 0, 0, UIntPtr.Zero); keybd_event(0x5B, 0, 2, UIntPtr.Zero);
    double first = -1, prev = -1, done = -1, gap = 0; int moves = 0;
    while (sw.ElapsedMilliseconds < ms) {
      GetWindowRect(menu, out r);
      if (r.t != last) {
        double now = sw.Elapsed.TotalMilliseconds; last = r.t; moves++;
        if (first < 0) first = now; else gap = Math.Max(gap, now - prev);
        prev = now; done = now;
      }
      // busy-wait about 0.2 ms: Sleep(1) can really sleep 15.6 ms, because Windows 11 ignores timeBeginPeriod in a
      // background process without a visible window, and that showed up as fake 31 ms gaps
      var until = sw.Elapsed.TotalMilliseconds + 0.2; while (sw.Elapsed.TotalMilliseconds < until) Thread.SpinWait(20);
    }
    return string.Format("{0:0.0},{1:0.0},{2},{3:0.0}", first, done, moves, gap);
  }
}
'@
[void][MS]::SetProcessDPIAware(); [void][MS]::timeBeginPeriod(1)
$m = [MS]::Menu()
if ($m -eq [IntPtr]::Zero) { 'menu window not found'; return }
$rows = @()
for ($i = 1; $i -le $Count; $i++) {
  foreach ($kind in 'open', 'close') {
    $f = ([MS]::Slide($m, 700)).Split(',')
    $rows += [pscustomobject]@{ Run = $i; Slide = $kind; FirstMoveMs = [double]$f[0]; DoneMs = [double]$f[1]; Moves = [int]$f[2]; MaxGapMs = [double]$f[3] }
    Start-Sleep -Milliseconds 350
  }
}
[void][MS]::timeEndPeriod(1)
$rows | Format-Table -AutoSize
foreach ($kind in 'open', 'close') {
  $s = $rows | Where-Object Slide -eq $kind
  "{0,-5} median: first move {1:0.0} ms, done {2:0.0} ms, max gap {3:0.0} ms (worst {4:0.0} ms)" -f $kind,
    (($s.FirstMoveMs | Sort-Object)[[int][math]::Floor($s.Count / 2)]), (($s.DoneMs | Sort-Object)[[int][math]::Floor($s.Count / 2)]),
    (($s.MaxGapMs | Sort-Object)[[int][math]::Floor($s.Count / 2)]), (($s.MaxGapMs | Measure-Object -Maximum).Maximum)
}
& (Join-Path $PSScriptRoot 'refocus-claude.ps1')
