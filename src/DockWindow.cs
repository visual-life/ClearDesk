using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClearDesk
{
    public class DockWindow : GlassWindow
    {
        readonly Canvas canvas = new Canvas();
        readonly List<Button> buttons = new List<Button>();
        readonly List<double> sizes = new List<double>();
        readonly List<Border> dots = new List<Border>();
        readonly RunningApps running = new RunningApps();
        List<DockEntry> entries = new List<DockEntry>();
        Border plate, separator, marker;
        GlassPane dockGlass;
        DesktopVisibility desktop;
        double cursor = double.NaN, baseSize;
        bool rendering, dragging, rebuilding;
        TimeSpan lastFrame;
        int frames, dropIndex, pinnedCount, separatorBefore = -1;
        Button dragSource;
        Point dragDown;
        public int AnimatedFrames
        {
            get
            {
                return frames;
            }
        }

        public DockWindow() : base(Program.Widget("dock", 0, 0, 620, 140), "Dock")
        {
            Frame.BorderThickness = new Thickness(0);
            Root.Children.Clear();
            Root.Clip = null;
            Root.Children.Add(canvas);
            Root.Background = null;
            Layout.Visibility = Visibility.Collapsed;
            plate = new Border
            {
                CornerRadius = new CornerRadius(25),
                BorderThickness = new Thickness(1),
                BorderBrush = GlassSurface.Highlight()
            };
            dockGlass = new GlassPane(State.Material);
            Surface = dockGlass;
            plate.Child = dockGlass;
            canvas.Children.Add(plate);
            separator = new Border
            {
                Width = 1,
                Background = new SolidColorBrush(Color.FromArgb(85, 255, 255, 255)),
                IsHitTestVisible = false
            };
            marker = new Border
            {
                Width = 3,
                Height = 42,
                Background = Brushes.White,
                CornerRadius = new CornerRadius(2),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            AllowDrop = true;
            DragOver += delegate (object s, DragEventArgs e)
            {
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
                e.Handled = true;
            };
            Drop += delegate (object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    foreach (string p in (string[])e.Data.GetData(DataFormats.FileDrop))
                        if ((File.Exists(p) || Directory.Exists(p)) && !Program.S.Pins.Contains(p) && Program.S.Pins.Count < 32)
                            Program.S.Pins.Add(p);
                    Program.Save();
                    Rebuild();
                }
            };
            MouseMove += delegate (object s, MouseEventArgs e)
            {
                cursor = e.GetPosition(canvas).X;
                StartFrames();
            };
            MouseLeave += delegate
            {
                cursor = double.NaN;
                StartFrames();
            };
            ContextMenu.Items.Insert(0, Item("打开桌面启动台", Program.Launchpad));
            running.Changed += delegate
            {
                if (!dragging && !buttons.Any(b => b.ContextMenu != null && b.ContextMenu.IsOpen))
                    Rebuild();
            };
            Closed += delegate
            {
                StopFrames();
                running.Dispose();
                if (desktop != null)
                    desktop.Dispose();
            };
            Loaded += delegate
            {
                if (!Program.Rendering)
                {
                    running.Start();
                    desktop = new DesktopVisibility(this);
                    desktop.Start();
                }
            };
            IsVisibleChanged += delegate
            {
                ResetMagnification();
                CancelDrag();
            };
            Deactivated += delegate
            {
                ResetMagnification();
            };
            SizeChanged += delegate
            {
                Root.Clip = null;
            };
            PreviewMouseMove += DragMoveIcon;
            PreviewMouseLeftButtonUp += DropIcon;
            PreviewKeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape)
                {
                    CancelDrag();
                    e.Handled = true;
                }
            };
            LostMouseCapture += delegate (object s, MouseEventArgs e)
            {
                if (dragging && e.OriginalSource == this)
                    CancelDrag();
            };
            Rebuild();
        }

        public void Rebuild()
        {
            if (rebuilding || WasClosed)
                return;
            rebuilding = true;
            try
            {
                StopFrames();
                for (int i = canvas.Children.Count - 1; i >= 1; i--)
                    canvas.Children.RemoveAt(i);
                buttons.Clear();
                sizes.Clear();
                dots.Clear();
                entries = running.Entries(Program.S.Pins);
                pinnedCount = entries.Count(e => e.Pinned);
                separatorBefore = entries.Any(e => !e.Pinned) ? pinnedCount + 1 : -1;
                var wa = SystemParameters.WorkArea;
                int count = entries.Count + 1;
                double gap = separatorBefore >= 0 ? 18 : 0;
                baseSize = Math.Min(Program.S.DockSize, Math.Max(16, (wa.Width - 180 - gap) / count - 9));
                Width = Math.Min(wa.Width - 20, 40 + count * (baseSize + 9) + 130 + gap);
                Height = baseSize * 1.8 + 46;
                State.Height = Height;
                Left = wa.Left + (wa.Width - Width) / 2;
                Top = wa.Bottom - Height - Program.S.DockOffset;
                AddIcon(null);
                foreach (var entry in entries)
                    AddIcon(entry);
                if (separatorBefore >= 0)
                    canvas.Children.Add(separator);
                canvas.Children.Add(marker);
                LayoutIcons();
            }
            finally
            {
                rebuilding = false;
            }
        }

        void AddIcon(DockEntry entry)
        {
            var b = new Button
            {
                Template = FluidUI.IconTemplate(),
                Width = baseSize,
                Height = baseSize,
                ToolTip = entry == null ? "启动台" : entry.Title,
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                Tag = entry
            };
            ToolTipService.SetInitialShowDelay(b, 550);
            ToolTipService.SetPlacement(b, PlacementMode.Top);
            if (entry == null)
            {
                var grid = new UniformGrid
                {
                    Rows = 3,
                    Columns = 3,
                    Width = 42,
                    Height = 42
                };
                for (int i = 0; i < 9; i++)
                    grid.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = Brushes.White, Margin = new Thickness(2.5) });
                b.Content = new Viewbox
                {
                    Child = grid
                };
            }
            else
            {
                var image = new Image
                {
                    Stretch = Stretch.Uniform
                };
                b.Content = image;
                string icon = entry.Pinned ? entry.PinPath : entry.Identity.Exe ?? entry.IconPath;
                if (icon != null)
                    IconCache.Bind(image, icon);
                b.ContextMenu = EntryMenu(entry);
                b.ContextMenu.Closed += delegate
                {
                    Dispatcher.BeginInvoke(new Action(Rebuild));
                };
            }

            b.PreviewMouseLeftButtonDown += delegate (object sender, MouseButtonEventArgs e)
            {
                if (entry != null && entry.Pinned)
                {
                    dragSource = b;
                    dragDown = e.GetPosition(canvas);
                    dragging = false;
                }
            };
            b.Click += delegate
            {
                if (dragging)
                    return;
                ResetMagnification();
                if (entry == null)
                    Program.Launchpad();
                else
                    running.ActivateOrLaunch(entry);
                if (!IsVisible)
                    return;
                var bounce = new TranslateTransform();
                b.RenderTransform = bounce;
                var a = new DoubleAnimationUsingKeyFrames
                {
                    Duration = TimeSpan.FromMilliseconds(340)
                };
                a.KeyFrames.Add(new EasingDoubleKeyFrame(-12, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
                a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(340)), new BounceEase { Bounces = 1, Bounciness = 2 }));
                bounce.BeginAnimation(TranslateTransform.YProperty, a);
            };
            var dot = new Border
            {
                Width = 4,
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = Brushes.White,
                IsHitTestVisible = false,
                Visibility = entry != null && entry.IsRunning ? Visibility.Visible : Visibility.Collapsed
            };
            buttons.Add(b);
            sizes.Add(baseSize);
            dots.Add(dot);
            canvas.Children.Add(b);
            canvas.Children.Add(dot);
        }

        ContextMenu EntryMenu(DockEntry entry)
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item(entry.Pinned ? "取消固定" : "固定到 Dock", delegate
            {
                if (entry.Pinned)
                    Program.S.Pins.Remove(entry.PinPath);
                else
                {
                    string target = running.PinTarget(entry);
                    if (!string.IsNullOrEmpty(target) && !Program.S.Pins.Contains(target))
                        Program.S.Pins.Add(target);
                }

                Program.Save();
                Rebuild();
            }));
            menu.Items.Add(Item("打开所在位置", delegate
            {
                string path = entry.PinPath ?? entry.Identity.Exe;
                if (string.IsNullOrEmpty(path) || path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                    return;
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                }
                catch (Exception e)
                {
                    Program.Log(e);
                }
            }));
            if (entry.Running != null && entry.Running.Windows.Count > 1)
            {
                var windows = new MenuItem
                {
                    Header = "已打开的窗口"
                };
                foreach (var handle in entry.Running.Windows)
                {
                    IntPtr h = handle;
                    windows.Items.Add(Item(WindowTitle(h), delegate
                    {
                        RunningApps.Activate(h);
                    }));
                }

                menu.Items.Add(windows);
            }

            if (entry.Pinned)
            {
                menu.Items.Add(Item("向左移动", delegate
                {
                    int i = Program.S.Pins.IndexOf(entry.PinPath);
                    MovePin(i, i - 1);
                }));
                menu.Items.Add(Item("向右移动", delegate
                {
                    int i = Program.S.Pins.IndexOf(entry.PinPath);
                    MovePin(i, i + 1);
                }));
            }

            return menu;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int GetWindowText(IntPtr h, System.Text.StringBuilder text, int count);
        static string WindowTitle(IntPtr h)
        {
            var b = new System.Text.StringBuilder(512);
            GetWindowText(h, b, b.Capacity);
            return b.Length == 0 ? "切换窗口" : b.ToString();
        }

        double Total()
        {
            return sizes.Sum() + Math.Max(0, sizes.Count - 1) * 9 + (separatorBefore >= 0 ? 18 : 0);
        }

        void LayoutIcons()
        {
            double total = Total(), x = (Width - total) / 2;
            for (int i = 0; i < buttons.Count; i++)
            {
                if (i == separatorBefore)
                {
                    Canvas.SetLeft(separator, x + 4);
                    separator.Height = baseSize * .65;
                    Canvas.SetTop(separator, Height - baseSize - 12);
                    x += 18;
                }

                var b = buttons[i];
                b.Width = b.Height = sizes[i];
                Canvas.SetLeft(b, x);
                Canvas.SetTop(b, Height - 18 - sizes[i]);
                Canvas.SetLeft(dots[i], x + sizes[i] / 2 - 2);
                Canvas.SetTop(dots[i], Height - 11);
                var view = b.Content as Viewbox;
                if (view != null)
                    view.Width = view.Height = sizes[i] * .73;
                x += sizes[i] + 9;
            }

            plate.Width = total + 32;
            plate.Height = baseSize + 28;
            Canvas.SetLeft(plate, (Width - plate.Width) / 2);
            Canvas.SetTop(plate, Height - plate.Height - 5);
            plate.Clip = new RectangleGeometry(new Rect(0, 0, plate.Width, plate.Height), 25, 25);
        }

        void StartFrames()
        {
            if (rendering || WasClosed || !IsVisible)
                return;
            rendering = true;
            lastFrame = TimeSpan.Zero;
            CompositionTarget.Rendering += Render;
        }

        void StopFrames()
        {
            if (rendering)
            {
                CompositionTarget.Rendering -= Render;
                rendering = false;
            }
        }

        internal void ResetMagnification()
        {
            StopFrames();
            cursor = double.NaN;
            for (int i = 0; i < sizes.Count; i++)
            {
                sizes[i] = baseSize;
                buttons[i].RenderTransform = Transform.Identity;
            }

            dockGlass.ResetPointer();
            if (buttons.Count > 0)
                LayoutIcons();
        }

        internal double TestMaxScale
        {
            get
            {
                return sizes.Count == 0 ? 1 : sizes.Max() / baseSize;
            }
        }

        void Render(object sender, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (now == lastFrame)
                return;
            double dt = lastFrame == TimeSpan.Zero ? 1.0 / 60 : Math.Min(.05, (now - lastFrame).TotalSeconds);
            lastFrame = now;
            double blend = 1 - Math.Exp(-18 * dt);
            bool moving = false;
            double start = (Width - (buttons.Count * (baseSize + 9) - 9 + (separatorBefore >= 0 ? 18 : 0))) / 2;
            for (int i = 0; i < sizes.Count; i++)
            {
                double center = start + i * (baseSize + 9) + baseSize / 2 + (separatorBefore >= 0 && i >= separatorBefore ? 18 : 0), distance = double.IsNaN(cursor) ? 10000 : Math.Abs(cursor - center);
                double wave = distance >= 155 ? 0 : (1 + Math.Cos(Math.PI * distance / 155)) / 2;
                double target = baseSize * (1 + .55 * wave), delta = target - sizes[i];
                if (Math.Abs(delta) > .07)
                {
                    sizes[i] += delta * blend;
                    moving = true;
                }
                else
                    sizes[i] = target;
            }

            if (!double.IsNaN(cursor))
                dockGlass.Pointer(new Point(cursor - (Width - plate.Width) / 2, 0), plate.Width, plate.Height);
            else
                dockGlass.Leave();
            LayoutIcons();
            frames++;
            if (!moving)
                StopFrames();
        }

        public double PlateTop
        {
            get
            {
                return Top + Height - (baseSize + 28) - 5;
            }
        }

        internal void MovePin(int from, int to)
        {
            if (from < 0 || from >= Program.S.Pins.Count)
                return;
            to = Math.Max(0, Math.Min(Program.S.Pins.Count - 1, to));
            if (from == to)
                return;
            string path = Program.S.Pins[from];
            Program.S.Pins.RemoveAt(from);
            Program.S.Pins.Insert(to, path);
            Program.Save();
            Rebuild();
        }

        void DragMoveIcon(object sender, MouseEventArgs e)
        {
            if (dragSource == null || e.LeftButton != MouseButtonState.Pressed)
                return;
            Point p = e.GetPosition(canvas);
            if (!dragging && (p - dragDown).Length < 6)
                return;
            if (!dragging)
            {
                dragging = true;
                Mouse.Capture(this);
                dragSource.Opacity = .45;
            }

            e.Handled = true;
            dropIndex = 0;
            for (int i = 1; i <= pinnedCount; i++)
                if (p.X > Canvas.GetLeft(buttons[i]) + buttons[i].Width / 2)
                    dropIndex++;
            double x = dropIndex >= pinnedCount ? Canvas.GetLeft(buttons[pinnedCount]) + buttons[pinnedCount].Width + 4 : Canvas.GetLeft(buttons[dropIndex + 1]) - 5;
            Canvas.SetLeft(marker, x);
            Canvas.SetTop(marker, Height - 64);
            marker.Visibility = Visibility.Visible;
        }

        void DropIcon(object sender, MouseButtonEventArgs e)
        {
            if (dragSource == null)
                return;
            if (!dragging)
            {
                dragSource = null;
                return;
            }

            int from = Program.S.Pins.IndexOf(((DockEntry)dragSource.Tag).PinPath), target = dropIndex;
            if (target > from)
                target--;
            Point p = e.GetPosition(canvas);
            bool inside = p.X >= 0 && p.X <= ActualWidth && p.Y >= 0 && p.Y <= ActualHeight;
            e.Handled = true;
            CancelDrag();
            if (inside)
                MovePin(from, target);
        }

        void CancelDrag()
        {
            if (dragSource != null)
                dragSource.Opacity = 1;
            dragSource = null;
            dragging = false;
            if (marker != null)
                marker.Visibility = Visibility.Collapsed;
            if (IsMouseCaptured)
                ReleaseMouseCapture();
        }

        public void TestHover(double fraction)
        {
            cursor = Width * fraction;
            StartFrames();
        }

        internal RunningApps TestRunning
        {
            get
            {
                return running;
            }
        }

        internal int TestTemporaryCount
        {
            get
            {
                return entries.Count(e => !e.Pinned);
            }
        }

        internal bool TestHasSeparator
        {
            get
            {
                return separatorBefore >= 0;
            }
        }
    }
}
