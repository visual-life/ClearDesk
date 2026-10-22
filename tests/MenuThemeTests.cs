using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClearDesk
{
    internal static class MenuThemeTests
    {
        internal static async Task Run(OrganizerWindow organizer, DockWindow dock, Action<bool, string> check)
        {
            var surface = dock.TestSurface;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1), pixel, 4, 0);
            check(pixel[3] > 0 && pixel[3] < 128, "actual Dock plate leaves most background light visible instead of painting wallpaper");

            Program.S.Widgets["dock"].Material = "frosted";
            var reopened = new DockWindow();
            try
            {
                reopened.Show();
                await Task.Delay(100);
                check(reopened.TestSurface.IsFrosted && reopened.TestLiveFrost && !reopened.TestSurface.FrostEnabled, "reopening Dock restores live frost without a static wallpaper layer");
                var backdrop = reopened.TestBackdropHandle;
                check(Native.IsWindowVisible(backdrop), "live blur surface is visible behind the shown Dock");
                reopened.TestHover(.5);
                await Task.Delay(200);
                Native.RECT blurBounds;
                Native.GetWindowRect(backdrop, out blurBounds);
                var dpi = PresentationSource.FromVisual(reopened).CompositionTarget.TransformToDevice.M11;
                check(Math.Abs((blurBounds.R - blurBounds.L) - reopened.TestPlateWidth * dpi) < 3, "native frost width tracks the magnified Dock plate");
                reopened.Hide();
                await Task.Delay(100);
                check(!Native.IsWindowVisible(backdrop), "hiding Dock immediately hides its blur surface");
                reopened.Show();
                await Task.Delay(100);
                check(Native.IsWindowVisible(backdrop), "restoring Dock also restores its blur surface");
                reopened.TestMaterial("liquid");
                check(!Native.IsWindow(backdrop) && !reopened.TestLiveFrost, "switching to glass destroys the blur window");
                reopened.TestMaterial("frosted");
                await Task.Delay(100);
                check(reopened.TestLiveFrost, "switching back to frost recreates native blur");
            }
            finally { reopened.Close(); }
            dock.TestMaterial("liquid");

            var menu = organizer.ContextMenu;
            var parent = (MenuItem)menu.Items[0];
            var clear = (MenuItem)parent.Items[0];
            var frost = (MenuItem)parent.Items[1];
            try
            {
                organizer.TestMaterial("frosted");
                menu.PlacementTarget = organizer;
                menu.Placement = PlacementMode.Center;
                menu.IsOpen = true;
                await Task.Delay(100);
                parent.IsSubmenuOpen = true;
                await Task.Delay(180);
                parent.ApplyTemplate();
                frost.ApplyTemplate();
                var panel = (Border)parent.Template.FindName("SubmenuSurface", parent);
                var popup = (Popup)parent.Template.FindName("PART_Popup", parent);
                var bg = ((SolidColorBrush)panel.Background).Color;
                var fg = ((SolidColorBrush)frost.Foreground).Color;
                check(popup.IsOpen && bg.R > 240 && bg.G > 240 && fg.R < 60, "open material submenu has the light Windows 11 surface and dark readable text");
                check(frost.IsChecked && !clear.IsChecked && ((UIElement)frost.Template.FindName("Check", frost)).Visibility == Visibility.Visible, "material submenu shows current selection with the custom checkmark");
                parent.IsSubmenuOpen = false;
                menu.IsOpen = false;
                organizer.TestMaterial("liquid");
                menu.IsOpen = true;
                parent.IsSubmenuOpen = true;
                await Task.Delay(100);
                check(clear.IsChecked && !frost.IsChecked, "reopened submenu updates selection after material changes");
            }
            finally
            {
                parent.IsSubmenuOpen = false;
                menu.IsOpen = false;
                organizer.TestMaterial("liquid");
            }
        }
    }
}
