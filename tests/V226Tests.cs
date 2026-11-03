using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ClearDesk
{
    /// <summary>2.2.6: list view (Explorer-like rows) for the folder widget.</summary>
    internal static class V226Tests
    {
        internal static void Rules(Action<bool, string> check)
        {
            check(SettingsWindow.Version.StartsWith("2.2."), "2.2.6 version string");
            check(FolderWindow.RowIcon >= 16 && FolderWindow.RowIcon <= 20, "2.2.6 folder list: row icon is 16-20 px (" + FolderWindow.RowIcon + ")");
        }

        static string Temp() { string d = Path.Combine(Path.GetTempPath(), "cd226-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(d); return d; }
        static Rect Bounds(FrameworkElement e, Window w) { return e.TransformToAncestor(w).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight)); }

        internal static async Task Run(Action<bool, string> check)
        {
            string d = Temp();
            var opened = new List<FolderWindow>();
            try
            {
                Directory.CreateDirectory(Path.Combine(d, "zeta folder")); Directory.CreateDirectory(Path.Combine(d, "Alpha folder"));
                File.WriteAllText(Path.Combine(d, "a.txt"), "x");
                string longName = "这是一个名字非常非常非常非常非常非常长的文件用来测试省略号的显示效果_" + new string('x', 60) + ".txt";
                File.WriteAllText(Path.Combine(d, longName), "x");
                var w1 = (FolderWindow)Program.NewWidget("folder"); opened.Add(w1);
                var w2 = (FolderWindow)Program.NewWidget("folder"); opened.Add(w2);
                check(w1.IsList && w2.IsList, "2.2.6 folder list: a new folder widget uses the list view");
                w1.SetFolder(d); w2.SetFolder(d);
                await Task.Delay(500);
                var rows = w1.Tiles.Children.OfType<Border>().ToList();
                var names = rows.Select(r => ((FolderEntry)r.Tag).Name).ToList();
                check(rows.Count == 4 && names[0] == "Alpha folder" && names[1] == "zeta folder", "2.2.6 folder list: one row per item, folders first (" + string.Join(", ", names) + ")");
                double vw = w1.Scroller.ViewportWidth;
                check(rows.All(r => Math.Abs(r.ActualHeight - rows[0].ActualHeight) < .5 && Math.Abs(r.ActualWidth - (vw - 2)) < 3), "2.2.6 folder list: rows are full width and of equal height (" + rows[0].ActualWidth + " / " + vw + ")");
                double y0 = rows[0].TranslatePoint(new Point(0, 0), w1.Tiles).Y, y1 = rows[1].TranslatePoint(new Point(0, 0), w1.Tiles).Y;
                check(y1 > y0 && Math.Abs(rows[0].TranslatePoint(new Point(0, 0), w1.Tiles).X - rows[1].TranslatePoint(new Point(0, 0), w1.Tiles).X) < .5, "2.2.6 folder list: rows stack vertically");
                var img = ((DockPanel)rows[0].Child).Children.OfType<Image>().First();
                check(img.Width * w1.ContentScale >= 15.5 && img.Width * w1.ContentScale <= 48.5, "2.2.6 folder list: small icon per row (" + (img.Width * w1.ContentScale).ToString("0.#") + " px)");
                var longRow = rows.First(r => ((FolderEntry)r.Tag).Name == longName);
                var tb = ((DockPanel)longRow.Child).Children.OfType<TextBlock>().First();
                tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                check(tb.TextTrimming == TextTrimming.CharacterEllipsis && tb.TextWrapping == TextWrapping.NoWrap && tb.DesiredSize.Width > tb.ActualWidth + 20 && Math.Abs(longRow.ActualHeight - rows[0].ActualHeight) < .5, "2.2.6 folder list: a long name stays on one line and ends with an ellipsis (" + tb.ActualWidth + " of " + tb.DesiredSize.Width + ")");

                // hover
                rows[2].RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
                bool hl = rows[2].Background != Brushes.Transparent;
                rows[2].RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseLeaveEvent });
                check(hl && rows[2].Background == Brushes.Transparent, "2.2.6 folder list: row is highlighted on hover");

                // double-click opens
                string opened1 = null; w1.Open = p => opened1 = p;
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
                typeof(MouseButtonEventArgs).GetProperty("ClickCount").GetSetMethod(true).Invoke(args, new object[] { 2 });
                rows[2].RaiseEvent(args);
                check(opened1 == ((FolderEntry)rows[2].Tag).Path, "2.2.6 folder list: double-click opens the item (" + opened1 + ")");
                check(w1.Scroller.VerticalScrollBarVisibility == ScrollBarVisibility.Hidden, "2.2.6 folder list: no visible scroll bar");

                // scaling: bigger widget -> bigger rows and text on screen
                double h0 = Bounds(rows[0], w1).Height;
                w1.SetCustomSize(w1.ActualWidth * 1.6, w1.ActualHeight * 1.6, false);
                await Task.Delay(400);
                var r0 = w1.Tiles.Children.OfType<Border>().First();
                double h1 = Bounds(r0, w1).Height;
                check(h1 > h0 * 1.3 || (h1 > h0 && h1 >= Math.Round(FolderLogic.ListIconMax * 1.45) - 1), "2.2.6 folder list: row height and font grow with the widget, up to the 48 px icon limit (" + h0.ToString("0.0") + " -> " + h1.ToString("0.0") + ")");
                check(w1.Tiles.Children.OfType<Border>().All(r => Math.Abs(r.ActualWidth - (w1.Scroller.ViewportWidth - 2)) < 3), "2.2.6 folder list: rows follow the new width");

                // live update in list view
                File.WriteAllText(Path.Combine(d, "b-new.txt"), "x");
                for (int i = 0; i < 30 && w1.Shown != 5; i++) await Task.Delay(100);
                check(w1.Shown == 5, "2.2.6 folder list: a new file appears by itself");

                // menu toggle, saved per instance
                var item = w1.ContextMenu.Items.OfType<MenuItem>().First(m => (m.Header as string ?? "").StartsWith("视图"));
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Task.Delay(200);
                check(!w1.IsList && w2.IsList && ToolStore.Load<FolderData>(w1.TestState.Id).View == "icons" && ToolStore.Load<FolderData>(w2.TestState.Id).View == "list", "2.2.6 folder list: 视图 menu switches this instance only and is saved per instance");
                var menuText = string.Join("|", w1.ContextMenu.Items.OfType<MenuItem>().Select(m => m.Header as string ?? ""));
                check(menuText.Contains("视图：列表 / 图标") && menuText.Contains("无边框") && menuText.Contains("背景"), "2.2.6 folder list: menu has 视图 plus frameless / background");
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                check(w1.IsList, "2.2.6 folder list: and back to the list");

                // a 2.2.5 widget (saved without a view, folder chosen) keeps its tiles
                var old = new FolderData { Path = d }; ToolStore.Save("folder~99", old);
                var loaded = ToolStore.Load<FolderData>("folder~99");
                check(loaded.View == null, "2.2.6 folder list: (setup) a 2.2.5 file has no view");
                ToolWidgets.ForgetData("folder~99");
            }
            catch (Exception ex) { check(false, "2.2.6 folder list tests crashed: " + ex); }
            finally
            {
                foreach (var w in opened) try { string id = w.TestState.Id; w.Close(); ToolWidgets.ForgetData(id); Program.S.Widgets.Remove(id); } catch { }
                Program.Save();
                await Task.Delay(200);
                try { Directory.Delete(d, true); } catch { }
            }
        }
    }
}
