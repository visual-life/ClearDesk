using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClearDesk
{
    // Glass uses live alpha; frosted uses a position-aligned desktop wallpaper layer.
    // Only the frosted background is blurred; content remains sharp.
    internal static class GlassSurface
    {
        public static Brush Highlight()
        {
            var b = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(.8, 1)
            };
            b.GradientStops.Add(new GradientStop(Color.FromArgb(125, 255, 255, 255), 0));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(28, 255, 255, 255), .35));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(8, 255, 255, 255), .65));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(80, 255, 255, 255), 1));
            b.Freeze();
            return b;
        }
    }

    public sealed class GlassPane : Grid
    {
        readonly Border tint = new Border();
        Border washLayer, edge;
        string material;
        FrostedBackdrop backdrop;
        LiveBehind live;
        internal bool LiveEnabled { get { return live != null && live.Enabled; } }
        readonly bool wallpaperFrost;
        readonly RadialGradientBrush light;
        Point target = new Point(.5, .1), current = new Point(.5, .1);
        double targetOpacity;
        bool active;
        TimeSpan last;
        public static int ActiveAnimations;
        public GlassPane(string material, bool wallpaperFrost = true)
        {
            this.wallpaperFrost = wallpaperFrost;
            IsHitTestVisible = false;
            Children.Add(tint);
            this.material = material;
            var wash = new LinearGradientBrush(Color.FromArgb(18, 255, 255, 255), Color.FromArgb(2, 255, 255, 255), 90);
            wash.Freeze();
            washLayer = new Border
            {
                Background = wash
            };
            Children.Add(washLayer);
            light = new RadialGradientBrush(Color.FromArgb(55, 255, 255, 255), Colors.Transparent)
            {
                RadiusX = .55,
                RadiusY = .85,
                Opacity = 0
            };
            Children.Add(new Border { Background = light });
            edge = new Border
            {
                BorderBrush = GlassSurface.Highlight(),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(23)
            };
            Children.Add(edge);
            Loaded += delegate
            {
                SetMaterial(this.material);
            };
            Unloaded += delegate
            {
                Stop();
                if (backdrop != null)
                {
                    backdrop.Dispose();
                    backdrop = null;
                }
                if (live != null) { live.Dispose(); live = null; }
            };
            SetMaterial(material);
        }

        public void SetMaterial(string value)
        {
            material = value;
            bool frosted = value == "frosted";
            bool liveMode = frosted && wallpaperFrost && Program.S != null && Program.S.LiveDesktopBlur && !Program.Rendering;
            if (liveMode && IsLoaded && live == null)
            {
                live = new LiveBehind(this);
                if (!live.Enabled) { live.Dispose(); live = null; liveMode = false; }
            }
            if (!liveMode && live != null) { live.Dispose(); live = null; }
            if (frosted && wallpaperFrost && !liveMode && IsLoaded && backdrop == null)
                backdrop = new FrostedBackdrop(this);
            if ((!frosted || liveMode) && backdrop != null)
            {
                backdrop.Dispose();
                backdrop = null;
            }

            bool native = frosted && (!wallpaperFrost || (backdrop != null && backdrop.Enabled) || (live != null && live.Enabled));
            tint.Background = new SolidColorBrush(Color.FromArgb((byte)(frosted ? (native ? 38 : 235) : 38), 24, 23, 24));
            if (washLayer != null)
                washLayer.Opacity = frosted ? .12 : 1;
            if (edge != null)
                edge.BorderBrush = frosted ? new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)) : GlassSurface.Highlight();
            if (frosted)
            {
                targetOpacity = 0;
                if (light != null)
                    light.Opacity = 0;
                Stop();
            }
        }

        public bool IsFrosted
        {
            get
            {
                return material == "frosted";
            }
        }

        internal bool FrostEnabled
        {
            get
            {
                return backdrop != null && backdrop.Enabled;
            }
        }

        public void Pointer(Point p, double w, double h)
        {
            if (material == "frosted")
                return;
            target = new Point(Math.Max(0, Math.Min(1, p.X / Math.Max(1, w))), Math.Max(0, Math.Min(1, p.Y / Math.Max(1, h))));
            targetOpacity = 1;
            Start();
        }

        public void Leave()
        {
            targetOpacity = 0;
            Start();
        }

        public void ResetPointer()
        {
            Stop();
            targetOpacity = 0;
            light.Opacity = 0;
            current = target = new Point(.5, .1);
            light.Center = light.GradientOrigin = current;
        }

        void Start()
        {
            if (active)
                return;
            active = true;
            ActiveAnimations++;
            last = TimeSpan.Zero;
            CompositionTarget.Rendering += Frame;
        }

        void Stop()
        {
            if (!active)
                return;
            active = false;
            ActiveAnimations--;
            CompositionTarget.Rendering -= Frame;
        }

        void Frame(object sender, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (now == last)
                return;
            double dt = last == TimeSpan.Zero ? 1.0 / 60 : Math.Min(.05, (now - last).TotalSeconds);
            last = now;
            double a = 1 - Math.Exp(-17 * dt);
            current = new Point(current.X + (target.X - current.X) * a, current.Y + (target.Y - current.Y) * a);
            light.Opacity += (targetOpacity - light.Opacity) * a;
            light.Center = current;
            light.GradientOrigin = current;
            if (Math.Abs(targetOpacity - light.Opacity) < .004 && Math.Abs(current.X - target.X) + Math.Abs(current.Y - target.Y) < .004)
            {
                light.Opacity = targetOpacity;
                Stop();
            }
        }
    }
}
