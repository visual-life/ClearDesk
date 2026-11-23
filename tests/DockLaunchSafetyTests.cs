using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ClearDesk
{
    internal static class DockLaunchSafetyTests
    {
        static async Task<IntPtr> FixtureHandle(Process process)
        {
            string path = DockBehaviorTest.FixtureHandleFile(process.Id);
            for (int i = 0; i < 60; i++)
            {
                long handle;
                if (File.Exists(path) && long.TryParse(File.ReadAllText(path), out handle))
                    return new IntPtr(handle);
                await Task.Delay(50);
            }
            throw new InvalidOperationException("Fixture window did not publish its handle.");
        }

        static Process StartFixture(string mode)
        {
            return Process.Start(new ProcessStartInfo(Path.Combine(Program.Base, "ClearDesk.exe"), "--dock-fixture " + mode)
            {
                UseShellExecute = false
            });
        }

        static async Task CheckFixture(bool hidden, Action<bool, string> check)
        {
            using (var child = StartFixture(hidden ? "--hidden-fixture" : "--stalled-fixture"))
            using (var running = new RunningApps())
            try
            {
                IntPtr handle = await FixtureHandle(child);
                string exe = Path.Combine(Program.Base, "ClearDesk.exe");
                var identity = new AppIdentity { Key = exe, Exe = AppIdentity.Normalize(exe), LaunchPath = exe };
                var entry = new DockEntry { Identity = identity, PinPath = exe };
                await running.RequestRefresh();
                if (hidden)
                {
                    var hiddenWindows = running.HiddenWindowsFor(identity, null);
                    check(Native.IsWindow(handle) && !Native.IsWindowVisible(handle) && hiddenWindows.Contains(handle),
                        "a real hidden captioned auxiliary window matches the old Dock tray heuristic");
                }
                else
                {
                    check(Native.IsWindowVisible(handle) && running.Apps.Any(app => app.Windows.Contains(handle)),
                        "a real stalled application still has a taskbar-visible window");
                }

                int starts = 0;
                var launched = new TaskCompletionSource<string>();
                var noticed = new TaskCompletionSource<string>();
                running.OpenLaunchItem = path => { starts++; launched.TrySetResult(path); return Task.CompletedTask; };
                running.ActivationNotice += text => noticed.TrySetResult(text);
                running.ActivateOrLaunch(entry);
                running.ActivateOrLaunch(entry);
                await Dispatcher.Yield(DispatcherPriority.Input);
                if (hidden)
                {
                    await Task.WhenAny(launched.Task, Task.Delay(1600));
                    check(launched.Task.IsCompleted && launched.Task.Result == exe && starts == 1 && !Native.IsWindowVisible(handle),
                        "repeated Dock clicks use the original launch target once and never display the hidden auxiliary window");
                }
                else
                {
                    await Task.WhenAny(noticed.Task, Task.Delay(1600));
                    check(noticed.Task.IsCompleted && noticed.Task.Result == "应用暂未响应" && starts == 0 && !child.HasExited,
                        "the Dock dispatcher stays responsive while a stalled foreign window is detected, with no relaunch or termination");
                    string other = Path.Combine(Program.Base, "other-app.lnk");
                    running.ActivateOrLaunch(new DockEntry
                    {
                        PinPath = other,
                        Identity = new AppIdentity { Key = other, LaunchPath = other }
                    });
                    await Task.WhenAny(launched.Task, Task.Delay(1600));
                    check(launched.Task.IsCompleted && launched.Task.Result == other && starts == 1,
                        "a stalled app does not block opening another Dock item");
                    await CheckStalledScan(handle, other, check);
                }
            }
            finally { if (!child.HasExited) child.Kill(); }
        }

        static async Task CheckStalledScan(IntPtr handle, string path, Action<bool, string> check)
        {
            using (var running = new RunningApps())
            using (var release = new ManualResetEventSlim())
            {
                var scanning = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var opened = new TaskCompletionSource<string>();
                running.WindowAppIdLookup = window =>
                {
                    if (window == handle)
                    {
                        scanning.TrySetResult(true);
                        release.Wait(3000);
                    }
                    return null;
                };
                running.OpenLaunchItem = item => { opened.TrySetResult(item); return Task.CompletedTask; };
                Task scan = running.RequestRefresh();
                try
                {
                    await Task.WhenAny(scanning.Task, Task.Delay(1000));
                    check(scanning.Task.IsCompleted && !scan.IsCompleted, "foreign window metadata can wait while the Dock dispatcher continues");
                    running.ActivateOrLaunch(new DockEntry { PinPath = path, Identity = new AppIdentity { Key = path, LaunchPath = path } });
                    await Task.WhenAny(opened.Task, Task.Delay(1000));
                    check(opened.Task.IsCompleted && opened.Task.Result == path && !scan.IsCompleted,
                        "an unrelated launch continues even when a window metadata lookup stalls");
                }
                finally { release.Set(); await scan; }
            }
        }

        static async Task CheckShellThread(Action<bool, string> check)
        {
            var start = Program.StartItemProcess;
            using (var allowFirstToFinish = new ManualResetEventSlim())
            {
                string path = Path.Combine(Program.Base, "ClearDesk.exe");
                var first = new TaskCompletionSource<ApartmentState>(TaskCreationOptions.RunContinuationsAsynchronously);
                var second = new TaskCompletionSource<ApartmentState>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task opening = null;
                int requests = 0;
                int ui = Thread.CurrentThread.ManagedThreadId;
                try
                {
                    Program.StartItemProcess = info =>
                    {
                        Interlocked.Increment(ref requests);
                        var apartment = Thread.CurrentThread.ManagedThreadId == ui ? ApartmentState.Unknown : Thread.CurrentThread.GetApartmentState();
                        if (info.FileName == path)
                        {
                            first.TrySetResult(apartment);
                            allowFirstToFinish.Wait(3000);
                        }
                        else if (info.FileName == Program.Base) second.TrySetResult(apartment);
                    };
                    opening = Program.OpenItemAsync(path);
                    await Task.WhenAny(first.Task, Task.Delay(1000));
                    check(first.Task.IsCompleted && first.Task.Result == ApartmentState.STA && !opening.IsCompleted,
                        "a blocked shell launch runs in an STA outside the UI dispatcher");
                    await Program.OpenItemAsync(path);
                    await Program.OpenItemAsync(Program.Base);
                    check(second.Task.IsCompleted && second.Task.Result == ApartmentState.STA && requests == 2 && !opening.IsCompleted,
                        "the shared item opener deduplicates the same target while other folders or apps can open");
                }
                finally
                {
                    allowFirstToFinish.Set();
                    if (opening != null) await opening;
                    Program.StartItemProcess = start;
                }
            }
        }

        internal static async Task Run(Action<bool, string> check)
        {
            await CheckFixture(true, check);
            await CheckFixture(false, check);
            await CheckShellThread(check);
        }
    }
}
