using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClearDesk
{
    /// <summary>ClearDesk.exe --weather-diag [file] [rounds] : tests every location and weather source from this PC and writes a report (no settings or cache are touched).</summary>
    internal static class WeatherDiag
    {
        internal static void Run(string[] args)
        {
            string path = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "weather-diag.txt");
            int rounds = 1; if (args.Length > 2) int.TryParse(args[2], out rounds); rounds = Math.Max(1, Math.Min(rounds, 10));
            var o = new StringBuilder();
            try { Task.Run(() => RunAsync(o, rounds)).Wait(); } catch (Exception e) { o.AppendLine("诊断出错：" + e.GetBaseException().Message); }
            File.WriteAllText(path, o.ToString(), new UTF8Encoding(false));
        }
        static void L(StringBuilder o, string s) { lock (o) o.AppendLine(s); }
        static async Task<string> Timed(StringBuilder o, string name, Func<Task<string>> f)
        {
            var sw = Stopwatch.StartNew();
            try { string r = await f().ConfigureAwait(false); L(o, "  " + name + "：成功 " + sw.ElapsedMilliseconds + "ms"); return r; }
            catch (Exception e) { L(o, "  " + name + "：失败 " + sw.ElapsedMilliseconds + "ms — " + WeatherService.Reason(e)); return null; }
        }
        static async Task RunAsync(StringBuilder o, int rounds)
        {
            L(o, "ClearDesk " + SettingsWindow.Version + " 天气诊断  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "（本机时区 " + TimeZoneInfo.Local.Id + "）");
            try { var wp = WebRequest.DefaultWebProxy; L(o, "系统代理（访问 api.open-meteo.com）：" + (wp == null ? "无" : wp.GetProxy(new Uri("https://api.open-meteo.com/")).ToString())); } catch (Exception e) { L(o, "系统代理：读取失败 " + e.Message); }
            var none = new List<string>();
            L(o, "\n[1] Windows 定位");
            var sw = Stopwatch.StartNew(); var sys = await SystemLocation.GetAsync(8000, CancellationToken.None);
            L(o, sys == null ? "  不可用/未允许/无数据（" + sw.ElapsedMilliseconds + "ms）" : "  成功 " + sw.ElapsedMilliseconds + "ms，精度约 " + Math.Round(sys.Accuracy) + " 米，坐标 " + sys.Lat.ToString("0.00") + "," + sys.Lon.ToString("0.00") + "（已取整）");
            L(o, "\n[2] IP 定位服务（逐个，" + rounds + " 轮）");
            var cands = new List<IpCandidate>();
            for (int r = 0; r < rounds; r++)
                foreach (var s in IpLocator.Services)
                {
                    var svc = s; IpCandidate c = null;
                    string text = await Timed(o, svc.Name, () => WeatherService.Download(svc.Url, CancellationToken.None));
                    if (text != null) { try { c = svc.Parse(text); L(o, "      → " + c.Country + " " + c.Province + " " + c.City + " " + c.District + (c.HasCoords ? "（含坐标）" : "")); if (r == 0) cands.Add(c); } catch (Exception e) { L(o, "      解析失败：" + WeatherService.Reason(e)); } }
                }
            int agree; bool proxy; bool expectCn = TimeZoneInfo.Local.Id == "China Standard Time";
            var win = IpLocator.Vote(cands, expectCn, out agree, out proxy);
            L(o, win == null ? "  投票：无结果" : "  投票结果：" + win.Country + " " + win.Province + " " + win.City + "（" + agree + " 个服务一致" + (proxy ? "；忽略了境外结果（疑似代理出口）" : "") + "）");
            L(o, "\n[3] 反向地理编码（把定位坐标解析为区县）");
            GeoPlace place = null;
            double lat = sys != null ? sys.Lat : (win != null && win.HasCoords ? win.Lat : double.NaN), lon = sys != null ? sys.Lon : (win != null ? win.Lon : double.NaN);
            if (!double.IsNaN(lat))
            {
                var rl = new List<string>();
                try { place = await PlaceLookup.ReverseAsync(WeatherService.Download, lat, lon, CancellationToken.None, rl); L(o, "  " + string.Join("\n  ", rl)); L(o, "  结果：" + place.FullName + " → 显示 “" + place.Display + "”"); place.Lat = lat; place.Lon = lon; }
                catch (Exception e) { L(o, "  " + string.Join("\n  ", rl)); L(o, "  失败：" + WeatherService.Reason(e)); }
            }
            else L(o, "  没有坐标可解析。");
            if (place == null && win != null) place = new GeoPlace { Kind = "ip", Province = win.Province, City = win.City, District = win.District, CountryCode = win.Country == "CN" ? "CN" : "", Lat = win.Lat, Lon = win.Lon };
            if (place == null) { L(o, "\n无法确定位置，结束。"); return; }
            L(o, "\n[4] 天气来源（逐个，" + rounds + " 轮；位置 " + place.Display + "）");
            foreach (var p in WeatherService.ProvidersFor(place))
                for (int r = 0; r < rounds; r++)
                {
                    var prov = p; var sw1 = Stopwatch.StartNew();
                    try
                    {
                        var rd = await WeatherService.Fetch(prov, place, WeatherService.Download, CancellationToken.None).ConfigureAwait(false);
                        L(o, "  " + rd.Source + "：成功 " + sw1.ElapsedMilliseconds + "ms");
                        L(o, "      → " + rd.Temperature.ToString("0.#") + "°C 湿度" + rd.Humidity.ToString("0") + "% 风" + rd.Wind.ToString("0.#") + "km/h " + WeatherWindow.Description(rd.Code) + "，预报 " + rd.Days.Count + " 天，观测时间 " + rd.Time);
                    }
                    catch (Exception e) { L(o, "  " + prov.Name + "：失败 " + sw1.ElapsedMilliseconds + "ms — " + WeatherService.Reason(e)); }
                }
            L(o, "\n[5] 完整流程（Windows 定位 → IP 投票 → 带回退的天气）");
            var svcAll = new WeatherService();
            sw.Restart();
            try { var rd = await svcAll.ReadAsync(true, CancellationToken.None); L(o, "  成功 " + sw.ElapsedMilliseconds + "ms：" + rd.City + " " + rd.Temperature.ToString("0.#") + "°C " + WeatherWindow.Description(rd.Code) + "；天气来源 " + rd.Source + "；定位 " + rd.PlaceSource); }
            catch (Exception e) { L(o, "  失败 " + sw.ElapsedMilliseconds + "ms：" + e.Message); }
            L(o, "  步骤：\n    " + svcAll.LastReport.Replace("\n", "\n    "));
        }
    }
}
