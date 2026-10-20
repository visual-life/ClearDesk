using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClearDesk
{
    public partial class DockWindow
    {
        sealed class ItemPosition
        {
            public string Key;
            public Button Button;
            public Border Dot;
            public double X, Y, Size, DotX, DotY;
            public bool Running;
        }

        const int TransitionMilliseconds = 240;
        readonly List<UIElement> retiring = new List<UIElement>();
        DispatcherTimer transitionTimer;
        bool transitioning, rebuildPending;
        double targetWidth, targetHeight;
        static string ItemKey(DockEntry entry)
        {
            return entry == null ? "launchpad" : entry.Pinned ? "pin:" + entry.PinPath : "running:" + entry.Identity.Key;
        }

        List<ItemPosition> CaptureItems()
        {
            var result = new List<ItemPosition>();
            for (int i = 0; i < buttons.Count; i++)
            {
                var entry = buttons[i].Tag as DockEntry;
                result.Add(new ItemPosition { Key = ItemKey(entry), Button = buttons[i], Dot = dots[i], X = Left + Canvas.GetLeft(buttons[i]), Y = Top + Canvas.GetTop(buttons[i]), Size = buttons[i].Width, DotX = Left + Canvas.GetLeft(dots[i]), DotY = Top + Canvas.GetTop(dots[i]), Running = entry != null && entry.IsRunning });
            }

            return result;
        }

        bool ItemsChanged(List<ItemPosition> previous)
        {
            var keys = new[]
            {
                "launchpad"
            }.Concat(entries.Select(ItemKey));
            var runningStates = new[]
            {
                false
            }.Concat(entries.Select(e => e.IsRunning));
            return !previous.Select(p => p.Key).SequenceEqual(keys, StringComparer.OrdinalIgnoreCase) || !previous.Select(p => p.Running).SequenceEqual(runningStates) || Math.Abs(previous[0].Size - baseSize) > .1;
        }

        static void Animate(UIElement element, DependencyProperty property, double from, double to, int milliseconds = TransitionMilliseconds)
        {
            if (double.IsNaN(from) || Math.Abs(from - to) < .001)
                return;
            element.BeginAnimation(property, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.HoldEnd });
        }

        static void AnimateScale(UIElement element, double from, double to, int milliseconds)
        {
            var scale = new ScaleTransform(to, to);
            element.RenderTransformOrigin = new Point(.5, .65);
            element.RenderTransform = scale;
            var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }

        void BeginTransition(List<ItemPosition> previous, double previousPlateWidth, double previousPlateHeight, bool hadSeparator, double separatorX)
        {
            transitioning = true;
            cursor = double.NaN;
            dockGlass.ResetPointer();
            var remaining = new List<ItemPosition>(previous);
            for (int i = 0; i < buttons.Count; i++)
            {
                var button = buttons[i];
                var entry = button.Tag as DockEntry;
                var old = remaining.FirstOrDefault(p => string.Equals(p.Key, ItemKey(entry), StringComparison.OrdinalIgnoreCase));
                bool isRunning = entry != null && entry.IsRunning;
                if (old != null)
                {
                    remaining.Remove(old);
                    Animate(button, Canvas.LeftProperty, old.X - Left, Canvas.GetLeft(button));
                    Animate(button, Canvas.TopProperty, old.Y - Top, Canvas.GetTop(button));
                    Animate(button, WidthProperty, old.Size, button.Width);
                    Animate(button, HeightProperty, old.Size, button.Height);
                    Animate(dots[i], Canvas.LeftProperty, old.DotX - Left, Canvas.GetLeft(dots[i]));
                    Animate(dots[i], Canvas.TopProperty, old.DotY - Top, Canvas.GetTop(dots[i]));
                    if (old.Running || isRunning)
                    {
                        dots[i].Visibility = Visibility.Visible;
                        Animate(dots[i], OpacityProperty, old.Running ? 1 : 0, isRunning ? 1 : 0, 160);
                    }
                }
                else
                {
                    Animate(button, OpacityProperty, 0, 1, 180);
                    AnimateScale(button, .72, 1, TransitionMilliseconds);
                    Animate(dots[i], OpacityProperty, 0, 1, 180);
                }
            }

            // Keep departing visuals until they fade out. They cannot launch a closed app.
            foreach (var old in remaining)
            {
                old.Button.IsHitTestVisible = false;
                old.Button.ContextMenu = null;
                old.Button.ToolTip = null;
                Canvas.SetLeft(old.Button, old.X - Left);
                Canvas.SetTop(old.Button, old.Y - Top);
                Canvas.SetLeft(old.Dot, old.DotX - Left);
                Canvas.SetTop(old.Dot, old.DotY - Top);
                canvas.Children.Add(old.Button);
                canvas.Children.Add(old.Dot);
                retiring.Add(old.Button);
                retiring.Add(old.Dot);
                Animate(old.Button, OpacityProperty, 1, 0, 170);
                AnimateScale(old.Button, 1, .55, 200);
                Animate(old.Button, Canvas.TopProperty, old.Y - Top, old.Y - Top + 8, 200);
                Animate(old.Dot, OpacityProperty, 1, 0, 120);
            }

            double width = plate.Width, height = plate.Height;
            Animate(plate, WidthProperty, previousPlateWidth, width);
            Animate(plate, HeightProperty, previousPlateHeight, height);
            Animate(plate, Canvas.LeftProperty, (Width - previousPlateWidth) / 2, (Width - width) / 2);
            var clip = new RectangleGeometry(new Rect(0, 0, width, height), 25, 25);
            plate.Clip = clip;
            clip.BeginAnimation(RectangleGeometry.RectProperty, new RectAnimation(new Rect(0, 0, previousPlateWidth, previousPlateHeight), new Rect(0, 0, width, height), TimeSpan.FromMilliseconds(TransitionMilliseconds)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            if (separatorBefore >= 0)
            {
                if (hadSeparator)
                    Animate(separator, Canvas.LeftProperty, separatorX - Left, Canvas.GetLeft(separator));
                else
                    Animate(separator, OpacityProperty, 0, 1, 180);
            }
            else if (hadSeparator)
            {
                Canvas.SetLeft(separator, separatorX - Left);
                canvas.Children.Add(separator);
                Animate(separator, OpacityProperty, 1, 0, 160);
            }

            transitionTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(TransitionMilliseconds + 20)
            };
            transitionTimer.Tick += delegate
            {
                FinishTransition(true);
            };
            transitionTimer.Start();
        }

        static void ClearAnimations(UIElement element)
        {
            foreach (var property in new[]
            {
                Canvas.LeftProperty,
                Canvas.TopProperty,
                WidthProperty,
                HeightProperty,
                OpacityProperty
            }

            )
                element.BeginAnimation(property, null);
            element.RenderTransform = Transform.Identity;
        }

        void FinishTransition(bool refresh)
        {
            if (!transitioning)
                return;
            transitionTimer.Stop();
            transitionTimer = null;
            transitioning = false;
            foreach (var element in retiring)
            {
                ClearAnimations(element);
                canvas.Children.Remove(element);
            }

            retiring.Clear();
            foreach (var button in buttons)
                ClearAnimations(button);
            for (int i = 0; i < dots.Count; i++)
            {
                ClearAnimations(dots[i]);
                dots[i].Visibility = i > 0 && entries[i - 1].IsRunning ? Visibility.Visible : Visibility.Collapsed;
            }

            ClearAnimations(plate);
            ClearAnimations(separator);
            if (separatorBefore < 0)
                canvas.Children.Remove(separator);
            bool pending = rebuildPending;
            rebuildPending = false;
            if (WasClosed)
                return;
            Width = targetWidth;
            Height = targetHeight;
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Bottom - Height - Program.S.DockOffset;
            LayoutIcons();
            if (refresh && pending)
                Rebuild();
        }

        internal bool TestTransitioning
        {
            get
            {
                return transitioning;
            }
        }

        internal int TestRetiringCount
        {
            get
            {
                return retiring.OfType<Button>().Count();
            }
        }

        internal double TestRetiringOpacity
        {
            get
            {
                return retiring.OfType<Button>().Select(b => b.Opacity).DefaultIfEmpty(0).First();
            }
        }

        internal double TestPlateWidth
        {
            get
            {
                return plate.Width;
            }
        }
    }
}
