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
            try { reading.Days=Daily(json); } catch { reading.Days=new List<ForecastDay>(); }
            return reading;
        }
        // Optional multi-day forecast; shown when the weather widget is made taller.
        internal static List<ForecastDay> Daily(Dictionary<string,object> json)
        {
            var days=new List<ForecastDay>(); object daily;
            if(!json.TryGetValue("daily",out daily) || !(daily is Dictionary<string,object>)) return days;
            var d=(Dictionary<string,object>)daily; object t,c,hi,lo;
            if(!d.TryGetValue("time",out t) || !d.TryGetValue("weather_code",out c) || !d.TryGetValue("temperature_2m_max",out hi) || !d.TryGetValue("temperature_2m_min",out lo)) return days;
            var ta=t as System.Collections.IList; var ca=c as System.Collections.IList; var ha=hi as System.Collections.IList; var la=lo as System.Collections.IList;
            if(ta==null || ca==null || ha==null || la==null) return days;
            int n=Math.Min(Math.Min(ta.Count,ca.Count),Math.Min(ha.Count,la.Count));
            for(int i=0;i<n && i<7;i++)
            {
                DateTime date; double code,max,min;
                if(!DateTime.TryParse(Convert.ToString(ta[i],CultureInfo.InvariantCulture),CultureInfo.InvariantCulture,DateTimeStyles.None,out date)) continue;
                if(!double.TryParse(Convert.ToString(ca[i],CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out code)) continue;
                if(!double.TryParse(Convert.ToString(ha[i],CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out max)) continue;
                if(!double.TryParse(Convert.ToString(la[i],CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out min)) continue;
                days.Add(new ForecastDay { Date=date.Date,Code=(int)code,Max=max,Min=min });
            }
            return days;
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
                string url="https://api.open-meteo.com/v1/forecast?latitude="+lat+"&longitude="+lon+"&current=temperature_2m,relative_humidity_2m,weather_code,wind_speed_10m&daily=weather_code,temperature_2m_max,temperature_2m_min&forecast_days=7&timezone=auto";
                var reading=Parse(location.Item1,await download(url,cancellation).ConfigureAwait(false));
                cancellation.ThrowIfCancellationRequested(); cached=reading; return reading;
            }
            finally { gate.Release(); }
        }
    }
}
