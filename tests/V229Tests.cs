using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ClearDesk
{
    /// <summary>2.2.9: compact folder widget, editable launchpad, 以管理员身份运行.</summary>
    internal static class V229Tests
    {
        static bool Has(ContextMenu m, string h) { return m.Items.OfType<MenuItem>().Any(i => Equals(i.Header, h)); }
        static MenuItem Get(ContextMenu m, string h) { return m.Items.OfType<MenuItem>().FirstOrDefault(i => Equals(i.Header, h)); }

        internal static void Rules(Action<bool, string> check)
        {
            check(SettingsWindow.Version == "2.2.9", "2.2.9 version string");
            var c = LaunchpadStore.Compose(new[] { "a", "b", "c" }, new[] { "x", "b" }, new[] { "c", "y" });
            check(string.Join(",", c) == "x,b,a", "2.2.9 launchpad: pinned first in their saved order, then desktop items, removed ones hidden (" + string.Join(",", c) + ")");
            var pa = LaunchpadStore.PinArgs(new[] { "--pin-launchpad", @"C:\a b\x.exe", @"D:\y.lnk", "--other" }).ToList();
            check(pa.Count == 2 && pa[0] == @"C:\a b\x.exe", "2.2.9 --pin-launchpad takes every path after it");
            check(DesktopMenu.PinCommand(@"C:\CD\ClearDesk.exe") == "\"C:\\CD\\ClearDesk.exe\" --pin-launchpad \"%1\"", "2.2.9 classic Explorer verb command line");
            bool mono = true; double last = 0;
            for (int w = 120; w <= 600; w++) { double s = FolderLogic.CompactBarScale(w, 400); if (s < last || s < .9) mono = false; last = s; }
            check(mono, "2.2.9 compact bar scale grows with the width and never drops below 0.9");

            // 以管理员身份运行: classification
            string dir = Path.Combine(Path.GetTempPath(), "cd229a-" + Guid.NewGuid().ToString("N").Substring(0, 6)); Directory.CreateDirectory(dir);
            string exe = Path.Combine(dir, "tool.exe"), txt = Path.Combine(dir, "a.txt"), lnk = Path.Combine(dir, "app.lnk"), uwp = Path.Combine(dir, "store.lnk"), url = Path.Combine(dir, "x.url");
            foreach (var f in new[] { exe, txt, lnk, uwp, url }) File.WriteAllText(f, "x");
            var oldTarget = AdminRun.ShortcutTarget; var oldStart = AdminRun.Starter;
            try
            {
                AdminRun.ShortcutTarget = p => p == lnk ? exe : "";
                check(AdminRun.Supported(exe) && AdminRun.Supported(lnk), "2.2.9 runas offered for .exe and a shortcut to an .exe");
                check(!AdminRun.Supported(uwp) && !AdminRun.Supported("shell:AppsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App") && !AdminRun.Supported(dir) && !AdminRun.Supported(txt) && !AdminRun.Supported(url) && !AdminRun.Supported(SystemItems.RecycleBin.Path),
                    "2.2.9 runas hidden for store / UWP apps, folders, documents, links and system icons");
                ProcessStartInfo seen = null;
                AdminRun.Starter = psi => { seen = psi; return null; };
                check(AdminRun.Run(exe, true) && seen != null && seen.Verb == "runas" && seen.UseShellExecute && seen.FileName == exe, "2.2.9 runas: ShellExecute with the runas verb (fake starter, no UAC prompt)");
                AdminRun.Starter = psi => { throw new Win32Exception(1223); };
                check(!AdminRun.Run(exe), "2.2.9 runas: cancelling UAC (1223) is silent and returns false");
            }
            finally { AdminRun.ShortcutTarget = oldTarget; AdminRun.Starter = oldStart; try { Directory.Delete(dir, true); } catch { } }

            // classic Explorer verb, written to a throwaway key
            string root = @"Software\ClearDeskTest229\Classes";
            try
            {
                DesktopMenu.RegisterPin(@"C:\CD\ClearDesk.exe", root);
                using (var k = Registry.CurrentUser.OpenSubKey(root + @"\*\shell\" + DesktopMenu.PinVerb + @"\command"))
                    check(k != null && (string)k.GetValue("") == DesktopMenu.PinCommand(@"C:\CD\ClearDesk.exe"), "2.2.9 固定到启动台 registered for files");
                using (var k = Registry.CurrentUser.OpenSubKey(root + @"\Directory\shell\" + DesktopMenu.PinVerb))
                    check(k != null && (string)k.GetValue("MUIVerb") == "固定到启动台", "2.2.9 固定到启动台 registered for folders");
                DesktopMenu.UnregisterPin(root);
                using (var k = Registry.CurrentUser.OpenSubKey(root + @"\*\shell\" + DesktopMenu.PinVerb)) check(k == null, "2.2.9 固定到启动台 removed again");
            }
            finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\ClearDeskTest229", false); }
        }

        internal static async Task Run(Action<bool, string> check)
        {
            await Launchpad(check);
            await Menus(check);
            await Compact(check);
        }

        static async Task Launchpad(Action<bool, string> check)
        {
            string dir = Path.Combine(Path.GetTempPath(), "cd229l-" + Guid.NewGuid().ToString("N").Substring(0, 6)); Directory.CreateDirectory(dir);
            string a = Path.Combine(dir, "甲.txt"), b = Path.Combine(dir, "乙.txt"), cc = Path.Combine(dir, "丙.txt");
            foreach (var f in new[] { a, b, cc }) File.WriteAllText(f, "x");
            var savedItems = Program.S.LaunchpadItems.ToList(); var savedHidden = Program.S.LaunchpadHidden.ToList();
            try
            {
                LaunchpadStore.Pin(LaunchpadStore.PinArgs(new[] { "--pin-launchpad", a, b, cc }));
                check(LaunchpadStore.Paths().Contains(a) && LaunchpadStore.IsPinned(cc), "2.2.9 固定到启动台 (as from the Explorer menu) adds and saves the items");
                Program.Launchpad(); await Task.Delay(200);
                var pad = Program.App.Windows.OfType<LaunchpadWindow>().First();
                var menu = pad.BuildItemMenu(b);
                check(Has(menu, "从启动台移除") && Has(menu, "向前移动") && Has(menu, "向后移动") && Has(menu, "固定到 Dock") && Has(menu, "发送至桌面收纳"), "2.2.9 launchpad item menu: 从启动台移除 and reordering next to the existing entries");
                check(!Has(menu, "以管理员身份运行"), "2.2.9 launchpad: no runas for a document");
                Get(menu, "向前移动").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                var pinned = Program.S.LaunchpadItems; check(pinned.IndexOf(b) == pinned.IndexOf(a) - 1, "2.2.9 launchpad: reorder moves the item and saves the order");
                Get(pad.BuildItemMenu(b), "从启动台移除").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Task.Delay(100);
                check(!pad.TestPaths.Contains(b) && File.Exists(b), "2.2.9 从启动台移除 hides the entry live and never deletes the file");
                var blank = pad.TestBlankMenu(); var restore = Get(blank, "恢复已移除的项目");
                LaunchpadWindow.FillRestore(restore);
                var back = restore.Items.OfType<MenuItem>().FirstOrDefault(i => Equals(i.ToolTip, b));
                check(back != null, "2.2.9 blank-area menu lists the removed item under 恢复已移除的项目");
                if (back != null) back.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Task.Delay(100);
                check(pad.TestPaths.Contains(b) && !Program.S.LaunchpadHidden.Contains(b), "2.2.9 restoring brings the item back");
                // a desktop item is hidden (not unpinned) and can be restored with 全部恢复
                string desk = Program.DesktopItems().FirstOrDefault();
                if (desk != null)
                {
                    LaunchpadStore.Remove(desk); check(!LaunchpadStore.Paths().Contains(desk) && (File.Exists(desk) || Directory.Exists(desk) || desk.StartsWith("shell:")), "2.2.9 a desktop item is only hidden from the launchpad");
                    LaunchpadStore.Remove(a); LaunchpadStore.Restore(null); check(LaunchpadStore.Paths().Contains(desk), "2.2.9 全部恢复");
                }
                else check(true, "INFO: no desktop items on this PC; desktop hide check skipped");
                pad.Close();
            }
            catch (Exception ex) { check(false, "2.2.9 launchpad tests crashed: " + ex); }
            finally
            {
                foreach (var w in Program.App.Windows.OfType<LaunchpadWindow>().ToArray()) try { w.Close(); } catch { }
                Program.S.LaunchpadItems = savedItems; Program.S.LaunchpadHidden = savedHidden; Program.Save();
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        static async Task Menus(Action<bool, string> check)
        {
            string dir = Path.Combine(Path.GetTempPath(), "cd229m-" + Guid.NewGuid().ToString("N").Substring(0, 6)); Directory.CreateDirectory(dir);
            string exe = Path.Combine(dir, "tool.exe"); File.WriteAllText(exe, "x");
            var savedItems = Program.S.LaunchpadItems.ToList(); var old = AdminRun.Starter; OrganizerWindow ow = null;
            try
            {
                ProcessStartInfo seen = null; AdminRun.Starter = psi => { seen = psi; return null; };
                ow = new OrganizerWindow(new BoxData { Title = "测试收纳", Desktop = false, X = 60, Y = 60 }); ow.Show(); await Task.Delay(150);
                var m = ow.TestItemMenu(exe);
                var items = m.Items.OfType<MenuItem>().Select(i => i.Header as string).ToList();
                check(Has(m, "以管理员身份运行") && Has(m, "固定到启动台") && items.IndexOf("固定到启动台") == items.IndexOf("固定到 Dock") + 1, "2.2.9 organizer item menu: 以管理员身份运行, and 固定到启动台 right after 固定到 Dock");
                Get(m, "以管理员身份运行").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                check(seen != null && seen.Verb == "runas", "2.2.9 organizer: runas through ShellExecute (fake)");
                Get(m, "固定到启动台").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                check(LaunchpadStore.IsPinned(exe), "2.2.9 organizer: 固定到启动台 pins the item");
                check(!Has(ow.TestItemMenu(dir), "以管理员身份运行") && !Has(ow.TestItemMenu(SystemItems.RecycleBin.Path), "固定到启动台"), "2.2.9 organizer: no runas for folders, no launchpad pin for system icons");

                var dock = Program.App.Windows.OfType<DockWindow>().FirstOrDefault();
                if (dock != null)
                {
                    seen = null;
                    var dm = dock.TestEntryMenu(new DockEntry { PinPath = exe, Identity = new AppIdentity { Exe = exe, Name = "tool" } });
                    check(Has(dm, "以管理员身份运行"), "2.2.9 Dock: 以管理员身份运行 for a program");
                    Get(dm, "以管理员身份运行").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    check(seen != null && seen.Verb == "runas", "2.2.9 Dock: runas (fake)");
                    Program.S.LaunchpadItems.Remove(exe);
                    dm = dock.TestEntryMenu(new DockEntry { PinPath = exe, Identity = new AppIdentity { Exe = exe, Name = "tool" } });
                    check(Has(dm, "固定到启动台"), "2.2.9 Dock: 固定到启动台");
                    var uwpMenu = dock.TestEntryMenu(new DockEntry { PinPath = "shell:AppsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", Identity = new AppIdentity { Exe = "calc", Name = "计算器", AppId = "x" } });
                    check(!Has(uwpMenu, "以管理员身份运行"), "2.2.9 Dock: no runas for a store app");
                    check(Equals(((MenuItem)dm.Items[dm.Items.Count - 1]).Header, "关闭应用"), "2.2.9 Dock: 关闭应用 stays last");
                }
                else check(true, "INFO: no Dock window in this run; Dock menu checks skipped");
            }
            catch (Exception ex) { check(false, "2.2.9 menu tests crashed: " + ex); }
            finally
            {
                AdminRun.Starter = old; if (ow != null) try { ow.Close(); } catch { }
                Program.S.LaunchpadItems = savedItems; Program.Save();
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        static Rect Box(FrameworkElement e, Window w) { return e.TransformToAncestor(w).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight)); }

        static async Task Compact(Action<bool, string> check)
        {
            string dir = Path.Combine(Path.GetTempPath(), "cd229f-" + Guid.NewGuid().ToString("N").Substring(0, 6)); Directory.CreateDirectory(dir);
            foreach (var n in new[] { "000夸父", "001计划", "002总结", "003工作汇报", "004课程", "讲解词", "赛事", "展台使用指南" }) Directory.CreateDirectory(Path.Combine(dir, n));
            File.WriteAllText(Path.Combine(dir, "出差付款清单.xlsx"), "x");
            FolderWindow w = null;
            try
            {
                w = (FolderWindow)Program.NewWidget("folder");
                w.SetFolder(dir); w.SetView("list");
                check(w.Compact && w.TestMinW <= 120 && w.TestMinH <= 100, "2.2.9 folder widget: compact by default, minimum " + w.TestMinW + "x" + w.TestMinH + " (2.2.8: 228x168)");
                w.SetCustomSize(100, 80, false); await Task.Delay(300);
                check(Math.Abs(w.ActualWidth - 120) < .6 && Math.Abs(w.ActualHeight - 96) < .6, "2.2.9 can be dragged down to 120 px wide (" + w.ActualWidth + "x" + w.ActualHeight + ")");
                bool ok = true, mono = true, own = true; string bad = ""; double prev = 0;
                for (int width = 120; width <= 320; width += 2)
                {
                    w.SetCustomSize(width, 420, false); await Task.Delay(40);
                    if (Math.Abs(w.ActualWidth - width) > .6 || Math.Abs(w.ActualHeight - 420) > .6) { own = false; if (bad == "") bad = "size at " + width; }
                    var win = new Rect(0, 0, w.ActualWidth, w.ActualHeight);
                    var bv = Box(w.TestViewButton, w); var bo = Box(w.TestOpenButton, w); var bm = Box(w.TestMoreButton, w); var ti = Box(w.TestTitle, w); var sc = Box(w.Scroller, w);
                    bool inside = new[] { bv, bo, bm, ti }.All(r => r.Left >= -0.5 && r.Right <= win.Right + .5 && r.Bottom <= win.Bottom + .5);
                    bool apart = bv.Right <= bo.Left + .5 && bo.Right <= bm.Left + .5 && ti.Right <= bv.Left + 1;      // 标题 | 视图 打开 更多
                    bool below = ti.Top >= sc.Bottom - 1 && bv.Top >= sc.Bottom - 1;
                    if (!(inside && apart && below)) { ok = false; if (bad == "") bad = "bar at " + width + ": inside " + inside + " apart " + apart + " below " + below; }
                    if (w.LastList.Icon < prev - .01) { mono = false; if (bad == "") bad = "icon at " + width; }
                    prev = w.LastList.Icon;
                }
                check(own, "2.2.9 compact: widths 120-320 px, the widget keeps the size the user set " + bad);
                check(ok, "2.2.9 compact: title and buttons sit in the bottom bar, inside the widget, not overlapping each other or the list " + bad);
                check(mono, "2.2.9 compact: icons grow with the width (never shrink when widening) " + bad);
                w.SetCustomSize(120, 420, false); await Task.Delay(300);
                var sv = Box(w.Scroller, w);
                check(sv.Left <= 6 && w.ActualWidth - sv.Right <= 6, "2.2.9 compact at 120 px: content almost touches the edges (left " + sv.Left.ToString("0.#") + ", right " + (w.ActualWidth - sv.Right).ToString("0.#") + " px)");
                var name = w.Tiles.Children.OfType<Border>().Select(t => ((DockPanel)t.Child).Children.OfType<TextBlock>().First()).First();
                check(Box(name, w).Width >= 40, "2.2.9 compact at 120 px: names keep " + Box(name, w).Width.ToString("0") + " px");
                await Task.Delay(300); int runs = w.MetricsRuns; double ic = w.LastList.Icon; await Task.Delay(600);
                check(w.MetricsRuns - runs <= 1 && w.LastList.Icon == ic && Math.Abs(w.ActualWidth - 120) < .6, "2.2.9 compact at 120 px: steady, no oscillation");
                check(w.Scroller.VerticalScrollBarVisibility == ScrollBarVisibility.Hidden, "2.2.9 compact: scrollbar stays hidden");
                w.SetCustomSize(210, 470, false); await Task.Delay(400);
                check(w.LastList.Icon >= 28, "2.2.9 compact 210x470: icons " + w.LastList.Icon + " px, text " + w.LastList.Font + " px");
                Shot(w, "CLEARDESK_SHOT229", check);
                w.TestToggleCompact(); await Task.Delay(300);
                check(!w.Compact && Box(w.TestTitle, w).Top < Box(w.Scroller, w).Top, "2.2.9 compact mode can be switched off (title back at the top)");
                w.TestToggleCompact(); await Task.Delay(200);
            }
            catch (Exception ex) { check(false, "2.2.9 compact folder tests crashed: " + ex); }
            finally
            {
                if (w != null) try { string id = w.TestState.Id; w.Close(); ToolWidgets.ForgetData(id); Program.S.Widgets.Remove(id); } catch { }
                Program.Save(); await Task.Delay(200);
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        static Rect Shrink(Rect r) { r.Inflate(-1, -1); return r.IsEmpty ? new Rect() : r; }

        static void Shot(Window w, string env, Action<bool, string> check)
        {
            try
            {
                string shot = Environment.GetEnvironmentVariable(env);
                if (string.IsNullOrEmpty(shot)) return;
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth), (int)Math.Ceiling(w.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render((System.Windows.Media.Visual)w.Content);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var f = File.Create(shot)) enc.Save(f);
                check(true, "INFO: screenshot " + shot);
            }
            catch (Exception e) { check(true, "INFO: screenshot failed " + e.Message); }
        }
    }
}
