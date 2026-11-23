using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Bitmap = System.Drawing.Bitmap;
using Color = System.Drawing.Color;
using Graphics = System.Drawing.Graphics;

namespace ClearDesk
{
    internal static class DockBackdropTests
    {
        static Window AppWindow(DockWindow dock, Brush background)
        {
            return new Window
            {
                Title = "ClearDesk Dock backdrop fixture",
                Left = dock.Left,
                Top = dock.PlateTop - 140,
                Width = dock.Width,
                Height = 260,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Background = background
            };
        }

        static Color ReadSample(int x, int y)
        {
            using (var bitmap = new Bitmap(1, 1))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(x, y, 0, 0, bitmap.Size);
                return bitmap.GetPixel(0, 0);
            }
        }

        static async Task<IntPtr> WaitForStaging(DockWindow dock)
        {
            for (int i = 0; i < 35 && dock.TestPendingBackdropHandle == IntPtr.Zero; i++) await Task.Delay(12);
            return dock.TestPendingBackdropHandle;
        }

        internal static async Task Run(DockWindow dock, Action<bool, string> check)
        {
            string material = dock.TestState.Material;
            bool topmost = dock.Topmost;
            var red = AppWindow(dock, Brushes.Red);
            var blue = AppWindow(dock, Brushes.Blue);
            using (var visibility = new DesktopVisibility(dock))
            try
            {
                dock.ResetMagnification();
                dock.Topmost = true;
                dock.TestMaterial("frosted");
                IntPtr backdrop = dock.TestBackdropHandle;
                Native.RECT bounds;
                bool measured = Native.GetWindowRect(backdrop, out bounds);
                check(dock.TestLiveFrost && measured,
                    "Dock prepares a native backdrop for the app-switch refresh test");
                int frames = dock.AnimatedFrames;
                int refreshes = dock.BackdropRefreshCount;
                int sampleX = bounds.R - 20, sampleY = bounds.B - 12;
                red.Show();
                IntPtr redHwnd = new WindowInteropHelper(red).Handle;
                check(Native.SetWindowPos(redHwnd, backdrop, bounds.L - 40, bounds.T - 140,
                    bounds.R - bounds.L + 80, 250, 0x10), "places the first app behind the Dock");
                visibility.ApplyForeground(redHwnd);
                await Task.Delay(550);
                check(dock.BackdropRefreshCount == refreshes + 1,
                    "a foreground app change refreshes the Dock without a pointer event");
                Color redSample = ReadSample(sampleX, sampleY);
                Color redWindow = ReadSample(sampleX, bounds.T - 75);

                blue.Show();
                IntPtr blueHwnd = new WindowInteropHelper(blue).Handle;
                check(Native.SetWindowPos(blueHwnd, dock.TestBackdropHandle, bounds.L - 40, bounds.T - 140,
                    bounds.R - bounds.L + 80, 250, 0x10), "places the second app behind the Dock");
                IntPtr oldBackdrop = dock.TestBackdropHandle;
                visibility.ApplyForeground(blueHwnd);
                IntPtr staged = await WaitForStaging(dock);
                Native.RECT stagedBounds, oldBounds;
                bool coversPlate = Native.GetWindowRect(oldBackdrop, out oldBounds)
                    && Native.GetWindowRect(staged, out stagedBounds)
                    && stagedBounds.L == oldBounds.L && stagedBounds.T == oldBounds.T
                    && stagedBounds.R == oldBounds.R && stagedBounds.B == oldBounds.B;
                check(staged != IntPtr.Zero && staged != oldBackdrop && dock.TestBackdropHandle == oldBackdrop
                    && Native.IsWindowVisible(oldBackdrop) && Native.IsWindowVisible(staged)
                    && dock.TestPendingBehindCurrent && coversPlate,
                    "the current Dock backdrop covers the plate while its replacement prepares directly behind it");
                await Task.Delay(400);
                Color blueSample = ReadSample(sampleX, sampleY);
                Color blueWindow = ReadSample(sampleX, bounds.T - 75);
                check(dock.BackdropRefreshCount == refreshes + 2 && dock.AnimatedFrames == frames
                    && Native.IsWindowVisible(dock.TestBackdropHandle) && !Native.IsWindow(backdrop),
                    "switching to a second app replaces the stale backdrop without animating icons");
                check(redWindow.R > redWindow.B + 100 && blueWindow.B > blueWindow.R + 100,
                    "both colored fixture windows paint on screen (" + redWindow + ", " + blueWindow + ")");
                check(redSample.R > redSample.B + 40 && blueSample.B > blueSample.R + 40,
                    "the visible Dock background changes from red to blue at the same screen point (" + redSample + ", " + blueSample + ")");

                blue.Hide();
                visibility.ApplyForeground(redHwnd);
                visibility.ApplyForeground(redHwnd, true);
                await Task.Delay(500);
                Color restoredSample = ReadSample(sampleX, sampleY);
                check(dock.BackdropRefreshCount == refreshes + 3 && restoredSample.R > restoredSample.B + 40,
                    "foreground and minimize notifications coalesce and restore the exposed app color");
                visibility.ApplyForeground(redHwnd);
                await Task.Delay(350);
                check(dock.BackdropRefreshCount == refreshes + 3,
                    "polling an unchanged foreground does not repaint the backdrop");

                visibility.ApplyForeground(redHwnd, true);
                IntPtr pending = await WaitForStaging(dock);
                dock.TestMaterial("liquid");
                check(pending != IntPtr.Zero && !Native.IsWindow(pending) && dock.TestBackdropHandle == IntPtr.Zero,
                    "switching the material cancels a pending backdrop and releases both native hosts");
            }
            finally
            {
                blue.Close();
                red.Close();
                dock.TestMaterial(material);
                dock.Topmost = topmost;
            }
        }
    }
}
