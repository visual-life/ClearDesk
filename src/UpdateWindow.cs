using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;

namespace ClearDesk
{
    // White Windows 11 style dialog: version, release notes, 立即更新 / 稍后提醒 / 跳过此版本, download progress.
    internal sealed class UpdateWindow : Window
    {
        static UpdateWindow current;
        readonly UpdateManifest m;
        readonly TextBlock status, notes;
        readonly ProgressBar bar;
        readonly Button now, later, skip;
        CancellationTokenSource cts;
        bool busy;
        internal string TestStatus { get { return status.Text; } }
        internal Button TestNow { get { return now; } }
        internal Button TestLater { get { return later; } }
        internal Button TestSkip { get { return skip; } }
        internal string TestNotes { get { return notes.Text; } }

        static Brush Br(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }
        static TextBlock Label(string text, double size, string color, FontWeight? weight = null)
        {
            var t = new TextBlock { Text = text, FontSize = size, Foreground = Br(color), TextWrapping = TextWrapping.Wrap };
            if (weight.HasValue) t.FontWeight = weight.Value; return t;
        }
        static string Mb(long n) { return (n / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB"; }

        internal static UpdateWindow ShowFor(UpdateManifest manifest, bool manual)
        {
            if (current != null) { try { current.Show(); current.Activate(); return current; } catch { current = null; } }
            current = new UpdateWindow(manifest);
            current.Closed += delegate { current = null; };
            current.Show(); current.Activate();
            return current;
        }

        internal UpdateWindow(UpdateManifest manifest)
        {
            m = manifest;
            Title = "ClearDesk · 软件更新"; try { Icon = AppBrand.Icon; } catch { }
            Width = 520; SizeToContent = SizeToContent.Height; MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height - 40);
            ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Br("#FFFFFF"); Foreground = Br("#273348"); FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            Resources = SettingsTheme.Create();
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 42, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(16), UseAeroCaptionButtons = false });

            var root = new Grid { Background = Brushes.White };
            var panel = new StackPanel { Margin = new Thickness(32, 38, 32, 26) };
            var head = new DockPanel();
            head.Children.Add(new Image { Source = AppBrand.Icon, Width = 44, Height = 44, Margin = new Thickness(0, 0, 14, 0) });
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(Label("发现新版本", 22, "#101828", FontWeights.SemiBold));
            titles.Children.Add(Label("ClearDesk " + m.Latest + " 已发布", 13, "#7D889B"));
            head.Children.Add(titles); panel.Children.Add(head);

            var info = new Border { Background = Br("#F5F8FC"), CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 20, 0, 0) };
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
            var cur = new StackPanel(); cur.Children.Add(Label("当前版本", 11.5, "#8C97A9")); cur.Children.Add(Label(UpdateService.Version, 18, "#273348", FontWeights.SemiBold));
            var latest = new StackPanel(); latest.Children.Add(Label("最新版本", 11.5, "#8C97A9")); latest.Children.Add(Label(m.Latest, 18, "#1677FF", FontWeights.SemiBold));
            Grid.SetColumn(latest, 1); grid.Children.Add(cur); grid.Children.Add(latest);
            var meta = new System.Collections.Generic.List<string>();
            if (m.Date.Length > 0) meta.Add("发布于 " + m.Date);
            if (m.Size > 0) meta.Add("大小 " + Mb(m.Size));
            if (m.Signed) meta.Add("已通过签名验证 ✓");
            var infoStack = new StackPanel(); infoStack.Children.Add(grid);
            if (meta.Count > 0) { var mt = Label(string.Join("  ·  ", meta), 11.5, "#7D889B"); mt.Margin = new Thickness(0, 10, 0, 0); infoStack.Children.Add(mt); }
            info.Child = infoStack; panel.Children.Add(info);

            var nl = Label("更新说明", 14, "#182337", FontWeights.SemiBold); nl.Margin = new Thickness(0, 20, 0, 8); panel.Children.Add(nl);
            string text = m.Changes.Count > 0 ? string.Join("\n", m.Changes.Select(c => "•  " + c)) : m.NotesText.Length > 0 ? (m.NotesText.Length > 1800 ? m.NotesText.Substring(0, 1800) + "…" : m.NotesText) : "本次更新的详细说明请查看发布页。";
            notes = Label(text, 12.5, "#4A566B"); notes.LineHeight = 20;
            var scroll = new ScrollViewer { Content = notes, MaxHeight = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) };
            panel.Children.Add(new Border { BorderBrush = Br("#E6EAF1"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 8, 10), Child = scroll });
            if (!string.IsNullOrEmpty(m.PageUrl))
            {
                var open = new Button { Content = "查看完整发布说明", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Br("#1677FF"), Padding = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 0 };
                open.Click += delegate { try { Process.Start(new ProcessStartInfo(m.PageUrl) { UseShellExecute = true }); } catch { } };
                AutomationProperties.SetName(open, "查看完整发布说明"); panel.Children.Add(open);
            }

            bar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 18, 0, 0), Foreground = Br("#1677FF"), Background = Br("#E6EAF1"), Visibility = Visibility.Collapsed };
            status = Label("", 12, "#7D889B"); status.Margin = new Thickness(0, 8, 0, 0); status.Visibility = Visibility.Collapsed;
            panel.Children.Add(bar); panel.Children.Add(status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
            skip = new Button { Content = "跳过此版本", Margin = new Thickness(0, 0, 8, 0) }; AutomationProperties.SetName(skip, "跳过此版本");
            later = new Button { Content = "稍后提醒", Margin = new Thickness(0, 0, 8, 0) }; AutomationProperties.SetName(later, "稍后提醒");
            now = new Button { Content = m.NotifyOnly ? "打开下载页" : "立即更新", Background = Br("#1677FF"), Foreground = Brushes.White, BorderBrush = Br("#1677FF") }; AutomationProperties.SetName(now, now.Content.ToString());
            buttons.Children.Add(skip); buttons.Children.Add(later); buttons.Children.Add(now); panel.Children.Add(buttons);
            root.Children.Add(panel);

            var close = new Button { Content = "\uE8BB", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11, Width = 44, Height = 36, MinHeight = 0, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, ToolTip = "关闭" };
            AutomationProperties.SetName(close, "关闭更新窗口"); WindowChrome.SetIsHitTestVisibleInChrome(close, true); close.Click += delegate { Close(); };
            root.Children.Add(close);
            Content = root;

            skip.Click += delegate { UpdateService.Skip(m); Close(); };
            later.Click += delegate { UpdateService.Remind(m); Close(); };
            now.Click += async delegate { await StartInstall(); };
            Closing += delegate { if (cts != null) cts.Cancel(); };
        }

        void SetBusy(bool on)
        {
            busy = on; skip.IsEnabled = later.IsEnabled = !on; now.IsEnabled = !on;
            bar.Visibility = status.Visibility = Visibility.Visible;
        }

        internal async System.Threading.Tasks.Task StartInstall()
        {
            if (busy) return;
            if (m.NotifyOnly)
            {
                try { Process.Start(new ProcessStartInfo(string.IsNullOrEmpty(m.PageUrl) ? UpdateSources.GithubPage : m.PageUrl) { UseShellExecute = true }); } catch { }
                return;
            }
            SetBusy(true); status.Foreground = Br("#7D889B"); status.Text = "正在准备…"; bar.Value = 0;
            cts = new CancellationTokenSource();
            string err;
            try
            {
                err = await UpdateService.InstallAsync(m, (s, p) => Dispatcher.BeginInvoke(new Action(() => { status.Text = s; bar.Value = Math.Max(0, Math.Min(100, p * 100)); })), cts.Token);
            }
            catch (Exception e) { err = e.Message; }
            if (err != null)
            {
                SetBusy(false);
                status.Foreground = Br("#C2410C"); status.Text = err; now.Content = "重试"; AutomationProperties.SetName(now, "重试");
                return;
            }
            bar.Value = 100; status.Text = "更新包已通过签名和完整性校验，ClearDesk 即将自动重启完成更新…";
            await System.Threading.Tasks.Task.Delay(900);
            Program.Quit();
        }
    }
}
