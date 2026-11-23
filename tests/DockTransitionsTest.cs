using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Media;

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
                var firstButton = dock.TestButton(1);
                var resident = new RunningApp { Identity = first.Identity, Title = first.Title, InTray = true };
                monitor.Apps[0] = resident;
                bool hidWithoutRebuild = !dock.RebuildIfChanged() && ReferenceEquals(dock.TestButton(1), firstButton)
                    && ReferenceEquals(dock.TestEntries[0].Running, resident);
                monitor.Apps[0] = first;
                check(hidWithoutRebuild && !dock.RebuildIfChanged() && ReferenceEquals(dock.TestButton(1), firstButton)
                    && ReferenceEquals(dock.TestEntries[0].Running, first),
                    "a tray app is hidden and restored without recreating its Dock icon");
                dock.TestHover(.5);
                await Task.Delay(350);
                double hovered = dock.TestMaxScale;
                dock.EaseHoverOut();
                check(hovered > 1.15 && Math.Abs(dock.TestMaxScale - hovered) < .01,
                    "switching away from the Dock preserves the hovered icon size at the start of the transition");
                await Task.Delay(420);
                check(Math.Abs(dock.TestMaxScale - 1) < .01, "hovered icons shrink smoothly back to their default size");
                double before = dock.TestPlateWidth;
                monitor.Apps.Remove(second);
                dock.RebuildIfChanged();
                check(dock.TestTransitioning && dock.TestRetiringCount == 1, "closed temporary item keeps a noninteractive exit visual during transition");
                await Task.Delay(80);
                check(dock.TestRetiringOpacity > 0 && dock.TestRetiringOpacity < 1, "departing icon has an intermediate fade opacity");
                double during = dock.TestPlateWidth;
                check(during < before && during > before - Program.S.DockSize - 9, "Dock background shrinks continuously instead of jumping to final width");
                monitor.Apps.Remove(first);
                dock.RebuildIfChanged();
                await Task.Delay(650);
                check(!dock.TestTransitioning && dock.TestRetiringCount == 0 && dock.TestTemporaryCount == 0 && !dock.TestHasSeparator, "rapid consecutive closes settle without duplicate icons, gaps or separator");
                check(Math.Abs(dock.Left - DockPlacement.CenteredLeft(dock.Geometry(), dock.Width)) < 1,
                    "Dock stays centered after the plate shrinks during icon removal");
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
                await CloseOneOfSeven(dock, check);
                Program.S.Pins = new List<string> { first.Identity.Exe };
                monitor.Apps = new List<RunningApp>();
                dock.Rebuild();
                await Task.Delay(350);
                var pinButton = dock.TestButton(1);
                monitor.Apps.Add(first);
                check(!dock.RebuildIfChanged() && ReferenceEquals(pinButton, dock.TestButton(1)) && dock.TestEntries[0].IsRunning,
                    "launching a fixed app updates the running mark without rebuilding the Dock");
            }
            finally
            {
                Program.S.Pins = savedPins;
                monitor.Apps = savedApps;
                dock.Rebuild();
                await Task.Delay(350);
            }
        }

        static async Task CloseOneOfSeven(DockWindow dock, Action<bool, string> check)
        {
            var monitor = dock.TestRunning;
            string material = dock.TestState.Material;
            int size = Program.S.DockSize;
            try
            {
                Program.S.DockSize = 48;
                monitor.Apps = Enumerable.Range(1, 7)
                    .Select(i => Fixture("close-fixture-" + i, Path.Combine(Program.Base, "ClearDesk.exe"))).ToList();
                dock.TestMaterial("frosted");
                dock.Rebuild();
                await Task.Delay(460);
                var buttons = Enumerable.Range(0, 8).Select(dock.TestButton).ToArray();
                var content = buttons.Select(b => b.Content).ToArray();
                var originalParent = VisualTreeHelper.GetParent(buttons[4]);
                var hwnd = new WindowInteropHelper(dock).Handle;
                var backdrop = dock.TestBackdropHandle;
                Native.RECT originalWindow, originalBackdrop;
                bool hasWindow = Native.GetWindowRect(hwnd, out originalWindow);
                bool hasBackdrop = Native.GetWindowRect(backdrop, out originalBackdrop);
                double originalPlate = dock.TestPlateWidth;
                monitor.Apps.RemoveAt(3);
                dock.RebuildIfChanged();
                check(dock.TestTransitioning && dock.TestTemporaryCount == 6 && !buttons[4].IsHitTestVisible
                    && VisualTreeHelper.GetParent(buttons[4]) == originalParent,
                    "closing an app in a seven-app Dock animates just its departing icon");
                check(Enumerable.Range(0, 7).All(i =>
                {
                    int old = i < 4 ? i : i + 1;
                    return dock.TestButton(i) == buttons[old] && ReferenceEquals(dock.TestButton(i).Content, content[old])
                        && VisualTreeHelper.GetParent(dock.TestButton(i)) == originalParent;
                }), "the six surviving Dock icons and launcher keep their original attached visuals");
                dock.RequestBackdropRefresh();
                await Task.Delay(75);
                bool stableWindow = SameBounds(hwnd, originalWindow), stableBlur = SameBounds(backdrop, originalBackdrop);
                check(dock.TestTransitioning && dock.TestPlateWidth < originalPlate && stableWindow && stableBlur,
                    "the plate animates inside stable native Dock and blur windows (transition: " + dock.TestTransitioning
                    + ", plate: " + originalPlate + " to " + dock.TestPlateWidth + ", Dock: " + stableWindow + ", blur: " + stableBlur + ")");
                await Task.Delay(240);
                var dpi = VisualTreeHelper.GetDpi(dock).DpiScaleX;
                var interaction = dock.TestAutoHide().ShownRect();
                check(hasWindow && hasBackdrop && !dock.TestTransitioning && dock.TestRetiringCount == 0
                    && VisualTreeHelper.GetParent(buttons[4]) == null
                    && SameBounds(dock.TestBackdropHandle, originalBackdrop) && SameBounds(hwnd, originalWindow)
                    && Math.Abs(dock.TestPlateWidth - (originalPlate - size - 9)) < .1
                    && Math.Abs((interaction.R - interaction.L) - (dock.Width - size - 9) * dpi) < 2,
                    "the smaller layout settles with a smaller interaction zone and no native resize or lingering icon");
                await Task.Delay(430);
                check(!Native.IsWindow(backdrop) && Native.IsWindowVisible(dock.TestBackdropHandle)
                    && SameBounds(dock.TestBackdropHandle, originalBackdrop),
                    "the deferred backdrop refresh keeps the settled Dock covered");
            }
            finally
            {
                Program.S.DockSize = size;
                dock.TestMaterial(material);
            }
        }

        static bool SameBounds(IntPtr hwnd, Native.RECT expected)
        {
            Native.RECT actual;
            return Native.GetWindowRect(hwnd, out actual) && actual.L == expected.L && actual.T == expected.T
                && actual.R == expected.R && actual.B == expected.B;
        }
    }
}
