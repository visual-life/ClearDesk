using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClearDesk
{
    /// <summary>What the Dock knows about a running app when it needs its icon.</summary>
    internal sealed class AppIconRequest
    {
        public string Exe, AppId, Name, LaunchPath;
        public IntPtr[] Windows = new IntPtr[0];
        internal string CacheKey { get { return "run:" + (AppId ?? "") + "|" + (Exe ?? "") + "|" + (Name ?? ""); } }
    }

    /// <summary>
    /// 2.2.3: icon resolution for apps in the Dock. A chain of sources, each of which may fail or return a blank image; the first usable one wins.
    /// Apps hosted by ApplicationFrameHost (UWP) and apps whose exe has no icon of its own fall through to the package / window / shortcut icon.
    /// </summary>
    internal static class AppIcons
    {
        internal struct Step { public string Name; public Func<ImageSource> Get; }

        // sources are replaceable so the chain can be tested without real apps
        internal static Func<string, ImageSource> ExeIcon = path => Native.Icon(path);
        internal static Func<string, ImageSource> PackageIcon = aumid => ShellApps.Icon(aumid);
        internal static Func<IntPtr, ImageSource> WindowIcon = WindowIcons.Get;
        internal static Func<AppIconRequest, ImageSource> ShortcutIcon = StartMenuIcon;
        internal static Func<string, bool> IsGenericExe = path => Native.HasGenericIcon(path);

        internal static bool IsHostExe(string exe)
        {
            string n = Path.GetFileName(exe ?? "").ToLowerInvariant();
            return n == "applicationframehost.exe";
        }

        /// <summary>The ordered sources for one app.</summary>
        internal static List<Step> Chain(AppIconRequest r)
        {
            var steps = new List<Step>();
            bool host = IsHostExe(r.Exe);
            bool hasId = !string.IsNullOrEmpty(r.AppId);
            if (host && hasId) steps.Add(new Step { Name = "package icon (AppUserModelID)", Get = () => PackageIcon(r.AppId) });
            if (!string.IsNullOrEmpty(r.Exe) && !host)
                steps.Add(new Step { Name = "exe icon", Get = () => IsGenericExe(r.Exe) ? null : ExeIcon(r.Exe) });
            foreach (var w in r.Windows ?? new IntPtr[0])
            {
                var win = w;
                steps.Add(new Step { Name = "window icon 0x" + win.ToInt64().ToString("X"), Get = () => WindowIcon(win) });
            }
            if (!host && hasId) steps.Add(new Step { Name = "package icon (AppUserModelID)", Get = () => PackageIcon(r.AppId) });
            steps.Add(new Step { Name = "Start menu shortcut", Get = () => ShortcutIcon(r) });
            if (!string.IsNullOrEmpty(r.Exe) && !host)
                steps.Add(new Step { Name = "exe icon (generic)", Get = () => ExeIcon(r.Exe) });
            return steps;
        }

        /// <summary>First usable icon, or null when every source failed (the caller shows a letter tile and tries again later; a failure is never cached).</summary>
        internal static ImageSource Resolve(AppIconRequest r, out string how)
        {
            how = null;
            foreach (var s in Chain(r))
            {
                ImageSource img = null;
                try { img = s.Get(); } catch { img = null; }
                if (img != null && !IsBlank(img)) { how = s.Name; return img; }
            }
            return null;
        }

        /// <summary>Same as <see cref="Resolve"/>, but reports what every source returned.</summary>
        internal static ImageSource Trace(AppIconRequest r, List<string> log, string saveTo = null, string label = null)
        {
            ImageSource won = null; int n = 0;
            foreach (var s in Chain(r))
            {
                ImageSource img = null; string err = null;
                try { img = s.Get(); } catch (Exception e) { err = e.GetType().Name; }
                string state = err != null ? "异常 " + err : img == null ? "无" : IsBlank(img) ? "空白图" : (Size(img) + " 像素");
                bool usable = err == null && img != null && !IsBlank(img);
                log.Add("    " + (usable && won == null ? "→ " : "  ") + s.Name + "：" + state);
                if (saveTo != null && img is BitmapSource)
                    try { Directory.CreateDirectory(saveTo); var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create((BitmapSource)img)); string fn = string.Join("_", (label ?? "app").Split(Path.GetInvalidFileNameChars())) + "-" + (++n) + ".png"; using (var f = File.Create(Path.Combine(saveTo, fn))) enc.Save(f); } catch { }
                if (usable && won == null) won = img;
            }
            return won;
        }
        static string Size(ImageSource i) { var b = i as BitmapSource; return b == null ? "?" : b.PixelWidth + "×" + b.PixelHeight; }

        /// <summary>An image that is completely (or almost completely) transparent is a failed lookup, not an icon.</summary>
        internal static bool IsBlank(ImageSource source)
        {
            var bmp = source as BitmapSource;
            if (bmp == null) return false;
            try
            {
                var c = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
                int w = c.PixelWidth, h = c.PixelHeight; if (w <= 0 || h <= 0) return true;
                var px = new byte[w * h * 4]; c.CopyPixels(px, w * 4, 0);
                int opaque = 0; for (int i = 3; i < px.Length; i += 4) if (px[i] > 24) opaque++;
                return opaque < Math.Max(4, w * h / 400);
            }
            catch { return false; }
        }

        /// <summary>The tile shown while no real icon is available: a rounded square with the first letter of the app's name.</summary>
        internal static ImageSource Letter(string name)
        {
            string text = "?";
            if (!string.IsNullOrWhiteSpace(name)) { var si = new StringInfo(name.Trim()); text = si.SubstringByTextElements(0, 1).ToUpperInvariant(); }
            int hash = 17; foreach (char ch in name ?? "") hash = hash * 31 + ch;
            double hue = ((hash & 0x7FFFFFFF) % 360);
            Color top = FromHsv(hue, .55, .85), bottom = FromHsv(hue, .7, .62);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var brush = new LinearGradientBrush(top, bottom, 90);
                dc.DrawRoundedRectangle(brush, null, new Rect(8, 8, 112, 112), 26, 26);
                var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 64, Brushes.White);
                dc.DrawText(ft, new Point(64 - ft.Width / 2, 64 - ft.Height / 2));
            }
            var bmp = new RenderTargetBitmap(128, 128, 96, 96, PixelFormats.Pbgra32); bmp.Render(dv); bmp.Freeze(); return bmp;
        }
        static Color FromHsv(double h, double s, double v)
        {
            double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c, r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; } else if (h < 120) { r = x; g = c; } else if (h < 180) { g = c; b = x; } else if (h < 240) { g = x; b = c; } else if (h < 300) { r = x; b = c; } else { r = c; b = x; }
            return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
        }

        // ---- Start menu shortcut whose target is this app
        static readonly Dictionary<string, AppIdentity> linkIdentity = new Dictionary<string, AppIdentity>(StringComparer.OrdinalIgnoreCase);
        static List<string> links; static DateTime linksAt;
        static ImageSource StartMenuIcon(AppIconRequest r)
        {
            if (links == null || DateTime.UtcNow - linksAt > TimeSpan.FromMinutes(2))
            {
                var l = new List<string>();
                foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) })
                    try { l.AddRange(Directory.GetFiles(folder, "*.lnk", SearchOption.AllDirectories)); } catch { }
                links = l; linksAt = DateTime.UtcNow;
            }
            string stem = string.IsNullOrEmpty(r.Exe) ? null : Path.GetFileNameWithoutExtension(r.Exe);
            var want = new AppIdentity { Exe = r.Exe, AppId = r.AppId };
            foreach (var link in links)
            {
                string n = Path.GetFileNameWithoutExtension(link);
                bool near = (stem != null && n.IndexOf(stem, StringComparison.OrdinalIgnoreCase) >= 0) || (!string.IsNullOrEmpty(r.Name) && n.IndexOf(r.Name, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!near) continue;
                AppIdentity id; if (!linkIdentity.TryGetValue(link, out id)) { id = ShellIdentity.Resolve(link); if (linkIdentity.Count > 512) linkIdentity.Clear(); linkIdentity[link] = id; }
                if (AppIdentity.Matches(want, id)) { var icon = Native.Icon(link); if (icon != null && !IsBlank(icon)) return icon; }
            }
            return null;
        }
    }

    /// <summary>Icons the window itself provides (WM_GETICON, class icon).</summary>
    internal static class WindowIcons
    {
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] static extern IntPtr GetClassLongPtr64(IntPtr h, int index);
        [DllImport("user32.dll", EntryPoint = "GetClassLongW")] static extern uint GetClassLong32(IntPtr h, int index);
        static IntPtr ClassIcon(IntPtr h, int index) { return IntPtr.Size == 8 ? GetClassLongPtr64(h, index) : new IntPtr((int)GetClassLong32(h, index)); }

        internal static ImageSource Get(IntPtr window)
        {
            if (window == IntPtr.Zero) return null;
            var handles = new List<IntPtr>();
            foreach (int kind in new[] { 1, 2, 0 })    // ICON_BIG, ICON_SMALL2, ICON_SMALL
            {
                IntPtr icon; try { if (SendMessageTimeout(window, 0x7F, new IntPtr(kind), IntPtr.Zero, 0x3, 150, out icon) != IntPtr.Zero && icon != IntPtr.Zero) handles.Add(icon); } catch { }
            }
            try { var c = ClassIcon(window, -14); if (c != IntPtr.Zero) handles.Add(c); var s = ClassIcon(window, -34); if (s != IntPtr.Zero) handles.Add(s); } catch { }
            ImageSource best = null; int bestSize = 0;
            foreach (var h in handles)
            {
                try
                {
                    var bmp = Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); bmp.Freeze();
                    if (AppIcons.IsBlank(bmp)) continue;
                    if (bmp.PixelWidth > bestSize) { best = bmp; bestSize = bmp.PixelWidth; }
                }
                catch { }
            }
            return best;     // the icons belong to the window: never destroyed here
        }
    }

    /// <summary>Packaged / UWP apps: the shell's own image for "shell:AppsFolder\AppUserModelID".</summary>
    internal static class ShellApps
    {
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public short bmPlanes, bmBitsPixel; public IntPtr bmBits; }
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap); }
        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetParent(out IShellItem item);
            [PreserveSig] int GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)] static extern int SHCreateItemFromParsingName(string path, IntPtr bc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object item);
        [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, ref BITMAP bmp);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

        static object Item(string aumid, Guid iid)
        {
            object o; if (SHCreateItemFromParsingName("shell:AppsFolder\\" + aumid, IntPtr.Zero, ref iid, out o) != 0) return null; return o;
        }

        /// <summary>The app's display name from the Start menu (e.g. "设置"), or null.</summary>
        static readonly Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal static string DisplayName(string aumid)
        {
            if (string.IsNullOrEmpty(aumid)) return null;
            string known; if (names.TryGetValue(aumid, out known)) return known;
            object o = null;
            try { o = Item(aumid, typeof(IShellItem).GUID); var item = o as IShellItem; string name; if (item != null && item.GetDisplayName(0, out name) == 0) { if (names.Count > 256) names.Clear(); names[aumid] = name; return name; } }
            catch { }
            finally { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }
            return null;
        }

        internal static ImageSource Icon(string aumid)
        {
            if (string.IsNullOrEmpty(aumid)) return null;
            object o = null; IntPtr hbmp = IntPtr.Zero;
            try
            {
                o = Item(aumid, typeof(IShellItemImageFactory).GUID); var f = o as IShellItemImageFactory; if (f == null) return null;
                foreach (int px in new[] { 256, 128, 64 })
                {
                    if (f.GetImage(new SIZE { cx = px, cy = px }, 0x4 | 0x1, out hbmp) != 0 || hbmp == IntPtr.Zero) continue;     // SIIGBF_ICONONLY | BIGGEROK
                    var b = new BITMAP(); if (GetObject(hbmp, Marshal.SizeOf(typeof(BITMAP)), ref b) == 0 || b.bmBits == IntPtr.Zero || b.bmBitsPixel != 32) { DeleteObject(hbmp); hbmp = IntPtr.Zero; continue; }
                    var data = new byte[b.bmWidth * b.bmHeight * 4]; Marshal.Copy(b.bmBits, data, 0, data.Length);
                    var bmp = BitmapSource.Create(b.bmWidth, b.bmHeight, 96, 96, PixelFormats.Bgra32, null, data, b.bmWidth * 4); bmp.Freeze();
                    DeleteObject(hbmp); hbmp = IntPtr.Zero;
                    if (!AppIcons.IsBlank(bmp)) return bmp;
                }
            }
            catch { }
            finally { if (hbmp != IntPtr.Zero) DeleteObject(hbmp); if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }
            return null;
        }
    }
}
