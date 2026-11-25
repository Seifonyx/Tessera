Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Diagnostics; using System.Text; using System.Collections.Generic; using System.Threading;
public static class F {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
  [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  delegate bool EW(IntPtr h, IntPtr l); [DllImport("user32.dll")] static extern bool EnumWindows(EW f, IntPtr l);
  public struct RECT { public int l, t, r, b; } public struct POINT { public int x, y; }
  static IntPtr menu;
  public static void FindMenu() {
    var ids = new HashSet<uint>(); foreach (var p in Process.GetProcessesByName("Tessera")) ids.Add((uint)p.Id);
    EnumWindows((h, l) => { uint pid; GetWindowThreadProcessId(h, out pid); var sb = new StringBuilder(128); GetClassName(h, sb, 128);
      if (ids.Contains(pid) && sb.ToString().StartsWith("HwndWrapper")) { RECT r; GetWindowRect(h, out r); if (r.r - r.l > 300) { menu = h; return false; } } return true; }, IntPtr.Zero);
  }
  static string Fg() { uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); try { return Process.GetProcessById((int)pid).ProcessName; } catch { return "(none)"; } }
  static int MenuTop() { RECT r; GetWindowRect(menu, out r); return r.t; }
  public static string State() { int t = MenuTop(); RECT r; GetWindowRect(menu, out r); bool open = r.b <= 2000 && r.t < 1500 && r.t > -1000 && MenuTopOnScreen(); return (open ? "menu OPEN" : "menu closed") + ", focus=" + Fg(); }
  static bool MenuTopOnScreen() { RECT r; GetWindowRect(menu, out r); var s = System.Windows.Forms.Screen.PrimaryScreen.Bounds; return r.t < s.Bottom - 50; }
  public static void Keys(UIntPtr extra, params byte[] vks) { foreach (var v in vks) keybd_event(v, 0, 0, extra); for (int i = vks.Length - 1; i >= 0; i--) keybd_event(vks[i], 0, 2, extra); }
  public static void Click(int x, int y) { POINT old; GetCursorPos(out old); SetCursorPos(x, y); Thread.Sleep(30); mouse_event(2, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(30); mouse_event(4, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(30); SetCursorPos(old.x, old.y); }
}
'@ -ReferencedAssemblies System.Windows.Forms, System.Drawing
[void][F]::SetProcessDPIAware(); [F]::FindMenu()
$none = [UIntPtr]::Zero; $tag = [UIntPtr]::new(0x5C57A27)
function Check($label) { Start-Sleep -Milliseconds 650; "{0,-26} {1}" -f $label, [F]::State() }
"start:                     " + [F]::State()
[F]::Keys($none, 0x5B); Check 'Win'
[F]::Keys($none, 0x5B); Check 'Win again'
[F]::Keys($none, 0x11, 0x1B); Check 'Ctrl+Esc'
[F]::Keys($none, 0x11, 0x1B); Check 'Ctrl+Esc again'
$root = [Windows.Automation.AutomationElement]::RootElement
$cond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, 'StartButton')
$sb = $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $cond)
if ($sb) {
  $r = $sb.Current.BoundingRectangle; $x = [int]($r.X + $r.Width/2); $y = [int]($r.Y + $r.Height/2)
  "Start button at $x,$y"
  [F]::Click($x, $y); Check 'Start button click'
  [F]::Click($x, $y); Check 'Start button click again'
} else { 'Start button not found' }
[F]::Keys($tag, 0x11, 0x1B); Start-Sleep -Milliseconds 900; "{0,-26} {1}" -f 'tagged Ctrl+Esc (Windows Start route)', [F]::State()
[F]::Keys($none, 0x1B); Check 'Esc'

