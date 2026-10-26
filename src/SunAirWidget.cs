using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClearDesk
{
    public class SunAirCache { public string PlaceName = "", Source = "", Level = "", Scale = "", Updated = "", ObservedAt = ""; public int Aqi = -1; public double Pm25 = -1, Pm10 = -1; public double Lat, Lon; }

    internal sealed class AirReading
    {
        public int Aqi; public double Pm25 = -1, Pm10 = -1; public string Source = "", Scale = "", Time = ""; public DateTime Updated; public bool Stale;
        public string Level { get { return AirLogic.Level(Aqi); } }
    }

    internal static class AirLogic
    {
        internal static string Level(int aqi) { return aqi < 0 ? "—" : aqi <= 50 ? "优" : aqi <= 100 ? "良" : aqi <= 150 ? "轻度污染" : aqi <= 200 ? "中度污染" : aqi <= 300 ? "重度污染" : "严重污染"; }
        internal static Color LevelColor(int aqi) { return aqi < 0 ? Color.FromRgb(180, 190, 205) : aqi <= 50 ? Color.FromRgb(0x5B, 0xE0, 0x8A) : aqi <= 100 ? Color.FromRgb(0xFF, 0xDB, 0x5C) : aqi <= 150 ? Color.FromRgb(0xFF, 0xA3, 0x4D) : aqi <= 200 ? Color.FromRgb(0xFF, 0x6B, 0x5E) : aqi <= 300 ? Color.FromRgb(0xC9, 0x6B, 0xE0) : Color.FromRgb(0xB0, 0x3A, 0x5B); }

        static object At(object node, params string[] path) { foreach (var k in path) { var d = node as Dictionary<string, object>; if (d == null || !d.ContainsKey(k)) return null; node = d[k]; } return node; }
        static double? Num(object o) { if (o == null) return null; double v; return double.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? (double?)v : null; }

        /// <summary>Tencent weather "air" block (China national AQI, by county or city).</summary>
        internal static AirReading ParseTencent(string json)
        {
            var root = new JavaScriptSerializer().DeserializeObject(json);
            if (Convert.ToString(At(root, "status"), CultureInfo.InvariantCulture) != "200") throw new FormatException("腾讯空气质量未返回数据。");
            var aqi = Num(At(root, "data", "air", "aqi")); if (aqi == null || aqi < 0) throw new FormatException("腾讯空气质量没有这个地点的数据。");
            string t = Convert.ToString(At(root, "data", "air", "update_time"), CultureInfo.InvariantCulture); DateTime d;
            return new AirReading { Aqi = (int)Math.Round(aqi.Value), Pm25 = Num(At(root, "data", "air", "pm2.5")) ?? Num(At(root, "data", "air", "pm25")) ?? -1, Pm10 = Num(At(root, "data", "air", "pm10")) ?? -1, Source = "腾讯天气", Scale = "AQI（国标）",
                Time = DateTime.TryParseExact(t ?? "", "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d.ToString("HH:mm") : "", Updated = DateTime.Now };
        }
        /// <summary>Open-Meteo air-quality API (CAMS model, US AQI scale).</summary>
        internal static AirReading ParseOpenMeteo(string json)
        {
            var root = new JavaScriptSerializer().DeserializeObject(json);
            var aqi = Num(At(root, "current", "us_aqi")); if (aqi == null) throw new FormatException("Open-Meteo 空气质量没有数据。");
            string t = Convert.ToString(At(root, "current", "time"), CultureInfo.InvariantCulture); DateTime d;
            return new AirReading { Aqi = (int)Math.Round(aqi.Value), Pm25 = Num(At(root, "current", "pm2_5")) ?? -1, Pm10 = Num(At(root, "current", "pm10")) ?? -1, Source = "Open-Meteo", Scale = "AQI（美标）",
                Time = DateTime.TryParse(t ?? "", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d.ToString("HH:mm") : "", Updated = DateTime.Now };
        }
        static string Esc(string s) { return Uri.EscapeDataString(s ?? ""); }
        internal static List<KeyValuePair<string, Func<string, AirReading>>> Sources(GeoPlace p, out List<string> urls)
        {
            var list = new List<KeyValuePair<string, Func<string, AirReading>>>(); urls = new List<string>();
            if (p.InChina && (p.City.Length + p.Province.Length + p.District.Length) > 0)
            {
                string baseUrl = "https://wis.qq.com/weather/common?source=pc&weather_type=air&province=" + Esc(string.IsNullOrEmpty(p.Province) ? p.City : p.Province) + "&city=" + Esc(string.IsNullOrEmpty(p.City) ? p.Province : p.City) + "&county=";
                if (p.District.Length > 0) { urls.Add(baseUrl + Esc(p.District)); list.Add(new KeyValuePair<string, Func<string, AirReading>>("腾讯天气", ParseTencent)); }
                urls.Add(baseUrl); list.Add(new KeyValuePair<string, Func<string, AirReading>>(p.District.Length > 0 ? "腾讯天气（市级）" : "腾讯天气", ParseTencent));
            }
            if (p.HasCoords)
            {
                urls.Add("https://air-quality-api.open-meteo.com/v1/air-quality?latitude=" + p.Lat.ToString("0.00", CultureInfo.InvariantCulture) + "&longitude=" + p.Lon.ToString("0.00", CultureInfo.InvariantCulture) + "&current=us_aqi,pm2_5,pm10&timezone=auto");
                list.Add(new KeyValuePair<string, Func<string, AirReading>>("Open-Meteo", ParseOpenMeteo));
            }
            return list;
        }
        /// <summary>Tries the sources in order; the first one with data wins. Returns null with the reasons when all fail.</summary>
        internal static async Task<AirReading> FetchAsync(GeoPlace place, Func<string, CancellationToken, Task<string>> download, CancellationToken ct, List<string> log)
        {
            List<string> urls; var sources = Sources(place, out urls);
            for (int i = 0; i < sources.Count; i++)
            {
                try
                {
                    string text = await download(urls[i], ct).ConfigureAwait(false);
                    var r = sources[i].Value(text); r.Source = sources[i].Key; if (log != null) log.Add(sources[i].Key + " 成功"); return r;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e) { if (log != null) log.Add(sources[i].Key + " 失败（" + WeatherService.Reason(e) + "）"); }
            }
            return null;
        }
    }

    /// <summary>Sunrise / sunset computed offline (NOAA solar position equations, accurate to about a minute outside the polar regions).</summary>
    internal static class Astronomy
    {
        internal sealed class SunTimes { public DateTime? Sunrise, Sunset; public bool PolarDay, PolarNight; public TimeSpan? DayLength { get { return Sunrise.HasValue && Sunset.HasValue ? Sunset - Sunrise : (TimeSpan?)null; } } }
        static double Rad(double d) { return d * Math.PI / 180; }
        static double Deg(double r) { return r * 180 / Math.PI; }
        static double JulianDay(int y, int m, int d)
        {
            if (m <= 2) { y -= 1; m += 12; }
            int a = y / 100, b = 2 - a + a / 4;
            return Math.Floor(365.25 * (y + 4716)) + Math.Floor(30.6001 * (m + 1)) + d + b - 1524.5;
        }
        static double Century(double jd) { return (jd - 2451545.0) / 36525.0; }
        static void Sun(double t, out double eqTime, out double decl)
        {
            double l0 = (280.46646 + t * (36000.76983 + t * 0.0003032)) % 360, m = 357.52911 + t * (35999.05029 - 0.0001537 * t), e = 0.016708634 - t * (0.000042037 + 0.0000001267 * t);
            double mr = Rad(m), c = Math.Sin(mr) * (1.914602 - t * (0.004817 + 0.000014 * t)) + Math.Sin(2 * mr) * (0.019993 - 0.000101 * t) + Math.Sin(3 * mr) * 0.000289;
            double trueLong = l0 + c, omega = 125.04 - 1934.136 * t, lambda = trueLong - 0.00569 - 0.00478 * Math.Sin(Rad(omega));
            double seconds = 21.448 - t * (46.815 + t * (0.00059 - t * 0.001813)), eps0 = 23 + (26 + seconds / 60) / 60, eps = eps0 + 0.00256 * Math.Cos(Rad(omega));
            decl = Deg(Math.Asin(Math.Sin(Rad(eps)) * Math.Sin(Rad(lambda))));
            double y = Math.Pow(Math.Tan(Rad(eps) / 2), 2), l0r = Rad(l0);
            eqTime = 4 * Deg(y * Math.Sin(2 * l0r) - 2 * e * Math.Sin(mr) + 4 * e * y * Math.Sin(mr) * Math.Cos(2 * l0r) - 0.5 * y * y * Math.Sin(4 * l0r) - 1.25 * e * e * Math.Sin(2 * mr));
        }
        // minutes after 0:00 UTC of the given Julian day; NaN when the sun does not rise / set
        static double EventUtc(bool rise, double jd, double lat, double lon)
        {
            double eq, dec; Sun(Century(jd), out eq, out dec);
            double cosH = Math.Cos(Rad(90.833)) / (Math.Cos(Rad(lat)) * Math.Cos(Rad(dec))) - Math.Tan(Rad(lat)) * Math.Tan(Rad(dec));
            if (cosH > 1 || cosH < -1) return double.NaN;
            double h = Math.Acos(cosH); if (!rise) h = -h;
            return 720 - 4 * (lon + Deg(h)) - eq;
        }
        internal static SunTimes Compute(double lat, double lon, DateTime localDate, double utcOffsetHours)
        {
            var r = new SunTimes(); double jd = JulianDay(localDate.Year, localDate.Month, localDate.Day);
            Func<bool, DateTime?> one = rise =>
            {
                double m = EventUtc(rise, jd, lat, lon); if (double.IsNaN(m)) return null;
                m = EventUtc(rise, jd + m / 1440.0, lat, lon); if (double.IsNaN(m)) return null;
                return new DateTime(localDate.Year, localDate.Month, localDate.Day).AddMinutes(m + utcOffsetHours * 60);
            };
            r.Sunrise = one(true); r.Sunset = one(false);
            if (!r.Sunrise.HasValue || !r.Sunset.HasValue)
            {
                double eq, dec; Sun(Century(jd), out eq, out dec);
                bool summer = (lat >= 0) == (dec > 0); r.PolarDay = summer; r.PolarNight = !summer; r.Sunrise = null; r.Sunset = null;
            }
            return r;
        }
        internal static string Clock(DateTime? t) { return t.HasValue ? t.Value.ToString("HH:mm", CultureInfo.InvariantCulture) : "--:--"; }
        internal static string Length(TimeSpan? d) { return d.HasValue ? (int)d.Value.TotalHours + " 小时 " + d.Value.Minutes + " 分" : "—"; }
        /// <summary>0..1 position of "now" between sunrise and sunset (null at night).</summary>
        internal static double? DayProgress(SunTimes t, DateTime now)
        {
            if (!t.Sunrise.HasValue || !t.Sunset.HasValue || now < t.Sunrise || now > t.Sunset) return null;
            return (now - t.Sunrise.Value).TotalSeconds / (t.Sunset.Value - t.Sunrise.Value).TotalSeconds;
        }
    }

    public class SunAirWindow : ToolWidget
    {
        readonly TextBlock rise = new TextBlock { FontSize = 26, FontFamily = new FontFamily("Segoe UI Light"), Foreground = Brushes.White }, set = new TextBlock { FontSize = 26, FontFamily = new FontFamily("Segoe UI Light"), Foreground = Brushes.White },
            dayText = Label("", 11, Dim), place = Label("", 11, Dim), aqiNumber = new TextBlock { FontSize = 34, FontFamily = new FontFamily("Segoe UI Light"), Foreground = Brushes.White }, aqiLevel = Label("", 14), aqiDetail = Label("", 11, Dim), status = Label("", 10.5, Dim);
        readonly ProgressBar sunBar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 4, Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x6B)), BorderThickness = new Thickness(0), Margin = new Thickness(0, 8, 0, 4) };
        GeoPlace current; SunAirCache cache; readonly CancellationTokenSource cancel = new CancellationTokenSource();
        DispatcherTimer refresh; int failures; static readonly Random Jitter = new Random();
        internal Func<DateTime> Now = () => DateTime.Now;
        internal Func<string, CancellationToken, Task<string>> Download = (u, ct) => WeatherService.Download(u, ct);
        internal Func<CancellationToken, Task<GeoPlace>> Locate = async ct => { var m = Program.S.ManualPlace(); if (m != null) return m; var c = WeatherService.Shared.Cached; if (c != null && c.Place != null) return c.Place; if (!Program.S.WeatherOnline) return null; var r = await WeatherService.Shared.ReadAsync(false, ct); return r.Place; };
        internal Task LastRefresh;

        public SunAirWindow() : base("sunair", "日出日落 / 空气质量", SystemParameters.WorkArea.Left + 380, SystemParameters.WorkArea.Top + 440, 320, 250)
        {
            cache = ToolStore.Load<SunAirCache>("sunair");
            var cols = new Grid(); cols.ColumnDefinitions.Add(new ColumnDefinition()); cols.ColumnDefinitions.Add(new ColumnDefinition());
            var a = new StackPanel(); a.Children.Add(Label("☀ 日出", 11, Dim)); a.Children.Add(rise);
            var b = new StackPanel(); b.Children.Add(Label("☾ 日落", 11, Dim)); b.Children.Add(set);
            Grid.SetColumn(b, 1); cols.Children.Add(a); cols.Children.Add(b);
            var air = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var airRow = new StackPanel { Orientation = Orientation.Horizontal }; aqiLevel.Margin = new Thickness(10, 0, 0, 6); aqiLevel.VerticalAlignment = VerticalAlignment.Bottom;
            airRow.Children.Add(aqiNumber); airRow.Children.Add(aqiLevel);
            air.Children.Add(Label("空气质量", 11, Dim)); air.Children.Add(airRow); air.Children.Add(aqiDetail);
            var stack = new StackPanel(); stack.Children.Add(cols); stack.Children.Add(sunBar); stack.Children.Add(dayText); stack.Children.Add(air); stack.Children.Add(status); status.Margin = new Thickness(0, 8, 0, 0);
            Body.Children.Add(stack);
            ContextMenu.Items.Insert(0, Item("重新定位并刷新", delegate { WeatherService.Shared.NetworkChanged(); current = null; LastRefresh = RefreshAsync(); }));
            ShowCache(); Update();
            refresh = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) }; int ticks = 0;
            refresh.Tick += delegate { Update(); ticks++; if (ticks >= NextMinutes()) { ticks = 0; LastRefresh = RefreshAsync(); } };
            refresh.Start(); LastRefresh = RefreshAsync();
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetwork;
        }
        int NextMinutes() { double[] steps = { 30, 2, 4, 8, 15 }; return (int)Math.Round(steps[Math.Min(failures, steps.Length - 1)] * (failures == 0 ? 0.9 + 0.2 * Jitter.NextDouble() : 1)); }
        void OnNetwork(object s, EventArgs e) { try { Dispatcher.BeginInvoke(new Action(delegate { if (!WasClosed) LastRefresh = RefreshAsync(); })); } catch { } }
        protected override void OnWidgetClosed() { cancel.Cancel(); refresh.Stop(); System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetwork; }

        void Update()
        {
            double lat = cache.Lat, lon = cache.Lon; bool known = cache.Lat != 0 || cache.Lon != 0;
            if (current != null && current.HasCoords) { lat = current.Lat; lon = current.Lon; known = true; }
            if (!known) { rise.Text = "--:--"; set.Text = "--:--"; dayText.Text = "需要所在地：开启自动天气，或在 设置 → 外观与材质 手动选择区县"; sunBar.Value = 0; return; }
            var now = Now(); var t = Astronomy.Compute(lat, lon, now.Date, TimeZoneInfo.Local.GetUtcOffset(now).TotalHours);
            rise.Text = t.PolarNight ? "—" : t.PolarDay ? "极昼" : Astronomy.Clock(t.Sunrise); set.Text = t.PolarNight ? "极夜" : t.PolarDay ? "—" : Astronomy.Clock(t.Sunset);
            var p = Astronomy.DayProgress(t, now); sunBar.Value = p ?? 0; sunBar.Opacity = p.HasValue ? 1 : .35;
            dayText.Text = t.DayLength.HasValue ? "白天 " + Astronomy.Length(t.DayLength) + (p.HasValue ? "" : now < (t.Sunrise ?? now) ? " · 天还没亮" : " · 已日落") : (t.PolarDay ? "全天日照" : "全天无日照");
            place.Text = current != null ? current.Display : cache.PlaceName;
        }
        void ShowCache()
        {
            if (cache.Aqi >= 0) { DateTime up; DateTime.TryParse(cache.Updated, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out up); Show(new AirReading { Aqi = cache.Aqi, Pm25 = cache.Pm25, Pm10 = cache.Pm10, Source = cache.Source, Scale = cache.Scale, Time = cache.ObservedAt, Updated = up.ToLocalTime(), Stale = true }, cache.PlaceName); }
            else { aqiNumber.Text = "—"; aqiLevel.Text = ""; aqiDetail.Text = ""; status.Text = "正在获取…"; }
        }
        void Show(AirReading r, string placeName)
        {
            aqiNumber.Text = r.Aqi.ToString(CultureInfo.InvariantCulture); aqiLevel.Text = r.Level; aqiLevel.Foreground = new SolidColorBrush(AirLogic.LevelColor(r.Aqi)); aqiNumber.Foreground = new SolidColorBrush(AirLogic.LevelColor(r.Aqi));
            aqiDetail.Text = (r.Pm25 >= 0 ? "PM2.5 " + Math.Round(r.Pm25) + "  " : "") + (r.Pm10 >= 0 ? "PM10 " + Math.Round(r.Pm10) : "");
            status.Text = (placeName ?? "") + " · " + r.Scale + " · " + r.Source + (r.Stale ? " · 更新于 " + r.Updated.ToString("HH:mm") + "（缓存）" : r.Time.Length > 0 ? " · 观测 " + r.Time : "");
        }
        internal async Task RefreshAsync()
        {
            try
            {
                var ct = cancel.Token;
                var p = current ?? await Locate(ct).ConfigureAwait(true);
                if (p == null) { Update(); status.Text = cache.Aqi >= 0 ? status.Text : "没有位置：开启自动天气或手动选择区县"; return; }
                current = p; Update();
                var log = new List<string>();
                var r = await AirLogic.FetchAsync(p, Download, ct, log).ConfigureAwait(true);
                if (r == null) { failures++; if (cache.Aqi >= 0) ShowCache(); else status.Text = "空气质量暂不可用（" + string.Join("；", log) + "）"; return; }
                failures = 0; Show(r, p.Display);
                cache = new SunAirCache { PlaceName = p.Display, Source = r.Source, Level = r.Level, Scale = r.Scale, Updated = r.Updated.ToUniversalTime().ToString("o"), ObservedAt = r.Time, Aqi = r.Aqi, Pm25 = r.Pm25, Pm10 = r.Pm10, Lat = p.Lat, Lon = p.Lon };
                ToolStore.Save("sunair", cache);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { failures++; if (cache.Aqi < 0) status.Text = "获取失败：" + WeatherService.Reason(e); }
        }
        internal string Status { get { return status.Text; } }
        internal string AqiText { get { return aqiNumber.Text; } }
        internal string RiseText { get { return rise.Text; } }
        internal string SetText { get { return set.Text; } }
    }
}
