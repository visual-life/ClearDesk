using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace ClearDesk
{
    // The host backdrop is sampled and clipped by the GPU compositor itself.
    // A window region cannot clip a window-wide Accent acrylic background.
    internal sealed class DockBackdrop : IDisposable
    {
        [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDesktopInterop
        {
            void CreateDesktopWindowTarget(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool topmost,
                [MarshalAs(UnmanagedType.IUnknown)] out object target);
            void EnsureOnThread(uint threadId);
        }
        [StructLayout(LayoutKind.Sequential)]
        struct QueueOptions { public int Size, ThreadType, Apartment; }
        [StructLayout(LayoutKind.Sequential)]
        struct Accent { public int State, Flags; public uint Color; public int Animation; }
        [StructLayout(LayoutKind.Sequential)]
        struct CompositionData { public int Attribute; public IntPtr Data; public int Size; }
        [DllImport("CoreMessaging.dll")]
        static extern int CreateDispatcherQueueController(QueueOptions options, out IntPtr controller);
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("user32.dll")]
        static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionData data);
        [DllImport("user32.dll")]
        static extern IntPtr GetWindow(IntPtr hwnd, uint command);

        [ThreadStatic] static object dispatcherQueue;
        HwndSource source;
        Compositor compositor;
        DesktopWindowTarget target;
        SpriteVisual visual;
        CompositionBackdropBrush brush;
        CompositionRoundedRectangleGeometry geometry;
        CompositionGeometricClip clip;
        Native.RECT last;
        float lastRadius;
        bool shown, disposed;
        public float Radius = 25;
        public bool Enabled { get; private set; }
        public IntPtr Handle { get { return source == null ? IntPtr.Zero : source.Handle; } }

        public DockBackdrop()
        {
            try
            {
                if (Windows.System.DispatcherQueue.GetForCurrentThread() == null && dispatcherQueue == null)
                {
                    IntPtr queue;
                    Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(new QueueOptions
                    {
                        Size = Marshal.SizeOf(typeof(QueueOptions)), ThreadType = 2, Apartment = 2
                    }, out queue));
                    try { dispatcherQueue = Marshal.GetObjectForIUnknown(queue); }
                    finally { Marshal.Release(queue); }
                }
                source = new HwndSource(new HwndSourceParameters("ClearDesk · Dock 磨砂背景")
                {
                    Width = 1, Height = 1, PositionX = -32000, PositionY = -32000,
                    WindowStyle = unchecked((int)0x80000000),
                    // No GDI/WPF redirection bitmap: the composition visual owns every pixel.
                    ExtendedWindowStyle = 0x00200000 | 0x08000000 | 0x80 | 0x20
                });
                source.AddHook(Message);
                int enable = 1;
                bool host = DwmSetWindowAttribute(Handle, 17, ref enable, sizeof(int)) == 0;
                if (!host)
                {
                    var accent = new Accent { State = 5 };
                    var data = new CompositionData { Attribute = 19, Size = Marshal.SizeOf(typeof(Accent)) };
                    data.Data = Marshal.AllocHGlobal(data.Size);
                    try
                    {
                        Marshal.StructureToPtr(accent, data.Data, false);
                        host = SetWindowCompositionAttribute(Handle, ref data);
                    }
                    finally { Marshal.FreeHGlobal(data.Data); }
                }
                if (!host) return;
                compositor = new Compositor();
                object desktopTarget;
                ((IDesktopInterop)(object)compositor).CreateDesktopWindowTarget(Handle, false, out desktopTarget);
                target = (DesktopWindowTarget)desktopTarget;
                brush = compositor.CreateHostBackdropBrush();
                visual = compositor.CreateSpriteVisual();
                geometry = compositor.CreateRoundedRectangleGeometry();
                clip = compositor.CreateGeometricClip(geometry);
                visual.Brush = brush;
                visual.Clip = clip;
                target.Root = visual;
                Enabled = true;
            }
            catch (Exception error)
            {
                Program.Log(error);
                Dispose();
            }
        }

        IntPtr Message(IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled)
        {
            if (message == 0x84) { handled = true; return new IntPtr(-1); }
            if (message == 0x21) { handled = true; return new IntPtr(3); }
            return IntPtr.Zero;
        }

        public void Sync(IntPtr dock, Rect bounds, double scale, bool visible)
        {
            if (disposed) return;
            if (!visible || !Enabled || bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1)
            {
                if (shown) Native.ShowWindow(Handle, 0);
                shown = false;
                return;
            }
            int x = (int)Math.Round(bounds.X), y = (int)Math.Round(bounds.Y);
            int width = Math.Max(1, (int)Math.Round(bounds.Width)), height = Math.Max(1, (int)Math.Round(bounds.Height));
            float radius = (float)(Radius * scale);
            bool sizeChanged = last.R - last.L != width || last.B - last.T != height || radius != lastRadius;
            if (sizeChanged)
            {
                visual.Size = geometry.Size = new Vector2(width, height);
                geometry.CornerRadius = new Vector2(radius);
            }
            if (!shown || sizeChanged || last.L != x || last.T != y || GetWindow(dock, 2) != Handle)
                Native.SetWindowPos(Handle, dock, x, y, width, height, 0x10 | 0x40);
            last = new Native.RECT { L = x, T = y, R = x + width, B = y + height };
            lastRadius = radius;
            shown = true;
        }

        // Fades the blur together with the window content (no pop-in after the window is shown).
        public float TargetOpacity { get; private set; } = 1f;
        public void Fade(float value, int milliseconds)
        {
            if (disposed || !Enabled) return;
            TargetOpacity = value;
            if (milliseconds <= 0)
            {
                visual.StopAnimation("Opacity");
                visual.Opacity = value;
                return;
            }
            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(1f, value, compositor.CreateCubicBezierEasingFunction(new Vector2(0.215f, 0.61f), new Vector2(0.355f, 1f)));
            animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
            visual.StartAnimation("Opacity", animation);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Enabled = false;
            if (target != null) { target.Root = null; target.Dispose(); }
            if (visual != null) visual.Dispose();
            if (clip != null) clip.Dispose();
            if (geometry != null) geometry.Dispose();
            if (brush != null) brush.Dispose();
            if (compositor != null) compositor.Dispose();
            if (source != null)
            {
                source.RemoveHook(Message);
                source.Dispose();
                source = null;
            }
        }
    }
}
