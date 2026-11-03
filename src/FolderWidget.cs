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

        // ---- 2.2.7 adaptive sizes. All values are SCREEN pixels (DIP) of the visible list area; width AND height count.
        internal const double ListIconMin = 16, ListIconMax = 48, ListFontMin = 12, ListFontMax = 22, ListRowMin = 26;
        internal struct ListSize { public double Icon, Font, Row; public override string ToString() { return "icon " + Icon.ToString("0.#") + " / font " + Font.ToString("0.#") + " / row " + Row.ToString("0.#"); } }

        /// <summary>
        /// List view: few items in a tall widget -> bigger rows so the list fills the height; never above 48 px icons / 22 px text;
        /// a narrow widget limits it (icon at most width / 6, so names keep room); many items -> the base size (16 / 12 / 26) and scrolling.
        /// </summary>
        internal static ListSize ListMetrics(double width, double height, int count)
        {
            double icon = ListIconMin;
            if (count > 0 && width > 0 && height > 0)
            {
                double byHeight = height / count / 1.45;      // a row is about 1.45 x the icon
                double byWidth = width / 6;
                icon = Math.Max(ListIconMin, Math.Min(ListIconMax, Math.Min(byHeight, byWidth)));
            }
            double font = ListFontMin + (icon - ListIconMin) * (ListFontMax - ListFontMin) / (ListIconMax - ListIconMin);
            double row = Math.Max(ListRowMin, Math.Round(icon * 1.45));
            return new ListSize { Icon = Math.Round(icon), Font = Math.Round(font * 2) / 2, Row = row };
        }

        /// <summary>Title / header buttons scale: from the user window size only (screen px).</summary>
        internal static double HeaderScale(double winW, double winH) { return Math.Round(Math.Max(.8, Math.Min(1.7, Math.Min(winW / 220, winH / 420))), 2); }
        /// <summary>Keeps the previous size when the new one differs by one step and the list height moved less than 3 px since that size was chosen (no flicker at a threshold).</summary>
        internal static ListSize Hysteresis(ListSize prev, ListSize next, double prevH, double h)
        {
            if (prev.Row == 0 || prevH < 0) return next;
            if (Math.Abs(next.Icon - prev.Icon) <= 1 && Math.Abs(h - prevH) < 3) return prev;
            return next;
        }

        internal const double TileIconMax = 96;
        /// <summary>Icon view: the biggest icon (base .. 96) whose tile grid still fits the visible area; otherwise the base size and scrolling.</summary>
        internal static double TileMetrics(double width, double height, int count, double baseIcon)
        {
            baseIcon = Math.Max(24, Math.Min(TileIconMax, baseIcon));
            if (count <= 0 || width <= 0 || height <= 0) return baseIcon;
            for (double icon = TileIconMax; icon > baseIcon; icon -= 2)
            {
                int cols = Math.Max(1, (int)(width / (icon + 42)));
                int rows = (count + cols - 1) / cols;
                if (rows * (icon + icon * .35 + 34) <= height) return icon;
            }
            return baseIcon;
        }
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
            scroller.SizeChanged += delegate { QueueMetrics(); };      // window size settles; metrics read only the user size so this cannot loop
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

        sealed class Part { public Border Tile; public Image Img; public TextBlock Name; }
        readonly List<Part> parts = new List<Part>();
        internal FolderLogic.ListSize LastList;      // screen sizes last applied (tests / diagnostics)
        internal double LastTileIcon, LastHeaderScale; double appliedHeader = 1;

        /// <summary>2.2.8: recomputes row / icon / text sizes. Inputs are ONLY the size the user gave the widget (State size → window size) and the item count;
        /// nothing measured from the list itself (viewport, scrollbar, header height after scaling) is read back, so the result cannot feed into itself.
        /// The widget's Width / Height / MinHeight are never written here.</summary>
        internal void ApplyMetrics()
        {
            MetricsRuns++;
            double s = ContentScale; if (s <= 0 || double.IsNaN(s)) s = 1;
            double winW = UserW, winH = UserH;                  // screen px, user-set
            if (winW <= 0 || winH <= 0 || Header == null) return;
            // fixed chrome (padding, note, margins) in content units: invariant of every metric below
            double headNat = Header.ActualHeight;               // untransformed height, the LayoutTransform does not change it
            if (headNat <= 0 || scroller.ActualHeight <= 0) return;
            if (chromeH < 0 || Math.Abs(lastS - s) > .0001 || note.IsVisible != lastNote)
            {
                double win = ActualHeight / s, winWc = ActualWidth / s;
                chromeH = Math.Max(0, win - scroller.ActualHeight - headNat * appliedHeader);
                chromeW = Math.Max(0, winWc - scroller.ActualWidth);
                lastS = s; lastNote = note.IsVisible;
            }
            // header scale depends on the window size only
            double hs = FolderLogic.HeaderScale(winW, winH);
            double k = Math.Round(hs / s, 2);
            double W = Math.Max(1, winW - chromeW * s), H = Math.Max(1, winH - (chromeH + headNat * k) * s) - 4;    // screen px of the list area, 4px safety
            if (IsList)
            {
                var m = FolderLogic.ListMetrics(W, H, parts.Count);
                m = FolderLogic.Hysteresis(LastList, m, lastInH, H); if (m.Icon != LastList.Icon || LastList.Row == 0) lastInH = H;
                LastList = m;
                foreach (var p in parts)
                {
                    p.Img.Width = p.Img.Height = m.Icon / s;
                    p.Img.Margin = new Thickness(6 / s * Math.Max(1, m.Icon / 16), 0, 8 / s * Math.Max(1, m.Icon / 16), 0);
                    p.Name.FontSize = m.Font / s;
                    p.Tile.Height = m.Row / s; p.Tile.Width = double.NaN; p.Tile.HorizontalAlignment = HorizontalAlignment.Stretch;
                }
            }
            else
            {
                double icon = FolderLogic.TileMetrics(W, H, parts.Count, data.IconSize);
                if (LastTileIcon > 0 && Math.Abs(icon - LastTileIcon) <= 2 && Math.Abs(H - lastInH) < 6) icon = LastTileIcon; else lastInH = H;
                LastTileIcon = icon;
                double font = Math.Max(11.5, Math.Min(18, 11.5 * icon / 44));
                foreach (var p in parts)
                {
                    p.Img.Width = p.Img.Height = icon / s;
                    p.Name.FontSize = font / s; p.Name.Width = (icon + 34) / s; p.Name.MaxHeight = font * 2.8 / s;
                    p.Tile.Width = (icon + 40) / s;
                }
            }
            if (Math.Abs(k - appliedHeader) >= .01)
            {
                appliedHeader = k;
                Header.LayoutTransform = Math.Abs(k - 1) < .01 ? null : new ScaleTransform(k, k);
            }
            LastHeaderScale = appliedHeader * s;
        }
        double chromeH = -1, chromeW, lastS = -1, lastInH = -1; bool lastNote;
        internal int MetricsRuns;
        double UserW { get { return (State.CustomW > 0 ? State.CustomW : DefaultWidth); } }
        double UserH { get { return (State.CustomH > 0 ? State.CustomH : DefaultHeight); } }

        bool metricsQueued; DateTime lastMetrics;
        /// <summary>Coalesced at low priority and at most once per frame (16 ms) while dragging.</summary>
        void QueueMetrics()
        {
            if (metricsQueued) return; metricsQueued = true;
            double wait = 16 - (DateTime.UtcNow - lastMetrics).TotalMilliseconds;
            Action run = delegate { metricsQueued = false; lastMetrics = DateTime.UtcNow; ApplyMetrics(); };
            if (wait <= 0) Dispatcher.BeginInvoke(DispatcherPriority.Background, run);
            else { var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(wait) }; t.Tick += delegate { t.Stop(); run(); }; t.Start(); }
        }
        protected override void OnResponsive(double extraW, double extraH) { base.OnResponsive(extraW, extraH); QueueMetrics(); }     // user size changed

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
            grid.Children.Clear(); parts.Clear(); selected = null;
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
                parts.Add(new Part { Tile = tile, Img = img, Name = (TextBlock)(list ? ((DockPanel)tile.Child).Children[1] : ((StackPanel)tile.Child).Children[1]) });
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
            ApplyMetrics();
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
