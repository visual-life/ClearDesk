using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

namespace ClearDesk
{
    internal sealed class TrayItem
    {
        public string Key, Name, Tooltip;
        string raw; public string Raw { get { return raw ?? Name ?? ""; } set { raw = value; } }     // 2.3.6: the name UI Automation gave the icon (cleaned) — EMPTY for icons like QQ that have no tooltip; Name is then filled in from the program's own name
        public int Ordinal;         // 2.3.6: n-th icon with this Raw name in its list (identifies it when several icons share a name or have none)
        public string Exe;          // 2.3.6: the program, when Windows' own list of notification icons identified it
        public byte[] Capture, Snapshot;    // 2.3.6: screen copy taken during this read / the PNG Windows keeps for the program
        public bool Overflow;
        public byte[] Png;          // the icon, copied from the screen when it was visible (UI Automation has no image)
    }

    /// <summary>2.3.3: the notification-area icons, read and operated through UI Automation. Windows 11 (XAML taskbar) has no legacy toolbar to read and gives other programs no
    /// icon handles, so this is the supported way: the visible icons are children of Shell_TrayWnd; the hidden ones are listed by opening the overflow flyout for a moment.
    /// Left click = InvokePattern (the program receives its normal click); right click = a real mouse click on the icon (so the program shows its own menu).
    /// Every call is blocking — run it on a worker thread.</summary>
    internal static class TrayAccess
    {
        const string OverflowClass = "TopLevelWindowForOverflowXamlIsland";
        static readonly object gate = new object();
        static readonly Dictionary<string, byte[]> iconCache = new Dictionary<string, byte[]>();
        internal static string LastProblem;
        internal static int OverflowOpens, VisiblePasses, RevealFallbacks;
        internal static string LastRightClickMethod;
        static readonly HashSet<string> imageTried = new HashSet<string>();

        internal static string Clean(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            int cut = name.IndexOfAny(new[] { '\r', '\n' });
            return (cut >= 0 ? name.Substring(0, cut) : name).Trim();
        }

        /// <summary>Tooltips carry changing status ("YueLink · 已连接 · …"); the picture is remembered under the stable first part.</summary>
        internal static string IconKey(string name)
        {
            name = Clean(name); int cut = name.IndexOf(" · ", StringComparison.Ordinal);
            return (cut > 0 ? name.Substring(0, cut) : name).Trim();
        }

        static AutomationElement Tray()
        {
            try { return AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd")); }
            catch { return null; }
        }

        static AutomationElement OverflowWindow()
        {
            try
            {
                var w = AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, OverflowClass));
                if (w == null) return null;
                int hwnd = w.Current.NativeWindowHandle;
                if (hwnd != 0) return UiaCom.IsWindowVisible(new IntPtr(hwnd)) ? w : null;       // 2.3.4: an invisible (empty-region) flyout is still "open"
                return w.Current.IsOffscreen ? null : w;
            }
            catch { return null; }
        }

        static AutomationElement[] Buttons(AutomationElement root)
        {
            try { return root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ClassNameProperty, "SystemTray.NormalButton")).Cast<AutomationElement>().ToArray(); }
            catch { return new AutomationElement[0]; }
        }

        /// <summary>2.3.6: every notification icon below root, found by its AutomationId (not by the button class, which differs between Windows builds), in document order.</summary>
        static AutomationElement[] Icons(AutomationElement root)
        {
            try { return root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "NotifyItemIcon")).Cast<AutomationElement>().ToArray(); }
            catch { return new AutomationElement[0]; }
        }

        static bool IsAppIcon(AutomationElement e) { try { return e.Current.AutomationId == "NotifyItemIcon"; } catch { return false; } }

        static AutomationElement Chevron(AutomationElement tray)
        {
            AutomationElement first = null;
            foreach (var b in Buttons(tray))
            {
                string id, name; try { id = b.Current.AutomationId; name = b.Current.Name ?? ""; } catch { continue; }
                if (id != "SystemTrayIcon") continue;
                if (first == null) first = b;
                if (name.IndexOf("隐藏") >= 0 || name.IndexOf("hidden", StringComparison.OrdinalIgnoreCase) >= 0) return b;
            }
            return first;
        }

        /// <summary>Closes the overflow flyout if it is open (tests leave nothing open).</summary>
        internal static void CloseFlyout() { lock (gate) { var t = Tray(); if (t != null) CloseOverflow(t); } UiaCom.ResetFlyoutNow(); }

        internal static bool Available { get { return Tray() != null; } }

        internal struct RawIcon { public string Full; public byte[] Png; }

        internal static bool TestNoIconCache;       // unit tests: Build() leaves the icon cache (which is saved to disk) alone
        internal static void TestResetNamed() { lock (iconCache) { namedVisible = new List<string>(); namedHidden = new List<string>(); } }
        static List<string> namedVisible = new List<string>(), namedHidden = new List<string>();

        /// <summary>2.3.6: turns what UI Automation showed (names, possibly empty or identical) into items. NOTHING is dropped: an icon with an empty name (QQ) is kept and named after its program
        /// (tooltip, else the exe's file description / product name, else the exe's file name, else 未命名图标). Two icons with the same name stay two items (Ordinal tells them apart).</summary>
        internal static List<TrayItem> Build(IList<RawIcon> raws, bool overflow, List<NotifyEntry> entries)
        {
            var list = new List<TrayItem>(); var seen = new Dictionary<string, int>();
            foreach (var r in raws)
            {
                string raw = Clean(r.Full);
                int n; seen.TryGetValue(raw, out n); seen[raw] = n + 1;
                list.Add(new TrayItem { Raw = raw, Ordinal = n, Name = raw, Tooltip = (r.Full ?? "").Trim(), Overflow = overflow, Capture = r.Png });
            }
            var mine = list.Where(i => i.Raw.Length > 0).Select(i => i.Raw).ToList();
            if (!TestNoIconCache) lock (iconCache) { if (overflow) namedHidden = mine; else namedVisible = mine; }
            if (list.Any(i => i.Raw.Length == 0))
            {
                List<string> other; lock (iconCache) other = TestNoIconCache ? new List<string>() : overflow ? namedVisible : namedHidden;
                try { TrayNames.Resolve(list, entries ?? TrayNames.Entries(), other); }
                catch (Exception e) { Program.Log(e); int k = 0; foreach (var i in list.Where(x => x.Raw.Length == 0 && x.Name.Length == 0)) { k++; i.Name = "未命名图标 " + k; } }
            }
            foreach (var item in list)
            {
                item.Key = (overflow ? "o:" : "v:") + (item.Raw.Length > 0 ? item.Raw : "<无名称>") + "#" + item.Ordinal;
                if (TestNoIconCache) { item.Png = item.Capture ?? item.Snapshot; continue; }
                lock (iconCache)
                {
                    byte[] png; string ik = IconKey(item.Name);
                    if (item.Capture != null) { png = item.Capture; byte[] old; if (!iconCache.TryGetValue(ik, out old) || old.Length != png.Length) iconsDirty = true; iconCache[ik] = png; item.Png = png; }
                    else if (iconCache.TryGetValue(ik, out png)) item.Png = png;
                    else if (item.Snapshot != null) item.Png = item.Snapshot;
                }
            }
            return list;
        }

        static List<TrayItem> Read(AutomationElement root, bool overflow, bool capture, bool captureUnnamed = false)
        {
            var raws = new List<RawIcon>();
            foreach (var b in Icons(root))
            {
                string full; try { full = b.Current.Name ?? ""; } catch { continue; }
                byte[] png = null;
                if ((capture || (captureUnnamed && Clean(full).Length == 0)) && HitsItem(b)) png = Capture(b);
                raws.Add(new RawIcon { Full = full, Png = png });
            }
            return Build(raws, overflow, null);
        }

        /// <summary>The icons on the taskbar itself (cheap, no side effects). Icons are only copied from the screen when they really are visible there.</summary>
        internal static List<TrayItem> ReadVisible(bool capture)
        {
            lock (gate)
            {
                var tray = Tray();
                return tray == null ? new List<TrayItem>() : Read(tray, false, capture);
            }
        }

        /// <summary>The hidden icons. 2.3.4: the flyout is opened with an empty window region, so nothing appears on screen (it still takes the focus for a moment).
        /// Icon pictures can only be copied from pixels that are really shown, so an icon that has no picture yet makes ONE visible pass (a short flash, once per icon); after that the picture is remembered.</summary>
        internal static List<TrayItem> ReadOverflow(bool capture)
        {
            lock (gate)
            {
                LastProblem = null;
                var tray = Tray(); if (tray == null) { LastProblem = "找不到任务栏托盘"; return new List<TrayItem>(); }
                List<TrayItem> list = null; bool ours; AutomationElement w = null;
                for (int attempt = 0; attempt < 2 && w == null; attempt++)         // 2.3.6: one retry when the flyout does not come up
                {
                    LastProblem = null;
                    w = OpenOverflow(tray, out ours, true);
                    if (w == null) { Thread.Sleep(400); continue; }
                    try { list = Read(w, true, false, capture); }
                    finally { if (ours) CloseOverflow(tray); }
                }
                if (w == null || list == null) return new List<TrayItem>();
                if (capture)
                {
                    var missing = list.Where(i => i.Png == null && imageTried.Add(i.Key + "|" + IconKey(i.Name))).ToList();
                    if (missing.Count > 0)
                    {
                        VisiblePasses++;
                        bool ours2; var w2 = OpenOverflow(tray, out ours2, false);
                        if (w2 != null)
                        {
                            try { list = Read(w2, true, true); }
                            finally { if (ours2) CloseOverflow(tray); }
                        }
                    }
                }
                return list;
            }
        }

        static AutomationElement OpenOverflow(AutomationElement tray, out bool openedByUs, bool invisible)
        {
            openedByUs = false;
            var w = OverflowWindow(); if (w != null) return w;
            var chevron = Chevron(tray);
            if (chevron == null) { LastProblem = "找不到“显示隐藏的图标”按钮"; return null; }
            // 2.3.5: the flyout is drawn normally (the 2.3.4 empty-region trick is gone)
            try
            {
                try { ((InvokePattern)chevron.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); }
                catch (Exception e) { LastProblem = "无法展开隐藏的图标：" + e.Message; return null; }
                openedByUs = true; OverflowOpens++;
                // 2.3.6: wait until the flyout has icons AND their number has stopped changing (3 equal looks in a row), instead of taking the first look
                int last = -1, same = 0;
                for (int i = 0; i < 80; i++)
                {
                    Thread.Sleep(50);
                    w = OverflowWindow();
                    if (w == null) { last = -1; same = 0; continue; }
                    int c = Icons(w).Length;
                    if (c > 0 && c == last) same++; else same = 0;
                    last = c;
                    if (c > 0 && same >= 2) { Thread.Sleep(60); return w; }
                }
                if (w != null && last > 0) return w;          // still changing after 4 s: take what is there
                LastProblem = "隐藏的图标面板没有出现";
                if (w != null || OverflowWindow() != null) CloseOverflow(tray);
                return null;
            }
            finally { }
        }

        static void CloseOverflow(AutomationElement tray)
        {
            if (OverflowWindow() != null)
            {
                var chevron = Chevron(tray);
                try { if (chevron != null) ((InvokePattern)chevron.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); } catch { }
                for (int i = 0; i < 30 && OverflowWindow() != null; i++) Thread.Sleep(50);
            }
            UiaCom.ResetFlyoutNow();                 // 2.3.5: only heals a leftover empty region (none is made any more)
        }

        /// <summary>2.3.6: finds the icon by (name, ordinal): the n-th icon whose cleaned name is item.Raw (empty names included); when the order changed, the first with that name.</summary>
        static AutomationElement Find(AutomationElement root, TrayItem item)
        {
            string raw = item.Raw ?? Clean(item.Name); int n = 0; AutomationElement first = null;
            foreach (var b in Icons(root))
            {
                string full; try { full = b.Current.Name ?? ""; } catch { continue; }
                if (Clean(full) != raw) continue;
                if (n++ == item.Ordinal) return b;
                if (first == null) first = b;
            }
            return raw.Length > 0 ? first : null;
        }

        static AutomationElement TopOf(AutomationElement e)
        {
            var walker = TreeWalker.RawViewWalker; var root = AutomationElement.RootElement;
            for (int i = 0; e != null && i < 40; i++)
            {
                var parent = walker.GetParent(e);
                if (parent == null || Automation.Compare(parent, root)) return e;
                e = parent;
            }
            return null;
        }

        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);

        /// <summary>Is the icon really what is on screen at its position? UI Automation keeps reporting the icons of a taskbar that ClearDesk has clipped away (完全隐藏任务栏), so ask Windows what window is at that point
        /// (it honours window regions and other windows on top) and compare it with the icon's own top-level window.</summary>
        static bool HitsItem(AutomationElement el)
        {
            try
            {
                var r = el.Current.BoundingRectangle; if (r.IsEmpty || r.Width < 8 || r.Height < 8 || el.Current.IsOffscreen) return false;
                var top = TopOf(el); if (top == null) return false;
                var hwnd = new IntPtr(top.Current.NativeWindowHandle); if (hwnd == IntPtr.Zero) return false;
                var at = WindowFromPoint(new POINT { X = (int)(r.X + r.Width / 2), Y = (int)(r.Y + r.Height / 2) });
                if (at == IntPtr.Zero) return false;
                var root = GetAncestor(at, 2);       // GA_ROOT
                return root == hwnd || at == hwnd;
            }
            catch { }
            return false;
        }

        // ---- icon pictures are remembered between runs (a hidden taskbar cannot be copied from)
        static bool iconsDirty;
        internal static void LoadIcons(string path)
        {
            try
            {
                if (path == null || !File.Exists(path)) return;
                var map = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                lock (iconCache) foreach (var kv in map) if (!iconCache.ContainsKey(kv.Key) && !string.IsNullOrEmpty(kv.Value)) iconCache[kv.Key] = Convert.FromBase64String(kv.Value);
            }
            catch (Exception e) { Program.Log(e); }
        }
        internal static void SaveIcons(string path)
        {
            try
            {
                if (path == null || !iconsDirty) return;
                Dictionary<string, string> map;
                lock (iconCache) { map = iconCache.ToDictionary(kv => kv.Key, kv => Convert.ToBase64String(kv.Value)); iconsDirty = false; }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(map));
            }
            catch (Exception e) { Program.Log(e); }
        }
        internal static void TestPutIcon(string name, byte[] png) { lock (iconCache) { iconCache[name] = png; iconsDirty = true; } }
        internal static bool TestHasIcon(string name) { lock (iconCache) return iconCache.ContainsKey(name); }
        internal static void TestForgetIcons() { lock (iconCache) iconCache.Clear(); }

        static byte[] Capture(AutomationElement el)
        {
            try
            {
                var r = el.Current.BoundingRectangle;
                int w = (int)(r.Width * .58), h = (int)(r.Height * .58);
                if (w < 8 || h < 8) return null;
                int x = (int)(r.X + (r.Width - w) / 2), y = (int)(r.Y + (r.Height - h) / 2);
                using (var bmp = new Bitmap(w, h))
                {
                    using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, new Size(w, h));
                    using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); return ms.ToArray(); }
                }
            }
            catch { return null; }
        }

        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

        /// <summary>mode 0 = left click, 1 = right click, 2 = double click. Returns null when it worked, otherwise what went wrong (in Chinese, shown to the user).
        /// 2.3.4: the right click is handed to the program by the shell itself (UI Automation ShowContextMenu): no mouse, and a taskbar that ClearDesk has hidden stays hidden.
        /// Only when the shell refuses is the old way used (a real click on the icon, which brings the taskbar out for a moment).</summary>
        internal static string Click(TrayItem item, int mode)
        {
            lock (gate)
            {
                var tray = Tray(); if (tray == null) return "找不到任务栏托盘";
                AutomationElement el = null; bool ours = false; bool inFlyout = false;
                if (!item.Overflow) el = Find(tray, item);
                if (el == null)
                {
                    var w = OpenOverflow(tray, out ours, true);
                    if (w != null) { el = Find(w, item); inFlyout = el != null; }
                    if (el == null && item.Overflow) el = Find(tray, item);      // it was promoted onto the taskbar meanwhile
                }
                if (el == null) { if (ours) CloseOverflow(tray); return "没有找到“" + item.Name + "”，应用可能已经退出"; }
                string error = null;
                try
                {
                    if (mode == 1)
                    {
                        IntPtr root = inFlyout ? UiaCom.OverflowWindowHandle() : UiaCom.TrayWindow();
                        error = UiaCom.ShowContextMenu(root, item.Raw ?? Clean(item.Name), item.Ordinal);
                        if (error == null) LastRightClickMethod = "UI Automation ShowContextMenu";
                        else
                        {
                            RevealFallbacks++; LastRightClickMethod = "mouse click (fallback: " + error + ")";
                            if (inFlyout) { UiaCom.MakeVisible(UiaCom.OverflowWindowHandle()); Thread.Sleep(150); }
                            error = RightClick(el, inFlyout);
                        }
                    }
                    else
                    {
                        var invoke = (InvokePattern)el.GetCurrentPattern(InvokePattern.Pattern);
                        invoke.Invoke();
                        if (mode == 2) { Thread.Sleep(90); invoke.Invoke(); }
                    }
                }
                catch (Exception e) { error = "操作失败：" + e.Message; }
                if (ours)
                {
                    if (mode != 1) { Thread.Sleep(350); CloseOverflow(tray); }
                    else UiaCom.CloseWhenIdle(delegate { var t = Tray(); if (t != null) CloseOverflow(t); });
                }
                return error;
            }
        }

        static string RightClick(AutomationElement el, bool inFlyout)
        {
            if (!inFlyout && TopTaskbar.Active) { TopTaskbar.Hold(5000); Thread.Sleep(450); }       // the taskbar is hidden by ClearDesk: bring it out for the click
            else if (!inFlyout) Thread.Sleep(30);
            var r = el.Current.BoundingRectangle;
            for (int i = 0; i < 12; i++) { Thread.Sleep(60); var again = el.Current.BoundingRectangle; if (again == r) break; r = again; }      // the flyout / taskbar may still be sliding: click only where the icon has stopped
            if (r.IsEmpty) return "图标当前不可点击";
            int x = (int)(r.X + r.Width / 2), y = (int)(r.Y + r.Height / 2);
            for (int i = 0; i < 8 && !HitsItem(el); i++) Thread.Sleep(80);
            if (!HitsItem(el)) return "图标当前被遮住，无法发送右键（任务栏被完全隐藏时请先让任务栏显示，或把它放进隐藏的图标里）";
            POINT old; GetCursorPos(out old);
            SetCursorPos(x, y); Thread.Sleep(40);
            mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero); mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero);       // right down / up
            Thread.Sleep(120);
            SetCursorPos(old.X, old.Y);
            return null;
        }
    }
}
