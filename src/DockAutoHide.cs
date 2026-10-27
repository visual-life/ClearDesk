using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ClearDesk
{
    // 窗口遮挡时自动隐藏: slides the Dock below the screen while another app's window overlaps it.
    internal sealed class DockAutoHide : IDisposable
    {
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out Native.RECT rect, int size);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);

        readonly DockWindow dock;
        readonly uint own = (uint)Process.GetCurrentProcess().Id;
        Native.EventProc proc;
        IntPtr locationHook, stateHook;
        DispatcherTimer debounce, pointer;
        DateTime leftAt = DateTime.MaxValue;
        bool disposed;
        internal bool Hidden { get; private set; }
        internal bool Overlapped { get; private set; }
        internal bool PeekedByPointer { get; private set; }

        public DockAutoHide(DockWindow window) { dock = window; }

        public void Start()
        {
            debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
            debounce.Tick += delegate { debounce.Stop(); Evaluate(); };
            pointer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
            pointer.Tick += delegate { Pointer(); };
            proc = delegate (IntPtr hook, uint evt, IntPtr h, int obj, int child, uint thread, uint time)
            {
                if (disposed || obj != 0 || child != 0) return;
                if (evt == 0x800B && !Program.S.DockAutoHide) return;
                if (!debounce.IsEnabled) debounce.Start();
            };
            // foreground, minimize start/end, show/hide/destroy, and window moves (debounced; filtered to whole windows)
            stateHook = Native.SetWinEventHook(3, 0x17, IntPtr.Zero, proc, 0, 0, 2);
            locationHook = Native.SetWinEventHook(0x8001, 0x800B, IntPtr.Zero, proc, 0, 0, 2);
            Evaluate();
        }

        internal static bool Intersects(Native.RECT a, Native.RECT b)
        {
            return a.L < b.R && b.L < a.R && a.T < b.B && b.T < a.B;
        }

        // Pure decision: hide when enabled and overlapped, unless the pointer is using the Dock.
        internal static bool ShouldHide(bool enabled, bool overlapped, bool pointerEngaged)
        {
            return enabled && overlapped && !pointerEngaged;
        }

        // The rect the Dock occupies when shown, in physical pixels.
        internal Native.RECT ShownRect()
        {
            var dpi = VisualTreeHelper.GetDpi(dock);
            double top = dock.ShownTop;
            return new Native.RECT
            {
                L = (int)Math.Round(dock.Left * dpi.DpiScaleX), T = (int)Math.Round(top * dpi.DpiScaleY),
                R = (int)Math.Round((dock.Left + dock.ActualWidth) * dpi.DpiScaleX), B = (int)Math.Round((top + dock.ActualHeight) * dpi.DpiScaleY)
            };
        }

        internal bool CandidateOverlaps(IntPtr h, Native.RECT area)
        {
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return false;
            long ex = Native.GetLong(h, -20).ToInt64();
            if ((ex & 0x80) != 0 || (ex & 0x08000000) != 0) return false;           // tool / no-activate windows
            int cloaked;
            if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return false;
            string cls = Native.Class(h);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" || cls == "#32768") return false;
            uint pid; Native.GetWindowThreadProcessId(h, out pid);
            if (pid == own || GetWindowTextLength(h) == 0) return false;
            Native.RECT r;
            if (DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(Native.RECT))) != 0 && !Native.GetWindowRect(h, out r)) return false;
            return Intersects(r, area);
        }

        internal bool FindOverlap()
        {
            if (!dock.IsVisible && !Hidden) return false;
            var area = ShownRect();
            bool found = false;
            Native.EnumWindows(delegate (IntPtr h, IntPtr unused)
            {
                if (CandidateOverlaps(h, area)) { found = true; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        // 2.2.2: while the Dock is hidden it only comes back after the overlap has been gone for a short moment. Windows that are being activated, minimised
        // or moved briefly report odd rectangles; showing at once made the Dock slide out and straight back in ("jumping").
        internal const int ShowDelayMs = 280;
        internal static bool ShouldDelayShow(bool wantHide, bool hidden, bool enabled, bool peekedByPointer)
        {
            return enabled && hidden && !wantHide && !peekedByPointer;
        }
        DispatcherTimer showDelay; internal int DelayedShows;
        internal void Evaluate()
        {
            if (disposed || dock.WasClosed) return;
            bool enabled = Program.S.DockAutoHide;
            Overlapped = enabled && FindOverlap();
            if (!enabled || !Overlapped) PeekedByPointer = false;
            bool hide = ShouldHide(enabled, Overlapped, PeekedByPointer);
            if (ShouldDelayShow(hide, Hidden, enabled, PeekedByPointer) && !Program.Rendering)
            {
                if (showDelay == null)
                {
                    showDelay = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ShowDelayMs) };
                    showDelay.Tick += delegate { showDelay.Stop(); if (!disposed && !dock.WasClosed) { Overlapped = Program.S.DockAutoHide && FindOverlap(); if (!Overlapped) { DelayedShows++; Apply(false); } } };
                }
                if (!showDelay.IsEnabled) showDelay.Start();
                if (!pointer.IsEnabled) pointer.Start();
                return;
            }
            if (showDelay != null) showDelay.Stop();
            Apply(hide);
            if (enabled && (Hidden || PeekedByPointer)) { if (!pointer.IsEnabled) pointer.Start(); }
            else pointer.Stop();
        }

        void Pointer()
        {
            if (disposed || dock.WasClosed) return;
            var p = Forms.Cursor.Position;
            var screen = Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(dock).Handle).Bounds;
            var area = ShownRect();
            if (Hidden)
            {
                if (p.Y >= screen.Bottom - 2 && p.X >= area.L && p.X <= area.R)
                {
                    PeekedByPointer = true; leftAt = DateTime.MaxValue; Apply(false);
                }
                return;
            }
            bool inside = p.X >= area.L - 12 && p.X <= area.R + 12 && p.Y >= area.T - 12 || (dock.ContextMenu != null && dock.ContextMenu.IsOpen);
            if (inside) { leftAt = DateTime.MaxValue; return; }
            if (leftAt == DateTime.MaxValue) { leftAt = DateTime.UtcNow; return; }
            if ((DateTime.UtcNow - leftAt).TotalMilliseconds > 450)
            {
                PeekedByPointer = false; leftAt = DateTime.MaxValue;
                Evaluate();
            }
        }

        void Apply(bool hide)
        {
            if (hide == Hidden) return;
            Hidden = hide;
            double target = hide ? SystemParameters.PrimaryScreenHeight + 4 : dock.ShownTop;
            var a = new DoubleAnimation(target, TimeSpan.FromMilliseconds(hide ? 240 : 200)) { EasingFunction = new CubicEase { EasingMode = hide ? EasingMode.EaseIn : EasingMode.EaseOut } };
            if (!hide) a.Completed += delegate { if (!Hidden) { dock.BeginAnimation(Window.TopProperty, null); dock.Top = dock.ShownTop; } };
            if (Program.Rendering || !dock.IsVisible) { dock.BeginAnimation(Window.TopProperty, null); dock.Top = target; if (hide) dock.BeginAnimation(Window.TopProperty, new DoubleAnimation(target, TimeSpan.Zero)); return; }
            dock.BeginAnimation(Window.TopProperty, a);
        }

        // Test hook: force the overlap state without real windows.
        internal void TestApply(bool overlapped, bool pointerEngaged)
        {
            Overlapped = overlapped; PeekedByPointer = pointerEngaged;
            Apply(ShouldHide(Program.S.DockAutoHide, overlapped, pointerEngaged));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (debounce != null) debounce.Stop();
            if (pointer != null) pointer.Stop();
            if (showDelay != null) showDelay.Stop();
            if (stateHook != IntPtr.Zero) Native.UnhookWinEvent(stateHook);
            if (locationHook != IntPtr.Zero) Native.UnhookWinEvent(locationHook);
            stateHook = locationHook = IntPtr.Zero;
            if (Hidden && !dock.WasClosed) { dock.BeginAnimation(Window.TopProperty, null); dock.Top = dock.ShownTop; }
            Hidden = false;
        }
    }
}
