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

namespace ClearDesk
{
    public class OrganizerWindow : GlassWindow
    {
        readonly FluidGrid items = new FluidGrid();
        readonly Grid chrome = new Grid();
        readonly Border searchBox;
        readonly TextBox search;
        readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        readonly Button foldButton;
        DispatcherTimer debounce;
        bool searchOpen;
        List<string> all = new List<string>();
        public OrganizerWindow(BoxData state) : base(state, state.Title)
        {
            Root.Children.Remove(Layout);
            Layout.Children.Remove(Header);
            Root.Children.Add(chrome);
            chrome.RowDefinitions.Add(new RowDefinition { Height = new GridLength(58) });
            chrome.RowDefinitions.Add(new RowDefinition());
            chrome.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
            Header.Margin = new Thickness(22, 0, 16, 0);
            Header.LastChildFill = true;
            Header.Children.Clear();
            Heading.Text = "▦   " + State.Title;
            Heading.FontSize = 13;
            Heading.VerticalAlignment = VerticalAlignment.Center;
            Heading.ToolTip = "拖动标题移动 · 拖动边缘调整尺寸";
            foldButton = new Button
            {
                Template = FluidUI.IconTemplate(),
                Width = 28,
                Height = 30,
                FontSize = 17,
                Content = "⌃",
                Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255))
            };
            DockPanel.SetDock(foldButton, Dock.Right);
            foldButton.Click += delegate
            {
                ToggleCollapsed();
            };
            Header.Children.Add(foldButton);
            var searchButton = new Button
            {
                Template = FluidUI.IconTemplate(),
                Content = new Border
                {
                    Width = 29,
                    Height = 29,
                    CornerRadius = new CornerRadius(15),
                    Background = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = "⌕",
                        FontSize = 20,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                },
                Width = 32,
                Height = 32,
                ToolTip = "搜索桌面项目"
            };
            DockPanel.SetDock(searchButton, Dock.Right);
            Header.Children.Add(searchButton);
            search = new TextBox
            {
                FontSize = 12,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            searchBox = new Border
            {
                Width = 0,
                Height = 30,
                ClipToBounds = true,
                CornerRadius = new CornerRadius(15),
                Background = new SolidColorBrush(Color.FromArgb(38, 0, 0, 0)),
                Margin = new Thickness(0, 0, 8, 0),
                Child = new Border
                {
                    Padding = new Thickness(12, 0, 10, 0),
                    Child = search
                }
            };
            DockPanel.SetDock(searchBox, Dock.Right);
            Header.Children.Add(searchBox);
            Header.Children.Add(Heading);
            chrome.Children.Add(Header);
            searchButton.Click += delegate
            {
                searchOpen = !searchOpen;
                searchBox.BeginAnimation(WidthProperty, new DoubleAnimation(searchOpen ? Math.Min(190, Width - 180) : 0, TimeSpan.FromMilliseconds(210)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                if (searchOpen)
                    search.Focus();
                else
                    search.Text = "";
            };
            search.TextChanged += delegate
            {
                Filter();
            };
            search.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape)
                {
                    search.Text = "";
                    searchOpen = false;
                    searchBox.BeginAnimation(WidthProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)));
                }
            };
            items.Margin = new Thickness(14, 0, 14, 0);
            Grid.SetRow(items, 1);
            chrome.Children.Add(items);
            items.Open = Program.OpenItem;
            items.MenuFor = ItemMenu;
            items.Reorder = Reorder;
            AddResizeGrip(HorizontalAlignment.Right, VerticalAlignment.Stretch, 8, double.NaN, Cursors.SizeWE, true, false);
            AddResizeGrip(HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, 8, Cursors.SizeNS, false, true);
            AddResizeGrip(HorizontalAlignment.Right, VerticalAlignment.Bottom, 18, 18, Cursors.SizeNWSE, true, true);
            AllowDrop = true;
            DragOver += delegate (object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    e.Effects = DragDropEffects.Link;
                    e.Handled = true;
                }
            };
            Drop += delegate (object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    foreach (var p in (string[])e.Data.GetData(DataFormats.FileDrop))
                        if ((File.Exists(p) || Directory.Exists(p)) && !State.Items.Contains(p))
                            State.Items.Add(p);
                    Program.Save();
                    Refresh();
                    e.Handled = true;
                }
            };
            ContextMenu.Items.Insert(1, Item("网格 / 列表模式", delegate
            {
                State.List = !State.List;
                Filter();
                Program.Save();
            }));
            ContextMenu.Items.Insert(2, Item("新建收纳格", Program.NewBox));
            ContextMenu.Items.Insert(3, Item("刷新桌面项目", Refresh));
            ContextMenu.Items.Insert(4, Item("恢复隐藏的引用", delegate
            {
                State.Excluded.Clear();
                Refresh();
                Program.Save();
            }));
            ContextMenu.Items.Insert(5, Item("重命名收纳格", Rename));
            Refresh();
            Watch();
            Closed += delegate
            {
                if (debounce != null)
                    debounce.Stop();
                foreach (var watcher in watchers)
                    watcher.Dispose();
            };
        }

        protected override bool HeaderDoubleClickCollapses
        {
            get
            {
                return false;
            }
        }

        void ToggleCollapsed()
        {
            State.Collapsed = !State.Collapsed;
            SetCollapsed();
            Program.Save();
        }

        protected override void SetCollapsed()
        {
            if (items == null)
                return;
            items.Visibility = State.Collapsed ? Visibility.Collapsed : Visibility.Visible;
            if (foldButton != null)
            {
                foldButton.Content = State.Collapsed ? "⌄" : "⌃";
                foldButton.ToolTip = State.Collapsed ? "展开收纳，显示图标" : "收起收纳";
                System.Windows.Automation.AutomationProperties.SetName(foldButton, State.Collapsed ? "展开收纳" : "收起收纳");
            }

            double from = ActualHeight > 0 ? ActualHeight : Height;
            double target = State.Collapsed ? 58 : Math.Max(170, State.Height);
            BeginAnimation(HeightProperty, null);
            Height = target;
            if (IsLoaded)
                BeginAnimation(HeightProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(210)) { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        void AddResizeGrip(HorizontalAlignment ha, VerticalAlignment va, double w, double h, Cursor cursor, bool horizontal, bool vertical)
        {
            var grip = new Thumb
            {
                HorizontalAlignment = ha,
                VerticalAlignment = va,
                Width = w,
                Height = h,
                Cursor = cursor,
                Opacity = .06,
                Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Thumb'><Border Background='White'/></ControlTemplate>")
            };
            grip.DragStarted += delegate
            {
                BeginAnimation(HeightProperty, null);
            };
            grip.DragDelta += delegate (object sender, DragDeltaEventArgs e)
            {
                if (State.Locked || State.Collapsed)
                    return;
                var work = SystemParameters.WorkArea;
                if (horizontal)
                    Width = Math.Max(280, Math.Min(Math.Max(280, work.Right - Left), Width + e.HorizontalChange));
                if (vertical)
                    Height = Math.Max(170, Math.Min(Math.Max(170, work.Bottom - Top), Height + e.VerticalChange));
                State.Width = Width;
                State.Height = Height;
            };
            grip.DragCompleted += delegate
            {
                Program.Save();
            };
            Root.Children.Add(grip);
        }

        void Refresh()
        {
            all = State.Items.Concat(State.Desktop ? Program.DesktopItems() : Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).Where(p => !State.Excluded.Contains(p)).ToList();
            Filter();
        }

        void Filter()
        {
            items.SetItems(all.Where(p => Program.DisplayName(p).IndexOf(search.Text ?? "", StringComparison.CurrentCultureIgnoreCase) >= 0), State.List);
        }

        void Reorder(string from, string to)
        {
            if (from == to)
                return;
            var order = all.ToList();
            order.Remove(from);
            int index = order.IndexOf(to);
            if (index < 0)
                return;
            order.Insert(index, from);
            State.Items = order;
            Program.Save();
            Refresh();
        }

        ContextMenu ItemMenu(string path)
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("打开", delegate
            {
                Program.OpenItem(path);
            }));
            menu.Items.Add(Item("固定到 Dock", delegate
            {
                if (!Program.S.Pins.Contains(path))
                {
                    Program.S.Pins.Add(path);
                    Program.Save();
                    Program.RefreshDock();
                }
            }));
            menu.Items.Add(Item("从这个格子移除引用", delegate
            {
                State.Items.Remove(path);
                if (State.Desktop && !State.Excluded.Contains(path))
                    State.Excluded.Add(path);
                Program.Save();
                Refresh();
            }));
            return menu;
        }

        void Rename()
        {
            var dialog = new Window
            {
                Title = "收纳格名称",
                Width = 340,
                Height = 155,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = new SolidColorBrush(Color.FromRgb(25, 32, 43)),
                ResizeMode = ResizeMode.NoResize
            };
            var panel = new StackPanel
            {
                Margin = new Thickness(20)
            };
            var name = new TextBox
            {
                Text = State.Title
            };
            panel.Children.Add(name);
            panel.Children.Add(Program.Button("保存", delegate
            {
                if (!string.IsNullOrWhiteSpace(name.Text))
                {
                    State.Title = name.Text.Trim();
                    Heading.Text = "▦   " + State.Title;
                    Program.Save();
                }

                dialog.Close();
            }));
            dialog.Content = panel;
            dialog.ShowDialog();
        }

        void Watch()
        {
            if (!State.Desktop)
                return;
            debounce = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            debounce.Tick += delegate
            {
                debounce.Stop();
                Refresh();
            };
            foreach (var path in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            }

            )
                try
                {
                    var watcher = new FileSystemWatcher(path)
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                    };
                    FileSystemEventHandler update = delegate
                    {
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            if (!WasClosed)
                            {
                                debounce.Stop();
                                debounce.Start();
                            }
                        }));
                    };
                    watcher.Created += update;
                    watcher.Deleted += update;
                    watcher.Renamed += delegate
                    {
                        update(null, null);
                    };
                    watcher.EnableRaisingEvents = true;
                    watchers.Add(watcher);
                }
                catch (Exception e)
                {
                    Program.Log(e);
                }
        }

        internal FluidGrid TestGrid
        {
            get
            {
                return items;
            }
        }

        internal void TestToggleCollapsed()
        {
            ToggleCollapsed();
        }

        internal void TestResize(double w, double h)
        {
            BeginAnimation(HeightProperty, null);
            Width = w;
            Height = h;
            State.Width = w;
            State.Height = h;
        }
    }

    internal static class PointConversion
    {
        public static Point ToPoint(this System.Drawing.Point p)
        {
            return new Point(p.X, p.Y);
        }
    }
}
