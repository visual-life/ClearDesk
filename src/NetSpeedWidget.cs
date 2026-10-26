using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ClearDesk
{
    public class NetData { public string Date = ""; public long Down, Up; }

    /// <summary>Turns cumulative interface counters into speeds, today's totals and a short history. Counter wrap (32-bit IPv4 statistics) and resets are handled.</summary>
    internal sealed class NetMeter
    {
        internal const int History = 60;
        internal readonly Queue<double> Down = new Queue<double>(), Up = new Queue<double>();
        internal double DownSpeed, UpSpeed;               // bytes per second
        internal long TodayDown, TodayUp;
        internal string Date = "";
        long lastRx = -1, lastTx = -1; DateTime lastTime;
        internal static long Delta(long last, long now)
        {
            if (now >= last) return now - last;
            return last > 3L * 1024 * 1024 * 1024 && last <= uint.MaxValue ? now + (uint.MaxValue + 1L) - last : now;   // 32-bit wrap, otherwise the counter was reset (adapter restarted)
        }
        internal void Restore(NetData d, DateTime now) { if (d != null && d.Date == now.ToString("yyyy-MM-dd")) { TodayDown = Math.Max(0, d.Down); TodayUp = Math.Max(0, d.Up); } Date = now.ToString("yyyy-MM-dd"); }
        internal NetData Snapshot() { return new NetData { Date = Date, Down = TodayDown, Up = TodayUp }; }
        internal void Update(DateTime now, long rx, long tx)
        {
            string day = now.ToString("yyyy-MM-dd");
            if (Date != day) { Date = day; TodayDown = 0; TodayUp = 0; }
            if (lastRx >= 0 && now > lastTime)
            {
                long dr = Delta(lastRx, rx), dt = Delta(lastTx, tx); double secs = Math.Max(0.2, (now - lastTime).TotalSeconds);
                DownSpeed = dr / secs; UpSpeed = dt / secs; TodayDown += dr; TodayUp += dt;
                Push(Down, DownSpeed); Push(Up, UpSpeed);
            }
            lastRx = rx; lastTx = tx; lastTime = now;
        }
        static void Push(Queue<double> q, double v) { q.Enqueue(v); while (q.Count > History) q.Dequeue(); }
        internal void ResetBaseline() { lastRx = -1; lastTx = -1; }

        internal static string Speed(double bytesPerSecond)
        {
            double v = Math.Max(0, bytesPerSecond);
            if (v < 1000) return Math.Round(v).ToString("0", CultureInfo.InvariantCulture) + " B/s";
            if (v < 1000 * 1024) return (v / 1024).ToString(v / 1024 < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " KB/s";
            return (v / 1048576).ToString(v / 1048576 < 10 ? "0.00" : "0.0", CultureInfo.InvariantCulture) + " MB/s";
        }
        internal static string Bytes(long bytes)
        {
            double v = Math.Max(0, bytes);
            if (v < 1000) return v.ToString("0", CultureInfo.InvariantCulture) + " B";
            if (v < 1000 * 1024) return (v / 1024).ToString("0", CultureInfo.InvariantCulture) + " KB";
            if (v < 1000L * 1048576) return (v / 1048576).ToString(v / 1048576 < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " MB";
            return (v / 1073741824).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        }
    }

    /// <summary>Which adapters count: physical ones (Ethernet / Wi-Fi) that are up. Virtual adapters (VPN / TUN / Hyper-V / VMware) carry the same bytes a second time and are skipped, unless nothing else exists.</summary>
    internal static class NetSource
    {
        static readonly Regex Virtual = new Regex("TAP|TUN|Wintun|Meta|Clash|WireGuard|VPN|Virtual|Hyper-V|VMware|VirtualBox|Loopback|Pseudo|Bluetooth|Teredo|ISATAP|6to4|WAN Miniport|Npcap|vEthernet|Tailscale|ZeroTier", RegexOptions.IgnoreCase);
        internal static bool Counts(string description, string name, NetworkInterfaceType type, OperationalStatus status)
        {
            if (status != OperationalStatus.Up) return false;
            if (type == NetworkInterfaceType.Loopback || type == NetworkInterfaceType.Tunnel) return false;
            return !Virtual.IsMatch(description ?? "") && !Virtual.IsMatch(name ?? "");
        }
        internal static bool Read(out long rx, out long tx)
        {
            rx = 0; tx = 0; bool any = false;
            try
            {
                var all = NetworkInterface.GetAllNetworkInterfaces();
                var chosen = all.Where(n => Counts(n.Description, n.Name, n.NetworkInterfaceType, n.OperationalStatus)).ToList();
                if (chosen.Count == 0) chosen = all.Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel).ToList();
                foreach (var n in chosen) { var s = n.GetIPv4Statistics(); rx += s.BytesReceived; tx += s.BytesSent; any = true; }
            }
            catch { }
            return any;
        }
    }

    public class NetSpeedWindow : ToolWidget
    {
        readonly NetMeter meter = new NetMeter();
        readonly TextBlock down = new TextBlock { FontSize = 24, FontFamily = new FontFamily("Segoe UI Light"), Foreground = Brushes.White }, up = new TextBlock { FontSize = 24, FontFamily = new FontFamily("Segoe UI Light"), Foreground = Brushes.White },
            total = Label("", 11, Dim);
        readonly Polyline downLine = new Polyline { Stroke = new SolidColorBrush(Color.FromRgb(0x5F, 0xB0, 0xFF)), StrokeThickness = 1.6, Stretch = Stretch.Fill, StrokeLineJoin = PenLineJoin.Round },
            upLine = new Polyline { Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x5C)), StrokeThickness = 1.6, Stretch = Stretch.Fill, StrokeLineJoin = PenLineJoin.Round };
        DispatcherTimer sampler; DateTime lastSave = DateTime.UtcNow;
        internal Func<DateTime> Now = () => DateTime.Now;
        internal Func<long[]> Source = () => { long r, t; return NetSource.Read(out r, out t) ? new[] { r, t } : null; };
        internal NetMeter Meter { get { return meter; } }

        public NetSpeedWindow() : base("netspeed", "网速流量", SystemParameters.WorkArea.Left + 40, SystemParameters.WorkArea.Top + 500, 300, 200)
        {
            meter.Restore(ToolStore.Load<NetData>("netspeed"), Now());
            var cols = new Grid(); cols.ColumnDefinitions.Add(new ColumnDefinition()); cols.ColumnDefinitions.Add(new ColumnDefinition());
            var d = new StackPanel(); d.Children.Add(Label("↓ 下载", 11, Dim)); d.Children.Add(down);
            var u = new StackPanel(); u.Children.Add(Label("↑ 上传", 11, Dim)); u.Children.Add(up);
            Grid.SetColumn(u, 1); cols.Children.Add(d); cols.Children.Add(u);
            var chart = new Grid { Margin = new Thickness(0, 10, 0, 8), MinHeight = 36, Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)) };
            chart.Children.Add(downLine); chart.Children.Add(upLine);
            var dock = new DockPanel(); DockPanel.SetDock(cols, Dock.Top); DockPanel.SetDock(total, Dock.Bottom);
            dock.Children.Add(cols); dock.Children.Add(total); dock.Children.Add(chart);
            Body.Children.Add(dock);
            ContextMenu.Items.Insert(0, Item("清零今日流量", delegate { meter.TodayDown = 0; meter.TodayUp = 0; Save(); Render(); }));
            sampler = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; sampler.Tick += delegate { Sample(); }; sampler.Start();
            Sample();
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetwork;
        }
        void OnNetwork(object s, EventArgs e) { try { Dispatcher.BeginInvoke(new Action(delegate { meter.ResetBaseline(); })); } catch { } }
        protected override void OnWidgetClosed() { sampler.Stop(); System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetwork; Save(); }
        void Save() { ToolStore.Save("netspeed", meter.Snapshot()); lastSave = DateTime.UtcNow; }

        internal void Sample()
        {
            var s = Source(); if (s == null) { meter.ResetBaseline(); return; }
            meter.Update(Now(), s[0], s[1]);
            if ((DateTime.UtcNow - lastSave).TotalSeconds >= 60) Save();
            if (IsVisible && !State.Collapsed) Render();
        }
        static PointCollection Points(IEnumerable<double> values, double max)
        {
            var list = values.ToList(); var pc = new PointCollection();
            if (list.Count < 2) { pc.Add(new Point(0, 1)); pc.Add(new Point(1, 1)); return pc; }
            for (int i = 0; i < list.Count; i++) pc.Add(new Point(i, 1 - Math.Min(1, list[i] / max)));
            return pc;
        }
        void Render()
        {
            down.Text = NetMeter.Speed(meter.DownSpeed); up.Text = NetMeter.Speed(meter.UpSpeed);
            double max = Math.Max(64 * 1024, Math.Max(meter.Down.DefaultIfEmpty(0).Max(), meter.Up.DefaultIfEmpty(0).Max()) * 1.15);
            downLine.Points = Points(meter.Down, max); upLine.Points = Points(meter.Up, max);
            total.Text = "今日  ↓ " + NetMeter.Bytes(meter.TodayDown) + "   ↑ " + NetMeter.Bytes(meter.TodayUp);
        }
    }
}
