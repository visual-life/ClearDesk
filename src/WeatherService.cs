using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ClearDesk
{
    internal sealed class WeatherReading
    {
        internal string City, Time;
        internal double Temperature, Humidity, Wind;
        internal int Code;
        internal DateTime Received;
    }
    internal interface IWeatherService
    {
        Task<WeatherReading> ReadAsync(bool force, CancellationToken cancellation);
    }
    internal sealed class WeatherService : IWeatherService
    {
        internal static readonly WeatherService Shared = new WeatherService();
        static readonly HttpClient client = new HttpClient(new HttpClientHandler { AllowAutoRedirect=false }) { Timeout=TimeSpan.FromSeconds(15) };
        readonly Func<string,CancellationToken,Task<string>> download;
        readonly SemaphoreSlim gate=new SemaphoreSlim(1,1);
        WeatherReading cached;
        internal WeatherService(Func<string,CancellationToken,Task<string>> transport=null) { download=transport??Download; }
        static async Task<string> Download(string url,CancellationToken cancellation)
        {
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                using(var response=await client.GetAsync(url,timeout.Token).ConfigureAwait(false))
                { response.EnsureSuccessStatusCode(); return await response.Content.ReadAsStringAsync().ConfigureAwait(false); }
            }
        }
        internal static double Number(Dictionary<string,object> json,string key)
        {
            object value; double number;
            if(!json.TryGetValue(key,out value) || value==null || !double.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out number) || double.IsNaN(number) || double.IsInfinity(number)) throw new FormatException("天气服务返回了不完整的数据。");
            return number;
        }
        internal static Tuple<string,double,double> Location(string text)
        {
            var json=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(text);
            object success; if(!json.TryGetValue("success",out success) || !(success is bool) || !(bool)success) throw new InvalidOperationException("暂时无法获取所在地，请稍后重试。");
            object name; string city=json.TryGetValue("city",out name)?name as string:null;
            if(string.IsNullOrWhiteSpace(city)) city=json.TryGetValue("region",out name)?name as string:null;
            if(string.IsNullOrWhiteSpace(city)) throw new FormatException("定位服务未返回城市。");
            double lat=Number(json,"latitude"),lon=Number(json,"longitude");
            if(Math.Abs(lat)>90 || Math.Abs(lon)>180) throw new FormatException("定位服务坐标无效。");
            return Tuple.Create(city,lat,lon);
        }
        internal static WeatherReading Parse(string city,string text)
        {
            var json=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(text);
            object current; if(!json.TryGetValue("current",out current) || !(current is Dictionary<string,object>)) throw new FormatException("天气服务未返回当前天气。");
            var data=(Dictionary<string,object>)current;
            var reading=new WeatherReading { City=city,Temperature=Number(data,"temperature_2m"),Humidity=Number(data,"relative_humidity_2m"),Wind=Number(data,"wind_speed_10m"),Code=(int)Number(data,"weather_code"),Received=DateTime.UtcNow };
            object time; DateTime observed;
            if(!data.TryGetValue("time",out time) || !DateTime.TryParse(Convert.ToString(time,CultureInfo.InvariantCulture),CultureInfo.InvariantCulture,DateTimeStyles.None,out observed)) throw new FormatException("天气服务未返回观测时间。");
            reading.Time=observed.ToString("MM-dd HH:mm");
            return reading;
        }
        public async Task<WeatherReading> ReadAsync(bool force,CancellationToken cancellation)
        {
            await gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if(!force && cached!=null && DateTime.UtcNow-cached.Received<TimeSpan.FromMinutes(20)) return cached;
                var location=Location(await download("https://ipwho.is/?lang=zh-CN&fields=success,city,region,latitude,longitude,message",cancellation).ConfigureAwait(false));
                cancellation.ThrowIfCancellationRequested();
                // City-level weather needs no precise device location; do not persist IP or coordinates.
                string lat=Math.Round(location.Item2,2).ToString("0.00",CultureInfo.InvariantCulture),lon=Math.Round(location.Item3,2).ToString("0.00",CultureInfo.InvariantCulture);
                string url="https://api.open-meteo.com/v1/forecast?latitude="+lat+"&longitude="+lon+"&current=temperature_2m,relative_humidity_2m,weather_code,wind_speed_10m&timezone=auto";
                var reading=Parse(location.Item1,await download(url,cancellation).ConfigureAwait(false));
                cancellation.ThrowIfCancellationRequested(); cached=reading; return reading;
            }
            finally { gate.Release(); }
        }
    }
}
