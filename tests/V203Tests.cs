using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClearDesk
{
    // 2.0.3: 组件管理, Dock click minimizes the foreground app, Dock auto-hide when covered.
    internal static class V203Tests
    {
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
            using (var f = File.Create(Path.Combine(Program.Base, "preview", name + ".png"))) encoder.Save(f);
        }

        public static void Rules(Action<bool, string> check)
        {
            IntPtr a = new IntPtr(11), b = new IntPtr(22), popup = new IntPtr(33), other = new IntPtr(44);
            var wins = new[] { a, b };
            Func<IntPtr, bool> none = h => false;
            Func<IntPtr, IntPtr> self = h => h == popup ? a : h;
            IntPtr t;
            check(DockClick.Decide(wins, b, a, none, self, out t) == DockClick.Action.Minimize && t == a, "dock click: app window that was foreground is minimized");
            check(DockClick.Decide(wins, b, popup, none, self, out t) == DockClick.Action.Minimize && t == a, "dock click: an owned dialog in front counts as the app being foreground");
            check(DockClick.Decide(wins, b, other, none, self, out t) == DockClick.Action.Activate && t == b, "dock click: app behind another window is brought forward (last active window)");
            check(DockClick.Decide(wins, b, a, h => h == a, self, out t) == DockClick.Action.Activate && t == b, "dock click: minimized app is restored, not minimized again");
            check(DockClick.Decide(wins, b, IntPtr.Zero, none, self, out t) == DockClick.Action.Activate, "dock click: unknown previous foreground activates");
            var dockRect = new Native.RECT { L = 600, T = 980, R = 1300, B = 1060 };
            check(DockAutoHide.Intersects(new Native.RECT { L = 1000, T = 300, R = 1500, B = 1000 }, dockRect), "auto-hide: a window reaching into the Dock overlaps");
            check(!DockAutoHide.Intersects(new Native.RECT { L = 0, T = 0, R = 1920, B = 980 }, dockRect) && !DockAutoHide.Intersects(new Native.RECT { L = 1300, T = 900, R = 1600, B = 1060 }, dockRect), "auto-hide: touching edges is not overlap");
            check(DockAutoHide.ShouldHide(true, true, false) && !DockAutoHide.ShouldHide(false, true, false) && !DockAutoHide.ShouldHide(true, false, false) && !DockAutoHide.ShouldHide(true, true, true), "auto-hide: hides only when enabled, overlapped and not in use by the pointer");
            check(WidgetManager.NotePreview("\n  第一行内容\n第二行") == "第一行内容" && WidgetManager.NotePreview("  ") == "（空白便签）" && WidgetManager.NotePreview(new string('字', 30)).EndsWith("…"), "manager: note preview uses the first non-empty line");
            check(WidgetManager.StatusText(false, true) == "已关闭" && WidgetManager.StatusText(true, true) == "已锁定" && WidgetManager.StatusText(true, false) == "显示中", "manager: status text");
        }

        public static async Task Run(DockWindow dock, Action<bool, string> check)
        {
            // --- widget manager model
            var list = WidgetManager.List();
            check(WidgetManager.Singletons.All(k => list.Any(e => e.Kind == k)) && list.Count(e => e.Kind == "box") == Program.S.Boxes.Count && list.Count(e => e.Kind == "note") == Program.S.Notes.Count, "组件管理 lists every singleton, note and 收纳格");
            var note = Program.NewNote();
            note.TestBox.Text = "周五交周报\n第二行";
            await Task.Delay(900);
            var ne = WidgetManager.List().Single(e => e.Kind == "note" && e.Data == note.Data);
            check(ne.Name == "便签 · 周五交周报" && ne.Status == "显示中", "note entry shows first-line preview and 显示中");
            WidgetManager.SetVisible(ne, false);
            await Task.Delay(100);
            check(note.WasClosed && Program.S.Notes.Contains(ne.Data) && !ne.Data.Enabled && ne.Data.Text.StartsWith("周五交周报"), "关闭 hides the note but keeps its text");
            Program.Rebuild(); await Task.Delay(200);
            check(!Program.OpenWindows.OfType<NoteWindow>().Any(w => w.Data == ne.Data), "a closed note stays hidden after rebuild/restart");
            dock = Program.OpenWindows.OfType<DockWindow>().FirstOrDefault() ?? dock;
            WidgetManager.SetVisible(ne, true);
            await Task.Delay(200);
            var shown = WidgetManager.Window(ne) as NoteWindow;
            check(shown != null && shown.IsVisible && shown.TestBox.Text.StartsWith("周五交周报"), "显示 restores the note with its text");
            var located = WidgetManager.Locate(ne);
            await Task.Delay(300);
            check(located == shown && shown.TestToast != null, "定位 flashes the widget with a toast");
            string ct = WidgetManager.ConfirmText(ne);
            check(ct.Contains("周五交周报") && ct.Contains("无法恢复") && ct.Contains("关闭"), "note delete confirmation names the lost text and points to 关闭");
            bool asked = false;
            check(!WidgetManager.Delete(ne, s => { asked = true; return false; }) && asked && Program.S.Notes.Contains(ne.Data), "cancelling delete keeps the note");
            check(WidgetManager.Delete(ne, s => true) && !Program.S.Notes.Contains(ne.Data), "删除 removes the note permanently");

            // 收纳格: rename live + delete keeps real files
            string temp = Path.Combine(Path.GetTempPath(), "cd203-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(temp, "keep me");
            Program.NewBox();
            var boxData = Program.S.Boxes.Last();
            boxData.Items.Add(temp); Program.Save();
            var be = WidgetManager.List().Single(e => e.Kind == "box" && e.Data == boxData);
            check(be.Detail.StartsWith("1 个项目"), "收纳格 entry shows its item count");
            WidgetManager.Rename(be, "工作资料", false);
            var ow = (OrganizerWindow)WidgetManager.Window(be);
            check(ow.TestHeading.EndsWith("工作资料") && boxData.Title == "工作资料", "rename is reflected on the 收纳格 title live");
            string bt = WidgetManager.ConfirmText(be);
            check(bt.Contains("1 个项目的引用") && bt.Contains("原文件和文件夹不会被删除"), "收纳格 delete confirmation states only references are removed");
            check(ow.ContextMenu.Items.OfType<MenuItem>().Any(m => Equals(m.Header, "删除收纳格")), "收纳格 right-click menu offers 删除收纳格");
            WidgetManager.Delete(be, s => true);
            await Task.Delay(100);
            check(!Program.S.Boxes.Contains(boxData) && ow.WasClosed && File.Exists(temp) && File.ReadAllText(temp) == "keep me", "deleting a 收纳格 never touches the referenced file");
            File.Delete(temp);

            // singleton close vs delete
            bool clockWas = Program.S.Clock;
            var ce = WidgetManager.List().Single(e => e.Kind == "clock");
            WidgetManager.SetVisible(ce, true); await Task.Delay(150);
            ce = WidgetManager.List().Single(e => e.Kind == "clock");
            check(ce.Visible && WidgetManager.Window(ce) != null, "显示 opens the clock");
            WidgetManager.SetVisible(ce, false); await Task.Delay(100);
            check(!Program.S.Clock && WidgetManager.Window(ce) == null && Program.S.Widgets.ContainsKey("clock"), "关闭 hides the clock but keeps its position");
            WidgetManager.Delete(ce, s => true);
            check(!Program.S.Widgets.ContainsKey("clock") && WidgetManager.List().Any(e => e.Kind == "clock" && !e.Visible), "删除 resets the clock's saved data; it can be added again");
            if (clockWas) Program.NewWidget("clock");
            Program.Save();

            // settings page
            var window = new SettingsWindow(delegate { });
            try
            {
                Program.NewNote().TestBox.Text = "示例便签：给设计稿留言";
                await Task.Delay(800);
                window.Show(); await Task.Delay(200);
                window.TestSelect(6); window.RefreshManager(); window.UpdateLayout();
                check(window.TestCards >= 4 && window.TestEntries.Count >= 8, "组件管理 page renders its groups");
                Render((FrameworkElement)window.Content, "settings-widget-manager");
                window.TestScrollEnd(); await Task.Delay(100);
                Render((FrameworkElement)window.Content, "settings-widget-manager-2");
                window.TestSearch("收纳"); window.UpdateLayout();
                check(window.TestCards >= 1, "search reaches 组件管理 rows");
                window.TestSearch(""); window.TestSelect(2); window.UpdateLayout();
                Render((FrameworkElement)window.Content, "settings-dock-autohide");
            }
            finally
            {
                window.Close();
                foreach (var n in Program.OpenWindows.OfType<NoteWindow>().Where(w => w.TestBox.Text.StartsWith("示例便签")).ToArray()) n.Delete(false);
            }

            // --- Dock auto-hide (deterministic, no real windows involved)
            dock = Program.OpenWindows.OfType<DockWindow>().FirstOrDefault();
            if (dock != null)
            {
                var ah = dock.TestAutoHide();
                bool was = Program.S.DockAutoHide;
                var probe = new Window { WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Title = "probe", Left = dock.Left, Top = dock.ShownTop, Width = 200, Height = 80 };
                probe.Show(); await Task.Delay(100);
                check(!ah.CandidateOverlaps(new System.Windows.Interop.WindowInteropHelper(probe).Handle, ah.ShownRect()), "auto-hide ignores ClearDesk's own windows");
                probe.Close();
                Program.S.DockAutoHide = true;
                ah.TestApply(true, false); await Task.Delay(350);
                check(ah.Hidden && dock.Top >= SystemParameters.PrimaryScreenHeight, "overlapped Dock slides below the screen");
                ah.TestApply(true, true); await Task.Delay(350);
                check(!ah.Hidden && Math.Abs(dock.Top - dock.ShownTop) < 1, "pointer at the bottom edge reveals the Dock");
                ah.TestApply(true, false); await Task.Delay(350);
                ah.TestApply(false, false); await Task.Delay(350);
                check(!ah.Hidden && Math.Abs(dock.Top - dock.ShownTop) < 1, "Dock returns when the overlap ends");
                Program.S.DockAutoHide = false;
                ah.TestApply(true, false);
                check(!ah.Hidden, "setting off never hides the Dock");
                Program.S.DockAutoHide = was;
            }
            check(!new Settings().DockAutoHide, "窗口遮挡时自动隐藏 defaults to off");
        }
    }
}
