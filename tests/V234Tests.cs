using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClearDesk
{
    /// <summary>2.3.4: the 托盘图标 page in 快捷控制 (like 音量合成器) and right clicks that reach the program without the taskbar being shown.</summary>
    internal static class V234Tests
    {
        [DllImport("user32.dll")] static extern bool GetCursorPos(out Native.POINT p);
        static TrayItem T(string name, bool overflow) { return new TrayItem { Name = name, Tooltip = name + "\n提示", Overflow = overflow, Key = (overflow ? "o:" : "v:") + name + "#0" }; }

        internal static void Rules(Action<bool, string> check)
        {
            check(new Version(SettingsWindow.Version) >= new Version("2.3.4"), "2.3.4 version string (or later)");
            var m = typeof(IEl3_234).GetMethods();
            check(m.Length == 89 && m[m.Length - 1].Name == "ShowContextMenu", "2.3.4 IUIAutomationElement3.ShowContextMenu sits behind IUIAutomationElement (82 slots) and IUIAutomationElement2 (6 slots) (" + m.Length + " methods)");
            check(typeof(IUIA234).GetMethods().Length == 19 && typeof(IUIA234).GetMethods()[3].Name == "ElementFromHandle", "2.3.4 IUIAutomation vtable: ElementFromHandle is slot 6, CreateTrueCondition slot 21");
            check(UiaCom.Clean("QQ\r\n新消息 3 条") == "QQ" && UiaCom.Clean(null) == "" && UiaCom.Clean("  网易UU远程 ") == TrayAccess.Clean("  网易UU远程 "), "2.3.4 the icon name used to find the element is the same as the list's");
            check(UiaCom.ShowContextMenu(IntPtr.Zero, "nothing") != null, "2.3.4 no window, no menu (the call fails cleanly)");
        }

        static void Snap(GlassWindow w, string name)
        {
            string dir = Environment.GetEnvironmentVariable("CLEARDESK_SHOTS"); if (string.IsNullOrEmpty(dir)) return;
            try
            {
                Directory.CreateDirectory(dir); w.UpdateLayout();
                int px = Math.Max(1, (int)Math.Ceiling(w.ActualWidth)), py = Math.Max(1, (int)Math.Ceiling(w.ActualHeight)), scale = 2;     // 2x: a high-DPI look
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(px * scale, py * scale, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                var content = (Visual)w.Content; var dv = new DrawingVisual();
                using (var c = dv.RenderOpen())
                {
                    c.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(38, 44, 58)), null, new Rect(0, 0, px, py), 16, 16);
                    c.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, px, py));
                }
                bmp.Render(dv);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var f = File.Create(Path.Combine(dir, name + ".png"))) enc.Save(f);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        internal static async Task Run(Action<bool, string> check)
        {
            string scope = Program.S.TrayScope; bool area = Program.S.TrayArea;
            try
            {
                await PageModel(check);
                await Widget(check);
            }
            finally { Program.S.TrayScope = scope; Program.S.TrayArea = area; }
            await RealTray(check);
        }

        // the page with fake readers: listing, scopes, remembered icons, clicks
        static async Task PageModel(Action<bool, string> check)
        {
            string cache = Path.Combine(Path.GetTempPath(), "cd234-tray-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var toasts = new List<string>(); var clicks = new List<string>(); var counts = new List<int>(); var opened = new List<string>();
                var page = new TrayPage234(Dispatcher.CurrentDispatcher, delegate { }, u => opened.Add(u)) { CachePath = cache, AutoRead = false };
                page.Toast = (g, m) => toasts.Add(m); page.CountChanged = n => counts.Add(n);
                page.Clicker = (it, mode) => { clicks.Add(it.Name + ":" + mode); return it.Name == "gone" ? "没有找到“gone”，应用可能已经退出" : null; };
                Program.S.TrayArea = true; Program.S.TrayScope = "all";
                check(page.EmptyText == "" || page.RowCount == 0, "2.3.4 tray page: nothing listed before anything is read");
                page.Render(true);
                check(page.EmptyText.Contains("刷新"), "2.3.4 tray page: before the hidden icons are read it says how to read them (" + page.EmptyText + ")");
                var gone = T("gone", true); gone.Png = new byte[] { 1, 2, 3 };
                page.SetVisible(new List<TrayItem> { T("A", false), T("B", false) });
                check(page.RowNames.SequenceEqual(new[] { "A", "B" }) && page.Count == 2 && counts.Contains(2), "2.3.4 tray page: visible icons are listed before the hidden ones are read (count " + page.Count + ")");
                page.SetHidden(new List<TrayItem> { T("B", true), T("C", true), gone }, false);
                check(page.RowNames.SequenceEqual(new[] { "A", "B", "C", "gone" }) && page.TitleText == "托盘图标 · 4", "2.3.4 tray page: all = visible + hidden, an icon that is on the taskbar is listed once (" + page.TitleText + ")");
                Program.S.TrayScope = "hidden"; page.ApplySettings();
                check(page.RowNames.SequenceEqual(new[] { "C", "gone" }), "2.3.4 tray page scope 仅被收起的");
                Program.S.TrayScope = "visible"; page.ApplySettings();
                check(page.RowNames.SequenceEqual(new[] { "A", "B" }), "2.3.4 tray page scope 仅任务栏上可见的");
                Program.S.TrayScope = "all"; page.ApplySettings();
                check(File.Exists(cache), "2.3.4 the hidden icons are remembered between runs");
                var again = new TrayPage234(Dispatcher.CurrentDispatcher, delegate { }, u => { }) { CachePath = cache }; again.LoadCache(); again.SetVisible(new List<TrayItem>());
                check(again.RowNames.SequenceEqual(new[] { "B", "C", "gone" }) && again.Shown().First(i => i.Name == "gone").Png.SequenceEqual(new byte[] { 1, 2, 3 }), "2.3.4 ... and shown (with their images) right after a restart");
                page.SetVisible(new List<TrayItem> { T("A", false), T("C", false) });
                check(page.RowNames.Count(n => n == "C") == 1, "2.3.4 an icon promoted to the taskbar is not listed twice (" + string.Join(",", page.RowNames) + ")");
                page.SetVisible(new List<TrayItem> { T("A", false), T("B", false) });
                // rows
                page.TestClickRow(0, 0);
                check(await V233Tests.Until(() => clicks.Contains("A:0"), 1500), "2.3.4 left click on a row → the icon's left click");
                await V233Tests.Until(() => !page.Busy, 1000);
                page.TestClickRow(0, 1);
                check(await V233Tests.Until(() => clicks.Contains("A:1"), 1500), "2.3.4 right click on a row → the icon's own menu");
                await V233Tests.Until(() => !page.Busy, 1000); clicks.Clear();
                page.TestClickMore(1);
                check(await V233Tests.Until(() => clicks.Contains("B:1"), 1500), "2.3.4 the … button of a row opens the same menu as a right click");
                await V233Tests.Until(() => !page.Busy, 1000);
                page.TestClickRow(0, 2);
                check(await V233Tests.Until(() => clicks.Contains("A:2"), 1500), "2.3.4 double click on a row is forwarded as a double click");
                await V233Tests.Until(() => !page.Busy, 1000);
                page.Act(gone, 0, null);
                check(await V233Tests.Until(() => toasts.Count > 0, 1500) && await V233Tests.Until(() => !page.RowNames.Contains("gone"), 1500), "2.3.4 clicking an icon whose app has quit says so and removes it (" + string.Join(" / ", toasts) + ")");
                // empty states
                page.SetVisible(new List<TrayItem>()); page.SetHidden(new List<TrayItem>(), false);
                check(page.RowCount == 0 && page.EmptyText == "没有托盘图标。", "2.3.4 tray page: empty state (" + page.EmptyText + ")");
                // many icons: scrolls, one row each
                page.SetVisible(Enumerable.Range(0, 30).Select(i => T("n" + i, false)).ToList());
                check(page.RowCount == 30, "2.3.4 tray page: 30 icons = 30 rows (the list scrolls)");
                // polling gate
                int reads = 0; var gate = new TrayPage234(Dispatcher.CurrentDispatcher, delegate { }, u => { }) { CachePath = cache + ".x", AutoRead = false }; gate.ReadVisible = c => { reads++; return new List<TrayItem>(); }; gate.Polling = () => false;
                gate.PollVisible(); await Task.Delay(200);
                check(reads == 0, "2.3.4 the taskbar is not read while the widget is not visible");
                try { File.Delete(cache + ".x"); } catch { }
            }
            finally { try { File.Delete(cache); } catch { } }
        }

        static byte[] Icon(string letter, byte r, byte g, byte b)
        {
            using (var bmp = new System.Drawing.Bitmap(48, 48))
            {
                using (var gr = System.Drawing.Graphics.FromImage(bmp))
                {
                    gr.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; gr.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    using (var br = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(r, g, b))) gr.FillEllipse(br, 2, 2, 44, 44);
                    using (var f = new System.Drawing.Font("Segoe UI", 22, System.Drawing.FontStyle.Bold)) gr.DrawString(letter, f, System.Drawing.Brushes.White, 9, 5);
                }
                using (var ms = new MemoryStream()) { bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png); return ms.ToArray(); }
            }
        }

        // the widget: no strip on the main page, an entry that opens the page, a same-size transition, settings
        static async Task Widget(Action<bool, string> check)
        {
            bool oldQuick = Program.S.QuickControls;
            var q = Program.NewWidget("quick") as QuickControlWindow;
            if (q == null) { check(true, "2.3.4 tray page: quick widget not created — skipped"); return; }
            try
            {
                await Task.Delay(600);
                var names = new[] { "ChatGPT", "微信", "QQ", "NVIDIA 设置", "YueLink · 已连接 · 规则 · TUN", "ClearDesk · 你的桌面", "联想智能引擎", "网易UU远程", "Steam", "Wallpaper Engine" };
                var cols = new byte[][] { new byte[] { 16, 163, 127 }, new byte[] { 7, 193, 96 }, new byte[] { 18, 183, 245 }, new byte[] { 118, 185, 0 }, new byte[] { 90, 80, 200 }, new byte[] { 40, 120, 255 }, new byte[] { 220, 60, 60 }, new byte[] { 250, 140, 30 }, new byte[] { 30, 70, 110 }, new byte[] { 60, 60, 200 } };
                Func<int, bool, TrayItem> mk = (i, o) => new TrayItem { Name = names[i], Tooltip = names[i], Overflow = o, Key = (o ? "o:" : "v:") + names[i] + "#0", Png = Icon(names[i].Substring(0, 1), cols[i][0], cols[i][1], cols[i][2]) };
                Program.S.TrayArea = true; Program.S.TrayScope = "all";
                q.ApplyTraySetting();
                q.Tray.Polling = () => false; q.Tray.AutoRead = false;
                q.Tray.ReadVisible = c => new List<TrayItem>(); q.Tray.ReadHidden = c => new List<TrayItem>();
                q.Tray.SetVisible(new List<TrayItem> { mk(0, false), mk(1, false) });
                q.Tray.SetHidden(Enumerable.Range(2, 8).Select(i => mk(i, true)).ToList(), false);
                await Task.Delay(300);
                check(q.TrayEntryButton != null && q.TrayEntryButton.Visibility == Visibility.Visible && !q.TrayOpen, "2.3.4 the main page has a 托盘图标 entry and the page is closed");
                check(Math.Abs(q.Height - 329) < 1, "2.3.4 the 快捷控制 widget is 329 px tall with the entry (" + q.Height.ToString("0") + "), not the 367-499 px of 2.3.3's strip");
                check(q.Tray.Visibility == Visibility.Collapsed && !(q.Tray.Parent is StackPanel), "2.3.4 the icons are not on the main page any more (the page is beside the 声音 page)");
                Snap(q, "234-quick-main");
                // open the page
                q.TrayEntryButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                await Task.Delay(500);
                check(q.TrayOpen && q.Tray.Visibility == Visibility.Visible && q.Tray.PageOpen && Math.Abs(q.Height - 329) < 1, "2.3.4 the entry opens the 托盘图标 page at the same widget size (" + q.Height.ToString("0") + ")");
                q.UpdateLayout();
                Snap(q, "234-tray-page");
                var firstRow = (Border)((StackPanel)((StackPanel)((ScrollViewer)q.Tray.Children[1]).Content).Children[0]).Children[0];
                var face = (Border)((Grid)firstRow.Child).Children[0];
                check(firstRow.ActualHeight >= 44 && face.Child is Image && ((Image)face.Child).Width >= 26, "2.3.4 rows are " + firstRow.ActualHeight.ToString("0") + " px high with a " + ((FrameworkElement)face.Child).Width + " px icon (the old tiles were 24 px)");
                var scroller = (ScrollViewer)q.Tray.Children[1];
                check(scroller.ViewportHeight > 0 && scroller.ExtentHeight > scroller.ViewportHeight, "2.3.4 10 icons do not fit: the list scrolls (" + scroller.ExtentHeight.ToString("0") + " > " + scroller.ViewportHeight.ToString("0") + ")");
                scroller.ScrollToVerticalOffset(scroller.ScrollableHeight); await Task.Delay(80);
                Snap(q, "234-tray-page-scrolled");
                scroller.ScrollToVerticalOffset(0);
                q.ShowMixer(true); await Task.Delay(50);
                check(!q.MixerOpen && q.TrayOpen, "2.3.4 only one page is open at a time");
                // back
                q.ShowTray(false); await Task.Delay(450);
                check(!q.TrayOpen && q.Tray.Visibility == Visibility.Collapsed && !q.Tray.PageOpen, "2.3.4 ← goes back to the main page");
                // the volume mixer still works beside it
                q.ShowMixer(true); await Task.Delay(300); bool mixerOpened = q.MixerOpen; q.ShowMixer(false); await Task.Delay(300);
                check(mixerOpened && !q.MixerOpen, "2.3.4 the 音量合成器 page still opens and closes");
                // settings off / on
                q.ShowTray(true); await Task.Delay(300);
                Program.S.TrayArea = false; q.ApplyTraySetting(); await Task.Delay(300);
                check(!q.TrayOpen && q.TrayEntryButton.Visibility == Visibility.Collapsed && Math.Abs(q.Height - 285) < 1, "2.3.4 托盘图标 off: entry and page are gone, the widget is 285 px again (" + q.Height.ToString("0") + ")");
                Program.S.TrayArea = true; q.ApplyTraySetting(); await Task.Delay(200);
                check(q.TrayEntryButton.Visibility == Visibility.Visible && Math.Abs(q.Height - 329) < 1, "2.3.4 ... and back on (" + q.Height.ToString("0") + ")");
            }
            finally { q.Close(); Program.S.QuickControls = oldQuick; }
        }

        // a real program with its own tray icon: invisible flyout, right click through the shell without the mouse
        static async Task RealTray(Action<bool, string> check)
        {
            if (Program.Rendering && Environment.GetEnvironmentVariable("CLEARDESK_NO_TRAY_TEST") == "1") return;
            if (!TrayAccess.Available) { check(true, "2.3.4 real tray: no taskbar to read in this session — skipped"); return; }
            string tip = "ClearDesk托盘测试" + Guid.NewGuid().ToString("N").Substring(0, 6), log = Path.Combine(Path.GetTempPath(), "cd234-tray-" + Guid.NewGuid().ToString("N") + ".log"), ctl = log + ".ctl"; Process p = null;
            Func<string[]> lines = () => { try { return File.Exists(log) ? File.ReadAllLines(log) : new string[0]; } catch { return new string[0]; } };
            try
            {
                p = V233Tests.StartProcess("--tray-helper " + tip + " \"" + log + "\" \"" + ctl + "\" 90");
                check(await V233Tests.Until(() => File.Exists(ctl + ".ready"), 10000), "2.3.4 real tray: the helper program is up with its own tray icon");
                TrayItem item = null;
                for (int i = 0; i < 25 && item == null; i++) { item = (await Task.Run(() => TrayAccess.ReadVisible(false))).FirstOrDefault(x => x.Name.StartsWith(tip)); if (item == null) await Task.Delay(120); }
                if (item == null) item = (await Task.Run(() => TrayAccess.ReadOverflow(true))).FirstOrDefault(i => i.Name.StartsWith(tip));
                check(item != null, "2.3.4 real tray: the helper's icon is found (" + (item == null ? TrayAccess.LastProblem : (item.Overflow ? "in the hidden icons" : "on the taskbar")) + ")");
                if (item == null) return;
                // 1. reading the hidden icons draws nothing (the picture is known by now)
                if (item.Overflow)
                {
                    int passes = TrayAccess.VisiblePasses; int shown = 0, samples = 0; bool stop = false;
                    var sampler = new Thread(() => { var sw = Stopwatch.StartNew(); while (!stop && sw.ElapsedMilliseconds < 20000) { IntPtr h = UiaCom.OverflowWindowHandle(); samples++; if (h != IntPtr.Zero && UiaCom.HasRegion(h)) shown++; Thread.Sleep(1); } }) { IsBackground = true };
                    sampler.Start();
                    var list = await Task.Run(() => TrayAccess.ReadOverflow(true)); stop = true; sampler.Join(2000);
                    check(list.Any(x => x.Name.StartsWith(tip)), "2.3.4 real tray: the hidden icons are read again (" + list.Count + " icons)");
                    check(TrayAccess.VisiblePasses == passes, "2.3.4 real tray: no visible pass is needed once an icon's picture is remembered");
                    check(shown == 0, "2.3.5 real tray: while the hidden icons were read the flyout window never had a region of its own (" + shown + " of " + samples + " 1-ms samples) — nothing of the explorer is reshaped any more");
                    bool regionBack = false;
                    for (int i = 0; i < 40 && !regionBack; i++) { await Task.Delay(100); IntPtr h = UiaCom.OverflowWindowHandle(); regionBack = h == IntPtr.Zero || (!UiaCom.IsWindowVisible(h) && !UiaCom.HasRegion(h)); }
                    check(regionBack, "2.3.4 real tray: the flyout window has its normal shape back after it is closed (the user's own opening shows it)");
                }
                // 2. right click: no mouse, no taskbar
                var before = lines().Length; Native.POINT c0; GetCursorPos(out c0); int reveals = TrayAccess.RevealFallbacks;
                string err = await Task.Run(() => TrayAccess.Click(item, 1));
                bool menu = await V233Tests.Until(() => lines().Skip(before).Any(l => l == "right" || l == "menu-open"), 4000);
                Native.POINT c1; GetCursorPos(out c1);
                check(err == null && menu, "2.3.4 real tray: right click opens the program's own context menu (" + (err ?? string.Join(",", lines().Skip(before))) + ")");
                check((TrayAccess.LastRightClickMethod ?? "").Contains("ShowContextMenu") && TrayAccess.RevealFallbacks == reveals, "2.3.4 real tray: it was done by UI Automation ShowContextMenu, not by a mouse click on a revealed taskbar (" + TrayAccess.LastRightClickMethod + ")");
                check(Math.Abs(c0.X - c1.X) <= 1 && Math.Abs(c0.Y - c1.Y) <= 1, "2.3.4 real tray: the mouse pointer did not move at all");
                await Task.Delay(2800);       // the helper closes its menu by itself
                bool back = false;
                for (int i = 0; i < 40 && !back; i++) { await Task.Delay(100); IntPtr h = UiaCom.OverflowWindowHandle(); back = h == IntPtr.Zero || (!UiaCom.IsWindowVisible(h) && !UiaCom.HasRegion(h)); }
                check(back, "2.3.4 real tray: after the menu the flyout window is back to normal");
                Cmd(ctl, "exit");
                check(p.WaitForExit(8000), "2.3.4 real tray: the helper exits");
            }
            finally
            {
                try { if (p != null && !p.HasExited) p.Kill(); } catch { }
                TrayAccess.CloseFlyout();
                try { File.Delete(log); File.Delete(ctl); File.Delete(ctl + ".ready"); } catch { }
            }
        }

        static void Cmd(string ctl, string line) { File.AppendAllText(ctl, line + Environment.NewLine); }
    }
}
