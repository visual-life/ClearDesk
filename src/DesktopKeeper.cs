using System;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ClearDesk
{
    /// <summary>
    /// 2.2.5: widgets stay visible on "Show desktop" (Win+D, the taskbar's 显示桌面 corner, Win+M).
    /// What Windows does: every minimizable window is minimized and the desktop window (WorkerW / Progman, the one holding SHELLDLL_DefView) is brought to the
    /// front and made the foreground window. ClearDesk's widgets cannot be minimized, but they now sit BEHIND that raised desktop window — they look "collapsed".
    /// What ClearDesk does (the approach desktop-widget tools use; observed with an out-of-process WinEvent hook, no injection, no keyboard hook):
    ///  • when the foreground becomes the desktop window, every widget is put into the topmost band without activation (so it is in front of the raised desktop);
    ///    a widget that somehow got minimized is restored;
    ///  • as soon as any other window becomes the foreground, each widget leaves the topmost band again and goes back to where it belongs: locked widgets to the
    ///    bottom (2.0.8 behaviour), others directly below the new foreground window — so a widget never covers the app the user switched to.
    /// Positions are not touched (SWP_NOMOVE | SWP_NOSIZE), so PositionGuard and the saved places are unaffected. Dock / 启动台 have their own logic (DesktopVisibility).
    /// </summary>
    internal static class DesktopKeeper
    {
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1), HWND_NOTOPMOST = new IntPtr(-2), HWND_BOTTOM = new IntPtr(1);
        static Native.EventProc callback;
        static IntPtr hook;
        static bool queued;
        /// <summary>Widgets are currently lifted above the shown desktop.</summary>
        internal static bool Raised { get; private set; }
        internal static int Raises, Lowers;
        /// <summary>Test hook: the foreground window to classify (default GetForegroundWindow).</summary>
        internal static Func<IntPtr> Foreground = Native.GetForegroundWindow;

        internal static void Start()
        {
            if (callback != null) return;
            callback = delegate
            {
                if (queued || Program.App == null) return;
                queued = true;
                Program.App.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(delegate { queued = false; Update(); }));
            };
            hook = Native.SetWinEventHook(3, 3, IntPtr.Zero, callback, 0, 0, 0);          // EVENT_SYSTEM_FOREGROUND, out of context
            // 2.2.5: Show desktop does not always change the foreground (e.g. when the desktop already had it): also look every 400 ms whether the desktop window
            // has been raised over any widget. Cheap: one walk down the top-level z-order.
            poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
            poll.Tick += delegate { try { Poll(); } catch (Exception e) { Program.Log(e); poll.Stop(); } };
            poll.Start();
        }

        internal static bool Hooked { get { return hook != IntPtr.Zero; } }

        static DispatcherTimer poll;
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        internal static IntPtr DesktopWindow() { return FindWindow("Progman", null); }

        /// <summary>Raises the widgets when the desktop (Progman) is above one of them; lowers them again once it is back at the bottom and another window is in front.</summary>
        internal static void Poll()
        {
            IntPtr desk = DesktopWindow();
            if (desk == IntPtr.Zero) return;
            if (!Raised) { if (AnyBelow(desk)) Raise(); }
            else
            {
                IntPtr fg = Foreground();
                if (IsDesktop(fg) == false && Below(desk).Count < 3) Lower(fg);      // desktop back at the bottom and an application is active
            }
        }

        internal static bool AnyBelow(IntPtr desk)
        {
            var below = Below(desk);
            foreach (var w in Program.OpenWindows.ToArray())
            {
                if (!Applies(w)) continue;
                var h = new WindowInteropHelper(w).Handle;
                if (h != IntPtr.Zero && below.Contains(h)) return true;
            }
            return false;
        }

        internal static void Stop()
        {
            if (poll != null) { poll.Stop(); poll = null; }
            if (hook != IntPtr.Zero) { Native.UnhookWinEvent(hook); hook = IntPtr.Zero; }
            callback = null;
        }

        /// <summary>true: the desktop itself; false: some other application window; null: keep the current state (our own windows, menus, the taskbar).</summary>
        internal static bool? IsDesktop(IntPtr h)
        {
            if (h == IntPtr.Zero) return null;
            string c = Native.Class(h);
            if (c == "WorkerW" || c == "Progman") return true;
            if (c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" || c == "#32768" || c == "NotifyIconOverflowWindow" || c == "TopLevelWindowForOverflowXamlIsland" || c == "XamlExplorerHostIslandWindow") return null;
            var src = HwndSource.FromHwnd(h);
            if (src != null) return null;                                            // a ClearDesk window (widget, menu, settings, popup)
            return false;
        }

        static bool Applies(GlassWindow w)
        {
            return !w.WasClosed && w.IsVisible && !(w is DockWindow) && !(w is LaunchpadWindow) && !w.Topmost;
        }

        internal static void Update()
        {
            try
            {
                IntPtr fg = Foreground();
                bool? desk = IsDesktop(fg);
                if (desk == true) Raise(fg);
                else if (desk == false && Raised) Lower(fg);
            }
            catch (Exception e) { Program.Log(e); }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);

        /// <summary>Windows below h in the z-order (h itself excluded).</summary>
        internal static System.Collections.Generic.HashSet<IntPtr> Below(IntPtr h)
        {
            var set = new System.Collections.Generic.HashSet<IntPtr>();
            IntPtr x = h;
            for (int i = 0; i < 5000; i++) { x = GetWindow(x, 2); if (x == IntPtr.Zero) break; set.Add(x); }      // GW_HWNDNEXT
            return set;
        }

        /// <summary>
        /// The desktop window became the foreground. Only when it was brought IN FRONT of widgets (Show desktop) are they lifted: a plain click on the wallpaper
        /// also makes the desktop the foreground, but then it stays at the bottom and the widgets are already visible — nothing must change (they must not jump
        /// over the user's open windows).
        /// </summary>
        internal static void Raise(IntPtr desktop)
        {
            var below = Below(desktop);
            bool any = false;
            foreach (var w in Program.OpenWindows.ToArray())
            {
                if (w.WasClosed || w is DockWindow || w is LaunchpadWindow || w.Topmost || w.DesktopLayer) continue;
                if (w.WindowState == WindowState.Minimized) { w.WindowState = WindowState.Normal; any = true; }
                var h = new WindowInteropHelper(w).Handle;
                if (h != IntPtr.Zero && w.IsVisible && below.Contains(h)) any = true;
            }
            if (any) Raise();
        }

        internal static void Raise()
        {
            Raised = true; Raises++;
            foreach (var w in Program.OpenWindows.ToArray())
            {
                if (w.WasClosed || w is DockWindow || w is LaunchpadWindow || w.Topmost) continue;
                if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                if (!w.IsVisible) continue;
                var h = new WindowInteropHelper(w).Handle; if (h == IntPtr.Zero) continue;
                w.DesktopLayer = true;
                Native.SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            }
        }

        internal static void Lower(IntPtr foreground)
        {
            Raised = false; Lowers++;
            foreach (var w in Program.OpenWindows.ToArray())
            {
                if (!w.DesktopLayer) continue;
                w.DesktopLayer = false;
                if (w.WasClosed) continue;
                var h = new WindowInteropHelper(w).Handle; if (h == IntPtr.Zero) continue;
                Native.SetWindowPos(h, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                // back under the applications, where the widget was before Show desktop (HWND_BOTTOM still stays above the desktop window itself)
                Native.SetWindowPos(h, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            }
        }
    }
}
