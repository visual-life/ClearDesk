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
        public string MusicSource = "auto";
        public bool MusicLyrics = false;
        public Dictionary<string,string> MusicLyricFiles = new Dictionary<string,string>();
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
            if(args.Length>0 && args[0]=="--music-diag") { MusicDiag.Run(args.Length>1?args[1]:"music-diag.txt"); return; }
            if(args.Contains("--client-lyrics-test")) { ClientLyricsTests.Live(); return; }
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

            Rendering = args.Contains("--lyrics-test") || args.Contains("--weather-test") || args.Contains("--popup-benchmark") || args.Contains("--render") || args.Contains("--benchmark") || args.Contains("--regression-test");
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

            if (!args.Contains("--lyrics-test") && !args.Contains("--weather-test") && !args.Contains("--popup-benchmark") && !args.Contains("--startup") && !args.Contains("--benchmark") && !args.Contains("--integration-test") && !args.Contains("--regression-test"))
                ShowSettings();
            if (args.Contains("--popup-benchmark"))
                PopupBenchmark.Start();
            if (args.Contains("--regression-test"))
                RegressionTest.Start();
            if (args.Contains("--weather-test"))
                FeatureTests.LiveWeather();
            if (args.Contains("--lyrics-test"))
                LyricsTests.Live();
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

        internal static void SendToOrganizer(string path, BoxData target)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new FileNotFoundException("项目已不存在，请刷新启动台。", path);
            if (target == null)
            {
                target = new BoxData { Title = "新的收纳格", X = 70, Y = 100, Width = 420, Height = 300 };
                S.Boxes.Add(target);
            }
            if (!target.Items.Contains(path, StringComparer.OrdinalIgnoreCase)) target.Items.Add(path);
            target.Excluded.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            target.Enabled = true; target.Collapsed = false; target.Height = Math.Max(170, target.Height);
            Save();
            var existing = windows.OfType<OrganizerWindow>().FirstOrDefault(w => w.ExportName == target.Id && !w.WasClosed);
            if (existing == null) AddWindow(new OrganizerWindow(target));
            else existing.RefreshReferences();
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
            MenuTheme.Apply(App.Resources);
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
                if (settings.WindowState == WindowState.Minimized) settings.WindowState = WindowState.Normal;
                settings.Activate();
                return;
            }
            settings = new SettingsWindow(SetStartup);
            settings.Closed += delegate { settings = null; };
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
