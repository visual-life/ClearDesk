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
        Button searchButton;
        internal const double MinW = 120, MinH = 96, CompactW = 240, MinSearchW = 200;      // 2.3.1: as small as the folder widget; below CompactW the header is thin and the tiles smaller
        MenuItem searchItem;
        internal bool Compact { get { return Width < CompactW; } }
        double HeaderH { get { return Compact ? 36 : 58; } }
        internal void TestToggleSearch() { searchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
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
            Heading.TextTrimming = TextTrimming.CharacterEllipsis;
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
            searchButton = new Button
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
            searchButton.Click += delegate { ToggleSearch(); };
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
            ContextMenu.Items.Insert(2, Item("新建收纳格", delegate { Program.NewBox(PlaceMode.LastRightClick); }));
            ContextMenu.Items.Insert(3, Item("刷新桌面项目", Refresh));
            ContextMenu.Items.Insert(4, Item("恢复隐藏的引用", delegate
            {
                State.Excluded.Clear();
                Refresh();
                Program.Save();
            }));
            ContextMenu.Items.Insert(5, Item("重命名收纳格", Rename));
            systemMenu = new MenuItem { Header = "添加系统图标" };
            systemMenu.SubmenuOpened += delegate { FillSystemMenu(); };
            FillSystemMenu();
            ContextMenu.Items.Insert(6, systemMenu);
            edgeItem = new MenuItem { Header = "贴边隐藏", IsCheckable = true, IsChecked = State.EdgeHide, ToolTip = "拖到屏幕边缘后自动缩进，只露出一条细边；鼠标移上去滑出，移开 0.6 秒后收回" };
            edgeItem.Click += delegate { SetEdgeHide(edgeItem.IsChecked); };
            ContextMenu.Items.Insert(7, edgeItem);
            searchItem = new MenuItem { Header = "显示搜索", IsCheckable = true, IsChecked = !Program.S.HideSearch, ToolTip = "收纳格和启动台的搜索框；隐藏后标题栏更窄，收纳格可以缩得更小（设置里也有这个开关）" };
            searchItem.Click += delegate { Program.SetSearchHidden(!searchItem.IsChecked); };
            ContextMenu.Items.Insert(8, searchItem);
            if (State.EdgeHide) Loaded += delegate { SetEdgeHide(true); };
            Tick(3000, CheckRecycleBin);
            var create = new MenuItem { Header = "新建" };
            create.Items.Add(Item("文件夹", delegate { OrganizerFiles.Prompt(this, State, "新建文件夹", true, Refresh); }));
            create.Items.Add(Item("文本文档", delegate { OrganizerFiles.Prompt(this, State, "新建文本文档.txt", false, Refresh); }));
            create.Items.Add(Item("Markdown 文档", delegate { OrganizerFiles.Prompt(this, State, "新建文档.md", false, Refresh); }));
            ContextMenu.Items.Add(create);
            ContextMenu.Items.Add(new Separator());
            ContextMenu.Items.Add(Item("删除收纳格", delegate
            {
                var entry = WidgetManager.List().FirstOrDefault(x => x.Kind == "box" && x.Data == State);
                if (entry != null) WidgetManager.Delete(entry, text => MessageBox.Show(text, "ClearDesk · 删除收纳格", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
            }));
            ContextMenu.Items.Add(Item("打开新建文件所在文件夹", delegate
            {
                string folder = OrganizerFiles.Folder(State); Directory.CreateDirectory(folder);
                Program.OpenItem(folder);
            }));
            ApplyChrome();
            ApplySearch();
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

        // ---- 2.3.1: small boxes. The header gets thinner, the title shorter, the tiles smaller, the search box out of the way.
        bool lastCompact; bool chromeApplied;
        internal bool SearchAvailable { get { return !Program.S.HideSearch && Width >= MinSearchW; } }

        /// <summary>Header height, corner, tile size and search availability from the box's size. Cheap: only acts when something changed.</summary>
        internal void ApplyChrome()
        {
            bool c = Compact;
            if (!chromeApplied || c != lastCompact)
            {
                chromeApplied = true; lastCompact = c;
                chrome.RowDefinitions[0].Height = new GridLength(HeaderH);
                chrome.RowDefinitions[2].Height = new GridLength(c ? 8 : 14);
                Header.Margin = c ? new Thickness(10, 0, 6, 0) : new Thickness(22, 0, 16, 0);
                Heading.FontSize = c ? 12 : 13;
                Heading.Text = (c ? "" : "▦   ") + State.Title;
                items.Margin = c ? new Thickness(6, 0, 6, 0) : new Thickness(14, 0, 14, 0);
                foldButton.Width = c ? 24 : 28; foldButton.Height = c ? 26 : 30;
                Frame.CornerRadius = new CornerRadius(c ? 14 : 22);
                items.SetMetrics(c ? 68 : 96, c ? 76 : 96, c ? 34 : 44);
            }
            ApplySearch();
        }

        /// <summary>Search button / box follow the setting and the width: hidden settings or too narrow = no search, and nothing is filtered.</summary>
        internal void ApplySearch()
        {
            if (searchButton == null) return;
            bool show = SearchAvailable;
            searchButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (searchItem != null) searchItem.IsChecked = !Program.S.HideSearch;
            if (!show && (searchOpen || searchBox.Width > 0 || (search.Text ?? "").Length > 0))
            {
                searchOpen = false; searchBox.BeginAnimation(WidthProperty, null); searchBox.Width = 0;
                if ((search.Text ?? "").Length > 0) search.Text = "";
            }
        }

        void ToggleSearch()
        {
            if (!SearchAvailable) return;
            searchOpen = !searchOpen;
            searchBox.BeginAnimation(WidthProperty, new DoubleAnimation(searchOpen ? Math.Min(190, Width - 180) : 0, TimeSpan.FromMilliseconds(210)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            if (searchOpen)
                search.Focus();
            else
                search.Text = "";
        }
        internal bool TestSearchVisible { get { return searchButton != null && searchButton.Visibility == Visibility.Visible; } }
        internal MenuItem TestSearchItem { get { return searchItem; } }
        internal double TestHeaderHeight { get { return chrome.RowDefinitions[0].Height.Value; } }

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
            if (foldButton != null)
            {
                foldButton.Content = State.Collapsed ? "⌄" : "⌃";
                foldButton.ToolTip = State.Collapsed ? "展开收纳，显示图标" : "收起收纳";
                System.Windows.Automation.AutomationProperties.SetName(foldButton, State.Collapsed ? "展开收纳" : "收起收纳");
            }

            double from = foldRunning ? Frame.ActualHeight : (ActualHeight > 0 ? ActualHeight : Height);
            double target = State.Collapsed ? HeaderH : Math.Max(MinH, State.Height);
            BeginAnimation(HeightProperty, null);
            EndFold(false);
            if (!IsLoaded || !AnimateFold || Math.Abs(from - target) < 1)
            {
                Height = target;
                items.Visibility = State.Collapsed ? Visibility.Collapsed : Visibility.Visible;
                return;
            }
            // 2.3.1: no window resize and no layout of the tiles per frame. The window is made big enough for the whole animation ONCE; the tiles keep one fixed size
            // (so they are not laid out again) and the frame's height is what moves (cheap: a handful of elements). The real height is set when it ends.
            double big = Math.Max(from, target);
            items.Visibility = Visibility.Visible;
            items.VerticalAlignment = VerticalAlignment.Top;
            items.Height = Math.Max(1, big - HeaderH - chrome.RowDefinitions[2].Height.Value);
            Frame.VerticalAlignment = VerticalAlignment.Top; Frame.ClipToBounds = true; Frame.Height = from;
            foldRunning = true;
            Height = big;
            var anim = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(210)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            anim.Completed += delegate { if (foldRunning) EndFold(true); };
            Frame.BeginAnimation(HeightProperty, anim);
        }

        bool foldRunning; internal static bool AnimateFold = true;
        /// <summary>Ends the fold animation (also when a new one starts): back to the normal layout at the final height.</summary>
        void EndFold(bool final)
        {
            if (!foldRunning) return;
            foldRunning = false;
            Frame.BeginAnimation(HeightProperty, null);
            Frame.ClearValue(HeightProperty); Frame.ClearValue(VerticalAlignmentProperty); Frame.ClipToBounds = false;
            items.ClearValue(HeightProperty); items.ClearValue(VerticalAlignmentProperty);
            double target = State.Collapsed ? HeaderH : Math.Max(MinH, State.Height);
            Height = target;
            items.Visibility = State.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        }
        internal bool TestFoldRunning { get { return foldRunning; } }

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
            Point start = new Point(); double startW = 0, startH = 0;
            grip.DragStarted += delegate
            {
                BeginAnimation(HeightProperty, null);
                start = CursorInfo.Dip(this); startW = Width; startH = Height;
            };
            grip.DragDelta += delegate (object sender, DragDeltaEventArgs e)
            {
                if (State.Locked || State.Collapsed)
                    return;
                var work = SystemParameters.WorkArea;
                var now = CursorInfo.Dip(this);
                double w = horizontal ? startW + now.X - start.X : Width, h = vertical ? startH + now.Y - start.Y : Height;
                if (Program.S.Magnet && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
                {
                    var r = MagnetSnap.SnapResize(new Rect(Left, Top, Math.Max(1, w), Math.Max(1, h)), Program.SnapTargets(this), CursorInfo.Areas(this), MagnetSnap.Threshold, horizontal, vertical);
                    if (r.SnappedX) w = r.X - Left;
                    if (r.SnappedY) h = r.Y - Top;
                    GuideOverlay.Show(r.Guides);
                }
                else GuideOverlay.Hide();
                if (horizontal)
                    Width = Math.Max(MinW, Math.Min(Math.Max(MinW, work.Right - Left), w));
                if (vertical)
                    Height = Math.Max(MinH, Math.Min(Math.Max(MinH, work.Bottom - Top), h));
                State.Width = Width;
                State.Height = Height;
                ApplyChrome();
            };
            grip.DragCompleted += delegate
            {
                GuideOverlay.Hide();
                Program.Save();
            };
            Root.Children.Add(grip);
        }

        MenuItem systemMenu; bool? binFull;

        void FillSystemMenu()
        {
            systemMenu.Items.Clear();
            foreach (var item in SystemItems.All)
            {
                var entry = item;
                systemMenu.Items.Add(Item(entry.Name, delegate { SetSystemItem(entry.Path, !HasItem(entry.Path)); }, HasItem(entry.Path)));
            }
        }

        internal bool HasItem(string path) { return all != null && all.Contains(path, StringComparer.OrdinalIgnoreCase); }

        /// <summary>Adds (or removes) one of the system icons: 此电脑, 回收站, 控制面板, 用户文件夹, 网络.</summary>
        internal void SetSystemItem(string path, bool present)
        {
            if (!SystemItems.IsSystem(path)) return;
            if (present)
            {
                State.Excluded.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
                if (!State.Items.Contains(path, StringComparer.OrdinalIgnoreCase)) State.Items.Add(path);
            }
            else
            {
                State.Items.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
                if (State.Desktop && !State.Excluded.Contains(path, StringComparer.OrdinalIgnoreCase)) State.Excluded.Add(path);
            }
            Program.Save(); Refresh(); FillSystemMenu();
        }

        internal MenuItem TestSystemMenu { get { return systemMenu; } }
        internal ContextMenu TestItemMenu(string path) { return ItemMenu(path); }

        void CheckRecycleBin()
        {
            if (!HasItem(SystemItems.RecycleBin.Path)) { binFull = null; return; }
            bool full = SystemItems.RecycleBinFull();
            if (binFull.HasValue && binFull.Value != full) items.RefreshIcon(SystemItems.RecycleBin.Path);
            binFull = full;
        }

        internal void TestCheckRecycleBin() { CheckRecycleBin(); }

        void Refresh()
        {
            all = State.Items.Concat(State.Desktop ? Program.DesktopItems().Concat(SystemItems.OnDesktop()) : Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).Where(p => !State.Excluded.Contains(p)).ToList();
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

        MenuItem edgeItem; EdgeHider edge;
        internal EdgeHider TestEdge { get { return edge; } }
        internal MenuItem TestEdgeItem { get { return edgeItem; } }
        /// <summary>2.3.0: 贴边隐藏 on / off for this organizer.</summary>
        internal void SetEdgeHide(bool on)
        {
            State.EdgeHide = on; if (edgeItem != null) edgeItem.IsChecked = on;
            Program.Save();
            if (on) { if (edge == null) edge = new EdgeHider(this); edge.Start(); }
            else if (edge != null) edge.Stop();
        }

        ContextMenu ItemMenu(string path)
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("打开", delegate
            {
                Program.OpenItem(path);
            }));
            if (AdminRun.Supported(path)) { var admin = Item("以管理员身份运行", delegate { AdminRun.Run(path); }); admin.Tag = "admin"; menu.Items.Add(admin); }
            if (SystemItems.IsRecycleBin(path))
            {
                var empty = Item("清空回收站", delegate { if (SystemItems.Empty(new System.Windows.Interop.WindowInteropHelper(this).Handle)) { binFull = null; CheckRecycleBin(); } });
                empty.IsEnabled = SystemItems.RecycleBinFull();
                menu.Opened += delegate { empty.IsEnabled = SystemItems.RecycleBinFull(); };
                menu.Items.Add(empty);
            }
            if (!SystemItems.IsSystem(path)) menu.Items.Add(Item("固定到 Dock", delegate
            {
                if (!Program.S.Pins.Contains(path))
                {
                    Program.S.Pins.Add(path);
                    Program.Save();
                    Program.RefreshDock();
                }
            }));
            if (!SystemItems.IsSystem(path) && !LaunchpadStore.IsPinned(path)) menu.Items.Add(Item("固定到启动台", delegate { LaunchpadStore.Pin(new[] { path }); }));
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

        internal void SetTitle(string title)
        {
            State.Title = title;
            Heading.Text = (Compact ? "" : "▦   ") + title;
            Title = "ClearDesk · " + title;
        }

        internal string TestHeading { get { return Heading.Text; } }

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
                    SetTitle(name.Text.Trim());
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

        internal void RefreshReferences()
        {
            SetCollapsed(); Refresh();
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
