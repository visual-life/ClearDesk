using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClearDesk
{
    // 2.1.1: stable weather — place names (county level, Traditional → Simplified), IP vote, fallbacks, cache, location sources.
    // Everything runs offline against fixtures captured from the real services (names trimmed to what the parsers read).
    internal static class V211Tests
    {
        static string J(string s) { return s.Replace('\'', '"'); }

        // --- fixtures ---
        const string BdcSaihan = "{'latitude':40.7907,'longitude':111.6946,'countryName':'中华人民共和国','countryCode':'CN','principalSubdivision':'内蒙古自治区','city':'呼和浩特市','locality':'賽罕區','localityInfo':{'administrative':[{'name':'中华人民共和国','adminLevel':2},{'name':'内蒙古自治区','adminLevel':4},{'name':'呼和浩特市','adminLevel':5},{'name':'賽罕區','adminLevel':6},{'name':'昭乌达路街道','adminLevel':8}]}}";
        const string BdcHuangpu = "{'latitude':31.2318,'longitude':121.4846,'countryCode':'CN','principalSubdivision':'上海市','city':'上海市','locality':'黃浦區','localityInfo':{'administrative':[{'name':'中华人民共和国','adminLevel':2},{'name':'上海市','adminLevel':4},{'name':'上海市','adminLevel':4},{'name':'黃浦區','adminLevel':6}]}}";
        const string BdcDongguan = "{'countryCode':'CN','principalSubdivision':'广东省','city':'东莞市','locality':'莞城街道','localityInfo':{'administrative':[{'name':'广东省','adminLevel':4},{'name':'东莞市','adminLevel':5},{'name':'莞城街道','adminLevel':6}]}}";
        const string BdcDujiangyan = "{'countryCode':'CN','principalSubdivision':'四川省','city':'成都市','locality':'都江堰市','localityInfo':{'administrative':[{'name':'四川省','adminLevel':4},{'name':'成都市','adminLevel':5},{'name':'都江堰市','adminLevel':6}]}}";
        const string BdcUs = "{'countryCode':'US','principalSubdivision':'District of Columbia','city':'Washington','locality':'Washington','localityInfo':{'administrative':[{'name':'District of Columbia','adminLevel':4}]}}";
        const string NomSaihan = "{'display_name':'昭乌达路街道, 赛罕区, 呼和浩特市, 玉泉区, 内蒙古自治区, 010070, 中国','address':{'country_code':'cn'}}";
        const string NomHuangpu = "{'display_name':'外滩街道, 上海市, 黄浦区, 上海市, 中国','address':{'country_code':'cn'}}";
        const string Pconline = "{'ip':'1.182.60.50','pro':'内蒙古','proCode':'150000','city':'包头市','cityCode':'150200','region':'','regionCode':'0','addr':'内蒙古包头市 电信','regionNames':'','err':''}";
        const string PconlineForeign = "{'ip':'8.8.8.8','pro':'','proCode':'0','city':'','cityCode':'0','region':'','regionCode':'0','addr':'美国 ','regionNames':'','err':''}";
        const string Ipip = "{'ret':'ok','data':{'ip':'1.182.60.50','location':['中国','内蒙古','包头','','chinatelecom.com.cn']}}";
        const string Bilibili = "{'code':0,'msg':'','message':'','data':{'addr':'1.182.60.50','country':'中国','province':'内蒙古','city':'包头','isp':'电信','latitude':'40.659069','longitude':'109.816322'}}";
        const string IpwhoUs = "{'ip':'63.246.150.56','success':true,'country_code':'US','region':'哥倫比亞特區','city':'華盛頓哥倫比亞特區','latitude':38.8951121,'longitude':-77.0363706}";
        const string IpApiUs = "{'status':'success','countryCode':'US','regionName':'弗吉尼亚州','city':'Sterling','district':'','lat':39.0067,'lon':-77.4291}";
        const string TencentJson = "{'status':200,'message':'OK','data':{'observe':{'degree':'8','humidity':'87','weather_code':'07','update_time':'202610251655','wind_power':'3-4','weather':'小雨'},'forecast_24h':{'0':{'time':'2026-10-24','day_weather_code':'02','night_weather_code':'07','max_degree':'12','min_degree':'4'},'1':{'time':'2026-10-25','day_weather_code':'07','night_weather_code':'01','max_degree':'10','min_degree':'3'},'2':{'time':'2026-10-26','day_weather_code':'01','night_weather_code':'00','max_degree':'14','min_degree':'2'}}}}";
        const string MetNoJson = "{'properties':{'timeseries':["
            + "{'time':'2026-10-25T08:00:00Z','data':{'instant':{'details':{'air_temperature':9.1,'relative_humidity':68,'wind_speed':3.0}},'next_1_hours':{'summary':{'symbol_code':'cloudy'}}}},"
            + "{'time':'2026-10-25T14:00:00Z','data':{'instant':{'details':{'air_temperature':12.0,'relative_humidity':60,'wind_speed':2.0}},'next_6_hours':{'summary':{'symbol_code':'lightrain_day'}}}},"
            + "{'time':'2026-10-25T20:00:00Z','data':{'instant':{'details':{'air_temperature':6.0,'relative_humidity':70,'wind_speed':1.0}},'next_6_hours':{'summary':{'symbol_code':'cloudy'}}}},"
            + "{'time':'2026-10-26T02:00:00Z','data':{'instant':{'details':{'air_temperature':3.0,'relative_humidity':75,'wind_speed':1.0}},'next_6_hours':{'summary':{'symbol_code':'partlycloudy_day'}}}},"
            + "{'time':'2026-10-26T08:00:00Z','data':{'instant':{'details':{'air_temperature':7.0,'relative_humidity':55,'wind_speed':2.0}},'next_6_hours':{'summary':{'symbol_code':'clearsky_day'}}}},"
            + "{'time':'2026-10-26T14:00:00Z','data':{'instant':{'details':{'air_temperature':13.0,'relative_humidity':50,'wind_speed':2.0}},'next_6_hours':{'summary':{'symbol_code':'fair_day'}}}}]}}";
        const string WttrJson = "{'current_condition':[{'temp_C':'8','humidity':'68','windspeedKmph':'12','weatherCode':'266','localObsDateTime':'2026-10-25 09:06 AM'}],'weather':[{'date':'2026-10-25','maxtempC':'12','mintempC':'4','hourly':[{'time':'1200','weatherCode':'113'}]},{'date':'2026-10-26','maxtempC':'13','mintempC':'3','hourly':[{'time':'1200','weatherCode':'119'}]}]}";
        const string OpenMeteoJson = "{'current':{'temperature_2m':8.4,'relative_humidity_2m':70,'weather_code':3,'wind_speed_10m':9.5,'time':'2026-10-25T16:45'},'daily':{'time':['2026-10-25','2026-10-26'],'weather_code':[3,61],'temperature_2m_max':[12,14],'temperature_2m_min':[4,5]}}";
        const string NominatimSearch = "[{'lat':'40.7907931','lon':'111.6946262','display_name':'赛罕区, 呼和浩特市, 内蒙古自治区, 中国','address':{'country_code':'cn'}},{'lat':'31.2','lon':'121.4','display_name':'赛罕, 某镇, 云南省, 中国','address':{'country_code':'cn'}}]";
        const string OpenMeteoSearch = "{'results':[{'name':'赛罕','latitude':22.8271,'longitude':99.8168,'country_code':'CN','admin1':'云南','admin2':'思茅市'},{'name':'Saihan','latitude':41.0,'longitude':110.0,'country_code':'MN','admin1':'Somewhere'}]}";

        static GeoPlace Cn(string prov, string city, string district, double lat = double.NaN, double lon = double.NaN) { return new GeoPlace { Kind = "manual", CountryCode = "CN", Province = prov, City = city, District = district, Lat = lat, Lon = lon }; }

        // ---------------- sync rules (self-test) ----------------
        internal static void Rules(Action<bool, string> check)
        {
            // county-level name extraction — the examples from the request plus the awkward cases
            var table = new[]
            {
                new[] { "呼和浩特市赛罕区", "赛罕区" }, new[] { "内蒙古自治区呼和浩特市赛罕区", "赛罕区" }, new[] { "上海市黄浦区", "黄浦区" }, new[] { "賽罕區", "赛罕区" }, new[] { "黃浦區", "黄浦区" },
                new[] { "呼和浩特市賽罕區", "赛罕区" }, new[] { "成都市都江堰市", "都江堰市" }, new[] { "四川省成都市都江堰市", "都江堰市" }, new[] { "东莞市", "东莞市" }, new[] { "广东省东莞市", "东莞市" }, new[] { "上海市", "上海市" },
                new[] { "北京市市辖区", "北京市" }, new[] { "河南省济源市", "济源市" }, new[] { "济南市市中区", "市中区" }, new[] { "广东省广州市天河区", "天河区" }, new[] { "延边朝鲜族自治州延吉市", "延吉市" },
                new[] { "新疆维吾尔自治区乌鲁木齐市天山区", "天山区" }, new[] { "中国内蒙古自治区呼和浩特市赛罕区", "赛罕区" }, new[] { "呼和浩特市赛罕区昭乌达路街道", "赛罕区" }, new[] { "吉林省吉林市", "吉林市" },
                new[] { "大兴安岭地区呼玛县", "呼玛县" }, new[] { "兴安盟科尔沁右翼前旗", "科尔沁右翼前旗" }, new[] { "Washington", "Washington" }, new[] { "華盛頓哥倫比亞特區", "华盛顿哥伦比亚特区" }
            };
            var bad = table.Where(r => PlaceNames.DistrictOf(r[0]) != r[1]).Select(r => r[0] + "→" + PlaceNames.DistrictOf(r[0]) + "（应为" + r[1] + "）").ToList();
            check(bad.Count == 0, "weather names: county-level extraction table (" + table.Length + " cases: 赛罕区, 黄浦区, 县级市, 无区城市, 繁体…)" + (bad.Count > 0 ? " " + string.Join("; ", bad) : ""));
            check(PlaceNames.Simplify("賽罕區") == "赛罕区" && PlaceNames.Simplify("黃浦區") == "黄浦区" && PlaceNames.Simplify("區縣鎮") == "区县镇" && PlaceNames.Simplify("华盛顿") == "华盛顿" && PlaceNames.Simplify("") == "" && PlaceNames.Simplify(null) == "", "weather names: Traditional → Simplified (區→区) and empty input");
            check(PlaceNames.IsCountyLevel("赛罕区") && PlaceNames.IsCountyLevel("都江堰市") && PlaceNames.IsCountyLevel("科尔沁右翼前旗") && PlaceNames.IsCountyLevel("呼玛县") && !PlaceNames.IsCountyLevel("昭乌达路街道") && !PlaceNames.IsCountyLevel("莞城镇") && !PlaceNames.IsCountyLevel("") && PlaceNames.SameCity("包头市", "包头") && !PlaceNames.SameCity("包头", "呼和浩特"), "weather names: county-level test, street/town rejected, city stems match");

            // reverse geocoding fixtures
            var p = PlaceLookup.ParseBigDataCloud(J(BdcSaihan));
            check(p.District == "赛罕区" && p.City == "呼和浩特市" && p.Province == "内蒙古自治区" && p.CountryCode == "CN" && p.Display == "赛罕区", "reverse geocode: 呼和浩特市赛罕区 → shows 赛罕区 (Traditional 賽罕區 normalised)");
            p = PlaceLookup.ParseBigDataCloud(J(BdcHuangpu));
            check(p.District == "黄浦区" && p.City == "上海市" && p.Display == "黄浦区", "reverse geocode: 上海市黄浦区 → shows 黄浦区");
            p = PlaceLookup.ParseBigDataCloud(J(BdcDongguan));
            check(p.District == "" && p.City == "东莞市" && p.Display == "东莞市", "reverse geocode: a city without districts falls back to the city (东莞市), not the street");
            p = PlaceLookup.ParseBigDataCloud(J(BdcDujiangyan));
            check(p.District == "都江堰市" && p.City == "成都市", "reverse geocode: county-level city shows its own name");
            p = PlaceLookup.ParseBigDataCloud(J(BdcUs));
            check(p.CountryCode == "US" && p.District == "" && p.City == "Washington" && !p.InChina, "reverse geocode: outside China keeps the city and never invents a district");
            p = PlaceLookup.ParseNominatim(J(NomSaihan));
            check(p.District == "赛罕区" && p.City == "呼和浩特市" && p.Province == "内蒙古自治区", "reverse geocode fallback (OpenStreetMap): first county-level name in the address, not the neighbouring 玉泉区");
            p = PlaceLookup.ParseNominatim(J(NomHuangpu));
            check(p.District == "黄浦区" && p.City == "上海市" && p.Province == "上海市", "reverse geocode fallback (OpenStreetMap): municipality 上海市黄浦区");
            bool rejected = false; try { PlaceLookup.ParseBigDataCloud("{}"); } catch (FormatException) { rejected = true; }
            check(rejected, "reverse geocode: an empty answer is rejected instead of showing a blank place");

            // IP services
            var c = IpLocator.ParsePconline(J(Pconline));
            check(c.IsChina && c.Province == "内蒙古" && c.City == "包头市" && !c.HasCoords, "ip service pconline parsed (names only)");
            c = IpLocator.ParseIpip(J(Ipip)); check(c.IsChina && c.City == "包头", "ip service ipip.net parsed");
            c = IpLocator.ParseBilibili(J(Bilibili)); check(c.IsChina && c.City == "包头" && c.HasCoords && Math.Abs(c.Lat - 40.659069) < 1e-6, "ip service bilibili parsed with coordinates");
            c = IpLocator.ParseIpwho(J(IpwhoUs)); check(c.Country == "US" && c.City == "华盛顿哥伦比亚特区" && c.Province == "哥伦比亚特区", "ip service ipwho.is parsed and its Traditional city name (華盛頓哥倫比亞特區) is simplified");
            c = IpLocator.ParseIpApi(J(IpApiUs)); check(c.Country == "US" && c.City == "Sterling" && c.HasCoords, "ip service ip-api.com parsed");
            var foreign = IpLocator.ParsePconline(J(PconlineForeign));
            check(!foreign.IsChina && foreign.Country == "??", "ip service pconline: an address outside mainland China is flagged as not-China");
            rejected = false; try { IpLocator.ParseIpwho("{\"success\":false,\"message\":\"rate limited\"}"); } catch (InvalidOperationException) { rejected = true; }
            check(rejected, "ip service failure is an error, never a hard-coded city");

            // vote
            int agree; bool proxy;
            var cn = new List<IpCandidate> { IpLocator.ParsePconline(J(Pconline)), IpLocator.ParseIpip(J(Ipip)), IpLocator.ParseBilibili(J(Bilibili)) };
            var w = IpLocator.Vote(cn, true, out agree, out proxy);
            check(w != null && w.City.StartsWith("包头") && agree == 3 && !proxy && w.HasCoords, "ip vote: three domestic services agree (包头市 / 包头 are the same city), coordinates taken from the one that has them");
            var mixed = new List<IpCandidate> { IpLocator.ParseIpwho(J(IpwhoUs)), IpLocator.ParseIpApi(J(IpApiUs)) }; mixed.AddRange(cn);
            w = IpLocator.Vote(mixed, true, out agree, out proxy);
            check(w != null && w.IsChina && w.City.StartsWith("包头") && proxy, "ip vote: on a China time zone the foreign answers (proxy / VPN exit, e.g. 華盛頓) are discarded");
            w = IpLocator.Vote(new List<IpCandidate> { IpLocator.ParseIpwho(J(IpwhoUs)), IpLocator.ParseIpApi(J(IpApiUs)) }, true, out agree, out proxy);
            check(w != null && w.Country == "US" && !proxy, "ip vote: with only foreign answers they are used (nothing better exists)");
            w = IpLocator.Vote(mixed, false, out agree, out proxy);
            check(w != null && w.IsChina && agree == 3 && !proxy, "ip vote: a non-China time zone does not discard anything; the majority still wins");
            var split = new List<IpCandidate> { new IpCandidate { Service = "a", Country = "CN", Province = "内蒙古", City = "呼和浩特市" }, new IpCandidate { Service = "b", Country = "CN", Province = "内蒙古", City = "包头市" }, new IpCandidate { Service = "c", Country = "CN", Province = "内蒙古", City = "包头" } };
            w = IpLocator.Vote(split, true, out agree, out proxy); check(w.City.StartsWith("包头") && agree == 2, "ip vote: the majority city wins");
            split.RemoveAt(2); w = IpLocator.Vote(split, true, out agree, out proxy); check(w.City == "呼和浩特市", "ip vote: a tie goes to the higher-priority service");
            check(IpLocator.Vote(new List<IpCandidate>(), true, out agree, out proxy) == null, "ip vote: no candidates → no result");

            // weather sources
            var place = Cn("内蒙古自治区", "呼和浩特市", "赛罕区", 40.79, 111.69);
            var r = WeatherService.ParseTencent(place, J(TencentJson), new DateTime(2026, 10, 25));
            check(r.Source == "腾讯天气" && r.Temperature == 8 && r.Humidity == 87 && r.Code == 61 && r.Time == "10-25 16:55" && r.City == "赛罕区" && Math.Abs(r.Wind - WeatherService.BeaufortKmh("3-4")) < 1e-9, "weather source 腾讯天气: current conditions, 小雨 → rain, observation time");
            check(r.Days.Count == 2 && r.Days[0].Date == new DateTime(2026, 10, 25) && r.Days[0].Max == 10 && r.Days[0].Min == 3 && r.Days[0].Code == 61 && r.Days[1].Code == 2, "weather source 腾讯天气: forecast starts today (yesterday's entry dropped)");
            check(WeatherService.BeaufortKmh("3-4") > 19 && WeatherService.BeaufortKmh("3-4") < 21 && WeatherService.BeaufortKmh("<3") > 0 && WeatherService.BeaufortKmh("") == 0, "weather source 腾讯天气: wind level → km/h");
            var m = WeatherService.ParseMetNo(place, J(MetNoJson), 8);
            check(m.Source == "met.no" && m.Temperature == 9.1 && m.Humidity == 68 && Math.Abs(m.Wind - 10.8) < 1e-9 && m.Code == 3 && m.Time == "10-25 16:00", "weather source met.no: current conditions (m/s → km/h, local time)");
            check(m.Days.Count == 2 && m.Days[0].Max == 12 && m.Days[0].Min == 9.1 && m.Days[1].Max == 13 && m.Days[1].Min == 3 && m.Days[1].Code == 2, "weather source met.no: daily min/max grouped by local date, midday symbol");
            var t = WeatherService.ParseWttr(place, J(WttrJson));
            check(t.Source == "wttr.in" && t.Temperature == 8 && t.Humidity == 68 && t.Wind == 12 && t.Code == 51 && t.Time == "10-25 09:06" && t.Days.Count == 2 && t.Days[0].Code == 0 && t.Days[1].Code == 3, "weather source wttr.in: current conditions and 3-day forecast");
            var om = WeatherService.Parse("赛罕区", J(OpenMeteoJson));
            check(om.Temperature == 8.4 && om.Code == 3 && om.Days.Count == 2 && om.Time == "10-25 16:45", "weather source Open-Meteo: current conditions and forecast");
            rejected = false; try { WeatherService.ParseTencent(place, J("{'status':200,'data':{'observe':{'degree':'999','humidity':'87','weather_code':'07','update_time':'202610251655','wind_power':'3'}}}"), DateTime.Today); } catch (FormatException) { rejected = true; }
            check(rejected, "weather sources: an impossible temperature is rejected instead of displayed");
            rejected = false; try { WeatherService.ParseTencent(place, J("{'status':500,'message':'x'}"), DateTime.Today); } catch (FormatException) { rejected = true; }
            check(rejected, "weather sources: an error answer is rejected");
            check(WeatherService.TencentCode("07") == 61 && WeatherService.TencentCode("18") == 45 && WeatherService.TencentCode("53") == 102 && WeatherService.TencentCode("04") == 95 && WeatherService.TencentCode("zz") == 3, "weather code mapping: Tencent");
            check(WeatherService.MetNoCode("lightrain_day") == 61 && WeatherService.MetNoCode("heavyrainandthunder") == 95 && WeatherService.MetNoCode("partlycloudy_night") == 2 && WeatherService.MetNoCode("sleet") == 68 && WeatherService.MetNoCode("fog") == 45 && WeatherService.MetNoCode("clearsky_night") == 0 && WeatherService.MetNoCode("snowshowers_day") == 85, "weather code mapping: met.no");
            check(WeatherService.WwoCode(113) == 0 && WeatherService.WwoCode(296) == 61 && WeatherService.WwoCode(389) == 95 && WeatherService.WwoCode(248) == 45 && WeatherService.WwoCode(122) == 3 && WeatherService.WwoCode(338) == 75, "weather code mapping: wttr.in");
            check(WeatherWindow.Description(101) == "沙尘" && WeatherWindow.Description(102) == "霾" && WeatherWindow.Description(61) == "雨" && WeatherWindow.Description(0) == "晴", "weather code mapping: dust and haze labels, existing labels unchanged");

            // source order
            Func<GeoPlace, string> order = g => string.Join(">", WeatherService.ProvidersFor(g).Select(x => x.Name));
            check(order(place) == "腾讯天气>Open-Meteo>met.no>wttr.in", "weather source order in China: 腾讯天气 → Open-Meteo → met.no → wttr.in (" + order(place) + ")");
            check(order(new GeoPlace { CountryCode = "US", City = "Washington", Lat = 38.9, Lon = -77.0 }) == "Open-Meteo>met.no>wttr.in", "weather source order elsewhere: Open-Meteo → met.no → wttr.in; the Chinese source is not used");
            check(order(Cn("内蒙古", "包头市", "")) == "腾讯天气", "a Chinese place without coordinates can still be read by name");
            check(order(new GeoPlace { Lat = 31.2, Lon = 121.4 }) == "Open-Meteo>met.no>wttr.in", "a place with coordinates but no name uses the coordinate-based sources");

            // refresh schedule: reasonable polling, backoff with jitter
            check(WeatherWindow.NextDelay(0, 0.5).TotalMinutes >= 18 && WeatherWindow.NextDelay(0, 1).TotalMinutes <= 22 && WeatherWindow.NextDelay(0, 0).TotalMinutes >= 17.9, "weather refresh: about every 20 minutes after a success (±10%)");
            check(WeatherWindow.NextDelay(1, 0.5).TotalMinutes == 1 && WeatherWindow.NextDelay(2, 0.5).TotalMinutes == 2 && WeatherWindow.NextDelay(3, 0.5).TotalMinutes == 4 && WeatherWindow.NextDelay(4, 0.5).TotalMinutes == 8 && WeatherWindow.NextDelay(5, 0.5).TotalMinutes == 15 && WeatherWindow.NextDelay(50, 0.5).TotalMinutes == 15, "weather refresh: exponential backoff 1, 2, 4, 8, 15 minutes");
            check(WeatherWindow.NextDelay(3, 0).TotalMinutes < WeatherWindow.NextDelay(3, 1).TotalMinutes && WeatherWindow.NextDelay(5, 1).TotalMinutes <= 18.0001 && WeatherWindow.NextDelay(1, 0).TotalMinutes >= 0.79, "weather refresh: backoff is jittered and capped");

            // settings → manual place
            var s = new Settings();
            check(s.ManualPlace() == null, "manual place: off by default");
            s.WeatherManual = true; s.ManualProvince = "内蒙古自治区"; s.ManualCity = "呼和浩特市"; s.ManualDistrict = "赛罕区"; s.ManualLat = 40.79; s.ManualLon = 111.69;
            var mp = s.ManualPlace(); check(mp != null && mp.Display == "赛罕区" && mp.Kind == "manual" && mp.HasCoords && mp.InChina, "manual place: county name and coordinates come from the settings");
            s.ManualLat = 0; s.ManualLon = 0; mp = s.ManualPlace(); check(mp != null && !mp.HasCoords && order(mp) == "腾讯天气", "manual place without coordinates is read by name");
            s.ManualDistrict = ""; s.ManualCity = ""; s.ManualProvince = ""; check(s.ManualPlace() == null, "manual place: empty selection is ignored");
        }

        // ---------------- async (regression) ----------------
        static WeatherService Make(Func<string, CancellationToken, Task<string>> net, Func<CancellationToken, Task<GeoPlace>> locator = null, IWeatherStore store = null, bool system = false, GeoPlace manual = null)
        {
            var s = new WeatherService(net, locator ?? (ct => Task.FromResult<GeoPlace>(null)), store);
            s.ExpectChina = () => true; s.UseSystemLocation = () => system; s.ManualSource = () => manual == null ? null : manual.Clone();
            return s;
        }
        static string Ip(string url)
        {
            if (url.Contains("pconline")) return J(Pconline);
            if (url.Contains("ipip.net")) return J(Ipip);
            if (url.Contains("bilibili")) return J(Bilibili);
            if (url.Contains("ipwho.is")) return J(IpwhoUs);
            if (url.Contains("ip-api.com")) return J(IpApiUs);
            return null;
        }

        // Forecast dates relative to today so the async tests keep working on any day.
        static string TencentNow()
        {
            var t = DateTime.Today; Func<int, string> d = o => t.AddDays(o).ToString("yyyy-MM-dd");
            return J("{'status':200,'message':'OK','data':{'observe':{'degree':'8','humidity':'87','weather_code':'07','update_time':'" + t.ToString("yyyyMMdd") + "1655','wind_power':'3-4','weather':'小雨'},'forecast_24h':{'0':{'time':'" + d(-1) + "','day_weather_code':'02','night_weather_code':'07','max_degree':'12','min_degree':'4'},'1':{'time':'" + d(0) + "','day_weather_code':'07','night_weather_code':'01','max_degree':'10','min_degree':'3'},'2':{'time':'" + d(1) + "','day_weather_code':'01','night_weather_code':'00','max_degree':'14','min_degree':'2'}}}}");
        }

        internal static async Task Run(Action<bool, string> check)
        {
            int oldHedge = WeatherService.HedgeMs, oldTimeout = WeatherService.RequestTimeoutMs;
            try
            {
                // A. fallback order: the first source fails, the next one answers
                var urls = new List<string>();
                var a = Make(async (url, ct) => { lock (urls) urls.Add(url); await Task.Yield(); string ip = Ip(url); if (ip != null) return ip; if (url.Contains("wis.qq.com")) throw new IOException("HTTP 503"); if (url.Contains("open-meteo")) return J(OpenMeteoJson); throw new IOException("unexpected " + url); });
                var ra = await a.ReadAsync(true, CancellationToken.None);
                int iTencent = urls.FindIndex(u => u.Contains("wis.qq.com")), iOm = urls.FindIndex(u => u.Contains("open-meteo"));
                check(ra.Source == "Open-Meteo" && iTencent >= 0 && iOm > iTencent && !urls.Any(u => u.Contains("met.no") || u.Contains("wttr.in")), "weather fallback: 腾讯天气 failed → Open-Meteo answered; later sources were not touched");
                check(a.LastReport.Contains("腾讯天气 失败") && a.LastReport.Contains("Open-Meteo 成功") && !a.LastReport.Contains("http"), "weather fallback: the report names each step without URLs");
                check(urls.Count(u => u.Contains("pconline") || u.Contains("ipip.net") || u.Contains("bilibili")) == 3 && !urls.Any(u => u.Contains("ipwho.is") || u.Contains("ip-api.com")), "ip location: three domestic services agree, so the foreign ones are never asked");
                check(ra.Place != null && ra.Place.Kind == "ip" && ra.Place.Display == "包头市" && ra.PlaceSource.Contains("IP") && !(ra.Place.Note ?? "").Contains("x"), "ip location: result is marked approximate (网络 IP)");

                // A2. Tencent does not know 土默特右旗 (it lists 土右旗): empty answer → the city-level URL is tried, and the source says so
                var qqUrls = new List<string>();
                var qqPlace = Cn("内蒙古自治区", "包头市", "土默特右旗", 40.57, 110.53);
                var rq = await WeatherService.Fetch(WeatherService.Tencent, qqPlace, async (url, ct) => { qqUrls.Add(url); await Task.Yield(); return url.EndsWith("county=") ? TencentNow() : J("{'status':200,'message':'OK','data':{'observe':{'degree':'','humidity':'','update_time':'','weather_code':'','wind_power':''}}}"); }, CancellationToken.None);
                check(rq.Source == "腾讯天气（市级）" && rq.Temperature == 8 && qqUrls.Count == 2 && qqUrls[0].Contains("county=" + Uri.EscapeDataString("土默特右旗")) && !qqUrls[1].Contains("county=%"), "weather source 腾讯天气: no data for the county → the city-level answer is used and labelled 市级");
                qqUrls.Clear();
                var rq2 = await WeatherService.Fetch(WeatherService.Tencent, Cn("内蒙古自治区", "呼和浩特市", "赛罕区", 40.79, 111.69), async (url, ct) => { qqUrls.Add(url); await Task.Yield(); return TencentNow(); }, CancellationToken.None);
                check(rq2.Source == "腾讯天气" && qqUrls.Count == 1, "weather source 腾讯天气: a county with data needs exactly one request");

                // B. slow first source: the next one is started after the hedge delay and the first valid answer wins
                WeatherService.HedgeMs = 60; bool slowCancelled = false;
                var b = Make(async (url, ct) => { string ip = Ip(url); if (ip != null) return ip; if (url.Contains("wis.qq.com")) { try { await Task.Delay(20000, ct); } catch (OperationCanceledException) { slowCancelled = true; throw; } return TencentNow(); } if (url.Contains("open-meteo")) return J(OpenMeteoJson); throw new IOException("x"); });
                var sw = System.Diagnostics.Stopwatch.StartNew(); var rb = await b.ReadAsync(true, CancellationToken.None);
                await Task.Delay(100);
                check(rb.Source == "Open-Meteo" && sw.ElapsedMilliseconds < 5000 && slowCancelled, "weather fallback: a slow source does not block the next one (hedged), and the loser is cancelled");

                // C. everything fails → exception with reasons, previous reading survives
                var failing = false; var store = new MemoryWeatherStore();
                var c = Make(async (url, ct) => { await Task.Yield(); if (failing) throw new TimeoutException("t"); string ip = Ip(url); if (ip != null) return ip; if (url.Contains("wis.qq.com")) return TencentNow(); throw new IOException("x"); }, null, store);
                var rc = await c.ReadAsync(true, CancellationToken.None);
                check(rc.Source == "腾讯天气" && rc.City == "包头市" && !rc.Stale && rc.Updated != default(DateTime), "weather read: Tencent answers first in China, reading carries source and update time");
                failing = true; WeatherException err = null;
                try { await c.ReadAsync(true, CancellationToken.None); } catch (WeatherException e) { err = e; }
                check(err != null && err.Reasons.Any(x => x.Contains("失败")) && c.Cached != null && c.Cached.Temperature == 8, "weather read: when every source fails an error with reasons is raised and the last good reading is kept");
                var cached = new WeatherService(null, null, store).Cached;
                check(cached != null && cached.Stale && cached.Temperature == 8 && cached.City == "包头市" && cached.Updated != default(DateTime) && cached.Days.Count >= 1, "weather cache: last good reading and place are restored from disk as stale data after a restart");
                check(store.Data != null && store.Data.Coords == "40.66,109.82" && string.IsNullOrEmpty(store.Data.PlaceAt) == false && !JsonHas(store.Data, "63.246") && !JsonHas(store.Data, "1.182.60.50"), "weather cache: stores rounded coordinates only, never the IP address");
                var fresh = Make(async (url, ct) => { await Task.Yield(); throw new IOException("offline"); }, null, store);
                WeatherException err2 = null; try { await fresh.ReadAsync(false, CancellationToken.None); } catch (WeatherException e) { err2 = e; }
                check(err2 != null && fresh.Cached != null && fresh.Cached.Stale && fresh.Cached.Temperature == 8, "weather cache: offline at startup still has the stale reading to show (stale reading is not reported as fresh)");

                // D. Windows location → reverse geocode → district → weather by district name
                urls.Clear();
                var d = Make(async (url, ct) => { lock (urls) urls.Add(url); await Task.Yield(); if (url.Contains("bigdatacloud")) return J(BdcHuangpu); if (url.Contains("wis.qq.com")) return TencentNow(); throw new IOException("x"); },
                    ct => Task.FromResult(new GeoPlace { Kind = "system", Lat = 31.2318, Lon = 121.4846, Accuracy = 90 }), null, true);
                var rd = await d.ReadAsync(true, CancellationToken.None);
                string tencentUrl = urls.First(u => u.Contains("wis.qq.com"));
                check(rd.Place.Kind == "system" && rd.City == "黄浦区" && rd.PlaceSource == "Windows 定位" && !urls.Any(u => u.Contains("pconline") || u.Contains("ipwho")), "location: Windows location wins, no IP service is asked, shows 黄浦区");
                check(tencentUrl.Contains("county=" + Uri.EscapeDataString("黄浦区")) && tencentUrl.Contains("province=" + Uri.EscapeDataString("上海市")), "location: the weather lookup uses the district name from the reverse geocode");

                // E. Windows location without a reverse geocode keeps working by coordinates; failures fall back to IP
                var e1 = Make(async (url, ct) => { await Task.Yield(); if (url.Contains("open-meteo")) return J(OpenMeteoJson); throw new IOException("down"); }, ct => Task.FromResult(new GeoPlace { Kind = "system", Lat = 31.2318, Lon = 121.4846, Accuracy = 30 }), null, true);
                var re1 = await e1.ReadAsync(true, CancellationToken.None);
                check(re1.Source == "Open-Meteo" && re1.City == "所在位置" && e1.LastReport.Contains("无法解析为地名"), "location: if the fix cannot be turned into a name, coordinates still give weather (name shown as 所在位置)");
                var e2 = Make(async (url, ct) => { await Task.Yield(); string ip = Ip(url); if (ip != null) return ip; if (url.Contains("wis.qq.com")) return TencentNow(); throw new IOException("x"); }, ct => Task.FromResult<GeoPlace>(null), null, true);
                var re2 = await e2.ReadAsync(true, CancellationToken.None);
                check(re2.Place.Kind == "ip" && e2.LastReport.Contains("Windows 定位不可用"), "location: Windows location unavailable (off / denied / no data) → IP vote");
                var e3 = Make(async (url, ct) => { await Task.Yield(); string ip = Ip(url); if (ip != null) return ip; if (url.Contains("wis.qq.com")) return TencentNow(); throw new IOException("x"); }, ct => { throw new InvalidOperationException("boom"); }, null, true);
                check((await e3.ReadAsync(true, CancellationToken.None)).Place.Kind == "ip", "location: a crashing location provider falls back to IP");
                var e4 = Make(async (url, ct) => { await Task.Yield(); string ip = Ip(url); if (ip != null) return ip; if (url.Contains("wis.qq.com")) return TencentNow(); throw new IOException("x"); }, ct => Task.FromResult(new GeoPlace { Kind = "system", Lat = 10, Lon = 10, Accuracy = 900000 }), null, true);
                check((await e4.ReadAsync(true, CancellationToken.None)).Place.Kind == "ip", "location: an IP-grade Windows fix (accuracy worse than 50 km) is not trusted");

                // F. proxy / VPN: foreign IP services answer with the exit node, domestic ones with the real city
                urls.Clear();
                var f = Make(async (url, ct) => { lock (urls) urls.Add(url); await Task.Yield(); if (url.Contains("pconline")) return J(Pconline); if (url.Contains("ipip.net")) throw new IOException("HTTP 429"); if (url.Contains("bilibili")) throw new TimeoutException("t"); if (url.Contains("ipwho.is")) return J(IpwhoUs); if (url.Contains("ip-api.com")) return J(IpApiUs); if (url.Contains("wis.qq.com")) return TencentNow(); throw new IOException("x"); });
                var rf = await f.ReadAsync(true, CancellationToken.None);
                check(rf.Place.Display == "包头市" && rf.Place.Note.Contains("代理") && urls.Any(u => u.Contains("ipwho.is")), "ip location behind a proxy/VPN: foreign exit-node answers are ignored in favour of the Chinese service; the user is told");
                check(!rf.Place.HasCoords || rf.Place.Lat > 30, "ip location: when no service returned coordinates they are looked up by city name (or the name alone is used)");

                // G. manual override: no location service at all, exact district
                urls.Clear();
                var g = Make(async (url, ct) => { lock (urls) urls.Add(url); await Task.Yield(); if (url.Contains("wis.qq.com")) return TencentNow(); throw new IOException("x"); }, null, null, true, Cn("内蒙古自治区", "呼和浩特市", "赛罕区", 40.79, 111.69));
                var rg = await g.ReadAsync(true, CancellationToken.None);
                check(rg.City == "赛罕区" && rg.Place.Kind == "manual" && urls.Count == 1 && urls[0].Contains("county=" + Uri.EscapeDataString("赛罕区")), "manual place: exactly one request (weather), no IP or system location, shows 赛罕区");

                // H. network change / wake: forget the approximate place, refetch even though the cache is fresh
                urls.Clear();
                var h = Make(async (url, ct) => { lock (urls) urls.Add(url); await Task.Yield(); string ip = Ip(url); if (ip != null) return ip; if (url.Contains("wis.qq.com")) return TencentNow(); throw new IOException("x"); });
                await h.ReadAsync(true, CancellationToken.None); int first = urls.Count;
                await h.ReadAsync(false, CancellationToken.None);
                check(urls.Count == first, "no spam: a second read within 20 minutes is served from the cache without any request");
                h.NetworkChanged(); await h.ReadAsync(false, CancellationToken.None);
                check(urls.Count > first && urls.Skip(first).Any(u => u.Contains("pconline")), "network change / wake from sleep: location is determined again and weather refetched");

                // I. place search (settings)
                var s = await PlaceLookup.SearchAsync(async (url, ct) => { await Task.Yield(); if (url.Contains("nominatim")) return J(NominatimSearch); if (url.Contains("open-meteo")) return J(OpenMeteoSearch); throw new IOException("x"); }, "赛罕区", CancellationToken.None, null);
                check(s.Count >= 2 && s[0].District == "赛罕区" && s[0].City == "呼和浩特市" && s[0].Province == "内蒙古自治区" && s[0].HasCoords && s.All(x => x.Kind == "manual"), "place search: 赛罕区 → 呼和浩特市赛罕区 first (with coordinates), other candidates listed");
                var s2 = await PlaceLookup.SearchAsync(async (url, ct) => { await Task.Yield(); if (url.Contains("nominatim")) throw new TimeoutException("t"); return J(OpenMeteoSearch); }, "赛罕", CancellationToken.None, null);
                check(s2.Count >= 1 && s2.Any(x => x.CountryCode == "CN"), "place search: one search service down → the other still answers");
                var s3 = await PlaceLookup.SearchAsync(async (url, ct) => { await Task.Yield(); throw new IOException("x"); }, "赛罕", CancellationToken.None, null);
                check(s3.Count == 0, "place search: both down → empty list, no exception");
                var qs = new List<string>(); await PlaceLookup.SearchAsync(async (url, ct) => { qs.Add(url); await Task.Yield(); return "[]"; }, "呼和浩特市赛罕区", CancellationToken.None, null);
                check(qs.Any(u => u.Contains(Uri.EscapeDataString("赛罕区 呼和浩特市"))), "place search: a full address is reduced to 'district city' for the name lookup");
            }
            finally { WeatherService.HedgeMs = oldHedge; WeatherService.RequestTimeoutMs = oldTimeout; }
        }
        static bool JsonHas(WeatherCacheData d, string text) { return new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(d).Contains(text); }
    }
}
