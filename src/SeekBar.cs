using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ClearDesk
{
    // Thin, self-drawn timeline matching the widget's translucent style:
    // a 3px line normally, 5px with a small knob on hover/drag. Click or drag to seek.
    internal sealed class SeekBar : Grid
    {
        readonly Border track = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)) };
        readonly Border fill = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)) };
        readonly Ellipse knob = new Ellipse { Width = 10, Height = 10, Fill = Brushes.White, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Hidden, IsHitTestVisible = false };
        double value;
        public event EventHandler ValueChanged;

        public SeekBar()
        {
            Height = 14; // generous hit area, the visible line stays thin
            Background = Brushes.Transparent;
            Cursor = Cursors.Hand;
            Focusable = false;
            knob.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = .35 };
            Children.Add(track); Children.Add(fill); Children.Add(knob);
            SizeChanged += delegate { Layout(); };
            MouseEnter += delegate { Hover(true); };
            MouseLeave += delegate { if (!IsMouseCaptured) Hover(false); };
            IsEnabledChanged += delegate { Opacity = IsEnabled ? 1 : .55; Cursor = IsEnabled ? Cursors.Hand : Cursors.Arrow; if (!IsEnabled) Hover(false); };
            MouseLeftButtonDown += (s, e) => { if (!IsEnabled) return; CaptureMouse(); Set(e.GetPosition(this).X); e.Handled = true; };
            MouseMove += (s, e) => { if (IsMouseCaptured) Set(e.GetPosition(this).X); };
            MouseLeftButtonUp += (s, e) => { if (IsMouseCaptured) { Set(e.GetPosition(this).X); ReleaseMouseCapture(); } if (!IsMouseOver) Hover(false); };
            LostMouseCapture += delegate { if (!IsMouseOver) Hover(false); };
        }

        public double Value
        {
            get { return value; }
            set
            {
                double v = double.IsNaN(value) ? 0 : Math.Max(0, Math.Min(1, value));
                if (v == this.value) return;
                this.value = v; Layout();
                var h = ValueChanged; if (h != null) h(this, EventArgs.Empty);
            }
        }

        void Set(double x) { if (ActualWidth > 0) Value = x / ActualWidth; }

        void Hover(bool on)
        {
            bool show = on && IsEnabled;
            track.Height = fill.Height = show ? 5 : 3;
            track.CornerRadius = fill.CornerRadius = new CornerRadius(show ? 2.5 : 1.5);
            knob.Visibility = show ? Visibility.Visible : Visibility.Hidden;
        }

        void Layout()
        {
            double w = Math.Max(0, ActualWidth);
            fill.Width = w * value;
            knob.Margin = new Thickness(Math.Max(0, Math.Min(w - knob.Width, w * value - knob.Width / 2)), 0, 0, 0);
        }
    }
}
