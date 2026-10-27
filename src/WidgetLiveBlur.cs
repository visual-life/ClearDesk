using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ClearDesk
{
    // 动态壁纸实时模糊: the same DWM host-backdrop the Dock uses, placed right behind a widget.
    // DWM blurs whatever is really behind (Wallpaper Engine, video wallpapers, windows) on the GPU,
    // so the cost is compositor work, not CPU frame capture.
    internal sealed class LiveBehind : IDisposable
    {
        readonly FrameworkElement pane;
        readonly Window owner;
        DockBackdrop backdrop;
        readonly DispatcherTimer order;
        bool disposed;
        public static int ActiveCount;
        public bool Enabled { get { return backdrop != null && backdrop.Enabled; } }
        internal IntPtr Handle { get { return backdrop == null ? IntPtr.Zero : backdrop.Handle; } }

        internal static bool TestDisableReorder;     // reproduces the 2.2.1 behaviour in tests
        System.Windows.Interop.HwndSource hooked;
        internal bool IsDirectlyBelowOwner { get { return backdrop == null || owner == null || backdrop.IsDirectlyBelow(new WindowInteropHelper(owner).Handle); } }
        IntPtr OwnerMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == 0x47 && backdrop != null && !disposed && !TestDisableReorder) backdrop.Reorder(hwnd);       // WM_WINDOWPOSCHANGED
            return IntPtr.Zero;
        }
        void Hook()
        {
            if (owner == null || hooked != null) return;
            var h = new WindowInteropHelper(owner).Handle; if (h == IntPtr.Zero) return;
            hooked = System.Windows.Interop.HwndSource.FromHwnd(h); if (hooked != null) hooked.AddHook(OwnerMessage);
        }

        public LiveBehind(FrameworkElement element)
        {
            pane = element;
            owner = Window.GetWindow(element);
            backdrop = new DockBackdrop { Radius = 22 };
            ActiveCount++;
            if (owner != null)
            {
                owner.LocationChanged += Changed;
                owner.SizeChanged += Changed;
                owner.IsVisibleChanged += Visible;
                owner.Closed += Closed;
            }
            pane.SizeChanged += Changed;
            Hook();
            if (owner != null && hooked == null) owner.SourceInitialized += delegate { Hook(); };
            // keep the blur directly under the widget if something re-orders the desktop layer
            order = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
            order.Tick += delegate { Sync(); };
            order.Start();
            Sync();
        }

        void Changed(object sender, EventArgs e) { Sync(); }
        internal int TestShownCount { get { return backdrop != null && backdrop.Shown ? 1 : 0; } }
        void Visible(object sender, DependencyPropertyChangedEventArgs e) { Sync(); }
        void Closed(object sender, EventArgs e) { Dispose(); }

        public void Sync()
        {
            if (disposed || backdrop == null || owner == null) return;
            var handle = new WindowInteropHelper(owner).Handle;
            if (handle == IntPtr.Zero || !owner.IsVisible || !pane.IsVisible || pane.ActualWidth < 1 || pane.ActualHeight < 1 || PresentationSource.FromVisual(pane) == null)
            {
                backdrop.Sync(handle, Rect.Empty, 1, false);
                return;
            }
            var a = pane.PointToScreen(new Point(1, 1));
            var b = pane.PointToScreen(new Point(pane.ActualWidth - 1, pane.ActualHeight - 1));
            Hook();
            backdrop.Sync(handle, new Rect(a, b), (b.X - a.X) / Math.Max(1, pane.ActualWidth - 2), true);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ActiveCount--;
            order.Stop();
            if (owner != null)
            {
                owner.LocationChanged -= Changed;
                owner.SizeChanged -= Changed;
                owner.IsVisibleChanged -= Visible;
                owner.Closed -= Closed;
            }
            pane.SizeChanged -= Changed;
            if (hooked != null) { hooked.RemoveHook(OwnerMessage); hooked = null; }
            if (backdrop != null) { backdrop.Dispose(); backdrop = null; }
        }
    }
}
