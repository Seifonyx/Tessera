Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Diagnostics;
public static class R {
  [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  public static string Go() {
    IntPtr target = IntPtr.Zero;
    foreach (var p in Process.GetProcessesByName("claude")) if (p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Length > 0) { target = p.MainWindowHandle; break; }
    if (target == IntPtr.Zero) return "no claude window";
    // Alt tap: lets this process change the foreground window (the standard way around the focus lock)
    keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero);
    bool ok = SetForegroundWindow(target);
    System.Threading.Thread.Sleep(100);
    return "SetForegroundWindow=" + ok + " now fg is claude: " + (GetForegroundWindow() == target);
  }
}
'@
[R]::Go()
