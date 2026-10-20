using System;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClearDesk
{
    // Stable tile instances, animated translation on reflow, and bounded smooth scrolling.
    // No ScrollViewer chrome, wallpaper snapshots, polling, or idle render subscription.
    public sealed class FluidGrid : Grid
    {
        sealed class Tile
        {
            public string Path;
            public Border Host;
            public Button Button;
            public StackPanel Stack;
            public Image Icon;
            public TextBlock Label;
            public TranslateTransform Position = new TranslateTransform();
            public double X, Y, TX, TY;
            public bool Placed;
        }

        readonly Canvas canvas = new Canvas();
        readonly TranslateTransform pan = new TranslateTransform();
        readonly List<Tile> tiles = new List<Tile>();
        readonly GradientStop topFade = new GradientStop(Colors.White, 0), topSolid = new GradientStop(Colors.White, .04), bottomSolid = new GradientStop(Colors.White, .94), bottomFade = new GradientStop(Colors.White, 1);
        bool animating, listMode;
        double offset, targetOffset, contentHeight;
        TimeSpan last;
        public int ItemCount
        {
            get
            {
                return tiles.Count;
            }
        }

        public int Columns { get; private set; }

        public double Offset
        {
            get
            {
                return offset;
            }
        }

        public double MaxOffset
        {
            get
            {
                return Math.Max(0, contentHeight - ActualHeight);
            }
        }

        public bool IsAnimating
        {
            get
            {
                return animating;
            }
        }

        public static int ActiveAnimations;
        public double CellMinimum = 96, RowHeight = 96, IconSize = 44;
        public Action<string> Open;
        public Func<string, ContextMenu> MenuFor;
        public Action<string, string> Reorder;
        public FluidGrid()
        {
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            ClipToBounds = true;
            Focusable = true;
            Background = Brushes.Transparent;
            Children.Add(canvas);
            canvas.RenderTransform = pan;
            var mask = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            mask.GradientStops.Add(topFade);
            mask.GradientStops.Add(topSolid);
            mask.GradientStops.Add(bottomSolid);
            mask.GradientStops.Add(bottomFade);
            SizeChanged += delegate
            {
                Reflow();
            };
            PreviewMouseWheel += delegate (object sender, MouseWheelEventArgs e)
            {
                ScrollBy(-e.Delta / 120.0 * 84);
                e.Handled = true;
            };
            KeyDown += delegate (object sender, KeyEventArgs e)
            {
                if (e.Key == Key.PageDown)
                {
                    ScrollBy(ActualHeight * .8);
                    e.Handled = true;
                }
                else if (e.Key == Key.PageUp)
                {
                    ScrollBy(-ActualHeight * .8);
                    e.Handled = true;
                }
                else if (e.Key == Key.Down)
                {
                    ScrollBy(RowHeight);
                    e.Handled = true;
                }
                else if (e.Key == Key.Up)
                {
                    ScrollBy(-RowHeight);
                    e.Handled = true;
                }
                else if (e.Key == Key.Home)
                {
                    ScrollBy(-contentHeight);
                    e.Handled = true;
                }
                else if (e.Key == Key.End)
                {
                    ScrollBy(contentHeight);
                    e.Handled = true;
                }
            };
            Loaded += delegate
            {
                Reflow();
            };
            Unloaded += delegate
            {
                Stop();
            };
        }

        public void SetItems(IEnumerable<string> paths, bool list = false)
        {
            listMode = list;
            var ordered = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var old = tiles.ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);
            tiles.Clear();
            foreach (var path in ordered)
            {
                Tile tile;
                if (!old.TryGetValue(path, out tile))
                {
                    tile = Create(path);
                    canvas.Children.Add(tile.Host);
                }
                else
                    old.Remove(path);
                Configure(tile);
                tiles.Add(tile);
            }

            foreach (var removed in old.Values)
                canvas.Children.Remove(removed.Host);
            Reflow();
        }

        Tile Create(string path)
        {
            var tile = new Tile
            {
                Path = path
            };
            tile.Icon = new Image
            {
                Stretch = Stretch.Uniform
            };
            IconCache.Bind(tile.Icon, path);
            tile.Label = Program.Text(Program.DisplayName(path), 12);
            TextOptions.SetTextFormattingMode(tile.Label, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(tile.Label, TextRenderingMode.Grayscale);
            tile.Label.TextWrapping = TextWrapping.NoWrap;
            tile.Label.TextTrimming = TextTrimming.CharacterEllipsis;
            tile.Label.IsHitTestVisible = false;
            tile.Label.FontWeight = FontWeights.Medium;
            tile.Stack = new StackPanel();
            tile.Stack.Children.Add(tile.Icon);
            tile.Stack.Children.Add(tile.Label);
            tile.Button = new Button
            {
                Content = tile.Stack,
                Template = FluidUI.IconTemplate(),
                ToolTip = Program.DisplayName(path),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                RenderTransform = new ScaleTransform(1, 1),
                RenderTransformOrigin = new Point(.5, .5),
                AllowDrop = true
            };
            ToolTipService.SetInitialShowDelay(tile.Button, 700);
            tile.Host = new Border
            {
                Child = tile.Button,
                RenderTransform = tile.Position
            };
            Point down = new Point();
            bool dragged = false;
            tile.Button.PreviewMouseLeftButtonDown += delegate (object sender, MouseButtonEventArgs e)
            {
                down = e.GetPosition(this);
                dragged = false;
                FluidUI.Animate(tile.Icon, 1, .94, 85);
            };
            tile.Button.PreviewMouseLeftButtonUp += delegate
            {
                FluidUI.Animate(tile.Icon, 1, tile.Button.IsMouseOver ? 1.08 : 1, 180);
            };
            tile.Button.MouseEnter += delegate
            {
                FluidUI.Animate(tile.Icon, 1, 1.08, 160);
            };
            tile.Button.MouseLeave += delegate
            {
                FluidUI.Animate(tile.Icon, 1, 1, 220);
            };
            tile.Button.Click += delegate
            {
                if (!dragged && Open != null)
                    Open(path);
            };
            tile.Button.ContextMenuOpening += delegate
            {
                if (MenuFor != null)
                    tile.Button.ContextMenu = MenuFor(path);
            };
            tile.Button.ContextMenu = new ContextMenu();
            tile.Button.PreviewMouseMove += delegate (object sender, MouseEventArgs e)
            {
                if (Reorder == null || e.LeftButton != MouseButtonState.Pressed || dragged)
                    return;
                var p = e.GetPosition(this);
                if ((p - down).Length < 8)
                    return;
                dragged = true;
                var data = new DataObject("ClearDesk.Reference", path);
                DragDrop.DoDragDrop(tile.Button, data, DragDropEffects.Move);
                FluidUI.Animate(tile.Icon, 1, 1, 180);
            };
            tile.Button.Drop += delegate (object sender, DragEventArgs e)
            {
                if (e.Data.GetDataPresent("ClearDesk.Reference") && Reorder != null)
                {
                    Reorder((string)e.Data.GetData("ClearDesk.Reference"), path);
                    e.Handled = true;
                }
            };
            return tile;
        }

        void Configure(Tile t)
        {
            t.Stack.Orientation = listMode ? Orientation.Horizontal : Orientation.Vertical;
            t.Stack.HorizontalAlignment = listMode ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            t.Stack.VerticalAlignment = VerticalAlignment.Center;
            t.Icon.Width = t.Icon.Height = listMode ? 30 : IconSize;
            t.Icon.Margin = listMode ? new Thickness(12, 0, 14, 0) : new Thickness(0, 6, 0, 8);
            t.Label.TextAlignment = listMode ? TextAlignment.Left : TextAlignment.Center;
            t.Label.VerticalAlignment = VerticalAlignment.Center;
            t.Label.Margin = listMode ? new Thickness(0) : new Thickness(4, 0, 4, 0);
        }

        void Reflow()
        {
            if (ActualWidth < 1)
                return;
            double width = ActualWidth;
            Columns = listMode ? 1 : Math.Max(1, (int)(width / CellMinimum));
            double cell = width / Columns;
            double row = listMode ? 48 : RowHeight;
            int i = 0;
            foreach (var t in tiles)
            {
                t.Host.Width = t.Button.Width = cell;
                t.Host.Height = t.Button.Height = row;
                t.Label.MaxWidth = Math.Max(20, cell - (listMode ? 65 : 8));
                t.TX = (i % Columns) * cell;
                t.TY = (i / Columns) * row;
                if (!t.Placed)
                {
                    t.X = t.TX;
                    t.Y = t.TY;
                    t.Position.X = Snap(t.X);
                    t.Position.Y = Snap(t.Y);
                    t.Placed = true;
                }

                i++;
            }

            contentHeight = Math.Ceiling((double)tiles.Count / Columns) * row;
            canvas.Width = width;
            canvas.Height = Math.Max(contentHeight, ActualHeight);
            targetOffset = Math.Max(0, Math.Min(MaxOffset, targetOffset));
            offset = Math.Max(0, Math.Min(MaxOffset, offset));
            FadeEdges();
            Start();
        }

        public void ScrollBy(double amount)
        {
            targetOffset = Math.Max(0, Math.Min(MaxOffset, targetOffset + amount));
            Start();
        }

        void FadeEdges()
        {
            double fraction = Math.Min(.15, 14 / Math.Max(1, ActualHeight));
            topSolid.Offset = fraction;
            bottomSolid.Offset = 1 - fraction;
            topFade.Color = offset > 1 ? Colors.Transparent : Colors.White;
            bottomFade.Color = offset < MaxOffset - 1 ? Colors.Transparent : Colors.White;
        }

        void Start()
        {
            if (animating || !IsLoaded)
                return;
            animating = true;
            ActiveAnimations++;
            last = TimeSpan.Zero;
            CompositionTarget.Rendering += Frame;
        }

        void Stop()
        {
            if (!animating)
                return;
            animating = false;
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
            double blend = 1 - Math.Exp(-19 * dt);
            bool moving = false;
            foreach (var t in tiles)
            {
                double dx = t.TX - t.X, dy = t.TY - t.Y;
                if (Math.Abs(dx) + Math.Abs(dy) > .15)
                {
                    t.X += dx * blend;
                    t.Y += dy * blend;
                    moving = true;
                }
                else
                {
                    t.X = t.TX;
                    t.Y = t.TY;
                }

                t.Position.X = Snap(t.X);
                t.Position.Y = Snap(t.Y);
            }

            double d = targetOffset - offset;
            if (Math.Abs(d) > .12)
            {
                offset += d * blend;
                moving = true;
            }
            else
                offset = targetOffset;
            pan.Y = -Snap(offset);
            FadeEdges();
            if (!moving)
                Stop();
        }

        double Snap(double value)
        {
            var source = PresentationSource.FromVisual(this);
            double scale = source == null ? 1 : source.CompositionTarget.TransformToDevice.M11;
            return Math.Round(value * scale) / scale;
        }

        internal void SettleLayout()
        {
            Reflow();
            Stop();
            foreach (var t in tiles)
            {
                t.X = t.TX;
                t.Y = t.TY;
                t.Position.X = Snap(t.X);
                t.Position.Y = Snap(t.Y);
            }

            offset = targetOffset;
            pan.Y = -Snap(offset);
            FadeEdges();
        }

        internal bool PositionsSettled
        {
            get
            {
                return tiles.All(t => Math.Abs(t.X - t.TX) + Math.Abs(t.Y - t.TY) < .2);
            }
        }
    }
}
