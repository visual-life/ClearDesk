using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ClearDesk
{
    /// <summary>2.3.0: 完全隐藏任务栏 and the organizer's 贴边隐藏.</summary>
    internal static class V230Tests
    {
        internal static void Rules(Action<bool, string> check)
        {
            check(SettingsWindow.Version == "2.3.0", "2.3.0 version string");
            check(!new Settings().TopTaskbarFullHide && !new BoxData().EdgeHide, "2.3.0 完全隐藏任务栏 and 贴边隐藏 are off by default");
            // full hide: the edge never reveals, tray flyouts never reveal; only Start / search does
            check(!TopTaskbar.ShouldReveal(true, true, false, false), "2.3.0 full hide: the top edge does not wake the taskbar");
            check(!TopTaskbar.ShouldReveal(true, false, true, false), "2.3.0 full hide: other shell surfaces (Alt+Tab, flyouts) do not reveal it");
            check(TopTaskbar.ShouldReveal(true, false, true, true), "2.3.0 full hide: the Windows key / Ctrl+Esc (Start or search in front) reveals it");
            check(TopTaskbar.ShouldReveal(false, true, false, false) && TopTaskbar.ShouldReveal(false, false, true, false), "2.3.0 auto-hide (old mode) unchanged: edge dwell or a shell surface reveals");
            check(TopTaskbar.ShouldStay(true, false, false, true, false) && !TopTaskbar.ShouldStay(true, false, false, false, false), "2.3.0 full hide: stays while Start is open, hides again when it closes");
            check(TopTaskbar.ShouldStay(true, true, false, false, false) && TopTaskbar.ShouldStay(true, false, true, false, false), "2.3.0 full hide: a revealed taskbar stays while the pointer is on it or a flyout opened from it is in front");

            var screen = new Rect(0, 0, 1600, 900);
            check(EdgeHider.SideOf(new Rect(0, 200, 300, 400), screen) == EdgeSide.Left && EdgeHider.SideOf(new Rect(1290, 200, 300, 400), screen) == EdgeSide.Right
                && EdgeHider.SideOf(new Rect(500, 3, 300, 400), screen) == EdgeSide.Top && EdgeHider.SideOf(new Rect(500, 500, 300, 400), screen) == EdgeSide.Bottom
                && EdgeHider.SideOf(new Rect(500, 200, 300, 400), screen) == EdgeSide.None, "2.3.0 贴边隐藏: the nearest edge within 14 px is chosen; elsewhere nothing happens");
            var t = EdgeHider.Tucked(new Rect(0, 200, 300, 400), screen, EdgeSide.Left);
            var r = EdgeHider.Tucked(new Rect(1300, 200, 300, 400), screen, EdgeSide.Right);
            var b = EdgeHider.Tucked(new Rect(500, 500, 300, 400), screen, EdgeSide.Bottom);
            check(t == new Point(-294, 200) && r == new Point(1594, 200) && b == new Point(500, 894), "2.3.0 贴边隐藏: tucked away, 6 px stay on screen (" + t + " / " + r + " / " + b + ")");
            check(EdgeHider.Hold(false, false, false, false, 100) && !EdgeHider.Hold(false, false, false, false, 700) && EdgeHider.Hold(false, true, false, false, 9999) && EdgeHider.Hold(false, false, true, false, 9999) && EdgeHider.Hold(false, false, false, true, 9999),
                "2.3.0 贴边隐藏: stays out during the 600 ms delay, while dragging, while the menu is open and while files are dragged in");
        }

        internal static async Task Run(Action<bool, string> check)
        {
            OrganizerWindow w = null;
            try
            {
                var box = new BoxData { Title = "贴边测试", Desktop = false, X = 0, Y = 200, Width = 300, Height = 400 };
                w = new OrganizerWindow(box); w.Show(); await Task.Delay(300);
                check(w.TestEdgeItem != null && Equals(w.TestEdgeItem.Header, "贴边隐藏") && w.ContextMenu.Items.Contains(w.TestEdgeItem) && !w.TestEdgeItem.IsChecked, "2.3.0 organizer menu has 贴边隐藏 (off)");
                Point? pointer = new Point(900, 600); var screen = new Rect(0, 0, 1600, 900);
                w.SetEdgeHide(true);
                var e = w.TestEdge; e.TestPointer = () => pointer; e.TestScreen = () => screen;
                int restores = w.PositionRestores; double W = w.ActualWidth;
                await Task.Delay(1100);
                check(Math.Abs(w.Left - (6 - W)) < .6 && box.X == 0 && box.Y == 200, "2.3.0 贴边隐藏 left: tucked to " + w.Left.ToString("0.#") + " (6 px visible), saved position stays 0,200");
                pointer = new Point(2, 300); await Task.Delay(450);
                check(Math.Abs(w.Left) < .6, "2.3.0 pointer on the strip: slides out (" + w.Left.ToString("0.#") + ")");
                pointer = new Point(900, 600); await Task.Delay(350);
                check(Math.Abs(w.Left) < .6, "2.3.0 pointer gone: stays out during the delay");
                await Task.Delay(700);
                check(w.Left < -W / 2, "2.3.0 ... then slides back");
                // the menu keeps it out
                pointer = new Point(2, 300); await Task.Delay(400); pointer = new Point(900, 600);
                w.ContextMenu.PlacementTarget = w; w.ContextMenu.IsOpen = true; await Task.Delay(1200);
                check(Math.Abs(w.Left) < .6, "2.3.0 an open right-click menu keeps it out");
                w.ContextMenu.IsOpen = false; await Task.Delay(1200);
                check(w.Left < -W / 2, "2.3.0 menu closed: slides back");
                // files dragged onto the strip
                e.Dropping = true; await Task.Delay(400);
                check(Math.Abs(w.Left) < .6, "2.3.0 dragging files onto the strip slides it out");
                await Task.Delay(1000); check(Math.Abs(w.Left) < .6, "2.3.0 ... and it stays out while the files are over it");
                e.Dropping = false; await Task.Delay(1200);
                check(w.Left < -W / 2, "2.3.0 drop finished: slides back");
                // right edge
                box.X = 1600 - W; w.MoveQuietly(box.X, box.Y); await Task.Delay(1200);
                check(Math.Abs(w.Left - 1594) < .6 && Math.Abs(box.X - (1600 - W)) < .01, "2.3.0 贴边隐藏 right: tucked to " + w.Left.ToString("0.#"));
                // away from the edges: nothing happens
                box.X = 600; w.MoveQuietly(600, 200); await Task.Delay(1000);
                check(Math.Abs(w.Left - 600) < .6, "2.3.0 not near an edge: stays where it is");
                check(w.PositionRestores == restores, "2.3.0 the position guard never fights the slides (" + (w.PositionRestores - restores) + ")");
                box.X = 0; w.MoveQuietly(0, 200); await Task.Delay(1100);
                w.SetEdgeHide(false); await Task.Delay(200);
                check(!box.EdgeHide && Math.Abs(w.Left) < .6 && Math.Abs(w.Top - 200) < .6, "2.3.0 switching off puts it back at the saved position");
                w.SetEdgeHide(true); w.TestEdge.TestPointer = () => pointer; w.TestEdge.TestScreen = () => screen; await Task.Delay(1100);
                w.TestEdge.Finish();
                Program.Save();
                check(box.EdgeHide && box.X == 0 && box.Y == 200, "2.3.0 saving while tucked keeps the expanded position");
            }
            catch (Exception ex) { check(false, "2.3.0 贴边隐藏 tests crashed: " + ex); }
            finally { if (w != null) try { w.Close(); } catch { } await Task.Delay(100); }
        }
    }
}
