using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ClearDesk
{
    internal static class V2Tests
    {
        // Pure logic (self-test).
        public static void Rules(Action<bool, string> check)
        {
            var none = new Rect[0];
            var screen = new[] { new Rect(0, 0, 1920, 1040) };
            var r = MagnetSnap.Snap(new Rect(6, 300, 200, 100), none, screen, 8);
            check(r.SnappedX && r.X == 0 && !r.SnappedY && r.Y == 300 && r.Guides.Count == 1, "magnet: snaps to work-area left edge within threshold and draws one guide");
            r = MagnetSnap.Snap(new Rect(1915 - 200 + 2, 300, 200, 100), none, screen, 8);
            check(r.SnappedX && r.X == 1720, "magnet: snaps right edge to work-area right edge");
            r = MagnetSnap.Snap(new Rect(30, 300, 200, 100), none, screen, 8);
            check(!r.SnappedX && r.X == 30 && r.Guides.Count == 0, "magnet: free placement beyond threshold is untouched");
            var other = new[] { new Rect(400, 100, 300, 200) };
            r = MagnetSnap.Snap(new Rect(705, 150, 200, 100), other, none, 8);
            check(r.SnappedX && r.X == 700, "magnet: left edge abuts neighbour's right edge");
            r = MagnetSnap.Snap(new Rect(403, 400, 200, 100), other, none, 8);
            check(r.SnappedX && r.X == 400 && r.Guides[0].A.X == 400, "magnet: aligns left edges with neighbour and guide spans both");
            r = MagnetSnap.Snap(new Rect(452 - 2, 400, 200, 100), other, none, 8);
            check(r.SnappedX && Math.Abs(r.X + 100 - 550) < 0.001, "magnet: aligns centres");
            r = MagnetSnap.Snap(new Rect(1000, 103, 200, 100), other, none, 8);
            check(r.SnappedY && r.Y == 100, "magnet: aligns top edges vertically");
            var row = new[] { new Rect(0, 100, 100, 100), new Rect(140, 100, 100, 100) }; // gap 40
            r = MagnetSnap.Snap(new Rect(283, 100, 100, 100), row, none, 8);
            check(r.SnappedX && r.X == 280 && r.Guides.Any(g => g.Gap), "magnet: equal spacing reproduces an existing 40px gap with gap guides");
            r = MagnetSnap.Snap(new Rect(283, 100, 100, 100), row, none, 0);
            check(!r.SnappedX, "magnet: zero threshold disables snapping");

            check(WidgetCatalog.Requested(new[] { "--new-widget", "note" }) == "note" && WidgetCatalog.Requested(new[] { "--startup", "--new-widget", "CLOCK" }) == "clock", "new-widget argument is parsed case-insensitively");
            check(WidgetCatalog.Requested(new[] { "--new-widget", "..\\evil" }) == null && WidgetCatalog.Requested(new[] { "--new-widget" }) == null && WidgetCatalog.Requested(null) == null, "unknown or missing widget kinds are rejected");
            check(WidgetCatalog.Kinds.Length == 17 && WidgetCatalog.Kinds[0].Key == "note" && WidgetCatalog.Kinds.All(k => WidgetCatalog.IsKnown(k.Key)), "widget catalog lists 17 kinds (9 + 8 tool widgets in 2.2.0) with 便签 first");
            check(DesktopMenu.Command(@"C:\A B\ClearDesk.exe", "note") == "\"C:\\A B\\ClearDesk.exe\" --new-widget note", "desktop menu command quotes the exe path");

            string root = @"Software\ClearDeskSelfTest\DesktopBackground\Shell\ClearDesk";
            try
            {
                DesktopMenu.Register(@"C:\x\ClearDesk.exe", root);
                using (var k = Registry.CurrentUser.OpenSubKey(root))
                {
                    var subs = Registry.CurrentUser.OpenSubKey(root + @"\shell").GetSubKeyNames();
                    using (var cmd = Registry.CurrentUser.OpenSubKey(root + @"\shell\01note\command"))
                        check((string)k.GetValue("MUIVerb") == DesktopMenu.Title && k.GetValue("SubCommands") != null && ((string)k.GetValue("Icon")).Contains("ClearDesk.exe") && subs.Length == WidgetCatalog.Kinds.Length && ((string)cmd.GetValue("")).EndsWith("--new-widget note"), "desktop menu registers cascading submenu with icon and one command per widget type");
                }
                DesktopMenu.Register(@"C:\y\ClearDesk.exe", root);
                using (var cmd = Registry.CurrentUser.OpenSubKey(root + @"\shell\01note\command"))
                    check(((string)cmd.GetValue("")).StartsWith("\"C:\\y\\"), "re-registering replaces the previous exe path");
                DesktopMenu.Unregister(root);
                check(!DesktopMenu.IsRegistered(root), "desktop menu unregisters cleanly");
            }
            finally
            {
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\ClearDeskSelfTest", false);
            }

            var s = new Settings();
            check(s.Magnet && s.DesktopMenu && s.Notes != null, "magnet and desktop-menu default on");
            s.Notes.Add(new BoxData { Text = "第一行\n第二行", NoteColor = "mint", Width = 300, Height = 220 });
            var t = new JavaScriptSerializer().Deserialize<Settings>(new JavaScriptSerializer().Serialize(s));
            check(t.Notes.Count == 1 && t.Notes[0].Text == "第一行\n第二行" && t.Notes[0].NoteColor == "mint" && t.Notes[0].Width == 300, "notes round-trip text, colour and size");
            var old = new JavaScriptSerializer().Deserialize<Settings>("{\"Clock\":true}");
            check(old.Notes != null && old.Magnet, "1.9.x settings without notes load with defaults");
            check(NoteWindow.NeedsConfirm("x") && !NoteWindow.NeedsConfirm("  \n") && !NoteWindow.NeedsConfirm(null), "only non-empty notes ask before deleting");
            check(new Settings().ModernMenu && new Settings().DesktopMenu, "Windows 11 top-level menu visibility defaults on");
            check(GlassWindow.LockActionText(false) == "锁定位置" && GlassWindow.LockActionText(true) == "解锁位置" && GlassWindow.LockStateText(true) == "位置已锁定" && GlassWindow.LockStateText(false) == "位置未锁定", "lock menu wording reflects state and action");
        }

        static double Lum(Color c)
        {
            Func<double, double> f = v => { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); };
            return 0.2126 * f(c.R) + 0.7152 * f(c.G) + 0.0722 * f(c.B);
        }
        static double Contrast(Color a, Color b) { double x = Lum(a), y = Lum(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }

        static string HeaderText(MenuItem m)
        {
            var p = m.Header as StackPanel;
            return p == null ? "" : string.Join("|", p.Children.OfType<TextBlock>().Select(x => x.Text));
        }

        static void Render(FrameworkElement element, string name)
        {
            element.UpdateLayout();
            int w = (int)Math.Ceiling(element.ActualWidth), h = (int)Math.Ceiling(element.ActualHeight);
            if (w <= 0 || h <= 0) return;
            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.Combine(Program.Base, "preview"));
            using (var f = File.Create(Path.Combine(Program.Base, "preview", name + ".png")))
                encoder.Save(f);
        }

        // UI behaviour (regression run, preview data only).
        public static async Task Run(GlassWindow organizer, Action<bool, string> check)
        {
            // lock toggle
            bool was = organizer.IsLocked;
            var item = organizer.TestLockItem;
            check(item != null && HeaderText(item) == "锁定位置|当前：位置未锁定" && !was, "widget menu shows lock action with current state");
            organizer.ContextMenu.PlacementTarget = organizer;
            organizer.ContextMenu.IsOpen = true;
            await Task.Delay(250);
            try { Render((FrameworkElement)organizer.ContextMenu, "menu-unlocked"); } catch { }
            organizer.ContextMenu.IsOpen = false;
            organizer.SetLocked(true);
            await Task.Delay(200);
            check(organizer.IsLocked && HeaderText(organizer.TestLockItem) == "解锁位置|当前：位置已锁定" && organizer.TestToast != null && organizer.TestToast.Opacity > 0, "locking updates menu wording and shows a toast");
            Render((FrameworkElement)organizer.Content, "widget-lock-toast");
            organizer.ContextMenu.IsOpen = true;
            await Task.Delay(250);
            try { Render((FrameworkElement)organizer.ContextMenu, "menu-locked"); } catch { }
            organizer.ContextMenu.IsOpen = false;
            await Task.Delay(1700);
            check(organizer.TestToast == null, "toast removes itself after ~1.5 s");
            organizer.SetLocked(false);
            await Task.Delay(50);
            check(!organizer.IsLocked, "unlock restores free movement");
            check(organizer.ContextMenu.Items.OfType<MenuItem>().Any(m => Equals(m.Tag, "new") && m.Items.Count == WidgetCatalog.Kinds.Length), "widget menu offers 新建组件 with every widget type");

            // notes
            int before = Program.S.Notes.Count;
            var note = (NoteWindow)Program.NewWidget("note");
            await Task.Delay(300);
            check(note != null && note.IsVisible && Program.S.Notes.Count == before + 1, "新建组件 → 便签 opens a new note window");
            note.TestBox.Text = "买牛奶\n周五交报告";
            await Task.Delay(900);
            var saved = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(Program.Config));
            check(saved.Notes.Any(n => n.Text == "买牛奶\n周五交报告"), "note text autosaves to settings");
            note.SetColor("sky");
            Render((FrameworkElement)note.Content, "note");
            var second = (NoteWindow)Program.NewWidget("note");
            await Task.Delay(200);
            check(Program.S.Notes.Count == before + 2 && second.Data != note.Data, "multiple notes are independent instances");
            check(second.Delete(true) && Program.S.Notes.Count == before + 1, "empty note deletes without confirmation");
            double w0 = note.Width;
            note.Close();
            await Task.Delay(100);
            check(Program.S.Notes.Count == before + 1, "closing the window keeps the note (persisted for restart)");
            var reopened = new NoteWindow(Program.S.Notes.Last());
            reopened.Show();
            await Task.Delay(200);
            check(reopened.TestBox.Text == "买牛奶\n周五交报告" && reopened.Data.NoteColor == "sky", "note restores text and colour");
            reopened.Delete(false);
            check(Program.S.Notes.Count == before, "删除便签 removes it from settings");

            // singleton widget via catalog
            bool clock = Program.S.Clock;
            var cw = Program.NewWidget("clock");
            await Task.Delay(200);
            check(cw is ClockWindow && Program.S.Clock, "新建组件 → 时钟 enables and shows the clock");
            var again = Program.NewWidget("clock");
            check(ReferenceEquals(again, cw), "creating an existing singleton widget reveals it instead of duplicating");
            cw.Close();
            Program.S.Clock = clock;

            // drag with snapping (programmatic)
            // Keep other widgets (e.g. a full-width Dock when many apps run) out of the snap test.
            var others = Program.OpenWindows.Where(w => w != organizer && w.IsVisible).ToList();
            foreach (var o in others) o.Hide();
            organizer.Left = 300; organizer.Top = 300;
            var area = SystemParameters.WorkArea;
            bool magnet = Program.S.Magnet;
            Program.S.Magnet = true;
            typeof(GlassWindow).GetField("dragOffset", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(organizer, new Vector(10, 10));
            organizer.DragTo(new Point(area.Left + 15, 310), false);
            check(Math.Abs(organizer.Left - area.Left) < 0.5 && GuideOverlay.VisibleCount >= 1, "dragging near the work-area edge snaps and shows a guide line");
            organizer.DragTo(new Point(area.Left + 15, 310), true);
            check(Math.Abs(organizer.Left - (area.Left + 5)) < 0.5 && GuideOverlay.VisibleCount == 0, "holding Alt places freely without guides");
            Program.S.Magnet = false;
            organizer.DragTo(new Point(area.Left + 15, 310), false);
            check(Math.Abs(organizer.Left - (area.Left + 5)) < 0.5, "磁吸对齐 off disables snapping");
            Program.S.Magnet = magnet;
            GuideOverlay.Hide();
            organizer.Left = 20; organizer.Top = 20;
            foreach (var o in others) o.Show();

            // Windows 11 top-level menu: status + moved-folder detection
            check(ModernMenu.Status(null) == ModernMenu.State.Off && ModernMenu.Status(Program.Base + "\\") == ModernMenu.State.On && ModernMenu.Status(@"D:\elsewhere\ClearDesk") == ModernMenu.State.Moved && ModernMenu.Status("") == ModernMenu.State.Moved, "top-level menu status distinguishes off / on / moved folder");
            check(ModernMenu.StatusText(ModernMenu.State.Off).Contains("显示更多选项") && ModernMenu.StatusText(ModernMenu.State.On).StartsWith("已启用"), "top-level menu status text");

            // Windows 11 menu style + Dock send-to-organizer
            check(MenuTheme.GlyphFor("新建组件") != null && MenuTheme.GlyphFor("发送至桌面收纳") != null && MenuTheme.GlyphFor("随便") == null, "known commands get Fluent icons");
            MenuTheme.Decorate(organizer.ContextMenu);
            check(organizer.ContextMenu.Items.OfType<MenuItem>().Where(m => m.Header is string).All(m => m.Icon != null || MenuTheme.GlyphFor((string)m.Header) == null) && organizer.TestLockItem.Icon != null, "widget menu items carry line icons");
            var lockBrush = (SolidColorBrush)((TextBlock)((StackPanel)organizer.TestLockItem.Header).Children[1]).Foreground;
            check(Contrast(lockBrush.Color, Color.FromRgb(249, 249, 249)) >= 4.5, "lock-state line keeps >=4.5:1 contrast on the light menu");
            organizer.SetLocked(true);
            lockBrush = (SolidColorBrush)((TextBlock)((StackPanel)organizer.TestLockItem.Header).Children[1]).Foreground;
            check(Contrast(lockBrush.Color, Color.FromRgb(249, 249, 249)) >= 4.5, "amber locked-state line keeps >=4.5:1 contrast on the light menu");
            organizer.SetLocked(false);
            string temp = Path.Combine(Path.GetTempPath(), "cd-send-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(temp, "x");
            try
            {
                var send = DockWindow.SendMenu(temp);
                send.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, send));
                check(send.Items.OfType<MenuItem>().Count() == Program.S.Boxes.Count + 1 && Equals(send.Items.OfType<MenuItem>().Last().Header, "新建收纳格并添加"), "Dock 发送至桌面收纳 lists every organizer plus a new one");
                var first = Program.S.Boxes[0];
                send.Items.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                check(first.Items.Contains(temp), "Dock send adds a reference to the chosen organizer");
                first.Items.Remove(temp);
                Program.Save();
            }
            finally { File.Delete(temp); }

            // launchpad materials
            var state = Program.Widget("launchpad", 0, 0, 880, 520);
            string oldMaterial = state.Material;
            state.Material = "liquid";
            var lp = new LaunchpadWindow();
            lp.Show();
            await Task.Delay(500);
            var materials = (MenuItem)lp.ContextMenu.Items[0];
            check(Equals(materials.Header, "材质设置") && Equals(((MenuItem)materials.Items[0]).Header, "玻璃 · 实时清透") && Equals(((MenuItem)materials.Items[1]).Header, "磨砂 · 实时模糊"), "launchpad background menu offers both materials");
            check(lp.CurrentMaterial == "liquid" && !lp.TestLiveFrost, "launchpad glass has no backdrop window");
            var lpHeaders = lp.ContextMenu.Items.OfType<MenuItem>().Select(m => m.Header as string).ToList();
            check(lpHeaders.SequenceEqual(new[] { "材质设置", "添加到启动台…", "打开设置" }), "launchpad menu keeps only 材质设置 / 添加 / 打开设置 (no widget items): " + string.Join(",", lpHeaders));
            Render((FrameworkElement)lp.Content, "launchpad-glass");
            lp.TestMaterial2("frosted");
            await Task.Delay(300);
            check(lp.CurrentMaterial == "frosted" && state.Material == "frosted" && state.MaterialSelected, "launchpad material persists in settings");
            check(lp.TestLiveFrost && lp.TestBackdropHandle != IntPtr.Zero && Native.IsWindowVisible(lp.TestBackdropHandle), "launchpad frosted uses a live compositor blur placed behind it");
            materials.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));
            check(((MenuItem)materials.Items[1]).IsChecked && !((MenuItem)materials.Items[0]).IsChecked, "launchpad material submenu shows a checkmark on the current material");
            Render((FrameworkElement)lp.Content, "launchpad-frosted");
            lp.Close();
            await Task.Delay(200);
            var lp2 = new LaunchpadWindow();
            bool preparedHidden = false;
            lp2.SourceInitialized += delegate { preparedHidden = lp2.TestLiveFrost && lp2.TestBackdropOpacity == 0f; };
            lp2.Show();
            check(preparedHidden, "frosted backdrop is prepared, fully transparent, before the launchpad is first painted");
            await Task.Delay(400);
            check(lp2.CurrentMaterial == "frosted" && lp2.TestLiveFrost, "reopened launchpad keeps frosted material");
            check(lp2.TestBackdropOpacity == 1f, "frosted backdrop fades in together with the content");
            IntPtr h = lp2.TestBackdropHandle;
            lp2.Close();
            await Task.Delay(200);
            check(h == IntPtr.Zero || !Native.IsWindowVisible(h), "closing launchpad releases its backdrop window");
            state.Material = oldMaterial;
        }
    }
}
