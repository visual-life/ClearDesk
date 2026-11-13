using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Edge = ClearDesk.TopTaskbar.Edge;

namespace ClearDesk
{
    /// <summary>2.3.1: 完全隐藏任务栏 on every edge, the desktop-icons guard, one ClearDesk at a time, widgets down to 120 px, hideable search, smooth fold / 启动台.</summary>
    internal static class V231Tests
    {
        static TopTaskbar.RECT R(int l, int t, int r, int b) { return new TopTaskbar.RECT { L = l, T = t, R = r, B = b }; }
        static bool Same(TopTaskbar.RECT a, TopTaskbar.RECT b) { return a.L == b.L && a.T == b.T && a.R == b.R && a.B == b.B; }

        internal static void Rules(Action<bool, string> check)
        {
            check(new Version(SettingsWindow.Version) >= new Version("2.3.1"), "2.3.1 version string (or later)");
            check(!new Settings().HideSearch, "2.3.1 搜索 is shown by default");

            // ---- taskbar on any edge
            var mon = R(0, 0, 1920, 1080);
            check(TopTaskbar.EdgeOf(R(0, 1040, 1920, 1080), mon) == Edge.Bottom && TopTaskbar.EdgeOf(R(0, 0, 1920, 40), mon) == Edge.Top
                && TopTaskbar.EdgeOf(R(0, 0, 64, 1080), mon) == Edge.Left && TopTaskbar.EdgeOf(R(1856, 0, 1920, 1080), mon) == Edge.Right, "2.3.1 taskbar edge is recognised at bottom / top / left / right");
            check(TopTaskbar.EdgeOf(R(300, 300, 900, 360), mon) == Edge.None && TopTaskbar.EdgeOf(R(0, 0, 1920, 1080), mon) == Edge.None && TopTaskbar.EdgeOf(R(0, 1078, 1920, 1080 + 0), mon) == Edge.Bottom, "2.3.1 a floating or full-screen window is not taken for a taskbar");
            check(TopTaskbar.EdgeOf(R(0, 1078, 1920, 1118), mon) == Edge.None && TopTaskbar.EdgeOf(R(1918, 0, 1982, 1080), mon) == Edge.None, "2.3.1 a taskbar that Windows itself auto-hid (only 2 px on screen) is left to Windows");
            var cb = TopTaskbar.ClipRect(Edge.Bottom, 1920, 40, .25); var ct = TopTaskbar.ClipRect(Edge.Top, 1920, 40, .25);
            var cl = TopTaskbar.ClipRect(Edge.Left, 64, 1080, .25); var cr = TopTaskbar.ClipRect(Edge.Right, 64, 1080, .25);
            check(cb.SequenceEqual(new[] { 0, 30, 1920, 40 }) && ct.SequenceEqual(new[] { 0, 0, 1920, 10 }) && cl.SequenceEqual(new[] { 0, 0, 16, 1080 }) && cr.SequenceEqual(new[] { 48, 0, 64, 1080 }),
                "2.3.1 the visible slice stays against the screen edge on every edge");
            check(TopTaskbar.ClipRect(Edge.Bottom, 1920, 40, 0).SequenceEqual(new[] { 0, 40, 1920, 40 }) && TopTaskbar.ClipRect(Edge.Right, 64, 1080, 1).SequenceEqual(new[] { 0, 0, 64, 1080 }), "2.3.1 fraction 0 = nothing visible, 1 = all of it");
            var work = R(0, 0, 1920, 1040);
            check(Same(TopTaskbar.Claim(Edge.Bottom, mon, work), R(0, 0, 1920, 1080)) && Same(TopTaskbar.Claim(Edge.Left, mon, R(64, 0, 1920, 1080)), R(0, 0, 1920, 1080))
                && Same(TopTaskbar.Claim(Edge.Right, mon, R(0, 0, 1856, 1080)), R(0, 0, 1920, 1080)) && Same(TopTaskbar.Claim(Edge.Top, mon, R(0, 40, 1920, 1080)), R(0, 0, 1920, 1080)), "2.3.1 applications get the taskbar's strip back on every edge");
            check(Same(TopTaskbar.Reserve(Edge.Bottom, R(0, 1040, 1920, 1080), R(0, 0, 1920, 1080)), work) && Same(TopTaskbar.Reserve(Edge.Left, R(0, 0, 64, 1080), R(0, 0, 1920, 1080)), R(64, 0, 1920, 1080))
                && Same(TopTaskbar.Reserve(Edge.Right, R(1856, 0, 1920, 1080), R(0, 0, 1920, 1080)), R(0, 0, 1856, 1080)), "2.3.1 ... and the strip is taken out again on exit");
            var shifted = TopTaskbar.Shifted(Edge.Bottom, R(0, 0, 1920, 1040), work, R(0, 0, 1920, 1080));
            check(shifted.HasValue && Same(shifted.Value, R(0, 0, 1920, 1080)) && TopTaskbar.Shifted(Edge.Bottom, R(100, 100, 800, 600), work, R(0, 0, 1920, 1080)) == null
                && Same(TopTaskbar.Shifted(Edge.Left, R(64, 0, 1920, 1080), R(64, 0, 1920, 1080), R(0, 0, 1920, 1080)).Value, R(0, 0, 1920, 1080)), "2.3.1 a maximized window follows the work area on bottom / left; a normal window is left alone");
            check(TopTaskbar.Managed(Edge.Top, false) && !TopTaskbar.Managed(Edge.Bottom, false) && TopTaskbar.Managed(Edge.Bottom, true) && TopTaskbar.Managed(Edge.Left, true) && TopTaskbar.Managed(Edge.Right, true) && !TopTaskbar.Managed(Edge.None, true),
                "2.3.1 完全隐藏任务栏 manages every edge; the old 顶部自动隐藏 still only the top one");

            // ---- desktop icons: never restored to a state that was read from the live window
            var oh = DesktopIcons.Handle; var ov = DesktopIcons.IsVisible; var os = DesktopIcons.Show; var ow = DesktopIcons.WindowsHides;
            try
            {
                bool visible = false, windowsHides = false; var shows = new List<bool>();
                DesktopIcons.Handle = () => (IntPtr)77; DesktopIcons.IsVisible = h => visible; DesktopIcons.Show = (h, v) => { shows.Add(v); visible = v; }; DesktopIcons.WindowsHides = () => windowsHides;
                check(DesktopIcons.ShouldShow(false, false) && !DesktopIcons.ShouldShow(true, false) && !DesktopIcons.ShouldShow(false, true), "2.3.1 icons: shown again only when they are hidden although Windows shows them");
                visible = false; check(DesktopIcons.Heal("test") && visible && shows.SequenceEqual(new[] { true }), "2.3.1 icons left hidden by another ClearDesk are shown at start-up");
                check(!DesktopIcons.Heal("test") && shows.Count == 1, "2.3.1 ... and nothing is done when they are visible");
                windowsHides = true; visible = false; shows.Clear();
                check(!DesktopIcons.Heal("test") && shows.Count == 0, "2.3.1 icons the user turned off in Windows stay off");
                windowsHides = false; visible = false; shows.Clear(); DesktopIcons.Restore();
                check(shows.SequenceEqual(new[] { true }), "2.3.1 on exit the icons go to VISIBLE even if they are hidden right now (the 2.3.0 guard restored 'hidden' here: the bug)");
                windowsHides = true; visible = true; shows.Clear(); DesktopIcons.Restore();
                check(shows.SequenceEqual(new[] { false }), "2.3.1 on exit with Windows' own 'hide icons' on, they stay hidden");
            }
            finally { DesktopIcons.Handle = oh; DesktopIcons.IsVisible = ov; DesktopIcons.Show = os; DesktopIcons.WindowsHides = ow; }

            // ---- other instances: who counts
            check(OtherInstances.IsHelper("\"C:\\a\\ClearDesk.exe\" --guard 12 C:\\x.json") && OtherInstances.IsHelper("ClearDesk.exe --apply-update x") && !OtherInstances.IsHelper("\"C:\\a\\ClearDesk.exe\" --startup") && !OtherInstances.IsHelper(""),
                "2.3.1 the crash guard and the updater are not 'another ClearDesk'");
            check(OtherInstances.IsTest("ClearDesk.exe --preview") && OtherInstances.IsTest("ClearDesk.exe --regression-test") && OtherInstances.IsTest("ClearDesk.exe --perf231 x.json") && !OtherInstances.IsTest("ClearDesk.exe"), "2.3.1 test runs are not 'another ClearDesk'");
            var oldList = OtherInstances.List; var oldKill = OtherInstances.Kill;
            try
            {
                int me = Process.GetCurrentProcess().Id, session = Process.GetCurrentProcess().SessionId;
                var fake = new List<ProcInfo>
                {
                    new ProcInfo { Pid = me, Session = session, Path = @"C:\w\ClearDesk-2.3.1\ClearDesk.exe", CommandLine = "ClearDesk.exe" },
                    new ProcInfo { Pid = 5001, Session = session, Path = @"C:\w\ClearDesk-2.2.5\ClearDesk.exe", CommandLine = "\"C:\\w\\ClearDesk-2.2.5\\ClearDesk.exe\" --startup" },
                    new ProcInfo { Pid = 5002, Session = session, Path = @"C:\w\ClearDesk-2.2.5\ClearDesk.exe", CommandLine = "ClearDesk.exe --guard 5001 C:\\r.json" },
                    new ProcInfo { Pid = 5003, Session = session + 1, Path = @"C:\w\ClearDesk-2.2.5\ClearDesk.exe", CommandLine = "ClearDesk.exe" },
                    new ProcInfo { Pid = 5004, Session = session, Path = @"C:\w\ClearDesk-2.2.5\ClearDesk.exe", CommandLine = "ClearDesk.exe --preview" },
                };
                OtherInstances.List = () => fake;
                var found = OtherInstances.Find();
                check(found.Count == 1 && found[0].Pid == 5001, "2.3.1 only a real second ClearDesk of this Windows session is found (not me, not the guard, not another user's, not a test): " + string.Join(",", found.Select(f => f.Pid)));
                var gs = OtherInstances.GuardsOf(new[] { 5001 });
                check(gs.Count == 1 && gs[0].Pid == 5002 && OtherInstances.GuardsOf(new[] { 9 }).Count == 0, "2.3.1 the crash guard that belongs to a program is found by its pid");
                check(OtherInstances.Question(found, false).Contains("关闭") && OtherInstances.Describe(found).Contains("ClearDesk-2.2.5"), "2.3.1 the question names the older program and its folder");

                // Close: kills, waits for the old guard, THEN shows the icons
                var order = new List<string>(); int guardPolls = 0;
                var h0 = DesktopIcons.Handle; var v0 = DesktopIcons.IsVisible; var s0 = DesktopIcons.Show; var w0 = DesktopIcons.WindowsHides;
                try
                {
                    DesktopIcons.Handle = () => (IntPtr)1; DesktopIcons.IsVisible = h => false; DesktopIcons.Show = (h, v) => order.Add("show" + v); DesktopIcons.WindowsHides = () => false;
                    OtherInstances.Kill = p => { order.Add("kill" + p.Pid); return true; };
                    OtherInstances.List = () => { guardPolls++; return guardPolls <= 2 ? fake : fake.Where(p => p.Pid != 5002).ToList(); };
                    var failed = OtherInstances.Close(found, null, 5000);
                    check(failed.Count == 0 && order.SequenceEqual(new[] { "kill5001", "showTrue" }) && guardPolls >= 3, "2.3.1 closing the old one waits for its guard to finish and then shows the icons (" + string.Join(",", order) + ", polls " + guardPolls + ")");
                    order.Clear(); OtherInstances.Kill = p => false;
                    failed = OtherInstances.Close(found, null, 100);
                    check(failed.Count == 1 && order.Count == 0, "2.3.1 a program that cannot be closed (e.g. started as administrator) is reported, nothing else happens");
                }
                finally { DesktopIcons.Handle = h0; DesktopIcons.IsVisible = v0; DesktopIcons.Show = s0; DesktopIcons.WindowsHides = w0; }
            }
            finally { OtherInstances.List = oldList; OtherInstances.Kill = oldKill; }
        }

        internal static async Task Run(Action<bool, string> check)
        {
            await RealProcesses(check);
            await Search(check);
            await Sizes(check);
            await Fold(check);
            await LaunchpadOpen(check);
        }

        // real processes: a harmless copy of cmd.exe named ClearDesk.exe in a temp folder plays the "old version" and its crash guard. The real WMI list and the real Kill are used; scope = the temp folder.
        static async Task RealProcesses(Action<bool, string> check)
        {
            string dir = Path.Combine(Path.GetTempPath(), "cd231-inst-" + Guid.NewGuid().ToString("N").Substring(0, 6)); Directory.CreateDirectory(dir);
            Process old = null, guard = null;
            try
            {
                string exe = Path.Combine(dir, "ClearDesk.exe");
                File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe);
                Func<string, Process> start = a => Process.Start(new ProcessStartInfo(exe, a) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                old = start("/c ping -n 60 127.0.0.1 >nul");
                guard = start("/c ping -n 4 127.0.0.1 >nul & rem --guard " + old.Id + " x.json");
                await Task.Delay(800);
                var found = await Task.Run(() => OtherInstances.Find(dir));
                check(found.Count == 1 && found[0].Pid == old.Id, "2.3.1 (real processes) the older ClearDesk.exe is found by the real process list; its guard is not counted: " + string.Join(",", found.Select(f => f.Pid)));
                var gs = await Task.Run(() => OtherInstances.GuardsOf(new[] { old.Id }, dir));
                check(gs.Count == 1 && gs[0].Pid == guard.Id, "2.3.1 (real processes) its guard is found");
                var sw = Stopwatch.StartNew();
                var failed = await Task.Run(() => OtherInstances.Close(found, dir, 8000));
                bool oldGone = old.WaitForExit(2000);
                check(failed.Count == 0 && oldGone, "2.3.1 (real processes) the older ClearDesk.exe is closed");
                check(guard.HasExited && sw.ElapsedMilliseconds < 7500, "2.3.1 (real processes) Close waited until its guard had finished (" + sw.ElapsedMilliseconds + " ms)");
                check((await Task.Run(() => OtherInstances.Find(dir))).Count == 0, "2.3.1 (real processes) nothing is left afterwards");
            }
            finally
            {
                foreach (var p in new[] { old, guard }) { try { if (p != null && !p.HasExited) p.Kill(); } catch { } }
                await Task.Delay(300);
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        static async Task Search(Action<bool, string> check)
        {
            bool before = Program.S.HideSearch;
            OrganizerWindow o = null; LaunchpadWindow pad = null;
            try
            {
                Program.S.HideSearch = false;
                o = new OrganizerWindow(new BoxData { Title = "搜索测试", Desktop = false, X = 20, Y = 200, Width = 400, Height = 360 }); Program.AddWindow(o);
                pad = new LaunchpadWindow(); Program.AddWindow(pad); await Task.Delay(700);
                check(o.TestSearchVisible && pad.TestSearchVisible && o.TestSearchItem != null && o.TestSearchItem.IsChecked && pad.TestSearchItem != null && pad.TestSearchItem.IsChecked && o.ContextMenu.Items.Contains(o.TestSearchItem),
                    "2.3.1 search is shown, and both right-click menus have a ticked 显示搜索");
                o.TestSearchItem.IsChecked = false; o.TestSearchItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Task.Delay(150);
                check(Program.S.HideSearch && !o.TestSearchVisible && !pad.TestSearchVisible && !o.TestSearchItem.IsChecked && !pad.TestSearchItem.IsChecked, "2.3.1 unticking 显示搜索 in the 收纳格 menu hides it in both windows and saves the setting");
                string json = File.Exists(Program.Config) ? File.ReadAllText(Program.Config) : "";
                for (int i = 0; i < 20 && json.IndexOf("\"HideSearch\": true", StringComparison.OrdinalIgnoreCase) < 0 && json.IndexOf("\"HideSearch\":true", StringComparison.OrdinalIgnoreCase) < 0; i++) { await Task.Delay(100); json = File.Exists(Program.Config) ? File.ReadAllText(Program.Config) : ""; }
                check(json.IndexOf("HideSearch", StringComparison.OrdinalIgnoreCase) >= 0 && (json.Replace(" ", "").IndexOf("\"HideSearch\":true", StringComparison.OrdinalIgnoreCase) >= 0 || Program.Preview), "2.3.1 settings.json holds HideSearch=true" + (Program.Preview ? " (preview data folder)" : ""));
                pad.TestSearchItem.IsChecked = true; pad.TestSearchItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Task.Delay(150);
                check(!Program.S.HideSearch && o.TestSearchVisible && pad.TestSearchVisible, "2.3.1 ticking it in the 启动台 menu brings both searches back");
                Program.SetSearchHidden(true);
                var again = Program.S; check(again.HideSearch, "2.3.1 Program.SetSearchHidden sets the setting");
                var loaded = Program.S; // round trip through the serializer used for settings.json
                var ser = new System.Web.Script.Serialization.JavaScriptSerializer(); var back = ser.Deserialize<Settings>(ser.Serialize(Program.S));
                check(back.HideSearch, "2.3.1 HideSearch survives the settings serializer");
            }
            finally
            {
                Program.SetSearchHidden(before);
                if (o != null) o.Close(); if (pad != null) pad.Close();
            }
        }

        static IEnumerable<T> Down<T>(DependencyObject d) where T : DependencyObject
        {
            if (d == null) yield break;
            if (d is T) yield return (T)d;
            int n = (d is Visual || d is System.Windows.Media.Media3D.Visual3D) ? VisualTreeHelper.GetChildrenCount(d) : 0;
            for (int i = 0; i < n; i++) foreach (var c in Down<T>(VisualTreeHelper.GetChild(d, i))) yield return c;
        }

        /// <summary>Texts that stick out of the window or lie on top of each other (ignoring scrolling lists, where cutting off is the point).</summary>
        static string Problems(GlassWindow w)
        {
            var win = new Rect(0, 0, w.ActualWidth, w.ActualHeight); var items = new List<KeyValuePair<TextBlock, Rect>>(); string bad = "";
            foreach (var t in Down<TextBlock>(w))
            {
                if (string.IsNullOrWhiteSpace(t.Text) || !t.IsVisible || t.ActualWidth < 1 || t.Opacity < .05) continue;
                if (t.Text == "已添加" || t.Text == "已在桌面上" || t.FontFamily.Source.Contains("Script") || t.Text == "\uE710" || t.Text == "\uE8A7") continue;      // the toast is temporary; the clock's big day name is a watermark behind the time on purpose
                DependencyObject p = t; bool inScroll = false, inButtonLike = false;
                while (p != null && !(p is Window)) { if (p is ScrollViewer || p is ListBox || p is ScrollContentPresenter) inScroll = true; p = VisualTreeHelper.GetParent(p); }
                if (inScroll || inButtonLike) continue;
                Rect r; try { r = t.TransformToAncestor(w).TransformBounds(new Rect(0, 0, t.ActualWidth, t.ActualHeight)); } catch { continue; }
                if (r.Left < -1 || r.Top < -1 || r.Right > win.Width + 1 || r.Bottom > win.Height + 1) bad += " [outside: '" + (t.Text.Length > 12 ? t.Text.Substring(0, 12) : t.Text) + "' " + r.ToString() + "]";
                items.Add(new KeyValuePair<TextBlock, Rect>(t, r));
            }
            for (int i = 0; i < items.Count; i++)
                for (int j = i + 1; j < items.Count; j++)
                {
                    var a = items[i].Value; var b = items[j].Value; a.Inflate(-1, -1); b.Inflate(-1, -1);
                    if (a.Width > 0 && b.Width > 0 && a.IntersectsWith(b) && Rect.Intersect(a, b).Width * Rect.Intersect(a, b).Height > 6)
                        bad += " [overlap: '" + Short(items[i].Key.Text) + "' " + items[i].Value.ToString() + " / '" + Short(items[j].Key.Text) + "' " + items[j].Value.ToString() + "]";
                }
            return bad;
        }
        static string Short(string s) { return (s.Length > 10 ? s.Substring(0, 10) : s) + (s.Length < 3 ? "(U+" + ((int)s[0]).ToString("X4") + ")" : ""); }

        internal static void Shot(GlassWindow w, string name)
        {
            string dir = Environment.GetEnvironmentVariable("CLEARDESK_SHOTS");
            if (string.IsNullOrEmpty(dir)) return;
            try
            {
                Directory.CreateDirectory(dir); w.UpdateLayout();
                var bmp = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(w.ActualWidth)), Math.Max(1, (int)Math.Ceiling(w.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
                var content = (Visual)w.Content; var dv = new DrawingVisual();
                using (var c = dv.RenderOpen()) c.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, bmp.Width, bmp.Height));
                bmp.Render(dv);
                var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var f = File.Create(Path.Combine(dir, name + ".png"))) enc.Save(f);
            }
            catch (Exception e) { Program.Log(e); }
        }

        static async Task Sizes(Action<bool, string> check)
        {
            // every scalable widget goes down to the folder widget's size, and its texts neither leave the window nor sit on each other
            var summary = new List<string>(); var problems = new List<string>(); var all = new List<GlassWindow>();
            string tmp = Path.Combine(Path.GetTempPath(), "cd231-fold-" + Guid.NewGuid().ToString("N").Substring(0, 6)); Directory.CreateDirectory(tmp);
            var flags = new[] { Program.S.Clock, Program.S.Weather, Program.S.Music, Program.S.Calendar, Program.S.QuickControls, Program.S.Almanac, Program.S.Performance };
            try
            {
                foreach (var kind in new[] { "clock", "weather", "calendar", "almanac", "performance", "quick", "todo", "countdown", "schedule", "clipboard", "netspeed", "launcher", "album", "sunair", "folder", "battery", "music" })
                {
                    GlassWindow w = null;
                    try
                    {
                        w = Program.NewWidget(kind); if (w == null) { problems.Add(kind + ": not created"); continue; }
                        all.Add(w); await Task.Delay(500);
                        if (!w.Scalable) { problems.Add(kind + ": not scalable"); continue; }
                        if (kind == "folder") { foreach (var n in new[] { "资料", "项目", "总结" }) Directory.CreateDirectory(Path.Combine(tmp, n)); File.WriteAllText(Path.Combine(tmp, "清单.xlsx"), "x"); ((FolderWindow)w).SetFolder(tmp); await Task.Delay(300); }
                        Shot(w, "def-" + kind);
                        w.SetCustomSize(60, 40, false); await Task.Delay(350); w.UpdateLayout();
                        summary.Add(kind + " " + w.ActualWidth.ToString("0") + "x" + w.ActualHeight.ToString("0"));
                        if (w.ActualWidth > 121.5) problems.Add(kind + ": only down to " + w.ActualWidth.ToString("0.#") + " px wide");
                        string p = Problems(w); if (p.Length > 0) problems.Add(kind + p);
                        Shot(w, "min-" + kind);
                        w.SetCustomSize(180, 150, false); await Task.Delay(300); w.UpdateLayout();
                        string p2 = Problems(w); if (p2.Length > 0) problems.Add(kind + "@180" + p2);
                        Shot(w, "mid-" + kind);
                    }
                    catch (Exception e) { problems.Add(kind + ": " + e.Message); }
                }
                check(problems.Count == 0, "2.3.1 every widget shrinks to 120 px wide without text outside or overlapping" + (problems.Count > 0 ? ": " + string.Join(" | ", problems) : " (" + string.Join(", ", summary) + ")"));
            }
            finally
            {
                foreach (var w in all)
                {
                    try { var tw = w as ToolWidget; string id = tw == null ? null : tw.InstanceId; w.Close(); if (id != null) { ToolWidgets.ForgetData(id); Program.S.Widgets.Remove(id); } } catch { }
                }
                Program.S.Clock = flags[0]; Program.S.Weather = flags[1]; Program.S.Music = flags[2]; Program.S.Calendar = flags[3]; Program.S.QuickControls = flags[4]; Program.S.Almanac = flags[5]; Program.S.Performance = flags[6];
                Program.Save();
                try { Directory.Delete(tmp, true); } catch { }
            }

            // organizer (收纳格)
            OrganizerWindow o = null;
            try
            {
                bool hide = Program.S.HideSearch; Program.S.HideSearch = false;
                var box = new BoxData { Title = "收纳格测试", Desktop = false, X = 40, Y = 220, Width = 120, Height = 96 };
                o = new OrganizerWindow(box); o.Show(); await Task.Delay(500); o.UpdateLayout();
                check(Math.Abs(o.ActualWidth - 120) < .6 && Math.Abs(o.ActualHeight - 96) < .6, "2.3.1 收纳格 can be 120 x 96 (" + o.ActualWidth + "x" + o.ActualHeight + ")");
                check(o.Compact && o.TestHeaderHeight < 58 && !o.TestSearchVisible, "2.3.1 收纳格 at 120 px: thin header, no search button (" + o.TestHeaderHeight + ")");
                string pr = Problems(o); check(pr.Length == 0, "2.3.1 收纳格 at 120 px: nothing outside or overlapping" + pr);
                Shot(o, "min-organizer");
                o.Width = 330; o.UpdateLayout(); o.ApplyChrome(); await Task.Delay(150);
                check(!o.Compact && o.TestHeaderHeight >= 58 && o.TestSearchVisible, "2.3.1 收纳格 at 330 px: normal header and search again");
                Shot(o, "wide-organizer");
                Program.S.HideSearch = hide;
            }
            finally { if (o != null) o.Close(); }
        }

        static async Task Fold(Action<bool, string> check)
        {
            OrganizerWindow o = null;
            try
            {
                var box = new BoxData { Title = "折叠测试", Desktop = false, X = 40, Y = 220, Width = 360, Height = 380 };
                o = new OrganizerWindow(box); o.Show(); await Task.Delay(500);
                double full = o.ActualHeight;
                o.TestToggleCollapsed(); await Task.Delay(40);
                check(o.TestFoldRunning, "2.3.1 collapse runs as a frame animation (no per-frame window resize)");
                await Task.Delay(500);
                check(!o.TestFoldRunning && o.ActualHeight < full / 3 && box.Collapsed, "2.3.1 collapsed: window is only the header (" + o.ActualHeight + ")");
                o.TestToggleCollapsed(); await Task.Delay(600);
                check(!o.TestFoldRunning && Math.Abs(o.ActualHeight - full) < 1.5 && !box.Collapsed, "2.3.1 expanded: back to the saved height (" + o.ActualHeight + " vs " + full + ")");
                o.TestToggleCollapsed(); await Task.Delay(60); o.TestToggleCollapsed(); await Task.Delay(700);
                check(!o.TestFoldRunning && Math.Abs(o.ActualHeight - full) < 1.5, "2.3.1 toggling again in the middle of the animation ends in the right state (" + o.ActualHeight + ")");
            }
            finally { if (o != null) o.Close(); }
        }

        static async Task LaunchpadOpen(Action<bool, string> check)
        {
            LaunchpadWindow pad = null;
            try
            {
                pad = new LaunchpadWindow(); pad.Show();
                int total = pad.TestPaths.Count;
                await Task.Delay(900);
                check(!pad.TestRestPending || total <= LaunchpadWindow.FirstBatch || pad.TestGrid.ItemCount >= total, "2.3.1 启动台: after the animation all " + total + " items are in (first screen " + LaunchpadWindow.FirstBatch + " before it)");
            }
            finally { if (pad != null) pad.Close(); }
        }
    }
}
