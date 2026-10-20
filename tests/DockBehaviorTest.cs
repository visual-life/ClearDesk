using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClearDesk
{
    internal static class DockBehaviorTest
    {
        internal static void Fixture()
        {
            var app = new Application();
            var window = new Window
            {
                Title = "ClearDesk test fixture " + Process.GetCurrentProcess().Id,
                Width = 360,
                Height = 220,
                Icon = AppBrand.Icon,
                Background = Brushes.DarkSlateBlue,
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = "Dock lifecycle test window",
                    FontSize = 20,
                    Foreground = Brushes.White,
                    Margin = new Thickness(25)
                }
            };
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(3)
            };
            timer.Tick += delegate
            {
                app.Shutdown();
            };
            timer.Start();
            app.Run(window);
        }

        internal static async Task Run(DockWindow dock, Action<bool, string> check)
        {
            var pins = Program.S.Pins.ToArray();
            Process one = null, two = null;
            string shortcut = Path.Combine(Program.Data, "dock-fixture.lnk");
            try
            {
                object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")), link = null;
                try
                {
                    link = ((dynamic)shell).CreateShortcut(shortcut);
                    ((dynamic)link).TargetPath = Path.Combine(Program.Base, "ClearDesk.exe");
                    ((dynamic)link).Arguments = "--dock-fixture";
                    ((dynamic)link).Save();
                }
                finally
                {
                    if (link != null)
                        Marshal.ReleaseComObject(link);
                    Marshal.ReleaseComObject(shell);
                }

                var identity = ShellIdentity.Resolve(shortcut);
                check(identity.Exe == AppIdentity.Normalize(Path.Combine(Program.Base, "ClearDesk.exe")), "shortcut is matched by executable identity");
                one = Process.Start(new ProcessStartInfo(Path.Combine(Program.Base, "ClearDesk.exe"), "--dock-fixture") { UseShellExecute = false });
                two = Process.Start(new ProcessStartInfo(Path.Combine(Program.Base, "ClearDesk.exe"), "--dock-fixture") { UseShellExecute = false });
                var monitor = dock.TestRunning;
                RunningApp app = null;
                for (int i = 0; i < 40; i++)
                {
                    await Task.Delay(100);
                    monitor.Refresh();
                    app = monitor.Apps.FirstOrDefault(a => AppIdentity.Matches(a.Identity, identity));
                    if (app != null && app.Windows.Count == 2)
                        break;
                }

                check(app != null && app.Windows.Count == 2, "two windows of one application are grouped as one temporary Dock item");
                dock.Rebuild();
                check(dock.TestTemporaryCount > 0 && dock.TestHasSeparator, "running unpinned applications create the temporary section and separator");
                Program.S.Pins.Add(shortcut);
                Program.Save();
                dock.Rebuild();
                var entries = monitor.Entries(Program.S.Pins);
                var matched = entries.Where(e => AppIdentity.Matches(e.Identity, identity)).ToArray();
                check(matched.Length == 1 && matched[0].Pinned && matched[0].IsRunning, "pinning a running app moves it to fixed section without duplicate and keeps running dot");
                IntPtr handle = app.Preferred;
                foreach (var h in app.Windows)
                    Native.ShowWindow(h, 6);
                await Task.Delay(250);
                monitor.ActivateOrLaunch(matched[0]);
                monitor.ActivateOrLaunch(matched[0]);
                await Task.Delay(500);
                monitor.Refresh();
                app = monitor.Apps.First(a => AppIdentity.Matches(a.Identity, identity));
                check(app.Windows.Any(h => !Native.IsIconic(h)) && app.Windows.Count == 2 && !one.HasExited && !two.HasExited, "repeated Dock clicks restore an existing minimized window without launching another instance");
                Program.S.Pins.Remove(shortcut);
                Program.Save();
                dock.Rebuild();
                var temporary = monitor.Entries(Program.S.Pins).Single(e => AppIdentity.Matches(e.Identity, identity));
                check(!temporary.Pinned && temporary.IsRunning, "unpinning an open application keeps it in temporary section");
                Program.S.Pins.Add(shortcut);
                Program.Save();
                dock.Rebuild();
                var first = app.Windows[0];
                Native.PostMessage(first, 0x10, IntPtr.Zero, IntPtr.Zero);
                await Task.Delay(500);
                monitor.Refresh();
                app = monitor.Apps.FirstOrDefault(a => AppIdentity.Matches(a.Identity, identity));
                check(app != null && app.Windows.Count == 1, "closing one window keeps application and running dot");
                Native.PostMessage(app.Windows[0], 0x10, IntPtr.Zero, IntPtr.Zero);
                await Task.Delay(500);
                monitor.Refresh();
                entries = monitor.Entries(Program.S.Pins);
                matched = entries.Where(e => AppIdentity.Matches(e.Identity, identity)).ToArray();
                check(matched.Length == 1 && matched[0].Pinned && !matched[0].IsRunning, "closing last window retains pinned item and clears running dot");
                Program.S.Pins.Remove(shortcut);
                dock.Rebuild();
                check(!monitor.Entries(Program.S.Pins).Any(e => AppIdentity.Matches(e.Identity, identity)), "closed unpinned application is removed from Dock");
                check(((BitmapSource)AppBrand.Icon).PixelWidth >= 256, "embedded application icon includes high-resolution frame");
                Program.Launchpad();
                await Task.Delay(350);
                var pad = Program.App.Windows.OfType<LaunchpadWindow>().First();
                int frostCount = FrostedBackdrop.ActiveCount;
                var pane = pad.TestSurface;
                var bitmap = new RenderTargetBitmap((int)pane.ActualWidth, (int)pane.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(pane);
                var pixel = new byte[4];
                bitmap.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1), pixel, 4, 0);
                check(pixel[3] > 0 && pixel[3] < 128 && !pane.FrostEnabled, "launchpad background retains alpha transparency with no wallpaper layer");
                Native.RECT bounds;
                Native.GetWindowRect(new WindowInteropHelper(pad).Handle, out bounds);
                pad.CheckOutside(new Native.POINT { X = (bounds.L + bounds.R) / 2, Y = (bounds.T + bounds.B) / 2 });
                await Task.Delay(200);
                check(!pad.WasClosed, "click inside launchpad does not dismiss it");
                pad.CheckOutside(new Native.POINT { X = bounds.L - 20, Y = bounds.T - 20 });
                await Task.Delay(250);
                check(pad.WasClosed, "click outside launchpad dismisses it without requiring another process to activate");
                check(FrostedBackdrop.ActiveCount == frostCount, "launchpad does not create a wallpaper backdrop during dismissal");
                int hooks = OutsideClickWatcher.ActiveCount;
                using (var watcher = new OutsideClickWatcher(delegate
                {
                }))
                {
                    check(OutsideClickWatcher.ActiveCount == hooks + 1, "outside-click observer registers successfully");
                }

                check(OutsideClickWatcher.ActiveCount == hooks, "outside-click observer releases its hook");
            }
            finally
            {
                Program.S.Pins = pins.ToList();
                Program.Save();
                dock.Rebuild();
                foreach (var process in new[]
                {
                    one,
                    two
                }

                )
                    if (process != null)
                    {
                        try
                        {
                            if (!process.HasExited)
                            {
                                process.CloseMainWindow();
                                if (!process.WaitForExit(1000))
                                    process.Kill();
                            }
                        }
                        catch
                        {
                        }

                        process.Dispose();
                    }
            }
        }
    }
}
