// Tessera - a translucent Start menu for Windows 11 that groups your apps into category tiles.
// Opens with the Windows key, Ctrl+Esc and the taskbar Start button; the footer's ⊞ button opens Windows' own Start.
// Categories live in categories.txt next to this program (plain text, editable; also editable from the right-click menu).
// Build: build.cmd (uses the C# compiler that ships with Windows). See README.md.
using System;
using System.Reflection;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WF = System.Windows.Forms;
using SD = System.Drawing;

// shown in Task Manager, the file's Properties > Details, and Tessera's About box
[assembly: AssemblyTitle("Tessera")]
[assembly: AssemblyProduct("Tessera")]
[assembly: AssemblyDescription("A translucent Start menu that groups your apps into category tiles.")]
[assembly: AssemblyCopyright("© 2026")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0")]

namespace Tessera
{
    public class AppEntry
    {
        public string Name;
        public string Id;
        public IntPtr Pidl;
        public ImageSource Icon;
    }

    // ---------------------------------------------------------------- shell: list apps, icons, launching
    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumShellItems
    {
        [PreserveSig] int Next(uint celt, [MarshalAs(UnmanagedType.Interface)] out IShellItem rgelt, out uint pceltFetched);
        void Skip(uint celt);
        void Reset();
        void Clone(out IEnumShellItems ppenum);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NSIZE { public int cx, cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NSIZE size, int flags, out IntPtr phbm);
    }

    static class Shell
    {
        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderItem([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, int flags, IntPtr token,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);
        [DllImport("shell32.dll")]
        static extern int SHGetIDListFromObject([MarshalAs(UnmanagedType.IUnknown)] object punk, out IntPtr ppidl);
        [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int c, out BITMAP bm);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHELLEXECUTEINFO
        {
            public int cbSize; public uint fMask; public IntPtr hwnd;
            public string lpVerb, lpFile, lpParameters, lpDirectory;
            public int nShow; public IntPtr hInstApp, lpIDList; public string lpClass;
            public IntPtr hkeyClass; public uint dwHotKey; public IntPtr hIcon, hProcess;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO i);

        static readonly Guid AppsFolder = new Guid("1e87508d-89c2-42f0-8a7e-645a0f50ca58");
        static readonly Guid BHID_EnumItems = new Guid("94f60519-2850-4924-aa5a-d15e84868039");

        static string DisplayName(IShellItem item, uint kind)
        {
            IntPtr p; item.GetDisplayName(kind, out p);
            string s = Marshal.PtrToStringUni(p); Marshal.FreeCoTaskMem(p); return s;
        }

        // Every app Windows knows about (same list as Start > All apps), with icons. Must run on an STA thread.
        public static List<AppEntry> Enumerate(Dictionary<string, ImageSource> iconCache)
        {
            var list = new List<AppEntry>();
            IShellItem folder;
            if (SHGetKnownFolderItem(AppsFolder, 0, IntPtr.Zero, typeof(IShellItem).GUID, out folder) != 0) return list;
            IntPtr pEnum;
            folder.BindToHandler(IntPtr.Zero, BHID_EnumItems, typeof(IEnumShellItems).GUID, out pEnum);
            var en = (IEnumShellItems)Marshal.GetObjectForIUnknown(pEnum);
            Marshal.Release(pEnum);
            IShellItem item; uint fetched;
            while (en.Next(1, out item, out fetched) == 0 && fetched == 1)
            {
                try
                {
                    var e = new AppEntry();
                    e.Name = DisplayName(item, 0);                 // SIGDN_NORMALDISPLAY
                    e.Id = DisplayName(item, 0x80018001);         // SIGDN_PARENTRELATIVEPARSING (the AppID)
                    SHGetIDListFromObject(item, out e.Pidl);
                    ImageSource ic;
                    if (iconCache != null && iconCache.TryGetValue(e.Id, out ic)) e.Icon = ic; else e.Icon = GetIcon(item, IconPx);
                    list.Add(e);
                }
                catch { }
                finally { Marshal.ReleaseComObject(item); }
            }
            Marshal.ReleaseComObject(en); Marshal.ReleaseComObject(folder);
            return list;
        }

        // Icons are fetched at exactly the pixel size they're shown at (32 units x your display scaling, e.g. 48 px at
        // 150%), like the real Start menu does. Windows then picks the icon image drawn for that size instead of
        // us shrinking a different size, which is what made them look soft.
        public static double Scale = 1;
        public static int IconPx { get { return (int)Math.Round(32 * Scale); } }

        static ImageSource GetIcon(IShellItem item, int size)
        {
            var f = item as IShellItemImageFactory;
            if (f == null) return null;
            IntPtr hbm;
            NSIZE sz; sz.cx = size; sz.cy = size;
            if (f.GetImage(sz, 0x4, out hbm) != 0 || hbm == IntPtr.Zero) return null;   // ICONONLY, exact size
            try { return ToSource(hbm); } catch { return null; } finally { DeleteObject(hbm); }
        }

        static ImageSource ToSource(IntPtr hbm)
        {
            BITMAP bm;
            GetObject(hbm, Marshal.SizeOf(typeof(BITMAP)), out bm);
            if (bm.bmBitsPixel == 32 && bm.bmBits != IntPtr.Zero)
            {
                int stride = bm.bmWidthBytes, h = Math.Abs(bm.bmHeight);
                var buf = new byte[stride * h];
                Marshal.Copy(bm.bmBits, buf, 0, buf.Length);
                var flipped = new byte[buf.Length];                   // DIB sections are stored bottom-up
                for (int y = 0; y < h; y++) Buffer.BlockCopy(buf, y * stride, flipped, (h - 1 - y) * stride, stride);
                // Some icons come back with colour already multiplied by transparency, some don't. Using the wrong
                // assumption gives dark or bright fringes on soft edges, so check: un-multiplied data has a colour
                // value brighter than its transparency somewhere.
                bool straight = false;
                for (int i = 0; i < flipped.Length && !straight; i += 4)
                {
                    byte a = flipped[i + 3];
                    if (flipped[i] > a || flipped[i + 1] > a || flipped[i + 2] > a) straight = true;
                }
                double dpi = 96 * Scale;   // so the image's natural size is exactly its pixels on screen
                var bs = BitmapSource.Create(bm.bmWidth, h, dpi, dpi, straight ? PixelFormats.Bgra32 : PixelFormats.Pbgra32, null, flipped, stride);
                bs.Freeze(); return bs;
            }
            var s = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            s.Freeze(); return s;
        }

        public static void Launch(AppEntry a, bool admin)
        {
            var t = new Thread(() =>
            {
                var i = new SHELLEXECUTEINFO();
                i.cbSize = Marshal.SizeOf(typeof(SHELLEXECUTEINFO));
                i.fMask = 0x0000000C | 0x00000400;   // SEE_MASK_INVOKEIDLIST | SEE_MASK_FLAG_NO_UI
                i.lpIDList = a.Pidl; i.nShow = 1;
                if (admin) i.lpVerb = "runas";
                ShellExecuteEx(ref i);
            });
            t.SetApartmentState(ApartmentState.STA); t.IsBackground = true; t.Start();
        }

        public static void Free(List<AppEntry> list) { foreach (var a in list) if (a.Pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(a.Pidl); }
    }

    // ---------------------------------------------------------------- categories.txt
    public class Category { public string Name; public List<string> Keys = new List<string>(); }

    public class Config
    {
        public string Path;
        public List<Category> Cats = new List<Category>();
        public DateTime Stamp;

        public Config(string path) { Path = path; Load(); }

        public void Load()
        {
            Cats.Clear();
            if (!File.Exists(Path)) { File.WriteAllText(Path, "[Pinned]\r\nFile Explorer\r\nSettings\r\n"); }
            Category cur = null;
            foreach (var raw in File.ReadAllLines(Path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]")) { cur = Get(line.Substring(1, line.Length - 2).Trim(), true); continue; }
                if (cur != null) cur.Keys.Add(line);
            }
            Stamp = File.GetLastWriteTimeUtc(Path);
        }

        public void Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Tessera categories. Each [Section] is a category card, in this order.");
            sb.AppendLine("# List apps by the name shown in the menu (or by AppID when two apps share a name).");
            sb.AppendLine("# [Pinned] is the top row, [Hidden] is never shown. Apps not listed anywhere go to \"Other\".");
            foreach (var c in Cats)
            {
                sb.AppendLine();
                sb.AppendLine("[" + c.Name + "]");
                foreach (var k in c.Keys) sb.AppendLine(k);
            }
            File.WriteAllText(Path, sb.ToString());
            Stamp = File.GetLastWriteTimeUtc(Path);
        }

        public Category Get(string name, bool create)
        {
            foreach (var c in Cats) if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
            if (!create) return null;
            var n = new Category { Name = name };
            int hidden = Cats.FindIndex(x => x.Name.Equals("Hidden", StringComparison.OrdinalIgnoreCase));
            if (hidden >= 0) Cats.Insert(hidden, n); else Cats.Add(n);
            return n;
        }

        public bool Changed() { return File.Exists(Path) && File.GetLastWriteTimeUtc(Path) != Stamp; }
    }

    // ---------------------------------------------------------------- keyboard / mouse hooks (own thread, so a busy UI never lags input)
    static class Hooks
    {
        delegate IntPtr LLProc(int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, LLProc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string n);
        [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint a, uint b);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint t, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern IntPtr FindWindow(string cls, string title);

        [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr w, l; public uint time; public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct KBD { public uint vk, scan, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] struct MSLL { public int x, y; public uint data, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Explicit)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; [FieldOffset(8)] public MouseFill pad; }
        [StructLayout(LayoutKind.Sequential)] struct MouseFill { public int a, b; public uint c, d, e; public IntPtr f; }

        static LLProc kbProc, msProc;
        static IntPtr kbHook, msHook;
        static uint threadId;
        static bool winDown, combo, swallowUp, escHeld;
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        static Rect startRect = Rect.Empty;
        public static volatile bool Enabled = true;
        public static Action Toggle;

        public static bool Running { get { return threadId != 0; } }
        static bool watching;

        public static void Start()
        {
            if (threadId != 0) return;
            var ready = new ManualResetEvent(false);
            var t = new Thread(() =>
            {
                threadId = GetCurrentThreadId();
                kbProc = KbCb; msProc = MsCb;
                IntPtr mod = GetModuleHandle(null);
                kbHook = SetWindowsHookEx(13, kbProc, mod, 0);
                msHook = SetWindowsHookEx(14, msProc, mod, 0);
                ready.Set();
                MSG m;
                while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { }
                UnhookWindowsHookEx(kbHook); UnhookWindowsHookEx(msHook);
            });
            t.IsBackground = true; t.Priority = ThreadPriority.Highest; t.Start();
            ready.WaitOne(2000);

            if (!watching) { watching = true; var r = new Thread(WatchStartButton); r.IsBackground = true; r.Start(); }
        }

        public static void Stop()
        {
            uint t = threadId;
            if (t != 0) { threadId = 0; winDown = false; swallowUp = false; PostThreadMessage(t, 0x12, IntPtr.Zero, IntPtr.Zero); }
        }

        static readonly IntPtr Tag = (IntPtr)0x5C57A27;   // marks keys this program sends itself
        static INPUT Key(ushort vk, bool up) { var i = new INPUT(); i.type = 1; i.ki.vk = vk; i.ki.flags = up ? 2u : 0u; i.ki.extra = Tag; return i; }

        static void Send(params INPUT[] keys) { SendInput((uint)keys.Length, keys, Marshal.SizeOf(typeof(INPUT))); }

        // Set when the Windhawk mod (custom-start-takeover) is loaded in explorer: it then sends taskbar Start button clicks
        // (and any other "open Start" request) to this menu itself, so the mouse part here stands down. The Win key stays
        // with the keyboard hook: Windows hands the keyboard to its own Start search (SearchHost) as soon as it sees a lone
        // Win press, before explorer is even told, so it has to be hidden from Windows here.
        public static volatile bool ModActive;

        // An Alt tap from this process: Windows then counts it as having had the last input, which lets it move focus
        public static void TapAlt() { Send(Key(0x12, false), Key(0x12, true)); }

        public static void OpenWindowsStart() { Send(Key(0x11, false), Key(0x1B, false), Key(0x1B, true), Key(0x11, true)); } // Ctrl+Esc

        static IntPtr KbCb(int code, IntPtr w, IntPtr l)
        {
            if (code >= 0 && Enabled)
            {
                var k = (KBD)Marshal.PtrToStructure(l, typeof(KBD));
                int msg = (int)w;
                bool down = msg == 0x100 || msg == 0x104, up = msg == 0x101 || msg == 0x105;
                if (k.extra != Tag)                              // ignore keys this program sent itself
                {
                    // Ctrl+Esc (the other built-in Start shortcut) opens this menu too
                    if (k.vk == 0x1B && !winDown)
                    {
                        if (down && (escHeld || ((GetAsyncKeyState(0x11) & 0x8000) != 0 && (GetAsyncKeyState(0x10) & 0x8000) == 0 && (GetAsyncKeyState(0x12) & 0x8000) == 0)))
                        {
                            if (!escHeld) { escHeld = true; if (Toggle != null) Toggle(); }   // ignore key-repeat
                            return (IntPtr)1;
                        }
                        if (up && escHeld) { escHeld = false; return (IntPtr)1; }
                    }
                    if (k.vk == 0x5B || k.vk == 0x5C)
                    {
                        if (down && !winDown) { winDown = true; combo = false; }
                        else if (up)
                        {
                            winDown = false;
                            if (!combo)
                            {
                                // Win pressed alone: hide it from Windows (mask key in between) so the normal Start stays closed
                                Send(Key(0xE8, false), Key(0xE8, true), Key((ushort)k.vk, true));
                                if (Toggle != null) Toggle();
                                return (IntPtr)1;
                            }
                        }
                    }
                    else if (winDown && down) combo = true;
                }
            }
            return CallNextHookEx(kbHook, code, w, l);
        }

        static IntPtr MsCb(int code, IntPtr w, IntPtr l)
        {
            if (code >= 0 && Enabled && !ModActive)
            {
                int msg = (int)w;
                if (msg == 0x201 || msg == 0x202)
                {
                    var m = (MSLL)Marshal.PtrToStructure(l, typeof(MSLL));
                    if (true)
                    {
                        if (msg == 0x201 && !startRect.IsEmpty && startRect.Contains(m.x, m.y))
                        {
                            swallowUp = true;
                            if (Toggle != null) Toggle();
                            return (IntPtr)1;
                        }
                        if (msg == 0x202 && swallowUp) { swallowUp = false; return (IntPtr)1; }
                    }
                }
            }
            return CallNextHookEx(msHook, code, w, l);
        }

        // Keeps track of where the taskbar Start button is, so clicks on it open this menu.
        static void WatchStartButton()
        {
            AutomationElement btn = null; int age = 0;
            while (true)
            {
                try
                {
                    if (btn == null || age++ > 15)
                    {
                        age = 0; btn = null;
                        IntPtr tray = FindWindow("Shell_TrayWnd", null);
                        if (tray != IntPtr.Zero)
                            btn = AutomationElement.FromHandle(tray).FindFirst(TreeScope.Descendants,
                                new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"));
                    }
                    startRect = btn != null ? btn.Current.BoundingRectangle : Rect.Empty;
                }
                catch { btn = null; startRect = Rect.Empty; }
                Thread.Sleep(2000);
            }
        }
    }

    // Cubic-bezier easing curve (same maths as CSS / Windows' Fluent motion curves)
    public class Bezier : IEasingFunction
    {
        readonly double x1, y1, x2, y2;
        public Bezier(double x1, double y1, double x2, double y2) { this.x1 = x1; this.y1 = y1; this.x2 = x2; this.y2 = y2; }
        static double Bz(double u, double p1, double p2) { double v = 1 - u; return 3 * v * v * u * p1 + 3 * v * u * u * p2 + u * u * u; }
        static double D(double u, double p1, double p2) { double v = 1 - u; return 3 * v * v * p1 + 6 * v * u * (p2 - p1) + 3 * u * u * (1 - p2); }
        public double Ease(double t)
        {
            if (t <= 0) return 0; if (t >= 1) return 1;
            double u = t;
            for (int i = 0; i < 10; i++)
            {
                double x = Bz(u, x1, x2) - t, d = D(u, x1, x2);
                if (Math.Abs(x) < 1e-6 || Math.Abs(d) < 1e-6) break;
                u = Math.Max(0, Math.Min(1, u - x / d));
            }
            return Bz(u, y1, y2);
        }
    }

    // ---------------------------------------------------------------- the menu window
    public class StartWindow : Window
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] static extern bool LockWorkStation();
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);

        public Config Cfg;
        List<AppEntry> apps = new List<AppEntry>();
        DateTime lastScan;
        bool scanning;
        IntPtr hwnd;
        Grid content;
        TextBox search;
        TextBlock placeholder;
        ContentControl host;
        string view = "home", viewCat;
        List<AppEntry> viewList;
        List<Border> resultRows = new List<Border>();
        List<AppEntry> results = new List<AppEntry>();
        int sel;
        public bool IsOpen, SuppressHide, Exiting;
        DispatcherTimer watch;
        FrameworkElement homeView;
        ScrollViewer homeScroll;
        bool homeDirty = true;

        [StructLayout(LayoutKind.Sequential)] struct AccentPolicy { public int State, Flags, Color, AnimationId; }
        [StructLayout(LayoutKind.Sequential)] struct WCAData { public int Attribute; public IntPtr Data; public int Size; }
        [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr h, ref WCAData d);
        [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("gdi32.dll")] static extern int CombineRgn(IntPtr dst, IntPtr a, IntPtr b, int mode);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] static extern int NetUserGetInfo(string server, string user, int level, out IntPtr buf);
        [DllImport("netapi32.dll")] static extern int NetApiBufferFree(IntPtr b);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct USER_INFO_10 { public string name, comment, usr_comment, full_name; }

        const double W = 680, H = 740, Pad = 28, Radius = 15, Gap = 12;   // 15px corners, as in the TranslucentStartMenu theme

        // Opening/closing works like the real Start menu: the window sits just *behind* the taskbar in stacking order
        // and slides up out of it. A dedicated thread moves it once per screen refresh (synced with DwmFlush), so the
        // motion is paced by the display rather than by WPF's own timer.
        // slide: 0 = fully open, 1 = fully tucked behind the taskbar
        [DllImport("dwmapi.dll")] static extern int DwmFlush();
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
        [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int l, r, t, b; }
        [DllImport("user32.dll")] static extern IntPtr FindWindow(string cls, string title);
        readonly object animLock = new object();
        readonly AutoResetEvent animWake = new AutoResetEvent(false);
        double slideNow = 1, aFrom, aTo, aMs;
        long aStart;
        bool aActive;
        IEasingFunction aEase;
        Action aDone;
        int pxLeft, pxFinalTop, pxTravel, pxHidden;
        double finalTop, scale = 1;
        // Smooth ease-out for opening (no big first-frame jump), quick ease-in for closing
        static readonly IEasingFunction Decelerate = new Bezier(0.25, 1.0, 0.5, 1.0);
        static readonly IEasingFunction Accelerate = new Bezier(0.5, 0.0, 0.75, 0.0);
        const double OpenMs = 330, CloseMs = 180;

        static Brush B(string s) { var b = (Brush)new BrushConverter().ConvertFromString(s); b.Freeze(); return b; }
        static Color C(string s) { return (Color)ColorConverter.ConvertFromString(s); }
        static readonly Brush Fg = B("#FFFFFFFF"), Fg2 = B("#C5FFFFFF"), Fg3 = B("#8BFFFFFF"), Line = B("#15FFFFFF"), Accent = B("#FF4CC2FF"), Clear = Brushes.Transparent;
        // Frosted-glass fills from the TranslucentStartMenu theme (light, see-through tiles and buttons)
        static readonly Color Rest = C("#00FFFFFF"), Over = C("#1FFFFFFF"), Press = C("#14FFFFFF"),
            CardRest = C("#14FFFFFF"), CardOver = C("#26FFFFFF"), TileRest = C("#0DFFFFFF"), TileOver = C("#22FFFFFF");
        static readonly Brush Frost = B("#33FFFFFF"), FrostLine = B("#29FFFFFF");
        const string Tint = "#25323232";   // TranslucentStartMenu's CommonBgBrush tint
        static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        public StartWindow(Config cfg)
        {
            Cfg = cfg;
            Title = "Tessera";   // the Windhawk mod finds this window by its title and class
            // Not a "layered" window: WPF draws straight to the GPU instead of copying every frame through the CPU
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false; Topmost = true; Width = W; Height = H;
            Background = Brushes.Transparent; Foreground = Fg;
            UseLayoutRounding = true;   // keep icons and text on whole pixels (half-pixel positions look smeared)
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Styles.Xaml));
            BuildShell();
            SourceInitialized += (s, e) => InitNative();
            Deactivated += (s, e) => { if (!SuppressHide && IsOpen) { autoHidAt = DateTime.UtcNow; HideMenu(); } };
            Closing += (s, e) => { if (!Exiting) { e.Cancel = true; HideMenu(true); } };
            PreviewKeyDown += OnKey;
            // letters typed while the menu is still sliding in go straight into the search
            PreviewTextInput += (s, e) =>
            {
                if (search.IsKeyboardFocusWithin || !IsOpen || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
                search.Text += e.Text;
                e.Handled = true;
            };
            watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            watch.Tick += (s, e) =>
            {   // safety net: hide if another app took focus without us getting a Deactivated event
                if (!IsOpen || SuppressHide) return;
                uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
                if (pid != (uint)Process.GetCurrentProcess().Id) { autoHidAt = DateTime.UtcNow; HideMenu(); }
            };
            Scan(false);

            // Warm-up: draw the menu once off-screen so the first real open is instant
            ShowActivated = false; Left = -20000; Top = -20000;
            BuildHome(); host.Content = homeView;
            Show(); UpdateLayout();
            ShowActivated = true;
            // It stays "shown" but parked just off the bottom of the screen, so opening it is only the slide
            // (no window has to be created or re-shown, which is what used to cost a noticeable moment)
            Geometry(); Park();

            var at = new Thread(AnimLoop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "slide" };
            at.Start();
        }

        void InitNative()
        {
            hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x80);   // tool window: not in Alt+Tab
            int v = 1; DwmSetWindowAttribute(hwnd, 33, ref v, 4);         // we round the corners ourselves (15px)
            // let WPF's transparent pixels show through to the blur (GPU composited, no layered window)
            HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
            HwndSource.FromHwnd(hwnd).AddHook(MoveFilter);
            var m = new MARGINS { l = -1, r = -1, t = -1, b = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref m);
            // Plain blur behind the window (state 3), like the theme's WindhawkBlur. Not acrylic (state 4): Windows adds a
            // frosted fill and grain to acrylic that can't be turned off, which made the menu look nearly solid grey.
            // The tint is drawn by WPF on top (see BuildShell).
            var ap = new AccentPolicy { State = 3, Flags = 0, Color = 0 };
            int size = Marshal.SizeOf(ap);
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(ap, p, false);
                var d = new WCAData { Attribute = 19, Data = p, Size = size };
                SetWindowCompositionAttribute(hwnd, ref d);
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        // Where the menu sits when open: centred above the taskbar, like Windows 11
        void Geometry()
        {
            var wa = SystemParameters.WorkArea;
            double h = Math.Min(H, wa.Height - 2 * Gap), w = Math.Min(W, wa.Width - 2 * Gap);
            if (h != Height || w != Width) { Height = h; Width = w; }
            double left = wa.Left + (wa.Width - Width) / 2;   // applied by our own moves (setting WPF's Left would force extra work)
            finalTop = wa.Bottom - Height - Gap;
            var src = PresentationSource.FromVisual(this);
            if (src != null) scale = src.CompositionTarget.TransformToDevice.M11;
            pxLeft = (int)Math.Round(left * scale);
            pxFinalTop = (int)Math.Round(finalTop * scale);
            pxTravel = (int)Math.Round((wa.Bottom - finalTop) * scale);           // fully behind the taskbar
            pxHidden = (int)Math.Round(SystemParameters.PrimaryScreenHeight * scale) + 4;   // fully off-screen
            if (hwnd != IntPtr.Zero)
            {   // rounded corners (clips the blur too); only re-applied when the size changes, because applying it
                // forces a full redraw
                int pw = (int)Math.Round(Width * scale), ph = (int)Math.Round(Height * scale), r = (int)Math.Round(Radius * 2 * scale);
                if (pw != rgnW || ph != rgnH)
                {
                    rgnW = pw; rgnH = ph;
                    SetWindowRgn(hwnd, CreateRoundRectRgn(0, 0, pw + 1, ph + 1, r, r), true);
                }
            }
        }
        int rgnW, rgnH;

        // While sliding, WPF doesn't need to hear about each move; skipping its bookkeeping keeps every frame on time
        IntPtr MoveFilter(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == 0x0047 && aActive)   // WM_WINDOWPOSCHANGED
            {
                uint flags = (uint)Marshal.ReadInt32(l, IntPtr.Size * 2 + 16);
                if ((flags & SWP_NOSIZE) != 0) handled = true;
            }
            else if (msg == 0x0046 && aActive && trayHwnd != IntPtr.Zero)   // WM_WINDOWPOSCHANGING
            {   // getting focus mid-slide would lift it above the taskbar for a frame: keep it tucked underneath
                uint flags = (uint)Marshal.ReadInt32(l, IntPtr.Size * 2 + 16);
                if ((flags & SWP_NOZORDER) == 0) Marshal.WriteIntPtr(l, IntPtr.Size, trayHwnd);
            }
            return IntPtr.Zero;
        }
        IntPtr trayHwnd;

        const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;

        // Off the bottom of the screen: invisible no matter what is in front or behind
        void Park()
        {
            lock (animLock) { aActive = false; aDone = null; slideNow = 1; }
            parked = true;
            if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, IntPtr.Zero, pxLeft, pxHidden, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
        bool parked;

        void Animate(double to, double fullMs, IEasingFunction ease, Action done)
        {
            lock (animLock)
            {
                aFrom = slideNow; aTo = to; aEase = ease; aDone = done;
                aMs = Math.Max(60, fullMs * Math.Abs(to - slideNow));
                aStart = Stopwatch.GetTimestamp();
                aActive = true;
            }
            animWake.Set();
        }

        // Runs on its own thread: one window move per screen refresh, just behind the taskbar in stacking order
        void AnimLoop()
        {
            while (true)
            {
                animWake.WaitOne();
                bool first = true;
                while (true)
                {
                    if (!first) DwmFlush();   // wait for the next screen refresh
                    first = false;
                    double s; bool finished; Action done = null;
                    lock (animLock)
                    {
                        if (!aActive) break;
                        double t = Math.Min(1, (Stopwatch.GetTimestamp() - aStart) * 1000.0 / Stopwatch.Frequency / aMs);
                        slideNow = aFrom + (aTo - aFrom) * aEase.Ease(t);
                        s = slideNow;
                        finished = t >= 1;
                        if (finished) { aActive = false; done = aDone; aDone = null; }
                    }
                    IntPtr tray = FindWindow("Shell_TrayWnd", null);
                    trayHwnd = tray;
                    SetWindowPos(hwnd, tray != IntPtr.Zero ? tray : new IntPtr(-1), pxLeft, pxFinalTop + (int)Math.Round(s * pxTravel), 0, 0,
                        SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                    if (finished) { if (done != null) Dispatcher.BeginInvoke(done); break; }
                }
            }
        }

        // ------------------------------------------------ data
        void Scan(bool background)
        {
            if (scanning) return;
            scanning = true;
            var cache = new Dictionary<string, ImageSource>();
            foreach (var a in apps) if (a.Icon != null && !cache.ContainsKey(a.Id)) cache[a.Id] = a.Icon;
            var t = new Thread(() =>
            {
                List<AppEntry> list;
                try { list = Shell.Enumerate(cache); } catch { list = null; }
                Action done = () =>
                {
                    if (list != null && list.Count > 0) { var old = apps; apps = list; Shell.Free(old); homeDirty = true; }
                    lastScan = DateTime.Now; scanning = false;
                };
                if (background) Dispatcher.BeginInvoke(done); else done();
            });
            t.SetApartmentState(ApartmentState.STA); t.IsBackground = true; t.Start();
            if (!background) t.Join();
        }

        static bool Match(AppEntry a, string key)
        {
            return string.Equals(a.Id, key, StringComparison.OrdinalIgnoreCase) || string.Equals(a.Name, key, StringComparison.OrdinalIgnoreCase);
        }

        List<AppEntry> Resolve(Category c)
        {
            var r = new List<AppEntry>();
            if (c == null) return r;
            foreach (var k in c.Keys) foreach (var a in apps) if (Match(a, k) && !r.Contains(a)) r.Add(a);
            return r;
        }

        string KeyFor(AppEntry a) { return apps.Count(x => x.Name == a.Name) > 1 ? a.Id : a.Name; }

        bool IsSpecial(string n) { return n.Equals("Pinned", StringComparison.OrdinalIgnoreCase) || n.Equals("Hidden", StringComparison.OrdinalIgnoreCase); }

        List<KeyValuePair<string, List<AppEntry>>> Categories()
        {
            var res = new List<KeyValuePair<string, List<AppEntry>>>();
            var used = new HashSet<AppEntry>();
            foreach (var c in Cfg.Cats)
            {
                var l = Resolve(c);
                if (c.Name.Equals("Hidden", StringComparison.OrdinalIgnoreCase)) { foreach (var a in l) used.Add(a); continue; }
                if (IsSpecial(c.Name)) continue;
                foreach (var a in l) used.Add(a);
                if (l.Count > 0) res.Add(new KeyValuePair<string, List<AppEntry>>(c.Name, l));
            }
            var other = apps.Where(a => !used.Contains(a)).OrderBy(a => a.Name).ToList();
            if (other.Count > 0) res.Add(new KeyValuePair<string, List<AppEntry>>("Other", other));
            return res;
        }

        List<AppEntry> Hidden() { return Resolve(Cfg.Get("Hidden", false)); }

        string CategoryOf(AppEntry a)
        {
            foreach (var c in Cfg.Cats) if (!IsSpecial(c.Name) && c.Keys.Any(k => Match(a, k))) return c.Name;
            return "Other";
        }

        // ------------------------------------------------ show / hide
        // Clicking the taskbar Start button while the menu is open first takes focus away (which closes the menu), and only
        // then does explorer ask to open Start. Without this, that click would close the menu and immediately reopen it.
        DateTime autoHidAt = DateTime.MinValue;
        public void Toggle()
        {
            if (IsOpen) HideMenu(true);
            else if ((DateTime.UtcNow - autoHidAt).TotalMilliseconds > 400) ShowMenu();
        }

        public void ShowMenu()
        {
            if (IsOpen) return;
            IsOpen = true;
            var before = GetForegroundWindow();
            if (before != hwnd) prevFg = before;   // where the keyboard goes back to if the menu is closed without opening anything
            if (parked || !IsVisible)
            {
                if (Cfg.Changed()) { Cfg.Load(); homeDirty = true; }
                if (search.Text.Length > 0) search.Text = "";
                if (view != "home" || homeDirty) ShowHome();
                Geometry();
                if (!IsVisible) { Park(); Show(); }   // only if something hid it (e.g. Show desktop)
                parked = false;
            }
            // Start sliding straight away. The keyboard stays on the window itself (not the search box) until the
            // slide has finished: putting the cursor in a text box makes Windows set up typing support, which
            // briefly blocks the window and made the slide stutter. Anything typed meanwhile still goes into search.
            FocusManager.SetFocusedElement(this, this);
            Animate(0, OpenMs, Decelerate, () => { if (IsOpen) FocusSearch(); });
            // Switching focus away from the previous app takes ~40 ms, so do it on a side thread while the slide runs
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool ok = SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd;
                if (!ok) Dispatcher.BeginInvoke(new Action(() => { if (IsOpen) ForceForeground(); }));
            });
            watch.Start();
        }

        void FocusSearch()
        {
            search.Focus(); Keyboard.Focus(search);
            search.CaretIndex = search.Text.Length;
        }

        void ForceForeground()
        {
            if (hwnd == IntPtr.Zero) return;
            // the helper already allowed us to take focus, so the plain call normally works; the input-attach trick
            // (slower) is only the fallback
            if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd) return;
            uint pid;
            uint fg = GetWindowThreadProcessId(GetForegroundWindow(), out pid), me = GetCurrentThreadId();
            if (fg != me) { AttachThreadInput(me, fg, true); SetForegroundWindow(hwnd); BringWindowToTop(hwnd); AttachThreadInput(me, fg, false); }
        }

        public static void Log(string m) { try { File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log"), DateTime.Now.ToString("HH:mm:ss.fff ") + m + "\r\n"); } catch { } }

        IntPtr prevFg;

        // returnFocus: closed with nothing else to go to (Win key, Esc, Start button...): give the keyboard back to the
        // window that had it before the menu opened, like Windows' Start does. Otherwise it would stay on this (now
        // hidden) window and typing would go nowhere. Not used when an app is being opened, which takes focus itself.
        public void HideMenu(bool returnFocus = false)
        {
            if (!IsOpen) return;
            IsOpen = false; watch.Stop();
            if (returnFocus)
            {
                var p = prevFg;
                // on a side thread like the focus switch when opening, so it can't stall the slide
                if (p != IntPtr.Zero && IsWindow(p) && IsWindowVisible(p) && !IsIconic(p))
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        if (IsOpen || GetForegroundWindow() != hwnd || SetForegroundWindow(p)) return;
                        // refused: the closing click went to the taskbar, so Windows no longer lets this process move
                        // focus. A (tagged, so the hooks ignore it) Alt tap makes this the process with the last input.
                        Hooks.TapAlt();
                        SetForegroundWindow(p);
                    });
            }
            Animate(1, CloseMs, Accelerate, () =>
            {
                // reset to the home page while it's tucked away, then hide once that frame has been drawn
                if (search.Text.Length > 0) search.Text = "";
                if (view != "home") ShowHome();
                FocusManager.SetFocusedElement(this, this);   // so next time, getting focus doesn't wake the text box
                Dispatcher.BeginInvoke(new Action(() => { if (!IsOpen) Park(); }), DispatcherPriority.Background);
                if ((DateTime.Now - lastScan).TotalSeconds > 60) Scan(true);   // pick up newly installed apps for next time
                // rebuild the home page while idle (if anything changed) so the next open is instant
                Dispatcher.BeginInvoke(new Action(() => { if (!IsOpen && homeDirty) BuildHome(); }), DispatcherPriority.ApplicationIdle);
            });
        }

        void Run(AppEntry a, bool admin) { HideMenu(); Shell.Launch(a, admin); }

        // ------------------------------------------------ layout
        void BuildShell()
        {
            var root = new Grid();
            // the blur comes from Windows (see InitNative); this lays the TranslucentStartMenu tint (#25323232) over it.
            // No edge line, like the theme (BorderThickness=0).
            root.Children.Add(new Border { Background = B(Tint), CornerRadius = new CornerRadius(Radius) });
            content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(content);

            // search box
            // search box: dark pill like the Windows 11 Start search
            var sb = new Border { CornerRadius = new CornerRadius(10), Background = B("#26000000"), BorderBrush = B("#47FFFFFF"), BorderThickness = new Thickness(1),
                Height = 38, Margin = new Thickness(Pad + 4, Pad + 4, Pad + 4, 20) };
            var sg = new Grid();
            sg.Children.Add(new TextBlock { Text = "", FontFamily = IconFont, FontSize = 13, Foreground = Fg2, Margin = new Thickness(15, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            placeholder = new TextBlock { Text = "Search for apps", Foreground = Fg3, FontSize = 13.5, Margin = new Thickness(42, 0, 16, 1), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            search = new TextBox { Background = Clear, BorderThickness = new Thickness(0), Foreground = Fg, CaretBrush = Fg, FontSize = 13.5, SelectionBrush = Accent,
                Margin = new Thickness(40, 0, 16, 1), VerticalContentAlignment = VerticalAlignment.Center, FocusVisualStyle = null };
            search.TextChanged += (s, e) =>
            {
                placeholder.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (search.Text.Trim().Length > 0) ShowSearch(search.Text.Trim()); else if (view == "search") ShowHome();
            };
            sg.Children.Add(placeholder); sg.Children.Add(search);
            sb.Child = sg;
            content.Children.Add(sb);

            host = new ContentControl();
            Grid.SetRow(host, 1); content.Children.Add(host);

            // footer
            // footer: darker strip with your account on the left and power on the right, as in Windows 11
            // footer: same glass as the rest (no darker band, as in the theme); account button left, power right
            var ft = new Border { Height = 68, Padding = new Thickness(Pad + 4, 0, Pad + 4, 0) };
            var fg = new Grid();
            var user = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 6, 14, 6),
                BorderBrush = FrostLine, BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Account settings" };
            var up = new StackPanel { Orientation = Orientation.Horizontal };
            string uname = DisplayName();
            var av = new Grid { Width = 32, Height = 32 };
            var pic = AccountPicture();
            if (pic != null) av.Children.Add(new System.Windows.Shapes.Ellipse { Fill = new ImageBrush(pic) { Stretch = Stretch.UniformToFill } });
            else
            {
                av.Children.Add(new System.Windows.Shapes.Ellipse { Fill = B("#FF3A3A3A") });
                av.Children.Add(new TextBlock { Text = "", FontFamily = IconFont, FontSize = 16, Foreground = Fg2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            }
            up.Children.Add(av);
            up.Children.Add(new TextBlock { Text = uname, FontSize = 13, Margin = new Thickness(12, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center });
            user.Child = up;
            Hover(user, TileRest, TileOver);
            user.MouseLeftButtonUp += (s, e) => { HideMenu(); Process.Start("ms-settings:yourinfo"); };
            fg.Children.Add(user);
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            btns.Children.Add(IconButton("⊞", "Windows' own Start menu", false, () => { HideMenu(); Hooks.OpenWindowsStart(); }));
            btns.Children.Add(IconButton("", "File Explorer", true, () => { HideMenu(); Process.Start("explorer.exe"); }));
            btns.Children.Add(IconButton("", "Settings", true, () => { HideMenu(); Process.Start("ms-settings:"); }));
            Border power = null;
            power = IconButton("", "Power", true, () => PowerMenu(power));
            btns.Children.Add(power);
            fg.Children.Add(btns);
            ft.Child = fg;
            Grid.SetRow(ft, 2); content.Children.Add(ft);

            Content = root;
        }

        Border IconButton(string glyph, string tip, bool fluent, Action click)
        {
            var b = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(6), ToolTip = tip, Margin = new Thickness(3, 0, 3, 0),
                BorderBrush = FrostLine, BorderThickness = new Thickness(1) };
            var t = new TextBlock { Text = glyph, FontSize = fluent ? 16 : 19, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (fluent) t.FontFamily = IconFont;
            b.Child = t;
            Hover(b, TileRest, TileOver);
            b.MouseLeftButtonUp += (s, e) => click();
            return b;
        }

        // Hover / press highlight that fades in and out quickly, like Windows 11
        static void Hover(Border b, Color rest, Color over)
        {
            var br = new SolidColorBrush(rest);
            b.Background = br;
            Action<Color, double> go = (c, ms) => br.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(c, TimeSpan.FromMilliseconds(ms)));
            Color press = Color.FromArgb((byte)Math.Max(0, over.A - 6), over.R, over.G, over.B);
            b.MouseEnter += (s, e) => go(over, 85);
            b.MouseLeave += (s, e) => go(rest, 160);
            b.PreviewMouseLeftButtonDown += (s, e) => go(press, 50);
            b.PreviewMouseLeftButtonUp += (s, e) => go(over, 85);
        }

        // The faint grain that Windows' acrylic has
        static Brush noise;
        static Brush Noise()
        {
            if (noise != null) return noise;
            int n = 128; var px = new byte[n * n * 4]; var rnd = new Random(11);
            for (int i = 0; i < n * n; i++) { byte v = (byte)rnd.Next(256); px[i * 4] = v; px[i * 4 + 1] = v; px[i * 4 + 2] = v; px[i * 4 + 3] = 255; }
            var bmp = BitmapSource.Create(n, n, 96, 96, PixelFormats.Bgra32, null, px, n * 4); bmp.Freeze();
            var br = new ImageBrush(bmp) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, n, n), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None, Opacity = 0.022 };
            br.Freeze(); noise = br; return br;
        }

        static string DisplayName()
        {
            try
            {
                IntPtr buf;
                if (NetUserGetInfo(null, Environment.UserName, 10, out buf) == 0)
                {
                    var info = (USER_INFO_10)Marshal.PtrToStructure(buf, typeof(USER_INFO_10));
                    NetApiBufferFree(buf);
                    if (!string.IsNullOrWhiteSpace(info.full_name)) return info.full_name;
                }
            }
            catch { }
            return Environment.UserName;
        }

        static ImageSource AccountPicture()
        {
            try
            {
                string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\" + sid))
                {
                    if (k == null) return null;
                    foreach (var n in new[] { "Image96", "Image192", "Image208", "Image240", "Image448", "Image64", "Image48" })
                    {
                        var p = k.GetValue(n) as string;
                        if (p == null || !File.Exists(p)) continue;
                        var bi = new BitmapImage();
                        bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.UriSource = new Uri(p); bi.DecodePixelWidth = 96; bi.EndInit();
                        bi.Freeze(); return bi;
                    }
                }
            }
            catch { }
            return null;
        }

        // Slide a page in from the side (dir 1 = going deeper, -1 = going back), like Windows 11's "All apps" page
        void SetPage(FrameworkElement el, int dir)
        {
            host.Content = el;
            if (dir == 0) { el.BeginAnimation(OpacityProperty, null); el.Opacity = 1; el.RenderTransform = Transform.Identity; return; }
            var tt = new TranslateTransform(36 * dir, 0);
            el.RenderTransform = tt;
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(36 * dir, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = Decelerate });
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
        }

        ScrollViewer Scroll(UIElement inner)
        {
            var sv = new ScrollViewer { Content = inner, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 6, 0), Focusable = false };
            SmoothWheel(sv);
            return sv;
        }

        // Glide to the new scroll position instead of jumping (eases out over a few frames)
        static void SmoothWheel(ScrollViewer sv)
        {
            // Each wheel notch sets a target; the view glides there with an ease-out whose speed depends on real
            // elapsed time (not on how many frames got drawn), so it feels the same even if a frame is late.
            double target = 0, pos = 0, tau = 0.07; bool running = false;
            var clock = new Stopwatch(); double last = 0;
            EventHandler tick = null;
            tick = (s, e) =>
            {
                double now = clock.Elapsed.TotalSeconds, dt = Math.Min(0.05, now - last); last = now;
                double d = target - pos;
                if (Math.Abs(d) < 0.4) { pos = target; sv.ScrollToVerticalOffset(pos); CompositionTarget.Rendering -= tick; running = false; return; }
                pos += d * (1 - Math.Exp(-dt / tau));
                sv.ScrollToVerticalOffset(Math.Round(pos * 2) / 2);
            };
            sv.PreviewMouseWheel += (s, e) =>
            {
                e.Handled = true;
                if (!running) { pos = sv.VerticalOffset; target = pos; }
                bool touchpad = Math.Abs(e.Delta) < 120 || e.Delta % 120 != 0;   // precision touchpads send small steps
                tau = touchpad ? 0.035 : 0.075;
                target = Math.Max(0, Math.Min(sv.ScrollableHeight, target - e.Delta * (touchpad ? 1.0 : 0.85)));
                if (!running) { running = true; clock.Restart(); last = 0; CompositionTarget.Rendering += tick; }
            };
        }

        FrameworkElement Header(string text, string link, Action click)
        {
            var g = new Grid { Margin = new Thickness(12, 4, 8, 10) };
            g.Children.Add(new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") });
            if (link != null)
            {
                // the small "All apps  >" button, Windows 11 style
                var b = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 4, 8, 5), HorizontalAlignment = HorizontalAlignment.Right,
                    BorderBrush = FrostLine, BorderThickness = new Thickness(1) };
                var bp = new StackPanel { Orientation = Orientation.Horizontal };
                bp.Children.Add(new TextBlock { Text = link, FontSize = 12 });
                bp.Children.Add(new TextBlock { Text = "", FontFamily = IconFont, FontSize = 9, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                b.Child = bp;
                Hover(b, C("#26C0C0C0"), C("#40FFFFFF"));
                b.MouseLeftButtonUp += (s, e) => click();
                g.Children.Add(b);
            }
            return g;
        }

        Image Icon(AppEntry a, double size)
        {
            var i = new Image { Source = a.Icon, Width = size, Height = size };
            RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality);
            return i;
        }

        // App with its name underneath (pinned row, category pages)
        Border Tile(AppEntry a)
        {
            var b = new Border { Width = 98, Height = 84, Margin = new Thickness(2), CornerRadius = new CornerRadius(6), ToolTip = a.Name };
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var ic = Icon(a, 32); ic.Margin = new Thickness(0, 0, 0, 9);
            sp.Children.Add(ic);
            sp.Children.Add(new TextBlock { Text = a.Name, FontSize = 12, Foreground = Fg, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 6, 0) });
            b.Child = sp;
            Hover(b, TileRest, TileOver);
            b.MouseLeftButtonUp += (s, e) => Run(a, false);
            b.ContextMenu = AppMenu(a);
            return b;
        }

        // Category card: 3 big icons + a mini grid of more, name and count underneath
        Border Card(string name, List<AppEntry> list)
        {
            var card = new Border { Width = 196, Margin = new Thickness(4), Padding = new Thickness(10, 10, 10, 10), CornerRadius = new CornerRadius(8),
                BorderBrush = FrostLine, BorderThickness = new Thickness(1) };
            var sp = new StackPanel();
            var row = new UniformGrid { Rows = 1, Columns = 4, Height = 44 };
            int big = list.Count <= 4 ? Math.Min(4, list.Count) : 3;
            for (int i = 0; i < big; i++)
            {
                var a = list[i];
                var cell = new Border { CornerRadius = new CornerRadius(4), ToolTip = a.Name, Margin = new Thickness(1) };
                cell.Child = Icon(a, 32);
                Hover(cell, Rest, C("#2EFFFFFF"));
                cell.MouseLeftButtonUp += (s, e) => { e.Handled = true; Run(a, false); };
                cell.ContextMenu = AppMenu(a);
                row.Children.Add(cell);
            }
            if (list.Count > 4)
            {
                var mini = new UniformGrid { Rows = 2, Columns = 2, Width = 38, Height = 38, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                for (int i = 3; i < Math.Min(7, list.Count); i++) { var m = Icon(list[i], 15); m.Margin = new Thickness(2); mini.Children.Add(m); }
                var mb = new Border { CornerRadius = new CornerRadius(4), Background = B("#0DFFFFFF"), Margin = new Thickness(3), Child = mini, ToolTip = "Show all " + list.Count };
                row.Children.Add(mb);
            }
            sp.Children.Add(row);
            var lg = new Grid { Margin = new Thickness(4, 10, 4, 0) };
            lg.Children.Add(new TextBlock { Text = name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 28, 0) });
            lg.Children.Add(new TextBlock { Text = list.Count.ToString(), FontSize = 12, Foreground = Fg3, HorizontalAlignment = HorizontalAlignment.Right });
            sp.Children.Add(lg);
            card.Child = sp;
            Hover(card, CardRest, CardOver);
            card.MouseLeftButtonUp += (s, e) => ShowCategory(name, list);
            card.ContextMenu = CardMenu(name);
            return card;
        }

        void ShowHome() { ShowHome(0); }

        void ShowHome(int dir)
        {
            view = "home";
            if (homeView == null || homeDirty) BuildHome();
            if (host.Content != homeView || dir != 0) SetPage(homeView, dir);
            homeScroll.ScrollToVerticalOffset(0);
        }

        void BuildHome()
        {
            homeDirty = false;
            var sp = new StackPanel { Margin = new Thickness(Pad - 4, 0, Pad - 10, 12) };
            var pinned = Resolve(Cfg.Get("Pinned", false));
            if (pinned.Count > 0)
            {
                sp.Children.Add(Header("Pinned", null, null));
                var wp = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
                foreach (var a in pinned.Take(18)) wp.Children.Add(Tile(a));
                sp.Children.Add(wp);
            }
            sp.Children.Add(Header("All", "All apps", () => ShowCategory("All apps", apps.Except(Hidden()).OrderBy(a => a.Name).ToList())));
            var cw = new WrapPanel();
            foreach (var c in Categories()) cw.Children.Add(Card(c.Key, c.Value));
            sp.Children.Add(cw);
            homeScroll = Scroll(sp);
            homeView = homeScroll;
            if (view == "home") SetPage(homeView, 0);
        }

        void ShowCategory(string name, List<AppEntry> list)
        {
            bool fromHome = view == "home";
            view = "cat"; viewCat = name; viewList = list;
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var hd = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(Pad - 2, 0, Pad, 10) };
            var back = IconButton("", "Back (Esc)", true, () => ShowHome(-1));
            back.Width = 34; back.Height = 34;
            hd.Children.Add(back);
            hd.Children.Add(new TextBlock { Text = name, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 2), VerticalAlignment = VerticalAlignment.Center });
            hd.Children.Add(new TextBlock { Text = list.Count + " apps", FontSize = 12, Foreground = Fg3, Margin = new Thickness(12, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            g.Children.Add(hd);
            var wp = new WrapPanel { Margin = new Thickness(Pad - 4, 0, Pad - 10, 12) };
            foreach (var a in list) wp.Children.Add(Tile(a));
            var sv = Scroll(wp); Grid.SetRow(sv, 1); g.Children.Add(sv);
            SetPage(g, fromHome ? 1 : 0);
        }

        void ShowSearch(string q)
        {
            view = "search";
            var hidden = new HashSet<AppEntry>(Hidden());
            results = apps.Where(a => !hidden.Contains(a) && a.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(a => a.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : (a.Name.IndexOf(" " + q, StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 2))
                .ThenBy(a => a.Name).Take(40).ToList();
            var sp = new StackPanel { Margin = new Thickness(Pad - 4, 0, Pad - 10, 12) };
            sp.Children.Add(Header(results.Count == 0 ? "No apps match \"" + q + "\"" : "Best matches", null, null));
            resultRows.Clear();
            foreach (var a in results)
            {
                var r = new Border { Height = 46, CornerRadius = new CornerRadius(6), Background = Clear, Padding = new Thickness(12, 0, 14, 0) };
                var rg = new Grid();
                var ic = Icon(a, 26); ic.HorizontalAlignment = HorizontalAlignment.Left;
                rg.Children.Add(ic);
                rg.Children.Add(new TextBlock { Text = a.Name, FontSize = 13.5, Margin = new Thickness(42, 0, 120, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                rg.Children.Add(new TextBlock { Text = CategoryOf(a), FontSize = 12, Foreground = Fg3, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
                r.Child = rg;
                var idx = resultRows.Count;
                r.MouseEnter += (s, e) => Select(idx);
                r.MouseLeftButtonUp += (s, e) => Run(a, false);
                r.ContextMenu = AppMenu(a);
                resultRows.Add(r); sp.Children.Add(r);
            }
            SetPage(Scroll(sp), 0);
            Select(0);
        }

        void Select(int i)
        {
            if (resultRows.Count == 0) return;
            sel = Math.Max(0, Math.Min(resultRows.Count - 1, i));
            for (int k = 0; k < resultRows.Count; k++) resultRows[k].Background = k == sel ? B("#2BFFFFFF") : Clear;
            resultRows[sel].BringIntoView();
        }

        void OnKey(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (search.Text.Length > 0) search.Text = "";
                else if (view != "home") ShowHome(-1);
                else HideMenu(true);
                e.Handled = true;
            }
            else if (view == "search" && e.Key == Key.Down) { Select(sel + 1); e.Handled = true; }
            else if (view == "search" && e.Key == Key.Up) { Select(sel - 1); e.Handled = true; }
            else if (view == "search" && e.Key == Key.Enter && results.Count > 0)
            {
                Run(results[sel], (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift) && CanAdmin(results[sel]));
                e.Handled = true;
            }
            else if (!search.IsKeyboardFocusWithin && e.Key == Key.Back && search.Text.Length > 0)
            {   // typing before the search box has the cursor (during the slide)
                search.Text = search.Text.Substring(0, search.Text.Length - 1);
                e.Handled = true;
            }
        }

        // ------------------------------------------------ right-click menus
        static MenuItem MI(string text, Action click)
        {
            var m = new MenuItem { Header = text };
            if (click != null) m.Click += (s, e) => click();
            return m;
        }

        static bool CanAdmin(AppEntry a) { return !a.Id.Contains("!") && !a.Id.Contains("://"); }

        ContextMenu AppMenu(AppEntry a)
        {
            var cm = new ContextMenu();
            cm.Opened += (s, e) =>
            {
                cm.Items.Clear();
                var pin = Cfg.Get("Pinned", false);
                bool pinned = pin != null && pin.Keys.Any(k => Match(a, k));
                cm.Items.Add(MI("Open", () => Run(a, false)));
                if (CanAdmin(a)) cm.Items.Add(MI("Run as administrator", () => Run(a, true)));
                cm.Items.Add(new Separator());
                cm.Items.Add(MI(pinned ? "Unpin from top" : "Pin to top", () =>
                {
                    var p = Cfg.Get("Pinned", true);
                    if (pinned) p.Keys.RemoveAll(k => Match(a, k)); else p.Keys.Add(KeyFor(a));
                    SaveAndRefresh();
                }));
                var mv = MI("Move to category", null);
                foreach (var c in Cfg.Cats.Where(c => !IsSpecial(c.Name)))
                {
                    var name = c.Name;
                    mv.Items.Add(MI(name, () => MoveTo(a, name)));
                }
                mv.Items.Add(new Separator());
                mv.Items.Add(MI("New category…", () =>
                {
                    var n = Ask("New category", "Name for the new category:", "");
                    if (!string.IsNullOrWhiteSpace(n)) MoveTo(a, n.Trim());
                }));
                cm.Items.Add(mv);
                cm.Items.Add(MI("Hide from this menu", () => MoveTo(a, "Hidden")));
            };
            cm.Items.Add(new MenuItem());   // placeholder so the menu opens; filled in Opened
            return cm;
        }

        ContextMenu CardMenu(string name)
        {
            var cm = new ContextMenu();
            var c = Cfg.Get(name, false);
            if (c != null)
            {
                cm.Items.Add(MI("Move card earlier", () => MoveCard(name, -1)));
                cm.Items.Add(MI("Move card later", () => MoveCard(name, 1)));
                cm.Items.Add(MI("Rename…", () =>
                {
                    var n = Ask("Rename category", "New name for \"" + name + "\":", name);
                    if (!string.IsNullOrWhiteSpace(n) && Cfg.Get(n.Trim(), false) == null) { c.Name = n.Trim(); SaveAndRefresh(); }
                }));
                cm.Items.Add(new Separator());
            }
            cm.Items.Add(MI("Edit categories file", () => { HideMenu(); Process.Start("notepad.exe", "\"" + Cfg.Path + "\""); }));
            return cm;
        }

        void MoveTo(AppEntry a, string cat)
        {
            foreach (var c in Cfg.Cats) if (!c.Name.Equals("Pinned", StringComparison.OrdinalIgnoreCase)) c.Keys.RemoveAll(k => Match(a, k));
            if (!cat.Equals("Other", StringComparison.OrdinalIgnoreCase)) Cfg.Get(cat, true).Keys.Add(KeyFor(a));
            SaveAndRefresh();
        }

        void MoveCard(string name, int dir)
        {
            var cats = Cfg.Cats;
            int i = cats.FindIndex(x => x.Name == name);
            int j = i + dir;
            while (j >= 0 && j < cats.Count && IsSpecial(cats[j].Name)) j += dir;
            if (i < 0 || j < 0 || j >= cats.Count) return;
            var t = cats[i]; cats[i] = cats[j]; cats[j] = t;
            SaveAndRefresh();
        }

        void SaveAndRefresh()
        {
            Cfg.Save();
            homeDirty = true;
            if (view == "cat" && viewCat != null)
            {
                var hit = Categories().Where(x => x.Key == viewCat).ToList();
                if (hit.Count > 0) ShowCategory(viewCat, hit[0].Value); else ShowHome();
            }
            else if (view == "search") ShowSearch(search.Text.Trim());
            else ShowHome();
        }

        void PowerMenu(FrameworkElement anchor)
        {
            var cm = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Top };
            cm.Items.Add(MI("Lock", () => { HideMenu(); LockWorkStation(); }));
            cm.Items.Add(MI("Sign out", () => { HideMenu(); Process.Start("shutdown.exe", "/l"); }));
            cm.Items.Add(new Separator());
            cm.Items.Add(MI("Sleep", () => { HideMenu(); WF.Application.SetSuspendState(WF.PowerState.Suspend, false, false); }));
            cm.Items.Add(MI("Shut down", () => { HideMenu(); Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false }); }));
            cm.Items.Add(MI("Restart", () => { HideMenu(); Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false }); }));
            cm.IsOpen = true;
        }

        // Small dark text prompt (keeps the menu open while it's showing)
        string Ask(string title, string prompt, string initial)
        {
            SuppressHide = true;
            try
            {
                var d = new Window { Title = title, Width = 360, SizeToContent = SizeToContent.Height, WindowStyle = WindowStyle.ToolWindow, ResizeMode = ResizeMode.NoResize,
                    Topmost = true, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = B("#FF2B2B2B"), Foreground = Fg, ShowInTaskbar = false };
                var sp = new StackPanel { Margin = new Thickness(18) };
                sp.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) });
                var tb = new TextBox { Text = initial, Background = B("#FF1C1C1C"), Foreground = Fg, CaretBrush = Fg, BorderBrush = B("#33FFFFFF"), Padding = new Thickness(6, 4, 6, 4) };
                sp.Children.Add(tb);
                var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
                string result = null;
                ok.Click += (s, e) => { result = tb.Text; d.Close(); };
                sp.Children.Add(ok);
                d.Content = sp;
                d.Loaded += (s, e) => { tb.Focus(); tb.SelectAll(); };
                d.ShowDialog();
                Activate();
                return result;
            }
            finally { SuppressHide = false; }
        }
    }

    // ---------------------------------------------------------------- dark styles for menus, tooltips, scrollbars
    static class Styles
    {
        public const string Xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style TargetType='ToolTip'>
    <Setter Property='Background' Value='#FF2B2B2B'/><Setter Property='Foreground' Value='White'/>
    <Setter Property='BorderBrush' Value='#33FFFFFF'/><Setter Property='Padding' Value='8,4'/><Setter Property='FontSize' Value='12'/>
  </Style>
  <Style TargetType='ContextMenu'>
    <Setter Property='Foreground' Value='White'/><Setter Property='FontSize' Value='13'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='ContextMenu'>
        <Border Background='#FF2C2C2C' BorderBrush='#26FFFFFF' BorderThickness='1' CornerRadius='8' Padding='4'>
          <StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Cycle' MinWidth='180'/>
        </Border>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>
  <Style TargetType='Separator'>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='Separator'><Border Height='1' Background='#1FFFFFFF' Margin='6,4'/></ControlTemplate>
    </Setter.Value></Setter>
  </Style>
  <Style TargetType='MenuItem'>
    <Setter Property='Foreground' Value='White'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='MenuItem'>
        <Border x:Name='Bd' Background='Transparent' CornerRadius='4' Padding='12,7,10,7' Margin='0,1'>
          <Grid>
            <Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
            <ContentPresenter ContentSource='Header' RecognizesAccessKey='False'/>
            <TextBlock x:Name='Arrow' Grid.Column='1' Text='&#xE76C;' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='10' Margin='18,0,0,0'
                       VerticalAlignment='Center' Foreground='#A6FFFFFF' Visibility='Collapsed'/>
            <Popup x:Name='PART_Popup' Placement='Right' HorizontalOffset='8' VerticalOffset='-5' AllowsTransparency='True' Focusable='False'
                   IsOpen='{Binding IsSubmenuOpen, RelativeSource={RelativeSource TemplatedParent}}'>
              <Border Background='#FF2C2C2C' BorderBrush='#26FFFFFF' BorderThickness='1' CornerRadius='8' Padding='4'>
                <StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Cycle' MinWidth='170'/>
              </Border>
            </Popup>
          </Grid>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Bd' Property='Background' Value='#1FFFFFFF'/></Trigger>
          <Trigger Property='HasItems' Value='True'><Setter TargetName='Arrow' Property='Visibility' Value='Visible'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>
  <Style TargetType='ScrollBar'>
    <Setter Property='Width' Value='6'/><Setter Property='MinWidth' Value='6'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='ScrollBar'>
        <Track x:Name='PART_Track' IsDirectionReversed='True'>
          <Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'>
            <Border CornerRadius='3' Background='#40FFFFFF'/>
          </ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
        </Track>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>
</ResourceDictionary>";
    }

    // ---------------------------------------------------------------- program: single instance, tray icon
    public static class Program
    {
        static WF.NotifyIcon tray;

        const string InstanceName = "Local\\Tessera.Instance", HelperName = "Local\\Tessera.Helper",
            ShowName = "Local\\Tessera.Show", ToggleName = "Local\\Tessera.Toggle",
            // shared with the Windhawk mod (custom-start-takeover.wh.cpp)
            ModName = "Local\\Tessera.Mod", EnabledName = "Local\\Tessera.Enabled";
        static string Dir { get { return AppDomain.CurrentDomain.BaseDirectory; } }
        static string DisabledFlag { get { return Path.Combine(Dir, "disabled.flag"); } }

        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        static bool MutexExists(string name)
        {
            try { Mutex.OpenExisting(name).Dispose(); return true; }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch (UnauthorizedAccessException) { return true; }   // exists, owned by the admin helper
        }

        // "--hook" mode: a tiny admin-level helper that only watches the Windows key and Start button, so they
        // keep working even while an admin window (Task Manager, installers...) is in front. It shows nothing itself:
        // it just tells the normal menu (which runs without admin, so apps you open don't run as admin) to open.
        static void RunHelper()
        {
            bool first;
            var mutex = new Mutex(true, HelperName, out first);
            if (!first) return;
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
            EventWaitHandle toggle = null;
            Hooks.Toggle = () =>
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    AllowSetForegroundWindow(-1);   // let the menu take focus even over an admin window
                    try
                    {
                        if (toggle == null) toggle = EventWaitHandle.OpenExisting(ToggleName);
                        toggle.Set();
                    }
                    catch { toggle = null; }
                });
            };
            Hooks.Enabled = false;
            Hooks.Start();
            var stamp = ExeStamp();
            while (true)
            {
                Hooks.ModActive = MutexExists(ModName);   // the Windhawk mod handles Start button clicks itself
                // only take over the Windows key while the menu itself is running and switched on
                Hooks.Enabled = MutexExists(InstanceName) && !File.Exists(DisabledFlag);
                if (Updated(ref stamp))
                {   // a new build was dropped in: restart into it (keeps admin level, no prompt needed)
                    Hooks.Stop(); mutex.ReleaseMutex(); mutex.Dispose();
                    Process.Start(Exe, "--hook");
                    return;
                }
                Thread.Sleep(1500);
            }
        }

        static string Exe { get { return Path.Combine(Dir, "Tessera.exe"); } }
        static DateTime ExeStamp() { try { return File.GetLastWriteTimeUtc(Exe); } catch { return DateTime.MinValue; } }
        static DateTime pendingStamp;
        // True once Tessera.exe has been replaced by a newer build and has stopped changing
        static bool Updated(ref DateTime stamp)
        {
            var now = ExeStamp();
            if (now == stamp || now == DateTime.MinValue) { pendingStamp = DateTime.MinValue; return false; }
            if (now != pendingStamp) { pendingStamp = now; return false; }   // wait one more round so the file is complete
            return true;
        }

        // Compile all of this program's code up front (in the background at startup), so even the very first
        // open doesn't pause while .NET compiles it on demand
        static void PreCompile()
        {
            var flags = System.Reflection.BindingFlags.DeclaredOnly | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            foreach (var t in typeof(Program).Assembly.GetTypes())
            {
                if (t.ContainsGenericParameters) continue;
                foreach (var m in t.GetMethods(flags).Cast<System.Reflection.MethodBase>().Concat(t.GetConstructors(flags)))
                {
                    try
                    {
                        if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null) continue;
                        System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(m.MethodHandle);
                    }
                    catch { }
                }
            }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            new Thread(PreCompile) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
            if (args.Contains("--hook")) { RunHelper(); return; }

            bool first;
            var mutex = new Mutex(true, InstanceName, out first);
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowName);
            if (!first) { if (!args.Contains("--background")) showEvent.Set(); return; }   // already running: just open it
            var toggleEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ToggleName);
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }

            // display scaling (e.g. 1.5 at 150%), needed to fetch icons at their exact on-screen pixel size
            SetProcessDPIAware();
            try { using (var g = SD.Graphics.FromHwnd(IntPtr.Zero)) Shell.Scale = g.DpiX / 96.0; } catch { }
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var win = new StartWindow(new Config(Path.Combine(Dir, "categories.txt")));
            new WindowInteropHelper(win).EnsureHandle();

            Hooks.Toggle = () => win.Dispatcher.BeginInvoke((Action)win.Toggle);
            Hooks.Enabled = !File.Exists(DisabledFlag);
            // for the Windhawk mod: "takeover is on" (the event only exists while this runs)
            var enabledEvent = new EventWaitHandle(Hooks.Enabled, EventResetMode.ManualReset, EnabledName);
            if (Hooks.Enabled) enabledEvent.Set(); else enabledEvent.Reset();

            var waiter = new Thread(() => { while (true) { showEvent.WaitOne(); win.Dispatcher.BeginInvoke((Action)win.ShowMenu); } });
            waiter.IsBackground = true; waiter.Start();
            var toggler = new Thread(() => { while (true) { toggleEvent.WaitOne(); win.Dispatcher.BeginInvoke((Action)win.Toggle); } });
            toggler.IsBackground = true; toggler.Start();

            // Use our own key/mouse watching only when the admin helper isn't running
            var helperWatch = new Thread(() =>
            {
                while (true)
                {
                    Hooks.ModActive = MutexExists(ModName);   // the Windhawk mod handles Start button clicks itself
                    bool helper = MutexExists(HelperName);
                    if (helper && Hooks.Running) Hooks.Stop();
                    else if (!helper && !Hooks.Running) Hooks.Start();
                    Thread.Sleep(2000);
                }
            });
            helperWatch.IsBackground = true; helperWatch.Start();

            tray = new WF.NotifyIcon { Text = "Tessera", Icon = AppIcon(), Visible = true };
            var menu = new WF.ContextMenuStrip();
            menu.Items.Add("Open Tessera", null, (s, e) => win.ShowMenu());
            menu.Items.Add("Edit categories", null, (s, e) => Process.Start("notepad.exe", "\"" + win.Cfg.Path + "\""));
            var takeover = new WF.ToolStripMenuItem("Open with Windows key and Start button") { Checked = Hooks.Enabled, CheckOnClick = true };
            takeover.CheckedChanged += (s, e) =>
            {
                Hooks.Enabled = takeover.Checked;
                if (takeover.Checked) enabledEvent.Set(); else enabledEvent.Reset();
                try { if (takeover.Checked) File.Delete(DisabledFlag); else File.WriteAllText(DisabledFlag, "off"); } catch { }
            };
            menu.Items.Add(takeover);
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add("About Tessera", null, (s, e) => ShowAbout());
            menu.Items.Add("Exit", null, (s, e) => { tray.Visible = false; Hooks.Stop(); win.Exiting = true; app.Shutdown(); });
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) win.ShowMenu(); };

            // restart into a new build automatically when Tessera.exe is replaced
            var stamp = ExeStamp();
            var upd = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            upd.Tick += (s, e) =>
            {
                if (win.IsOpen || !Updated(ref stamp)) return;
                upd.Stop(); tray.Visible = false; Hooks.Stop();
                mutex.ReleaseMutex(); mutex.Dispose();
                Process.Start(Exe, "--background");
                win.Exiting = true; app.Shutdown();
            };
            upd.Start();

            if (!args.Contains("--background")) win.ShowMenu();
            app.Run();
            GC.KeepAlive(mutex);
        }

        // Tessera.ico is embedded in the exe (build.cmd); take the size the tray uses at the current display scaling
        static SD.Icon AppIcon()
        {
            using (var s = typeof(Program).Assembly.GetManifestResourceStream("Tessera.ico"))
                return new SD.Icon(s, WF.SystemInformation.SmallIconSize);
        }

        static void ShowAbout()
        {
            var a = typeof(Program).Assembly;
            var desc = (AssemblyDescriptionAttribute)Attribute.GetCustomAttribute(a, typeof(AssemblyDescriptionAttribute));
            var ver = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(a, typeof(AssemblyInformationalVersionAttribute));
            WF.MessageBox.Show(
                "Tessera " + ver.InformationalVersion + "\n\n" + desc.Description + "\n\n" +
                "Opens with the Windows key, Ctrl+Esc and the taskbar Start button.\n" +
                "Categories: right-click the menu, or edit categories.txt.\n\n" + Dir,
                "About Tessera", WF.MessageBoxButtons.OK, WF.MessageBoxIcon.Information);
        }
    }
}
