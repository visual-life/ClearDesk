using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClearDesk
{
    /// <summary>A slider that matches the widget's volume bar: click / drag / mouse wheel. No scroll bars, no focus visuals.</summary>
    internal sealed class MixerSlider : Grid
    {
        readonly Border fill, thumb;
        internal float Value; internal bool Dragging, Dimmed;
        internal event Action<float> UserChanged;
        internal MixerSlider()
        {
            Height = 26; Background = Brushes.Transparent; Cursor = Cursors.Hand; VerticalAlignment = VerticalAlignment.Center; Margin = new Thickness(6, 0, 6, 0);
            Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)) });
            fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromRgb(120, 180, 255)), HorizontalAlignment = HorizontalAlignment.Left };
            thumb = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
            Children.Add(fill); Children.Add(thumb);
            MouseLeftButtonDown += (s, e) => { Dragging = true; CaptureMouse(); Seek(e.GetPosition(this).X); e.Handled = true; };
            MouseMove += (s, e) => { if (IsMouseCaptured) { Seek(e.GetPosition(this).X); e.Handled = true; } };
            MouseLeftButtonUp += (s, e) => { Dragging = false; ReleaseMouseCapture(); e.Handled = true; };
            LostMouseCapture += delegate { Dragging = false; };
            MouseWheel += (s, e) => { Set(MixerModel.Step(Value, e.Delta), true); e.Handled = true; };
            SizeChanged += delegate { Layout(); };
        }
        void Seek(double x) { Set(MixerModel.LevelFromX(x - 0, ActualWidth), true); }
        internal void Set(float v, bool user)
        {
            if (!user && Dragging) return;       // the user is holding it: do not fight with the poll
            Value = Math.Max(0, Math.Min(1, v)); Layout();
            if (user) { var h = UserChanged; if (h != null) h(Value); }
        }
        void Layout()
        {
            double w = Math.Max(0, ActualWidth);
            fill.Width = w * Value; fill.Opacity = Dimmed ? .35 : 1;
            thumb.Margin = new Thickness(Math.Max(0, Math.Min(w - 14, w * Value - 7)), 0, 0, 0);
        }
        internal void SetDimmed(bool dimmed) { Dimmed = dimmed; Layout(); }
    }

    /// <summary>The 音量合成器 page inside 快捷控制 (2.2.1): master volume + output device, one slider and mute per application (including system sounds).
    /// Reads Core Audio sessions only while the page is open (1.5 s poll); changes go through ISimpleAudioVolume.</summary>
    internal sealed class MixerPage : Grid
    {
        sealed class RowView
        {
            public MixerRow Row; public Grid Grid; public TextBlock Name, Percent, MuteGlyph; public Button Mute; public MixerSlider Slider; public FrameworkElement Icon;
        }
        readonly OutputVolume volume;
        readonly StackPanel list = new StackPanel(); readonly ScrollViewer scroll; readonly TextBlock empty, deviceName, masterPercent, masterGlyph;
        readonly MixerSlider masterSlider = new MixerSlider(); readonly Button masterMute;
        readonly Dictionary<string, RowView> views = new Dictionary<string, RowView>(); List<MixerRow> rows = new List<MixerRow>();
        readonly DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1500) };
        static readonly Dictionary<string, ImageSource> icons = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        internal Func<List<RawSession>> Reader = () => { string error; return SessionAudio.ReadSessions(out error); };
        internal int Refreshes;

        internal MixerPage(OutputVolume volume, Action back, Action chooseDevice, Action openSystemMixer)
        {
            this.volume = volume;
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // header: back + title
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            var backButton = Glyphs.Button("\uE72B", "返回", back, 12, 28); DockPanel.SetDock(backButton, Dock.Left); head.Children.Add(backButton);
            var title = Program.Text("音量合成器", 15); title.FontWeight = FontWeights.SemiBold; title.VerticalAlignment = VerticalAlignment.Center; title.Margin = new Thickness(8, 0, 0, 0); head.Children.Add(title);
            Children.Add(head);
            // master + device
            var master = new StackPanel { Margin = new Thickness(0, 0, 0, 2) }; SetRow(master, 1);
            var mrow = new Grid(); mrow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); mrow.ColumnDefinitions.Add(new ColumnDefinition()); mrow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            masterMute = Glyphs.Button("\uE767", "静音 / 取消静音", delegate { if (volume != null && volume.Available) volume.SetMuted(!volume.Muted); UpdateMaster(); }, 14, 28); masterGlyph = (TextBlock)masterMute.Content;
            masterSlider.UserChanged += v => { if (volume != null && volume.Available) { volume.SetLevel(v); UpdateMaster(); } };
            masterPercent = Program.Text("—", 13, "#E8EEF8"); masterPercent.HorizontalAlignment = HorizontalAlignment.Right; masterPercent.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(masterSlider, 1); Grid.SetColumn(masterPercent, 2); mrow.Children.Add(masterMute); mrow.Children.Add(masterSlider); mrow.Children.Add(masterPercent);
            deviceName = Program.Text("", 11.5, "#C9D2E0"); deviceName.Margin = new Thickness(36, 0, 0, 0); deviceName.TextTrimming = TextTrimming.CharacterEllipsis; deviceName.Cursor = Cursors.Hand; deviceName.ToolTip = "点击选择输出设备";
            deviceName.MouseLeftButtonUp += (s, e) => { chooseDevice(); e.Handled = true; };
            master.Children.Add(mrow); master.Children.Add(deviceName); Children.Add(master);
            // sessions (mouse wheel scrolls, no visible bar)
            empty = Program.Text("", 12, "#C9D2E0"); empty.TextWrapping = TextWrapping.Wrap; empty.Margin = new Thickness(4, 10, 4, 0); empty.Visibility = Visibility.Collapsed;
            var host = new StackPanel(); host.Children.Add(list); host.Children.Add(empty);
            scroll = new ScrollViewer { Content = host, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, CanContentScroll = false }; SetRow(scroll, 2);
            Children.Add(scroll);
            // link to the system mixer
            var link = Program.Text("打开系统音量合成器", 11.5, "#A9C7FF"); link.Cursor = Cursors.Hand; link.HorizontalAlignment = HorizontalAlignment.Center; link.Margin = new Thickness(0, 3, 0, 0); link.TextDecorations = TextDecorations.Underline; SetRow(link, 3);
            link.MouseLeftButtonUp += (s, e) => { openSystemMixer(); e.Handled = true; }; Children.Add(link);
            timer.Tick += delegate { if (IsVisible) RefreshNow(); };
            UpdateMaster();
        }

        internal bool Running { get { return timer.IsEnabled; } }
        internal IList<MixerRow> Rows { get { return rows; } }
        internal int RowCount { get { return list.Children.Count; } }
        internal string RowName(int i) { return ((TextBlock)views[rows[i].Key].Name).Text; }
        internal string StatusText { get { return empty.Visibility == Visibility.Visible ? empty.Text : ""; } }
        internal void Start() { RefreshNow(); timer.Start(); }
        internal void Stop() { timer.Stop(); SessionAudio.Release(rows); rows = new List<MixerRow>(); views.Clear(); list.Children.Clear(); }

        internal void UpdateMaster()
        {
            bool ok = volume != null && volume.Available; float level = ok ? volume.Level : 0; bool muted = ok && volume.Muted;
            masterSlider.Set(level, false); masterSlider.SetDimmed(muted);
            masterPercent.Text = ok ? (muted ? "静音" : MixerModel.Percent(level)) : "—";
            masterGlyph.Text = QuickLogic.VolumeGlyph(level, muted);
            deviceName.Text = ok ? volume.DeviceName + "  \u25BE" : "没有可用的输出设备";
        }

        internal void RefreshNow()
        {
            Refreshes++;
            List<RawSession> raws; try { raws = Reader(); } catch (Exception e) { Program.Log(e); raws = new List<RawSession>(); }
            var fresh = MixerModel.Group(raws);
            var old = rows; rows = fresh;
            var keys = new HashSet<string>(fresh.Select(r => r.Key));
            foreach (var k in views.Keys.Where(k => !keys.Contains(k)).ToList()) views.Remove(k);
            foreach (var r in fresh)
            {
                RowView v; if (!views.TryGetValue(r.Key, out v)) { v = Build(r); views[r.Key] = v; }
                v.Row = r; v.Name.Text = r.Name; v.Name.Opacity = r.Active ? 1 : .72;
                v.Slider.Set(r.Volume, false); v.Slider.SetDimmed(r.Muted); v.Percent.Text = r.Muted ? "静音" : MixerModel.Percent(r.Volume); v.MuteGlyph.Text = QuickLogic.VolumeGlyph(r.Volume, r.Muted);
            }
            // keep the layout when nothing changed; otherwise put the rows in order
            bool same = list.Children.Count == fresh.Count && fresh.Select((r, i) => ReferenceEquals(list.Children[i], views[r.Key].Grid)).All(b => b);
            if (!same) { list.Children.Clear(); foreach (var r in fresh) list.Children.Add(views[r.Key].Grid); }
            SessionAudio.Release(old);
            empty.Visibility = fresh.Count == 0 ? Visibility.Visible : Visibility.Collapsed; if (fresh.Count == 0) empty.Text = "现在没有应用在使用声音。打开播放器或浏览器后，这里会出现对应的音量滑块。";
            UpdateMaster();
        }

        RowView Build(MixerRow r)
        {
            var v = new RowView { Row = r, Grid = new Grid { Height = 34, Margin = new Thickness(0, 1, 0, 1) } };
            v.Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            v.Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star), MinWidth = 54 });
            v.Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            v.Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 60 });
            v.Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            v.Icon = IconFor(r); v.Icon.VerticalAlignment = VerticalAlignment.Center;
            v.Name = Program.Text(r.Name, 12.5); v.Name.VerticalAlignment = VerticalAlignment.Center; v.Name.TextTrimming = TextTrimming.CharacterEllipsis; v.Name.TextWrapping = TextWrapping.NoWrap; v.Name.Margin = new Thickness(6, 0, 2, 0); v.Name.ToolTip = r.ExePath ?? r.Name;
            var view = v;
            v.Mute = Glyphs.Button("\uE767", "静音 / 取消静音", delegate { SessionAudio.SetMuted(view.Row, !view.Row.Muted); Show(view); }, 12, 26); v.MuteGlyph = (TextBlock)v.Mute.Content;
            v.Slider = new MixerSlider(); v.Slider.UserChanged += level => { SessionAudio.SetVolume(view.Row, level); Show(view); };
            v.Percent = Program.Text("", 11.5, "#E8EEF8"); v.Percent.HorizontalAlignment = HorizontalAlignment.Right; v.Percent.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(v.Name, 1); Grid.SetColumn(v.Mute, 2); Grid.SetColumn(v.Slider, 3); Grid.SetColumn(v.Percent, 4);
            v.Grid.Children.Add(v.Icon); v.Grid.Children.Add(v.Name); v.Grid.Children.Add(v.Mute); v.Grid.Children.Add(v.Slider); v.Grid.Children.Add(v.Percent);
            return v;
        }
        void Show(RowView v) { v.Slider.SetDimmed(v.Row.Muted); v.Percent.Text = v.Row.Muted ? "静音" : MixerModel.Percent(v.Row.Volume); v.MuteGlyph.Text = QuickLogic.VolumeGlyph(v.Row.Volume, v.Row.Muted); if (!v.Slider.Dragging) v.Slider.Set(v.Row.Volume, false); }

        static FrameworkElement IconFor(MixerRow r)
        {
            if (!r.System && r.ExePath != null)
            {
                ImageSource src; if (!icons.TryGetValue(r.ExePath, out src)) { try { src = Native.Icon(r.ExePath); } catch { src = null; } if (icons.Count > 200) icons.Clear(); icons[r.ExePath] = src; }
                if (src != null) return new Image { Source = src, Width = 24, Height = 24, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
            }
            var g = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)) };
            g.Child = new TextBlock { Text = r.System ? "\uE995" : "\uE8D6", FontFamily = new FontFamily(MenuTheme.IconFont), FontSize = 13, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            return g;
        }

        // tests: what the sliders do
        internal bool TestSetVolume(string key, float level) { RowView v; if (!views.TryGetValue(key, out v)) return false; v.Slider.Set(level, true); return true; }
        internal bool TestToggleMute(string key) { RowView v; if (!views.TryGetValue(key, out v)) return false; v.Mute.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return true; }
    }

    /// <summary>Small flat icon buttons for the quick-controls pages.</summary>
    internal static class Glyphs
    {
        static ControlTemplate flat;
        internal static Button Button(string glyph, string tip, Action click, double size, double box)
        {
            if (flat == null)
            {
                var t = new ControlTemplate(typeof(Button)); var b = new FrameworkElementFactory(typeof(Border)); b.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
                b.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
                var c = new FrameworkElementFactory(typeof(ContentPresenter)); c.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center); c.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                b.AppendChild(c); t.VisualTree = b; var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(UIElement.OpacityProperty, .78)); t.Triggers.Add(hover); flat = t;
            }
            var button = new Button { Content = new TextBlock { Text = glyph, FontFamily = new FontFamily(MenuTheme.IconFont), FontSize = size, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                Template = flat, Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), Width = box, Height = box, Padding = new Thickness(0), ToolTip = tip, Cursor = Cursors.Hand, Focusable = false };
            System.Windows.Automation.AutomationProperties.SetName(button, tip);
            button.Click += (s, e) => { click(); e.Handled = true; };
            return button;
        }
    }
}
