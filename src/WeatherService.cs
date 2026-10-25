using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ClearDesk
{
    internal sealed class WeatherReading
    {
        internal string City, Time, Source = "", PlaceSource = "";
        internal double Temperature, Humidity, Wind;
        internal int Code;
        internal DateTime Received;           // UTC, when it was fetched
        internal DateTime Updated;            // local time shown as 「更新于 HH:mm」
        internal bool Stale;                  // loaded from the cache, not refreshed yet
        internal GeoPlace Place;
        internal List<ForecastDay> Days = new List<ForecastDay>();
    }
    internal sealed class ForecastDay
    {
        internal DateTime Date;
        internal int Code;
        internal double Max, Min;
    }
    internal interface IWeatherService
    {
        Task<WeatherReading> ReadAsync(bool force, CancellationToken cancellation);
        /// <summary>Last good reading (from this run or the on-disk cache), marked Stale until refreshed. Null when there is none.</summary>
        WeatherReading Cached { get; }
        /// <summary>The network or the proxy changed (or the PC woke up): forget the approximate location and fetch again soon.</summary>
        void NetworkChanged();
        /// <summary>One line per step of the last refresh (no URLs, no coordinates), for the tooltip and diagnostics.</summary>
        string LastReport { get; }
    }
    internal sealed class WeatherException : Exception
    {
        internal readonly List<string> Reasons;
        internal WeatherException(string message, List<string> reasons) : base(message) { Reasons = reasons ?? new List<string>(); }
    }

    // ---- on-disk cache of the last good weather + place (coordinates rounded to ~1 km, never the IP address) ----
    public sealed class WeatherCacheDay { public string Date = ""; public int Code; public double Max, Min; }
    public sealed class WeatherCacheData
    {
        public string Source = "", Time = "", Updated = "", Kind = "", Province = "", City = "", District = "", Country = "", PlaceSource = "", Coords = "";
        public double Temperature, Humidity, Wind; public int Code; public bool HasReading;
        public List<WeatherCacheDay> Days = new List<WeatherCacheDay>();
        public string PlaceAt = "";
    }
    internal interface IWeatherStore { WeatherCacheData Load(); void Save(WeatherCacheData data); }
    internal sealed class MemoryWeatherStore : IWeatherStore
    {
        internal WeatherCacheData Data;
        public WeatherCacheData Load() { return Data; }
        public void Save(WeatherCacheData data) { Data = data; }
    }
    internal sealed class FileWeatherStore : IWeatherStore
    {
        static string FilePath { get { return Path.Combine(Program.Data, "weather-cache.json"); } }
        public WeatherCacheData Load()
        {
            try { return File.Exists(FilePath) ? new JavaScriptSerializer().Deserialize<WeatherCacheData>(File.ReadAllText(FilePath)) : null; } catch { return null; }
        }
        public void Save(WeatherCacheData data)
        {
            try { Directory.CreateDirectory(Program.Data); string tmp = FilePath + ".tmp"; File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(data)); if (File.Exists(FilePath)) File.Delete(FilePath); File.Move(tmp, FilePath); } catch { }
        }
    }

    internal sealed class WeatherProvider
    {
        internal string Name; internal bool NeedsCoords, NeedsNames;
        internal Func<GeoPlace, string> Url, Url2; internal Func<GeoPlace, string, WeatherReading> Parse;   // Url2: coarser retry when the first answer has no data for that place
    }

    internal sealed class WeatherService : IWeatherService
    {
        internal static readonly WeatherService Shared = new WeatherService(null, null, new FileWeatherStore());
        internal static int RequestTimeoutMs = 8000, HedgeMs = 3500, SystemLocationTimeoutMs = 6000;
        internal static readonly TimeSpan Fresh = TimeSpan.FromMinutes(20), SystemPlaceLife = TimeSpan.FromHours(3), IpPlaceLife = TimeSpan.FromHours(6);
        static volatile HttpClient client = MakeClient();
        readonly Func<string, CancellationToken, Task<string>> download;
        readonly Func<CancellationToken, Task<GeoPlace>> systemLocator;
        readonly IWeatherStore store;
        readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        internal Func<GeoPlace> ManualSource = delegate { return Program.S == null ? null : Program.S.ManualPlace(); };
        internal Func<bool> UseSystemLocation = delegate { return Program.S == null || Program.S.WeatherSystemLocation; };
        internal Func<bool> ExpectChina = delegate { return TimeZoneInfo.Local.Id == "China Standard Time"; };
        WeatherReading cached; GeoPlace place; DateTime placeAt = DateTime.MinValue; bool networkDirty, loaded;
        volatile string report = "";
        public string LastReport { get { return report; } }
        internal GeoPlace CurrentPlace { get { return place; } }

        internal WeatherService(Func<string, CancellationToken, Task<string>> transport = null, Func<CancellationToken, Task<GeoPlace>> locator = null, IWeatherStore cache = null)
        {
            download = transport ?? Download;
            systemLocator = locator ?? (ct => SystemLocation.GetAsync(SystemLocationTimeoutMs, ct));
            store = cache ?? new MemoryWeatherStore();
        }

        // ---------------- transport ----------------
        static HttpClient MakeClient()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; ServicePointManager.DnsRefreshTimeout = 30000; } catch { }
            // UseProxy with the default proxy follows the Windows / browser proxy setting live (Clash, V2Ray… in system-proxy mode).
            try { var wp = WebRequest.DefaultWebProxy; if (wp != null && wp.Credentials == null) wp.Credentials = CredentialCache.DefaultCredentials; } catch { }
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = true, AutomaticDecompression = DecompressionMethods.GZip };   // not Deflate: .NET Framework mis-reads zlib-wrapped deflate ("块的长度与它的补集不匹配")
            var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("ClearDesk/" + UpdateService.Version + " (+https://github.com/visual-life/ClearDesk)");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
            return c;
        }
        internal static async Task<string> Download(string url, CancellationToken cancellation)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(RequestTimeoutMs);
                try
                {
                    using (var response = await client.GetAsync(url, timeout.Token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode) throw new IOException("HTTP " + (int)response.StatusCode);
                        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new TimeoutException("超时"); }
            }
        }
        /// <summary>Short Chinese reason for an error, without URLs or coordinates.</summary>
        internal static string Reason(Exception e)
        {
            for (var x = e; x != null; x = x.InnerException)
            {
                var we = x as WebException;
                if (we != null)
                    switch (we.Status)
                    {
                        case WebExceptionStatus.NameResolutionFailure: return "DNS 解析失败";
                        case WebExceptionStatus.ProxyNameResolutionFailure: return "代理不可用";
                        case WebExceptionStatus.ConnectFailure: return "无法连接";
                        case WebExceptionStatus.Timeout: return "超时";
                        case WebExceptionStatus.TrustFailure: case WebExceptionStatus.SecureChannelFailure: return "TLS 握手失败";
                        case WebExceptionStatus.ConnectionClosed: case WebExceptionStatus.ReceiveFailure: case WebExceptionStatus.SendFailure: return "连接被中断";
                    }
            }
            var t = e;
            while (t.InnerException != null && (t is AggregateException || t is System.Net.Http.HttpRequestException)) t = t.InnerException;
            if (t is TimeoutException || t is OperationCanceledException) return "超时";
            if (t is FormatException || t is InvalidCastException || t is ArgumentException || t is KeyNotFoundException) return "返回数据异常";
            string m = t.Message ?? t.GetType().Name; if (m.Length > 60) m = m.Substring(0, 60);
            return m.Replace('\r', ' ').Replace('\n', ' ');
        }

        // ---------------- parsing helpers ----------------
        static Dictionary<string, object> Json(string text) { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text); }
        internal static double Number(Dictionary<string, object> json, string key)
        {
            object value; double number;
            if (!json.TryGetValue(key, out value) || value == null || !double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number) || double.IsNaN(number) || double.IsInfinity(number)) throw new FormatException("天气服务返回了不完整的数据。");
            return number;
        }
        static object At(object node, params string[] path)
        {
            foreach (var k in path) { var d = node as Dictionary<string, object>; object v; if (d == null || !d.TryGetValue(k, out v)) return null; node = v; }
            return node;
        }
        static double? Opt(object v)
        {
            double r; return v != null && double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out r) && !double.IsNaN(r) && !double.IsInfinity(r) ? (double?)r : null;
        }
        static void CheckRange(WeatherReading r)
        {
            if (r.Temperature < -90 || r.Temperature > 70 || r.Humidity < 0 || r.Humidity > 100 || r.Wind < 0 || r.Wind > 400) throw new FormatException("天气服务返回了不合理的数据。");
        }

        // ---- Open-Meteo ----
        internal static WeatherReading Parse(string city, string text)
        {
            var json = Json(text);
            object current; if (!json.TryGetValue("current", out current) || !(current is Dictionary<string, object>)) throw new FormatException("天气服务未返回当前天气。");
            var data = (Dictionary<string, object>)current;
            var reading = new WeatherReading { City = city, Temperature = Number(data, "temperature_2m"), Humidity = Number(data, "relative_humidity_2m"), Wind = Number(data, "wind_speed_10m"), Code = (int)Number(data, "weather_code"), Received = DateTime.UtcNow, Source = "Open-Meteo" };
            object time; DateTime observed;
            if (!data.TryGetValue("time", out time) || !DateTime.TryParse(Convert.ToString(time, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.None, out observed)) throw new FormatException("天气服务未返回观测时间。");
            reading.Time = observed.ToString("MM-dd HH:mm");
            CheckRange(reading);
            try { reading.Days = Daily(json); } catch { reading.Days = new List<ForecastDay>(); }
            return reading;
        }
        // Optional multi-day forecast; shown when the weather widget is made taller.
        internal static List<ForecastDay> Daily(Dictionary<string, object> json)
        {
            var days = new List<ForecastDay>(); object daily;
            if (!json.TryGetValue("daily", out daily) || !(daily is Dictionary<string, object>)) return days;
            var d = (Dictionary<string, object>)daily; object t, c, hi, lo;
            if (!d.TryGetValue("time", out t) || !d.TryGetValue("weather_code", out c) || !d.TryGetValue("temperature_2m_max", out hi) || !d.TryGetValue("temperature_2m_min", out lo)) return days;
            var ta = t as System.Collections.IList; var ca = c as System.Collections.IList; var ha = hi as System.Collections.IList; var la = lo as System.Collections.IList;
            if (ta == null || ca == null || ha == null || la == null) return days;
            int n = Math.Min(Math.Min(ta.Count, ca.Count), Math.Min(ha.Count, la.Count));
            for (int i = 0; i < n && i < 7; i++)
            {
                DateTime date; double code, max, min;
                if (!DateTime.TryParse(Convert.ToString(ta[i], CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) continue;
                if (!double.TryParse(Convert.ToString(ca[i], CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out code)) continue;
                if (!double.TryParse(Convert.ToString(ha[i], CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out max)) continue;
                if (!double.TryParse(Convert.ToString(la[i], CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out min)) continue;
                days.Add(new ForecastDay { Date = date.Date, Code = (int)code, Max = max, Min = min });
            }
            return days;
        }

        // ---- Tencent weather (wis.qq.com, keyless, takes province / city / county names) ----
        static readonly Dictionary<string, int> TencentCodes = new Dictionary<string, int>
        {
            {"00",0},{"01",2},{"02",3},{"03",80},{"04",95},{"05",96},{"06",68},{"07",61},{"08",63},{"09",65},{"10",65},{"11",65},{"12",65},{"13",85},{"14",71},{"15",73},{"16",75},{"17",75},
            {"18",45},{"19",66},{"20",101},{"21",63},{"22",65},{"23",65},{"24",65},{"25",65},{"26",73},{"27",75},{"28",75},{"29",101},{"30",101},{"31",101},{"32",45},{"33",73},{"49",45},{"53",102},
            {"54",102},{"55",102},{"56",102},{"57",45},{"58",45},{"301",63},{"302",73}
        };
        internal static int TencentCode(string code)
        {
            int c; return code != null && TencentCodes.TryGetValue(code.Trim(), out c) ? c : 3;
        }
        // Wind "level" (Beaufort) text such as "3-4" → km/h.
        internal static double BeaufortKmh(string text)
        {
            var m = Regex.Matches(text ?? "", "\\d+"); if (m.Count == 0) return 0;
            double level = m.Cast<Match>().Average(x => double.Parse(x.Value, CultureInfo.InvariantCulture));
            return Math.Round(0.836 * Math.Pow(level, 1.5) * 3.6, 1);
        }
        internal static WeatherReading ParseTencent(GeoPlace place, string text, DateTime today)
        {
            var json = Json(text);
            if (Convert.ToString(At(json, "status"), CultureInfo.InvariantCulture) != "200") throw new FormatException("腾讯天气未返回数据。");
            var obs = At(json, "data", "observe") as Dictionary<string, object>; if (obs == null || obs.Count == 0) throw new FormatException("腾讯天气未返回当前天气。");
            var r = new WeatherReading { City = place.Display, Source = "腾讯天气", Received = DateTime.UtcNow };
            r.Temperature = Opt(At(obs, "degree")) ?? Number(obs, "degree");
            r.Humidity = Opt(At(obs, "humidity")) ?? 0;
            r.Wind = BeaufortKmh(Convert.ToString(At(obs, "wind_power"), CultureInfo.InvariantCulture));
            r.Code = TencentCode(Convert.ToString(At(obs, "weather_code"), CultureInfo.InvariantCulture));
            DateTime t; string ut = Convert.ToString(At(obs, "update_time"), CultureInfo.InvariantCulture);
            r.Time = DateTime.TryParseExact(ut, "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out t) ? t.ToString("MM-dd HH:mm") : DateTime.Now.ToString("MM-dd HH:mm");
            CheckRange(r);
            var f = At(json, "data", "forecast_24h") as Dictionary<string, object>;
            if (f != null)
                foreach (var item in f.Values.OfType<Dictionary<string, object>>())
                {
                    DateTime date; if (!DateTime.TryParseExact(Convert.ToString(At(item, "time"), CultureInfo.InvariantCulture), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) || date < today.Date) continue;
                    double? hi = Opt(At(item, "max_degree")), lo = Opt(At(item, "min_degree")); if (hi == null || lo == null) continue;
                    string code = Convert.ToString(At(item, "day_weather_code"), CultureInfo.InvariantCulture); if (string.IsNullOrEmpty(code)) code = Convert.ToString(At(item, "night_weather_code"), CultureInfo.InvariantCulture);
                    r.Days.Add(new ForecastDay { Date = date, Code = TencentCode(code), Max = hi.Value, Min = lo.Value });
                }
            r.Days = r.Days.OrderBy(d => d.Date).Take(7).ToList();
            return r;
        }

        // ---- met.no (Norwegian Meteorological Institute, keyless, needs an identifying User-Agent) ----
        internal static int MetNoCode(string symbol)
        {
            string s = (symbol ?? "").ToLowerInvariant(); int us = s.IndexOf('_'); if (us > 0) s = s.Substring(0, us);
            if (s.Contains("thunder")) return 95;
            if (s.Contains("sleet")) return 68;
            if (s.Contains("snowshowers")) return 85;
            if (s.Contains("snow")) return s.StartsWith("light") ? 71 : s.StartsWith("heavy") ? 75 : 73;
            if (s.Contains("rainshowers")) return 80;
            if (s.Contains("rain")) return s.StartsWith("light") ? 61 : s.StartsWith("heavy") ? 65 : 63;
            if (s == "fog") return 45;
            if (s == "clearsky" || s == "fair") return 0;
            if (s == "partlycloudy") return 2;
            if (s == "cloudy") return 3;
            return 3;
        }
        internal static double LocalOffsetHours(GeoPlace place)
        {
            double nominal = Math.Round(place.Lon / 15.0), local = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalHours;
            return Math.Abs(nominal - local) <= 1 ? local : nominal;
        }
        internal static WeatherReading ParseMetNo(GeoPlace place, string text, double offsetHours)
        {
            var json = Json(text); var series = At(json, "properties", "timeseries") as System.Collections.IList;
            if (series == null || series.Count == 0) throw new FormatException("met.no 未返回预报。");
            var rows = new List<Tuple<DateTime, double, double?, double?, string>>();  // local time, temp, humidity, wind m/s, symbol
            foreach (var item in series)
            {
                DateTime utc; if (!DateTime.TryParse(Convert.ToString(At(item, "time"), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc)) continue;
                double? temp = Opt(At(item, "data", "instant", "details", "air_temperature")); if (temp == null) continue;
                string sym = Convert.ToString(At(item, "data", "next_1_hours", "summary", "symbol_code") ?? At(item, "data", "next_6_hours", "summary", "symbol_code") ?? At(item, "data", "next_12_hours", "summary", "symbol_code"), CultureInfo.InvariantCulture);
                rows.Add(Tuple.Create(utc.AddHours(offsetHours), temp.Value, Opt(At(item, "data", "instant", "details", "relative_humidity")), Opt(At(item, "data", "instant", "details", "wind_speed")), sym ?? ""));
            }
            if (rows.Count == 0) throw new FormatException("met.no 未返回温度。");
            var now = rows[0];
            var r = new WeatherReading { City = place.Display, Source = "met.no", Received = DateTime.UtcNow, Temperature = now.Item2, Humidity = now.Item3 ?? 0, Wind = Math.Round((now.Item4 ?? 0) * 3.6, 1), Code = MetNoCode(now.Item5), Time = now.Item1.ToString("MM-dd HH:mm") };
            CheckRange(r);
            foreach (var g in rows.GroupBy(x => x.Item1.Date).OrderBy(g => g.Key).Take(8))
            {
                if (g.Count() < 2 && r.Days.Count > 0) continue;
                var noon = g.Where(x => x.Item5.Length > 0).OrderBy(x => Math.Abs(x.Item1.Hour - 12)).FirstOrDefault();
                r.Days.Add(new ForecastDay { Date = g.Key, Code = noon == null ? r.Code : MetNoCode(noon.Item5), Max = g.Max(x => x.Item2), Min = g.Min(x => x.Item2) });
            }
            r.Days = r.Days.Take(7).ToList();
            return r;
        }

        // ---- wttr.in (keyless; 3-day forecast) ----
        internal static int WwoCode(int c)
        {
            if (c == 113) return 0; if (c == 116) return 2; if (c == 119 || c == 122) return 3;
            if (c == 143 || c == 248 || c == 260) return 45;
            if (new[] { 200, 386, 389, 392, 395 }.Contains(c)) return 95;
            if (new[] { 263, 266, 281, 284 }.Contains(c)) return 51;
            if (new[] { 176, 293, 296, 353 }.Contains(c)) return c == 353 ? 80 : 61;
            if (new[] { 299, 302, 305, 308, 356, 359 }.Contains(c)) return c >= 356 ? 82 : 63;
            if (new[] { 311, 314, 185 }.Contains(c)) return 66;
            if (new[] { 179, 227, 230, 323, 326, 329, 332, 335, 338, 368, 371, 350, 374, 377 }.Contains(c)) return c == 179 || c == 323 || c == 326 || c == 368 ? 71 : c == 230 || c == 335 || c == 338 || c == 371 ? 75 : 73;
            if (new[] { 182, 317, 320, 362, 365 }.Contains(c)) return 68;
            return 3;
        }
        internal static WeatherReading ParseWttr(GeoPlace place, string text)
        {
            var json = Json(text); var cur = (At(json, "current_condition") as System.Collections.IList);
            var c0 = cur != null && cur.Count > 0 ? cur[0] as Dictionary<string, object> : null; if (c0 == null) throw new FormatException("wttr.in 未返回当前天气。");
            var r = new WeatherReading { City = place.Display, Source = "wttr.in", Received = DateTime.UtcNow, Temperature = Number(c0, "temp_C"), Humidity = Number(c0, "humidity"), Wind = Number(c0, "windspeedKmph"), Code = WwoCode((int)Number(c0, "weatherCode")) };
            DateTime t; r.Time = DateTime.TryParse(Convert.ToString(c0.ContainsKey("localObsDateTime") ? c0["localObsDateTime"] : "", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.None, out t) ? t.ToString("MM-dd HH:mm") : DateTime.Now.ToString("MM-dd HH:mm");
            CheckRange(r);
            var days = At(json, "weather") as System.Collections.IList;
            if (days != null)
                foreach (var d in days.OfType<Dictionary<string, object>>())
                {
                    DateTime date; double? hi = Opt(At(d, "maxtempC")), lo = Opt(At(d, "mintempC"));
                    if (!DateTime.TryParse(Convert.ToString(At(d, "date"), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.None, out date) || hi == null || lo == null) continue;
                    int code = r.Code; var hourly = At(d, "hourly") as System.Collections.IList;
                    if (hourly != null) { var noon = hourly.OfType<Dictionary<string, object>>().FirstOrDefault(h => Convert.ToString(At(h, "time"), CultureInfo.InvariantCulture) == "1200"); if (noon != null && Opt(At(noon, "weatherCode")) != null) code = WwoCode((int)Opt(At(noon, "weatherCode")).Value); }
                    r.Days.Add(new ForecastDay { Date = date.Date, Code = code, Max = hi.Value, Min = lo.Value });
                }
            return r;
        }

        // ---------------- provider list ----------------
        static string F2(double v) { return Math.Round(v, 2).ToString("0.00", CultureInfo.InvariantCulture); }
        static string Esc(string s) { return Uri.EscapeDataString(s ?? ""); }
        internal static readonly WeatherProvider OpenMeteo = new WeatherProvider
        {
            Name = "Open-Meteo", NeedsCoords = true,
            Url = p => "https://api.open-meteo.com/v1/forecast?latitude=" + F2(p.Lat) + "&longitude=" + F2(p.Lon) + "&current=temperature_2m,relative_humidity_2m,weather_code,wind_speed_10m&daily=weather_code,temperature_2m_max,temperature_2m_min&forecast_days=7&timezone=auto",
            Parse = (p, t) => Parse(p.Display, t)
        };
        internal static readonly WeatherProvider Tencent = new WeatherProvider
        {
            Name = "腾讯天气", NeedsNames = true,
            Url = p => "https://wis.qq.com/weather/common?source=pc&weather_type=observe%7Cforecast_24h&province=" + Esc(string.IsNullOrEmpty(p.Province) ? p.City : p.Province) + "&city=" + Esc(string.IsNullOrEmpty(p.City) ? p.Province : p.City) + "&county=" + Esc(p.District),
            Url2 = p => p.District.Length > 0 && p.City.Length > 0 ? "https://wis.qq.com/weather/common?source=pc&weather_type=observe%7Cforecast_24h&province=" + Esc(string.IsNullOrEmpty(p.Province) ? p.City : p.Province) + "&city=" + Esc(p.City) + "&county=" : null,
            Parse = (p, t) => ParseTencent(p, t, DateTime.Today)
        };
        internal static readonly WeatherProvider MetNo = new WeatherProvider
        {
            Name = "met.no", NeedsCoords = true,
            Url = p => "https://api.met.no/weatherapi/locationforecast/2.0/compact?lat=" + F2(p.Lat) + "&lon=" + F2(p.Lon),
            Parse = (p, t) => ParseMetNo(p, t, LocalOffsetHours(p))
        };
        internal static readonly WeatherProvider Wttr = new WeatherProvider
        {
            Name = "wttr.in", NeedsCoords = true,
            Url = p => "https://wttr.in/" + F2(p.Lat) + "," + F2(p.Lon) + "?format=j1",
            Parse = (p, t) => ParseWttr(p, t)
        };
        /// <summary>China: Tencent first (domestic, fast, real station data), then the global sources. Elsewhere: Open-Meteo first.</summary>
        internal static List<WeatherProvider> ProvidersFor(GeoPlace p)
        {
            var list = p.InChina ? new List<WeatherProvider> { Tencent, OpenMeteo, MetNo, Wttr } : new List<WeatherProvider> { OpenMeteo, MetNo, Wttr, Tencent };
            return list.Where(x => (!x.NeedsCoords || p.HasCoords) && (!x.NeedsNames || (p.InChina && (p.District.Length + p.City.Length + p.Province.Length) > 0))).ToList();
        }

        // ---------------- location ----------------
        internal void ForgetPlace() { place = null; placeAt = DateTime.MinValue; }
        /// <summary>The user changed the manual location or the location settings: the next refresh starts from scratch.</summary>
        internal void PlaceChanged() { ForgetPlace(); networkDirty = true; }
        public void NetworkChanged()
        {
            networkDirty = true;
            if (place != null && place.Kind != "manual") placeAt = DateTime.MinValue;   // re-locate on the next refresh
            client = MakeClient();                                                      // drop connections bound to the old network / proxy
        }
        void Ensure()
        {
            if (loaded) return; loaded = true;
            var d = store.Load(); if (d == null) return;
            var p = new GeoPlace { Kind = d.Kind, Province = d.Province, City = d.City, District = d.District, CountryCode = d.Country, Source = d.PlaceSource };
            var c = (d.Coords ?? "").Split(','); double la, lo;
            if (c.Length == 2 && double.TryParse(c[0], NumberStyles.Float, CultureInfo.InvariantCulture, out la) && double.TryParse(c[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lo)) { p.Lat = la; p.Lon = lo; }
            DateTime at; if (p.Display.Length > 0 || p.HasCoords) { place = p; placeAt = DateTime.TryParse(d.PlaceAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at) ? at : DateTime.MinValue; }
            DateTime up;
            if (d.HasReading && DateTime.TryParse(d.Updated, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out up))
            {
                var r = new WeatherReading { City = p.Display, Source = d.Source, Time = d.Time, Temperature = d.Temperature, Humidity = d.Humidity, Wind = d.Wind, Code = d.Code, Updated = up.ToLocalTime(), Received = up.ToUniversalTime(), Stale = true, Place = place, PlaceSource = d.PlaceSource };
                foreach (var day in d.Days) { DateTime dd; if (DateTime.TryParse(day.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out dd)) r.Days.Add(new ForecastDay { Date = dd.Date, Code = day.Code, Max = day.Max, Min = day.Min }); }
                cached = r;
            }
        }
        public WeatherReading Cached { get { Ensure(); return cached; } }
        void Persist()
        {
            var d = new WeatherCacheData();
            if (place != null) { d.Kind = place.Kind; d.Province = place.Province; d.City = place.City; d.District = place.District; d.Country = place.CountryCode; d.PlaceSource = place.Source; d.Coords = place.HasCoords ? F2(place.Lat) + "," + F2(place.Lon) : ""; d.PlaceAt = placeAt.ToString("o", CultureInfo.InvariantCulture); }
            if (cached != null)
            {
                d.HasReading = true; d.Source = cached.Source; d.Time = cached.Time; d.Updated = cached.Updated.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture); d.Temperature = cached.Temperature; d.Humidity = cached.Humidity; d.Wind = cached.Wind; d.Code = cached.Code;
                d.Days = cached.Days.Select(x => new WeatherCacheDay { Date = x.Date.ToString("yyyy-MM-dd"), Code = x.Code, Max = x.Max, Min = x.Min }).ToList();
            }
            store.Save(d);
        }

        async Task<GeoPlace> ResolvePlaceAsync(bool force, CancellationToken ct, List<string> log)
        {
            Ensure();
            var manual = ManualSource();
            if (manual != null) { manual = manual.Clone(); manual.Kind = "manual"; if (manual.Source.Length == 0) manual.Source = "手动选择"; place = manual; placeAt = DateTime.UtcNow; log.Add("使用手动选择的所在地：" + manual.Display); return manual; }
            if (!force && place != null && place.Kind != "manual")
            {
                var life = place.Kind == "system" ? SystemPlaceLife : IpPlaceLife;
                if (DateTime.UtcNow - placeAt < life) return place;
            }
            GeoPlace found = null; string note = "";
            if (UseSystemLocation())
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var sys = await systemLocator(ct).ConfigureAwait(false);
                    if (sys != null && sys.HasCoords && (double.IsNaN(sys.Accuracy) || sys.Accuracy <= 50000))
                    {
                        log.Add("Windows 定位成功 " + sw.ElapsedMilliseconds + "ms" + (double.IsNaN(sys.Accuracy) ? "" : "，精度约 " + Math.Round(sys.Accuracy) + " 米"));
                        try
                        {
                            var rev = await PlaceLookup.ReverseAsync(download, sys.Lat, sys.Lon, ct, log).ConfigureAwait(false);
                            rev.Kind = "system"; rev.Source = "Windows 定位"; rev.Accuracy = sys.Accuracy; rev.Lat = sys.Lat; rev.Lon = sys.Lon; found = rev;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception e)
                        {
                            log.Add("坐标无法解析为地名：" + Reason(e));
                            // keep the previous names when the new fix is close to the old place, otherwise use coordinates only
                            found = sys; if (place != null && place.HasCoords && Math.Abs(place.Lat - sys.Lat) < 0.2 && Math.Abs(place.Lon - sys.Lon) < 0.2) { found.Province = place.Province; found.City = place.City; found.District = place.District; found.CountryCode = place.CountryCode; }
                        }
                    }
                    else log.Add("Windows 定位不可用（系统未开启定位、未允许桌面应用或没有定位数据），改用网络定位。");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { log.Add("Windows 定位出错：" + Reason(e)); }
            }
            if (found == null)
            {
                try
                {
                    var res = await IpLocator.LocateAsync(download, ExpectChina(), ct, log).ConfigureAwait(false); var w = res.Winner;
                    found = new GeoPlace { Kind = "ip", Source = "网络 IP（近似）", CountryCode = w.Country == "CN" ? "CN" : (w.Country == "??" ? "" : w.Country), Province = w.Province, City = w.City, Lat = w.Lat, Lon = w.Lon };
                    if (w.District.Length > 0 && PlaceNames.IsCountyLevel(w.District)) found.District = PlaceNames.DistrictOf(w.District);
                    if (res.ProxySuspected) found.Note = "检测到代理/VPN：已忽略境外 IP 定位结果。";
                    if (!found.HasCoords)
                    {
                        try
                        {
                            var hits = await PlaceLookup.SearchAsync(download, w.City.Length > 0 ? w.City : w.Province, ct, log).ConfigureAwait(false);
                            var hit = hits.FirstOrDefault(h => h.CountryCode == "CN" && h.HasCoords && PlaceNames.SameCity(h.City, found.City)) ?? hits.FirstOrDefault(h => h.CountryCode == "CN" && h.HasCoords);
                            if (hit != null) { found.Lat = hit.Lat; found.Lon = hit.Lon; }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    note = Reason(e); log.Add("网络定位失败：" + note);
                    if (place != null) { log.Add("沿用上次的位置：" + place.Display); return place; }
                    throw new WeatherException("无法确定所在地（" + note + "）。可在 设置 → 外观与材质 手动选择所在地。", log);
                }
            }
            place = found; placeAt = DateTime.UtcNow;
            return found;
        }

        // ---------------- weather ----------------
        sealed class Attempt { internal string Name; internal WeatherReading Reading; internal string Error; }
        /// <summary>One provider: first its exact URL; if the answer is empty for this place (Tencent knows 土右旗 but not 土默特右旗) and a coarser URL exists, that one is tried.</summary>
        internal static async Task<WeatherReading> Fetch(WeatherProvider p, GeoPlace pl, Func<string, CancellationToken, Task<string>> dl, CancellationToken ct)
        {
            string text = await dl(p.Url(pl), ct).ConfigureAwait(false);
            try { var r = p.Parse(pl, text); r.Source = p.Name; return r; }
            catch (Exception e) when (p.Url2 != null && p.Url2(pl) != null && (e is FormatException || e is KeyNotFoundException || e is InvalidCastException || e is ArgumentException))
            {
                text = await dl(p.Url2(pl), ct).ConfigureAwait(false);
                var r = p.Parse(pl, text); r.Source = p.Name + "（市级）"; return r;
            }
        }
        async Task<Attempt> Run(WeatherProvider p, GeoPlace pl, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(RequestTimeoutMs + 500);
                    var r = await Fetch(p, pl, download, timeout.Token).ConfigureAwait(false);
                    return new Attempt { Name = p.Name, Reading = r, Error = p.Name + " 成功 " + sw.ElapsedMilliseconds + "ms" };
                }
            }
            catch (Exception e) { return new Attempt { Name = p.Name, Error = p.Name + " 失败（" + (ct.IsCancellationRequested ? "已取消" : Reason(e)) + "，" + sw.ElapsedMilliseconds + "ms）" }; }
        }
        /// <summary>Tries the providers in order. If one is merely slow, the next one is started after HedgeMs without cancelling it; the first valid answer wins.</summary>
        async Task<WeatherReading> FetchWeatherAsync(GeoPlace pl, CancellationToken ct, List<string> log)
        {
            var list = ProvidersFor(pl);
            if (list.Count == 0) throw new WeatherException("没有可用的天气来源：缺少位置坐标和地名。", log);
            var pending = new List<Task<Attempt>>(); int next = 0; int failed = 0;
            using (var all = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    if (next < list.Count) pending.Add(Run(list[next++], pl, all.Token));
                    if (pending.Count == 0) break;
                    var waits = new List<Task>(pending); CancellationTokenSource timerCts = null;
                    if (next < list.Count) { timerCts = CancellationTokenSource.CreateLinkedTokenSource(all.Token); waits.Add(Task.Delay(HedgeMs, timerCts.Token)); }
                    await Task.WhenAny(waits).ConfigureAwait(false);
                    if (timerCts != null) { timerCts.Cancel(); timerCts.Dispose(); }
                    foreach (var t in pending.Where(x => x.IsCompleted).ToList())
                    {
                        pending.Remove(t); var a = await t.ConfigureAwait(false); log.Add(a.Error);
                        if (a.Reading != null) { all.Cancel(); return a.Reading; }
                        failed++;
                    }
                }
            }
            ct.ThrowIfCancellationRequested();
            throw new WeatherException("所有天气来源（" + failed + " 个）都失败了。", log);
        }

        public async Task<WeatherReading> ReadAsync(bool force, CancellationToken cancellation)
        {
            await gate.WaitAsync(cancellation).ConfigureAwait(false);
            var log = new List<string>();
            try
            {
                Ensure();
                if (!force && !networkDirty && cached != null && !cached.Stale && DateTime.UtcNow - cached.Received < Fresh) return cached;
                var pl = await ResolvePlaceAsync(force, cancellation, log).ConfigureAwait(false);
                var reading = await FetchWeatherAsync(pl, cancellation, log).ConfigureAwait(false);
                reading.Place = pl; reading.City = pl.Display.Length > 0 ? pl.Display : "所在位置"; reading.PlaceSource = pl.Source; reading.Updated = DateTime.Now; reading.Received = DateTime.UtcNow; reading.Stale = false;
                cached = reading; networkDirty = false; Persist();
                return reading;
            }
            catch (WeatherException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { throw new WeatherException(Reason(e), log); }
            finally { report = string.Join("\n", log); gate.Release(); }
        }
    }
}
