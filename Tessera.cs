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
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]
[assembly: AssemblyInformationalVersion("1.1")]

namespace Tessera
{
    public class AppEntry
    {
        public string Name;
        public string Id;
        public IntPtr Pidl;
        public ImageSource Icon;
        public string Path;     // file-system path; non-null only for file results (never in the apps list)
        public string Detail;   // right-hand subtitle for file results
        public int Source;      // 0 app, 1 PATH, 2 App Paths, 3 search index, 4 typed path,
                                // 5 setting / Control Panel item, 6 action (calculator, power), 7 web search, 8 document or folder
        public string Keywords; // extra words a setting or action is also found by
        public Action Act;      // what an action does when run (Source 6 and 7)
        public bool Strict;     // only found by exact, prefix or word-start matches (power actions)
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
        // us shrinking a different size, which is what made them look soft. IconUnits follows the tile size setting.
        public static double Scale = 1;
        public static int IconUnits = 32;
        public static int IconPx { get { return (int)Math.Round(IconUnits * Scale); } }

        internal static ImageSource GetIcon(IShellItem item, int size)
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

        // Opens a file or folder with ShellExecuteEx, in the given working directory (null for none).
        internal static void LaunchFile(string file, string dir, bool admin)
        {
            var t = new Thread(() =>
            {
                var i = new SHELLEXECUTEINFO();
                i.cbSize = Marshal.SizeOf(typeof(SHELLEXECUTEINFO));
                i.fMask = 0x00000400;   // SEE_MASK_FLAG_NO_UI
                i.lpFile = file; i.lpDirectory = dir; i.nShow = 1;
                if (admin) i.lpVerb = "runas";
                ShellExecuteEx(ref i);
            });
            t.SetApartmentState(ApartmentState.STA); t.IsBackground = true; t.Start();
        }

        public static void Free(List<AppEntry> list) { foreach (var a in list) if (a.Pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(a.Pidl); }
    }

    // ---------------------------------------------------------------- file search: PATH, App Paths, Windows Search index
    static class FileSearch
    {
        static readonly string[] Exts = { ".exe", ".bat", ".cmd", ".com", ".msc" };

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        static readonly Dictionary<string, ImageSource> iconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        public static bool IsExt(string ext)
        {
            foreach (var e in Exts) if (string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // "build.bat" -> "build"; names whose extension isn't an executable one ("Node.js") are left alone
        public static string StripExt(string name)
        {
            string ext = System.IO.Path.GetExtension(name);
            return !string.IsNullOrEmpty(ext) && IsExt(ext) ? name.Substring(0, name.Length - ext.Length) : name;
        }

        static AppEntry Make(string path, string name, string detail, int source)
        {
            var e = new AppEntry();
            e.Path = path; e.Id = path; e.Name = name; e.Detail = detail; e.Source = source;
            return e;
        }

        static string ParentName(string path)
        {
            try { return System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)); } catch { return ""; }
        }

        // Programs on PATH and App Paths registry entries. Runs on the scan thread.
        public static List<AppEntry> ScanCommands()
        {
            var list = new List<AppEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // PATH: machine + user + this process's copy (Tessera runs for days, so the process copy can be stale)
            var dirs = new List<string>();
            var dirSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sources = new List<string>();
            try { sources.Add(Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine)); } catch { }
            try { sources.Add(Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User)); } catch { }
            try { sources.Add(Environment.GetEnvironmentVariable("PATH")); } catch { }
            foreach (var src in sources)
            {
                if (string.IsNullOrEmpty(src)) continue;
                foreach (var part in src.Split(';'))
                {
                    try
                    {
                        string d = part.Trim().Trim('"').Trim();
                        if (d.Length == 0) continue;
                        d = Environment.ExpandEnvironmentVariables(d);
                        if (d.Length == 0 || !Directory.Exists(d)) continue;
                        if (dirSeen.Add(d.TrimEnd('\\'))) dirs.Add(d);
                    }
                    catch { }
                }
            }
            var pathNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // first directory on PATH wins, like a command prompt
            foreach (var d in dirs)
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(d))
                    {
                        try
                        {
                            if (!IsExt(System.IO.Path.GetExtension(f))) continue;
                            string fn = System.IO.Path.GetFileName(f);
                            if (!pathNames.Add(fn) || !seen.Add(f)) continue;
                            list.Add(Make(f, fn, "Command", 1));
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // App Paths (what Win+R and the Start search use to find chrome.exe, winword.exe, ...)
            var hives = new[]
            {
                new KeyValuePair<Microsoft.Win32.RegistryHive, Microsoft.Win32.RegistryView>(Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Default),
                new KeyValuePair<Microsoft.Win32.RegistryHive, Microsoft.Win32.RegistryView>(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64),
                new KeyValuePair<Microsoft.Win32.RegistryHive, Microsoft.Win32.RegistryView>(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32)
            };
            foreach (var h in hives)
            {
                try
                {
                    using (var bk = Microsoft.Win32.RegistryKey.OpenBaseKey(h.Key, h.Value))
                    using (var ap = bk.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"))
                    {
                        if (ap == null) continue;
                        foreach (var sub in ap.GetSubKeyNames())
                        {
                            try
                            {
                                using (var k = ap.OpenSubKey(sub))
                                {
                                    if (k == null) continue;
                                    string v = k.GetValue("") as string;
                                    if (string.IsNullOrEmpty(v)) continue;
                                    v = Environment.ExpandEnvironmentVariables(v.Trim().Trim('"').Trim());
                                    if (v.Length == 0 || !File.Exists(v) || !seen.Add(v)) continue;
                                    string name = System.IO.Path.GetExtension(sub).Length > 0 ? sub : System.IO.Path.GetFileName(v);
                                    list.Add(Make(v, name, ParentName(v), 2));
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            return list;
        }

        // Executable files in the Windows Search index whose file name contains q. Empty on any failure
        // (Windows Search service off, provider missing, timeout).
        public static List<AppEntry> QueryIndex(string q, int max)
        {
            var list = new List<AppEntry>();
            try
            {
                var sb = new StringBuilder();
                foreach (char c in q)
                {
                    if (c == '\'') sb.Append("''");
                    else if (c == '[') sb.Append("[[]");
                    else if (c == '%') sb.Append("[%]");
                    else if (c == '_') sb.Append("[_]");
                    else sb.Append(c);
                }
                string sql = "SELECT TOP " + max + " System.ItemPathDisplay FROM SystemIndex WHERE SCOPE='file:' AND (System.FileExtension='.exe' OR System.FileExtension='.bat' OR System.FileExtension='.cmd' OR System.FileExtension='.com' OR System.FileExtension='.msc') AND System.FileName LIKE '%" + sb.ToString() + "%'";
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var conn = new System.Data.OleDb.OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';"))
                {
                    conn.Open();
                    using (var cmd = new System.Data.OleDb.OleDbCommand(sql, conn))
                    {
                        cmd.CommandTimeout = 3;
                        using (var rd = cmd.ExecuteReader())
                        {
                            while (rd.Read())
                            {
                                string p = rd.GetValue(0) as string;
                                if (string.IsNullOrEmpty(p) || !seen.Add(p)) continue;
                                try { if (!File.Exists(p)) continue; } catch { continue; }
                                list.Add(Make(p, System.IO.Path.GetFileName(p), ParentName(p), 3));
                            }
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        static readonly string[] JunkNames = { "setup", "install", "updater", "update", "crashpad", "crashreport", "crash_handler", "crashhandler",
            "errorreport", "bugreport", "helper", "elevat", "notification_helper", "redist", "vcredist", "dxsetup", "squirrel", "createdump",
            "dotnet-install", "_test", "test_", "startuptask", "webview", "shellext", "overlayui", "_monitor", "xboxutil",
            "askpass", "askyesno", "credential-", "native-host", "command-runner", "make-data-folder", "steamservice", "py-manager", "-shipping" };
        static readonly string[] JunkPaths = { "\\node_modules\\", "\\.git\\", "\\site-packages\\", "\\__pycache__\\", "\\$recycle.bin\\",
            "\\appdata\\local\\temp\\", "\\windows\\temp\\", "\\temp\\", "\\tmp\\", "\\winsxs\\", "\\windows\\installer\\",
            "\\package cache\\", "\\_commonredist\\", "\\redist\\", "\\redistributables\\", "\\directx\\", "\\vendor\\",
            "\\.cargo\\registry\\", "\\go\\pkg\\mod\\", "\\.nuget\\", "\\.vscode\\extensions\\", "\\resources\\app\\",
            "\\usr\\bin\\", "\\usr\\libexec\\", "\\mingw64\\", "\\mingw32\\", "\\git-core\\", "\\steam\\bin\\",
            "\\locales\\", "\\test\\", "\\tests\\", "\\examples\\", "\\samples\\" };

        // Installers, uninstallers, helper binaries and library-folder hits. Callers never ask about files the user has launched.
        public static bool IsJunk(AppEntry f)
        {
            try
            {
                string p = (f.Path ?? "").ToLowerInvariant();
                string n = System.IO.Path.GetFileNameWithoutExtension(p);
                if (n.StartsWith("unins") || n.StartsWith("uninst") || n.StartsWith("uninstall")) return true;
                foreach (var j in JunkNames) if (n.Contains(j)) return true;
                if (n.StartsWith("git-") && n != "git-bash" && n != "git-cmd" && n != "git-gui" && n != "git-lfs") return true;
                if (n == "vc_redist.x64" || n == "vc_redist.x86" || n == "dxwebsetup" || n.StartsWith("ndp")) return true;
                foreach (var j in JunkPaths) if (p.Contains(j)) return true;
                if (f.Source == 3 && p.Split('\\').Length - 2 > 6) return true;
            }
            catch { }
            return false;
        }

        public static ImageSource Cached(string path)
        {
            ImageSource ic;
            lock (iconCache) { return iconCache.TryGetValue(path, out ic) ? ic : null; }
        }

        // Must be called from an STA thread. Null when Windows has no icon for it.
        public static ImageSource Icon(string path)
        {
            var c = Cached(path);
            if (c != null) return c;
            IShellItem item = null;
            try
            {
                if (SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItem).GUID, out item) != 0 || item == null) return null;
                var ic = Shell.GetIcon(item, Shell.IconPx);
                if (ic != null) lock (iconCache) iconCache[path] = ic;
                return ic;
            }
            catch { return null; }
            finally { if (item != null) Marshal.ReleaseComObject(item); }
        }

        public static void Launch(AppEntry a, bool admin)
        {
            string dir = null;
            try { if (File.Exists(a.Path)) dir = System.IO.Path.GetDirectoryName(a.Path); } catch { }
            Shell.LaunchFile(a.Path, dir, admin);
        }

        // Settings pages have no file to take an icon from; they all use the Settings app's icon.
        public static string IconSource(string path)
        {
            if (path != null && path.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
                return Environment.ExpandEnvironmentVariables(@"%SystemRoot%\ImmersiveControlPanel\SystemSettings.exe");
            return path;
        }

        static readonly string[] DocJunkPaths = { "\\appdata\\", "\\node_modules\\", "\\.git\\", "\\site-packages\\", "\\__pycache__\\",
            "\\$recycle.bin\\", "\\windows\\", "\\program files\\", "\\program files (x86)\\", "\\programdata\\", "\\.vscode\\",
            "\\.cargo\\", "\\.nuget\\", "\\.gradle\\", "\\.m2\\", "\\bin\\", "\\obj\\", "\\.cache\\", "\\steamapps\\", "\\vendor\\" };

        // Documents and folders in the Windows Search index whose name contains every word of q, newest first.
        public static List<AppEntry> QueryDocs(string q, int max)
        {
            var list = new List<AppEntry>();
            try
            {
                var conds = new List<string>();
                foreach (var w in q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    conds.Add("System.FileName LIKE '%" + Like(w) + "%'");
                if (conds.Count == 0) return list;
                string sql = "SELECT TOP " + (max * 4) + " System.ItemPathDisplay, System.ItemType FROM SystemIndex WHERE SCOPE='file:' AND " +
                    string.Join(" AND ", conds) +
                    " AND System.FileExtension<>'.exe' AND System.FileExtension<>'.dll' AND System.FileExtension<>'.lnk' AND System.FileExtension<>'.sys'" +
                    " ORDER BY System.DateModified DESC";
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var conn = new System.Data.OleDb.OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';"))
                {
                    conn.Open();
                    using (var cmd = new System.Data.OleDb.OleDbCommand(sql, conn))
                    {
                        cmd.CommandTimeout = 3;
                        using (var rd = cmd.ExecuteReader())
                        {
                            while (rd.Read() && list.Count < max)
                            {
                                string p = rd.GetValue(0) as string;
                                if (string.IsNullOrEmpty(p) || !seen.Add(p)) continue;
                                string lp = p.ToLowerInvariant();
                                bool junk = false;
                                foreach (var j in DocJunkPaths) if (lp.Contains(j)) { junk = true; break; }
                                if (junk) continue;
                                bool dir;
                                try { dir = Directory.Exists(p); if (!dir && !File.Exists(p)) continue; } catch { continue; }
                                string parent = ParentName(p);
                                list.Add(Make(p, System.IO.Path.GetFileName(p), dir ? "Folder · " + parent : parent, 8));
                            }
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        static string Like(string q)
        {
            var sb = new StringBuilder();
            foreach (char c in q)
            {
                if (c == '\'') sb.Append("''");
                else if (c == '[') sb.Append("[[]");
                else if (c == '%') sb.Append("[%]");
                else if (c == '_') sb.Append("[_]");
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }

    // ---------------------------------------------------------------- search matching
    // Tiers, lower is better: 0 exact, 1 prefix, 2 a word starts with it, 3 initials ("vsc", "ps" for PowerShell),
    // 4 every typed word starts a word in any order, 5 contains, 6 letters in order ("chrm"), 7 a typo or two. -1 no match.
    static class Matcher
    {
        public const int Exact = 0, Prefix = 1, WordStart = 2, Initials = 3, AllWords = 4, Contains = 5, Fuzzy = 6, Typo = 7;

        // Words of a name: split at spaces and punctuation, at lower->Upper ("PowerShell") and letter<->digit changes.
        public static List<string> Words(string s)
        {
            var words = new List<string>();
            var cur = new StringBuilder();
            char prev = '\0';
            foreach (char c in s)
            {
                if (!char.IsLetterOrDigit(c)) { if (cur.Length > 0) { words.Add(cur.ToString()); cur.Clear(); } prev = c; continue; }
                bool split = cur.Length > 0 && ((char.IsUpper(c) && char.IsLower(prev)) || (char.IsDigit(c) != char.IsDigit(prev)));
                if (split) { words.Add(cur.ToString()); cur.Clear(); }
                cur.Append(c); prev = c;
            }
            if (cur.Length > 0) words.Add(cur.ToString());
            return words;
        }

        static string Squash(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        public static int Score(string name, string q)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(q)) return -1;
            var ic = StringComparison.OrdinalIgnoreCase;
            string bare = FileSearch.StripExt(name);
            if (name.Equals(q, ic) || bare.Equals(q, ic)) return Exact;
            string sn = Squash(bare), sq = Squash(q);
            if (sq.Length > 0 && sn == sq) return Exact;
            if (name.StartsWith(q, ic) || (sq.Length > 0 && sn.StartsWith(sq))) return Prefix;
            if (name.IndexOf(" " + q, ic) >= 0 || name.IndexOf("-" + q, ic) >= 0 || name.IndexOf("_" + q, ic) >= 0
                || name.IndexOf("." + q, ic) >= 0 || name.IndexOf("(" + q, ic) >= 0) return WordStart;
            var words = Words(bare);
            foreach (var w in words) if (w.StartsWith(q, ic)) return WordStart;
            if (sq.Length >= 2 && sq.Length == q.Length && words.Count >= 2)
            {
                var ini = new StringBuilder();
                foreach (var w in words) ini.Append(char.ToLowerInvariant(w[0]));
                if (ini.ToString().Contains(sq)) return Initials;
                if (InitialsPlus(words, sq)) return Initials;
            }
            var qw = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (qw.Length >= 2)
            {
                bool all = true;
                foreach (var t in qw)
                {
                    bool hit = false;
                    foreach (var w in words) if (w.StartsWith(t, ic)) { hit = true; break; }
                    if (!hit) { all = false; break; }
                }
                if (all) return AllWords;
            }
            if (name.IndexOf(q, ic) >= 0 || (sq.Length >= 3 && sn.Contains(sq))) return Contains;
            if (sq.Length >= 3 && InOrder(sn, sq, words)) return Fuzzy;
            if (sq.Length >= 4 && Typos(sn, words, sq)) return Typo;
            return -1;
        }

        // "vscode": first letters of some words, then the rest of the last word ("vsco" -> Visual Studio COde)
        static bool InitialsPlus(List<string> words, string sq)
        {
            for (int split = 1; split < sq.Length; split++)
            {
                string ini = sq.Substring(0, split), rest = sq.Substring(split);
                for (int start = 0; start + split <= words.Count; start++)
                {
                    bool ok = true;
                    for (int k = 0; k < split - 1 && ok; k++) ok = char.ToLowerInvariant(words[start + k][0]) == ini[k];
                    if (!ok) continue;
                    string last = words[start + split - 1].ToLowerInvariant();
                    if (last[0] == ini[split - 1] && last.StartsWith(ini[split - 1] + rest)) return true;
                }
            }
            return false;
        }

        // every typed letter appears in order, the first one at a word start, without huge gaps
        static bool InOrder(string sn, string sq, List<string> words)
        {
            bool startOk = false;
            foreach (var w in words) if (char.ToLowerInvariant(w[0]) == sq[0]) { startOk = true; break; }
            if (!startOk) return false;
            int best = int.MaxValue;
            for (int s = sn.IndexOf(sq[0]); s >= 0; s = sn.IndexOf(sq[0], s + 1))
            {
                int i = s, j = 0;
                while (i < sn.Length && j < sq.Length) { if (sn[i] == sq[j]) j++; i++; }
                if (j == sq.Length) best = Math.Min(best, i - s);
            }
            return best != int.MaxValue && best <= sq.Length * 2 + 2;
        }

        // a slip of one letter (two for long words): against the start of the name or of any word
        static bool Typos(string sn, List<string> words, string sq)
        {
            int allowed = sq.Length >= 8 ? 2 : 1;
            var targets = new List<string> { sn };
            foreach (var w in words) if (w.Length >= 3) targets.Add(w.ToLowerInvariant());
            foreach (var t in targets)
            {
                if (Math.Abs(t.Length - sq.Length) <= allowed && Distance(t, sq, allowed) <= allowed) return true;   // whole word
                if (t.Length > sq.Length)
                {
                    for (int len = sq.Length - allowed; len <= sq.Length + allowed; len++)                          // its start
                        if (len >= 3 && len <= t.Length && Distance(t.Substring(0, len), sq, allowed) <= allowed) return true;
                }
            }
            return false;
        }

        // Damerau-Levenshtein (adjacent swaps count as one), giving up past max
        public static int Distance(string a, string b, int max)
        {
            if (Math.Abs(a.Length - b.Length) > max) return max + 1;
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                int rowMin = int.MaxValue;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    int v = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) v = Math.Min(v, d[i - 2, j - 2] + 1);
                    d[i, j] = v; rowMin = Math.Min(rowMin, v);
                }
                if (rowMin > max) return max + 1;
            }
            return d[a.Length, b.Length];
        }

        // Best tier over the name and the extra keywords (keywords count one tier worse than the name).
        public static int ScoreWithKeywords(AppEntry a, string q)
        {
            int s = a.Strict ? StrictScore(a.Name, q) : Score(a.Name, q);
            if (!string.IsNullOrEmpty(a.Keywords))
                foreach (var k in a.Keywords.Split(','))
                {
                    string kw = k.Trim();
                    if (kw.Length == 0) continue;
                    int ks = a.Strict ? StrictScore(kw, q) : Score(kw, q);
                    if (ks >= 0) { ks = Math.Min(Typo, ks + 1); if (s < 0 || ks < s) s = ks; }
                }
            return s;
        }

        static int StrictScore(string name, string q)
        {
            if (q.Length < 2) return -1;
            int s = Score(name, q);
            return s >= 0 && s <= WordStart ? s : -1;
        }
    }

    // ---------------------------------------------------------------- learned picks: which result you launched for what you typed
    static class Picks
    {
        class Pick { public string Query, Key; public int Count; public DateTime Last; }
        static readonly object gate = new object();
        static List<Pick> list;
        static string FilePath { get { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "picks.txt"); } }

        static void Load()
        {
            if (list != null) return;
            list = new List<Pick>();
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var p = line.Split('\t');
                    if (p.Length < 4) continue;
                    int n; long t;
                    if (!int.TryParse(p[2], out n) || !long.TryParse(p[3], out t)) continue;
                    list.Add(new Pick { Query = p[0], Key = p[1], Count = n, Last = new DateTime(t, DateTimeKind.Utc) });
                }
            }
            catch { }
        }

        public static void Record(string query, string key)
        {
            if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(key)) return;
            query = query.Trim().ToLowerInvariant(); key = key.ToLowerInvariant();
            if (query.Length == 0 || query.IndexOf('\t') >= 0) return;
            lock (gate)
            {
                Load();
                Pick hit = null;
                foreach (var p in list) if (p.Query == query && p.Key == key) { hit = p; break; }
                if (hit == null) { hit = new Pick { Query = query, Key = key }; list.Add(hit); }
                hit.Count++; hit.Last = DateTime.UtcNow;
                if (list.Count > 2000) list = list.OrderByDescending(x => x.Last).Take(1500).ToList();
                try
                {
                    var sb = new StringBuilder();
                    foreach (var p in list) sb.Append(p.Query).Append('\t').Append(p.Key).Append('\t').Append(p.Count).Append('\t').Append(p.Last.Ticks).Append("\r\n");
                    File.WriteAllText(FilePath, sb.ToString());
                }
                catch { }
            }
        }

        // Bonus in tiers (0-3) for results launched before from this query or a related one ("chr" -> "chrome").
        public static Dictionary<string, double> Bonuses(string query)
        {
            var r = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            query = (query ?? "").Trim().ToLowerInvariant();
            if (query.Length == 0) return r;
            lock (gate)
            {
                Load();
                foreach (var p in list)
                {
                    double w = p.Query == query ? 1.0 : (p.Query.StartsWith(query) || query.StartsWith(p.Query)) ? 0.5 : 0;
                    if (w == 0) continue;
                    double age = (DateTime.UtcNow - p.Last).TotalDays;
                    double fade = age <= 30 ? 1 : age <= 120 ? 0.6 : 0.3;
                    double b = w * fade * Math.Min(3.0, 1.2 + Math.Log(p.Count, 2) * 0.6);
                    double cur;
                    r[p.Key] = Math.Min(3.0, (r.TryGetValue(p.Key, out cur) ? cur : 0) + b);
                }
            }
            return r;
        }
    }

    // ---------------------------------------------------------------- calculator for the search box
    static class Calc
    {
        // A result when q is arithmetic ("12*7", "2^10", "sqrt(2)", "15% of 80", "(3+4)/2"), else null.
        public static string TryEval(string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return null;
            string s = q.Trim().ToLowerInvariant();
            if (s.StartsWith("=")) s = s.Substring(1);
            bool digit = false, op = false;
            foreach (char c in s) { if (char.IsDigit(c)) digit = true; if ("+-*/^%x×÷(".IndexOf(c) >= 0) op = true; }
            if (!digit) return null;
            if (!op && !s.Contains("sqrt") && !s.Contains("pi")) return null;
            s = s.Replace(" of ", "*").Replace("×", "*").Replace("÷", "/").Replace(",", "");
            try
            {
                var p = new Parser(s);
                double v = p.Expr();
                p.Skip();
                if (p.Pos != s.Length || double.IsNaN(v) || double.IsInfinity(v)) return null;
                double r = Math.Round(v, 10);
                if (Math.Abs(r) >= 1e15 || (r != 0 && Math.Abs(r) < 1e-6)) return v.ToString("G10", System.Globalization.CultureInfo.InvariantCulture);
                return r.ToString("#,0.##########", System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { return null; }
        }

        class Parser
        {
            readonly string s; public int Pos;
            public Parser(string text) { s = text; }
            public void Skip() { while (Pos < s.Length && s[Pos] == ' ') Pos++; }
            bool Eat(char c) { Skip(); if (Pos < s.Length && s[Pos] == c) { Pos++; return true; } return false; }

            public double Expr()
            {
                double v = Term();
                while (true) { if (Eat('+')) v += Term(); else if (Eat('-')) v -= Term(); else return v; }
            }
            double Term()
            {
                double v = Power();
                while (true)
                {
                    if (Eat('*') || Eat('x')) v *= Power();
                    else if (Eat('/')) { double d = Power(); if (d == 0) throw new DivideByZeroException(); v /= d; }
                    else if (Eat('%'))
                    {
                        Skip();
                        if (Pos < s.Length && (char.IsDigit(s[Pos]) || s[Pos] == '(' || s[Pos] == '.')) v %= Power();   // 10 % 3
                        else v /= 100;                                                                                   // 15% (of)
                    }
                    else return v;
                }
            }
            double Power() { double b = Unary(); if (Eat('^')) return Math.Pow(b, Power()); return b; }
            double Unary() { if (Eat('-')) return -Unary(); if (Eat('+')) return Unary(); return Atom(); }
            double Atom()
            {
                Skip();
                if (Eat('(')) { double v = Expr(); if (!Eat(')')) throw new FormatException(); return v; }
                if (Pos < s.Length && char.IsLetter(s[Pos]))
                {
                    int st = Pos; while (Pos < s.Length && char.IsLetter(s[Pos])) Pos++;
                    string f = s.Substring(st, Pos - st);
                    if (f == "pi") return Math.PI;
                    if (f == "e") return Math.E;
                    if (!Eat('(')) throw new FormatException();
                    double a = Expr(); if (!Eat(')')) throw new FormatException();
                    switch (f)
                    {
                        case "sqrt": return Math.Sqrt(a);
                        case "abs": return Math.Abs(a);
                        case "sin": return Math.Sin(a * Math.PI / 180);
                        case "cos": return Math.Cos(a * Math.PI / 180);
                        case "tan": return Math.Tan(a * Math.PI / 180);
                        case "log": return Math.Log10(a);
                        case "ln": return Math.Log(a);
                        case "round": return Math.Round(a);
                        case "floor": return Math.Floor(a);
                        case "ceil": return Math.Ceiling(a);
                    }
                    throw new FormatException();
                }
                int start = Pos;
                while (Pos < s.Length && (char.IsDigit(s[Pos]) || s[Pos] == '.')) Pos++;
                if (Pos == start) throw new FormatException();
                return double.Parse(s.Substring(start, Pos - start), System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }

    // ---------------------------------------------------------------- settings pages, Control Panel items, power actions
    static class Extras
    {
        // name | keywords | ms-settings page
        static readonly string[] SettingPages = {
            "Display|screen,monitor,resolution,brightness,scale,night light,hdr|display",
            "Night light|blue light,warm,display|nightlight",
            "Sound|audio,volume,speakers,microphone,output,input|sound",
            "Volume mixer|app volume,audio,sound|apps-volume",
            "Notifications|alerts,do not disturb,focus|notifications",
            "Focus|focus assist,do not disturb|quiethours",
            "Power & battery|battery,power mode,sleep,screen timeout,energy saver|powersleep",
            "Battery saver|energy saver,power|batterysaver",
            "Storage|disk,space,cleanup,storage sense|storagesense",
            "Multitasking|snap,alt tab,virtual desktops|multitasking",
            "Clipboard|clipboard history,copy,paste|clipboard",
            "Remote Desktop|rdp,remote|remotedesktop",
            "About this PC|system info,specs,pc name,rename pc,windows version,about|about",
            "Activation|product key,license,windows activation|activation",
            "Troubleshoot|troubleshooter,fix problems|troubleshoot",
            "Recovery|reset this pc,reset,advanced startup|recovery",
            "Bluetooth & devices|bluetooth,devices,pair|bluetooth",
            "Printers & scanners|printer,scanner,print|printers",
            "Mouse|pointer,cursor speed,scroll|mousetouchpad",
            "Touchpad|trackpad,gestures|devices-touchpad",
            "Typing|autocorrect,keyboard,spell check|typing",
            "Pen & Windows Ink|pen,stylus|pen",
            "AutoPlay|autoplay,usb|autoplay",
            "USB|usb devices|usb",
            "Phone Link|phone,mobile|mobile-devices",
            "Network & internet|network,internet,ethernet,connection|network",
            "Wi-Fi|wifi,wireless,wlan|network-wifi",
            "Ethernet|wired,lan,cable|network-ethernet",
            "VPN|vpn|network-vpn",
            "Mobile hotspot|hotspot,tethering|network-mobilehotspot",
            "Airplane mode|flight mode|network-airplanemode",
            "Proxy|proxy server|network-proxy",
            "Advanced network settings|network adapter,reset network|network-advancedsettings",
            "Personalization|personalize,customize,appearance|personalization",
            "Background|wallpaper,desktop background|personalization-background",
            "Colors|accent color,dark mode,light mode,theme color,transparency|personalization-colors",
            "Themes|theme,desktop icons|themes",
            "Lock screen|lockscreen,screen saver|lockscreen",
            "Start|start menu|personalization-start",
            "Taskbar|taskbar,system tray|taskbar",
            "Fonts|font,typeface|fonts",
            "Installed apps|uninstall,remove program,apps,programs,add remove programs|appsfeatures",
            "Default apps|default browser,open with,file associations|defaultapps",
            "Startup apps|startup,autostart,launch at login|startupapps",
            "Optional features|features,add feature|optionalfeatures",
            "Offline maps|maps|maps",
            "Video playback|video,hdr video|videoplayback",
            "Accounts|account,microsoft account,profile|yourinfo",
            "Sign-in options|password,pin,windows hello,fingerprint,face|signinoptions",
            "Email & accounts|email,mail accounts|emailandaccounts",
            "Family|family,parental|family-group",
            "Other users|add user,user accounts,guest|otherusers",
            "Windows Backup|backup,onedrive backup|backup",
            "Date & time|clock,time zone,date,time|dateandtime",
            "Language & region|language,region,locale,keyboard layout,input language|regionlanguage",
            "Speech|voice,speech recognition|speech",
            "Gaming|game mode,xbox game bar|gaming-gamebar",
            "Game Mode|gaming|gaming-gamemode",
            "Captures|game dvr,screen recording,record|gaming-gamedvr",
            "Accessibility|ease of access,a11y|easeofaccess",
            "Text size|font size,bigger text|easeofaccess-display",
            "Mouse pointer and touch|cursor size,pointer color|easeofaccess-mousepointer",
            "Magnifier|zoom,magnify|easeofaccess-magnifier",
            "Color filters|colorblind,grayscale|easeofaccess-colorfilter",
            "Contrast themes|high contrast|easeofaccess-highcontrast",
            "Narrator|screen reader|easeofaccess-narrator",
            "Captions|subtitles,live captions|easeofaccess-closedcaptioning",
            "Keyboard accessibility|sticky keys,filter keys,on-screen keyboard|easeofaccess-keyboard",
            "Privacy & security|privacy,security,permissions|privacy",
            "Windows Security|antivirus,defender,firewall,virus|windowsdefender",
            "Find my device|lost,locate|findmydevice",
            "For developers|developer mode,sudo,dev|developers",
            "Location|gps,location services|privacy-location",
            "Camera permissions|camera,webcam|privacy-webcam",
            "Microphone permissions|microphone,mic|privacy-microphone",
            "Diagnostics & feedback|telemetry,diagnostic data|privacy-feedback",
            "Windows Update|update,updates,patch,upgrade|windowsupdate",
            "Update history|updates installed|windowsupdate-history",
            "Advanced update options|active hours,restart options|windowsupdate-options",
            "Windows Insider Program|insider,preview builds|windowsinsider",
            "Delivery Optimization|update bandwidth|delivery-optimization",
            "Graphics|gpu,graphics performance,hardware acceleration|display-advancedgraphics",
            "Presence sensing|presence|presence",
            "Nearby sharing|share,nearby|crossdevice",
            "Settings home|settings|home",
        };

        public static List<AppEntry> Settings()
        {
            var list = new List<AppEntry>();
            foreach (var line in SettingPages)
            {
                var p = line.Split('|');
                list.Add(new AppEntry { Name = p[0], Keywords = p[1], Path = "ms-settings:" + p[2], Id = "setting:" + p[2], Detail = "Settings", Source = 5 });
            }
            return list;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);
        [DllImport("shell32.dll")]
        static extern int SHGetIDListFromObject([MarshalAs(UnmanagedType.IUnknown)] object punk, out IntPtr ppidl);

        static readonly Guid BHID_EnumItems = new Guid("94f60519-2850-4924-aa5a-d15e84868039");

        // Every item of "All Control Panel Items" (launched by its shell item, like Explorer does). Runs on the scan thread (STA).
        public static List<AppEntry> ControlPanel(Dictionary<string, ImageSource> iconCache)
        {
            var list = new List<AppEntry>();
            IShellItem folder = null;
            try
            {
                if (SHCreateItemFromParsingName("shell:::{21EC2020-3AEA-1069-A2DD-08002B30309D}", IntPtr.Zero, typeof(IShellItem).GUID, out folder) != 0 || folder == null) return list;
                IntPtr pEnum;
                folder.BindToHandler(IntPtr.Zero, BHID_EnumItems, typeof(IEnumShellItems).GUID, out pEnum);
                var en = (IEnumShellItems)Marshal.GetObjectForIUnknown(pEnum);
                Marshal.Release(pEnum);
                IShellItem item; uint fetched;
                while (en.Next(1, out item, out fetched) == 0 && fetched == 1)
                {
                    try
                    {
                        IntPtr pn;
                        item.GetDisplayName(0, out pn);
                        string name = Marshal.PtrToStringUni(pn); Marshal.FreeCoTaskMem(pn);
                        item.GetDisplayName(0x80018001, out pn);
                        string id = Marshal.PtrToStringUni(pn); Marshal.FreeCoTaskMem(pn);
                        var e = new AppEntry { Name = name, Id = "cpl:" + id, Detail = "Control Panel", Source = 5 };
                        SHGetIDListFromObject(item, out e.Pidl);
                        ImageSource ic;
                        if (iconCache != null && iconCache.TryGetValue(e.Id, out ic)) e.Icon = ic; else e.Icon = Shell.GetIcon(item, Shell.IconPx);
                        list.Add(e);
                    }
                    catch { }
                    finally { Marshal.ReleaseComObject(item); }
                }
                Marshal.ReleaseComObject(en);
            }
            catch { }
            finally { if (folder != null) Marshal.ReleaseComObject(folder); }
            return list;
        }

        [DllImport("user32.dll")] static extern bool LockWorkStation();
        [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool force, bool wakeupEventsDisabled);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);

        static void Run(string exe, string args)
        {
            try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true }); } catch { }
        }

        // Power and session actions. Strict: only found by exact, prefix or word-start matches, so a typo can't restart the PC.
        public static List<AppEntry> Actions()
        {
            var l = new List<AppEntry>();
            Func<string, string, string, Action, AppEntry> A = (name, kw, id, act) =>
                new AppEntry { Name = name, Keywords = kw, Id = "action:" + id, Detail = "Action", Source = 6, Act = act, Strict = true };
            l.Add(A("Lock", "lock screen,lock pc", "lock", () => LockWorkStation()));
            l.Add(A("Sleep", "suspend,standby", "sleep", () => SetSuspendState(false, false, false)));
            l.Add(A("Hibernate", "hibernation", "hibernate", () => SetSuspendState(true, false, false)));
            l.Add(A("Restart", "reboot,restart pc", "restart", () => Run("shutdown.exe", "/r /t 0")));
            l.Add(A("Shut down", "shutdown,power off,turn off", "shutdown", () => Run("shutdown.exe", "/s /t 0")));
            l.Add(A("Sign out", "log out,logout,log off,logoff,sign off", "signout", () => Run("shutdown.exe", "/l")));
            l.Add(A("Empty Recycle Bin", "recycle bin,trash,empty trash", "emptybin", () => SHEmptyRecycleBin(IntPtr.Zero, null, 0)));   // Windows asks first
            return l;
        }
    }

    // ---------------------------------------------------------------- categories.txt
    // ---------------------------------------------------------------- usage: what the user actually launches
    class UsageInfo { public int Count; public DateTime Last; }

    static class Usage
    {
        const string UaRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";
        static readonly string[] UaGuids = { "{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}", "{F4E57C4B-2036-45F0-A9AB-443BCFE33D9F}" };
        static readonly object gate = new object();
        static Dictionary<string, UsageInfo> own;                 // Tessera's own launches (usage.txt)
        static Dictionary<string, UsageInfo> windows = NewMap();  // Windows' UserAssist history
        static readonly Dictionary<string, string> folderCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

        public static Dictionary<string, UsageInfo> NewMap() { return new Dictionary<string, UsageInfo>(StringComparer.OrdinalIgnoreCase); }

        static string FilePath { get { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "usage.txt"); } }

        static void Add(Dictionary<string, UsageInfo> d, string key, int count, DateTime last)
        {
            if (string.IsNullOrEmpty(key)) return;
            key = key.ToLowerInvariant();
            UsageInfo u;
            if (!d.TryGetValue(key, out u)) { u = new UsageInfo(); d[key] = u; }
            u.Count += count;
            if (last > u.Last) u.Last = last;
        }

        static string Rot13(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c >= 'a' && c <= 'z') sb.Append((char)('a' + (c - 'a' + 13) % 26));
                else if (c >= 'A' && c <= 'Z') sb.Append((char)('A' + (c - 'A' + 13) % 26));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // "{GUID}\\cmd.exe" -> "C:\\Windows\\System32\\cmd.exe"; null when there is no known-folder prefix or it can't be resolved
        public static string ResolveKnownFolder(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length < 40 || id[0] != '{' || id[37] != '}' || id[38] != '\\') return null;
            string g = id.Substring(0, 38);
            string dir;
            lock (folderCache)
            {
                if (!folderCache.TryGetValue(g, out dir))
                {
                    dir = null;
                    try
                    {
                        Guid guid;
                        if (Guid.TryParse(g, out guid))
                        {
                            IntPtr p;
                            if (SHGetKnownFolderPath(guid, 0, IntPtr.Zero, out p) == 0 && p != IntPtr.Zero)
                            {
                                try { dir = Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
                            }
                        }
                    }
                    catch { dir = null; }
                    folderCache[g] = dir;
                }
            }
            if (string.IsNullOrEmpty(dir)) return null;
            try { return System.IO.Path.Combine(dir, id.Substring(39)); } catch { return null; }
        }

        // Reads Windows' launch history. Runs on the scan thread.
        public static void Reload()
        {
            var d = NewMap();
            foreach (var g in UaGuids)
            {
                try
                {
                    using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(UaRoot + "\\" + g + "\\Count"))
                    {
                        if (k == null) continue;
                        foreach (var vn in k.GetValueNames())
                        {
                            try
                            {
                                string name = Rot13(vn);
                                if (name.StartsWith("UEME_", StringComparison.OrdinalIgnoreCase)) continue;
                                var data = k.GetValue(vn) as byte[];
                                if (data == null || data.Length < 68) continue;
                                int count = BitTools.Int32(data, 4);
                                long ft = BitTools.Int64(data, 60);
                                DateTime last = default(DateTime);
                                if (ft > 0) { try { last = DateTime.FromFileTimeUtc(ft); } catch { last = default(DateTime); } }
                                if (count <= 0 && last == default(DateTime)) continue;
                                Add(d, name, count, last);
                                string real = ResolveKnownFolder(name);
                                if (real != null) Add(d, real, count, last);
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            lock (gate) windows = d;
        }

        static void LoadOwn()
        {
            var d = NewMap();
            try
            {
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath))
                    {
                        var p = line.Split('\t');
                        if (p.Length < 3) continue;
                        int c; long t;
                        if (!int.TryParse(p[0], out c) || !long.TryParse(p[1], out t)) continue;
                        DateTime last;
                        try { last = new DateTime(t, DateTimeKind.Utc); } catch { last = default(DateTime); }
                        Add(d, string.Join("\t", p, 2, p.Length - 2), c, last);
                    }
            }
            catch { }
            own = d;
        }

        public static void Record(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (gate)
            {
                if (own == null) LoadOwn();
                Add(own, key, 1, DateTime.UtcNow);
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    lock (gate)
                    {
                        if (own == null) return;
                        var sb = new StringBuilder();
                        foreach (var kv in own) sb.Append(kv.Value.Count).Append('\t').Append(kv.Value.Last.Ticks).Append('\t').Append(kv.Key).Append("\r\n");
                        File.WriteAllText(FilePath, sb.ToString());
                    }
                }
                catch { }
            });
        }

        // Windows' history and Tessera's own launches, merged
        public static Dictionary<string, UsageInfo> Snapshot()
        {
            var d = NewMap();
            lock (gate)
            {
                if (own == null) LoadOwn();
                foreach (var kv in windows) Add(d, kv.Key, kv.Value.Count, kv.Value.Last);
                foreach (var kv in own) Add(d, kv.Key, kv.Value.Count, kv.Value.Last);
            }
            return d;
        }

        public static double Frecency(UsageInfo u)
        {
            if (u == null) return 0;
            double age = (DateTime.UtcNow - u.Last).TotalDays;
            double bonus = u.Last == default(DateTime) ? 0 : age <= 3 ? 0.8 : age <= 14 ? 0.5 : age <= 60 ? 0.2 : 0;
            return Math.Min(2.0, Math.Log(1 + u.Count, 2) * 0.35 + bonus);
        }
    }

    static class BitTools
    {
        public static int Int32(byte[] b, int o) { return BitConverter.ToInt32(b, o); }
        public static long Int64(byte[] b, int o) { return BitConverter.ToInt64(b, o); }
    }

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

    // ---------------------------------------------------------------- settings.ini (changed from the menu: right-click the footer > Settings)
    public class Settings
    {
        public string Path;
        public int TintAlpha = 0x25;          // how dark the #323232 tint over the blur is (0-255); 0x25 is the theme's own
        public string TileSize = "Medium";    // Small, Medium or Large
        public bool ShowTrayIcon;             // off by default: its options are in the footer's right-click menu
        public bool Shadow = true;            // soft drop shadow under the menu
        public string WebSearch = "https://www.google.com/search?q=%s";   // the search box's last row; %s is the query

        public static readonly string[] Sizes = { "Small", "Medium", "Large" };

        public Settings(string path) { Path = path; Load(); if (!File.Exists(path)) Save(); }

        // Icon size in WPF units for each tile size. Icons are fetched at exactly this many pixels x display scaling,
        // so they stay sharp at every size; tiles, cards and the menu's width scale with it.
        public int IconUnits { get { return TileSize == "Small" ? 28 : TileSize == "Large" ? 36 : 32; } }
        public double TileScale { get { return IconUnits / 32.0; } }

        public void Load()
        {
            try
            {
                if (!File.Exists(Path)) return;
                foreach (var raw in File.ReadAllLines(Path))
                {
                    var line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (line.StartsWith("#") || eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    int n;
                    if (k.Equals("TintAlpha", StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out n)) TintAlpha = Math.Max(0, Math.Min(255, n));
                    else if (k.Equals("TileSize", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var s in Sizes) if (s.Equals(v, StringComparison.OrdinalIgnoreCase)) TileSize = s;
                    }
                    else if (k.Equals("ShowTrayIcon", StringComparison.OrdinalIgnoreCase)) ShowTrayIcon = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
                    else if (k.Equals("Shadow", StringComparison.OrdinalIgnoreCase)) Shadow = !(v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase));
                    else if (k.Equals("WebSearch", StringComparison.OrdinalIgnoreCase) && v.Contains("%s") && (v.StartsWith("http://") || v.StartsWith("https://"))) WebSearch = v;
                }
            }
            catch { }
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(Path,
                    "# Tessera settings. Changed from the menu: right-click the footer > Settings.\r\n" +
                    "# TintAlpha: how dark the glass is, 0-255 (37 is the TranslucentStartMenu theme's tint)\r\n" +
                    "# TileSize: Small, Medium or Large\r\n" +
                    "# ShowTrayIcon: 1 shows Tessera's icon in the notification area\r\n" +
                    "# Shadow: 1 draws a soft shadow under the menu, 0 turns it off\r\n" +
                    "# WebSearch: the web search offered as the search box's last row; %s is what you typed\r\n" +
                    "TintAlpha=" + TintAlpha + "\r\nTileSize=" + TileSize + "\r\nShowTrayIcon=" + (ShowTrayIcon ? "1" : "0") +
                    "\r\nShadow=" + (Shadow ? "1" : "0") + "\r\nWebSearch=" + WebSearch + "\r\n");
            }
            catch { }
        }
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
        public static Action<bool> Toggle;   // true: from the keyboard (Win, Ctrl+Esc), false: a Start button click

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
                            if (!escHeld) { escHeld = true; if (Toggle != null) Toggle(true); }   // ignore key-repeat
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
                                if (Toggle != null) Toggle(true);
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
                            if (Toggle != null) Toggle(false);
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

    // ---------------------------------------------------------------- soft drop shadow
    // The menu window can't draw its own shadow: it isn't layered (for speed) and SetWindowRgn clips everything outside
    // its rounded corners. So the shadow is a separate click-through layered window kept just behind the menu. Its
    // picture is drawn once per menu size, and moving it costs DWM nothing. It has its own thread and message loop, so
    // moving it from the slide thread never waits on the (sometimes busy) UI thread.
    sealed class ShadowWindow
    {
        delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEX
        {
            public int cbSize; public uint style; public WndProc lpfnWndProc; public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm;
        }
        [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr w, l; public uint time; public int x, y; }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte op, flags, alpha, format; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX c);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateWindowEx(int ex, string cls, string title, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint a, uint b);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")]
        static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, IntPtr pptDst, ref SIZE size, IntPtr src, ref POINT ptSrc, int key, ref BLENDFUNCTION bf, int flags);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr sec, uint off);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string n);

        public IntPtr Hwnd;
        WndProc proc;   // kept referenced so it isn't garbage-collected
        readonly object gate = new object();
        byte[] pending; int pendW, pendH;
        bool shown;
        const uint WM_APP_IMAGE = 0x8001;

        public ShadowWindow()
        {
            var ready = new ManualResetEvent(false);
            var t = new Thread(() =>
            {
                proc = (h, m, w, l) => DefWindowProc(h, m, w, l);
                var wc = new WNDCLASSEX { cbSize = Marshal.SizeOf(typeof(WNDCLASSEX)), lpfnWndProc = proc, hInstance = GetModuleHandle(null), lpszClassName = "Tessera.Shadow" };
                RegisterClassEx(ref wc);
                // layered + transparent: per-pixel alpha, and clicks go straight through it. Topmost tool window that
                // is never activated: not in Alt+Tab, never takes focus. No title, so the Windhawk mod can't mistake it.
                Hwnd = CreateWindowEx(0x80000 | 0x20 | 0x80 | 0x8000000 | 0x8, "Tessera.Shadow", null, unchecked((int)0x80000000),
                    -30000, -30000, 1, 1, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
                ready.Set();
                MSG msg;
                while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.message == WM_APP_IMAGE) { Apply(); continue; }
                    TranslateMessage(ref msg); DispatchMessage(ref msg);
                }
            });
            t.IsBackground = true; t.Name = "shadow"; t.Start();
            ready.WaitOne(2000);
        }

        public void SetImage(byte[] px, int w, int h)
        {
            lock (gate) { pending = px; pendW = w; pendH = h; }
            if (Hwnd != IntPtr.Zero) PostMessage(Hwnd, WM_APP_IMAGE, IntPtr.Zero, IntPtr.Zero);
        }

        void Apply()
        {
            byte[] px; int w, h;
            lock (gate) { px = pending; w = pendW; h = pendH; pending = null; }
            if (px == null) return;
            var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), bits;
            IntPtr bmp = CreateDIBSection(screen, ref bi, 0, out bits, IntPtr.Zero, 0);
            if (bmp != IntPtr.Zero)
            {
                Marshal.Copy(px, 0, bits, px.Length);
                IntPtr old = SelectObject(mem, bmp);
                var size = new SIZE { cx = w, cy = h }; var src = new POINT();
                var bf = new BLENDFUNCTION { op = 0, flags = 0, alpha = 255, format = 1 };   // AC_SRC_OVER, per-pixel alpha
                UpdateLayeredWindow(Hwnd, screen, IntPtr.Zero, ref size, mem, ref src, 0, ref bf, 2);   // ULW_ALPHA, keeps its position
                SelectObject(mem, old); DeleteObject(bmp);
            }
            DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
            if (!shown) { shown = true; ShowWindow(Hwnd, 4); }   // SW_SHOWNOACTIVATE
        }

        // The shadow of a w x h pixel rounded rectangle (corner radius r), in a picture m pixels larger on every side.
        // Two layers, like Windows 11's own flyouts: a wide soft one and a tight one near the edge, both slightly lower
        // than the menu. Nothing is drawn under the menu itself, or its glass would blur the shadow in.
        public static byte[] Picture(int w, int h, double r, int m, double scale)
        {
            int pw = w + 2 * m, ph = h + 2 * m;
            var px = new byte[pw * ph * 4];
            double cx = m + w / 2.0, cy = m + h / 2.0, hx = w / 2.0, hy = h / 2.0;
            double s1 = 14 * scale, o1 = 8 * scale, a1 = 0.28;    // wide and soft
            double s2 = 2.5 * scale, o2 = 1.5 * scale, a2 = 0.14; // tight, just around the edge
            for (int y = 0; y < ph; y++)
                for (int x = 0; x < pw; x++)
                {
                    double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
                    double inside = RoundRect(dx, dy, hx, hy, r);
                    if (inside <= 0) continue;
                    double v1 = a1 * Cover(RoundRect(dx, dy - o1, hx, hy, r), s1);
                    double v2 = a2 * Cover(RoundRect(dx, dy - o2, hx, hy, r), s2);
                    double a = (1 - (1 - v1) * (1 - v2)) * Math.Min(1, inside);
                    px[(y * pw + x) * 4 + 3] = (byte)Math.Round(a * 255);   // black, so premultiplied colour stays 0
                }
            return px;
        }

        // signed distance from a point to a rounded rectangle centred on 0,0 (negative inside)
        static double RoundRect(double x, double y, double hx, double hy, double r)
        {
            double qx = Math.Abs(x) - hx + r, qy = Math.Abs(y) - hy + r;
            double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
            return Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - r;
        }

        // how much of a blurred edge's darkness reaches distance d outside it (1 well inside, 0.5 on the edge)
        static double Cover(double d, double sigma) { return 0.5 * Erfc(d / (sigma * 1.41421356)); }

        static double Erfc(double x)
        {   // Abramowitz & Stegun 7.1.26
            double z = Math.Abs(x), t = 1 / (1 + 0.3275911 * z);
            double y = t * (0.254829592 + t * (-0.284496736 + t * (1.421413741 + t * (-1.453152027 + t * 1.061405429)))) * Math.Exp(-z * z);
            return x >= 0 ? y : 2 - y;
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
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int l, t, r, b; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] static extern IntPtr BeginDeferWindowPos(int n);
        [DllImport("user32.dll")] static extern IntPtr DeferWindowPos(IntPtr d, IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool EndDeferWindowPos(IntPtr d);

        public Config Cfg;
        public Settings Set;
        // set by Program: the options that used to be on the tray icon's menu
        public Func<bool> GetTakeover;
        public Action<bool> SetTakeover, ShowTray;
        public Action ExitApp;
        ShadowWindow shadow;
        volatile int shadowM;   // how far the shadow picture reaches past the menu on each side, in pixels
        Border tintLayer;
        IntPtr curMon;                  // the monitor the menu opens on (zero until the first open: the primary one)
        volatile IntPtr slideTray;      // that monitor's taskbar, when it isn't the main one
        List<AppEntry> apps = new List<AppEntry>();
        DateTime lastScan;
        bool scanning, rescanFresh;
        IntPtr hwnd;
        Grid content;
        TextBox search;
        TextBlock placeholder;
        ContentControl host;
        string view = "home", viewCat;
        List<AppEntry> viewList;
        List<Border> resultRows = new List<Border>();
        List<AppEntry> results = new List<AppEntry>();
        Dictionary<string, UsageInfo> usage = Usage.NewMap();   // launch history, refreshed on every scan
        List<AppEntry> commands = new List<AppEntry>();      // programs on PATH + App Paths entries, refreshed on every scan
        List<AppEntry> indexHits = new List<AppEntry>();     // results of the last completed Windows Search index query...
        string indexQuery = "";                              // ...and the query they belong to
        List<AppEntry> docHits = new List<AppEntry>();       // documents and folders from the index for docQuery
        string docQuery = "";
        List<AppEntry> controlPanel = new List<AppEntry>();  // Control Panel items, refreshed on every scan
        readonly List<AppEntry> settingPages = Extras.Settings();
        readonly List<AppEntry> actions = Extras.Actions();
        volatile int searchGen;
        bool oleWarmed;
        HashSet<string> iconFailed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
        // TranslucentStartMenu's CommonBgBrush tint is #25323232; the alpha comes from the Tint strength setting
        static Brush TintBrush(int alpha) { var b = new SolidColorBrush(Color.FromArgb((byte)alpha, 0x32, 0x32, 0x32)); b.Freeze(); return b; }
        static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        public StartWindow(Config cfg, Settings set)
        {
            Cfg = cfg; Set = set;
            Title = "Tessera";   // the Windhawk mod finds this window by its title and class
            // Not a "layered" window: WPF draws straight to the GPU instead of copying every frame through the CPU
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false; Topmost = true; Width = W * Set.TileScale; Height = H;
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
            shadow = new ShadowWindow();
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

        // Where the menu sits when open: centred above the taskbar, like Windows 11. On the main monitor that's the
        // usual work area; on another monitor (see PickMonitor) it's centred above that monitor's taskbar instead.
        void Geometry()
        {
            var src = PresentationSource.FromVisual(this);
            if (src != null) scale = src.CompositionTarget.TransformToDevice.M11;
            var wa = SystemParameters.WorkArea;
            IntPtr tray = IntPtr.Zero;   // zero: slide out from behind the main taskbar (found afresh every frame)
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (curMon != IntPtr.Zero && GetMonitorInfo(curMon, ref mi) && (mi.dwFlags & 1) == 0)   // not MONITORINFOF_PRIMARY
            {
                wa = new Rect(mi.rcWork.l / scale, mi.rcWork.t / scale, (mi.rcWork.r - mi.rcWork.l) / scale, (mi.rcWork.b - mi.rcWork.t) / scale);
                tray = TaskbarOn(curMon);
            }
            slideTray = tray;
            double h = Math.Min(H, wa.Height - 2 * Gap), w = Math.Min(W * Set.TileScale, wa.Width - 2 * Gap);
            if (h != Height || w != Width) { Height = h; Width = w; }
            double left = wa.Left + (wa.Width - Width) / 2;   // applied by our own moves (setting WPF's Left would force extra work)
            finalTop = wa.Bottom - Height - Gap;
            pxLeft = (int)Math.Round(left * scale);
            pxFinalTop = (int)Math.Round(finalTop * scale);
            pxTravel = (int)Math.Round((wa.Bottom - finalTop) * scale);           // fully behind the taskbar
            // fully off-screen: below the lowest monitor (with one monitor that's just below the screen)
            pxHidden = (int)Math.Round((SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight) * scale) + 4;
            if (hwnd != IntPtr.Zero)
            {   // rounded corners (clips the blur too); only re-applied when the size changes, because applying it
                // forces a full redraw
                int pw = (int)Math.Round(Width * scale), ph = (int)Math.Round(Height * scale), r = (int)Math.Round(Radius * 2 * scale);
                if (pw != rgnW || ph != rgnH)
                {
                    rgnW = pw; rgnH = ph;
                    SetWindowRgn(hwnd, CreateRoundRectRgn(0, 0, pw + 1, ph + 1, r, r), true);
                    UpdateShadow(pw, ph);
                }
            }
        }
        int rgnW, rgnH;

        // The shadow's picture for a menu of pw x ph pixels, drawn on a side thread (it takes a few tens of ms)
        void UpdateShadow(int pw, int ph)
        {
            if (shadow == null) return;
            int m = (int)Math.Round(44 * scale);
            double rr = Radius * scale, sc = scale;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var px = ShadowWindow.Picture(pw, ph, rr, m, sc);
                shadowM = m;
                shadow.SetImage(px, pw + 2 * m, ph + 2 * m);
                Dispatcher.BeginInvoke(new Action(() =>
                {   // put it where the menu is now (the slide moves it anyway while sliding)
                    if (aActive) return;
                    RECT r; GetWindowRect(hwnd, out r);
                    MoveTo(IntPtr.Zero, r.l, r.t, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                }));
            });
        }

        static string ClassOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return "";
            var sb = new StringBuilder(64); GetClassName(h, sb, 64); return sb.ToString();
        }

        // The taskbar shown on a given monitor that isn't the main one (Windows calls those Shell_SecondaryTrayWnd)
        static IntPtr TaskbarOn(IntPtr mon)
        {
            IntPtr h = IntPtr.Zero;
            while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                if (MonitorFromWindow(h, 2) == mon) return h;
            return IntPtr.Zero;
        }

        // Which monitor to open on: for a Start button click, the monitor of the taskbar under the pointer; for the
        // keyboard, the monitor of the window you're working in. With one monitor this is always the main one.
        IntPtr PickMonitor(bool keyboard)
        {
            POINT pt; GetCursorPos(out pt);
            if (!keyboard)
            {   // taskbar rectangles, not WindowFromPoint: that one has to ask windows (and so can wait on them)
                IntPtr t = FindWindow("Shell_TrayWnd", null);
                if (t != IntPtr.Zero && Under(t, pt)) return MonitorFromPoint(pt, 2);
                t = IntPtr.Zero;
                while ((t = FindWindowEx(IntPtr.Zero, t, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                    if (Under(t, pt)) return MonitorFromPoint(pt, 2);
            }
            IntPtr fg = GetForegroundWindow();
            if (fg == hwnd) return curMon;
            string c = ClassOf(fg);
            if (fg == IntPtr.Zero || c == "Progman" || c == "WorkerW") return MonitorFromPoint(pt, 2);   // the desktop spans every monitor
            return MonitorFromWindow(fg, 2);   // MONITOR_DEFAULTTONEAREST
        }

        static bool Under(IntPtr h, POINT pt)
        {
            RECT r; return GetWindowRect(h, out r) && pt.x >= r.l && pt.x < r.r && pt.y >= r.t && pt.y < r.b;
        }

        // Moves the menu and its shadow together, in one step so the shadow never lags a frame behind. Measured
        // (tools/measure-slide.ps1 and an in-app frame log): no difference to the slide with or without the shadow.
        void MoveTo(IntPtr after, int x, int y, uint flags) { MoveTo(after, x, y, flags, y); }

        // shadowY: where the shadow goes (pxHidden parks it while the menu itself stays put)
        void MoveTo(IntPtr after, int x, int y, uint flags, int shadowY)
        {
            IntPtr sh = shadow != null && Set.Shadow ? shadow.Hwnd : IntPtr.Zero;
            int m = shadowM;
            if (sh == IntPtr.Zero) { SetWindowPos(hwnd, after, x, y, 0, 0, flags); return; }
            IntPtr d = BeginDeferWindowPos(2);
            if (d != IntPtr.Zero) d = DeferWindowPos(d, hwnd, after, x, y, 0, 0, flags);
            if (d != IntPtr.Zero) d = DeferWindowPos(d, sh, hwnd, x - m, shadowY - m, 0, 0, flags);   // just behind the menu
            if (d != IntPtr.Zero && EndDeferWindowPos(d)) return;
            SetWindowPos(hwnd, after, x, y, 0, 0, flags);
            SetWindowPos(sh, hwnd, x - m, shadowY - m, 0, 0, flags);
        }

        // Settings > Shadow
        void SetShadow(bool on)
        {
            Set.Shadow = on; Set.Save();
            if (shadow == null || shadow.Hwnd == IntPtr.Zero) return;
            if (!on) SetWindowPos(shadow.Hwnd, IntPtr.Zero, -30000, -30000, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            else if (!aActive) { RECT r; GetWindowRect(hwnd, out r); MoveTo(IntPtr.Zero, r.l, r.t, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE); }
        }

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
            if (hwnd != IntPtr.Zero) MoveTo(IntPtr.Zero, pxLeft, pxHidden, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
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
                    IntPtr st = slideTray;
                    IntPtr tray = st != IntPtr.Zero ? st : FindWindow("Shell_TrayWnd", null);
                    trayHwnd = tray;
                    int y = pxFinalTop + (int)Math.Round(s * pxTravel);
                    // fully closed: the menu is behind the taskbar, but the top of its shadow would still show above
                    // it until the menu is parked, so the shadow goes away in this same frame
                    MoveTo(tray != IntPtr.Zero ? tray : new IntPtr(-1), pxLeft, y, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER,
                        finished && s >= 1 ? pxHidden : y);
                    if (finished) { if (done != null) Dispatcher.BeginInvoke(done); break; }
                }
            }
        }

        // ------------------------------------------------ data
        void Scan(bool background) { Scan(background, false); }

        // fresh: fetch every icon again (after the tile size, and so the icon size, changed)
        void Scan(bool background, bool fresh)
        {
            if (scanning) { if (fresh) rescanFresh = true; return; }
            scanning = true;
            var cache = new Dictionary<string, ImageSource>();
            if (!fresh) foreach (var a in apps.Concat(controlPanel)) if (a.Icon != null && !cache.ContainsKey(a.Id)) cache[a.Id] = a.Icon;
            bool warm = !oleWarmed; oleWarmed = true;
            var t = new Thread(() =>
            {
                List<AppEntry> list;
                try { list = Shell.Enumerate(cache); } catch { list = null; }
                List<AppEntry> cmds;
                try { cmds = FileSearch.ScanCommands(); } catch { cmds = null; }
                List<AppEntry> cpl;
                try { cpl = Extras.ControlPanel(cache); } catch { cpl = null; }
                Dictionary<string, UsageInfo> usg;
                try { Usage.Reload(); usg = Usage.Snapshot(); } catch { usg = null; }
                if (warm) { try { FileSearch.QueryIndex("tessera-warmup", 1); } catch { } }   // loads OLE DB so the first real search isn't slow
                Action done = () =>
                {
                    if (list != null && list.Count > 0) { var old = apps; apps = list; Shell.Free(old); homeDirty = true; }
                    if (cmds != null) commands = cmds;
                    if (cpl != null && cpl.Count > 0) { var oldC = controlPanel; controlPanel = cpl; Shell.Free(oldC); }
                    if (usg != null) usage = usg;
                    lastScan = DateTime.Now; scanning = false;
                    if (rescanFresh) { rescanFresh = false; Scan(true, true); }
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
        public void Toggle() { Toggle(false); }   // the Windhawk mod's requests: Start button clicks

        public void Toggle(bool keyboard)
        {
            if (IsOpen) HideMenu(true);
            else if ((DateTime.UtcNow - autoHidAt).TotalMilliseconds > 400) ShowMenu(keyboard);
        }

        public void ShowMenu() { ShowMenu(true); }

        void ShowMenu(bool keyboard)
        {
            if (IsOpen) return;
            IsOpen = true;
            var before = GetForegroundWindow();
            if (before != hwnd) prevFg = before;   // where the keyboard goes back to if the menu is closed without opening anything
            if (parked || !IsVisible)
            {
                curMon = PickMonitor(keyboard);
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
            if (fg == me) return;
            // attaching and detaching the input queues sends this window a deactivate in passing; without
            // SuppressHide that closed the menu again straight away (while it kept the keyboard)
            bool was = SuppressHide; SuppressHide = true;
            try { AttachThreadInput(me, fg, true); SetForegroundWindow(hwnd); BringWindowToTop(hwnd); AttachThreadInput(me, fg, false); }
            finally { SuppressHide = was; }
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

        double UsageOf(AppEntry a)
        {
            double best = 0;
            try
            {
                var keys = new List<string>();
                if (a.Path != null) keys.Add(a.Path);
                else if (a.Id != null)
                {
                    keys.Add(a.Id);
                    string real = Usage.ResolveKnownFolder(a.Id);
                    if (real != null) keys.Add(real);
                }
                foreach (var k in keys)
                {
                    UsageInfo u;
                    if (usage.TryGetValue(k.ToLowerInvariant(), out u)) best = Math.Max(best, Usage.Frecency(u));
                }
            }
            catch { }
            return best;
        }

        void NoteLaunch(AppEntry a)
        {
            try
            {
                string key = a.Path != null ? a.Path.ToLowerInvariant() : (a.Id != null ? a.Id.ToLowerInvariant() : null);
                if (key == null) return;
                Usage.Record(key);
                UsageInfo u;
                if (!usage.TryGetValue(key, out u)) { u = new UsageInfo(); usage[key] = u; }
                u.Count++; u.Last = DateTime.UtcNow;
            }
            catch { }
        }

        void Run(AppEntry a, bool admin)
        {
            if (view == "search" && a.Source != 7 && a.Source != 4)
            {
                string q = search.Text.Trim(), key = a.Path ?? a.Id;
                if (a.Source != 6 || a.Strict) { var t = new Thread(() => Picks.Record(q, key)) { IsBackground = true }; t.Start(); }
            }
            if (a.Source != 7 && !(a.Source == 6 && !a.Strict)) NoteLaunch(a);
            HideMenu();
            if (a.Act != null) { var act = a.Act; Dispatcher.BeginInvoke(new Action(() => { try { act(); } catch { } }), DispatcherPriority.Background); }
            else if (a.Path != null) FileSearch.Launch(a, admin);
            else Shell.Launch(a, admin);
        }

        // ------------------------------------------------ layout
        void BuildShell()
        {
            var root = new Grid();
            // the blur comes from Windows (see InitNative); this lays the TranslucentStartMenu tint (#25323232) over it.
            // No edge line, like the theme (BorderThickness=0).
            // Its strength is the Tint strength setting (TintAlpha in settings.ini).
            tintLayer = new Border { Background = TintBrush(Set.TintAlpha), CornerRadius = new CornerRadius(Radius) };
            root.Children.Add(tintLayer);
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
            placeholder = new TextBlock { Text = "Search apps and files", Foreground = Fg3, FontSize = 13.5, Margin = new Thickness(42, 0, 16, 1), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
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
            // right-click anywhere on it: Settings, Edit categories, the takeover switch, About, Exit
            var ft = new Border { Height = 68, Padding = new Thickness(Pad + 4, 0, Pad + 4, 0), Background = Clear };
            ft.ContextMenu = FooterMenu();
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
            double f = Set.TileScale;
            var b = new Border { Width = Math.Round(98 * f), Height = Math.Round(84 * f), Margin = new Thickness(2), CornerRadius = new CornerRadius(6), ToolTip = a.Name };
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var ic = Icon(a, Set.IconUnits); ic.Margin = new Thickness(0, 0, 0, 9);
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
            double f = Set.TileScale;
            var card = new Border { Width = Math.Round(196 * f), Margin = new Thickness(4), Padding = new Thickness(10, 10, 10, 10), CornerRadius = new CornerRadius(8),
                BorderBrush = FrostLine, BorderThickness = new Thickness(1) };
            var sp = new StackPanel();
            var row = new UniformGrid { Rows = 1, Columns = 4, Height = Math.Round(44 * f) };
            int big = list.Count <= 4 ? Math.Min(4, list.Count) : 3;
            for (int i = 0; i < big; i++)
            {
                var a = list[i];
                var cell = new Border { CornerRadius = new CornerRadius(4), ToolTip = a.Name, Margin = new Thickness(1) };
                cell.Child = Icon(a, Set.IconUnits);
                Hover(cell, Rest, C("#2EFFFFFF"));
                cell.MouseLeftButtonUp += (s, e) => { e.Handled = true; Run(a, false); };
                cell.ContextMenu = AppMenu(a);
                row.Children.Add(cell);
            }
            if (list.Count > 4)
            {
                var mini = new UniformGrid { Rows = 2, Columns = 2, Width = Math.Round(38 * f), Height = Math.Round(38 * f), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                for (int i = 3; i < Math.Min(7, list.Count); i++) { var m = Icon(list[i], Math.Round(15 * f)); m.Margin = new Thickness(2); mini.Children.Add(m); }
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
            var wp = new WrapPanel { Margin = new Thickness(Pad - 4, 0, Pad - 10, 12) };
            foreach (var a in list) wp.Children.Add(Tile(a));
            SetPage(PageGrid(name, list.Count + " apps", wp), fromHome ? 1 : 0);
        }

        // A page with a back button and a title (category pages, Settings, About)
        Grid PageGrid(string title, string sub, UIElement body)
        {
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var hd = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(Pad - 2, 0, Pad, 10) };
            var back = IconButton("", "Back (Esc)", true, () => ShowHome(-1));
            back.Width = 34; back.Height = 34;
            hd.Children.Add(back);
            hd.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 2), VerticalAlignment = VerticalAlignment.Center });
            if (sub != null)
                hd.Children.Add(new TextBlock { Text = sub, FontSize = 12, Foreground = Fg3, Margin = new Thickness(12, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            g.Children.Add(hd);
            var sv = Scroll(body); Grid.SetRow(sv, 1); g.Children.Add(sv);
            return g;
        }

        // ------------------------------------------------ Settings and About pages (from the footer's right-click menu)
        public void ShowSettings()
        {
            bool fromHome = view == "home";
            view = "settings";
            var sp = new StackPanel { Margin = new Thickness(Pad - 2, 0, Pad, 12) };

            var val = new TextBlock { FontSize = 12, Foreground = Fg2, VerticalAlignment = VerticalAlignment.Center };
            var slider = new Slider { Minimum = 0, Maximum = 153, Value = Set.TintAlpha, IsMoveToPointEnabled = true, SmallChange = 3, LargeChange = 13,
                Focusable = false, Margin = new Thickness(0, 12, 0, 0) };
            Action showVal = () => { val.Text = Math.Round(slider.Value / 2.55) + "%"; };
            showVal();
            slider.ValueChanged += (s, e) =>
            {
                Set.TintAlpha = (int)Math.Round(slider.Value);
                tintLayer.Background = TintBrush(Set.TintAlpha);
                showVal(); SaveSoon();
            };
            sp.Children.Add(Group("Tint strength", "How dark the glass is. 15% matches the TranslucentStartMenu theme.", val, slider));

            var sizes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            foreach (var size in Settings.Sizes)
            {
                var name = size;
                sizes.Children.Add(Choice(name, Set.TileSize == name, () => { SetTileSize(name); ShowSettings(); }));
            }
            sp.Children.Add(Group("Tile size", "Size of the app tiles and category cards. The menu gets wider or narrower to match.", null, sizes));

            var shade = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            foreach (var on in new[] { false, true })
            {
                bool v = on;
                shade.Children.Add(Choice(v ? "On" : "Off", Set.Shadow == v, () => { SetShadow(v); ShowSettings(); }));
            }
            sp.Children.Add(Group("Shadow", "A soft shadow around the menu, like Windows' own Start.", null, shade));

            var tray = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            foreach (var on in new[] { false, true })
            {
                bool v = on;
                tray.Children.Add(Choice(v ? "Shown" : "Hidden", Set.ShowTrayIcon == v, () =>
                {
                    Set.ShowTrayIcon = v; Set.Save();
                    if (ShowTray != null) ShowTray(v);
                    ShowSettings();
                }));
            }
            sp.Children.Add(Group("Tray icon", "Tessera's icon in the notification area. Everything it offered is also on the footer's right-click menu.", null, tray));

            sp.Children.Add(new TextBlock { Text = "Saved in settings.ini next to Tessera.exe.", FontSize = 12, Foreground = Fg3, Margin = new Thickness(8, 2, 0, 0) });
            SetPage(PageGrid("Settings", null, sp), fromHome ? 1 : 0);
        }

        // A frosted settings card: title (and something on its right), a line of explanation, then the control
        Border Group(string title, string desc, FrameworkElement right, UIElement body)
        {
            var b = new Border { CornerRadius = new CornerRadius(8), BorderBrush = FrostLine, BorderThickness = new Thickness(1), Background = new SolidColorBrush(CardRest),
                Padding = new Thickness(16, 13, 16, 15), Margin = new Thickness(4, 0, 4, 10) };
            var sp = new StackPanel();
            var top = new Grid();
            top.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") });
            if (right != null) { right.HorizontalAlignment = HorizontalAlignment.Right; top.Children.Add(right); }
            sp.Children.Add(top);
            sp.Children.Add(new TextBlock { Text = desc, FontSize = 12, Foreground = Fg3, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            sp.Children.Add(body);
            b.Child = sp;
            return b;
        }

        // One option in a row of choices; the chosen one has an accent outline
        Border Choice(string text, bool chosen, Action click)
        {
            var b = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(16, 6, 16, 7), Margin = new Thickness(0, 0, 6, 0),
                BorderThickness = new Thickness(1), BorderBrush = chosen ? Accent : FrostLine, Child = new TextBlock { Text = text, FontSize = 12.5 } };
            if (chosen) b.Background = B("#26FFFFFF");
            else { Hover(b, TileRest, TileOver); b.MouseLeftButtonUp += (s, e) => click(); }
            return b;
        }

        DispatcherTimer saveTimer;
        void SaveSoon()
        {   // the slider changes many times a second while it's dragged: write the file once it settles
            if (saveTimer == null)
            {
                saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                saveTimer.Tick += (s, e) => { saveTimer.Stop(); Set.Save(); };
            }
            saveTimer.Stop(); saveTimer.Start();
        }

        void SetTileSize(string size)
        {
            if (Set.TileSize == size) return;
            Set.TileSize = size; Set.Save();
            Shell.IconUnits = Set.IconUnits;
            homeDirty = true;
            Scan(true, true);   // fetch the icons again at the new size, so they stay sharp
            Geometry();         // new width, rounded corners and shadow
            if (IsOpen && !parked && !aActive) MoveTo(IntPtr.Zero, pxLeft, pxFinalTop, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        public void ShowAbout()
        {
            bool fromHome = view == "home";
            view = "about";
            var a = typeof(StartWindow).Assembly;
            var desc = (AssemblyDescriptionAttribute)Attribute.GetCustomAttribute(a, typeof(AssemblyDescriptionAttribute));
            var ver = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(a, typeof(AssemblyInformationalVersionAttribute));
            var sp = new StackPanel { Margin = new Thickness(Pad - 2, 0, Pad, 12) };

            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 2, 0, 16) };
            var logo = AppLogo();
            if (logo != null) top.Children.Add(new Image { Source = logo, Width = 48, Height = 48, Margin = new Thickness(0, 0, 14, 0) });
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(new TextBlock { Text = "Tessera", FontSize = 20, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") });
            names.Children.Add(new TextBlock { Text = "Version " + ver.InformationalVersion, FontSize = 12, Foreground = Fg3, Margin = new Thickness(0, 2, 0, 0) });
            top.Children.Add(names);
            sp.Children.Add(top);

            var how = new TextBlock { FontSize = 12.5, Foreground = Fg2, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), LineHeight = 20,
                Text = "Opens with the Windows key, Ctrl+Esc and the taskbar Start button.\n" +
                       "Categories: right-click a card or an app, or edit categories.txt.\n" +
                       "Settings, the on/off switch for the takeover, and Exit: right-click the footer." };
            sp.Children.Add(Group("How it works", desc.Description, null, how));

            var dir = AppDomain.CurrentDomain.BaseDirectory;
            var loc = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            loc.Children.Add(new TextBlock { Text = dir, FontSize = 12.5, Foreground = Fg2, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            var open = new StackPanel { Orientation = Orientation.Horizontal };
            open.Children.Add(Choice("Open folder", false, () => { HideMenu(); Process.Start("explorer.exe", "\"" + dir.TrimEnd('\\') + "\""); }));
            loc.Children.Add(open);
            sp.Children.Add(Group("Location", "Tessera.exe, categories.txt and settings.ini are here.", null, loc));
            SetPage(PageGrid("About", null, sp), fromHome ? 1 : 0);
        }

        static ImageSource logo;
        static ImageSource AppLogo()
        {
            if (logo != null) return logo;
            try
            {
                using (var s = typeof(StartWindow).Assembly.GetManifestResourceStream("Tessera.ico"))
                {
                    var dec = new IconBitmapDecoder(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    int want = (int)Math.Round(48 * Shell.Scale);
                    BitmapFrame best = null;
                    foreach (var f in dec.Frames)
                    {   // the smallest image at least as big as it's shown (so it's only ever shrunk), else the biggest
                        if (best == null) best = f;
                        else if (best.PixelWidth < want ? f.PixelWidth > best.PixelWidth : (f.PixelWidth >= want && f.PixelWidth < best.PixelWidth)) best = f;
                    }
                    if (best != null) { best.Freeze(); logo = best; }
                }
            }
            catch { }
            return logo;
        }

        // Right-click on the footer: what used to be on the tray icon's menu
        ContextMenu FooterMenu()
        {
            var cm = new ContextMenu();
            cm.Opened += (s, e) =>
            {
                cm.Items.Clear();
                cm.Items.Add(MI("Settings", () => ShowSettings()));
                cm.Items.Add(MI("Edit categories", () => { HideMenu(); Process.Start("notepad.exe", "\"" + Cfg.Path + "\""); }));
                bool on = GetTakeover != null && GetTakeover();
                var t = MI("Open with Windows key and Start button", () => { if (SetTakeover != null) SetTakeover(!on); });
                t.IsChecked = on;
                cm.Items.Add(t);
                cm.Items.Add(new Separator());
                cm.Items.Add(MI("About Tessera", () => ShowAbout()));
                cm.Items.Add(MI("Exit Tessera", () => { if (ExitApp != null) ExitApp(); }));
            };
            cm.Items.Add(new MenuItem());   // placeholder so the menu opens; filled in Opened
            return cm;
        }

        class SearchHit { public AppEntry E; public int S; }

        // 0 exact (with or without extension), 1 prefix, 2 word start, 3 contains, -1 no match
        static int Score(string name, string q) { return Matcher.Score(name, q); }

        // lowercase, letters and digits only, without a trailing "desktop" or "app"
        static string NormName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            string r = sb.ToString();
            if (r.Length > 7 && r.EndsWith("desktop")) r = r.Substring(0, r.Length - 7);
            else if (r.Length > 3 && r.EndsWith("app")) r = r.Substring(0, r.Length - 3);
            return r;
        }

        Dictionary<string, double> pickBonus = new Dictionary<string, double>();   // Picks for the query being shown

        // tier, then a nudge by kind (apps first, power actions last), minus how much you use it and pick it for this query
        double h_rank(SearchHit h, double use)
        {
            var e = h.E;
            double kind = e.Source == 0 ? 0 : e.Source == 5 ? (e.Path != null ? 0.6 : 0.8) : e.Source == 6 ? 1.0 : 0.5;
            double pick;
            string key = (e.Path ?? e.Id ?? "").ToLowerInvariant();
            if (!pickBonus.TryGetValue(key, out pick)) pick = 0;
            return h.S + kind - use - pick;
        }

        TextBlock Glyph(string glyph, double size)
        {
            return new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size * 0.75,
                Width = size, Height = size, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0, size * 0.1, 0, 0), Foreground = Brushes.White };
        }

        static string GlyphFor(AppEntry a)
        {
            if (a.Source == 7) return "";                      // search
            if (a.Id == "calc") return "";                     // calculator
            switch (a.Id)
            {
                case "action:lock": return "";
                case "action:sleep": return "";
                case "action:hibernate": return "";
                case "action:restart": return "";
                case "action:emptybin": return "";
                default: return "";                            // power
            }
        }

        static int SourceOrder(int src) { return src == 1 ? 0 : src == 2 ? 1 : src == 3 ? 2 : 3; }

        static bool IsUnderWindows(string path)
        {
            try
            {
                string root = Environment.GetEnvironmentVariable("SystemRoot");
                if (string.IsNullOrEmpty(root)) return false;
                return path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        void ShowSearch(string q) { ShowSearch(q, false); }

        // keepSel: this is the re-render after the background pass, so keep the user's moved selection on the same entry
        void ShowSearch(string q, bool keepSel)
        {
            AppEntry prev = keepSel && sel != 0 && sel < results.Count ? results[sel] : null;
            view = "search";
            var hidden = new HashSet<AppEntry>(Hidden());
            var appHits = new List<SearchHit>();
            foreach (var a in apps)
            {
                if (hidden.Contains(a)) continue;
                int s = Score(a.Name, q);
                if (s >= 0) appHits.Add(new SearchHit { E = a, S = s });
            }
            // settings pages, Control Panel items and power actions, ranked with the apps; one row per name
            var appNames = new HashSet<string>();
            foreach (var h in appHits) appNames.Add(NormName(h.E.Name));
            foreach (var a in settingPages.Concat(controlPanel).Concat(actions))
            {
                int s = Matcher.ScoreWithKeywords(a, q);
                if (s < 0 || !appNames.Add(NormName(a.Name))) continue;
                appHits.Add(new SearchHit { E = a, S = s });
            }
            // fuzzy and typo matches only when the close ones are few, so they never crowd out real hits
            int close = appHits.Count(h => h.S <= Matcher.Contains);
            if (close >= 3) appHits.RemoveAll(h => h.S >= Matcher.Typo);
            if (close >= 6) appHits.RemoveAll(h => h.S >= Matcher.Fuzzy);
            pickBonus = Picks.Bonuses(q);
            var fileHits = new List<SearchHit>();
            foreach (var c in commands)
            {
                int s = Score(c.Name, q);
                if (s != 0 && s != 1) continue;
                if (s != 0 && c.Source == 1 && IsUnderWindows(c.Path)) continue;   // System32 noise: exact matches only
                fileHits.Add(new SearchHit { E = c, S = s });
            }
            List<AppEntry> ih = null;
            if (indexQuery == q) ih = indexHits;
            else if (indexQuery.Length > 0 && q.StartsWith(indexQuery, StringComparison.OrdinalIgnoreCase)) ih = indexHits;
            if (ih != null)
                foreach (var c in ih)
                {
                    int s = Score(c.Name, q);
                    if (s >= 0) fileHits.Add(new SearchHit { E = c, S = s });
                }

            AppEntry typed = null;
            if (q.IndexOf('\\') >= 0 || q.IndexOf('/') >= 0 || (q.Length >= 2 && char.IsLetter(q[0]) && q[1] == ':'))
            {
                try
                {
                    string p = Environment.ExpandEnvironmentVariables(q);
                    if (File.Exists(p) || Directory.Exists(p)) typed = new AppEntry { Name = q, Id = p, Path = p, Detail = "Open", Source = 4 };
                }
                catch { }
            }

            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (typed != null) seenPaths.Add(typed.Path);
            var all = new List<SearchHit>(appHits);
            var cand = new List<SearchHit>();
            foreach (var h in fileHits)
            {
                if (!seenPaths.Add(h.E.Path)) continue;
                string bare = FileSearch.StripExt(h.E.Name);
                bool dup = false;
                string nb = NormName(bare);
                foreach (var ah in appHits)
                {
                    if (nb.Length > 0 && nb == NormName(ah.E.Name)) { dup = true; break; }
                    if (ah.E.Name.Equals(bare, StringComparison.OrdinalIgnoreCase) || (ah.E.Id != null && ah.E.Id.EndsWith("\\" + h.E.Name, StringComparison.OrdinalIgnoreCase))) { dup = true; break; }
                }
                if (!dup) cand.Add(h);
            }
            // junk (unless launched before), then one row per file name, then the best 8 files
            var best = new Dictionary<string, SearchHit>(StringComparer.OrdinalIgnoreCase);
            var bestUse = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in cand)
            {
                double u = UsageOf(h.E);
                if (u <= 0 && FileSearch.IsJunk(h.E)) continue;
                string fn = System.IO.Path.GetFileName(h.E.Path);
                SearchHit cur;
                if (best.TryGetValue(fn, out cur))
                {
                    double cu = bestUse[fn];
                    bool better = u > cu;
                    if (u == cu)
                    {
                        int rs = SourceOrder(h.E.Source), rc = SourceOrder(cur.E.Source);
                        better = rs < rc || (rs == rc && h.E.Path.Length < cur.E.Path.Length);
                    }
                    if (!better) continue;
                }
                best[fn] = h; bestUse[fn] = u;
            }
            var ranked = new List<KeyValuePair<double, SearchHit>>();
            foreach (var kv in best) ranked.Add(new KeyValuePair<double, SearchHit>(h_rank(kv.Value, bestUse[kv.Key]), kv.Value));
            foreach (var x in ranked.OrderBy(x => x.Key).ThenBy(x => x.Value.E.Name).Take(8)) all.Add(x.Value);
            var orderedHits = new List<KeyValuePair<double, SearchHit>>();
            foreach (var h in all) orderedHits.Add(new KeyValuePair<double, SearchHit>(h_rank(h, UsageOf(h.E)), h));
            var ordered = orderedHits.OrderBy(x => x.Key).ThenBy(x => x.Value.E.Name).Take(typed != null ? 29 : 30).Select(x => x.Value.E).ToList();
            if (typed != null) ordered.Insert(0, typed);

            // calculator answer goes first: typing "12*7" means you want 84, not an app with 12 in its name
            string answer = Calc.TryEval(q);
            if (answer != null)
            {
                string copy = answer.Replace(",", "");
                ordered.Insert(0, new AppEntry { Name = "= " + answer, Id = "calc", Detail = "Enter copies it", Source = 6,
                    Act = () => { try { Clipboard.SetText(copy); } catch { } } });
            }

            // documents and folders from the index, in their own section (newest first)
            var docs = new List<AppEntry>();
            if (q.Length >= 3 && docQuery.Length > 0 && (docQuery == q || q.StartsWith(docQuery, StringComparison.OrdinalIgnoreCase)))
            {
                var words = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var d in docHits)
                {
                    if (seenPaths.Contains(d.Path)) continue;
                    bool every = true;
                    foreach (var w in words) if (d.Name.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0) { every = false; break; }
                    if (every) docs.Add(d);
                }
                docs = docs.OrderByDescending(d => UsageOf(d)).Take(6).ToList();
            }
            int docStart = ordered.Count;
            ordered.AddRange(docs);

            // and always a way out: search the web for it
            string url = (Set != null ? Set.WebSearch : "https://www.google.com/search?q=%s").Replace("%s", Uri.EscapeDataString(q));
            string host = "the web";
            try { host = new Uri(url).Host; if (host.StartsWith("www.")) host = host.Substring(4); } catch { }
            int webAt = ordered.Count;
            ordered.Add(new AppEntry { Name = "Search the web for \"" + q + "\"", Id = "web", Detail = host, Source = 7,
                Act = () => Shell.LaunchFile(url, null, false) });

            results = ordered;
            foreach (var a in results) if (a.Path != null && a.Icon == null) a.Icon = FileSearch.Cached(FileSearch.IconSource(a.Path));

            var sp = new StackPanel { Margin = new Thickness(Pad - 4, 0, Pad - 10, 12) };
            sp.Children.Add(Header(webAt == 0 ? "Nothing on this PC matches \"" + q + "\"" : "Best matches", null, null));
            resultRows.Clear();
            for (int ri = 0; ri < results.Count; ri++)
            {
                var a = results[ri];
                if (ri == docStart && docs.Count > 0) sp.Children.Add(Header("Files & folders", null, null));
                var r = new Border { Height = 46, CornerRadius = new CornerRadius(6), Background = Clear, Padding = new Thickness(12, 0, 14, 0) };
                var rg = new Grid();
                if (a.Icon == null && (a.Source == 6 || a.Source == 7)) rg.Children.Add(Glyph(GlyphFor(a), 26));
                else { var ic = Icon(a, 26); ic.HorizontalAlignment = HorizontalAlignment.Left; rg.Children.Add(ic); }
                rg.Children.Add(new TextBlock { Text = a.Name, FontSize = 13.5, Margin = new Thickness(42, 0, 140, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                rg.Children.Add(new TextBlock { Text = a.Path != null || a.Source >= 5 ? a.Detail : CategoryOf(a), FontSize = 12, Foreground = Fg3, MaxWidth = 130, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
                r.Child = rg;
                var idx = resultRows.Count;
                r.MouseEnter += (s, e) => Select(idx);
                r.MouseLeftButtonUp += (s, e) => Run(a, false);
                if (a.Source >= 5 && a.Source <= 7) { if (a.Path != null) r.ToolTip = a.Path; }
                else if (a.Path != null) { r.ToolTip = a.Path; r.ContextMenu = FileMenu(a); }
                else r.ContextMenu = AppMenu(a);
                resultRows.Add(r); sp.Children.Add(r);
            }
            SetPage(Scroll(sp), 0);
            int pick = 0;
            if (prev != null)
                for (int k = 0; k < results.Count; k++)
                    if (prev.Path != null ? string.Equals(results[k].Path, prev.Path, StringComparison.OrdinalIgnoreCase) : ReferenceEquals(results[k], prev)) { pick = k; break; }
            Select(pick);

            // background pass: ask the Windows Search index for files and fetch icons of file results
            int gen = ++searchGen;
            bool needIndex = indexQuery != q && q.Length >= 2;
            bool needDocs = docQuery != q && q.Length >= 3 && answer == null;
            var need = new List<AppEntry>();
            foreach (var a in results) if (a.Path != null && a.Icon == null && !iconFailed.Contains(a.Path)) need.Add(a);
            if (needIndex || needDocs || need.Count > 0) StartSearchPass(q, gen, needIndex, needDocs, need);
        }

        void StartSearchPass(string q, int gen, bool needIndex, bool needDocs, List<AppEntry> need)
        {
            var t = new Thread(() =>
            {
                List<AppEntry> hits = null, docs = null;
                if (needIndex || needDocs)
                {
                    Thread.Sleep(120);   // let typing settle
                    if (gen != searchGen) return;
                }
                if (needIndex)
                {
                    hits = FileSearch.QueryIndex(q, 30);
                    if (gen != searchGen) return;
                }
                if (needDocs)
                {
                    docs = FileSearch.QueryDocs(q, 8);
                    if (gen != searchGen) return;
                }
                var todo = new List<AppEntry>(need);
                if (hits != null) todo.AddRange(hits);
                if (docs != null) todo.AddRange(docs);
                var failed = new List<string>();
                foreach (var e in todo)
                {
                    if (gen != searchGen) return;
                    if (e.Icon != null) continue;
                    var ic = FileSearch.Icon(FileSearch.IconSource(e.Path));
                    if (ic != null) e.Icon = ic; else failed.Add(e.Path);
                }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (var f in failed) iconFailed.Add(f);
                    if (gen != searchGen || view != "search" || search.Text.Trim() != q) return;
                    if (hits != null) { indexQuery = q; indexHits = hits; }
                    if (docs != null) { docQuery = q; docHits = docs; }
                    ShowSearch(q, true);
                }));
            });
            t.SetApartmentState(ApartmentState.STA); t.IsBackground = true; t.Start();
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

        static bool CanAdmin(AppEntry a) { if (a.Path != null) { try { return File.Exists(a.Path); } catch { return false; } } return !a.Id.Contains("!") && !a.Id.Contains("://"); }

        ContextMenu FileMenu(AppEntry a)
        {
            var cm = new ContextMenu();
            cm.Items.Add(MI("Open", () => Run(a, false)));
            if (CanAdmin(a)) cm.Items.Add(MI("Run as administrator", () => Run(a, true)));
            cm.Items.Add(new Separator());
            cm.Items.Add(MI("Open file location", () => { HideMenu(); try { Process.Start("explorer.exe", "/select,\"" + a.Path + "\""); } catch { } }));
            cm.Items.Add(MI("Copy path", () => { try { Clipboard.SetText(a.Path); } catch { } }));
            return cm;
        }

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
  <!-- separators inside menus look up this key instead of the plain Separator style above -->
  <Style x:Key='{x:Static MenuItem.SeparatorStyleKey}' TargetType='Separator'>
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
            <TextBlock x:Name='Check' Grid.Column='1' Text='&#xE73E;' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='12' Margin='18,0,0,0'
                       VerticalAlignment='Center' Foreground='#FF4CC2FF' Visibility='Collapsed'/>
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
          <Trigger Property='IsChecked' Value='True'><Setter TargetName='Check' Property='Visibility' Value='Visible'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>
  <Style x:Key='SliderFill' TargetType='RepeatButton'>
    <Setter Property='Focusable' Value='False'/><Setter Property='IsTabStop' Value='False'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='RepeatButton'><Border Height='4' CornerRadius='2' Background='#FF4CC2FF'/></ControlTemplate>
    </Setter.Value></Setter>
  </Style>
  <Style x:Key='SliderRest' TargetType='RepeatButton'>
    <Setter Property='Focusable' Value='False'/><Setter Property='IsTabStop' Value='False'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate>
    </Setter.Value></Setter>
  </Style>
  <Style TargetType='Slider'>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='Slider'>
        <Grid Height='24' Background='Transparent'>
          <Border Height='4' CornerRadius='2' Background='#33FFFFFF' VerticalAlignment='Center' Margin='2,0'/>
          <Track x:Name='PART_Track'>
            <Track.DecreaseRepeatButton><RepeatButton Command='Slider.DecreaseLarge' Style='{StaticResource SliderFill}'/></Track.DecreaseRepeatButton>
            <Track.IncreaseRepeatButton><RepeatButton Command='Slider.IncreaseLarge' Style='{StaticResource SliderRest}'/></Track.IncreaseRepeatButton>
            <Track.Thumb><Thumb Width='20' Height='20'><Thumb.Template><ControlTemplate TargetType='Thumb'>
              <Grid>
                <Ellipse Fill='#FF454545' Stroke='#33FFFFFF' StrokeThickness='1'/>
                <Ellipse x:Name='Dot' Width='10' Height='10' Fill='#FF4CC2FF'/>
              </Grid>
              <ControlTemplate.Triggers>
                <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Dot' Property='Width' Value='12'/><Setter TargetName='Dot' Property='Height' Value='12'/></Trigger>
                <Trigger Property='IsDragging' Value='True'><Setter TargetName='Dot' Property='Width' Value='8'/><Setter TargetName='Dot' Property='Height' Value='8'/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
          </Track>
        </Grid>
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
            ToggleKeyName = "Local\\Tessera.ToggleKey",   // like Toggle, from the keyboard: opens on the monitor you're working on
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
            EventWaitHandle toggle = null, toggleKey = null;
            Hooks.Toggle = keyboard =>
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    AllowSetForegroundWindow(-1);   // let the menu take focus even over an admin window
                    if (keyboard)
                        try
                        {
                            if (toggleKey == null) toggleKey = EventWaitHandle.OpenExisting(ToggleKeyName);
                            toggleKey.Set();
                            return;
                        }
                        catch { toggleKey = null; }   // a menu from before 1.1: the plain toggle below
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
            var settings = new Settings(Path.Combine(Dir, "settings.ini"));
            Shell.IconUnits = settings.IconUnits;
            var win = new StartWindow(new Config(Path.Combine(Dir, "categories.txt")), settings);
            new WindowInteropHelper(win).EnsureHandle();
            var toggleKeyEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ToggleKeyName);

            Hooks.Toggle = keyboard => win.Dispatcher.BeginInvoke(new Action(() => win.Toggle(keyboard)));
            Hooks.Enabled = !File.Exists(DisabledFlag);
            // for the Windhawk mod: "takeover is on" (the event only exists while this runs)
            var enabledEvent = new EventWaitHandle(Hooks.Enabled, EventResetMode.ManualReset, EnabledName);
            if (Hooks.Enabled) enabledEvent.Set(); else enabledEvent.Reset();

            var waiter = new Thread(() => { while (true) { showEvent.WaitOne(); win.Dispatcher.BeginInvoke((Action)win.ShowMenu); } });
            waiter.IsBackground = true; waiter.Start();
            var toggler = new Thread(() => { while (true) { toggleEvent.WaitOne(); win.Dispatcher.BeginInvoke((Action)win.Toggle); } });
            toggler.IsBackground = true; toggler.Start();
            var keyToggler = new Thread(() => { while (true) { toggleKeyEvent.WaitOne(); win.Dispatcher.BeginInvoke(new Action(() => win.Toggle(true))); } });
            keyToggler.IsBackground = true; keyToggler.Start();

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

            // The tray icon is hidden unless Settings > Tray icon says otherwise: its options are on the footer's
            // right-click menu too (see StartWindow.FooterMenu)
            tray = new WF.NotifyIcon { Text = "Tessera", Icon = AppIcon(), Visible = settings.ShowTrayIcon };
            var menu = new WF.ContextMenuStrip();
            menu.Items.Add("Open Tessera", null, (s, e) => win.ShowMenu());
            menu.Items.Add("Edit categories", null, (s, e) => Process.Start("notepad.exe", "\"" + win.Cfg.Path + "\""));
            var takeover = new WF.ToolStripMenuItem("Open with Windows key and Start button") { Checked = Hooks.Enabled, CheckOnClick = true };
            Action<bool> setTakeover = on =>
            {
                Hooks.Enabled = on;
                if (on) enabledEvent.Set(); else enabledEvent.Reset();
                try { if (on) File.Delete(DisabledFlag); else File.WriteAllText(DisabledFlag, "off"); } catch { }
                if (takeover.Checked != on) takeover.Checked = on;
            };
            takeover.CheckedChanged += (s, e) => { if (takeover.Checked != Hooks.Enabled) setTakeover(takeover.Checked); };
            Action exit = () => { tray.Visible = false; Hooks.Stop(); win.Exiting = true; app.Shutdown(); };
            menu.Items.Add(takeover);
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add("About Tessera", null, (s, e) => { win.ShowMenu(); win.ShowAbout(); });
            menu.Items.Add("Exit", null, (s, e) => exit());
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) win.ShowMenu(); };
            win.GetTakeover = () => Hooks.Enabled;
            win.SetTakeover = setTakeover;
            win.ExitApp = exit;
            win.ShowTray = on => tray.Visible = on;

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
    }
}
