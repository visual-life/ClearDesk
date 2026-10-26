using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Web.Script.Serialization;

namespace ClearDesk
{
    internal static class RegressionTest
    {
        static readonly List<string> results = new List<string>();
        static void Check(bool value, string label)
        {
            if (!value)
                throw new Exception(label);
            results.Add("PASS: " + label);
        }

        static void Render(Window w, string name)
        {
            var element = (FrameworkElement)w.Content;
            element.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.Combine(Program.Base, "preview"));
            using (var f = File.Create(Path.Combine(Program.Base, "preview", name + ".png")))
                encoder.Save(f);
        }

        public static async void Start()
        {
            Window foreign = null;
            DesktopVisibility visibility = null;
            try
            {
                await Task.Delay(1200);
                var organizer = Program.App.Windows.OfType<OrganizerWindow>().First();
                var dock = Program.App.Windows.OfType<DockWindow>().First();
                Check(organizer.IsLoaded && organizer.TestGrid.ItemCount == Program.DesktopItems().Count(), "organizer creates successfully and shows all desktop references");
                int initialCount = organizer.TestGrid.ItemCount;
                double initialHeight = organizer.Height;
                organizer.TestToggleCollapsed();
                await Task.Delay(300);
                Check(organizer.Height < 70 && !organizer.TestGrid.IsVisible, "explicit collapse hides the grid without deleting references");
                organizer.TestToggleCollapsed();
                await Task.Delay(300);
                Check(organizer.Height >= 170 && Math.Abs(organizer.Height - initialHeight) < 1 && organizer.TestGrid.IsVisible && organizer.TestGrid.ItemCount == initialCount, "expand restores saved height and all icons");
                var grid = organizer.TestGrid;
                organizer.TestResize(340, 180);
                await Task.Delay(800);
                int narrow = grid.Columns;
                Check(grid.PositionsSettled, "narrow layout settles");
                Check(grid.MaxOffset > 0, "short organizer overflows without ScrollViewer");
                grid.ScrollBy(100000);
                await Task.Delay(900);
                Check(Math.Abs(grid.Offset - grid.MaxOffset) < 1 && !grid.IsAnimating, "smooth scrolling clamps and stops at bottom");
                organizer.TestResize(720, 500);
                await Task.Delay(900);
                Check(grid.Columns > narrow && grid.PositionsSettled, "resize changes columns and settles tile positions");
                Check(grid.Offset <= grid.MaxOffset + .1, "resize clamps scroll offset");
                Render(organizer, "organizer-wide");
                organizer.TestResize(520, 360);
                await Task.Delay(700);
                Render(organizer, "organizer");
                string[] original = Program.S.Pins.ToArray();
                if (original.Length >= 2)
                {
                    dock.MovePin(0, original.Length - 1);
                    var persisted = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(Program.Config));
                    Check(persisted.Pins.Last() == original[0] && persisted.Pins.Count == original.Length, "Dock reorder persists all references exactly once");
                    Program.S.Pins = original.ToList();
                    Program.Save();
                    dock.Rebuild();
                }

                var resolution = new List<string>();
                foreach (var path in Program.DesktopItems())
                {
                    var bitmap = await Task.Run(() => Native.Icon(path) as BitmapSource);
                    Check(bitmap != null, "icon loads: " + Path.GetFileName(path));
                    resolution.Add(Path.GetFileName(path) + ": " + bitmap.PixelWidth + "x" + bitmap.PixelHeight);
                }

                File.WriteAllLines(Path.Combine(Program.Base, "icon-resolution.txt"), resolution);
                Program.Launchpad();
                await Task.Delay(900);
                var pad = Program.App.Windows.OfType<LaunchpadWindow>().First();
                Check(pad.Top + pad.Height <= dock.PlateTop - 10, "launchpad stays above Dock with gap");
                Check(pad.Top >= SystemParameters.WorkArea.Top, "launchpad stays in work area");
                Render(pad, "launchpad");
                pad.Dismiss();
                await Task.Delay(400);
                Check(pad.WasClosed, "launchpad dismiss animation closes window");
                Render(dock, "dock");
                var glass = new GlassPane("liquid")
                {
                    Width = 200,
                    Height = 100
                };
                glass.Measure(new Size(200, 100));
                glass.Arrange(new Rect(0, 0, 200, 100));
                var alpha = new RenderTargetBitmap(200, 100, 96, 96, PixelFormats.Pbgra32);
                alpha.Render(glass);
                var pixel = new byte[4];
                alpha.CopyPixels(new Int32Rect(100, 50, 1, 1), pixel, 4, 0);
                Check(pixel[3] > 0 && pixel[3] < 128, "glass center is translucent, no opaque wallpaper layer");
                foreign = new Window
                {
                    Title = "ClearDesk regression ordinary app window",
                    Width = 320,
                    Height = 180,
                    Content = new TextBlock
                    {
                        Text = "Foreground visibility test"
                    }
                };
                foreign.Show();
                foreign.Activate();
                await Task.Delay(400);
                visibility = new DesktopVisibility(dock);
                visibility.ApplyForeground(new WindowInteropHelper(foreign).Handle);
                Check(dock.IsVisible, "Dock stays visible for ordinary foreground window");
                dock.TestHover(.5);
                await Task.Delay(350);
                Check(dock.TestMaxScale > 1.1, "Dock magnification is active before visibility transition");
                foreign.WindowState = WindowState.Maximized;
                await Task.Delay(250);
                visibility.ApplyForeground(new WindowInteropHelper(foreign).Handle);
                Check(!dock.IsVisible, "Dock hides for work-area maximized window, including side-taskbar layout");
                Check(Math.Abs(dock.TestMaxScale - 1) < .001, "hiding Dock clears magnification immediately");
                visibility.ApplyForeground(IntPtr.Zero);
                Check(!dock.IsVisible, "temporary empty foreground preserves hiding while maximized blocker is still visible");
                foreign.WindowState = WindowState.Minimized;
                await Task.Delay(200);
                visibility.ApplyForeground(IntPtr.Zero);
                Check(dock.IsVisible, "minimizing maximized blocker restores Dock even with empty foreground and no desktop click");
                foreign.WindowState = WindowState.Maximized;
                await Task.Delay(200);
                visibility.ApplyForeground(new WindowInteropHelper(foreign).Handle);
                foreign.Hide();
                visibility.ApplyForeground(IntPtr.Zero);
                Check(dock.IsVisible, "hidden blocker cannot leave Dock stuck behind transient foreground");
                foreign.Show();
                foreign.WindowState = WindowState.Normal;
                await Task.Delay(250);
                visibility.ApplyForeground(new WindowInteropHelper(foreign).Handle);
                Check(dock.IsVisible, "Dock returns after restoring maximized window");
                Check(Math.Abs(dock.TestMaxScale - 1) < .001, "restored Dock does not retain enlarged icons");
                foreign.WindowState = WindowState.Maximized;
                foreign.WindowState = WindowState.Minimized;
                await Task.Delay(250);
                visibility.ApplyForeground(new WindowInteropHelper(foreign).Handle);
                Check(dock.IsVisible, "minimized maximized window does not hide Dock");
                Native.ShowWindow(new WindowInteropHelper(foreign).Handle, 9);
                await Task.Delay(100);
                foreign.WindowState = WindowState.Normal;
                foreign.WindowState = WindowState.Normal;
                foreign.WindowStyle = WindowStyle.None;
                foreign.ResizeMode = ResizeMode.NoResize;
                var monitor = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(dock).Handle).Bounds;
                Native.SetWindowPos(new WindowInteropHelper(foreign).Handle, IntPtr.Zero, monitor.Left, monitor.Top, monitor.Width, monitor.Height, 0x14);
                await Task.Delay(250);
                visibility.ApplyForeground(new WindowInteropHelper(foreign).Handle);
                Check(!dock.IsVisible, "Dock hides for true borderless fullscreen on its monitor");
                foreign.WindowState = WindowState.Minimized;
                await Task.Delay(200);
                visibility.ApplyForeground(IntPtr.Zero);
                Check(dock.IsVisible, "minimizing borderless fullscreen restores Dock without foreground click");
                foreign.WindowState = WindowState.Normal;
                Native.SetWindowPos(new WindowInteropHelper(foreign).Handle, IntPtr.Zero, monitor.Left + 100, monitor.Top + 100, 500, 400, 0x14);
                await Task.Delay(200);
                visibility.ApplyForeground(new WindowInteropHelper(foreign).Handle);
                Check(dock.IsVisible, "Dock returns after fullscreen exits");
                Check(!DesktopVisibility.CoversMonitor(new Native.RECT { L = 1920, T = 0, R = 3840, B = 1080 }, new Native.RECT { L = 0, T = 0, R = 1920, B = 1080 }), "fullscreen on another monitor does not hide this Dock");
                foreign.Close();
                foreign = null;
                visibility.ApplyForeground(new WindowInteropHelper(organizer).Handle);
                Check(dock.IsVisible, "Dock returns during desktop widget interaction");
                organizer.TestMaterial("frosted");
                dock.TestMaterial("frosted");
                await Task.Delay(300);
                Check(organizer.TestSurface.IsFrosted && dock.TestSurface.IsFrosted && dock.TestLiveFrost, "organizer uses wallpaper frost and Dock enables native live frost");
                Check(organizer.TestSurface.FrostEnabled && !dock.TestSurface.FrostEnabled, "live Dock frost does not allocate a wallpaper background over other windows");
                var saved = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(Program.Config));
                Check(saved.Boxes.First().Material == "frosted" && saved.Widgets["dock"].Material == "frosted", "Dock frost choice and organizer frost both persist");
                for (int i = 0; i < 3; i++)
                {
                    organizer.TestMaterial("liquid");
                    dock.TestMaterial("liquid");
                    await Task.Delay(60);
                    Check(FrostedBackdrop.ActiveCount == 0, "glass switch disposes frost background subscriptions cycle " + i);
                    organizer.TestMaterial("frosted");
                    dock.TestMaterial("frosted");
                    await Task.Delay(60);
                    Check(FrostedBackdrop.ActiveCount == 1, "only organizer subscribes to wallpaper changes cycle " + i);
                }

                Program.Launchpad();
                await Task.Delay(300);
                var materialPad = Program.App.Windows.OfType<LaunchpadWindow>().First();
                materialPad.TestMaterial("frosted");
                materialPad.Close();
                Program.Launchpad();
                await Task.Delay(300);
                materialPad = Program.App.Windows.OfType<LaunchpadWindow>().First();
                Check(materialPad.TestSurface.IsFrosted && !materialPad.TestSurface.FrostEnabled && materialPad.TestLiveFrost, "launchpad frosted persists and blurs live content behind it instead of the wallpaper image");
                materialPad.TestMaterial("liquid");
                materialPad.Close();
                organizer.TestMaterial("liquid");
                dock.TestMaterial("liquid");
                await Task.Delay(200);
                Check(FrostedBackdrop.ActiveCount == 0, "all temporary frost background layers released");
                Check(!DesktopVisibility.ShouldHide(new Native.RECT { L = 1920, T = 0, R = 3792, B = 1080 }, new Native.RECT { L = 0, T = 0, R = 1920, B = 1080 }, true, false), "maximized window on another monitor does not hide this Dock");
                var clock = new ClockWindow();
                clock.Show();
                clock.TestMaterial("frosted");
                await Task.Delay(120);
                clock.Close();
                clock = new ClockWindow();
                clock.Show();
                await Task.Delay(120);
                Check(clock.TestSurface != null && clock.TestSurface.FrostEnabled, "clock explicitly selected frosted material survives reopening");
                clock.TestMaterial("liquid");
                clock.Close();
                Check(FrostedBackdrop.ActiveCount == 0, "clock backdrop released on close");
                await DockBehaviorTest.Run(dock, Check);
                await DockTransitionsTest.Run(dock, Check);
                await MusicTests.Run(Check);
                await MenuThemeTests.Run(organizer, dock, Check);
                await SettingsWindowTests.Run(Check);
                await FeatureTests.Run(Check);
                await LyricsTests.Run(dock,Check);
                await V2Tests.Run(organizer, Check);
                await V203Tests.Run(dock, Check);
                await V204Tests.Run(Check);
                await V205Tests.Run(Check);
                await V206Tests.Run(Check);
                await V207Tests.Run(Check);
                await V208Tests.Run(Check);
                await V209Tests.Run(Check);
                await V210Tests.Run(Check);
                await V211Tests.Run(Check);
                await V220Tests.Run(Check);
                results.Add("INFO: visibility uses real window handles with deterministic dispatch; foreground hook registration is self-tested, interactive browser switching was not rechecked.");
                visibility.Dispose();
                visibility = null;
                await Task.Delay(1000);
                Check(FluidGrid.ActiveAnimations == 0 && GlassPane.ActiveAnimations == 0, "grid and glass have no idle frame subscriptions");
            }
            catch (Exception e)
            {
                results.Add("FAIL: " + e);
                Environment.ExitCode = 1;
            }
            finally
            {
                if (visibility != null)
                    visibility.Dispose();
                if (foreign != null)
                    foreign.Close();
                File.WriteAllLines(Path.Combine(Program.Base, "regression-test.txt"), results);
                Program.Quit();
            }
        }
    }
}
