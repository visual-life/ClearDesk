using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ClearDesk
{
    internal static class DockTransitionsTest
    {
        static RunningApp Fixture(string key, string exe)
        {
            var app = new RunningApp
            {
                Identity = new AppIdentity
                {
                    Key = key,
                    AppId = key,
                    Exe = exe,
                    Name = key,
                    LaunchPath = exe
                },
                Title = key
            };
            app.Windows.Add(new IntPtr(1)); // Model-only window; never activated or passed to native APIs.
            return app;
        }

        internal static async Task Run(DockWindow dock, Action<bool, string> check)
        {
            var monitor = dock.TestRunning;
            var savedApps = monitor.Apps;
            var savedPins = Program.S.Pins;
            var first = Fixture("transition-one", Path.Combine(Program.Base, "ClearDesk.exe"));
            var second = Fixture("transition-two", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
            try
            {
                await Task.Delay(600);
                Program.S.Pins = new List<string>();
                monitor.Apps = new List<RunningApp>
                {
                    first,
                    second
                };
                dock.Rebuild();
                await Task.Delay(350);
                double before = dock.TestPlateWidth;
                monitor.Apps.Remove(second);
                dock.Rebuild();
                check(dock.TestTransitioning && dock.TestRetiringCount == 1, "closed temporary item keeps a noninteractive exit visual during transition");
                await Task.Delay(80);
                check(dock.TestRetiringOpacity > 0 && dock.TestRetiringOpacity < 1, "departing icon has an intermediate fade opacity");
                double during = dock.TestPlateWidth;
                check(during < before && during > before - Program.S.DockSize - 9, "Dock background shrinks continuously instead of jumping to final width");
                monitor.Apps.Remove(first);
                dock.Rebuild();
                await Task.Delay(650);
                check(!dock.TestTransitioning && dock.TestRetiringCount == 0 && dock.TestTemporaryCount == 0 && !dock.TestHasSeparator, "rapid consecutive closes settle without duplicate icons, gaps or separator");
                monitor.Apps = new List<RunningApp>
                {
                    first,
                    second
                };
                dock.Rebuild();
                await Task.Delay(350);
                monitor.Apps.Remove(second);
                dock.Rebuild();
                dock.Hide();
                check(!dock.TestTransitioning && dock.TestRetiringCount == 0, "hiding Dock during an exit clears transient visuals immediately");
                dock.Show();
                await Task.Delay(100);
                check(dock.TestTemporaryCount == 1 && Math.Abs(dock.TestMaxScale - 1) < .001, "showing Dock after interrupted exit restores correct items and normal scale");
                int frames = dock.AnimatedFrames;
                await Task.Delay(250);
                check(dock.AnimatedFrames == frames, "settled Dock transition leaves no ongoing hover render loop");
            }
            finally
            {
                Program.S.Pins = savedPins;
                monitor.Apps = savedApps;
                dock.Rebuild();
                await Task.Delay(350);
            }
        }
    }
}
