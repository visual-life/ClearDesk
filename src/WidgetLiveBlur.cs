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
            // keep the blur directly under the widget if something re-orders the desktop layer
            order = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
            order.Tick += delegate { Sync(); };
            order.Start();
            Sync();
        }

        void Changed(object sender, EventArgs e) { Sync(); }
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
            if (backdrop != null) { backdrop.Dispose(); backdrop = null; }
        }
    }
}
