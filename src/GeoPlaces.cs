using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ClearDesk
{
    /// <summary>A resolved place. Kind: system (Windows location), ip (network address, approximate), manual (chosen by the user).</summary>
    public sealed class GeoPlace
    {
        public string Kind = "", Province = "", City = "", District = "", Source = "", CountryCode = "";
        public double Lat = double.NaN, Lon = double.NaN, Accuracy = double.NaN;
        public string Note = "";
        internal string Display { get { return !string.IsNullOrEmpty(District) ? District : !string.IsNullOrEmpty(City) ? City : Province; } }
        internal bool HasCoords { get { return !double.IsNaN(Lat) && !double.IsNaN(Lon) && Math.Abs(Lat) <= 90 && Math.Abs(Lon) <= 180; } }
        internal bool InChina { get { return CountryCode == "CN" || (CountryCode.Length == 0 && PlaceNames.HasAdminSuffix(Display)); } }
        internal GeoPlace Clone() { return (GeoPlace)MemberwiseClone(); }
        internal string FullName { get { return string.Join(" ", new[] { Province, City, District }.Where(x => !string.IsNullOrEmpty(x)).Distinct()); } }
    }

    /// <summary>Name handling for Chinese administrative divisions: Traditional→Simplified and county-level extraction.</summary>
    internal static class PlaceNames
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "LCMapStringEx")]
        static extern int LCMapStringEx(string localeName, uint flags, string src, int srcLength, [Out] char[] dst, int dstLength, IntPtr versionInfo, IntPtr reserved, IntPtr sortHandle);
        const uint LCMAP_SIMPLIFIED_CHINESE = 0x02000000;
        // Used when the system conversion is unavailable; also fixes characters the system table leaves alone.
        static readonly Dictionary<char, char> Fallback = new Dictionary<char, char>
        {
            {'區','区'},{'縣','县'},{'鎮','镇'},{'鄉','乡'},{'裡','里'},{'臺','台'},{'灣','湾'},{'島','岛'},{'陽','阳'},{'華','华'},{'盛','盛'},{'頓','顿'},{'倫','伦'},{'亞','亚'},{'爾','尔'},{'濱','滨'},{'陝','陕'},{'遼','辽'},{'寧','宁'},{'廣','广'},{'東','东'},{'慶','庆'},{'與','与'},{'羅','罗'},{'門','门'},{'黃','黄'},{'浦','浦'},{'賽','赛'},{'羣','群'}
        };
        internal static string Simplify(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = text;
            try
            {
                var dst = new char[s.Length * 2 + 2];
                int n = LCMapStringEx("zh-CN", LCMAP_SIMPLIFIED_CHINESE, s, s.Length, dst, dst.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (n > 0) s = new string(dst, 0, n);
            }
            catch { }
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) { char m; sb.Append(Fallback.TryGetValue(c, out m) ? m : c); }
            return sb.ToString().Trim();
        }

        static readonly Regex Segment = new Regex("(.+?)(自治区|特别行政区|自治州|自治县|自治旗|林区|特区|地区|省|市|区|县|旗|盟)", RegexOptions.Compiled);
        static readonly string[] Generic = { "市辖区", "县", "市", "区", "郊县", "城区", "城区街道" };
        internal static bool HasAdminSuffix(string s)
        {
            return !string.IsNullOrEmpty(s) && Regex.IsMatch(s, "(自治区|特别行政区|自治州|自治县|自治旗|林区|特区|地区|省|市|区|县|旗|盟)$");
        }
        /// <summary>Splits "内蒙古自治区呼和浩特市赛罕区" into its division names. Returns null when the text is not a Chinese division name.</summary>
        internal static List<string> Divisions(string text)
        {
            string s = Simplify(text).Replace(" ", "").Replace("，", "").Replace(",", "");
            if (s.StartsWith("中华人民共和国")) s = s.Substring(7); else if (s.StartsWith("中国")) s = s.Substring(2);
            if (s.Length == 0) return null;
            var list = new List<string>(); int pos = 0;
            foreach (Match m in Segment.Matches(s))
            {
                if (m.Index != pos) break;   // what follows is not a division (a street, a village…): keep what we have
                list.Add(m.Value); pos = m.Index + m.Length;
            }
            return list.Count > 0 ? list : null;
        }
        /// <summary>County-level name for display: 呼和浩特市赛罕区 → 赛罕区, 上海市黄浦区 → 黄浦区, 都江堰市 under 成都市 → 都江堰市; a city without districts (东莞市, 上海市) stays as the city.</summary>
        internal static string DistrictOf(string text)
        {
            var d = Divisions(text);
            if (d == null) return Simplify(text);
            for (int i = d.Count - 1; i >= 0; i--) if (Array.IndexOf(Generic, d[i]) < 0) return d[i];
            return d[d.Count - 1];
        }
        internal static bool IsCountyLevel(string name)
        {
            return !string.IsNullOrEmpty(name) && Regex.IsMatch(Simplify(name), "(区|县|旗|市|自治县|自治旗|林区|特区)$") && !Regex.IsMatch(name, "(街道|镇|乡|村|社区|办事处)$");
        }
        internal static string Clean(string name)
        {
            string s = Simplify(name); int p = s.IndexOf('('); if (p > 0) s = s.Substring(0, p).Trim();
            return s;
        }
        internal static bool SameCity(string a, string b)
        {
            return Stem(a).Length > 0 && Stem(a) == Stem(b);
        }
        internal static string Stem(string name)
        {
            string s = Simplify(name); if (s.Length == 0) return "";
            string t = Regex.Replace(s, "(自治区|特别行政区|自治州|地区|省|市|盟)$", "");
            return t.Length == 0 ? s : t;
        }
    }

    internal sealed class IpCandidate
    {
        public string Service = "", Country = "", Province = "", City = "", District = "";
        public double Lat = double.NaN, Lon = double.NaN;
        public bool IsChina { get { return Country == "CN"; } }
        public bool HasCoords { get { return !double.IsNaN(Lat) && !double.IsNaN(Lon) && Math.Abs(Lat) <= 90 && Math.Abs(Lon) <= 180 && !(Lat == 0 && Lon == 0); } }
        public string Key { get { return PlaceNames.Stem(string.IsNullOrEmpty(City) ? Province : City); } }
    }

    /// <summary>IP geolocation: several keyless services, a consistency vote, and the system time zone as a proxy/VPN sanity check.</summary>
    internal static class IpLocator
    {
        static Dictionary<string, object> Obj(string text) { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text); }
        static string Str(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture).Trim() : ""; }
        static double Num(Dictionary<string, object> d, string k)
        {
            double r; return double.TryParse(Str(d, k), NumberStyles.Float, CultureInfo.InvariantCulture, out r) && !double.IsNaN(r) && !double.IsInfinity(r) ? r : double.NaN;
        }
        static Dictionary<string, object> Child(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null; }
        static string Country(string code, string name)
        {
            string c = (code ?? "").Trim().ToUpperInvariant(), n = PlaceNames.Simplify(name);
            if (c == "CN" || n == "中国" || n == "中华人民共和国" || n == "China") return "CN";
            return c.Length > 0 ? c : (n.Length > 0 ? "??" : "");
        }

        internal sealed class Service
        {
            public string Name, Url; public bool Domestic; public Func<string, IpCandidate> Parse;
        }
        internal static readonly Service[] Services =
        {
            new Service { Name = "pconline", Domestic = true, Url = "https://whois.pconline.com.cn/ipJson.jsp?json=true", Parse = ParsePconline },
            new Service { Name = "ipip.net", Domestic = true, Url = "http://myip.ipip.net/json", Parse = ParseIpip },
            new Service { Name = "bilibili", Domestic = true, Url = "https://api.live.bilibili.com/ip_service/v1/ip_service/get_ip_addr", Parse = ParseBilibili },
            new Service { Name = "ipwho.is", Domestic = false, Url = "https://ipwho.is/?lang=zh-CN&fields=success,message,country_code,region,city,latitude,longitude", Parse = ParseIpwho },
            new Service { Name = "ip-api.com", Domestic = false, Url = "http://ip-api.com/json/?lang=zh-CN&fields=status,message,countryCode,regionName,city,district,lat,lon", Parse = ParseIpApi },
        };

        internal static IpCandidate ParseIpwho(string text)
        {
            var d = Obj(text); object ok; if (!d.TryGetValue("success", out ok) || !(ok is bool) || !(bool)ok) throw new InvalidOperationException(Str(d, "message").Length > 0 ? Str(d, "message") : "服务拒绝了请求");
            var c = new IpCandidate { Service = "ipwho.is", Country = Country(Str(d, "country_code"), ""), Province = PlaceNames.Clean(Str(d, "region")), City = PlaceNames.Clean(Str(d, "city")), Lat = Num(d, "latitude"), Lon = Num(d, "longitude") };
            if (c.City.Length == 0 && c.Province.Length == 0) throw new FormatException("未返回城市");
            return c;
        }
        internal static IpCandidate ParseIpApi(string text)
        {
            var d = Obj(text); if (Str(d, "status") != "success") throw new InvalidOperationException(Str(d, "message").Length > 0 ? Str(d, "message") : "服务拒绝了请求");
            var c = new IpCandidate { Service = "ip-api.com", Country = Country(Str(d, "countryCode"), ""), Province = PlaceNames.Clean(Str(d, "regionName")), City = PlaceNames.Clean(Str(d, "city")), District = PlaceNames.Clean(Str(d, "district")), Lat = Num(d, "lat"), Lon = Num(d, "lon") };
            if (c.City.Length == 0 && c.Province.Length == 0) throw new FormatException("未返回城市");
            return c;
        }
        internal static IpCandidate ParsePconline(string text)
        {
            var d = Obj(text.Trim()); if (Str(d, "err").Length > 0) throw new InvalidOperationException(Str(d, "err"));
            string pro = PlaceNames.Clean(Str(d, "pro")), city = PlaceNames.Clean(Str(d, "city")), region = PlaceNames.Clean(Str(d, "region"));
            // Outside mainland China this service returns only a country text in "addr".
            var c = new IpCandidate { Service = "pconline", Country = pro.Length + city.Length > 0 ? "CN" : (Str(d, "addr").Length > 0 ? "??" : ""), Province = pro, City = city, District = region };
            if (c.Country.Length == 0) throw new FormatException("未返回地址");
            return c;
        }
        internal static IpCandidate ParseIpip(string text)
        {
            var d = Obj(text); if (Str(d, "ret") != "ok") throw new InvalidOperationException("服务拒绝了请求");
            var data = Child(d, "data"); object loc; var arr = data != null && data.TryGetValue("location", out loc) ? loc as System.Collections.IList : null;
            if (arr == null || arr.Count < 3) throw new FormatException("未返回地址");
            Func<int, string> at = i => i < arr.Count && arr[i] != null ? PlaceNames.Clean(Convert.ToString(arr[i], CultureInfo.InvariantCulture)) : "";
            string country = Country("", at(0));
            if (country != "CN") return new IpCandidate { Service = "ipip.net", Country = country, Province = at(1), City = at(2) };
            // Hong Kong, Macau and Taiwan are reported as "中国 香港" and have no province level.
            var c = new IpCandidate { Service = "ipip.net", Country = "CN", Province = at(1), City = at(2), District = at(3) };
            if (c.Province.Length == 0 && c.City.Length == 0) throw new FormatException("未返回城市");
            return c;
        }
        internal static IpCandidate ParseBilibili(string text)
        {
            var d = Obj(text); if (Str(d, "code") != "0") throw new InvalidOperationException("服务拒绝了请求");
            var data = Child(d, "data"); if (data == null) throw new FormatException("未返回地址");
            var c = new IpCandidate { Service = "bilibili", Country = Country("", Str(data, "country")), Province = PlaceNames.Clean(Str(data, "province")), City = PlaceNames.Clean(Str(data, "city")), Lat = Num(data, "latitude"), Lon = Num(data, "longitude") };
            if (c.Province.Length == 0 && c.City.Length == 0) throw new FormatException("未返回城市");
            return c;
        }

        /// <summary>Picks the city most services agree on. Candidates outside China are discarded when the PC is set to a China time zone and at least one service says China (a foreign answer is then almost certainly the proxy/VPN exit).</summary>
        internal static IpCandidate Vote(IList<IpCandidate> all, bool expectChina, out int agreeing, out bool proxySuspected)
        {
            agreeing = 0; proxySuspected = false;
            var list = all.Where(c => c != null && (c.City.Length > 0 || c.Province.Length > 0)).ToList();
            if (list.Count == 0) return null;
            if (expectChina && list.Any(c => c.IsChina))
            {
                proxySuspected = list.Any(c => !c.IsChina);
                list = list.Where(c => c.IsChina).ToList();
            }
            var groups = list.Select((c, i) => new { c, i }).GroupBy(x => x.c.Country + "|" + x.c.Key).Select(g => new { Items = g.Select(x => x.c).ToList(), First = g.Min(x => x.i) }).ToList();
            var best = groups.OrderByDescending(g => g.Items.Count).ThenBy(g => g.First).First();
            agreeing = best.Items.Count;
            var pick = best.Items.FirstOrDefault(c => c.District.Length > 0) ?? best.Items[0];
            var win = new IpCandidate { Service = string.Join("+", best.Items.Select(c => c.Service)), Country = pick.Country, Province = best.Items.Select(c => c.Province).FirstOrDefault(p => p.Length > 0) ?? "", City = pick.City, District = pick.District };
            var withCoords = best.Items.FirstOrDefault(c => c.HasCoords);
            if (withCoords != null) { win.Lat = withCoords.Lat; win.Lon = withCoords.Lon; }
            return win;
        }

        internal sealed class Result { public IpCandidate Winner; public int Agreeing; public bool ProxySuspected; }
        /// <summary>Queries the services (domestic ones first on a China time zone, foreign first otherwise) and votes. The second group is only asked when the first one did not produce two agreeing answers.</summary>
        internal static async Task<Result> LocateAsync(Func<string, CancellationToken, Task<string>> download, bool expectChina, CancellationToken ct, List<string> log)
        {
            var first = Services.Where(s => s.Domestic == expectChina).ToList(); var second = Services.Where(s => s.Domestic != expectChina).ToList();
            var got = new List<IpCandidate>(); var r = new Result();
            foreach (var group in new[] { first, second })
            {
                var tasks = group.Select(s => Query(s, download, ct, log)).ToList();
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                got.AddRange(results.Where(x => x != null));
                int agree; bool proxy; var win = Vote(got, expectChina, out agree, out proxy);
                r.Winner = win; r.Agreeing = agree; r.ProxySuspected = proxy;
                if (win != null && agree >= 2) break;
                ct.ThrowIfCancellationRequested();
            }
            if (r.Winner == null) throw new InvalidOperationException("所有 IP 定位服务都没有返回可用结果。");
            if (log != null) log.Add("IP 定位投票：" + r.Winner.Country + " " + r.Winner.Province + " " + r.Winner.City + "（" + r.Agreeing + " 个服务一致" + (r.ProxySuspected ? "；检测到境外结果，疑似代理出口，已忽略" : "") + "）");
            return r;
        }
        static async Task<IpCandidate> Query(Service s, Func<string, CancellationToken, Task<string>> download, CancellationToken ct, List<string> log)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { var c = s.Parse(await download(s.Url, ct).ConfigureAwait(false)); Log(log, s.Name + " " + sw.ElapsedMilliseconds + "ms → " + c.Country + " " + c.Province + " " + c.City + " " + c.District); return c; }
            catch (Exception e) { if (ct.IsCancellationRequested) throw; Log(log, s.Name + " 失败 " + sw.ElapsedMilliseconds + "ms：" + WeatherService.Reason(e)); return null; }
        }
        static void Log(List<string> log, string m) { if (log != null) lock (log) log.Add(m); }
    }

    /// <summary>Reverse geocoding (coordinates → province / city / district) and place search, with fallbacks. All keyless.</summary>
    internal static class PlaceLookup
    {
        const string BigDataCloud = "https://api.bigdatacloud.net/data/reverse-geocode-client?latitude={0}&longitude={1}&localityLanguage=zh";
        const string Nominatim = "https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat={0}&lon={1}&zoom=10&addressdetails=1&accept-language=zh-CN";
        static string F(double v) { return v.ToString("0.0000", CultureInfo.InvariantCulture); }

        internal static GeoPlace ParseBigDataCloud(string text)
        {
            var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
            Func<string, string> s = k => { object v; return d.TryGetValue(k, out v) && v != null ? PlaceNames.Clean(Convert.ToString(v, CultureInfo.InvariantCulture)) : ""; };
            var p = new GeoPlace { CountryCode = s("countryCode").ToUpperInvariant(), Province = s("principalSubdivision"), City = s("city"), Source = "BigDataCloud" };
            string county = "";
            object info; Dictionary<string, object> li = d.TryGetValue("localityInfo", out info) ? info as Dictionary<string, object> : null;
            object adm; var list = li != null && li.TryGetValue("administrative", out adm) ? adm as System.Collections.IList : null;
            if (list != null)
                foreach (var item in list.OfType<Dictionary<string, object>>())
                {
                    object lv, nm; if (!item.TryGetValue("adminLevel", out lv) || !item.TryGetValue("name", out nm)) continue;
                    int level; if (!int.TryParse(Convert.ToString(lv, CultureInfo.InvariantCulture), out level)) continue;
                    string name = PlaceNames.Clean(Convert.ToString(nm, CultureInfo.InvariantCulture));
                    if (level == 6 && PlaceNames.IsCountyLevel(name)) county = name;
                }
            if (county.Length == 0) { string loc = s("locality"); if (PlaceNames.IsCountyLevel(loc)) county = loc; }
            if (p.CountryCode == "CN")
            {
                p.District = county.Length > 0 ? PlaceNames.DistrictOf(county) : "";
                if (p.City.Length == 0) p.City = p.Province;
                if (p.District == p.City) p.District = "";   // a city without districts
            }
            else { p.District = ""; if (p.City.Length == 0) p.City = s("locality"); }
            if (p.City.Length == 0 && p.District.Length == 0 && p.Province.Length == 0) throw new FormatException("反向地理编码未返回地名");
            return p;
        }
        internal static GeoPlace ParseNominatim(string text)
        {
            var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
            object disp, addr; if (!d.TryGetValue("display_name", out disp) || disp == null) throw new FormatException("反向地理编码未返回地名");
            var a = d.TryGetValue("address", out addr) ? addr as Dictionary<string, object> : null; object cc;
            var p = new GeoPlace { Source = "OpenStreetMap", CountryCode = a != null && a.TryGetValue("country_code", out cc) ? Convert.ToString(cc).ToUpperInvariant() : "" };
            FromDisplayName(p, Convert.ToString(disp));
            if (p.Display.Length == 0) throw new FormatException("反向地理编码未返回地名");
            return p;
        }
        /// <summary>"昭乌达路街道, 赛罕区, 呼和浩特市, 玉泉区, 内蒙古自治区, 010070, 中国": first county-level token is the district, first city-like token after it the city.</summary>
        internal static void FromDisplayName(GeoPlace p, string display)
        {
            var parts = display.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries).Select(x => PlaceNames.Clean(x)).Where(x => x.Length > 0).ToList();
            string district = parts.FirstOrDefault(x => Regex.IsMatch(x, "(区|县|旗)$") && !Regex.IsMatch(x, "(街道|社区)$"));
            string province = parts.FirstOrDefault(x => Regex.IsMatch(x, "(省|自治区|特别行政区)$")) ?? "";
            string city = parts.FirstOrDefault(x => Regex.IsMatch(x, "(市|自治州|地区|盟)$"));
            if (province.Length == 0) { var muni = new[] { "北京市", "上海市", "天津市", "重庆市" }; province = parts.FirstOrDefault(x => muni.Contains(x)) ?? ""; }
            if (city == null) city = province;
            if (district != null && district != city) { p.District = district; }
            p.City = city ?? ""; p.Province = province;
            if (p.District.Length == 0 && p.City.Length == 0 && parts.Count > 0) p.City = parts[0];
        }

        internal static async Task<GeoPlace> ReverseAsync(Func<string, CancellationToken, Task<string>> download, double lat, double lon, CancellationToken ct, List<string> log)
        {
            Exception first = null;
            try { var p = ParseBigDataCloud(await download(string.Format(CultureInfo.InvariantCulture, BigDataCloud, F(lat), F(lon)), ct).ConfigureAwait(false)); p.Lat = lat; p.Lon = lon; if (log != null) log.Add("反向地理编码 BigDataCloud → " + p.FullName); return p; }
            catch (Exception e) { if (ct.IsCancellationRequested) throw; first = e; if (log != null) log.Add("BigDataCloud 失败：" + WeatherService.Reason(e)); }
            try { var p = ParseNominatim(await download(string.Format(CultureInfo.InvariantCulture, Nominatim, F(lat), F(lon)), ct).ConfigureAwait(false)); p.Lat = lat; p.Lon = lon; if (log != null) log.Add("反向地理编码 OpenStreetMap → " + p.FullName); return p; }
            catch (Exception e) { if (ct.IsCancellationRequested) throw; if (log != null) log.Add("OpenStreetMap 失败：" + WeatherService.Reason(e)); }
            throw new InvalidOperationException("无法把坐标解析为地名：" + WeatherService.Reason(first));
        }

        // ---- place search (settings → 手动选择所在地) ----
        internal static List<GeoPlace> ParseOpenMeteoSearch(string text)
        {
            var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text); object res;
            var list = d.TryGetValue("results", out res) ? res as System.Collections.IList : null; var found = new List<GeoPlace>();
            if (list == null) return found;
            foreach (var item in list.OfType<Dictionary<string, object>>())
            {
                Func<string, string> s = k => { object v; return item.TryGetValue(k, out v) && v != null ? PlaceNames.Clean(Convert.ToString(v, CultureInfo.InvariantCulture)) : ""; };
                double lat, lon; if (!double.TryParse(s("latitude"), NumberStyles.Float, CultureInfo.InvariantCulture, out lat) || !double.TryParse(s("longitude"), NumberStyles.Float, CultureInfo.InvariantCulture, out lon)) continue;
                var p = new GeoPlace { Kind = "manual", Source = "Open-Meteo 地名库", CountryCode = s("country_code").ToUpperInvariant(), Lat = lat, Lon = lon, Province = s("admin1") };
                string name = s("name"), a2 = s("admin2"), a3 = s("admin3");
                if (p.CountryCode == "CN")
                {
                    // admin2 is the prefecture, admin3 the county; the entry itself may be the county ("黄浦") or a town.
                    // The caller normalises the chosen place with a reverse geocode, which returns the proper "黄浦区".
                    p.City = a2.Length > 0 ? a2 : name; p.District = a3.Length > 0 ? a3 : (a2.Length > 0 && name != a2 ? name : "");
                }
                else { p.City = name; p.District = ""; }
                found.Add(p);
            }
            return found;
        }
        internal static List<GeoPlace> ParseNominatimSearch(string text)
        {
            var arr = new JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(text); var found = new List<GeoPlace>();
            foreach (var item in arr ?? new List<Dictionary<string, object>>())
            {
                object lat, lon, disp, addr; double la, lo;
                if (!item.TryGetValue("lat", out lat) || !item.TryGetValue("lon", out lon) || !item.TryGetValue("display_name", out disp)) continue;
                if (!double.TryParse(Convert.ToString(lat, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out la) || !double.TryParse(Convert.ToString(lon, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out lo)) continue;
                var a = item.TryGetValue("address", out addr) ? addr as Dictionary<string, object> : null; object cc;
                var p = new GeoPlace { Kind = "manual", Source = "OpenStreetMap 地名库", Lat = la, Lon = lo, CountryCode = a != null && a.TryGetValue("country_code", out cc) ? Convert.ToString(cc).ToUpperInvariant() : "" };
                FromDisplayName(p, Convert.ToString(disp));
                if (p.Display.Length > 0) found.Add(p);
            }
            return found;
        }
        static string Q(string s) { return Uri.EscapeDataString(s); }
        internal static async Task<List<GeoPlace>> SearchAsync(Func<string, CancellationToken, Task<string>> download, string query, CancellationToken ct, List<string> log)
        {
            query = PlaceNames.Simplify((query ?? "").Trim());
            if (query.Length == 0) return new List<GeoPlace>();
            var tokens = PlaceNames.Divisions(query); string stem = tokens != null ? tokens[tokens.Count - 1] : query;
            string alt = tokens != null && tokens.Count >= 2 ? tokens[tokens.Count - 1] + " " + tokens[tokens.Count - 2] : query;
            var tasks = new List<Task<List<GeoPlace>>>();
            tasks.Add(Safe(async () => ParseNominatimSearch(await download("https://nominatim.openstreetmap.org/search?format=jsonv2&addressdetails=1&limit=8&accept-language=zh-CN&q=" + Q(alt), ct).ConfigureAwait(false)), log, "OpenStreetMap 搜索"));
            tasks.Add(Safe(async () => ParseOpenMeteoSearch(await download("https://geocoding-api.open-meteo.com/v1/search?count=8&language=zh&format=json&name=" + Q(Regex.Replace(stem, "(区|县|旗)$", "")), ct).ConfigureAwait(false)), log, "Open-Meteo 地名搜索"));
            var all = (await Task.WhenAll(tasks).ConfigureAwait(false)).SelectMany(x => x).ToList();
            var seen = new HashSet<string>(); var result = new List<GeoPlace>();
            foreach (var p in all.OrderByDescending(x => x.CountryCode == "CN" ? 1 : 0))
            {
                string key = p.Province + "|" + PlaceNames.Stem(p.City) + "|" + p.District;
                if (seen.Add(key)) result.Add(p);
            }
            return result.Take(8).ToList();
        }
        static async Task<List<GeoPlace>> Safe(Func<Task<List<GeoPlace>>> f, List<string> log, string name)
        {
            try { return await f().ConfigureAwait(false); }
            catch (Exception e) { if (log != null) lock (log) log.Add(name + " 失败：" + WeatherService.Reason(e)); return new List<GeoPlace>(); }
        }
    }

    /// <summary>Windows location platform (Wi-Fi / GPS / IP, uses the user's system privacy setting). Returns null when it is off, denied or has no fix.</summary>
    internal static class SystemLocation
    {
        internal static Task<GeoPlace> GetAsync(int timeoutMs, CancellationToken ct)
        {
            return Task.Run(delegate
            {
                try
                {
                    using (var w = new System.Device.Location.GeoCoordinateWatcher(System.Device.Location.GeoPositionAccuracy.Default))
                    {
                        if (!w.TryStart(false, TimeSpan.FromMilliseconds(Math.Min(timeoutMs, 3000)))) return null;
                        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                        while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
                        {
                            if (w.Permission == System.Device.Location.GeoPositionPermission.Denied || w.Status == System.Device.Location.GeoPositionStatus.Disabled) return null;
                            var l = w.Position.Location;
                            if (w.Status == System.Device.Location.GeoPositionStatus.Ready && !l.IsUnknown && !double.IsNaN(l.Latitude) && !double.IsNaN(l.Longitude))
                                return new GeoPlace { Kind = "system", Source = "Windows 定位", Lat = Math.Round(l.Latitude, 4), Lon = Math.Round(l.Longitude, 4), Accuracy = l.HorizontalAccuracy };
                            Thread.Sleep(150);
                        }
                    }
                }
                catch { }
                return null;
            }, ct);
        }
    }
}
