using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ClearDesk
{
    // Observe foreground transitions out of process. No injected DLL or global input hook.
    internal sealed class DesktopVisibility : IDisposable
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmGetWindowAttribute(IntPtr h, int attr, out Native.RECT rect, int size);
        [DllImport("user32.dll")]
        static extern bool IsZoomed(IntPtr h);
        readonly DockWindow dock;
        Native.EventProc callback;
        IntPtr hook, minimizeHook, blocker;
        DispatcherTimer fallback;
        bool disposed, desktopContext = true, queued;
        public DesktopVisibility(DockWindow window)
        {
            dock = window;
        }

        public void Start()
        {
            callback = delegate
            {
                if (disposed || queued)
                    return;
                queued = true;
                dock.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(delegate
                {
                    queued = false;
                    if (!disposed)
                        Update();
                }));
            };
            hook = Native.SetWinEventHook(3, 3, IntPtr.Zero, callback, 0, 0, 0);
            minimizeHook = Native.SetWinEventHook(0x16, 0x17, IntPtr.Zero, callback, 0, 0, 0);
            fallback = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            fallback.Tick += delegate
            {
                Update();
            };
            fallback.Start();
            Update();
        }

        internal static bool CoversMonitor(Native.RECT r, Native.RECT monitor)
        {
            return r.L <= monitor.L + 2 && r.T <= monitor.T + 2 && r.R >= monitor.R - 2 && r.B >= monitor.B - 2;
        }

        internal static bool ShouldHide(Native.RECT rect, Native.RECT monitor, bool maximized, bool sameMonitor)
        {
            return CoversMonitor(rect, monitor) || (maximized && sameMonitor);
        }

        internal bool? Classify(IntPtr foreground)
        {
            if (foreground == IntPtr.Zero)
                return null;
            string name = Native.Class(foreground);
            if (name == "Progman" || name == "WorkerW" || name == "SHELLDLL_DefView" || name == "SysListView32")
                return true;
            // Keep the previous context while the user clicks the taskbar or a context menu.
            if (name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd" || name == "#32768")
                return null;
            var source = HwndSource.FromHwnd(foreground);
            var root = source == null ? null : source.RootVisual;
            if (root is GlassWindow)
                return true;
            if (root is System.Windows.Controls.Primitives.Popup || (root != null && root.GetType().Name == "PopupRoot"))
                return null;
            if (!Native.IsWindowVisible(foreground) || Native.IsIconic(foreground))
                return true;
            Native.RECT rect;
            if (DwmGetWindowAttribute(foreground, 9, out rect, Marshal.SizeOf(typeof(Native.RECT))) != 0 && !Native.GetWindowRect(foreground, out rect))
                return true;
            var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(dock).Handle);
            var monitor = screen.Bounds;
            bool sameMonitor = System.Windows.Forms.Screen.FromHandle(foreground).DeviceName == screen.DeviceName;
            return !ShouldHide(rect, new Native.RECT { L = monitor.Left, T = monitor.Top, R = monitor.Right, B = monitor.Bottom }, IsZoomed(foreground), sameMonitor);
        }

        public void Update()
        {
            ApplyForeground(Native.GetForegroundWindow());
        }

        internal void ApplyForeground(IntPtr foreground)
        {
            if (disposed || dock.WasClosed)
                return;
            bool? state = Classify(foreground);
            if (state.HasValue)
            {
                desktopContext = state.Value;
                blocker = desktopContext ? IntPtr.Zero : foreground;
            }
            else if (!desktopContext && (blocker == IntPtr.Zero || !Native.IsWindow(blocker) || Classify(blocker) != false))
            {
                desktopContext = true;
                blocker = IntPtr.Zero;
            }

            if (desktopContext)
            {
                if (!dock.IsVisible)
                    dock.Show();
                if (dock.WindowState == WindowState.Minimized)
                    dock.WindowState = WindowState.Normal;
                if (!dock.Topmost)
                    dock.Topmost = true;
            }
            else
            {
                if (dock.Topmost)
                    dock.Topmost = false;
                if (dock.IsVisible)
                    dock.Hide();
                foreach (var popup in Program.App.Windows.OfType<LaunchpadWindow>().ToArray())
                    if (popup.IsVisible)
                        popup.Dismiss();
            }
            dock.SyncBackdrop();
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            if (fallback != null)
                fallback.Stop();
            if (hook != IntPtr.Zero)
            {
                Native.UnhookWinEvent(hook);
                hook = IntPtr.Zero;
            }

            if (minimizeHook != IntPtr.Zero)
            {
                Native.UnhookWinEvent(minimizeHook);
                minimizeHook = IntPtr.Zero;
            }

            callback = null;
        }
    }
}
