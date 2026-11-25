param([byte[]]$Keys = @(0x5B), [int]$Ms = 900)
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Diagnostics; using System.Text; using System.Collections.Generic;
public static class T {
  [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr p, IntPtr after, string cls, string title);
  public struct RECT { public int l, t, r, b; }
  public static void Tap(byte[] vks) { foreach (var v in vks) keybd_event(v, 0, 0, UIntPtr.Zero); for (int i = vks.Length - 1; i >= 0; i--) keybd_event(vks[i], 0, 2, UIntPtr.Zero); }
  static string Desc(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); var sb = new StringBuilder(128); GetClassName(h, sb, 128); string n; try { n = Process.GetProcessById((int)pid).ProcessName; } catch { n = "?"; } return n + "/" + sb + "/" + h.ToInt64().ToString("X"); }
  delegate bool EW(IntPtr h, IntPtr l); [DllImport("user32.dll")] static extern bool EnumWindows(EW f, IntPtr l);
  public static IntPtr Menu() {
    IntPtr found = IntPtr.Zero; var ids = new HashSet<uint>();
    foreach (var p in Process.GetProcessesByName("Tessera")) ids.Add((uint)p.Id);
    EnumWindows((h, l) => { uint pid; GetWindowThreadProcessId(h, out pid); var sb = new StringBuilder(128); GetClassName(h, sb, 128);
      if (ids.Contains(pid) && sb.ToString().StartsWith("HwndWrapper")) { RECT r; GetWindowRect(h, out r); if (r.r - r.l > 300) { found = h; return false; } }
      return true; }, IntPtr.Zero);
    return found;
  }
  public static List<string> Trace(byte[] keys, int ms, IntPtr menu) {
    var res = new List<string>(); var sw = Stopwatch.StartNew(); string last = null;
    if (keys.Length > 0) Tap(keys); else System.Threading.EventWaitHandle.OpenExisting("Local\\Tessera.Toggle").Set();
    while (sw.ElapsedMilliseconds < ms) {
      RECT r = new RECT(); if (menu != IntPtr.Zero) GetWindowRect(menu, out r);
      string s = Desc(GetForegroundWindow()) + " menuTop=" + r.t;
      if (s != last) { res.Add(sw.ElapsedMilliseconds.ToString().PadLeft(4) + "ms " + s); last = s; }
      System.Threading.Thread.Sleep(5);
    }
    return res;
  }
}
'@
$m = [T]::Menu(); "menu hwnd = {0:X}" -f $m.ToInt64()
[T]::Trace($Keys, $Ms, $m)
