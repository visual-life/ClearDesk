using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ClearDesk
{
    internal static class FluidUI
    {
        static ControlTemplate iconTemplate;
        public static ControlTemplate IconTemplate()
        {
            if (iconTemplate == null)
                iconTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Border Background='Transparent'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border></ControlTemplate>");
            return iconTemplate;
        }

        public static void Animate(UIElement element, double opacity, double scale, int duration)
        {
            element.RenderTransformOrigin = new Point(.5, .85);
            var t = element.RenderTransform as ScaleTransform;
            if (t == null)
            {
                t = new ScaleTransform(1, 1);
                element.RenderTransform = t;
            }

            var ease = new CubicEase
            {
                EasingMode = EasingMode.EaseOut
            };
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(duration)) { EasingFunction = ease });
            t.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, TimeSpan.FromMilliseconds(duration)) { EasingFunction = ease });
            t.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, TimeSpan.FromMilliseconds(duration)) { EasingFunction = ease });
        }
    }

    public class LaunchpadWindow : GlassWindow
    {
        FluidGrid icons = new FluidGrid
        {
            CellMinimum = 112,
            RowHeight = 112,
            IconSize = 56
        };
        TextBox search;
        List<string> paths = new List<string>();
        bool dismissing;
        OutsideClickWatcher outside;
        DispatcherTimer deactivateTimer;
        internal static DateTime AnimationUntil;
        public LaunchpadWindow() : base(CreateState(), "启动台")
        {
            AnimationUntil = DateTime.UtcNow.AddMilliseconds(350);
            Root.Opacity = 0;
            Root.RenderTransform = new TranslateTransform(0, 10);
            Topmost = true;
            ShowActivated = true;
            Header.Visibility = Visibility.Collapsed;
            Layout.Margin = new Thickness(28, 22, 28, 20);
            Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Body.RowDefinitions.Add(new RowDefinition());
            Body.Height = Height - 44;
            var bar = new Grid
            {
                Margin = new Thickness(30, 0, 30, 26)
            };
            bar.ColumnDefinitions.Add(new ColumnDefinition());
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var searchBorder = new Border
            {
                CornerRadius = new CornerRadius(20),
                BorderBrush = new SolidColorBrush(Color.FromArgb(130, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                Padding = new Thickness(14, 8, 14, 8)
            };
            var sg = new Grid();
            var hint = Program.Text("⌕  搜索桌面项目", 14, "#CEDAE6");
            hint.IsHitTestVisible = false;
            search = new TextBox
            {
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                FontSize = 14,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            sg.Children.Add(hint);
            sg.Children.Add(search);
            searchBorder.Child = sg;
            bar.Children.Add(searchBorder);
            search.TextChanged += delegate
            {
                hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
                Refresh();
            };
            var add = Program.Button("＋", AddFiles);
            add.Template = FluidUI.IconTemplate();
            add.FontSize = 28;
            add.Width = 44;
            add.Height = 38;
            Grid.SetColumn(add, 1);
            bar.Children.Add(add);
            Body.Children.Add(bar);
            Grid.SetRow(icons, 1);
            Body.Children.Add(icons);
            icons.Open = delegate (string path)
            {
                dismissing = true;
                Program.OpenItem(path);
                Close();
            };
            icons.MenuFor = BuildItemMenu;
            paths = Program.DesktopItems().Concat(Program.S.LaunchpadItems).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Refresh();
            KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape)
                    Dismiss();
            };
            deactivateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(120)
            };
            deactivateTimer.Tick += delegate
            {
                deactivateTimer.Stop();
                if (!dismissing && !IsActive && !OwnsPopup(Native.GetForegroundWindow()))
                    Dismiss();
            };
            Deactivated += delegate
            {
                if (!Program.Rendering)
                    deactivateTimer.Start();
            };
            AllowDrop = true;
            Drop += delegate (object sender, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    foreach (var path in (string[])e.Data.GetData(DataFormats.FileDrop))
                        if (!Program.S.LaunchpadItems.Contains(path))
                            Program.S.LaunchpadItems.Add(path);
                    Program.Save();
                    paths = Program.DesktopItems().Concat(Program.S.LaunchpadItems).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    Refresh();
                }
            };
            Loaded += async delegate
            {
                search.Focus();
                Activate();
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
                {
                    if (!WasClosed && !dismissing)
                    {
                        UpdateLayout();
                        icons.SettleLayout();
                        AnimatePopup(true);
                    }
                }));
                if (!Program.Rendering)
                    try
                    {
                        var watcher = await System.Threading.Tasks.Task.Run(() => new OutsideClickWatcher(CheckOutside));
                        if (WasClosed)
                            await System.Threading.Tasks.Task.Run(() => watcher.Dispose());
                        else
                            outside = watcher;
                    }
                    catch (Exception e)
                    {
                        Program.Log(e);
                    }
            };
            Closed += delegate
            {
                deactivateTimer.Stop();
                if (outside != null)
                {
                    var watcher = outside;
                    outside = null;
                    System.Threading.Tasks.Task.Run(() => watcher.Dispose());
                }
            };
        }

        internal ContextMenu BuildItemMenu(string path)
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("固定到 Dock", delegate
            {
                if (!Program.S.Pins.Contains(path)) { Program.S.Pins.Add(path); Program.Save(); Program.RefreshDock(); }
            }));
            var send = new MenuItem { Header = "发送至桌面收纳" };
            foreach (var box in Program.S.Boxes)
            {
                var target = box;
                send.Items.Add(Item(box.Title + (box.Enabled ? "" : "（已关闭）"), delegate { SendItem(path, target); }));
            }
            if (send.Items.Count > 0) send.Items.Add(new Separator());
            send.Items.Add(Item("新建收纳格并添加", delegate { SendItem(path, null); }));
            menu.Items.Add(send);
            return menu;
        }
        void SendItem(string path, BoxData box)
        {
            try { Program.SendToOrganizer(path, box); }
            catch (Exception error) { MessageBox.Show(error.Message, "未能添加到收纳"); }
        }

        protected override void Material(string material)
        {
            base.Material("liquid");
        }

        protected override ContextMenu Menu()
        {
            var menu = base.Menu();
            menu.Items.RemoveAt(0);
            menu.Items.Insert(0, new MenuItem { Header = "启动台 · 实时透明", IsEnabled = false });
            return menu;
        }

        bool OwnsPopup(IntPtr h)
        {
            if (h == IntPtr.Zero)
                return false;
            var own = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (h == own)
                return true;
            var source = System.Windows.Interop.HwndSource.FromHwnd(h);
            if (source != null && source.RootVisual != null && source.RootVisual.GetType().Name == "PopupRoot")
                return true;
            for (int i = 0; i < 8 && h != IntPtr.Zero; i++)
            {
                h = Native.GetParent(h);
                if (h == own)
                    return true;
            }

            return false;
        }

        internal void CheckOutside(Native.POINT point)
        {
            if (dismissing || WasClosed || !IsVisible)
                return;
            Native.RECT rect;
            var own = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (!Native.GetWindowRect(own, out rect))
                return;
            if (point.X >= rect.L && point.X < rect.R && point.Y >= rect.T && point.Y < rect.B)
                return;
            if (!OwnsPopup(Native.WindowFromPoint(point)))
                Dismiss();
        }

        static BoxData CreateState()
        {
            var wa = SystemParameters.WorkArea;
            var dock = Program.App.Windows.OfType<DockWindow>().FirstOrDefault(w => !w.WasClosed);
            double bottom = dock == null ? wa.Bottom - 100 : dock.PlateTop - 14;
            bottom = Math.Max(wa.Top + 180, bottom);
            double width = Math.Min(880, wa.Width - 32), height = Math.Min(520, bottom - wa.Top - 24);
            double center = dock == null ? wa.Left + wa.Width / 2 : dock.Left + dock.Width / 2;
            var state = Program.Widget("launchpad", 0, 0, width, height);
            state.X = Math.Max(wa.Left + 16, Math.Min(wa.Right - width - 16, center - width / 2));
            state.Y = bottom - height;
            state.Width = width;
            state.Height = height;
            state.Collapsed = false;
            state.Locked = true;
            state.Material = "liquid";
            return state;
        }

        void AnimatePopup(bool show)
        {
            var move = Root.RenderTransform as TranslateTransform;
            if (move == null)
            {
                move = new TranslateTransform();
                Root.RenderTransform = move;
            }

            var ease = new CubicEase
            {
                EasingMode = EasingMode.EaseOut
            };
            var time = TimeSpan.FromMilliseconds(show ? 160 : 100);
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, time) { EasingFunction = ease });
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(show ? 0 : 6, time) { EasingFunction = ease });
        }

        void AddFiles()
        {
            dismissing = true;
            var d = new OpenFileDialog
            {
                Title = "添加到启动台",
                Multiselect = true,
                Filter = "所有文件|*.*"
            };
            if (d.ShowDialog(this) == true)
            {
                foreach (var p in d.FileNames)
                    if (!Program.S.LaunchpadItems.Contains(p))
                        Program.S.LaunchpadItems.Add(p);
                Program.Save();
                paths = Program.DesktopItems().Concat(Program.S.LaunchpadItems).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                Refresh();
            }

            dismissing = false;
        }

        internal bool TestGridSettled
        {
            get
            {
                return icons.PositionsSettled;
            }
        }

        public void Dismiss()
        {
            if (dismissing || WasClosed)
                return;
            dismissing = true;
            AnimatePopup(false);
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(110)
            };
            timer.Tick += delegate
            {
                timer.Stop();
                Close();
            };
            timer.Start();
        }

        void Refresh()
        {
            icons.SetItems(paths.Where(p => Program.DisplayName(p).IndexOf(search.Text ?? "", StringComparison.CurrentCultureIgnoreCase) >= 0));
        }
    }
}
