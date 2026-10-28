using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace ClearDesk
{
    // 2.2.0: Dock restore for elevated / tray / launcher apps, and the new widgets' logic.
    internal static class V220Tests
    {
        // A window that behaves like a foreign one: when Elevated, ShowWindow / OpenIcon are refused (UIPI) while WM_SYSCOMMAND still works.
        sealed class FakeWindow : IWindowOps
        {
            public bool Exists = true, Visible = true, Iconic = true, Elevated, SysCommandWorks = true, Foregrounded;
            public List<string> Calls = new List<string>();
            public bool IsWindow(IntPtr h) { return Exists; }
            public bool IsVisible(IntPtr h) { return Visible; }
            public bool IsIconic(IntPtr h) { return Iconic; }
            public bool SysCommand(IntPtr h, int c)
            {
                Calls.Add("sys:" + c.ToString("X")); if (!SysCommandWorks) return false;
                if (c == DockRestorer.SC_RESTORE && Visible) Iconic = false;
                if (c == DockRestorer.SC_MINIMIZE) Iconic = true;
                return true;
            }
            public bool Show(IntPtr h, int cmd)
            {
                Calls.Add("show:" + cmd); if (Elevated) return false;
                if (cmd == 5) Visible = true; if (cmd == 9) Iconic = false; if (cmd == 6) Iconic = true; return true;
            }
            public bool OpenIcon(IntPtr h) { Calls.Add("openicon"); if (Elevated) return false; Iconic = false; return true; }
            public bool Foreground(IntPtr h) { Calls.Add("fg"); Foregrounded = true; return true; }
        }
        static readonly IntPtr W = (IntPtr)0x1234;
        static void NoSleep(int ms) { }

        internal static void Rules(Action<bool, string> check)
        {
            // ---- Dock restore ----
            var f = new FakeWindow { Elevated = true };
            var log = new List<string>();
            var r = DockRestorer.Restore(W, f, NoSleep, log);
            check(r == DockRestorer.Result.Restored && !f.Iconic && f.Foregrounded && f.Calls.First() == "sys:F120", "dock restore: a minimized elevated app (ShowWindow refused) is restored through WM_SYSCOMMAND SC_RESTORE and brought forward");
            check(!f.Calls.Contains("show:9"), "dock restore: no ShowWindow attempt is needed when SC_RESTORE worked");

            f = new FakeWindow { Elevated = false, SysCommandWorks = false };
            r = DockRestorer.Restore(W, f, NoSleep, null);
            check(r == DockRestorer.Result.Restored && f.Calls.Contains("show:9") && f.Foregrounded, "dock restore: when the app ignores SC_RESTORE the ShowWindow / OpenIcon fallback restores it");

            f = new FakeWindow { Elevated = true, SysCommandWorks = false };
            r = DockRestorer.Restore(W, f, NoSleep, null);
            check(r == DockRestorer.Result.Failed, "dock restore: when nothing works the result is Failed (the Dock then starts the app again)");

            f = new FakeWindow { Exists = false };
            check(DockRestorer.Restore(W, f, NoSleep, null) == DockRestorer.Result.Gone, "dock restore: a window that vanished reports Gone");

            f = new FakeWindow { Visible = false, Iconic = false };
            r = DockRestorer.Restore(W, f, NoSleep, null);
            check(r == DockRestorer.Result.Restored && f.Visible && f.Calls.First() == "show:5", "dock restore: a tray app's hidden window is shown again");
            f = new FakeWindow { Visible = false, Iconic = false, Elevated = true };
            check(DockRestorer.Restore(W, f, NoSleep, null) == DockRestorer.Result.Failed, "dock restore: an elevated tray app cannot be shown from here → Failed so that the Dock relaunches it");

            f = new FakeWindow { Iconic = false, Elevated = true };
            check(DockRestorer.Minimize(W, f) && f.Iconic && f.Calls[0] == "sys:F020", "dock minimize: elevated windows are minimized through SC_MINIMIZE");
            f = new FakeWindow { Iconic = false, SysCommandWorks = false };
            check(DockRestorer.Minimize(W, f) && f.Calls.Contains("show:6") && f.Iconic, "dock minimize: falls back to ShowWindow");

            // ---- hidden main window detection ----
            const long Caption = 0xC00000 | 0x80000 | 0x20000;
            check(DockRestorer.IsMainWindowCandidate(Caption, 0x100, IntPtr.Zero, false, 6, 900, 600), "tray app: a hidden captioned window of normal size is a main window");
            check(!DockRestorer.IsMainWindowCandidate(Caption, 0x100, IntPtr.Zero, true, 6, 900, 600), "tray app: a visible window is not treated as hidden");
            check(!DockRestorer.IsMainWindowCandidate(0x8C000000, 0, IntPtr.Zero, false, 14, 900, 600), "tray app: IME / helper windows without a caption are ignored");
            check(!DockRestorer.IsMainWindowCandidate(Caption, 0x80, IntPtr.Zero, false, 6, 900, 600), "tray app: tool windows are ignored");
            check(!DockRestorer.IsMainWindowCandidate(Caption, 0, (IntPtr)5, false, 6, 900, 600), "tray app: owned windows (dialogs) are ignored");
            check(!DockRestorer.IsMainWindowCandidate(Caption, 0, IntPtr.Zero, false, 0, 900, 600) && !DockRestorer.IsMainWindowCandidate(Caption, 0, IntPtr.Zero, false, 6, 20, 20), "tray app: untitled or tiny windows are ignored");

            // ---- launcher → client matching ----
            string riot = @"c:\riot games\riot client\riotclientservices.exe", ux = @"c:\riot games\league of legends\leagueclientux.exe";
            check(DockRestorer.AncestorMatch(riot, new[] { @"c:\riot games\league of legends\leagueclient.exe", riot }), "launcher match: the pinned launcher is an ancestor of the client process");
            check(!DockRestorer.AncestorMatch(@"c:\windows\explorer.exe", new[] { @"c:\windows\explorer.exe" }), "launcher match: explorer / shells as ancestors never group apps together");
            check(!DockRestorer.AncestorMatch(@"c:\windows\system32\cmd.exe", new[] { @"c:\windows\system32\cmd.exe" }), "launcher match: a pinned cmd.exe does not claim programs started from a console");
            check(!DockRestorer.AncestorMatch(riot, new[] { ux }) && !DockRestorer.AncestorMatch(null, new[] { riot }), "launcher match: unrelated processes do not match");

            V220WidgetTests.Rules(check);
            V221Tests.Rules(check);
            V222Tests.Rules(check);
            V223Tests.Rules(check);
            V224Tests.Rules(check);
        }

        internal static async Task Run(Action<bool, string> check)
        {
            // Real window of this process: minimize and restore through the same code the Dock uses.
            var win = new Window { Width = 300, Height = 200, Title = "ClearDesk dock restore test", ShowInTaskbar = false, Left = -4000, Top = -4000 };
            win.Show();
            IntPtr h = new WindowInteropHelper(win).Handle;
            bool minimized = DockRestorer.Minimize(h, WinOps.Instance);
            for (int i = 0; i < 20 && !Native.IsIconic(h); i++) await Task.Delay(50);
            check(minimized && Native.IsIconic(h), "dock minimize (real window): SC_MINIMIZE minimizes a window of another thread/app");
            var result = await Task.Run(() => DockRestorer.Restore(h, WinOps.Instance, Thread.Sleep, null));
            check(result == DockRestorer.Result.Restored && !Native.IsIconic(h), "dock restore (real window): SC_RESTORE restores it");
            win.Close();
            await V220WidgetTests.Run(check);
            await V221Tests.Run(check);
            await V222Tests.Run(check);
            await V223Tests.Run(check);
            await V224Tests.Run(check);
        }
    }
}
