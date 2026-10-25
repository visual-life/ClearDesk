using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClearDesk
{
    internal static class FeatureTests
    {
        const string LocationJson="{\"success\":true,\"city\":\"示例城市\",\"latitude\":31.2345,\"longitude\":121.4789}";
        // IP services as the weather service sees them (three domestic services agreeing on one city)
        static string FakeNetwork(string url)
        {
            if(url.Contains("pconline")) return "{\"ip\":\"x\",\"pro\":\"示例省\",\"city\":\"示例城市\",\"region\":\"\",\"err\":\"\"}";
            if(url.Contains("ipip.net")) return "{\"ret\":\"ok\",\"data\":{\"ip\":\"x\",\"location\":[\"中国\",\"示例省\",\"示例城市\",\"\"]}}";
            if(url.Contains("bilibili")) return "{\"code\":0,\"data\":{\"country\":\"中国\",\"province\":\"示例省\",\"city\":\"示例城市\",\"latitude\":\"31.2345\",\"longitude\":\"121.4789\"}}";
            throw new IOException("HTTP 404");
        }
        const string WeatherJson="{\"current\":{\"temperature_2m\":23.5,\"relative_humidity_2m\":65,\"weather_code\":3,\"wind_speed_10m\":8.2,\"time\":\"2026-10-21T10:15\"}}";
        internal static void Capture(Window window,string name)
        {
            window.UpdateLayout(); var root=(FrameworkElement)window.Content;
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),(int)Math.Ceiling(root.ActualHeight),96,96,PixelFormats.Pbgra32); bitmap.Render(root);
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(var file=File.Create(Path.Combine(Program.Base,name+".png"))) encoder.Save(file);
        }
        sealed class TickProbe : GlassWindow
        {
            internal int Count;
            internal TickProbe(BoxData state) : base(state,"Drag probe",true) { Tick(30,delegate { Count++; }); }
            internal bool BackgroundReceivesInput { get { return Root.InputHitTest(new Point(Width/2,Height-30))!=null; } }
        }
        static async Task CheckSubmenu(ContextMenu menu,Window owner,string header,string name,Action<bool,string> check)
        {
            var item=menu.Items.OfType<MenuItem>().Single(m=>Equals(m.Header,header));
            try
            {
                menu.PlacementTarget=owner; menu.IsOpen=true; await Task.Delay(60); item.IsSubmenuOpen=true; await Task.Delay(100); item.ApplyTemplate();
                var panel=(Border)item.Template.FindName("SubmenuSurface",item);
                check(panel!=null && ((SolidColorBrush)panel.Background).Color.R>240 && item.Items.OfType<MenuItem>().All(m=>m.IsEnabled),"new "+name+" submenu uses the light Windows 11 styling with actionable choices");
                var bitmap=new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth),(int)Math.Ceiling(panel.ActualHeight),96,96,PixelFormats.Pbgra32); bitmap.Render(panel);
                var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using(var file=File.Create(Path.Combine(Program.Base,name+".png"))) encoder.Save(file);
            }
            finally { item.IsSubmenuOpen=false; menu.IsOpen=false; }
        }
        internal static async Task Run(Action<bool,string> check)
        {
            int requests=0; string forecastUrl=null; bool fail=false;
            var service=new WeatherService(async (url,token)=> { requests++; await Task.Delay(10,token); if(fail) throw new IOException("offline"); if(url.Contains("wis.qq.com")) throw new IOException("HTTP 503"); if(url.Contains("open-meteo")) { forecastUrl=url; return WeatherJson; } return FakeNetwork(url); }, token=>Task.FromResult<GeoPlace>(null));
            service.ExpectChina=()=>true; service.UseSystemLocation=()=>false; service.ManualSource=()=>null;
            var culture=Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture=CultureInfo.GetCultureInfo("fr-FR");
                var pair=await Task.WhenAll(service.ReadAsync(false,CancellationToken.None),service.ReadAsync(false,CancellationToken.None));
                check(requests==5 && ReferenceEquals(pair[0],pair[1]),"weather concurrent refreshes share the cached result and avoid duplicate network requests (3 IP services + 1 failed + 1 working weather source)");
                check(forecastUrl.Contains("latitude=31.23&longitude=121.48") && !forecastUrl.Contains("31,23"),"weather sends rounded city coordinates with invariant decimal formatting");
                check(pair[0].City=="示例城市" && pair[0].Temperature==23.5 && pair[0].Time=="10-21 10:15" && pair[0].Source=="Open-Meteo","weather parses city temperature and observation time, falling back from the failing first source");
                await service.ReadAsync(true,CancellationToken.None);
                check(requests==10,"explicit weather refresh bypasses the cache");
            }
            finally { Thread.CurrentThread.CurrentCulture=culture; }
            bool rejected=false;
            try { WeatherService.Parse("test","{\"current\":{}}"); } catch(FormatException) { rejected=true; }
            check(rejected,"missing weather fields are rejected instead of displaying fabricated zero values");
            using(var cancellation=new CancellationTokenSource())
            {
                cancellation.Cancel(); rejected=false;
                try { await service.ReadAsync(true,cancellation.Token); } catch(OperationCanceledException) { rejected=true; }
                check(rejected && requests==10,"cancelled weather refresh makes no network request");
            }
            bool online=Program.S.WeatherOnline; WeatherWindow weather=null;
            try
            {
                Program.S.WeatherOnline=false; weather=new WeatherWindow(service); weather.Show();
                check(requests==10 && weather.TestTemperature=="未联网","disabled online weather sends no request");
                Program.S.WeatherOnline=true; await weather.FetchAsync(true);
                check(weather.TestDescription.Contains("示例城市") && weather.TestTemperature=="24°","weather component displays the automatic location and fetched temperature");
                Capture(weather,"weather-example"); fail=true; await weather.FetchAsync(true);
                check(weather.TestTemperature=="24°" && weather.TestDescription.Contains("示例城市") && weather.TestDetail.Contains("更新于") && weather.TestDetail.Contains("网络异常") && !weather.TestDescription.Contains("暂不可用"),"failed weather refresh keeps the previous reading and says how old it is instead of an error card");
                weather.Close(); weather=null;
                var started=new TaskCompletionSource<bool>(); bool cancelled=false;
                var slow=new WeatherService(async (url,token)=> { started.TrySetResult(true); try { await Task.Delay(30000,token); } catch(OperationCanceledException) { cancelled=true; throw; } return LocationJson; }, token=>Task.FromResult<GeoPlace>(null)); slow.UseSystemLocation=()=>false; slow.ManualSource=()=>null;
                Program.S.WeatherOnline=false; weather=new WeatherWindow(slow); weather.Show(); Program.S.WeatherOnline=true;
                var pending=weather.FetchAsync(true); await started.Task; weather.Close(); await pending;
                check(cancelled,"closing the weather component cancels its pending network operation"); weather=null;
            }
            finally { if(weather!=null) weather.Close(); Program.S.WeatherOnline=online; }

            var box=new BoxData { Title="Feature test", Enabled=false, Collapsed=true };
            Program.S.Boxes.Add(box); var originalWindows=Program.App.Windows.Cast<Window>().ToArray();
            try
            {
                string file=OrganizerFiles.Create(box,"note.txt",false); File.WriteAllText(file,"keep me");
                string duplicate=OrganizerFiles.Create(box,"note.txt",false);
                check(file!=duplicate && File.ReadAllText(file)=="keep me" && File.Exists(duplicate),"organizer new file automatically numbers duplicates without overwriting content");
                string directory=OrganizerFiles.Create(box,"folder",true);
                check(Directory.Exists(directory) && box.Items.Contains(directory),"organizer creates real folders and adds their references");
                check(new[]{"../escape.txt","CON.txt","bad.","a/b",""}.All(n=>!OrganizerFiles.ValidName(n)),"organizer rejects path traversal and Windows reserved filenames");
                var desktop=new BoxData { Desktop=true };
                check(OrganizerFiles.Folder(desktop).StartsWith(Program.Data,StringComparison.OrdinalIgnoreCase),"preview file creation stays isolated from the real desktop");
                box.Excluded.Add(file); Program.SendToOrganizer(file,box); Program.SendToOrganizer(file,box);
                check(box.Items.Count(p=>p==file)==1 && !box.Excluded.Contains(file) && File.ReadAllText(file)=="keep me","sending to organizer deduplicates references and leaves the original file untouched");
                await CheckSubmenu(Program.App.Windows.OfType<OrganizerWindow>().Single(w=>w.ExportName==box.Id).ContextMenu,Program.App.Windows.OfType<OrganizerWindow>().Single(w=>w.ExportName==box.Id),"新建","organizer-create-menu",check);
                check(box.Enabled && !box.Collapsed && Program.App.Windows.OfType<OrganizerWindow>().Any(w=>w.ExportName==box.Id && w.IsVisible),"sending to a disabled organizer reopens and expands it");
                check(originalWindows.All(w=>Program.App.Windows.Cast<Window>().Contains(w)),"sending to organizer does not rebuild or close other widgets");
                int count=Program.S.Boxes.Count; Program.SendToOrganizer(file,null);
                check(Program.S.Boxes.Count==count+1 && Program.S.Boxes.Last().Items.Contains(file),"launchpad can create a new organizer containing the selected reference");
                Program.Launchpad(); await Task.Delay(150);
                var pad=Program.App.Windows.OfType<LaunchpadWindow>().First();
                var menu=pad.BuildItemMenu(file);
                check(menu.Items.OfType<MenuItem>().Any(m=>Equals(m.Header,"发送至桌面收纳") && m.Items.Count>1),"launchpad context menu exposes existing and new organizer destinations");
                await CheckSubmenu(menu,pad,"发送至桌面收纳","launchpad-send-menu",check); pad.Close();
            }
            finally
            {
                foreach(var window in Program.App.Windows.OfType<OrganizerWindow>().Where(w=>!originalWindows.Contains(w)).ToArray()) { Program.S.Boxes.RemoveAll(b=>b.Id==window.ExportName); window.Close(); }
                Program.S.Boxes.Remove(box); Program.Save();
            }
            var probeState=new BoxData { Width=280,Height=180 }; var probe=new TickProbe(probeState); probe.Show(); await Task.Delay(130);
            check(probe.BackgroundReceivesInput && probe.Count>1,"plain widget background receives pointer input beyond the title area");
            probeState.Collapsed=true; int before=probe.Count; await Task.Delay(130);
            check(probe.Count==before,"collapsed widgets skip periodic refresh work");
            probeState.Collapsed=false; probe.Hide(); before=probe.Count; await Task.Delay(130);
            check(probe.Count==before,"hidden widgets skip periodic refresh work");
            probe.Show(); await Task.Delay(130); check(probe.Count>before,"widget refresh resumes after it becomes visible"); probe.Close();
            var root=new Grid(); var button=new Button(); var text=new TextBlock { Text="label" }; button.Content=text; root.Children.Add(button);
            var host=new Window { Content=root,Width=200,Height=100,ShowInTaskbar=false }; host.Show(); host.UpdateLayout();
            check(WidgetDrag.IsInteractive(text,root),"expanded widget drag area excludes text within action buttons");
            root.Children.Remove(button); root.Children.Add(text=new TextBlock { Text="draggable label" }); host.UpdateLayout();
            check(!WidgetDrag.IsInteractive(text,root),"widget labels and blank backgrounds remain draggable");
            check(WidgetDrag.IsInteractive(new Slider(),root) && WidgetDrag.IsInteractive(new TextBox(),root),"widget dragging does not steal slider or text input interactions"); host.Close();

        }
        public static async void LiveWeather()
        {
            WeatherWindow window=null;
            try
            {
                await WeatherService.Shared.ReadAsync(true,CancellationToken.None);
                Program.S.WeatherOnline=false; window=new WeatherWindow(); window.Show(); Program.S.WeatherOnline=true;
                await window.FetchAsync(false);
                bool ok=window.TestTemperature!="未联网" && window.TestTemperature!="—" && !window.TestDescription.Contains("暂不可用");
                File.WriteAllText(Path.Combine(Program.Base,"weather-live-test.txt"),(ok?"PASS: ":"FAIL: ")+"automatic location and real weather displayed through the application service; no location values recorded.");
                if(!ok) Environment.ExitCode=1;
            }
            catch(Exception error) { File.WriteAllText(Path.Combine(Program.Base,"weather-live-test.txt"),"FAIL: "+error.GetType().Name+" / "+error.GetBaseException().GetType().Name+" / "+error.GetBaseException().HResult+" / "+(error.GetBaseException() is System.Net.WebException ? ((System.Net.WebException)error.GetBaseException()).Status.ToString() : "")+" / "+(error is System.Net.Http.HttpRequestException ? error.Message : "request or display failed")); Environment.ExitCode=1; }
            finally { if(window!=null) window.Close(); Program.Quit(); }
        }
    }
}
