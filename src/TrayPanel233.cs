using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClearDesk
{
    /// <summary>2.3.3: 托盘图标 in the 快捷控制 widget — every notification-area icon (the hidden ones too), so the taskbar is not needed to open, close or manage background apps.
    /// Left click / double click / right click are forwarded to the real icon (see TrayAccess).</summary>
    internal sealed class TrayPanel233 : StackPanel
    {
        internal const double Tile = 44, HeaderH = 26, TopGap = 12;
        const int PerRow = 8;
        internal Func<bool, List<TrayItem>> ReadVisible = TrayAccess.ReadVisible, ReadHidden = TrayAccess.ReadOverflow;
        internal Func<TrayItem, int, string> Clicker = TrayAccess.Click;
        internal Action<string, string> Toast = delegate { };
        internal Action<double> ExtraHeightChanged = delegate { };
        internal string CachePath, IconsPath;

        readonly Dispatcher ui;
        readonly TextBlock title, hint;
        readonly WrapPanel wrap = new WrapPanel();
        readonly ScrollViewer scroller;
        readonly Button expand, refresh;
        List<TrayItem> visible = new List<TrayItem>(), hidden;       // hidden == null: not read yet
        bool expanded, busy, started;
        DispatcherTimer timer;
        string signature = "";
        internal int Renders, HiddenReads, VisibleReads;
        internal List<string> TileNames { get { return wrap.Children.OfType<Border>().Select(b => (string)b.Tag).ToList(); } }
        internal double ExtraHeight { get; private set; }

        public TrayPanel233(Dispatcher dispatcher)
        {
            ui = dispatcher;
            Margin = new Thickness(0, TopGap, 0, 0);
            var head = new Grid { Height = HeaderH };
            head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            title = Program.Text("托盘图标", 12, "#C9D2E0"); title.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(title);
            refresh = Small("\uE72C", "重新读取被收起的图标（会短暂展开一下任务栏的隐藏图标面板）", delegate { RefreshHidden(true); });
            expand = Small("\uE70D", "展开 / 收起", delegate { expanded = !expanded; Render(true); });
            Grid.SetColumn(refresh, 1); Grid.SetColumn(expand, 2); head.Children.Add(refresh); head.Children.Add(expand);
            Children.Add(head);
            scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = wrap, Height = Tile };
            hint = Program.Text("", 11.5, "#9AA6B8"); hint.Margin = new Thickness(2, 10, 0, 0); hint.TextWrapping = TextWrapping.Wrap;
            Children.Add(scroller);
            scroller.PreviewMouseWheel += delegate (object o, MouseWheelEventArgs e) { if (expanded) { scroller.ScrollToVerticalOffset(scroller.VerticalOffset - e.Delta / 2.0); e.Handled = true; } };
        }

        static Button Small(string glyph, string tip, Action click)
        {
            var b = new Button { Width = 28, Height = 24, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = tip, Cursor = Cursors.Hand, Focusable = false, Foreground = Brushes.White,
                Content = new TextBlock { Text = glyph, FontFamily = new FontFamily(MenuTheme.IconFont), FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(201, 210, 224)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            b.Click += delegate { click(); };
            System.Windows.Automation.AutomationProperties.SetName(b, tip);
            return b;
        }

        // ---- data
        internal void LoadCache()
        {
            try
            {
                if (CachePath == null || !File.Exists(CachePath)) return;
                var rows = new JavaScriptSerializer().Deserialize<List<Dictionary<string, string>>>(File.ReadAllText(CachePath));
                hidden = rows.Select((r, i) => new TrayItem { Name = r["Name"], Tooltip = r.ContainsKey("Tooltip") ? r["Tooltip"] : r["Name"], Overflow = true, Key = "o:" + r["Name"] + "#" + i, Png = string.IsNullOrEmpty(r["Png"]) ? null : Convert.FromBase64String(r["Png"]) }).ToList();
            }
            catch (Exception e) { Program.Log(e); hidden = null; }
        }

        void SaveCache()
        {
            try
            {
                if (CachePath == null || hidden == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath));
                File.WriteAllText(CachePath, new JavaScriptSerializer().Serialize(hidden.Select(i => new Dictionary<string, string> { { "Name", i.Name }, { "Tooltip", i.Tooltip }, { "Png", i.Png == null ? "" : Convert.ToBase64String(i.Png) } }).ToList()));
            }
            catch (Exception e) { Program.Log(e); }
        }

        internal void Start()
        {
            if (started) return; started = true;
            LoadCache(); TrayAccess.LoadIcons(IconsPath);
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += delegate { PollVisible(); };
            timer.Start(); PollVisible(); Render(false);
        }

        internal void Stop() { started = false; if (timer != null) timer.Stop(); }

        bool Enabled { get { return Program.S.TrayArea; } }

        /// <summary>Cheap, side-effect free: the icons on the taskbar itself.</summary>
        internal void PollVisible()
        {
            if (!Enabled || busy || !IsVisible || Program.Rendering) return;
            Task.Run(() => { try { return ReadVisible(true); } catch (Exception e) { Program.Log(e); return null; } }).ContinueWith(t =>
                ui.BeginInvoke(new Action(delegate { if (t.Result != null) SetVisible(t.Result); })));
        }

        internal void SetVisible(List<TrayItem> list)
        {
            VisibleReads++;
            visible = list; Task.Run(() => TrayAccess.SaveIcons(IconsPath));
            if (hidden != null)       // an icon that has been put on the taskbar is not hidden any more
            {
                var names = new HashSet<string>(list.Select(i => i.Name));
                if (hidden.RemoveAll(h => names.Contains(h.Name)) > 0) SaveCache();
            }
            Render(false);
        }

        /// <summary>Reads the hidden icons (opens the overflow flyout for a moment). Only when the user asks.</summary>
        internal void RefreshHidden(bool announce)
        {
            if (busy) return; busy = true; refresh.Opacity = .4;
            Task.Run(() => { try { return ReadHidden(true); } catch (Exception e) { Program.Log(e); return null; } }).ContinueWith(t =>
                ui.BeginInvoke(new Action(delegate { busy = false; refresh.Opacity = 1; SetHidden(t.Result, announce); })));
        }

        internal void SetHidden(List<TrayItem> list, bool announce)
        {
            HiddenReads++;
            if (list == null || (list.Count == 0 && TrayAccess.LastProblem != null && hidden == null)) { if (announce) Toast("\uE783", TrayAccess.LastProblem ?? "读取不到被收起的图标"); return; }
            hidden = list; SaveCache(); Task.Run(() => TrayAccess.SaveIcons(IconsPath)); Render(false);
        }

        // ---- view
        internal List<TrayItem> Shown()
        {
            string scope = Program.S.TrayScope;
            var names = new HashSet<string>(visible.Select(v => v.Name));
            var list = new List<TrayItem>();
            if (scope != "hidden") list.AddRange(visible);
            if (scope != "visible" && hidden != null) list.AddRange(hidden.Where(h => !names.Contains(h.Name)));
            return list;
        }

        internal void ApplySettings() { Render(false); }

        internal void Render(bool force)
        {
            Renders++;
            if (!Enabled) { Visibility = Visibility.Collapsed; SetExtra(0); return; }
            Visibility = Visibility.Visible;
            var items = Shown();
            string sig = string.Join("|", items.Select(i => i.Key + ":" + (i.Png == null ? 0 : i.Png.Length))) + "/" + expanded + "/" + Program.S.TrayScope + "/" + (hidden == null);
            int rows = expanded ? Math.Max(1, Math.Min(4, (items.Count + PerRow - 1) / PerRow)) : 1;
            scroller.Height = rows * Tile;
            scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            expand.Visibility = items.Count > PerRow ? Visibility.Visible : Visibility.Collapsed;
            ((TextBlock)expand.Content).Text = expanded ? "\uE70E" : "\uE70D";
            refresh.Visibility = Program.S.TrayScope == "visible" ? Visibility.Collapsed : Visibility.Visible;
            title.Text = "托盘图标" + (items.Count > 0 ? " · " + items.Count : "");
            if (sig != signature || force)
            {
                signature = sig; wrap.Children.Clear();
                foreach (var it in items) wrap.Children.Add(MakeTile(it));
                if (items.Count == 0)
                {
                    bool needHidden = hidden == null && Program.S.TrayScope != "visible";
                    hint.Text = needHidden ? "点右上角的刷新按钮，读取被收起的托盘图标。" : "没有托盘图标。";
                    wrap.Children.Add(hint);
                }
            }
            SetExtra(TopGap + HeaderH + rows * Tile);
        }

        void SetExtra(double extra) { if (Math.Abs(extra - ExtraHeight) > .5) { ExtraHeight = extra; ExtraHeightChanged(extra); } }

        static readonly Brush TileOff = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), TileOn = new SolidColorBrush(Color.FromArgb(64, 255, 255, 255));
        static readonly Dictionary<string, ImageSource> images = new Dictionary<string, ImageSource>();

        static FrameworkElement Face(TrayItem it)
        {
            if (it.Png != null && it.Png.Length > 0)
            {
                try
                {
                    string key = it.Name + ":" + it.Png.Length; ImageSource src;
                    if (!images.TryGetValue(key, out src))
                    {
                        var bmp = new System.Windows.Media.Imaging.BitmapImage();
                        bmp.BeginInit(); bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bmp.StreamSource = new MemoryStream(it.Png); bmp.EndInit(); bmp.Freeze();
                        if (images.Count > 64) images.Clear();
                        images[key] = src = bmp;
                    }
                    return new System.Windows.Controls.Image { Source = src, Width = 24, Height = 24, Stretch = Stretch.Uniform, IsHitTestVisible = false };
                }
                catch { }
            }
            string letter = it.Name.Length > 0 ? it.Name.Substring(0, 1).ToUpperInvariant() : "?";
            return new TextBlock { Text = letter, FontSize = 16, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        }

        Border MakeTile(TrayItem it)
        {
            var tile = new Border { Width = Tile - 6, Height = Tile - 6, Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(10), Background = TileOff, Child = Face(it), Cursor = Cursors.Hand, Tag = it.Name, ToolTip = it.Tooltip + (it.Overflow ? "\n（被收起的图标）" : "") + "\n左键：打开　右键：应用菜单" };
            System.Windows.Automation.AutomationProperties.SetName(tile, "托盘图标 " + it.Name);
            tile.MouseEnter += delegate { tile.Background = TileOn; };
            tile.MouseLeave += delegate { tile.Background = TileOff; };
            DispatcherTimer single = null;
            tile.MouseLeftButtonUp += delegate (object o, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (e.ClickCount >= 2) { if (single != null) single.Stop(); Act(it, 2, tile); return; }
                if (single == null) { single = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) }; single.Tick += delegate { single.Stop(); Act(it, 0, tile); }; }
                single.Stop(); single.Start();
            };
            tile.MouseRightButtonUp += delegate (object o, MouseButtonEventArgs e) { e.Handled = true; Act(it, 1, tile); };
            return tile;
        }

        internal void Act(TrayItem it, int mode, UIElement tile)
        {
            if (busy) return; busy = true; if (tile != null) tile.Opacity = .5;
            Task.Run(() => { try { return Clicker(it, mode); } catch (Exception e) { return "操作失败：" + e.Message; } }).ContinueWith(t =>
                ui.BeginInvoke(new Action(delegate
                {
                    busy = false; if (tile != null) tile.Opacity = 1;
                    LastResult = t.Result;
                    if (t.Result != null)
                    {
                        Toast("\uE783", t.Result);
                        if (t.Result.StartsWith("没有找到") && hidden != null) { hidden.RemoveAll(h => h.Name == it.Name); SaveCache(); Render(false); }
                    }
                })));
        }
        internal string LastResult;
        internal bool Busy { get { return busy; } }
    }
}
