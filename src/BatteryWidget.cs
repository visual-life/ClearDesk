using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClearDesk
{
    /// <summary>2.2.5 "设备电量": settings of the widget (widgets\battery.json).</summary>
    public sealed class BatteryData
    {
        public bool LowAlert;          // off by default
        public int Threshold = 20;
        public bool ShowUnsupported = true;
        public int PollSeconds = 60;
    }

    /// <summary>
    /// 2.2.5 "设备电量": battery of wireless mice / keyboards / headsets (Bluetooth, standard HID battery reports, Logitech HID++ receivers).
    /// Polled every 60 s on a background thread. A device whose level cannot be read says so ("不支持读取电量"); no value is ever guessed.
    /// </summary>
    public class BatteryWindow : ToolWidget
    {
        BatteryData data;
        readonly WrapPanel list = new WrapPanel();
        readonly ScrollViewer scroller;
        readonly TextBlock status;
        readonly DispatcherTimer timer;
        bool busy;
        readonly HashSet<string> warned = new HashSet<string>();
        internal List<BatteryReading> Last = new List<BatteryReading>();
        internal int Scans;
        internal WrapPanel Rows { get { return list; } }
        internal BatteryData Data { get { return data; } }
        internal Func<List<BatteryReading>> Source = DeviceBattery.Scan;     // test hook

        public BatteryWindow() : base("battery", "设备电量", SystemParameters.WorkArea.Left + 420, SystemParameters.WorkArea.Top + 160, 320, 220)
        {
            data = ToolStore.Load<BatteryData>(State.Id);
            Heading.Text = "设备电量";
            AddHeaderButton(IconButton("\uE72C", "立即刷新", delegate { Refresh(); }));
            scroller = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
            scroller.PreviewMouseWheel += (s, e) => { scroller.ScrollToVerticalOffset(scroller.VerticalOffset - e.Delta / 2.4); e.Handled = true; };
            status = Label("正在读取设备电量…", 11, Dim); status.Margin = new Thickness(2, 4, 0, 0); DockPanel.SetDock(status, Dock.Bottom);
            var host = new DockPanel(); host.Children.Add(status); host.Children.Add(scroller);
            Body.Children.Add(host);
            list.SizeChanged += delegate { Reflow(); };
            AddMenuItem("显示 / 隐藏不支持读取电量的设备", delegate { data.ShowUnsupported = !data.ShowUnsupported; Save(); Render(); });
            AddMenuItem("低电量提示：" + "开 / 关（低于 " + data.Threshold + "%）", delegate { data.LowAlert = !data.LowAlert; Save(); Toast("\uE7E7", data.LowAlert ? "已开启低电量提示（低于 " + data.Threshold + "%）" : "已关闭低电量提示"); });
            AddMenuItem("立即刷新", Refresh);
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(Math.Max(30, data.PollSeconds)) };
            timer.Tick += delegate { Refresh(); };
            timer.Start();
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(Refresh));
        }

        void Save() { ToolStore.Save(State.Id, data); }

        internal void Refresh()
        {
            if (busy) return;
            busy = true;
            var src = Source;
            Task.Run(() => { try { return src(); } catch (Exception e) { Program.Log(e); return new List<BatteryReading>(); } })
                .ContinueWith(t => Dispatcher.BeginInvoke(new Action(delegate
                {
                    busy = false; Scans++;
                    Last = t.Result ?? new List<BatteryReading>();
                    Render(); Alert();
                })));
        }

        internal void Render()
        {
            list.Children.Clear();
            var shown = Last.Where(r => r.Readable || data.ShowUnsupported).ToList();
            foreach (var r in shown) list.Children.Add(Row(r));
            int readable = Last.Count(r => r.Readable);
            status.Text = Last.Count == 0 ? "没有找到无线设备" : readable + " 个设备可读电量" + (Last.Count > readable ? "，" + (Last.Count - readable) + " 个不支持" : "") + " · " + DateTime.Now.ToString("HH:mm") + " 更新";
            Reflow();
        }

        FrameworkElement Row(BatteryReading r)
        {
            var name = Label(r.Name, 12.5, Brushes.White); name.ToolTip = r.Name + "\n" + r.Source + (r.Note != null ? "\n" + r.Note : "");
            var kind = Label(r.Kind + (r.Charging == true ? " · 充电中" : ""), 10.5, Dim);
            string value = r.Percent.HasValue ? r.Percent + "%" : r.Level ?? "不支持读取电量";
            var val = Label(value, r.Readable ? 15 : 11, r.Readable ? (Brush)Brushes.White : Dim); val.HorizontalAlignment = HorizontalAlignment.Right; val.VerticalAlignment = VerticalAlignment.Center;
            var top = new DockPanel(); DockPanel.SetDock(val, Dock.Right); top.Children.Add(val);
            var names = new StackPanel(); names.Children.Add(name); names.Children.Add(kind); top.Children.Add(names);
            var stack = new StackPanel(); stack.Children.Add(top);
            if (r.Percent.HasValue)
            {
                var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Soft, Margin = new Thickness(0, 5, 0, 0) };
                var fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Background = r.Percent <= data.Threshold ? new SolidColorBrush(Color.FromRgb(255, 107, 107)) : new SolidColorBrush(Color.FromRgb(112, 214, 140)) };
                int pct = r.Percent.Value;
                track.SizeChanged += delegate { fill.Width = Math.Max(0, track.ActualWidth * pct / 100.0); };
                var g = new Grid(); g.Children.Add(track); g.Children.Add(fill); fill.Margin = track.Margin; stack.Children.Add(g);
            }
            return new Border { Child = stack, Padding = new Thickness(8, 6, 8, 7), Margin = new Thickness(2), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Tag = r };
        }

        /// <summary>Free width: as many columns as fit (min 200 px each), every row card stretched to the column width.</summary>
        internal void Reflow()
        {
            double w = scroller.ActualWidth > 0 ? scroller.ActualWidth : Math.Max(100, ActualWidth - 30);
            int cols = Columns(w);
            double cw = Math.Floor(w / cols) - 4;
            foreach (FrameworkElement c in list.Children) c.Width = Math.Max(80, cw);
        }
        internal static int Columns(double width) { return Math.Max(1, (int)(width / 200)); }

        void Alert()
        {
            if (!data.LowAlert) return;
            foreach (var r in Last.Where(x => x.Percent.HasValue))
            {
                if (r.Percent <= data.Threshold && r.Charging != true) { if (warned.Add(r.Key)) Program.Balloon("设备电量低", r.Name + " 剩余 " + r.Percent + "%"); }
                else if (r.Percent > data.Threshold + 5) warned.Remove(r.Key);
            }
        }

        protected override void OnWidgetClosed() { timer.Stop(); }
    }
}
