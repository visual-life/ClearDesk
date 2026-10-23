using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace ClearDesk
{
    // 2.0.7: free width/height for widgets with responsive layout, 恢复默认大小, Scale migration.
    internal static class V207Tests
    {
        public static async Task Run(Action<bool, string> check)
        {
            var b = new BoxData();
            check(b.CustomW == 0 && b.CustomH == 0, "2.0.7: widgets start without a custom size");

            var w = Program.OpenWindows.OfType<WeatherWindow>().FirstOrDefault(x => !x.WasClosed) ?? (GlassWindow)Program.NewWidget("weather");
            await Task.Delay(600);
            check(w != null && w.Scalable, "weather supports free resizing");
            if (w == null) return;
            bool wasLocked = w.TestState.Locked; if (wasLocked) w.SetLocked(false);
            double bw = w.DefaultWidth, bh = w.DefaultHeight;
            // width only: text stays 100%, layout gets wider
            w.SetCustomSize(bw * 1.5, bh, false); w.UpdateLayout(); await Task.Delay(200);
            check(Math.Abs(w.ActualWidth - bw * 1.5) < 3 && Math.Abs(w.ActualHeight - bh) < 3, "width and height are independent (" + w.ActualWidth + "×" + w.ActualHeight + ")");
            check(Math.Abs(w.ContentScale - 1) < .001 && w.ExtraWidth > bw * .4 && w.ExtraHeight < 1, "width-only resize re-flows instead of scaling");
            // height only
            w.SetCustomSize(bw, bh * 1.6, false); w.UpdateLayout();
            check(Math.Abs(w.ContentScale - 1) < .001 && w.ExtraHeight > bh * .5, "height-only resize gives extra room to the layout");
            var ww = w as WeatherWindow;
            check(WeatherWindow.ForecastFor(0, 0) == 0 && WeatherWindow.ForecastFor(0, 120) >= 3 && WeatherWindow.ForecastFor(200, 120) > WeatherWindow.ForecastFor(0, 120) && WeatherWindow.ForecastFor(900, 900) == 7, "weather shows more forecast days when larger");
            if (ww != null)
            {
                var r = new WeatherReading { City = "测试", Days = Enumerable.Range(0, 7).Select(i => new ForecastDay { Date = DateTime.Today.AddDays(i), Code = i, Max = 20 + i, Min = 10 }).ToList() };
                ww.TestShowReading(r);
                check(ww.TestForecastDays >= 3, "tall weather widget renders the forecast row");
            }
            // both: text scale = min ratio
            w.SetCustomSize(bw * 2, bh * 1.5, false);
            check(Math.Abs(w.ContentScale - 1.5) < .01, "text scales by the smaller ratio so content never overflows");
            w.SetCustomSize(1, 1, false);
            check(Math.Abs(w.ContentScale - GlassWindow.MinScale) < .001, "minimum size is clamped");
            check(w.HasCustomSize, "custom size is stored");
            w.ResetSize(false); w.UpdateLayout();
            check(!w.HasCustomSize && Math.Abs(w.ActualWidth - bw) < 3 && Math.Abs(w.ActualHeight - bh) < 3, "恢复默认大小 returns to the default size");
            // migration of the 2.0.5/2.0.6 proportional scale
            w.TestState.Scale = 1.25; w.Width = bw;
            check(Math.Abs(w.TestState.Scale - 1) < .001 && Math.Abs(w.TestState.CustomW - bw * 1.25) < 1 && Math.Abs(w.ContentScale - 1.25) < .01, "old Scale migrates to a custom size");
            var e = new WidgetEntry { Kind = "weather", Data = w.TestState };
            check(WidgetManager.CanResetSize(e), "组件管理 重置大小 sees the custom size");
            WidgetManager.ResetSize(e);
            check(!w.HasCustomSize && w.TestState.Scale == 1, "组件管理 重置大小 clears the custom size");
            var item = w.ContextMenu.Items.OfType<MenuItem>().FirstOrDefault(m => Equals(m.Tag, "size"));
            check(item != null && (string)item.Header == "恢复默认大小", "right-click menu has 恢复默认大小");
            if (wasLocked) w.SetLocked(true);

            // music lyrics get taller and show more lines
            var m = Program.OpenWindows.OfType<MusicWindow>().FirstOrDefault(x => !x.WasClosed);
            if (m != null && Program.S.MusicLyrics)
            {
                m.SetCustomSize(m.DefaultWidth, m.DefaultHeight + 150, false); m.UpdateLayout();
                check(m.TestLyricsHeight > 200 && m.TestLyricNextLines >= 3, "taller music widget shows more lyric lines (" + m.TestLyricNextLines + ")");
                m.ResetSize(false);
                check(Math.Abs(m.TestLyricsHeight - 126) < .5 && m.TestLyricNextLines == 1, "music lyrics return to default");
            }
            check(WeatherService.Daily(new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>("{\"daily\":{\"time\":[\"2026-10-22\",\"2026-10-23\"],\"weather_code\":[0,61],\"temperature_2m_max\":[22.1,19],\"temperature_2m_min\":[12,11.5]}}")).Count == 2, "forecast parsing reads daily data");
        }
    }
}
