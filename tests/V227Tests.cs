using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ClearDesk
{
    /// <summary>2.2.7: the folder widget's list / icon sizes adapt to width AND height and to the item count.</summary>
    internal static class V227Tests
    {
        internal static void Rules(Action<bool, string> check)
        {
            check(SettingsWindow.Version == "2.2.7", "2.2.7 version string");
            var m = FolderLogic.ListMetrics(255, 470, 7);          // the user's case: 275 x 520 widget, 7 items
            check(m.Icon >= 36 && m.Font >= 18 && m.Row * 7 >= 470 * .7 && m.Row * 7 <= 470, "2.2.7 list size: tall narrow widget with 7 items -> big rows that fill most of the height (" + m + ", " + (m.Row * 7) + " of 470)");
            var many = FolderLogic.ListMetrics(255, 470, 120);
            check(many.Icon == 16 && many.Font == 12 && many.Row == 26, "2.2.7 list size: many items -> base size and scrolling (" + many + ")");
            var narrow = FolderLogic.ListMetrics(140, 900, 3);
            check(narrow.Icon <= 140 / 6.0 + .5 && narrow.Icon >= 16, "2.2.7 list size: a narrow widget is limited by its width so names keep room (" + narrow + ")");
            var huge = FolderLogic.ListMetrics(2000, 2000, 2);
            check(huge.Icon == 48 && huge.Font == 22, "2.2.7 list size: upper limits 48 px icon / 22 px text (" + huge + ")");
            var tiny = FolderLogic.ListMetrics(100, 60, 5);
            check(tiny.Icon == 16 && tiny.Font == 12, "2.2.7 list size: lower limits 16 px icon / 12 px text (" + tiny + ")");
            bool mono = true; double last = 0;
            foreach (int h in new[] { 200, 300, 400, 500, 700, 900 }) { var x = FolderLogic.ListMetrics(400, h, 6); if (x.Icon < last) mono = false; last = x.Icon; }
            check(mono, "2.2.7 list size: taller -> never smaller");
            var wide = FolderLogic.ListMetrics(1200, 250, 7);
            check(wide.Icon < 30, "2.2.7 list size: a wide but low widget is limited by its height, not its width (" + wide + ")");
            double t1 = FolderLogic.TileMetrics(255, 470, 7, 44), t2 = FolderLogic.TileMetrics(255, 470, 200, 44), t3 = FolderLogic.TileMetrics(255, 250, 7, 44);
            check(t1 > 44 && t2 == 44 && t3 < t1, "2.2.7 icon view: tall widget with few items -> bigger tiles; many items -> base size (" + t1 + ", " + t2 + ", " + t3 + ")");
        }

        static string Temp(int n)
        {
            string d = Path.Combine(Path.GetTempPath(), "cd227-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(d);
            for (int i = 0; i < n; i++) File.WriteAllText(Path.Combine(d, "文件 " + (i + 1) + ".txt"), "x");
            return d;
        }

        static double ScreenH(FrameworkElement e, Window w) { return e.TransformToAncestor(w).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight)).Height; }

        internal static async Task Run(Action<bool, string> check)
        {
            var dirs = new List<string>(); FolderWindow w = null;
            try
            {
                string seven = Temp(7); dirs.Add(seven); string lots = Temp(150); dirs.Add(lots);
                w = (FolderWindow)Program.NewWidget("folder");
                w.SetFolder(seven);
                w.SetCustomSize(275, 520, false); await Task.Delay(500);
                var rows = w.Tiles.Children.OfType<Border>().ToList();
                double rowScreen = ScreenH(rows[0], w), used = rows.Count * rowScreen, avail = w.Scroller.ViewportHeight * w.ContentScale;
                var img = ((DockPanel)rows[0].Child).Children.OfType<Image>().First();
                double iconScreen = img.Width * w.ContentScale;
                check(iconScreen >= 30 && used >= avail * .65 && used <= avail + 1, "2.2.7 widget 275x520, 7 items: icon " + iconScreen.ToString("0") + " px, rows use " + used.ToString("0") + " of " + avail.ToString("0") + " px");
                var tb = ((DockPanel)rows[0].Child).Children.OfType<TextBlock>().First();
                check(tb.FontSize * w.ContentScale >= 17, "2.2.7 widget 275x520: text " + (tb.FontSize * w.ContentScale).ToString("0.#") + " px (2.2.6: about 9-13)");
                check(w.LastHeaderScale > 1.2, "2.2.7 title and header buttons grow too (" + w.LastHeaderScale.ToString("0.00") + ")");

                // live: lower the widget -> smaller again, without a refresh
                int r0 = w.Refreshes;
                w.SetCustomSize(275, 260, false); await Task.Delay(400);
                double icon2 = ((DockPanel)((Border)w.Tiles.Children[0]).Child).Children.OfType<Image>().First().Width * w.ContentScale;
                check(icon2 < iconScreen && w.Refreshes == r0, "2.2.7 sizes follow a resize live (" + iconScreen.ToString("0") + " -> " + icon2.ToString("0") + " px)");

                // many items: base size, scrollable
                w.SetCustomSize(275, 520, false); w.SetFolder(lots); await Task.Delay(700);
                check(w.LastList.Icon == 16 && w.Scroller.ScrollableHeight > 0, "2.2.7 150 items: base size (" + w.LastList + ") and scrolling");

                // icon view in a tall widget
                w.SetFolder(seven); w.SetView("icons"); await Task.Delay(500);
                double tileIcon = w.LastTileIcon;
                check(tileIcon > 44, "2.2.7 icon view, tall widget with 7 items: bigger icons (" + tileIcon + " px)");
                w.SetCustomSize(700, 260, false); await Task.Delay(400);
                check(w.LastTileIcon <= tileIcon, "2.2.7 icon view follows the size too (" + w.LastTileIcon + ")");
                w.SetView("list");
                w.SetCustomSize(275, 520, false); await Task.Delay(500);

                // screenshot of the list view (for the report)
                try
                {
                    string shot = Environment.GetEnvironmentVariable("CLEARDESK_SHOT");
                    if (!string.IsNullOrEmpty(shot))
                    {
                        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth), (int)Math.Ceiling(w.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        bmp.Render((System.Windows.Media.Visual)w.Content);
                        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                        using (var f = File.Create(shot)) enc.Save(f);
                        check(true, "INFO: screenshot " + shot);
                    }
                }
                catch (Exception e) { check(true, "INFO: screenshot failed " + e.Message); }
            }
            catch (Exception ex) { check(false, "2.2.7 folder size tests crashed: " + ex); }
            finally
            {
                if (w != null) try { string id = w.TestState.Id; w.Close(); ToolWidgets.ForgetData(id); Program.S.Widgets.Remove(id); } catch { }
                Program.Save(); await Task.Delay(200);
                foreach (var d in dirs) try { Directory.Delete(d, true); } catch { }
            }
        }
    }
}
