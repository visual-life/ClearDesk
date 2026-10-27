using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace ClearDesk
{
    internal sealed class SettingsWindow : Window
    {
        internal const string Version = "2.2.3";
        readonly Settings state = Program.S;
        readonly List<Page> pages = new List<Page>();
        readonly StackPanel content = new StackPanel();
        readonly TextBlock heading = Label("通用", 28, "#101828");
        readonly TextBlock subtitle = Label("让桌面保持整洁，让常用触手可及。", 13, "#7D889B");
        readonly TextBlock status = Label("设置会保存，组件调整后点击应用。", 11, "#7D889B");
        readonly TextBox search = new TextBox();
        readonly ScrollViewer scroll;
        int selected;
        sealed class Page
        {
            public string Name;
            public Button Navigation;
            public List<Group> Groups = new List<Group>();
        }
        sealed class Group
        {
            public string Name;
            public Border Card;
            public StackPanel Rows;
            public List<KeyValuePair<FrameworkElement, string>> Entries = new List<KeyValuePair<FrameworkElement, string>>();
        }
        static Brush Color(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }
        static TextBlock Label(string text, double size = 14, string color = "#273348")
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = Color(color), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        }
        static Button ActionButton(string text, Action action, bool primary = false)
        {
            var button = new Button { Content = text, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (primary) { button.Background = Color("#1677FF"); button.Foreground = Brushes.White; button.BorderBrush = button.Background; }
            button.Click += delegate { action(); };
            return button;
        }
        internal SettingsWindow(Action<bool> startup)
        {
            Title = "ClearDesk · 设置"; Icon = AppBrand.Icon;
            Width = 1080; Height = Math.Min(840, SystemParameters.WorkArea.Height - 32); MinWidth = 840; MinHeight = 570;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Color("#F7F9FC"); Foreground = Color("#273348");
            FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            Resources = SettingsTheme.Create();
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 42, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(16), UseAeroCaptionButtons = false });
            var layout = new Grid { Background = Background }; layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(225) }); layout.ColumnDefinitions.Add(new ColumnDefinition());
            var sidebar = new Grid { Background = new LinearGradientBrush(System.Windows.Media.Color.FromRgb(226,235,251), System.Windows.Media.Color.FromRgb(241,245,253), 65) };
            sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); sidebar.RowDefinitions.Add(new RowDefinition()); sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var top = new StackPanel { Margin = new Thickness(24,47,24,24) };
            var brand = new DockPanel(); var icon = new Image { Source = AppBrand.Icon, Width = 36, Height = 36, Margin = new Thickness(0,0,12,0) }; brand.Children.Add(icon);
            var brandText = new StackPanel(); var name = Label("ClearDesk", 22, "#152139"); name.FontWeight = FontWeights.SemiBold; brandText.Children.Add(name); brandText.Children.Add(Label("桌面美化", 11, "#7D889B")); brand.Children.Add(brandText); top.Children.Add(brand);
            var searchBox = new Grid { Margin = new Thickness(0,28,0,0) };
            search.Height = 40; search.Padding = new Thickness(30,8,8,8); AutomationProperties.SetName(search, "搜索设置"); search.ToolTip = "搜索所有设置";
            searchBox.Children.Add(search);
            var magnifier = Label("\uE721", 13, "#8390A6"); magnifier.FontFamily = new FontFamily("Segoe MDL2 Assets"); magnifier.Margin = new Thickness(10,0,0,0); magnifier.IsHitTestVisible = false; searchBox.Children.Add(magnifier);
            var placeholder = Label("搜索设置", 12, "#8C97A9"); placeholder.Margin = new Thickness(30,0,0,0); placeholder.IsHitTestVisible = false; searchBox.Children.Add(placeholder);
            search.TextChanged += delegate { placeholder.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Render(); };
            top.Children.Add(searchBox); sidebar.Children.Add(top);
            var nav = new StackPanel { Margin = new Thickness(12,0,12,0) };
            string[] names = { "通用", "桌面收纳", "Dock 栏", "音乐组件", "外观与材质", "关于", "组件管理" };
            string[] glyphs = { "\uE713", "\uE80A", "\uE7F4", "\uE8D6", "\uE790", "\uE946", "\uECA5" };
            for (int i = 0; i < names.Length; i++)
            {
                int index = i; var page = new Page { Name = names[i] };
                var item = new StackPanel { Orientation = Orientation.Horizontal };
                var glyph = Label(glyphs[i], 19); glyph.FontFamily = new FontFamily("Segoe MDL2 Assets"); glyph.ClearValue(TextBlock.ForegroundProperty); glyph.Width = 33;
                var caption = Label(names[i],14); caption.ClearValue(TextBlock.ForegroundProperty); item.Children.Add(glyph); item.Children.Add(caption);
                var button = ActionButton("", delegate { selected = index; search.Text = ""; if (index == ManagerPage) BuildManager(); Render(); }); button.Content = item;
                button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Padding = new Thickness(16,11,8,11); button.Margin = new Thickness(0,2,0,2);
                AutomationProperties.SetName(button, names[i]); page.Navigation = button; pages.Add(page); nav.Children.Add(button);
            }
            Grid.SetRow(nav,1); sidebar.Children.Add(nav);
            var version = Label("版本 " + Version, 11, "#8C97A9"); version.Margin = new Thickness(24,20,0,22); Grid.SetRow(version,2); sidebar.Children.Add(version);
            layout.Children.Add(sidebar);
            var main = new Grid(); main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); main.RowDefinitions.Add(new RowDefinition()); main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Grid.SetColumn(main,1); layout.Children.Add(main);
            var header = new StackPanel { Margin = new Thickness(30,47,30,22) }; heading.FontWeight = FontWeights.SemiBold; header.Children.Add(heading); subtitle.Margin = new Thickness(0,6,0,0); header.Children.Add(subtitle); main.Children.Add(header);
            scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(30,0,23,10) }; Grid.SetRow(scroll,1); main.Children.Add(scroll);
            var footer = new Grid { Margin = new Thickness(30,14,30,18) }; footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            status.MaxWidth = 230; status.Margin = new Thickness(0,0,10,0); footer.Children.Add(status);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var restore = ActionButton("恢复桌面图标", delegate { if (!Program.Preview) Program.Services.SetIcons(true,false); status.Text = Program.Preview ? "预览模式：未更改系统桌面。" : "已恢复桌面图标。"; }); restore.Margin = new Thickness(0,0,8,0); actions.Children.Add(restore);
            var tray = ActionButton("收起到托盘", Hide); tray.Margin = new Thickness(0,0,8,0); actions.Children.Add(tray);
            actions.Children.Add(ActionButton("应用设置", Apply, true)); Grid.SetColumn(actions,1); footer.Children.Add(actions); Grid.SetRow(footer,2); main.Children.Add(footer);
            var chrome = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            var minimize = Caption("\uE921", "最小化", delegate { SystemCommands.MinimizeWindow(this); });
            var maximize = Caption("\uE922", "最大化 / 还原", delegate { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); });
            chrome.Children.Add(minimize); chrome.Children.Add(maximize); chrome.Children.Add(Caption("\uE8BB", "关闭设置", Close));
            Grid.SetColumn(chrome,1); layout.Children.Add(chrome);
            Content = layout;
            BuildPages(startup); BuildManager(); Render();
            Activated += delegate { if (selected == ManagerPage && search.Text.Length == 0 && !editing) RefreshManager(); };
        }
        Button Caption(string glyph, string name, Action action)
        {
            var b = ActionButton(glyph, action); b.FontFamily = new FontFamily("Segoe MDL2 Assets"); b.FontSize = 11; b.Width = 44; b.Height = 36; b.MinHeight = 0; b.Padding = new Thickness(0); b.Background = Brushes.Transparent; b.BorderThickness = new Thickness(0); b.ToolTip = name; AutomationProperties.SetName(b,name); WindowChrome.SetIsHitTestVisibleInChrome(b,true); return b;
        }
        void Apply()
        {
            Program.Save(); Program.Rebuild(); if (!Program.Preview) Program.Services.Apply(); status.Text = "已应用，设置已保存。";
        }
        void Changed() { Program.Save(); status.Text = "已保存，点击应用更新桌面组件。"; }
        Group AddGroup(int page, string title)
        {
            var rows = new StackPanel(); var panel = new StackPanel(); var label = Label(title,16,"#182337"); label.FontWeight = FontWeights.SemiBold; label.Margin = new Thickness(0,0,0,8); panel.Children.Add(label); panel.Children.Add(rows);
            var g = new Group { Name = title, Rows = rows, Card = new Border { Background = Brushes.White, BorderBrush = Color("#E0E5EE"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13), Padding = new Thickness(20,16,20,10), Margin = new Thickness(0,0,0,16), Child = panel } }; pages[page].Groups.Add(g); return g;
        }
        TextBlock Row(Group group, string title, string description, FrameworkElement control = null)
        {
            TextBlock hintBlock = null;
            var row = new Grid { Margin = new Thickness(0,0,0,0) }; row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var labels = new StackPanel { Margin = new Thickness(0,8,control == null ? 0 : 20,8), VerticalAlignment = VerticalAlignment.Center }; labels.Children.Add(Label(title));
            if (!string.IsNullOrEmpty(description)) { var hint = Label(description,12,"#7D889B"); hint.Margin = new Thickness(0,4,0,0); labels.Children.Add(hint); hintBlock = hint; }
            row.Children.Add(labels); if (control != null) { control.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(control,1); row.Children.Add(control); }
            var line = new Border { BorderBrush = Color("#EDF0F5"), BorderThickness = new Thickness(0,1,0,0), Child = row };
            group.Rows.Children.Add(line); group.Entries.Add(new KeyValuePair<FrameworkElement,string>(line, title + " " + description));
            return hintBlock;
        }
        // 2.0.9: shows whether Windows will really start ClearDesk at logon (StartupApproved), and opens
        // Windows' 启动应用 page when it was switched off there.
        void StartupRow(Group group, Action<bool> startup)
        {
            const string title = "登录后自动启动";
            var toggle = new CheckBox { IsChecked = Program.Startup() }; AutomationProperties.SetName(toggle,title);
            var open = ActionButton("启动应用设置", delegate { try { Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true }); } catch { status.Text = "无法打开 Windows 设置，请在任务管理器的“启动应用”中查看。"; } });
            open.Margin = new Thickness(12,0,0,0); AutomationProperties.SetName(open,"打开 Windows 启动应用设置"); open.ToolTip = "ClearDesk 被 Windows 或安全软件禁用时，在这里重新打开";
            var panel = new StackPanel { Orientation = Orientation.Horizontal }; panel.Children.Add(toggle); panel.Children.Add(open);
            var hint = Row(group,title,Program.StartupDescription(),panel);
            Activated += delegate { if (hint != null) hint.Text = Program.StartupDescription(); };
            toggle.Click += delegate
            {
                startup(toggle.IsChecked == true);
                if (toggle.IsChecked == true && !Program.LastStartupOk) toggle.IsChecked = false;
                if (hint != null) hint.Text = Program.StartupDescription();
                Changed(); if (Program.LastStartupMessage.Length > 0) status.Text = Program.LastStartupMessage;
            };
        }
        void Toggle(Group group, string title, string description, bool initial, Action<bool> change)
        {
            var toggle = new CheckBox { IsChecked = initial }; AutomationProperties.SetName(toggle,title);
            toggle.Click += delegate { change(toggle.IsChecked == true); Changed(); }; Row(group,title,description,toggle);
        }
        void Range(Group group, string title, string description, int min, int max, int initial, string unit, Action<int> change)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var slider = new Slider { Minimum = min, Maximum = max, Value = initial, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 145 }; AutomationProperties.SetName(slider,title);
            var value = Label(initial + unit,12,"#1677FF"); value.TextAlignment = TextAlignment.Center;
            var pill = new Border { Background = Color("#EDF4FF"), CornerRadius = new CornerRadius(12), Width = 62, Padding = new Thickness(4,4,4,4), Margin = new Thickness(12,0,0,0), Child = value };
            slider.ValueChanged += delegate { change((int)slider.Value); value.Text = ((int)slider.Value) + unit; Changed(); };
            panel.Children.Add(slider); panel.Children.Add(pill); Row(group,title,description,panel);
        }
        void Choice(Group group, string title, string description, string[] labels, int initial, Action<int> change)
        {
            var choice = new ComboBox { ItemsSource = labels, SelectedIndex = initial }; AutomationProperties.SetName(choice,title);
            choice.SelectionChanged += delegate { if (choice.SelectedIndex >= 0) { change(choice.SelectedIndex); Changed(); } }; Row(group,title,description,choice);
        }
        void BuildPages(Action<bool> startup)
        {
            var g = AddGroup(0,"桌面与启动");
            StartupRow(g,startup);
            Toggle(g,"双击桌面隐藏图标","在桌面空白处双击，隐藏或显示原生图标。",state.Icons,v => { state.Icons = v; if (!Program.Preview) Program.Services.Apply(); });
            Toggle(g,"启动时隐藏原生图标","退出 ClearDesk 时恢复。",state.HideAtStart,v => state.HideAtStart=v);
            Toggle(g,"空闲时自动隐藏","一段时间没有操作时，自动隐藏桌面图标。",state.AutoHide,v => state.AutoHide=v);
            Toggle(g,"操作时恢复显示","恢复被自动隐藏的图标。",state.RestoreOnInput,v => state.RestoreOnInput=v);
            Range(g,"自动隐藏延迟","空闲多久后隐藏图标。",3,120,state.IdleSeconds," 秒",v => state.IdleSeconds=v);
            g=AddGroup(0,"任务栏与窗口");
            Toggle(g,"透明任务栏","保留任务栏图标清晰度，退出时恢复。",state.Taskbar,v => { state.Taskbar=v; if (!Program.Preview) Program.Services.Apply(); });
            Toggle(g,"顶部任务栏自动隐藏","任务栏放在屏幕顶部时 Windows 不支持自动隐藏：开启后由 ClearDesk 收起任务栏、让窗口铺满屏幕；鼠标停在屏幕最上边缘即可唤出，打开开始菜单或托盘时也会显示。关闭或退出 ClearDesk 时自动恢复。",state.TopTaskbarAutoHide,v => { state.TopTaskbarAutoHide=v; if (!Program.Preview) Program.Services.Apply(); });
            Toggle(g,"窗口最小化动画","实验性 · 部分窗口可能不支持。",state.Animation,v => { state.Animation=v; if (!Program.Preview) Program.Services.Apply(); });
            Toggle(g,"恢复窗口时播放动画","从任务栏或桌面恢复窗口时播放。",state.RestoreAnimation,v => state.RestoreAnimation=v);
        Toggle(g,"GPU \u786c\u4ef6\u52a0\u901f\u6e32\u67d3","\u52a8\u753b\u66f4\u987a\u6ed1\uff0c\u4f46\u5185\u5b58\u5360\u7528\u7ea6\u589e\u52a0 150 MB\uff1b\u91cd\u542f ClearDesk \u540e\u751f\u6548\u3002\u9ed8\u8ba4\u5173\u95ed\u4ee5\u8282\u7701\u5185\u5b58\u3002",state.GpuRendering,v => state.GpuRendering=v);
            g=AddGroup(1,"桌面收纳");
            Row(g,"新建收纳格","拖入文件只添加引用，不移动原文件。",ActionButton("新建",Program.NewBox));
            Row(g,"重新显示桌面收纳","恢复已关闭或折叠的收纳格。",ActionButton("恢复显示",delegate { foreach(var b in state.Boxes) { b.Enabled=true; b.Collapsed=false; b.Height=Math.Max(170,b.Height); } Changed(); Program.Rebuild(); }));
            Row(g,"整理与查找","拖动标题移动位置；收纳格支持搜索、网格与列表、缩放和折叠。");
            g=AddGroup(2,"Dock 与启动台");
            Toggle(g,"显示 Dock 栏","固定常用应用，查看正在运行的程序。",state.Dock,v => state.Dock=v);
            Range(g,"图标大小","调整 Dock 的基础图标尺寸。",32,72,state.DockSize," px",v => state.DockSize=v);
            Range(g,"距屏幕底部距离","调整 Dock 与屏幕底边的距离，实时生效；自动隐藏和遮挡判断按新位置计算。",0,300,(int)Math.Round(state.DockOffset)," px",v => { state.DockOffset=v; Program.RefreshDockOffset(); });
            Toggle(g,"窗口遮挡时自动隐藏","其他应用的窗口挡住 Dock 时，Dock 向下滑出屏幕；鼠标移到屏幕底部边缘即可唤出，遮挡结束后自动回来。",state.DockAutoHide,v => { state.DockAutoHide=v; Program.RefreshDockAutoHide(); });
            Row(g,"桌面启动台","快速查找并打开桌面项目。",ActionButton("打开启动台",Program.Launchpad));
            var launchpad=Program.Widget("launchpad",0,0,880,520);
            Choice(g,"启动台材质","磨砂会实时模糊启动台后方的窗口和桌面；也可在启动台空白处右键切换。",new[]{"玻璃 · 实时清透","磨砂 · 实时模糊"},launchpad.Material=="frosted"?1:0,i => { launchpad.Material=i==1?"frosted":"liquid"; launchpad.MaterialSelected=true; Program.Save(); });
            Row(g,"使用方式","拖动调整顺序；右键固定应用。点击运行中的应用切回它的窗口；若它已在最前面，再点一次会最小化（与任务栏一致）。");
            g=AddGroup(3,"音乐控制");
            Toggle(g,"显示音乐组件","显示歌曲信息，控制播放、上一首和下一首。",state.Music,v => state.Music=v);
            var sources=new[]{"auto","qq","netease","kugou","local"};
            Choice(g,"音乐来源","选择后点击应用更新音乐组件。",new[]{"自动选择播放器","QQ 音乐","网易云音乐","酷狗音乐","本地音乐"},Array.IndexOf(sources,MusicSources.Normalize(state.MusicSource)),i => state.MusicSource=sources[i]);
            Toggle(g,"显示歌词","优先读取 QQ 音乐／网易云已加载的客户端歌词；没有缓存时向 LRCLIB 发送歌曲信息匹配。",state.MusicLyrics,v=>state.MusicLyrics=v);
            Row(g,"连接播放器","先在客户端播放歌曲。QQ 音乐、网易云音乐已实测；酷狗兼容性尚未真机验证。");
            Row(g,"本地播放","在桌面音乐组件中选择本地音乐，并添加音频文件。");
            g=AddGroup(4,"Dock 材质");
            var dock=Program.Widget("dock",0,0,620,140);
            Choice(g,"背景材质","图标保持清晰；磨砂实时模糊后方窗口。",new[]{"玻璃 · 实时清透","磨砂 · 实时模糊"},dock.Material=="frosted"?1:0,i => { dock.Material=i==1?"frosted":"liquid"; dock.MaterialSelected=true; });
            Toggle(g,"动态壁纸实时模糊","默认关闭。开启后“磨砂”材质的组件改为实时模糊身后的真实画面（支持 Wallpaper Engine 等动态壁纸和窗口），由系统合成器（DWM）用显卡完成，会略增加 GPU 占用；关闭时只模糊静态壁纸图片。",state.LiveDesktopBlur,v => { state.LiveDesktopBlur=v; Program.Save(); if (!Program.Preview) Program.Rebuild(); });
            Row(g,"组件材质","右键各桌面组件可独立选择材质、调整大小、锁定位置、新建组件或恢复位置；悬停时拖动右下角也可缩放。");
            g=AddGroup(4,"桌面组件");
            Toggle(g,"时钟","时间与日期。",state.Clock,v=>state.Clock=v);
            Toggle(g,"性能","CPU、GPU、内存与网络。",state.Performance,v=>state.Performance=v);
            Toggle(g,"日历","查看日期。",state.Calendar,v=>state.Calendar=v);
            Toggle(g,"黄历 / 农历","查看农历信息。",state.Almanac,v=>state.Almanac=v);
            Toggle(g,"天气","自动获取所在地并显示天气。",state.Weather,v=>state.Weather=v);
            Toggle(g,"快捷控制","音量、WLAN、蓝牙开关与 Windows 设置快捷入口。",state.QuickControls,v=>state.QuickControls=v);
            foreach (var tool in ToolWidgets.Kinds) { string kind = tool.Key; Toggle(g,tool.Value,ToolWidgets.Descriptions[kind],ToolWidgets.On(kind),v=>ToolWidgets.Set(kind,v)); }   // 2.2.0
            Toggle(g,"磁吸对齐","拖动组件时吸附屏幕边缘、其他组件的边缘与中线及等距间隔，并显示参考线；按住 Alt 临时关闭。",state.Magnet,v=>state.Magnet=v);
            Toggle(g,"桌面右键新建组件","在桌面右键菜单加入“ClearDesk 新建组件”。未安装 Windows 11 一级菜单时位于“显示更多选项”里。",state.DesktopMenu,v=>{ state.DesktopMenu=v; DesktopMenu.Sync(); });
            if (ModernMenu.Available)
            {
                Toggle(g,"Windows 11 一级菜单显示","已安装一级菜单时，在右键第一层显示“ClearDesk 新建组件”。",state.ModernMenu,v=>{ state.ModernMenu=v; DesktopMenu.Sync(); });
                var menuButtons = new StackPanel { Orientation = Orientation.Horizontal };
                Button enableMenu = null, disableMenu = null; TextBlock menuStatus = null;
                Action<string> showMenuState = delegate (string extra)
                {
                    var location = ModernMenu.InstalledLocation();
                    if (menuStatus != null) menuStatus.Text = ModernMenu.StatusText(ModernMenu.Status(location)) + (string.IsNullOrEmpty(extra) ? "" : "\n" + extra);
                    if (disableMenu != null) disableMenu.IsEnabled = location != null;
                };
                Func<bool, System.Threading.Tasks.Task> runMenu = async delegate (bool enable)
                {
                    enableMenu.IsEnabled = disableMenu.IsEnabled = false;
                    menuStatus.Text = enable ? "正在启用…（请在弹出的管理员授权窗口中选择“是”）" : "正在关闭并卸载…";
                    var r = await ModernMenu.Run(enable);
                    enableMenu.IsEnabled = true;
                    showMenuState(r.Key == 0 ? r.Value : "未完成：" + r.Value);
                    if (r.Key == 0) DesktopMenu.Sync();
                };
                enableMenu = ActionButton("一键启用", async delegate
                {
                    var answer = MessageBox.Show(this,
                        "将在本机启用 Windows 11 桌面右键一级菜单“ClearDesk 新建组件”：\n\n" +
                        "1. 在当前用户下生成一张仅本机使用的自签名证书（私钥不可导出，不会上传或随软件分发）；\n" +
                        "2. 用它为菜单组件签名，并以当前文件夹注册到 Windows；\n" +
                        "3. 让系统信任这张证书需要管理员权限，接下来会弹出一次 UAC 授权。\n\n" +
                        "以后可随时点“关闭/卸载”移除程序包和证书。是否继续？",
                        "启用桌面右键一级菜单", MessageBoxButton.OKCancel, MessageBoxImage.Information);
                    if (answer == MessageBoxResult.OK) await runMenu(true);
                }, true);
                enableMenu.Margin = new Thickness(0,0,8,0);
                disableMenu = ActionButton("关闭/卸载", async delegate { await runMenu(false); });
                menuButtons.Children.Add(enableMenu); menuButtons.Children.Add(disableMenu);
                menuStatus = Row(g,"桌面右键一级菜单",ModernMenu.StatusText(ModernMenu.State.Off),menuButtons);
                showMenuState(null);
            }
            Row(g,"便签","可新建多张，自动保存；右键可换颜色或删除。",ActionButton("新建便签",delegate { Program.NewNote(); }));
            g=AddGroup(4,"天气与隐私");
            Toggle(g,"自动天气联网","天气：中国大陆优先腾讯天气，备用 Open-Meteo、met.no、wttr.in，逐个回退；定位只在需要时请求。不保存 IP，仅在本机缓存最近一次天气和约 1 公里精度的位置，断网时显示上次数据。",state.WeatherOnline,v=>{ state.WeatherOnline=v; Program.Save(); RefreshWeather(false); });
            WeatherRows(g);
            g=AddGroup(5,"软件更新");
            UpdateRows(g);
            g=AddGroup(5,"ClearDesk");
            Row(g,"清透桌面","版本 "+Version+" · 维护者 visual-life");
            Row(g,"本地配置","配置保存在本机，无账户、广告或遥测；检查更新只会请求版本清单，不上传任何个人信息。",ActionButton("打开配置目录",delegate { Process.Start(new ProcessStartInfo(Program.Data){UseShellExecute=true}); }));
            Row(g,"恢复与退出","关闭所有组件，恢复桌面和任务栏。",ActionButton("退出并恢复",Program.Quit));
            Row(g,"使用提示","关闭设置窗口后，ClearDesk 继续在托盘运行。双击托盘图标可再次打开设置。");
        }
        // 2.1.1: weather location — Windows location, IP vote, or a county-level place chosen by hand
        static void RefreshWeather(bool relocate)
        {
            if (relocate) WeatherService.Shared.PlaceChanged();
            foreach (var w in Program.OpenWindows.OfType<WeatherWindow>().ToList()) if (!w.WasClosed) { var t = w.FetchAsync(true); }
        }
        static string PlaceStatus(Settings st)
        {
            var svc = WeatherService.Shared; var c = svc.Cached; var p = svc.CurrentPlace;
            if (st.WeatherManual) { var m = st.ManualPlace(); if (m != null) return "当前：" + m.Display + (m.City.Length > 0 && m.City != m.Display ? "（" + m.City + "）" : "") + " · 手动选择"; }
            if (p == null || p.Display.Length == 0) return "当前：尚未定位（打开天气组件后自动定位）。";
            return "当前：" + p.Display + (p.City.Length > 0 && p.City != p.Display ? "（" + p.City + "）" : "") + " · " + (string.IsNullOrEmpty(p.Source) ? "自动" : p.Source) + (p.Kind == "ip" ? "，只能到城市级别" : "");
        }
        void WeatherRows(Group g)
        {
            TextBlock status = null, hint = null; Button search = null; TextBox box = null; StackPanel results = null; Border resultsLine = null;
            Toggle(g, "使用 Windows 定位", "优先用 Windows 定位服务（Wi-Fi / GPS，比 IP 准，不受代理影响）。需要在 Windows 设置 → 隐私和安全性 → 位置 中打开定位并允许桌面应用访问；不可用时自动改用网络 IP。", state.WeatherSystemLocation, v => { state.WeatherSystemLocation = v; Program.Save(); RefreshWeather(true); });
            var relocate = ActionButton("重新定位", delegate { RefreshWeather(true); status.Text = "正在重新定位并刷新天气…"; });
            AutomationProperties.SetName(relocate, "重新定位并刷新天气");
            status = Row(g, "所在地", PlaceStatus(state), relocate);
            Activated += delegate { if (status != null) status.Text = PlaceStatus(state); };
            box = new TextBox { Width = 170, ToolTip = "输入区、县或城市名，例如 赛罕区、黄浦区" }; AutomationProperties.SetName(box, "手动选择所在地");
            Action doSearch = async delegate
            {
                string q = box.Text.Trim(); if (q.Length == 0) return;
                search.IsEnabled = false; hint.Text = "正在搜索…"; results.Children.Clear();
                try
                {
                    var found = await PlaceLookup.SearchAsync(WeatherService.Download, q, System.Threading.CancellationToken.None, null);
                    if (found.Count == 0) hint.Text = "没有找到“" + q + "”。可换成区县全称（如 赛罕区）再试，或检查网络。";
                    else
                    {
                        hint.Text = "找到 " + found.Count + " 个结果，点击要使用的地点：";
                        foreach (var place in found)
                        {
                            var pick = place; string label = pick.FullName + (pick.CountryCode.Length > 0 && pick.CountryCode != "CN" ? "（" + pick.CountryCode + "）" : "");
                            var b = ActionButton(label, delegate { ApplyManual(pick, hint, status); }); b.Margin = new Thickness(0, 4, 8, 0); b.HorizontalAlignment = HorizontalAlignment.Left;
                            AutomationProperties.SetName(b, "选择 " + label); results.Children.Add(b);
                        }
                    }
                }
                catch (Exception e) { hint.Text = "搜索失败：" + WeatherService.Reason(e); }
                finally { search.IsEnabled = true; results.Visibility = results.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed; }
            };
            search = ActionButton("搜索", delegate { doSearch(); }); search.Margin = new Thickness(8, 0, 0, 0); AutomationProperties.SetName(search, "搜索所在地");
            box.KeyDown += (s, e) => { if (e.Key == Key.Enter) doSearch(); };
            var auto = ActionButton("恢复自动", delegate { state.WeatherManual = false; Program.Save(); RefreshWeather(true); hint.Text = "已恢复自动定位。"; status.Text = PlaceStatus(state); }); auto.Margin = new Thickness(8, 0, 0, 0); AutomationProperties.SetName(auto, "恢复自动定位");
            var panel = new StackPanel { Orientation = Orientation.Horizontal }; panel.Children.Add(box); panel.Children.Add(search); panel.Children.Add(auto);
            hint = Row(g, "手动选择区县", "输入区、县或城市名（如 赛罕区、黄浦区）搜索并选择。IP 定位通常只能到城市级别，要显示准确的区县就在这里选。", panel);
            results = new StackPanel { Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
            resultsLine = new Border { BorderBrush = Color("#EDF0F5"), BorderThickness = new Thickness(0, 1, 0, 0), Child = results };
            g.Rows.Children.Add(resultsLine); g.Entries.Add(new KeyValuePair<FrameworkElement, string>(resultsLine, "手动选择区县 搜索结果"));
        }
        async void ApplyManual(GeoPlace p, TextBlock hint, TextBlock status)
        {
            hint.Text = "正在确认 " + p.FullName + " …";
            try
            {
                // normalise the chosen coordinates with a reverse geocode so the name is the proper county-level one (黄浦 → 黄浦区)
                if (p.HasCoords && (p.District.Length == 0 || !PlaceNames.IsCountyLevel(p.District)))
                {
                    var rev = await PlaceLookup.ReverseAsync(WeatherService.Download, p.Lat, p.Lon, System.Threading.CancellationToken.None, null);
                    if (rev.District.Length > 0 && (p.CountryCode.Length == 0 || rev.CountryCode == p.CountryCode)) { p.District = rev.District; if (rev.City.Length > 0) p.City = rev.City; if (rev.Province.Length > 0) p.Province = rev.Province; }
                }
            }
            catch { }
            state.WeatherManual = true; state.ManualProvince = p.Province; state.ManualCity = p.City; state.ManualDistrict = p.District; state.ManualCountry = p.CountryCode.Length > 0 ? p.CountryCode : "CN";
            state.ManualLat = p.HasCoords ? Math.Round(p.Lat, 2) : 0; state.ManualLon = p.HasCoords ? Math.Round(p.Lon, 2) : 0;
            Program.Save(); RefreshWeather(true);
            hint.Text = "已选择 " + p.Display + "（" + p.FullName + "），天气组件会按它刷新。"; status.Text = PlaceStatus(state);
        }
        // 2.1.0: online update — 检查更新 / 自动检查 / 更新源
        void UpdateRows(Group g)
        {
            TextBlock hint = null; Button check = null;
            check = ActionButton("检查更新", async delegate
            {
                check.IsEnabled = false; hint.Text = "正在检查更新…";
                try
                {
                    var r = await UpdateService.CheckAsync();
                    if (!r.Completed) hint.Text = r.Error + "（" + UpdateService.LastCheckText() + "）";
                    else
                    {
                        UpdateService.Record(r);
                        if (r.Newer) { hint.Text = "发现新版本 " + r.Manifest.Latest + (r.Manifest.NotifyOnly ? "（需要手动下载）。" : "，可以更新。"); UpdateWindow.ShowFor(r.Manifest, true); }
                        else hint.Text = "已是最新版本（" + Version + "）。" + UpdateService.LastCheckText();
                    }
                }
                catch (Exception e) { hint.Text = "检查更新失败：" + e.Message; }
                finally { check.IsEnabled = true; }
            }, true);
            AutomationProperties.SetName(check, "检查更新");
            hint = Row(g, "当前版本 " + Version, UpdateService.LastCheckText(), check);
            Toggle(g, "自动检查更新", "启动后在后台检查，最多每 12 小时一次；只请求版本清单，不上传任何个人信息。关闭后只在你点“检查更新”时联网。", state.AutoUpdateCheck, v => state.AutoUpdateCheck = v);
            var box = new TextBox { Width = 270, Text = state.UpdateSource ?? "", ToolTip = "留空使用默认更新源" }; AutomationProperties.SetName(box, "更新源");
            var reset = ActionButton("恢复默认", delegate { box.Text = ""; }); reset.Margin = new Thickness(8, 0, 0, 0); AutomationProperties.SetName(reset, "恢复默认更新源");
            var panel = new StackPanel { Orientation = Orientation.Horizontal }; panel.Children.Add(box); panel.Children.Add(reset);
            string normal = "默认 " + UpdateSources.Defaults[0] + "。有了域名后在这里填 https://你的域名/ 即可，不需要重新安装；留空使用默认。";
            var srcHint = Row(g, "更新源", normal, panel);
            box.TextChanged += delegate
            {
                string t = box.Text.Trim();
                if (t.Length == 0) { state.UpdateSource = ""; Program.Save(); srcHint.Text = normal; }
                else if (UpdateSources.IsHttpUrl(t)) { state.UpdateSource = t; Program.Save(); srcHint.Text = normal; }
                else srcHint.Text = "地址无效：只支持 http:// 或 https:// 开头的网址。";
            };
            Row(g, "更新方式", "发现新版本后点“立即更新”：下载 → 验证签名和 SHA256 → ClearDesk 自动退出、替换文件并重新打开；失败会自动恢复旧版本。你的设置和便签保存在 %LOCALAPPDATA%\\ClearDesk，更新不会改动。");
        }
        void Render()
        {
            if (pages.Count==0) return;
            string query=search.Text.Trim(); bool filtering=query.Length>0;
            heading.Text=filtering?"搜索结果":pages[selected].Name;
            subtitle.Text=filtering?"查找与“"+query+"”相关的设置。":selected==0?"让桌面保持整洁，让常用触手可及。":selected==ManagerPage?"在一处查看、定位和管理所有桌面组件。":"按你的习惯，调整每一个细节。";
            content.Children.Clear(); int found=0;
            for(int i=0;i<pages.Count;i++)
            {
                var page=pages[i]; bool active=!filtering && i==selected;
                page.Navigation.Background=active?Color("#3985F8"):Brushes.Transparent; page.Navigation.Foreground=active?Brushes.White:Color("#273348"); page.Navigation.BorderBrush=Brushes.Transparent;
                foreach(var group in page.Groups)
                {
                    bool visible=false;
                    foreach(var row in group.Entries)
                    {
                        bool match=!filtering || (page.Name+" "+group.Name+" "+row.Value).IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0;
                        row.Key.Visibility=match?Visibility.Visible:Visibility.Collapsed; visible|=match;
                    }
                    if((filtering || i==selected) && visible) { content.Children.Add(group.Card); found++; }
                }
            }
            if(found==0) { var empty=Label("没有找到相关设置，试试“图标”“音乐”或“材质”。",14,"#7D889B"); empty.Margin=new Thickness(0,20,0,0); content.Children.Add(empty); }
            if(scroll!=null) scroll.ScrollToTop();
        }
        const int ManagerPage = 6;
        bool editing;
        static Border Pill(string text)
        {
            string bg = text == "已关闭" ? "#F1F3F7" : text == "已锁定" ? "#FFF4E5" : "#E8F7EE";
            string fg = text == "已关闭" ? "#6B778C" : text == "已锁定" ? "#9A4A00" : "#147A3D";
            var t = Label(text, 11.5, fg); t.TextAlignment = TextAlignment.Center;
            return new Border { Background = Color(bg), CornerRadius = new CornerRadius(10), Padding = new Thickness(9,3,9,3), MinWidth = 58, Margin = new Thickness(0,0,12,0), Child = t, VerticalAlignment = VerticalAlignment.Center };
        }
        Button Small(string text, Action action, string name, bool danger = false)
        {
            var b = ActionButton(text, action); b.Margin = new Thickness(6,0,0,0); b.MinWidth = 52; b.Padding = new Thickness(10,5,10,5);
            if (danger) { b.Foreground = Color("#C2410C"); }
            AutomationProperties.SetName(b, name); return b;
        }
        bool ConfirmDelete(string text)
        {
            if (TestConfirm != null) return TestConfirm(text);
            return MessageBox.Show(this, text, "ClearDesk · 删除组件", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        }
        internal Func<string,bool> TestConfirm;
        internal List<WidgetEntry> TestEntries;

        void BuildManager()
        {
            var page = pages[ManagerPage]; page.Groups.Clear();
            var entries = WidgetManager.List(); TestEntries = entries;
            var g = AddGroup(ManagerPage, "组件管理");
            Button create;
            var menu = new ContextMenu();
            foreach (var kind in WidgetCatalog.Kinds)
            {
                string id = kind.Key; var mi = new MenuItem { Header = kind.Value };
                mi.Click += delegate { Program.NewWidget(id); RefreshManager(); };
                menu.Items.Add(mi);
            }
            MenuTheme.Decorate(menu);
            create = new Button { Content = "新建组件", Background = Color("#1677FF"), Foreground = Brushes.White, BorderBrush = Color("#1677FF") };
            create.Click += delegate { menu.PlacementTarget = create; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true; };
            AutomationProperties.SetName(create, "新建组件");
            Row(g, "关闭与删除", "“关闭”只是从桌面隐藏，内容、位置和设置都保留，可随时重新显示；“删除”会永久移除该组件及其保存的数据（便签文字、收纳格引用等），但绝不会删除你的真实文件。", create);
            AddManagerGroup("桌面组件", entries.Where(e => WidgetManager.Singletons.Contains(e.Kind)));
            AddManagerGroup("便签", entries.Where(e => e.Kind == "note"), "还没有便签。点击上方“新建组件”→ 便签。");
            AddManagerGroup("桌面收纳", entries.Where(e => e.Kind == "box"), "还没有收纳格。");
        }

        void AddManagerGroup(string title, IEnumerable<WidgetEntry> items, string empty = null)
        {
            var list = items.ToList();
            var g = AddGroup(ManagerPage, title + "（" + list.Count + "）");
            if (list.Count == 0 && empty != null) Row(g, empty, null);
            foreach (var e in list) ManagerRow(g, e);
        }

        void ManagerRow(Group g, WidgetEntry e)
        {
            var entry = e;
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var labels = new StackPanel { Margin = new Thickness(0,8,16,8), VerticalAlignment = VerticalAlignment.Center };
            if (e.Kind == "box")
            {
                var name = new TextBox { Text = e.Name, FontSize = 14, MinWidth = 180, MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(6,3,6,3), ToolTip = "点击即可重命名，回车保存" };
                AutomationProperties.SetName(name, "收纳格名称");
                string original = e.Name;
                name.GotKeyboardFocus += delegate { editing = true; };
                name.TextChanged += delegate { if (!string.IsNullOrWhiteSpace(name.Text)) WidgetManager.Rename(entry, name.Text, false); };
                Action commit = delegate
                {
                    editing = false;
                    if (string.IsNullOrWhiteSpace(name.Text)) { name.Text = original; WidgetManager.Rename(entry, original, true); return; }
                    WidgetManager.Rename(entry, name.Text, true); original = entry.Name; status.Text = "已重命名为“" + entry.Name + "”。";
                };
                name.LostKeyboardFocus += delegate { commit(); };
                name.KeyDown += (s, k) => { if (k.Key == System.Windows.Input.Key.Enter) { commit(); Keyboard.ClearFocus(); } else if (k.Key == System.Windows.Input.Key.Escape) { name.Text = original; commit(); Keyboard.ClearFocus(); } };
                labels.Children.Add(name);
            }
            else labels.Children.Add(Label(e.Name));
            var hint = Label(e.Detail, 12, "#7D889B"); hint.Margin = new Thickness(0,4,0,0); labels.Children.Add(hint);
            row.Children.Add(labels);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(Pill(e.Status));
            actions.Children.Add(Small(e.Visible ? "关闭" : "显示", delegate
            {
                WidgetManager.SetVisible(entry, !entry.Visible);
                status.Text = entry.Visible ? "已显示“" + entry.Name + "”。" : "已关闭“" + entry.Name + "”，可随时重新显示。"; RefreshManager();
            }, (e.Visible ? "关闭 " : "显示 ") + e.Name));
            if (WidgetManager.CanResetSize(e))
                actions.Children.Add(Small("重置大小", delegate { WidgetManager.ResetSize(entry); status.Text = "已将“" + entry.Name + "”恢复为默认大小。"; RefreshManager(); }, "重置大小 " + e.Name));
            actions.Children.Add(Small("定位", delegate { WidgetManager.Locate(entry); RefreshManager(); }, "定位 " + e.Name));
            actions.Children.Add(Small("删除", delegate
            {
                if (WidgetManager.Delete(entry, ConfirmDelete)) { status.Text = "已删除“" + entry.Name + "”。"; RefreshManager(); }
            }, "删除 " + e.Name, true));
            Grid.SetColumn(actions, 1); row.Children.Add(actions);
            var line = new Border { BorderBrush = Color("#EDF0F5"), BorderThickness = new Thickness(0,1,0,0), Child = row };
            g.Rows.Children.Add(line); g.Entries.Add(new KeyValuePair<FrameworkElement,string>(line, e.Name + " " + e.Detail + " " + e.Status));
        }

        internal void RefreshManager()
        {
            double offset = scroll != null ? scroll.VerticalOffset : 0;
            BuildManager(); Render();
            if (scroll != null) { scroll.UpdateLayout(); scroll.ScrollToVerticalOffset(offset); }
        }

        internal void TestScrollEnd() { scroll.UpdateLayout(); scroll.ScrollToEnd(); scroll.UpdateLayout(); }
        internal void TestSearch(string text) { search.Text=text; }
        internal void TestSelect(int index) { selected=index; search.Text=""; Render(); }
        internal int TestCards { get { return content.Children.Count; } }
    }
}
