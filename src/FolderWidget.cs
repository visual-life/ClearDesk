using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClearDesk
{
    /// <summary>2.2.5 "文件夹映射": what one widget instance remembers (widgets\folder*.json).</summary>
    public sealed class FolderData
    {
        public string Path = "";
        public int IconSize = 44;
        public bool ShowHidden;
        /// <summary>2.2.6: "list" (Explorer-like rows) or "icons" (tiles). null in 2.2.5 files.</summary>
        public string View;
    }

    internal sealed class FolderEntry
    {
        public string Path, Name; public bool IsDir;
        public override string ToString() { return Name; }
    }

    internal static class FolderLogic
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int StrCmpLogicalW(string a, string b);
        internal const int MaxItems = 600;

        /// <summary>The folder's direct content, folders first, then by name the way Explorer sorts ("2" before "10"). Hidden / system files only on request.</summary>
        internal static List<FolderEntry> List(string folder, bool showHidden, out int total, out string error)
        {
            total = 0; error = null;
            var list = new List<FolderEntry>();
            try
            {
                var di = new DirectoryInfo(folder);
                if (!di.Exists) { error = "文件夹不存在或暂时无法访问（U 盘 / 网络盘未连接？）"; return list; }
                foreach (var fsi in di.EnumerateFileSystemInfos())
                {
                    FileAttributes a; try { a = fsi.Attributes; } catch { continue; }
                    if (!showHidden && ((a & FileAttributes.Hidden) != 0 || (a & FileAttributes.System) != 0)) continue;
                    total++;
                    bool dir = (a & FileAttributes.Directory) != 0;
                    string name = fsi.Name;
                    if (!dir && name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !dir && name.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) name = System.IO.Path.GetFileNameWithoutExtension(name);
                    list.Add(new FolderEntry { Path = fsi.FullName, Name = name, IsDir = dir });
                }
            }
            catch (UnauthorizedAccessException) { error = "没有权限读取这个文件夹"; }
            catch (Exception e) { error = "读取文件夹失败：" + e.Message; }
            list.Sort((x, y) => x.IsDir != y.IsDir ? (x.IsDir ? -1 : 1) : StrCmpLogicalW(x.Name, y.Name));
            if (list.Count > MaxItems) list = list.Take(MaxItems).ToList();
            return list;
        }

        internal static int Columns(double width, int iconSize) { return Math.Max(1, (int)(width / (iconSize + 40))); }
    }

    /// <summary>
    /// 2.2.5 "文件夹映射" widget: shows the files and sub-folders of one folder with their Explorer icons and names; double-click opens, right-click gives the
    /// item / widget menus (white Windows 11 menu style), the content follows the folder live (FileSystemWatcher), wheel scrolling without a scroll bar.
    /// Free width / height (the tiles reflow), several instances, custom background and frameless like every widget. Nothing in the folder is changed by ClearDesk.
    /// </summary>
    public class FolderWindow : ToolWidget
    {
        FolderData data;
        readonly WrapPanel grid = new WrapPanel();
        readonly ScrollViewer scroller;
        readonly TextBlock title, note;
        readonly StackPanel emptyPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        FileSystemWatcher watcher;
        readonly DispatcherTimer debounce, rewatch;
        Border selected;
        internal FolderData Data { get { return data; } }
        internal int Shown { get { return grid.Children.Count; } }
        internal WrapPanel Tiles { get { return grid; } }
        internal ScrollViewer Scroller { get { return scroller; } }
        internal int Refreshes;
        internal Action<string> Open = p => Program.OpenItem(p);
        /// <summary>Test hook: picks a folder (default: the folder dialog).</summary>
        internal Func<string> Pick;

        public FolderWindow() : base("folder", "文件夹映射", SystemParameters.WorkArea.Left + 380, SystemParameters.WorkArea.Top + 120, 380, 280)
        {
            data = ToolStore.Load<FolderData>(State.Id);
            if (data.View != "list" && data.View != "icons") data.View = string.IsNullOrEmpty(data.Path) ? "list" : "icons";     // new widget: list; a 2.2.5 widget keeps its tiles
            Heading.Text = "文件夹映射";
            title = Heading;
            var pick = IconButton("\uE8DA", "选择文件夹…", delegate { ChooseFolder(); });
            AddHeaderButton(pick);
            grid.Margin = new Thickness(0, 0, 0, 4);
            scroller = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, PanningMode = PanningMode.VerticalOnly };
            scroller.PreviewMouseWheel += (s, e) => { scroller.ScrollToVerticalOffset(scroller.VerticalOffset - e.Delta / 2.4); e.Handled = true; };
            note = Label("", 11, Dim); note.Margin = new Thickness(2, 4, 0, 0); note.TextWrapping = TextWrapping.Wrap; note.Visibility = Visibility.Collapsed; DockPanel.SetDock(note, Dock.Bottom);
            var hint = Label("选择一个文件夹，把里面的文件和子文件夹显示在桌面上", 12, Brushes.White); hint.TextWrapping = TextWrapping.Wrap; hint.TextAlignment = TextAlignment.Center; hint.Margin = new Thickness(10, 0, 10, 8);
            var big = TextButton("选择文件夹…", delegate { ChooseFolder(); }, true); big.HorizontalAlignment = HorizontalAlignment.Center;
            emptyPanel.Children.Add(hint); emptyPanel.Children.Add(big);
            var root = new Grid(); root.Children.Add(scroller); root.Children.Add(emptyPanel);
            var host = new DockPanel(); host.Children.Add(note); host.Children.Add(root);
            Body.Children.Add(host);
            scroller.SizeChanged += delegate { if (IsList) { double w = RowWidth(); foreach (FrameworkElement c in grid.Children) c.Width = w; } };
            AddMenuItem("视图：列表 / 图标", delegate { SetView(IsList ? "icons" : "list"); });
            AddMenuItem("刷新", Refresh);
            AddMenuItem("图标大小：大 / 中 / 小", delegate { data.IconSize = data.IconSize >= 56 ? 32 : data.IconSize >= 44 ? 64 : 44; Save(); Refresh(); });
            AddMenuItem("显示 / 不显示隐藏文件", delegate { data.ShowHidden = !data.ShowHidden; Save(); Refresh(); });
            AddMenuItem("在资源管理器中打开", delegate { if (Directory.Exists(data.Path)) Open(data.Path); });
            AddMenuItem("选择文件夹…", ChooseFolder);
            debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
            debounce.Tick += delegate { debounce.Stop(); Refresh(); };
            rewatch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
            rewatch.Tick += delegate { if (Directory.Exists(data.Path)) { rewatch.Stop(); Watch(); Refresh(); } };
            Watch();
            Refresh();
        }

        void Save() { ToolStore.Save(State.Id, data); }

        internal bool IsList { get { return data.View == "list"; } }
        internal void SetView(string view) { data.View = view == "icons" ? "icons" : "list"; Save(); Refresh(); }
        internal const double RowHeight = 26, RowFont = 12.5, RowIcon = 18;      // base units: the widget's content scale makes them grow / shrink with the widget
        double RowWidth() { double w = scroller.ViewportWidth > 0 ? scroller.ViewportWidth : scroller.ActualWidth; return Math.Max(60, (w > 0 ? w : 340) - 2); }

        void ChooseFolder()
        {
            string chosen = null;
            if (Pick != null) chosen = Pick();
            else
                using (var host = new DialogHost())
                using (var d = new System.Windows.Forms.FolderBrowserDialog { Description = "选择要映射到桌面的文件夹", ShowNewFolderButton = false, SelectedPath = Directory.Exists(data.Path) ? data.Path : "" })
                    if (d.ShowDialog(host.Win32) == System.Windows.Forms.DialogResult.OK) chosen = d.SelectedPath;
            if (string.IsNullOrEmpty(chosen)) return;
            SetFolder(chosen);
        }

        internal void SetFolder(string path)
        {
            data.Path = path; Save();
            Watch(); Refresh();
        }

        void Watch()
        {
            if (watcher != null) { try { watcher.EnableRaisingEvents = false; watcher.Dispose(); } catch { } watcher = null; }
            if (string.IsNullOrEmpty(data.Path) || !Directory.Exists(data.Path)) { if (!string.IsNullOrEmpty(data.Path)) rewatch.Start(); return; }
            try
            {
                watcher = new FileSystemWatcher(data.Path) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes, InternalBufferSize = 32768 };
                FileSystemEventHandler changed = (s, e) => Soon();
                watcher.Created += changed; watcher.Deleted += changed; watcher.Changed += changed;
                watcher.Renamed += (s, e) => Soon();
                watcher.Error += (s, e) => Dispatcher.BeginInvoke(new Action(delegate { Watch(); Refresh(); }));      // buffer overflow or the folder went away
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception e) { Program.Log(e); rewatch.Start(); }
        }

        void Soon() { try { Dispatcher.BeginInvoke(new Action(delegate { debounce.Stop(); debounce.Start(); })); } catch { } }

        internal void Refresh()
        {
            Refreshes++;
            grid.Children.Clear(); selected = null;
            bool hasFolder = !string.IsNullOrEmpty(data.Path);
            title.Text = hasFolder ? FolderName(data.Path) : "文件夹映射";
            emptyPanel.Visibility = hasFolder ? Visibility.Collapsed : Visibility.Visible;
            scroller.Visibility = hasFolder ? Visibility.Visible : Visibility.Collapsed;
            if (!hasFolder) { note.Visibility = Visibility.Collapsed; return; }
            int total; string error;
            var items = FolderLogic.List(data.Path, data.ShowHidden, out total, out error);
            if (error != null) { note.Text = error; note.Visibility = Visibility.Visible; if (!Directory.Exists(data.Path)) rewatch.Start(); }
            else if (total == 0) { note.Text = "这个文件夹是空的"; note.Visibility = Visibility.Visible; }
            else if (total > items.Count) { note.Text = "只显示前 " + items.Count + " 项（共 " + total + " 项）"; note.Visibility = Visibility.Visible; }
            else note.Visibility = Visibility.Collapsed;
            int size = IsList ? (int)RowIcon : Math.Max(24, Math.Min(96, data.IconSize));
            bool list = IsList;
            var pending = new List<KeyValuePair<Image, string>>();
            foreach (var it in items)
            {
                var item = it;
                var img = new Image { Width = size, Height = size, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                pending.Add(new KeyValuePair<Image, string>(img, item.Path));
                Border tile;
                if (list)
                {
                    // Explorer-like row: small icon + one line of name, ellipsis at the end
                    img.Margin = new Thickness(6, 0, 8, 0); img.VerticalAlignment = VerticalAlignment.Center;
                    var name = new TextBlock { Text = item.Name, FontSize = RowFont, Foreground = Brushes.White, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                    var row = new DockPanel { LastChildFill = true }; DockPanel.SetDock(img, Dock.Left); row.Children.Add(img); row.Children.Add(name);
                    tile = new Border { Child = row, Height = RowHeight, Width = RowWidth(), Margin = new Thickness(1, 0, 1, 1), CornerRadius = new CornerRadius(5), Background = Brushes.Transparent, ToolTip = item.Name + "\n" + item.Path, Cursor = Cursors.Hand, Tag = item };
                }
                else
                {
                    var name = new TextBlock { Text = item.Name, FontSize = 11.5, Foreground = Brushes.White, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 32, Width = size + 34, Margin = new Thickness(0, 4, 0, 0) };
                    var stack = new StackPanel(); stack.Children.Add(img); stack.Children.Add(name);
                    tile = new Border { Child = stack, Width = size + 40, Padding = new Thickness(3, 6, 3, 5), Margin = new Thickness(1), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, ToolTip = item.Name + "\n" + item.Path, Cursor = Cursors.Hand, Tag = item };
                }
                tile.MouseEnter += delegate { if (tile != selected) tile.Background = Hover; };
                tile.MouseLeave += delegate { if (tile != selected) tile.Background = Brushes.Transparent; };
                tile.MouseLeftButtonDown += (s, e) =>
                {
                    e.Handled = true;      // a click on an item selects it; it does not start a widget drag
                    if (selected != null) selected.Background = Brushes.Transparent;
                    selected = tile; tile.Background = Soft;
                    if (e.ClickCount == 2) OpenItem(item);
                };
                var menu = new ContextMenu();
                menu.Items.Add(Item("打开", delegate { OpenItem(item); }));
                menu.Items.Add(Item("在资源管理器中显示", delegate { try { Process.Start("explorer.exe", "/select,\"" + item.Path + "\""); } catch { } }));
                menu.Items.Add(Item("复制路径", delegate { try { Clipboard.SetText(item.Path); Toast("\uE8C8", "已复制路径"); } catch { } }));
                tile.ContextMenu = menu;
                grid.Children.Add(tile);
            }
            // icons are fetched in small batches at background priority, so a big folder does not freeze the desktop
            int at = 0;
            Action next = null;
            next = delegate
            {
                for (int n = 0; n < 24 && at < pending.Count; n++, at++)
                {
                    var p = pending[at]; ImageSource src = null;
                    try { src = Native.Icon(p.Value); } catch { }
                    p.Key.Source = src;
                }
                if (at < pending.Count) Dispatcher.BeginInvoke(DispatcherPriority.Background, next);
            };
            Dispatcher.BeginInvoke(DispatcherPriority.Background, next);
        }

        void OpenItem(FolderEntry item)
        {
            try { Open(item.Path); }
            catch (Exception ex) { Toast("\uE783", "无法打开：" + ex.Message); }
        }

        static string FolderName(string p)
        {
            string n = Path.GetFileName(p.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(n) ? p : n;
        }

        protected override void OnWidgetClosed()
        {
            debounce.Stop(); rewatch.Stop();
            if (watcher != null) { try { watcher.EnableRaisingEvents = false; watcher.Dispose(); } catch { } watcher = null; }
        }
    }
}
