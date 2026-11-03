using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ClearDesk
{
    /// <summary>2.2.8: no layout feedback loop while resizing the folder widget (jitter at the "just does not fit" height in 2.2.7).</summary>
    internal static class V228Tests
    {
        internal static void Rules(Action<bool, string> check)
        {
            check(SettingsWindow.Version.StartsWith("2.2."), "2.2.8 version string");
            bool mono = true; double last = 0;
            for (int h = 150; h <= 900; h++) { double s = FolderLogic.HeaderScale(275, h); if (s < last) mono = false; last = s; }
            check(mono, "2.2.8 header scale depends on the window size only and is monotonic in height");
            var a = new FolderLogic.ListSize { Icon = 20, Font = 13, Row = 29 }; var b = new FolderLogic.ListSize { Icon = 21, Font = 13.5, Row = 30 };
            check(FolderLogic.Hysteresis(a, b, 300, 301.5).Icon == 20, "2.2.8 hysteresis: a 1 px step that moves the list < 3 px keeps the size");
            check(FolderLogic.Hysteresis(a, new FolderLogic.ListSize { Icon = 24, Font = 15, Row = 35 }, 300, 301).Icon == 24, "2.2.8 hysteresis: a real change is applied");
            check(FolderLogic.Hysteresis(a, b, 300, 305).Icon == 21, "2.2.8 hysteresis: >= 3 px of height applies the 1 px step");
        }

        static string Temp(int n)
        {
            string d = Path.Combine(Path.GetTempPath(), "cd228-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(d);
            for (int i = 0; i < n; i++) File.WriteAllText(Path.Combine(d, "文件 " + (i + 1) + ".txt"), "x");
            return d;
        }

        static async Task Sweep(FolderWindow w, string name, int from, int to, int step, Action<bool, string> check)
        {
            double minH0 = w.MinHeight; bool sizeOk = true, mono = true, barOk = true; string bad = ""; double prev = double.MaxValue;
            for (int h = from; h >= to; h -= step)
            {
                w.SetCustomSize(275, h, false); await Task.Delay(45);
                if (Math.Abs(w.ActualHeight - h) > .6 || Math.Abs(w.TestState.CustomH - h) > .6 || w.MinHeight != minH0 || w.SizeToContent != SizeToContent.Manual) { sizeOk = false; if (bad == "") bad = h + ": " + w.ActualHeight.ToString("0.#"); }
                double v = w.IsList ? w.LastList.Icon : w.LastTileIcon;
                if (v > prev + .01) { mono = false; if (bad == "") bad = h + ": icon " + prev + " -> " + v; }
                prev = v;
                if (w.Scroller.VerticalScrollBarVisibility != ScrollBarVisibility.Hidden || w.Scroller.ComputedVerticalScrollBarVisibility == Visibility.Visible) barOk = false;
            }
            check(sizeOk, "2.2.8 " + name + ": 1 px steps " + from + "->" + to + ", the widget never changes its own size / MinHeight / SizeToContent " + bad);
            check(mono, "2.2.8 " + name + ": shrinking never makes the icons bigger (monotonic) " + bad);
            check(barOk, "2.2.8 " + name + ": no visible scrollbar at any height");
            // and back up
            prev = 0; mono = true;
            for (int h = to; h <= from; h += step)
            {
                w.SetCustomSize(275, h, false); await Task.Delay(45);
                double v = w.IsList ? w.LastList.Icon : w.LastTileIcon; if (v < prev - .01) mono = false; prev = v;
            }
            check(mono, "2.2.8 " + name + ": growing never makes the icons smaller (monotonic)");
        }

        static async Task Steady(FolderWindow w, string name, Action<bool, string> check)
        {
            await Task.Delay(300);
            int runs = w.MetricsRuns; double h = w.ActualHeight, icon = w.IsList ? w.LastList.Icon : w.LastTileIcon, hs = w.LastHeaderScale;
            await Task.Delay(600);
            check(w.MetricsRuns - runs <= 1 && w.ActualHeight == h && (w.IsList ? w.LastList.Icon : w.LastTileIcon) == icon && w.LastHeaderScale == hs,
                "2.2.8 " + name + ": no oscillation when left alone (" + (w.MetricsRuns - runs) + " recalcs in 600 ms)");
        }

        internal static async Task Run(Action<bool, string> check)
        {
            var dirs = new List<string>(); FolderWindow w = null;
            try
            {
                string seven = Temp(7); dirs.Add(seven);
                w = (FolderWindow)Program.NewWidget("folder");
                w.SetFolder(seven); w.SetView("list");
                w.SetCustomSize(275, 520, false); await Task.Delay(500);
                await Sweep(w, "list view, 7 items", 420, 190, 1, check);
                // critical height of the recording: where 7 rows just stop fitting at base size
                for (int h = 200; h <= 330; h += 5) { w.SetCustomSize(275, h, false); await Task.Delay(30); }
                await Steady(w, "list view at 330", check);
                for (int c = 230; c <= 300; c += 7) { w.SetCustomSize(275, c, false); await Steady(w, "list view at " + c, check); }

                // drag simulation: fast moves with jitter around the threshold, as mouse moves would arrive
                int r0 = w.MetricsRuns, moves = 0; var rnd = new Random(7);
                for (int i = 0; i < 200; i++) { w.SetCustomSize(275, 260 + rnd.Next(-6, 7), false); moves++; if (i % 4 == 0) await Task.Delay(4); }
                w.SetCustomSize(275, 262, false);
                await Steady(w, "list view after a jittery drag", check);
                check(w.MetricsRuns - r0 < moves / 2, "2.2.8 drag: recalculation is throttled (" + (w.MetricsRuns - r0) + " for " + moves + " moves)");
                check(Math.Abs(w.ActualHeight - 262) < .6, "2.2.8 drag: the widget ends at the size the user dragged to (" + w.ActualHeight + ")");

                w.SetView("icons"); w.SetCustomSize(275, 520, false); await Task.Delay(500);
                await Sweep(w, "icon view, 7 items", 420, 190, 2, check);
                for (int c = 230; c <= 330; c += 20) { w.SetCustomSize(275, c, false); await Steady(w, "icon view at " + c, check); }
                w.SetView("list");
            }
            catch (Exception ex) { check(false, "2.2.8 resize-loop tests crashed: " + ex); }
            finally
            {
                if (w != null) try { string id = w.TestState.Id; w.Close(); ToolWidgets.ForgetData(id); Program.S.Widgets.Remove(id); } catch { }
                Program.Save(); await Task.Delay(200);
                foreach (var d in dirs) try { Directory.Delete(d, true); } catch { }
            }
        }
    }
}
