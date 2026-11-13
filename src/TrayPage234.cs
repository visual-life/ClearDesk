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
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClearDesk
{
    /// <summary>2.3.4: the 托盘图标 page inside 快捷控制, built like the 音量合成器 page: ← back + title + refresh, one row per notification-area icon (picture, name, state, … menu button),
    /// a footer link, and the same slide / fade between pages. Every icon of the notification area is listed, the hidden ones too, so the taskbar is not needed to open, close or manage
    /// background apps. Left click / double click / right click are forwarded to the real icon (see TrayAccess / UiaCom): the program's own menu appears and the taskbar stays where it is.</summary>
    internal sealed class TrayPage234 : Grid
    {
        internal const double RowH = 46;
        internal Func<bool, List<TrayItem>> ReadVisible = TrayAccess.ReadVisible, ReadHidden = TrayAccess.ReadOverflow;
        internal Func<TrayItem, int, string> Clicker = TrayAccess.Click;
        internal Action<string, string> Toast = delegate { };
        internal Action<int> CountChanged = delegate { };
        internal Func<bool> Polling = () => true;
        internal string CachePath, IconsPath;
        internal bool AutoRead = true;

        readonly Dispatcher ui;
        readonly TextBlock title, empty;
        readonly StackPanel list = new StackPanel();
        readonly ScrollViewer scroll;
        readonly Button refresh;
        List<TrayItem> visible = new List<TrayItem>(), hidden;       // hidden == null: not read yet
        bool busy, polling, started, pageOpen, readingHidden;
        DateTime hiddenAt = DateTime.MinValue;
        int idle;
        DispatcherTimer timer;
        string signature = "";
        internal int Renders, HiddenReads, VisibleReads, Count;
        internal List<string> RowNames { get { return list.Children.OfType<Border>().Select(b => (string)b.Tag).ToList(); } }
        internal int RowCount { get { return list.Children.Count; } }
        internal string EmptyText { get { return empty.Visibility == Visibility.Visible ? empty.Text : ""; } }
        internal string TitleText { get { return title.Text; } }
        internal bool Busy { get { return busy; } }
        internal string LastResult;
        internal bool PageOpen { get { return pageOpen; } }

        internal TrayPage234(Dispatcher dispatcher, Action back, Action<string> openUri)
        {
            ui = dispatcher;
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var backButton = Glyphs.Button("\uE72B", "返回", back, 12, 28); DockPanel.SetDock(backButton, Dock.Left); head.Children.Add(backButton);
            refresh = Glyphs.Button("\uE72C", "重新读取被收起的图标", delegate { RefreshHidden(true); }, 12, 28); DockPanel.SetDock(refresh, Dock.Right); head.Children.Add(refresh);
            title = Program.Text("托盘图标", 15); title.FontWeight = FontWeights.SemiBold; title.VerticalAlignment = VerticalAlignment.Center; title.Margin = new Thickness(8, 0, 0, 0); head.Children.Add(title);
            Children.Add(head);
            empty = Program.Text("", 12, "#C9D2E0"); empty.TextWrapping = TextWrapping.Wrap; empty.Margin = new Thickness(4, 10, 4, 0); empty.Visibility = Visibility.Collapsed;
            var host = new StackPanel(); host.Children.Add(list); host.Children.Add(empty);
            scroll = new ScrollViewer { Content = host, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, CanContentScroll = false }; SetRow(scroll, 1);
            Children.Add(scroll);
            var link = Program.Text("打开系统任务栏设置（托盘图标）", 11.5, "#A9C7FF"); link.Cursor = Cursors.Hand; link.HorizontalAlignment = HorizontalAlignment.Center; link.Margin = new Thickness(0, 3, 0, 0); link.TextDecorations = TextDecorations.Underline; SetRow(link, 2);
            link.MouseLeftButtonUp += (s, e) => { openUri("ms-settings:taskbar"); e.Handled = true; }; Children.Add(link);
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

        /// <summary>Starts the (cheap) background poll of the icons on the taskbar. Called once when the widget is created.</summary>
        internal void Start()
        {
            if (started) return; started = true;
            LoadCache(); TrayAccess.LoadIcons(IconsPath);
            try { UiaCom.ResetFlyoutNow(); } catch { }
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += delegate { if (pageOpen || ++idle % 5 == 0) PollVisible(); };       // page open: every 2 s; only counting for the button: every 10 s
            timer.Start(); PollVisible(); Render(false);
        }

        internal void Stop() { started = false; if (timer != null) timer.Stop(); try { UiaCom.ResetFlyoutNow(); } catch { } }

        /// <summary>The page was opened: read now, and read the hidden icons too (invisibly) when they are not known yet or old.</summary>
        internal void Opened()
        {
            pageOpen = true; Render(true); PollVisible();
            if (AutoRead && Program.S.TrayScope != "visible" && (hidden == null || (DateTime.UtcNow - hiddenAt).TotalSeconds > 45)) RefreshHidden(false);
        }
        internal void Closed() { pageOpen = false; }

        bool Enabled { get { return Program.S.TrayArea; } }

        /// <summary>Cheap, side-effect free: the icons on the taskbar itself.</summary>
        internal void PollVisible()
        {
            if (!Enabled || polling || Program.Rendering || !Polling()) return;
            polling = true;
            Task.Run(() => { try { return ReadVisible(true); } catch (Exception e) { Program.Log(e); return null; } }).ContinueWith(t =>
                ui.BeginInvoke(new Action(delegate { polling = false; if (t.Result != null) SetVisible(t.Result); })));
        }

        internal void SetVisible(List<TrayItem> items)
        {
            VisibleReads++;
            visible = items; Task.Run(() => TrayAccess.SaveIcons(IconsPath));
            if (hidden != null)       // an icon that has been put on the taskbar is not hidden any more
            {
                var names = new HashSet<string>(items.Select(i => i.Name));
                if (hidden.RemoveAll(h => names.Contains(h.Name)) > 0) SaveCache();
            }
            Render(false);
        }

        /// <summary>Reads the hidden icons (the overflow flyout is opened without being drawn).</summary>
        internal void RefreshHidden(bool announce)
        {
            if (busy || readingHidden) return; busy = true; readingHidden = true; refresh.Opacity = .4; Render(true);
            Task.Run(() => { try { return ReadHidden(true); } catch (Exception e) { Program.Log(e); return null; } }).ContinueWith(t =>
                ui.BeginInvoke(new Action(delegate { busy = false; readingHidden = false; refresh.Opacity = 1; SetHidden(t.Result, announce); })));
        }

        internal void SetHidden(List<TrayItem> items, bool announce)
        {
            HiddenReads++;
            if (items == null || (items.Count == 0 && TrayAccess.LastProblem != null && hidden == null)) { if (announce) Toast("\uE783", TrayAccess.LastProblem ?? "读取不到被收起的图标"); Render(true); return; }
            hidden = items; hiddenAt = DateTime.UtcNow; SaveCache(); Task.Run(() => TrayAccess.SaveIcons(IconsPath)); Render(false);
        }

        // ---- view
        internal List<TrayItem> Shown()
        {
            string scope = Program.S.TrayScope;
            var names = new HashSet<string>(visible.Select(v => v.Name));
            var all = new List<TrayItem>();
            if (scope != "hidden") all.AddRange(visible);
            if (scope != "visible" && hidden != null) all.AddRange(hidden.Where(h => !names.Contains(h.Name)));
            return all;
        }

        internal void ApplySettings() { Render(true); }

        internal void Render(bool force)
        {
            Renders++;
            var items = Enabled ? Shown() : new List<TrayItem>();
            if (items.Count != Count) { Count = items.Count; CountChanged(Count); }
            title.Text = "托盘图标" + (items.Count > 0 ? " · " + items.Count : "");
            refresh.Visibility = Program.S.TrayScope == "visible" ? Visibility.Collapsed : Visibility.Visible;
            string sig = string.Join("|", items.Select(i => i.Key + ":" + (i.Png == null ? 0 : i.Png.Length))) + "/" + Program.S.TrayScope + "/" + (hidden == null) + "/" + readingHidden;
            if (sig == signature && !force) return;
            if (sig != signature || list.Children.Count != items.Count)
            {
                signature = sig; list.Children.Clear();
                foreach (var it in items) list.Children.Add(MakeRow(it));
            }
            if (items.Count == 0)
            {
                bool needHidden = hidden == null && Program.S.TrayScope != "visible";
                empty.Text = readingHidden ? "正在读取被收起的托盘图标…" : needHidden ? "点右上角的刷新按钮，读取被收起的托盘图标。" : "没有托盘图标。";
                empty.Visibility = Visibility.Visible;
            }
            else empty.Visibility = Visibility.Collapsed;
        }

        static readonly Brush RowOff = Brushes.Transparent, RowOn = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), PlateBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
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
                        var bmp = new BitmapImage();
                        bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = new MemoryStream(it.Png); bmp.EndInit(); bmp.Freeze();
                        if (images.Count > 96) images.Clear();
                        images[key] = src = bmp;
                    }
                    var img = new Image { Source = src, Width = 28, Height = 28, Stretch = Stretch.Uniform, IsHitTestVisible = false, SnapsToDevicePixels = true, UseLayoutRounding = true };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Fant);
                    return img;
                }
                catch { }
            }
            string letter = it.Name.Length > 0 ? it.Name.Substring(0, 1).ToUpperInvariant() : "?";
            return new TextBlock { Text = letter, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        }

        Border MakeRow(TrayItem it)
        {
            string key = TrayAccess.IconKey(it.Name);
            string rest = it.Name.Length > key.Length ? it.Name.Substring(key.Length).Trim(' ', '·', '\u00B7') : "";
            var row = new Border { Height = RowH, Margin = new Thickness(0, 1, 0, 1), CornerRadius = new CornerRadius(10), Background = RowOff, Cursor = Cursors.Hand, Tag = it.Name, Padding = new Thickness(4, 0, 4, 0),
                ToolTip = it.Tooltip + (it.Overflow ? "\n（被收起的图标）" : "") + "\n左键：打开　右键：应用菜单" };
            System.Windows.Automation.AutomationProperties.SetName(row, "托盘图标 " + it.Name);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var plate = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(9), Background = PlateBrush, Child = Face(it), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
            g.Children.Add(plate);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0) };
            var name = Program.Text(key.Length > 0 ? key : it.Name, 13); name.TextTrimming = TextTrimming.CharacterEllipsis; name.TextWrapping = TextWrapping.NoWrap; text.Children.Add(name);
            if (rest.Length > 0) { var sub = Program.Text(rest, 11, "#9AA6B8"); sub.TextTrimming = TextTrimming.CharacterEllipsis; sub.TextWrapping = TextWrapping.NoWrap; sub.Margin = new Thickness(0, 1, 0, 0); text.Children.Add(sub); }
            Grid.SetColumn(text, 1); g.Children.Add(text);
            if (it.Overflow) { var tag = Program.Text("收起", 11, "#9AA6B8"); tag.VerticalAlignment = VerticalAlignment.Center; tag.Margin = new Thickness(0, 0, 6, 0); Grid.SetColumn(tag, 2); g.Children.Add(tag); }
            var more = Glyphs.Button("\uE712", "应用菜单（等于右键）", delegate { Act(it, 1, row); }, 12, 28); more.Background = Brushes.Transparent; more.Margin = new Thickness(0, 0, 2, 0); Grid.SetColumn(more, 3); g.Children.Add(more);
            row.Child = g;
            row.MouseEnter += delegate { row.Background = RowOn; };
            row.MouseLeave += delegate { row.Background = RowOff; };
            DispatcherTimer single = null;
            row.MouseLeftButtonUp += delegate (object o, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (e.ClickCount >= 2) { if (single != null) single.Stop(); Act(it, 2, row); return; }
                if (single == null) { single = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) }; single.Tick += delegate { single.Stop(); Act(it, 0, row); }; }
                single.Stop(); single.Start();
            };
            row.MouseRightButtonUp += delegate (object o, MouseButtonEventArgs e) { e.Handled = true; Act(it, 1, row); };
            return row;
        }

        internal void Act(TrayItem it, int mode, UIElement row)
        {
            if (busy) return; busy = true; if (row != null) row.Opacity = .55;
            Task.Run(() => { try { return Clicker(it, mode); } catch (Exception e) { return "操作失败：" + e.Message; } }).ContinueWith(t =>
                ui.BeginInvoke(new Action(delegate
                {
                    busy = false; if (row != null) row.Opacity = 1;
                    LastResult = t.Result;
                    if (t.Result != null)
                    {
                        Toast("\uE783", t.Result);
                        if (t.Result.StartsWith("没有找到") && hidden != null) { hidden.RemoveAll(h => h.Name == it.Name); SaveCache(); Render(false); }
                    }
                })));
        }

        // tests
        internal void TestClickRow(int index, int mode)
        {
            var row = (Border)list.Children[index];
            if (mode == 1) row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = UIElement.MouseRightButtonUpEvent });
            else if (mode == 2)
            {
                var e = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent };
                var f = typeof(MouseButtonEventArgs).GetField("_count", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (f != null) { f.SetValue(e, 2); row.RaiseEvent(e); } else Act(Shown()[index], 2, row);       // ClickCount is read-only: set it the way WPF does
            }
            else row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        }
        internal void TestClickMore(int index)
        {
            var row = (Border)list.Children[index];
            var more = ((Grid)row.Child).Children.OfType<Button>().Last();
            more.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }
        internal void TestClickRefresh() { refresh.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }
    }
}
