using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ClearDesk
{
    public class BoxData
    {
        public string Id = Guid.NewGuid().ToString("N"), Title = "桌面收纳";
        public double X = 20, Y = 20, Width = 520, Height = 360;
        public bool MaterialSelected = false;
        public bool Locked = false, Collapsed = false, List = false, Desktop = false, Enabled = true;
        public string Material = "liquid";
        public List<string> Items = new List<string>();
        public List<string> Excluded = new List<string>();
    }

    public class Settings
    {
        public bool Icons = true, AutoHide = false, RestoreOnInput = true, Taskbar = false, TaskbarFade = false, Animation = false, RestoreAnimation = true, Dock = true, Clock = true, Performance = true, Calendar = false, Weather = false, Almanac = false, Music = false, WeatherOnline = false, HideAtStart = false;
        public int ConfigVersion = 1;
        public List<string> LaunchpadItems = new List<string>();
        public int IdleSeconds = 20, DockSize = 48, AnimationMs = 400;
        public double DockOffset = 12;
        public string City = "上海";
        public string MusicSource = "auto";
        public List<BoxData> Boxes = new List<BoxData>();
        public List<string> Pins = new List<string>();
        public Dictionary<string, BoxData> Widgets = new Dictionary<string, BoxData>();
    }

    public static class Program
    {
        public static string Base = AppDomain.CurrentDomain.BaseDirectory;
        public static string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClearDesk");
        public static Settings S;
        public static Application App;
        public static DesktopServices Services;
        public static bool Preview = false, Quitting = false, Rendering = false;
        static LaunchpadWindow launchpad;
        static Mutex mutex;
        static Forms.NotifyIcon tray;
        static Window settings;
        static List<GlassWindow> windows = new List<GlassWindow>();
        static DispatcherTimer refresh;
        public static string Config
        {
            get
            {
                return Path.Combine(Data, "settings.json");
            }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            try
            {
                Run(args);
            }
            catch (Exception e)
            {
                try
                {
                    File.WriteAllText(Path.Combine(Base, "startup-error.txt"), e.ToString());
                }
                catch
                {
                }

                MessageBox.Show(e.Message, "ClearDesk 启动失败");
            }
        }

        static void Run(string[] args)
        {
            if (args.Length > 0 && args[0] == "--dock-fixture")
            {
                DockBehaviorTest.Fixture();
                return;
            }

            if (args.Length > 0 && args[0] == "--guard")
            {
                DesktopServices.Guard(args);
                return;
            }

            Rendering = args.Contains("--popup-benchmark") || args.Contains("--render") || args.Contains("--benchmark") || args.Contains("--regression-test");
            Preview = args.Contains("--preview") || Rendering || args.Contains("--integration-test");
            if (Preview)
                Data = Path.Combine(Base, "preview-data");
            if (args.Length > 0 && args[0] == "--self-test")
            {
                SelfTest();
                return;
            }

            bool created;
            mutex = new Mutex(true, "Local\\ClearDesk." + Environment.UserName + (args.Contains("--regression-test") ? ".Regression" : Preview ? ".Preview" : ""), out created);
            if (!created)
            {
                MessageBox.Show("ClearDesk 已在运行，请双击通知区域的图标打开设置。", "ClearDesk");
                return;
            }

            Directory.CreateDirectory(Data);
            Load();
            App = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            App.DispatcherUnhandledException += delegate (object sender, DispatcherUnhandledExceptionEventArgs e)
            {
                Log(e.Exception);
                e.Handled = true;
                MessageBox.Show("操作未完成：" + e.Exception.Message, "ClearDesk");
            };
            App.Exit += delegate
            {
                Quitting = true;
                if (Services != null)
                    Services.Dispose();
                if (tray != null)
                    tray.Dispose();
            };
            Resources();
            Services = new DesktopServices();
            SetupTray();
            Rebuild();
            if (!Preview)
            {
                Services.Apply();
                if (S.HideAtStart)
                    Services.SetIcons(false, false);
            }

            refresh = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            refresh.Tick += delegate
            {
                if (!Preview)
                    Services.Tick();
            };
            refresh.Start();
            if (args.Contains("--render"))
            {
                S.Calendar = true;
                S.Almanac = true;
                S.Weather = true;
                S.Music = true;
                S.WeatherOnline = false;
                Rebuild();
            }

            if (!args.Contains("--popup-benchmark") && !args.Contains("--startup") && !args.Contains("--benchmark") && !args.Contains("--integration-test") && !args.Contains("--regression-test"))
                ShowSettings();
            if (args.Contains("--popup-benchmark"))
                PopupBenchmark.Start();
            if (args.Contains("--regression-test"))
                RegressionTest.Start();
            if (args.Contains("--benchmark"))
                Benchmark.Start();
            if (args.Contains("--integration-test"))
                IntegrationTest.Start();
            if (args.Contains("--render"))
            {
                Launchpad();
                foreach (var dw in windows.OfType<DockWindow>())
                    dw.TestHover(.5);
                var rt = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(4)
                };
                rt.Tick += delegate
                {
                    rt.Stop();
                    try
                    {
                        string folder = Path.Combine(Base, "preview");
                        Directory.CreateDirectory(folder);
                        foreach (var win in windows.Cast<Window>().Concat(new[] { settings }).Where(w => w != null))
                        {
                            var element = (FrameworkElement)win.Content;
                            element.UpdateLayout();
                            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                            bitmap.Render(element);
                            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                            string fn = win is GlassWindow ? ((GlassWindow)win).ExportName : "settings";
                            using (var stream = File.Create(Path.Combine(folder, fn + ".png")))
                                encoder.Save(stream);
                        }

                        File.WriteAllText(Path.Combine(folder, "render-status.txt"), "Rendered all enabled widgets and settings successfully.");
                    }
                    catch (Exception e)
                    {
                        File.WriteAllText(Path.Combine(Base, "render-error.txt"), e.ToString());
                    }

                    Quit();
                };
                rt.Start();
            }

            App.Run();
            mutex.ReleaseMutex();
            mutex.Dispose();
        }

        static void Load()
        {
            S = new Settings();
            if (File.Exists(Config))
            {
                try
                {
                    S = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(Config));
                }
                catch (Exception e)
                {
                    Log(e);
                    try
                    {
                        File.Copy(Config, Config + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
                    }
                    catch
                    {
                    }
                }
            }

            if (S == null)
                S = new Settings();
            if (S.Boxes == null)
                S.Boxes = new List<BoxData>();
            if (S.Pins == null)
                S.Pins = new List<string>();
            if (S.Widgets == null)
                S.Widgets = new Dictionary<string, BoxData>();
            if (S.ConfigVersion < 2)
            {
                S.ConfigVersion = 2;
                S.Animation = false;
                S.TaskbarFade = false;
            }

            if (S.ConfigVersion < 3)
                S.ConfigVersion = 3;
            if (S.ConfigVersion < 4)
            {
                foreach (var box in S.Boxes)
                {
                    box.Collapsed = false;
                    box.Height = Math.Max(170, box.Height);
                }

                S.ConfigVersion = 4;
            }

            if (S.LaunchpadItems == null)
                S.LaunchpadItems = new List<string>();
            if (S.Boxes.Count == 0)
                S.Boxes.Add(new BoxData { Desktop = true });
            S.IdleSeconds = Math.Max(3, Math.Min(600, S.IdleSeconds));
            S.DockSize = Math.Max(32, Math.Min(72, S.DockSize));
            S.AnimationMs = Math.Max(150, Math.Min(900, S.AnimationMs));
            if (!File.Exists(Config))
            {
                S.Pins = DesktopItems().Where(p => Path.GetExtension(p).Equals(".lnk", StringComparison.OrdinalIgnoreCase) && !Path.GetFileNameWithoutExtension(p).Equals("Lume", StringComparison.OrdinalIgnoreCase)).Take(7).ToList();
                Save();
            }
        }

        public static void Save()
        {
            Directory.CreateDirectory(Data);
            string temp = Config + ".tmp";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(S));
            if (File.Exists(Config))
                File.Replace(temp, Config, Config + ".bak");
            else
                File.Move(temp, Config);
        }

        public static void Log(Exception e)
        {
            try
            {
                Directory.CreateDirectory(Data);
                File.AppendAllText(Path.Combine(Data, "errors.log"), DateTime.Now.ToString("s") + " " + e.GetType().Name + ": " + e.Message + Environment.NewLine);
            }
            catch
            {
            }
        }

        public static IEnumerable<string> DesktopItems()
        {
            var all = new List<string>();
            foreach (var d in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            }

            )
                try
                {
                    all.AddRange(Directory.GetFileSystemEntries(d).Where(p => !Path.GetFileName(p).StartsWith("~$") && !Path.GetFileName(p).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) && (File.GetAttributes(p) & (FileAttributes.Hidden | FileAttributes.System)) == 0));
                }
                catch
                {
                }

            return all.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => Path.GetFileName(p));
        }

        public static string DisplayName(string p)
        {
            string ext = Path.GetExtension(p);
            return ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(p) : Path.GetFileName(p);
        }

        public static void OpenItem(string p)
        {
            try
            {
                if (!p.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase) && !File.Exists(p) && !Directory.Exists(p))
                {
                    MessageBox.Show("原文件不存在或已移动：\n" + p, "ClearDesk");
                    return;
                }

                Process.Start(new ProcessStartInfo(p) { UseShellExecute = true, WorkingDirectory = p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? Base : Directory.Exists(p) ? p : Path.GetDirectoryName(p) });
            }
            catch (Exception e)
            {
                MessageBox.Show("无法打开：" + e.Message, "ClearDesk");
            }
        }

        static void SetupTray()
        {
            tray = new Forms.NotifyIcon
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Path.Combine(Base, "ClearDesk.exe")),
                Text = "ClearDesk · 你的清透桌面",
                Visible = true
            };
            var m = new Forms.ContextMenuStrip();
            m.Items.Add("打开设置", null, delegate
            {
                App.Dispatcher.Invoke(new Action(ShowSettings));
            });
            m.Items.Add("显示 / 隐藏桌面图标", null, delegate
            {
                App.Dispatcher.Invoke(new Action(delegate
                {
                    Services.ToggleIcons();
                }));
            });
            m.Items.Add("恢复桌面图标", null, delegate
            {
                App.Dispatcher.Invoke(new Action(delegate
                {
                    Services.SetIcons(true, false);
                }));
            });
            m.Items.Add("退出并恢复桌面", null, delegate
            {
                App.Dispatcher.Invoke(new Action(Quit));
            });
            tray.ContextMenuStrip = m;
            tray.DoubleClick += delegate
            {
                App.Dispatcher.Invoke(new Action(ShowSettings));
            };
        }

        public static void Quit()
        {
            Quitting = true;
            Save();
            foreach (var w in windows.ToArray())
                w.Close();
            App.Shutdown();
        }

        public static void Rebuild()
        {
            foreach (var w in windows.ToArray())
                w.Close();
            windows.Clear();
            foreach (var b in S.Boxes.Where(b => b.Enabled))
                AddWindow(new OrganizerWindow(b));
            if (S.Clock)
                AddWindow(new ClockWindow());
            if (S.Performance)
                AddWindow(new PerformanceWindow());
            if (S.Dock)
                AddWindow(new DockWindow());
            if (S.Calendar)
                AddWindow(new CalendarWindow(false));
            if (S.Almanac)
                AddWindow(new CalendarWindow(true));
            if (S.Weather)
                AddWindow(new WeatherWindow());
            if (S.Music)
                AddWindow(new MusicWindow());
        }

        public static void AddWindow(GlassWindow w)
        {
            windows.Add(w);
            w.Closed += delegate
            {
                windows.Remove(w);
            };
            w.Show();
        }

        public static void NewBox()
        {
            var b = new BoxData
            {
                Title = "新的收纳格",
                X = 70 + S.Boxes.Count * 25,
                Y = 100 + S.Boxes.Count * 25,
                Width = 420,
                Height = 300
            };
            S.Boxes.Add(b);
            Save();
            AddWindow(new OrganizerWindow(b));
        }

        public static BoxData Widget(string id, double x, double y, double width, double height)
        {
            BoxData b;
            if (!S.Widgets.TryGetValue(id, out b))
            {
                b = new BoxData
                {
                    Id = id,
                    Title = id,
                    X = x,
                    Y = y,
                    Width = width,
                    Height = height
                };
                S.Widgets[id] = b;
            }

            return b;
        }

        public static void RefreshDock()
        {
            foreach (var w in windows.OfType<DockWindow>().ToArray())
                w.Rebuild();
        }

        public static void Launchpad()
        {
            if (launchpad != null && !launchpad.WasClosed)
            {
                launchpad.Dismiss();
                return;
            }

            launchpad = new LaunchpadWindow();
            var w = launchpad;
            w.Closed += delegate
            {
                if (launchpad == w)
                    launchpad = null;
            };
            AddWindow(w);
            w.Activate();
        }

        public static void Resources()
        {
            string x = @"<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
 <Style TargetType='Button'><Setter Property='Foreground' Value='#F4F6FC'/><Setter Property='Background' Value='#263346'/><Setter Property='BorderThickness' Value='0'/><Setter Property='Padding' Value='14,8'/><Setter Property='Cursor' Value='Hand'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'><Border x:Name='B' CornerRadius='9' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='B' Property='Background' Value='#46556A'/></Trigger><Trigger Property='IsPressed' Value='True'><Setter TargetName='B' Property='Opacity' Value='.65'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
 <Style TargetType='ProgressBar'><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ProgressBar'><Border Background='{TemplateBinding Background}' CornerRadius='2'><Grid x:Name='PART_Track' ClipToBounds='True'><Border x:Name='PART_Indicator' Background='{TemplateBinding Foreground}' HorizontalAlignment='Left' CornerRadius='2'/></Grid></Border></ControlTemplate></Setter.Value></Setter></Style>
 <Style TargetType='CheckBox'><Setter Property='Foreground' Value='#F2F5FA'/><Setter Property='Margin' Value='0,10'/><Setter Property='FontSize' Value='14'/></Style>
 <Style TargetType='TextBox'><Setter Property='Background' Value='#26313E'/><Setter Property='Foreground' Value='White'/><Setter Property='CaretBrush' Value='White'/><Setter Property='Padding' Value='8'/><Setter Property='BorderBrush' Value='#485568'/></Style>
 <Style TargetType='ContextMenu'><Setter Property='Background' Value='#242832'/><Setter Property='Foreground' Value='White'/><Setter Property='BorderBrush' Value='#4A5361'/><Setter Property='Padding' Value='5'/></Style>
 <Style TargetType='MenuItem'><Setter Property='Foreground' Value='White'/><Setter Property='Padding' Value='12,7'/></Style>
 </ResourceDictionary>";
            App.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(x);
        }

        public static TextBlock Text(string s, double size = 14, string color = "#F4F6FC")
        {
            return new TextBlock
            {
                Text = s,
                FontSize = size,
                Foreground = (Brush)new BrushConverter().ConvertFromString(color),
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Microsoft YaHei UI")
            };
        }

        public static Button Button(string s, Action action)
        {
            var b = new Button
            {
                Content = s,
                Margin = new Thickness(0, 4, 8, 4)
            };
            b.Click += delegate
            {
                action();
            };
            return b;
        }

        static void Check(Panel p, string title, bool value, Action<bool> apply)
        {
            var c = new CheckBox
            {
                Content = title,
                IsChecked = value
            };
            c.Click += delegate
            {
                apply(c.IsChecked == true);
                Save();
            };
            p.Children.Add(c);
        }

        static Border Card(string title, Panel body)
        {
            var s = new StackPanel();
            s.Children.Add(Text(title, 18));
            s.Children.Add(body);
            return new Border
            {
                CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush(Color.FromRgb(25, 32, 43)),
                Padding = new Thickness(20),
                Margin = new Thickness(0, 0, 0, 14),
                Child = s
            };
        }

        static void Head(Panel p, string t)
        {
            var b = Text(t, 12, "#9AAFC5");
            b.Margin = new Thickness(0, 8, 0, 8);
            p.Children.Add(b);
        }

        public static bool Startup()
        {
            if (Preview)
                return false;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run"))
                {
                    return k != null && k.GetValue("ClearDesk") != null;
                }
            }
            catch (Exception e)
            {
                Log(e);
                return false;
            }
        }

        static void SetStartup(bool v)
        {
            if (Preview)
                return;
            using (var k = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run"))
            {
                if (v)
                    k.SetValue("ClearDesk", "\"" + Path.Combine(Base, "ClearDesk.exe") + "\" --startup");
                else
                    k.DeleteValue("ClearDesk", false);
            }
        }

        public static void ShowSettings()
        {
            if (settings != null)
            {
                settings.Show();
                settings.Activate();
                return;
            }

            settings = new Window
            {
                Icon = AppBrand.Icon,
                Title = "ClearDesk · 桌面美化",
                Width = 860,
                Height = 790,
                MinWidth = 680,
                MinHeight = 480,
                Background = new SolidColorBrush(Color.FromRgb(13, 18, 26)),
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                FontFamily = new FontFamily("Microsoft YaHei UI")
            };
            var root = new DockPanel
            {
                Margin = new Thickness(30)
            };
            var title = new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 24)
            };
            title.Children.Add(Text("ClearDesk", 32));
            Head(title, "清透桌面  /  个人精简版 1.6.0");
            title.Children.Add(Text("把常用的留在桌面，让其余的安静下来。", 14, "#B7C7DA"));
            DockPanel.SetDock(title, Dock.Top);
            root.Children.Add(title);
            var bottom = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 12, 0, 0)
            };
            bottom.Children.Add(Button("应用外观", delegate
            {
                Save();
                Rebuild();
                if (!Preview)
                    Services.Apply();
            }));
            bottom.Children.Add(Button("恢复桌面图标", delegate
            {
                Services.SetIcons(true, false);
            }));
            bottom.Children.Add(Button("收起到托盘", delegate
            {
                settings.Hide();
            }));
            bottom.Children.Add(Button("退出并恢复", Quit));
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);
            var body = new StackPanel();
            root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var a = new StackPanel();
            Check(a, "登录 Windows 后自动启动", Startup(), SetStartup);
            Check(a, "双击桌面空白处，隐藏 / 显示原生图标", S.Icons, delegate (bool v)
            {
                S.Icons = v;
                if (!Preview)
                    Services.Apply();
            });
            Check(a, "程序启动时隐藏原生图标（退出时恢复）", S.HideAtStart, delegate (bool v)
            {
                S.HideAtStart = v;
            });
            Check(a, "空闲时自动隐藏原生图标", S.AutoHide, delegate (bool v)
            {
                S.AutoHide = v;
            });
            Check(a, "输入时恢复被自动隐藏的图标", S.RestoreOnInput, delegate (bool v)
            {
                S.RestoreOnInput = v;
            });
            var idle = new Slider
            {
                Minimum = 3,
                Maximum = 120,
                Value = S.IdleSeconds,
                TickFrequency = 1,
                IsSnapToTickEnabled = true
            };
            var il = Text("自动隐藏延迟：" + S.IdleSeconds + " 秒", 12);
            idle.ValueChanged += delegate
            {
                S.IdleSeconds = (int)idle.Value;
                il.Text = "自动隐藏延迟：" + S.IdleSeconds + " 秒";
                Save();
            };
            a.Children.Add(il);
            a.Children.Add(idle);
            body.Children.Add(Card("桌面与启动", a));
            var b = new StackPanel();
            Check(b, "任务栏背景完全透明（TranslucentTB 官方组件）", S.Taskbar, delegate (bool v)
            {
                S.Taskbar = v;
                if (!Preview)
                    Services.Apply();
            });
            Head(b, "保留任务栏图标的不透明度；Windows 11 使用官方签名的独立组件。退出时恢复。");
            Check(b, "macOS 风格缩小动画 · 实验性", S.Animation, delegate (bool v)
            {
                S.Animation = v;
                if (!Preview)
                    Services.Apply();
            });
            Check(b, "还原窗口时播放反向动画", S.RestoreAnimation, delegate (bool v)
            {
                S.RestoreAnimation = v;
            });
            Head(b, "仅最小化事件发生时尝试捕获画面，不再周期性截取前台窗口。\n实验功能默认关闭；部分窗口可能不支持，系统原生动画不受影响。");
            body.Children.Add(Card("任务栏与窗口", b));
            var c = new StackPanel();
            Check(c, "底部 Dock / 启动台", S.Dock, delegate (bool v)
            {
                S.Dock = v;
            });
            Check(c, "时钟", S.Clock, delegate (bool v)
            {
                S.Clock = v;
            });
            Check(c, "性能（CPU、GPU、内存、网络）", S.Performance, delegate (bool v)
            {
                S.Performance = v;
            });
            Check(c, "日历", S.Calendar, delegate (bool v)
            {
                S.Calendar = v;
            });
            Check(c, "黄历 / 农历", S.Almanac, delegate (bool v)
            {
                S.Almanac = v;
            });
            Check(c, "天气", S.Weather, delegate (bool v)
            {
                S.Weather = v;
            });
            Check(c, "音乐控制与本地播放", S.Music, delegate (bool v)
            {
                S.Music = v;
            });
            Head(c, "勾选后点击“应用外观”。组件右键可切换材质、锁定、恢复位置或关闭。");
            var bs = new StackPanel
            {
                Orientation = Orientation.Horizontal
            };
            bs.Children.Add(Button("新建收纳格", NewBox));
            bs.Children.Add(Button("重新显示桌面收纳", delegate
            {
                foreach (var bx in S.Boxes)
                {
                    bx.Enabled = true;
                    bx.Collapsed = false;
                    bx.Height = Math.Max(170, bx.Height);
                }

                Save();
                Rebuild();
            }));
            bs.Children.Add(Button("启动台", Launchpad));
            c.Children.Add(bs);
            var sz = new Slider
            {
                Minimum = 32,
                Maximum = 72,
                Value = S.DockSize,
                TickFrequency = 1,
                IsSnapToTickEnabled = true
            };
            Head(c, "Dock 图标大小");
            sz.ValueChanged += delegate
            {
                S.DockSize = (int)sz.Value;
            };
            c.Children.Add(sz);
            body.Children.Add(Card("组件与 Dock", c));
            var d = new StackPanel();
            Check(d, "允许天气联网（仅发送所选城市坐标）", S.WeatherOnline, delegate (bool v)
            {
                S.WeatherOnline = v;
            });
            var cities = new ComboBox
            {
                ItemsSource = WeatherWindow.Cities.Keys.ToList(),
                SelectedItem = S.City,
                Margin = new Thickness(0, 10, 0, 10),
                Padding = new Thickness(8)
            };
            cities.SelectionChanged += delegate
            {
                if (cities.SelectedItem != null)
                {
                    S.City = (string)cities.SelectedItem;
                    Save();
                }
            };
            d.Children.Add(cities);
            Head(d, "天气由 Open-Meteo 提供；不开启时不发起天气请求。无自动定位、账户、广告或遥测。");
            body.Children.Add(Card("天气与隐私", d));
            var e = new StackPanel();
            Head(e, "拖动组件标题可移动；拖入文件添加引用，不移动或删除原文件。\n收纳格支持搜索、网格 / 列表、收缩模式和多格子。\n实时透明材质透出后方窗口；收纳区可拖边缩放、滚轮平滑滚动。\n设置保存在 %LOCALAPPDATA%\\ClearDesk；程序无需管理员权限。");
            e.Children.Add(Button("打开配置目录", delegate
            {
                Process.Start(new ProcessStartInfo(Data) { UseShellExecute = true });
            }));
            body.Children.Add(Card("使用说明", e));
            settings.Content = root;
            settings.Closed += delegate
            {
                settings = null;
            };
            settings.Show();
        }

        static void SelfTest()
        {
            var results = new List<string>();
            try
            {
                var s = new Settings();
                s.Boxes.Add(new BoxData { Title = "测试中文", Items = new List<string> { "C:\\a b\\x.lnk" } });
                string j = new JavaScriptSerializer().Serialize(s);
                var t = new JavaScriptSerializer().Deserialize<Settings>(j);
                if (t.Boxes[0].Title != "测试中文" || t.Boxes[0].Items[0] != s.Boxes[0].Items[0])
                    throw new Exception("Config round-trip");
                results.Add("PASS: Chinese/path config round-trip");
                MusicTests.Rules(delegate(bool ok, string label) { if (!ok) throw new Exception(label); results.Add("PASS: " + label); });
                var d = new DateTime(2026, 10, 19);
                string lunar = CalendarWindow.Lunar(d);
                if (string.IsNullOrEmpty(lunar))
                    throw new Exception("Lunar");
                results.Add("PASS: lunar conversion " + lunar);
                if (WeatherWindow.Description(95) != "雷雨")
                    throw new Exception("Weather mapping");
                results.Add("PASS: weather code mapping");
                if (new Lunar.Solar(2026, 2, 17).Lunar.DayInChinese != "初一")
                    throw new Exception("Lunar New Year reference");
                results.Add("PASS: known Lunar New Year reference (2026-02-17)");
                Native.MouseProc callback = delegate (int code, IntPtr w, IntPtr l)
                {
                    return Native.CallNextHookEx(IntPtr.Zero, code, w, l);
                };
                IntPtr mh = Native.SetWindowsHookEx(14, callback, Native.GetModuleHandle(null), 0);
                results.Add(mh == IntPtr.Zero ? "LIMITATION: mouse hook registration denied in test environment" : "PASS: mouse hook registered and released");
                if (mh != IntPtr.Zero)
                    Native.UnhookWindowsHookEx(mh);
                Native.EventProc ec = delegate (IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time)
                {
                };
                IntPtr eh = Native.SetWinEventHook(0x16, 0x17, IntPtr.Zero, ec, 0, 0, 2);
                results.Add(eh == IntPtr.Zero ? "LIMITATION: window event hook registration denied" : "PASS: window event hook registered and released");
                if (eh != IntPtr.Zero)
                    Native.UnhookWinEvent(eh);
                GC.KeepAlive(callback);
                GC.KeepAlive(ec);
                if (Native.Icons() == IntPtr.Zero)
                    results.Add("INFO: Explorer desktop not available");
                else
                    results.Add("PASS: Explorer desktop discovery (read-only)");
                results.Add("PASS: " + Native.Taskbars().Count + " taskbars discovered (read-only)");
                results.Add("PASS: " + DesktopItems().Count() + " desktop references enumerated (read-only)");
            }
            catch (Exception e)
            {
                results.Add("FAIL: " + e);
                Environment.ExitCode = 1;
            }

            File.WriteAllLines(Path.Combine(Base, "self-test.txt"), results);
        }
    }
}
