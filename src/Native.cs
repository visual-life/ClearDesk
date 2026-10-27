using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Automation;

namespace ClearDesk
{
    internal static class Native
    {
        public delegate bool EnumProc(IntPtr h, IntPtr p);
        public delegate IntPtr MouseProc(int code, IntPtr w, IntPtr l);
        public delegate void EventProc(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time);
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int L, T, R, B;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSE
        {
            public POINT pt;
            public uint data, flags, time;
            public UIntPtr extra;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUT
        {
            public uint size, time;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORY
        {
            public uint length, load;
            public ulong totalPhys, availPhys, totalPage, availPage, totalVirtual, availVirtual, availExtended;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHINFO
        {
            public IntPtr icon;
            public int index;
            public uint attributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string type;
        }

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumProc f, IntPtr p);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string c, string t);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string t);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr h, StringBuilder b, int n);
        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")]
        public static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        public static extern IntPtr GetLong(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        public static extern IntPtr SetLong(IntPtr h, int i, IntPtr value);
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPOS_PIN { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }
        [DllImport("user32.dll")]
        public static extern IntPtr SetWindowsHookEx(int id, MouseProc proc, IntPtr module, uint tid);
        [DllImport("user32.dll")]
        public static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")]
        public static extern uint GetDoubleClickTime();
        [DllImport("user32.dll")]
        public static extern bool GetLastInputInfo(ref LASTINPUT info);
        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr dll, EventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(IntPtr h);
        [DllImport("kernel32.dll")]
        public static extern bool GlobalMemoryStatusEx(ref MEMORY m);
        [DllImport("kernel32.dll")]
        public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHGetFileInfo(string p, uint attrs, ref SHINFO info, uint size, uint flags);
        [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW")]
        static extern IntPtr SHGetFileInfo(IntPtr pidl, uint attrs, ref SHINFO info, uint size, uint flags);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHParseDisplayName(string name, IntPtr context, out IntPtr pidl, uint attributes, out uint result);
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")]
        public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr h);
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr h, uint message, IntPtr w, IntPtr l);
        [DllImport("shell32.dll", EntryPoint = "#727")]
        static extern int SHGetImageList(int size, ref Guid iid, out IImageList list);
        [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IImageList
        {
            [PreserveSig]
            int Add(IntPtr a, IntPtr b, out int index);
            [PreserveSig]
            int ReplaceIcon(int index, IntPtr icon, out int value);
            [PreserveSig]
            int SetOverlayImage(int a, int b);
            [PreserveSig]
            int Replace(int i, IntPtr a, IntPtr b);
            [PreserveSig]
            int AddMasked(IntPtr a, uint b, out int i);
            [PreserveSig]
            int Draw(IntPtr p);
            [PreserveSig]
            int Remove(int i);
            [PreserveSig]
            int GetIcon(int i, int flags, out IntPtr icon);
        }

        public static string Class(IntPtr h)
        {
            var s = new StringBuilder(256);
            GetClassName(h, s, 256);
            return s.ToString();
        }

        public static IntPtr Icons()
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate (IntPtr h, IntPtr p)
            {
                var d = FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (d != IntPtr.Zero)
                {
                    found = FindWindowEx(d, IntPtr.Zero, "SysListView32", null);
                    return false;
                }

                return true;
            }, IntPtr.Zero);
            return found;
        }

        public static bool DesktopAt(POINT p)
        {
            string c = Class(WindowFromPoint(p));
            return c == "SysListView32" || c == "SHELLDLL_DefView" || c == "WorkerW" || c == "Progman";
        }

        public static bool EmptyAt(POINT p)
        {
            if (!DesktopAt(p))
                return false;
            try
            {
                var a = AutomationElement.FromPoint(new Point(p.X, p.Y));
                for (int i = 0; i < 5 && a != null; i++)
                {
                    if (a.Current.ControlType == ControlType.ListItem)
                        return false;
                    if (a.Current.ClassName == "SysListView32")
                        return true;
                    a = TreeWalker.ControlViewWalker.GetParent(a);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static List<IntPtr> Taskbars()
        {
            var l = new List<IntPtr>();
            EnumWindows(delegate (IntPtr h, IntPtr p)
            {
                var c = Class(h);
                if (c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd")
                    l.Add(h);
                return true;
            }, IntPtr.Zero);
            return l;
        }

        public static ImageSource Icon(string p)
        {
            SHINFO info = new SHINFO();
            IntPtr pidl = IntPtr.Zero;
            try
            {
                IntPtr result;
                int stock = SystemItems.StockIconIndex(p);                       // 2.2.1: the recycle bin shows its empty / full icon
                if (stock >= 0) { var stockIcon = IconByIndex(stock); if (stockIcon != null) return stockIcon; }
                if (p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                {
                    uint attributes;
                    if (SHParseDisplayName(SystemItems.ParsingName(p), IntPtr.Zero, out pidl, 0, out attributes) != 0)
                        return null;
                    result = SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf(info), 0x4008);
                }
                else
                    result = SHGetFileInfo(p, 0, ref info, (uint)Marshal.SizeOf(info), 0x4000);
                if (result == IntPtr.Zero)
                    return null;
            }
            finally
            {
                if (pidl != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(pidl);
            }

            return IconByIndex(info.index);
        }

        /// <summary>True when the file's icon is the shell's default "application" icon (an exe with no icon resource of its own).</summary>
        internal static bool HasGenericIcon(string path)
        {
            try
            {
                var generic = new SHINFO(); var mine = new SHINFO();
                if (SHGetFileInfo("x.exe", 0x80, ref generic, (uint)Marshal.SizeOf(generic), 0x4000 | 0x10) == IntPtr.Zero) return false;
                if (SHGetFileInfo(path, 0, ref mine, (uint)Marshal.SizeOf(mine), 0x4000) == IntPtr.Zero) return false;
                return mine.index == generic.index;
            }
            catch { return false; }
        }

        internal static ImageSource IconByIndex(int index)
        {
            foreach (int size in new[]
            {
                4,
                2,
                0
            }

            )
            {
                IImageList list = null;
                IntPtr icon = IntPtr.Zero;
                try
                {
                    var iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
                    if (SHGetImageList(size, ref iid, out list) != 0 || list == null || list.GetIcon(index, 1, out icon) != 0 || icon == IntPtr.Zero)
                        continue;
                    BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    // Some legacy apps return a small icon inside a padded 256px shell canvas.
                    if (size == 4)
                    {
                        var pixels = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                        int w = pixels.PixelWidth, h = pixels.PixelHeight;
                        byte[] data = new byte[w * h * 4];
                        pixels.CopyPixels(data, w * 4, 0);
                        int l = w, t = h, rr = -1, bb = -1;
                        for (int y = 0; y < h; y++)
                            for (int x = 0; x < w; x++)
                                if (data[(y * w + x) * 4 + 3] > 8)
                                {
                                    l = Math.Min(l, x);
                                    t = Math.Min(t, y);
                                    rr = Math.Max(rr, x);
                                    bb = Math.Max(bb, y);
                                }

                        if (rr < l)
                            continue;
                        if (Math.Max(rr - l + 1, bb - t + 1) < w * .65)
                            bitmap = new CroppedBitmap(bitmap, new Int32Rect(l, t, rr - l + 1, bb - t + 1));
                    }

                    bitmap.Freeze();
                    return bitmap;
                }
                catch
                {
                }
                finally
                {
                    if (icon != IntPtr.Zero)
                        DestroyIcon(icon);
                    if (list != null)
                        Marshal.ReleaseComObject(list);
                }
            }

            return null;
        }

        public static void MediaKey(byte k)
        {
            keybd_event(k, 0, 0, UIntPtr.Zero);
            keybd_event(k, 0, 2, UIntPtr.Zero);
        }
    }
}
