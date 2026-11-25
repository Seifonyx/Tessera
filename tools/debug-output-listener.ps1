param([string]$Out, [int]$Seconds = 120)
Add-Type -TypeDefinition @'
using System; using System.IO; using System.Threading; using System.Runtime.InteropServices; using System.IO.MemoryMappedFiles; using System.Text;
public static class DbWin {
  public static void Run(string outPath, int seconds) {
    var ready = new EventWaitHandle(false, EventResetMode.AutoReset, "DBWIN_BUFFER_READY");
    var data = new EventWaitHandle(false, EventResetMode.AutoReset, "DBWIN_DATA_READY");
    var mmf = MemoryMappedFile.CreateOrOpen("DBWIN_BUFFER", 4096);
    var view = mmf.CreateViewAccessor();
    var end = DateTime.UtcNow.AddSeconds(seconds);
    ready.Set();
    while (DateTime.UtcNow < end) {
      if (!data.WaitOne(500)) continue;
      int pid = view.ReadInt32(0);
      var bytes = new byte[4092]; view.ReadArray(4, bytes, 0, 4092);
      int n = Array.IndexOf(bytes, (byte)0); if (n < 0) n = 4092;
      string s = Encoding.Default.GetString(bytes, 0, n).TrimEnd();
      if (s.IndexOf("tessera", StringComparison.OrdinalIgnoreCase) >= 0)
        File.AppendAllText(outPath, DateTime.Now.ToString("HH:mm:ss.fff ") + pid + " " + s + "\r\n");
      ready.Set();
    }
  }
}
'@
[DbWin]::Run($Out, $Seconds)

