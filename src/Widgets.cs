using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace ClearDesk
{
    public class GlassWindow : Window
    {
        protected Grid Root = new Grid(), Body = new Grid();
        protected Border Frame;
        protected BoxData State;
        protected StackPanel Layout = new StackPanel();
        protected DockPanel Header;
        protected TextBlock Heading;
        protected DispatcherTimer Timer;
        protected GlassPane Surface;
        public bool WasClosed;
        bool ready;
        public string ExportName
        {
            get
            {
                return State.Id;
            }
        }

        public GlassWindow(BoxData state, string title, bool plain = false)
        {
            State = state;
            Icon = AppBrand.Icon;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
            Title = "ClearDesk · " + title;
            Width = Math.Max(180, state.Width);
            Height = Math.Max(100, state.Height);
            Left = state.X;
            Top = state.Y;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = Program.Preview;
            ShowActivated = false;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            Foreground = Brushes.White;
            var wa = SystemParameters.WorkArea;
            if (!System.Windows.Forms.Screen.AllScreens.Any(s => s.WorkingArea.Contains((int)Left + 30, (int)Top + 30)))
            {
                Left = wa.Left + 20;
                Top = wa.Top + 20;
            }

            Frame = new Border
            {
                CornerRadius = new CornerRadius(22),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(55, 235, 242, 255)),
                Child = Root
            };
            Content = Frame;
            if (!plain || state.MaterialSelected)
            {
                Surface = new GlassPane(state.Material);
                Root.Children.Add(Surface);
                MouseMove += delegate (object sender, MouseEventArgs e)
                {
                    if (Root.Children.Contains(Surface))
                        Surface.Pointer(e.GetPosition(Root), ActualWidth, ActualHeight);
                };
                MouseLeave += delegate
                {
                    if (Surface != null)
                        Surface.Leave();
                };
            }
            else
            {
                Frame.BorderThickness = new Thickness(0);
                Root.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            }

            Root.Children.Add(Layout);
            Layout.Margin = new Thickness(20);
            Header = new DockPanel
            {
                Margin = new Thickness(0, 0, 0, 18),
                Background = Brushes.Transparent
            };
            Heading = Program.Text(title, 13);
            Heading.FontWeight = FontWeights.SemiBold;
            Header.Children.Add(Heading);
            Layout.Children.Add(Header);
            Layout.Children.Add(Body);
            Header.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
            {
                if (e.ClickCount == 1 && !State.Locked && e.OriginalSource == Heading)
                {
                    try
                    {
                        DragMove();
                        Program.Save();
                    }
                    catch
                    {
                    }

                    e.Handled = true;
                }
                else if (e.ClickCount == 2 && HeaderDoubleClickCollapses)
                {
                    State.Collapsed = !State.Collapsed;
                    SetCollapsed();
                    Program.Save();
                }
            };
            LocationChanged += delegate
            {
                UpdatePaper();
                if (ready)
                {
                    State.X = Left;
                    State.Y = Top;
                }
            };
            MouseLeftButtonUp += delegate
            {
                if (ready)
                    Program.Save();
            };
            SizeChanged += delegate
            {
                Root.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2)), 22, 22);
                UpdatePaper();
            };
            ContextMenu = Menu();
            Loaded += delegate
            {
                ready = true;
                SetCollapsed();
                UpdatePaper();
                var h = new WindowInteropHelper(this).Handle;
                if (!Program.Preview)
                    Native.SetLong(h, -20, new IntPtr(Native.GetLong(h, -20).ToInt64() | 0x80));
                Native.SetWindowPos(h, new IntPtr(Topmost ? -1 : 1), 0, 0, 0, 0, 0x13);
            };
            Closed += delegate
            {
                WasClosed = true;
                if (Timer != null)
                    Timer.Stop();
            };
        }

        void UpdatePaper()
        {
        }

        protected virtual bool HeaderDoubleClickCollapses
        {
            get
            {
                return true;
            }
        }

        protected virtual void SetCollapsed()
        {
            Body.Visibility = State.Collapsed ? Visibility.Collapsed : Visibility.Visible;
            Height = State.Collapsed ? 64 : State.Height;
        }

        protected MenuItem Item(string text, Action action, bool check = false)
        {
            var m = new MenuItem
            {
                Header = text,
                IsChecked = check
            };
            m.Click += delegate
            {
                action();
            };
            return m;
        }

        protected virtual ContextMenu Menu()
        {
            var c = new ContextMenu();
            var material = new MenuItem
            {
                Header = "材质设置"
            };
            var clear = Item("玻璃 · 清透高光", delegate
            {
                Material("liquid");
            });
            var frost = Item("雾面 · 背景磨砂", delegate
            {
                Material("frosted");
            });
            material.Items.Add(clear);
            material.Items.Add(frost);
            material.SubmenuOpened += delegate
            {
                clear.IsChecked = State.Material == "liquid";
                frost.IsChecked = State.Material == "frosted";
            };
            c.Items.Add(material);
            c.Items.Add(Item("收缩 / 展开", delegate
            {
                State.Collapsed = !State.Collapsed;
                SetCollapsed();
                Program.Save();
            }));
            c.Items.Add(Item("锁定 / 解锁位置", delegate
            {
                State.Locked = !State.Locked;
                Program.Save();
            }, State.Locked));
            c.Items.Add(Item("打开设置", Program.ShowSettings));
            c.Items.Add(Item("恢复位置", delegate
            {
                Left = 20;
                Top = 20;
                State.X = Left;
                State.Y = Top;
                Program.Save();
            }));
            c.Items.Add(Item("关闭组件", Disable));
            return c;
        }

        protected virtual void Disable()
        {
            State.Enabled = false;
            switch (State.Id)
            {
                case "clock":
                    Program.S.Clock = false;
                    break;
                case "performance":
                    Program.S.Performance = false;
                    break;
                case "dock":
                    Program.S.Dock = false;
                    break;
                case "calendar":
                    Program.S.Calendar = false;
                    break;
                case "almanac":
                    Program.S.Almanac = false;
                    break;
                case "weather":
                    Program.S.Weather = false;
                    break;
                case "music":
                    Program.S.Music = false;
                    break;
            }

            Program.Save();
            Close();
        }

        protected virtual void Material(string m)
        {
            State.Material = m;
            State.MaterialSelected = true;
            if (Surface == null)
            {
                Surface = new GlassPane(m);
                Root.Children.Insert(0, Surface);
                Frame.BorderThickness = new Thickness(1);
            }

            Surface.SetMaterial(m);
            Program.Save();
        }

        internal void TestMaterial(string m)
        {
            Material(m);
        }

        internal GlassPane TestSurface
        {
            get
            {
                return Surface;
            }
        }

        protected void Tick(int ms, Action action)
        {
            Timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(ms)
            };
            Timer.Tick += delegate
            {
                action();
            };
            Timer.Start();
            action();
        }
    }

    public class ClockWindow : GlassWindow
    {
        public ClockWindow() : base(Program.Widget("clock", SystemParameters.WorkArea.Right - 410, 245, 370, 170), "时钟", true)
        {
            Header.Visibility = Visibility.Collapsed;
            Layout.Margin = new Thickness(0);
            var c = new Grid();
            var day = Program.Text("", 64, "#89FFFFFF");
            day.FontFamily = new FontFamily("Segoe Script");
            day.FontStyle = FontStyles.Italic;
            day.HorizontalAlignment = HorizontalAlignment.Center;
            c.Children.Add(day);
            var s = new StackPanel
            {
                Margin = new Thickness(0, 52, 0, 0)
            };
            var t = Program.Text("", 40);
            t.FontFamily = new FontFamily("Segoe UI Light");
            t.TextAlignment = TextAlignment.Center;
            var d = Program.Text("", 20);
            d.FontFamily = new FontFamily("Segoe UI Light");
            d.TextAlignment = TextAlignment.Center;
            s.Children.Add(t);
            s.Children.Add(d);
            c.Children.Add(s);
            Body.Children.Add(c);
            c.MouseLeftButtonDown += delegate
            {
                if (!State.Locked)
                {
                    try
                    {
                        DragMove();
                        Program.Save();
                    }
                    catch
                    {
                    }
                }
            };
            Tick(10000, delegate
            {
                var now = DateTime.Now;
                string a = now.ToString("dddd", CultureInfo.InvariantCulture), b = now.ToString("HH:mm"), cdate = now.ToString("dd MMMM", CultureInfo.InvariantCulture);
                if (day.Text != a)
                    day.Text = a;
                if (t.Text != b)
                    t.Text = b;
                if (d.Text != cdate)
                    d.Text = cdate;
            });
        }
    }

    public class PerformanceWindow : GlassWindow
    {
        TextBlock cpu, gpu, mem, net;
        ProgressBar cp, gp, mp;
        long lastIdle, lastKernel, lastUser, lastRx, lastTx;
        DateTime last = DateTime.UtcNow;
        List<PerformanceCounter> gpuCounters = new List<PerformanceCounter>();
        int cycles;
        public PerformanceWindow() : base(Program.Widget("performance", SystemParameters.WorkArea.Right - 370, 20, 350, 210), "性能")
        {
            var grid = new UniformGrid
            {
                Columns = 2,
                Rows = 2
            };
            cpu = Metric(grid, "CPU", out cp, "#72BDFF");
            gpu = Metric(grid, "GPU", out gp, "#FFC49A");
            mem = Metric(grid, "内存", out mp, "#86E3B8");
            ProgressBar unused;
            net = Metric(grid, "网络", out unused, "#9B9DFF");
            unused.Visibility = Visibility.Collapsed;
            Body.Children.Add(grid);
            Native.GetSystemTimes(out lastIdle, out lastKernel, out lastUser);
            ReadNet(out lastRx, out lastTx);
            Tick(2000, Update);
        }

        TextBlock Metric(Panel p, string name, out ProgressBar bar, string color)
        {
            var s = new StackPanel
            {
                Margin = new Thickness(0, 0, 16, 18)
            };
            s.Children.Add(Program.Text(name, 11, "#D0D7E1"));
            var v = Program.Text("—", 18);
            v.Margin = new Thickness(0, 5, 0, 6);
            s.Children.Add(v);
            bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Height = 3,
                Foreground = (Brush)new BrushConverter().ConvertFromString(color),
                Background = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
                BorderThickness = new Thickness(0)
            };
            s.Children.Add(bar);
            p.Children.Add(s);
            return v;
        }

        void ReadNet(out long rx, out long tx)
        {
            rx = 0;
            tx = 0;
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                if (n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                {
                    try
                    {
                        var s = n.GetIPv4Statistics();
                        rx += s.BytesReceived;
                        tx += s.BytesSent;
                    }
                    catch
                    {
                    }
                }
        }

        bool sampling;
        Dictionary<string, CounterSample> gpuPrevious = new Dictionary<string, CounterSample>();
        async void Update()
        {
            if (sampling || WasClosed)
                return;
            sampling = true;
            try
            {
                var data = await Task.Run(delegate
                {
                    long i, k, u;
                    Native.GetSystemTimes(out i, out k, out u);
                    long total = k - lastKernel + u - lastUser;
                    double value = total <= 0 ? 0 : Math.Max(0, Math.Min(100, 100.0 * (total - (i - lastIdle)) / total));
                    lastIdle = i;
                    lastKernel = k;
                    lastUser = u;
                    var m = new Native.MEMORY
                    {
                        length = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MEMORY))
                    };
                    Native.GlobalMemoryStatusEx(ref m);
                    long rx, tx;
                    ReadNet(out rx, out tx);
                    double sec = Math.Max(.1, (DateTime.UtcNow - last).TotalSeconds);
                    double down = Math.Max(0, rx - lastRx) / sec, up = Math.Max(0, tx - lastTx) / sec;
                    lastRx = rx;
                    lastTx = tx;
                    last = DateTime.UtcNow;
                    double gpuValue = -1;
                    if (cycles++ % 3 == 0)
                        try
                        {
                            var category = new PerformanceCounterCategory("GPU Engine");
                            var samples = category.ReadCategory()["Utilization Percentage"];
                            double sum = 0;
                            var fresh = new Dictionary<string, CounterSample>();
                            foreach (InstanceData instance in samples.Values)
                            {
                                if (!instance.InstanceName.Contains("engtype_3D"))
                                    continue;
                                CounterSample previous;
                                fresh[instance.InstanceName] = instance.Sample;
                                if (gpuPrevious.TryGetValue(instance.InstanceName, out previous))
                                    sum += CounterSample.Calculate(previous, instance.Sample);
                            }

                            gpuPrevious = fresh;
                            gpuValue = Math.Max(0, Math.Min(100, sum));
                        }
                        catch
                        {
                        }

                    return new double[]
                    {
                        value,
                        m.load,
                        (m.totalPhys - m.availPhys) / 1073741824.0,
                        m.totalPhys / 1073741824.0,
                        down,
                        up,
                        gpuValue
                    };
                });
                if (WasClosed)
                    return;
                cp.Value = data[0];
                cpu.Text = data[0].ToString("0") + "%";
                mp.Value = data[1];
                mem.Text = data[1].ToString("0") + "%";
                mem.ToolTip = data[2].ToString("0.0") + " / " + data[3].ToString("0.0") + " GB";
                if (data[6] >= 0)
                {
                    gp.Value = data[6];
                    gpu.Text = data[6].ToString("0") + "%";
                }
                else if (gpu.Text == "—")
                    gpu.Text = "不可用";
                net.FontSize = 11;
                net.Text = "↓ " + Rate(data[4]) + "\n↑ " + Rate(data[5]);
            }
            catch (Exception e)
            {
                Program.Log(e);
            }
            finally
            {
                sampling = false;
            }
        }

        static string Rate(double n)
        {
            return n > 1048576 ? (n / 1048576).ToString("0.0") + " MB/s" : (n / 1024).ToString("0.0") + " KB/s";
        }
    }

    public class CalendarWindow : GlassWindow
    {
        DateTime date = DateTime.Today;
        StackPanel panel = new StackPanel();
        bool almanac;
        public CalendarWindow(bool isAlmanac) : base(Program.Widget(isAlmanac ? "almanac" : "calendar", SystemParameters.WorkArea.Right - 370, isAlmanac ? 550 : 420, 350, isAlmanac ? 350 : 350), isAlmanac ? "黄历" : "日历")
        {
            almanac = isAlmanac;
            Body.Children.Add(panel);
            ContextMenu.Items.Insert(0, Item("回到今天", delegate
            {
                date = DateTime.Today;
                Render();
            }));
            Render();
        }

        public static string Lunar(DateTime dt)
        {
            try
            {
                var l = new Lunar.Solar(dt.Year, dt.Month, dt.Day).Lunar;
                return l.MonthInChinese + "月" + l.DayInChinese + " · " + l.YearInGanZhi + "年";
            }
            catch
            {
                return "日期超出农历支持范围";
            }
        }

        void Render()
        {
            panel.Children.Clear();
            var nav = new DockPanel();
            var prev = Program.Button("‹", delegate
            {
                date = almanac ? date.AddDays(-1) : date.AddMonths(-1);
                Render();
            });
            var next = Program.Button("›", delegate
            {
                date = almanac ? date.AddDays(1) : date.AddMonths(1);
                Render();
            });
            DockPanel.SetDock(prev, Dock.Left);
            DockPanel.SetDock(next, Dock.Right);
            nav.Children.Add(prev);
            nav.Children.Add(next);
            var h = Program.Text(date.ToString(almanac ? "yyyy年 M月 d日" : "yyyy年 M月"), 16);
            h.VerticalAlignment = VerticalAlignment.Center;
            h.TextAlignment = TextAlignment.Center;
            nav.Children.Add(h);
            panel.Children.Add(nav);
            if (almanac)
            {
                var l = new Lunar.Solar(date.Year, date.Month, date.Day).Lunar;
                var a = Program.Text(Lunar(date), 20);
                a.Margin = new Thickness(0, 14, 0, 8);
                panel.Children.Add(a);
                panel.Children.Add(Program.Text(l.DayInGanZhi + "日 · " + l.DayShengXiao + " · " + l.JieQi, 13, "#BBD0E5"));
                var yi = Program.Text("宜  " + string.Join(" · ", l.DayYi), 13, "#9AE8BE");
                yi.Margin = new Thickness(0, 16, 0, 10);
                panel.Children.Add(yi);
                panel.Children.Add(Program.Text("忌  " + string.Join(" · ", l.DayJi), 13, "#F2B5AF"));
                var note = Program.Text("传统历法参考 · lunar-csharp", 10, "#A2AAB8");
                note.Margin = new Thickness(0, 18, 0, 0);
                panel.Children.Add(note);
            }
            else
            {
                var grid = new UniformGrid
                {
                    Columns = 7,
                    Rows = 7,
                    Margin = new Thickness(0, 12, 0, 0)
                };
                foreach (var day in new[]
                {
                    "一",
                    "二",
                    "三",
                    "四",
                    "五",
                    "六",
                    "日"
                }

                )
                {
                    var t = Program.Text(day, 12, "#AABED1");
                    t.TextAlignment = TextAlignment.Center;
                    grid.Children.Add(t);
                }

                var first = new DateTime(date.Year, date.Month, 1);
                int offset = ((int)first.DayOfWeek + 6) % 7;
                for (int i = 0; i < 42; i++)
                {
                    int day = i - offset + 1;
                    var t = Program.Text(day > 0 && day <= DateTime.DaysInMonth(date.Year, date.Month) ? day.ToString() : "", 13);
                    t.TextAlignment = TextAlignment.Center;
                    t.Margin = new Thickness(0, 4, 0, 4);
                    if (day == DateTime.Today.Day && date.Year == DateTime.Today.Year && date.Month == DateTime.Today.Month)
                    {
                        t.Foreground = new SolidColorBrush(Color.FromRgb(130, 210, 255));
                        t.FontWeight = FontWeights.Bold;
                    }

                    grid.Children.Add(t);
                }

                panel.Children.Add(grid);
                panel.Children.Add(Program.Text(Lunar(DateTime.Today), 12, "#BBD0E5"));
            }
        }
    }

    public class WeatherWindow : GlassWindow
    {
        public static Dictionary<string, string> Cities = new Dictionary<string, string>
        {
            {
                "北京",
                "39.90,116.40"
            },
            {
                "上海",
                "31.23,121.47"
            },
            {
                "广州",
                "23.13,113.26"
            },
            {
                "深圳",
                "22.54,114.06"
            },
            {
                "杭州",
                "30.27,120.15"
            },
            {
                "成都",
                "30.57,104.07"
            },
            {
                "武汉",
                "30.59,114.31"
            },
            {
                "南京",
                "32.06,118.80"
            },
            {
                "重庆",
                "29.56,106.55"
            },
            {
                "西安",
                "34.34,108.94"
            },
            {
                "天津",
                "39.08,117.20"
            },
            {
                "苏州",
                "31.30,120.58"
            }
        };
        TextBlock temp = Program.Text("—", 40), description = Program.Text("", 14), detail = Program.Text("", 12, "#C5D3E2");
        bool busy;
        DateTime fetched = DateTime.MinValue;
        public WeatherWindow() : base(Program.Widget("weather", SystemParameters.WorkArea.Right - 370, 420, 350, 235), "天气")
        {
            var p = new StackPanel();
            p.Children.Add(temp);
            p.Children.Add(description);
            detail.Margin = new Thickness(0, 14, 0, 0);
            p.Children.Add(detail);
            p.Children.Add(Program.Text("数据：Open-Meteo · CC BY 4.0", 10, "#A8B7C9"));
            Body.Children.Add(p);
            ContextMenu.Items.Insert(0, Item("刷新天气", delegate
            {
                fetched = DateTime.MinValue;
                Fetch();
            }));
            Tick(60000, Fetch);
        }

        public static string Description(int code)
        {
            if (code == 0)
                return "晴";
            if (code <= 3)
                return "多云";
            if (code <= 48)
                return "雾";
            if (code <= 57)
                return "毛毛雨";
            if (code <= 67)
                return "雨";
            if (code <= 77)
                return "雪";
            if (code <= 82)
                return "阵雨";
            if (code <= 86)
                return "阵雪";
            return "雷雨";
        }

        async void Fetch()
        {
            if (busy || WasClosed)
                return;
            if (!Program.S.WeatherOnline)
            {
                temp.Text = "未联网";
                temp.FontSize = 28;
                description.Text = "在设置中开启天气联网";
                detail.Text = "城市由你选择，不自动定位";
                return;
            }

            if ((DateTime.Now - fetched).TotalMinutes < 20)
                return;
            busy = true;
            string city = Program.S.City, coords;
            if (!Cities.TryGetValue(city, out coords))
                coords = Cities["上海"];
            description.Text = city + " · 更新中";
            try
            {
                using (var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false
                }

                )
                using (var client = new HttpClient(handler))
                {
                    client.Timeout = TimeSpan.FromSeconds(12);
                    string[] xy = coords.Split(',');
                    string url = "https://api.open-meteo.com/v1/forecast?latitude=" + xy[0] + "&longitude=" + xy[1] + "&current=temperature_2m,relative_humidity_2m,weather_code,wind_speed_10m&timezone=auto";
                    string json = await client.GetStringAsync(url);
                    if (WasClosed)
                        return;
                    var j = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                    var c = (Dictionary<string, object>)j["current"];
                    temp.FontSize = 40;
                    temp.Text = Convert.ToDouble(c["temperature_2m"]).ToString("0°");
                    description.Text = city + " · " + Description(Convert.ToInt32(c["weather_code"]));
                    detail.Text = "湿度 " + c["relative_humidity_2m"] + "%  ·  风速 " + c["wind_speed_10m"] + " km/h\n观测时间 " + c["time"];
                    fetched = DateTime.Now;
                }
            }
            catch (Exception e)
            {
                Program.Log(e);
                if (!WasClosed)
                {
                    description.Text = city + " · 更新失败";
                    detail.Text = "网络暂不可用，右键可重试";
                }

                fetched = DateTime.Now.AddMinutes(-15);
            }
            finally
            {
                busy = false;
            }
        }
    }

}
