using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ClearDesk
{
    public sealed partial class MusicWindow : GlassWindow
    {
        readonly IMusicSessions sessions;
        readonly MediaPlayer local = new MediaPlayer();
        readonly Button source = new Button { Height = 25, Padding = new Thickness(10, 2, 10, 2), FontSize = 11 };
        readonly ContextMenu sourceMenu = new ContextMenu();
        readonly TextBlock song = Program.Text("暂无播放", 15);
        readonly TextBlock artist = Program.Text("", 11);
        readonly TextBlock status = Program.Text("", 10);
        readonly TextBlock elapsed = Program.Text("--:--", 10), total = Program.Text("--:--", 10);
        readonly Image artwork = new Image { Width = 52, Height = 52, Stretch = Stretch.UniformToFill };
        readonly TextBlock placeholder = Program.Text("♫", 28);
        // Clickable / draggable timeline. IsMoveToPointEnabled makes a click jump straight to that point.
        readonly SeekBar progress = new SeekBar { Margin = new Thickness(0, 8, 0, 2), ToolTip = "点击或拖动以跳转" };
        bool seeking;              // user is pressing / dragging the timeline
        DateTime seekHoldUntil;    // keep the requested position until the player reports it
        double seekTarget = -1;
        internal Task TestSeek(double ratio) { seeking = true; progress.Value = ratio; return CommitSeek(); }
        readonly Slider volume = new Slider { Minimum = 0, Maximum = 1, Value = .5, Width = 110, ToolTip = "本地音乐音量" };
        readonly StackPanel localTools = new StackPanel { Orientation = Orientation.Horizontal };
        readonly Button previous, toggle, next;
        readonly string[] sources = { "auto", "qq", "netease", "kugou", "local" };
        List<string> playlist = new List<string>();
        MusicSnapshot snapshot = new MusicSnapshot();
        int current = -1, revision;
        bool closed, busy, commandBusy, playing;
        string selectedSource, localError;
        DateTime retryAfter;
        internal string TestTitle { get { return song.Text; } }
        internal string TestStatus { get { return status.Text; } }
        internal bool TestCanNext { get { return next.IsEnabled; } }
        internal bool TestHasCover { get { return artwork.Source != null; } }
        internal void TestSelect(string value) { SelectSource(value); }
        internal void TestRefresh() { retryAfter = DateTime.MinValue; Refresh(); }
        internal Task TestCommand(MusicCommand command) { return Command(command); }

        public MusicWindow() : this(new MusicSessions()) { }

        internal MusicWindow(IMusicSessions backend, ILyricsProvider lyricProvider=null) : base(Program.Widget("music", 20, 430, 340, 232), "音乐")
        {
            sessions = backend;
            lyrics = lyricProvider ?? new LyricsProvider();
            if(Program.S.MusicLyricFiles==null) Program.S.MusicLyricFiles=new Dictionary<string,string>();
            Root.Children.Insert(Root.Children.IndexOf(Layout), new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(82, 10, 12, 18)),
                CornerRadius = new CornerRadius(22),
                IsHitTestVisible = false
            });
            Width = State.Width = Math.Max(320, Width);
            Layout.Margin = new Thickness(18, 14, 18, 12);
            Header.Margin = new Thickness(0, 0, 0, 14);
            Heading.FontSize = 11;
            Heading.Opacity = .65;
            Heading.VerticalAlignment = VerticalAlignment.Center;
            var panel = new StackPanel();
            selectedSource = MusicSources.Normalize(Program.S.MusicSource);
            source.Template = RoundedButtonTemplate(13);
            source.Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
            source.ToolTip = "选择音乐播放器";
            System.Windows.Automation.AutomationProperties.SetName(source, "选择音乐播放器");
            sourceMenu.PlacementTarget = source;
            sourceMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            sourceMenu.MinWidth = 192;
            sourceMenu.Background = new SolidColorBrush(Color.FromArgb(248, 31, 33, 40));
            sourceMenu.BorderBrush = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255));
            sourceMenu.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ContextMenu'><Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='14' Padding='6'><StackPanel IsItemsHost='True'/></Border></ControlTemplate>");
            foreach (string key in sources)
            {
                string value = key;
                var item = new MenuItem { Header = key == "auto" ? "自动选择播放器" : MusicSources.Name(key), Tag = key, IsCheckable = true, Height = 34, Padding = new Thickness(10, 5, 10, 5) };
                item.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                    "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='MenuItem'><Border x:Name='Row' CornerRadius='8' Padding='{TemplateBinding Padding}' Background='Transparent'><Grid><Grid.ColumnDefinitions><ColumnDefinition Width='22'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions><TextBlock x:Name='Mark' Text='✓' Opacity='0' VerticalAlignment='Center' Foreground='White'/><ContentPresenter Grid.Column='1' ContentSource='Header' VerticalAlignment='Center'/></Grid></Border><ControlTemplate.Triggers><Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Row' Property='Background' Value='#28FFFFFF'/></Trigger><Trigger Property='IsChecked' Value='True'><Setter TargetName='Mark' Property='Opacity' Value='1'/><Setter TargetName='Row' Property='Background' Value='#18FFFFFF'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");
                item.Click += delegate { SelectSource(value); };
                sourceMenu.Items.Add(item);
            }
            source.Click += delegate
            {
                foreach (MenuItem item in sourceMenu.Items) item.IsChecked = (string)item.Tag == selectedSource;
                sourceMenu.IsOpen = true;
            };
            DockPanel.SetDock(source, Dock.Right);
            Header.Children.Insert(0, source);
            var track = new Grid();
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            track.ColumnDefinitions.Add(new ColumnDefinition());
            var cover = new Grid { Width = 52, Height = 52, Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Left, Clip = new RectangleGeometry(new Rect(0, 0, 52, 52), 10, 10) };
            placeholder.HorizontalAlignment = HorizontalAlignment.Center;
            placeholder.VerticalAlignment = VerticalAlignment.Center;
            cover.Children.Add(placeholder);
            cover.Children.Add(artwork);
            track.Children.Add(cover);
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            song.TextTrimming = TextTrimming.CharacterEllipsis;
            song.FontWeight = FontWeights.SemiBold;
            artist.TextTrimming = TextTrimming.CharacterEllipsis;
            artist.Margin = new Thickness(0, 5, 0, 0);
            artist.Opacity = .75;
            labels.Children.Add(song);
            labels.Children.Add(artist);
            Grid.SetColumn(labels, 1);
            track.Children.Add(labels);
            panel.Children.Add(track);
            panel.Children.Add(progress);
            progress.PreviewMouseLeftButtonDown += delegate { seeking = true; };
            progress.PreviewMouseLeftButtonUp += async delegate { await CommitSeek(); };
            progress.LostMouseCapture += async delegate { if (seeking) await CommitSeek(); };
            progress.ValueChanged += delegate
            {
                if (!seeking) return;
                var d = CurrentDuration();
                if (d > TimeSpan.Zero) elapsed.Text = MusicSources.Time(TimeSpan.FromSeconds(progress.Value * d.TotalSeconds));
            };
            var timeline = new DockPanel { Opacity = .60 };
            total.HorizontalAlignment = HorizontalAlignment.Right;
            DockPanel.SetDock(total, Dock.Right);
            timeline.Children.Add(total);
            timeline.Children.Add(elapsed);
            panel.Children.Add(timeline);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) };
            previous = Transport("上一首", "M 2,2 L 2,18 L 5,18 L 5,2 Z M 18,2 L 6,10 L 18,18 Z", MusicCommand.Previous);
            toggle = Transport("播放", "M 5,2 L 18,10 L 5,18 Z", MusicCommand.Toggle);
            toggle.Background = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255));
            next = Transport("下一首", "M 15,2 L 15,18 L 18,18 L 18,2 Z M 2,2 L 14,10 L 2,18 Z", MusicCommand.Next);
            buttons.Children.Add(previous);
            buttons.Children.Add(toggle);
            buttons.Children.Add(next);
            panel.Children.Add(buttons);
            localTools.Children.Add(Program.Button("选择本地音乐", delegate { ChooseLocal(); }));
            localTools.Children.Add(volume);
            volume.ValueChanged += delegate { local.Volume = volume.Value; };
            panel.Children.Add(localTools);
            status.TextWrapping = TextWrapping.Wrap;
            status.Opacity = .75;
            status.MaxHeight = 32;
            status.Margin = new Thickness(0, 6, 0, 0);
            panel.Children.Add(status);
            SetupLyrics(panel);
            Body.Children.Add(panel);
            local.MediaEnded += delegate { if (selectedSource == "local" && playlist.Count > 0) Play((current + 1) % playlist.Count); };
            local.MediaFailed += delegate { playing = false; localError = "无法播放此格式，请使用 MP3 / WAV"; if (!closed) Render(); };
            local.MediaOpened += delegate { if (!closed) Render(); };
            Timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(750) };
            Timer.Tick += delegate { if (IsVisible && !State.Collapsed) Refresh(); };
            Loaded += delegate { Timer.Start(); Refresh(); };
            Closed += delegate
            {
                closed = true;
                revision++;
                CancelLyrics();
                Timer.Stop();
                sourceMenu.IsOpen = false;
                sessions.Dispose();
                local.Close();
            };
            Render();
        }

        static ControlTemplate RoundedButtonTemplate(int radius)
        {
            return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'><Border x:Name='Surface' CornerRadius='" + radius + "' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='Background' Value='#26FFFFFF'/></Trigger><Trigger Property='IsPressed' Value='True'><Setter TargetName='Surface' Property='Opacity' Value='.55'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter TargetName='Surface' Property='Opacity' Value='.3'/></Trigger><Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Surface' Property='Background' Value='#30FFFFFF'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");
        }

        static System.Windows.Shapes.Path Glyph(string data)
        {
            return new System.Windows.Shapes.Path { Data = Geometry.Parse(data), Width = 17, Height = 17, Fill = Brushes.White, Stretch = Stretch.Uniform };
        }

        Button Transport(string name, string geometry, MusicCommand command)
        {
            var button = new Button { Width = 38, Height = 38, Margin = new Thickness(10, 0, 10, 0), Padding = new Thickness(10), Background = Brushes.Transparent, Content = Glyph(geometry), ToolTip = name, Template = RoundedButtonTemplate(19) };
            System.Windows.Automation.AutomationProperties.SetName(button, name);
            button.Click += async delegate { await Command(command); };
            return button;
        }

        void SelectSource(string value)
        {
            selectedSource = MusicSources.Normalize(value);
            Program.S.MusicSource = selectedSource;
            Program.Save();
            revision++;
            retryAfter = DateTime.MinValue;
            snapshot = new MusicSnapshot();
            if (selectedSource != "local") { local.Pause(); playing = false; }
            Render();
            Refresh();
        }

        async void Refresh()
        {
            if (closed) return;
            if (selectedSource == "local") { Render(); return; }
            if (busy || commandBusy || DateTime.UtcNow < retryAfter) return;
            busy = true;
            int generation = revision;
            string requested = selectedSource;
            try
            {
                var result = await sessions.ReadAsync(requested);
                if (closed || generation != revision) return;
                snapshot = result;
                retryAfter = result.Key == null ? DateTime.UtcNow.AddSeconds(2) : DateTime.MinValue;
                Render();
            }
            catch
            {
                if (!closed && generation == revision)
                {
                    snapshot = new MusicSnapshot { Message = "读取失败，稍后自动重试" };
                    retryAfter = DateTime.UtcNow.AddSeconds(3);
                    Render();
                }
            }
            finally { busy = false; }
        }

        async Task Command(MusicCommand command)
        {
            if (commandBusy || closed) return;
            if (selectedSource == "local")
            {
                if (playlist.Count == 0) return;
                if (command == MusicCommand.Previous) Play((current - 1 + playlist.Count) % playlist.Count);
                else if (command == MusicCommand.Next) Play((current + 1) % playlist.Count);
                else { if (playing) local.Pause(); else local.Play(); playing = !playing; }
                Render();
                return;
            }
            int generation = revision;
            string targetKey = snapshot.Key;
            commandBusy = true;
            previous.IsEnabled = toggle.IsEnabled = next.IsEnabled = false;
            bool succeeded = false;
            try
            {
                // Do not drop clicks that arrive during a metadata/cover refresh.
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (busy && !closed && generation == revision && DateTime.UtcNow < deadline)
                    await Task.Delay(25);
                if (!busy && !closed && generation == revision)
                    succeeded = await sessions.CommandAsync(targetKey, command);
            }
            catch { }
            finally
            {
                commandBusy = false;
                if (!closed && generation == revision)
                {
                    if (!succeeded)
                    {
                        snapshot.Message = "播放器未接受此操作，请检查客户端的系统媒体控制设置。";
                        retryAfter = DateTime.UtcNow.AddSeconds(3);
                    }
                    Render();
                }
            }
            if (succeeded && !closed && generation == revision) Refresh();
        }

        TimeSpan CurrentDuration()
        {
            if (selectedSource == "local") return local.NaturalDuration.HasTimeSpan ? local.NaturalDuration.TimeSpan : TimeSpan.Zero;
            return snapshot.Duration;
        }

        async Task CommitSeek()
        {
            if (!seeking) return;
            seeking = false;
            var duration = CurrentDuration();
            if (closed || duration <= TimeSpan.Zero) { Render(); return; }
            double ratio = Math.Max(0, Math.Min(1, progress.Value));
            var position = TimeSpan.FromSeconds(ratio * duration.TotalSeconds);
            seekTarget = ratio;
            seekHoldUntil = DateTime.UtcNow.AddSeconds(2.5);
            if (selectedSource == "local") { local.Position = position; seekTarget = -1; Render(); return; }
            bool ok = false;
            int generation = revision;
            commandBusy = true;
            try
            {
                // Wait for an in-flight poll so the compatibility worker is free.
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (busy && !closed && generation == revision && DateTime.UtcNow < deadline) await Task.Delay(25);
                if (!busy && !closed && generation == revision) ok = await sessions.SeekAsync(snapshot.Key, position);
            }
            catch { }
            finally { commandBusy = false; }
            if (closed || generation != revision) return;
            if (ok) { snapshot.Position = position; Render(); }
            else
            {
                seekTarget = -1; seekHoldUntil = DateTime.MinValue;
                snapshot.Message = snapshot.Source == "netease" && snapshot.Key != null && snapshot.Key.StartsWith("netease-ui:")
                    ? "网易云未接受跳转：请确认网易云已用同步模式打开（右键菜单），或在客户端内拖动进度条。"
                    : snapshot.Source == "qq"
                    ? "QQ 音乐未接受跳转：请确认 QQ 音乐主窗口没有被关闭到托盘后重试。"
                    : "播放器不支持从外部调整进度。";
                retryAfter = DateTime.UtcNow.AddSeconds(3);
                Render();
            }
        }

        void Render()
        {
            bool isLocal = selectedSource == "local";
            var view = snapshot;
            if (isLocal)
            {
                bool loaded = current >= 0 && current < playlist.Count;
                var duration = local.NaturalDuration.HasTimeSpan ? local.NaturalDuration.TimeSpan : TimeSpan.Zero;
                view = new MusicSnapshot
                {
                    Title = loaded ? Path.GetFileNameWithoutExtension(playlist[current]) : "",
                    LocalPath=loaded?playlist[current]:null,
                    Artist = "本地音乐", Playing = playing, CanToggle = loaded && string.IsNullOrEmpty(localError),
                    CanNext = playlist.Count > 1, CanPrevious = playlist.Count > 1,
                    Position = MusicSources.ClampPosition(local.Position, duration), Duration = duration,
                    Message = localError ?? (loaded ? "" : "选择 MP3、WAV 等本地文件开始播放")
                };
            }
            song.Text = string.IsNullOrWhiteSpace(view.Title) ? "暂无播放" : view.Title;
            song.ToolTip = view.Title;
            artist.Text = string.IsNullOrWhiteSpace(view.Artist) ? MusicSources.Name(view.Source ?? selectedSource) : view.Artist;
            artist.ToolTip = artist.Text;
            artwork.Source = view.Cover;
            placeholder.Visibility = view.Cover == null ? Visibility.Visible : Visibility.Collapsed;
            double reported = view.Duration > TimeSpan.Zero ? view.Position.TotalSeconds / view.Duration.TotalSeconds : 0;
            // While seeking (or shortly after) polling must not snap the thumb back to the old position.
            if (seekTarget >= 0 && (DateTime.UtcNow >= seekHoldUntil || Math.Abs(reported - seekTarget) * Math.Max(1, view.Duration.TotalSeconds) < 2)) seekTarget = -1;
            progress.IsEnabled = view.Duration > TimeSpan.Zero && (isLocal || view.CanSeek);
            if (!seeking)
            {
                double shown = seekTarget >= 0 ? seekTarget : reported;
                progress.Value = shown;
                elapsed.Text = view.Duration > TimeSpan.Zero ? MusicSources.Time(TimeSpan.FromSeconds(shown * view.Duration.TotalSeconds)) : "--:--";
            }
            total.Text = view.Duration > TimeSpan.Zero ? MusicSources.Time(view.Duration) : "--:--";
            string action = view.Playing ? "暂停" : "播放";
            if (!Equals(toggle.ToolTip, action))
            {
                toggle.Content = Glyph(view.Playing ? "M 3,2 L 8,2 L 8,18 L 3,18 Z M 12,2 L 17,2 L 17,18 L 12,18 Z" : "M 5,2 L 18,10 L 5,18 Z");
                toggle.ToolTip = action;
                System.Windows.Automation.AutomationProperties.SetName(toggle, action);
            }
            previous.IsEnabled = view.CanPrevious && !commandBusy;
            toggle.IsEnabled = view.CanToggle && !commandBusy;
            next.IsEnabled = view.CanNext && !commandBusy;
            status.Text = view.Message;
            status.Visibility = string.IsNullOrEmpty(view.Message) ? Visibility.Collapsed : Visibility.Visible;
            localTools.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;
            source.Content = (selectedSource == "auto" ? "自动选择" : MusicSources.Name(selectedSource)) + "  ⌄";
            RenderLyrics(view);
            if(Program.S.MusicLyrics && string.IsNullOrWhiteSpace(view.Title)) status.Visibility=Visibility.Collapsed;
            double height = (isLocal ? 294 : status.Visibility==Visibility.Collapsed ? 214 : 252) + (Program.S.MusicLyrics?136:0);
            State.Height = height;
            if (!State.Collapsed && Height != height) Height = height;
        }

        void ChooseLocal()
        {
            var dialog = new OpenFileDialog { Filter = "音乐|*.mp3;*.wav;*.wma;*.m4a;*.aac|所有文件|*.*", Multiselect = true };
            if (dialog.ShowDialog(this) != true) return;
            playlist = dialog.FileNames.ToList();
            Play(0);
        }

        void Play(int index)
        {
            if (index < 0 || index >= playlist.Count) return;
            current = index;
            localError = null;
            local.Open(new Uri(playlist[index]));
            local.Volume = volume.Value;
            local.Play();
            playing = true;
            Render();
        }
    }
}
