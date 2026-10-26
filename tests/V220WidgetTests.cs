using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClearDesk
{
    /// <summary>2.2.0: logic tests of the eight new widgets (parsing, persistence, timers, clipboard filtering, fallbacks).</summary>
    internal static class V220WidgetTests
    {
        static readonly DateTime Now = new DateTime(2026, 10, 26, 10, 0, 0);
        static string TempDir(string name) { string d = Path.Combine(Path.GetTempPath(), "cd220-" + name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(d); return d; }
        static bool Near(DateTime? t, int h, int m, int tolMinutes = 3) { return t.HasValue && Math.Abs((t.Value - t.Value.Date.AddHours(h).AddMinutes(m)).TotalMinutes) <= tolMinutes; }

        internal static void Rules(Action<bool, string> check)
        {
            // ================= 待办 =================
            DateTime due;
            check(TodoLogic.TryParseDue("18:30", Now, out due) && due == new DateTime(2026, 10, 26, 18, 30, 0), "todo: '18:30' is today at 18:30");
            check(TodoLogic.TryParseDue("9:00", Now, out due) && due == new DateTime(2026, 10, 27, 9, 0, 0), "todo: a clock time that already passed means tomorrow");
            check(TodoLogic.TryParseDue("明天 9:00", Now, out due) && due == new DateTime(2026, 10, 27, 9, 0, 0), "todo: '明天 9:00'");
            check(TodoLogic.TryParseDue("后天", Now, out due) && due == new DateTime(2026, 10, 28, 9, 0, 0), "todo: a day word without time means 09:00");
            check(TodoLogic.TryParseDue("10/28 14:15", Now, out due) && due == new DateTime(2026, 10, 28, 14, 15, 0), "todo: 'M/d H:mm'");
            check(TodoLogic.TryParseDue("1/5", Now, out due) && due == new DateTime(2027, 1, 5, 9, 0, 0), "todo: a past M/d means next year");
            check(TodoLogic.TryParseDue("2026-12-01 08:00", Now, out due) && due == new DateTime(2026, 12, 1, 8, 0, 0), "todo: full date and time");
            check(TodoLogic.TryParseDue("30m", Now, out due) && due == new DateTime(2026, 10, 26, 10, 30, 0), "todo: '30m' counts from now");
            check(TodoLogic.TryParseDue("2小时后", Now, out due) && due == new DateTime(2026, 10, 26, 12, 0, 0) && TodoLogic.TryParseDue("45分钟后", Now, out due) && due == new DateTime(2026, 10, 26, 10, 45, 0), "todo: Chinese relative times");
            check(!TodoLogic.TryParseDue("", Now, out due) && !TodoLogic.TryParseDue("明天 25:00", Now, out due) && !TodoLogic.TryParseDue("2/30", Now, out due) && !TodoLogic.TryParseDue("随便", Now, out due), "todo: nonsense is rejected");
            check(TodoLogic.FormatDue(new DateTime(2026, 10, 26, 18, 0, 0), Now) == "今天 18:00" && TodoLogic.FormatDue(new DateTime(2026, 10, 27, 9, 5, 0), Now) == "明天 09:05" && TodoLogic.FormatDue(new DateTime(2026, 12, 1, 8, 0, 0), Now) == "12月1日 08:00", "todo: due text");

            var todo = new TodoData();
            var a = TodoLogic.Add(todo, "买牛奶", new DateTime(2026, 10, 26, 9, 0, 0), Now); var b = TodoLogic.Add(todo, "写周报", null, Now); var c = TodoLogic.Add(todo, "开会", new DateTime(2026, 10, 26, 11, 0, 0), Now);
            check(TodoLogic.Add(todo, "   ", null, Now) == null && todo.Items.Count == 3, "todo: an empty item is not added");
            var dueNow = TodoLogic.DueNow(todo.Items, Now);
            check(dueNow.Count == 1 && dueNow[0] == a, "todo: only items whose time has come are due");
            a.Notified = true; check(TodoLogic.DueNow(todo.Items, Now).Count == 0, "todo: an announced item does not remind twice");
            check(TodoLogic.DueNow(todo.Items, Now.AddHours(2)).Single() == c, "todo: later the next one becomes due");
            TodoLogic.SetDone(c, true, Now); check(TodoLogic.DueNow(todo.Items, Now.AddHours(2)).Count == 0 && TodoLogic.OpenCount(todo) == 2, "todo: a finished item never reminds");
            TodoLogic.SetDone(c, false, Now); check(!c.Done && !c.Notified, "todo: un-checking re-arms the reminder");
            check(TodoLogic.Move(todo.Items, 2, -1) && todo.Items[1] == c && !TodoLogic.Move(todo.Items, 0, -1) && !TodoLogic.Move(todo.Items, 2, 1), "todo: reorder moves up/down and stops at the ends");
            TodoLogic.SetDone(b, true, Now); check(TodoLogic.ClearDone(todo) == 1 && todo.Items.Count == 2, "todo: 'clear finished'");

            // ================= 倒计时 / 番茄钟 =================
            var today = new DateTime(2026, 10, 26);
            check(CountdownLogic.ParseDate("12/26", today) == new DateTime(2026, 12, 26) && CountdownLogic.ParseDate("2027-06-07", today) == new DateTime(2027, 6, 7) && CountdownLogic.ParseDate("30天后", today) == new DateTime(2026, 11, 25), "countdown: dates");
            check(CountdownLogic.ParseDate("1/1", today) == new DateTime(2027, 1, 1) && CountdownLogic.ParseDate("2026年12月26日", today) == new DateTime(2026, 12, 26), "countdown: past M/d rolls into next year; Chinese date");
            check(CountdownLogic.ParseDate("2/30", today) == null && CountdownLogic.ParseDate("abc", today) == null && CountdownLogic.ParseDate("", today) == null, "countdown: bad dates are rejected");
            var exam = new CountdownEvent { Name = "考试", Date = "2026-12-26" };
            check(CountdownLogic.DaysLeft(exam, today) == 61 && CountdownLogic.DaysText(61) == "61 天" && CountdownLogic.DaysText(0) == "今天" && CountdownLogic.DaysText(-3) == "已过 3 天", "countdown: days left and text");
            var birthday = new CountdownEvent { Name = "生日", Date = "1995-03-08", Yearly = true };
            check(CountdownLogic.DaysLeft(birthday, today) == (int)(new DateTime(2027, 3, 8) - today).TotalDays, "countdown: a yearly event counts to the next occurrence");
            var leap = new CountdownEvent { Name = "闰日", Date = "2024-02-29", Yearly = true };
            check(CountdownLogic.DaysLeft(leap, new DateTime(2026, 10, 26)) == (int)(new DateTime(2027, 2, 28) - today).TotalDays && CountdownLogic.DaysLeft(leap, new DateTime(2027, 2, 28)) == 0, "countdown: 29 Feb falls on 28 Feb in other years");
            var past = new CountdownEvent { Name = "旧", Date = "2026-10-01" };
            var sorted = CountdownLogic.Sorted(new[] { past, exam, new CountdownEvent { Name = "近", Date = "2026-10-30" } }, today);
            check(sorted[0].Name == "近" && sorted[1].Name == "考试" && sorted[2] == past, "countdown: upcoming first, finished last");
            int w, r, l;
            check(CountdownLogic.TryParseDurations("50/10/20", out w, out r, out l) && w == 50 && r == 10 && l == 20 && CountdownLogic.TryParseDurations("25,5", out w, out r, out l) && l == 15 && !CountdownLogic.TryParseDurations("0/5", out w, out r, out l) && !CountdownLogic.TryParseDurations("x", out w, out r, out l), "pomodoro: duration text");

            var clock = new DateTime(2026, 10, 26, 9, 0, 0); var p = new Pomodoro(); p.Configure(25, 5, 15, 4);
            var finished = new List<string>(); p.PhaseFinished += (d, n) => finished.Add(d + ">" + n);
            check(!p.Running && Pomodoro.Format(p.Remaining(clock)) == "25:00", "pomodoro: starts as a stopped 25:00");
            p.Start(clock); check(p.Running && Pomodoro.Format(p.Remaining(clock.AddSeconds(90))) == "23:30", "pomodoro: start counts down");
            p.Pause(clock.AddMinutes(10)); check(!p.Running && Pomodoro.Format(p.Remaining(clock.AddMinutes(99))) == "15:00", "pomodoro: pause freezes the time");
            p.Start(clock.AddMinutes(100)); check(!p.Tick(clock.AddMinutes(114)) && p.Tick(clock.AddMinutes(115)), "pomodoro: Tick finishes exactly when the time is up");
            check(p.Completed == 1 && p.Current == Pomodoro.Phase.ShortBreak && !p.Running && finished.Count == 1 && finished[0] == "Work>ShortBreak" && Pomodoro.Format(p.Left) == "05:00", "pomodoro: a finished focus round raises the event and prepares a short break");
            p.Start(clock); p.Tick(clock.AddMinutes(5)); check(p.Current == Pomodoro.Phase.Work, "pomodoro: break → focus");
            for (int i = 0; i < 3; i++) { p.Start(clock); p.Tick(clock.AddMinutes(25)); if (p.Current != Pomodoro.Phase.Work) { p.Start(clock); p.Tick(clock.AddMinutes(30)); } }
            check(p.Completed == 4 && finished.Any(x => x == "Work>LongBreak"), "pomodoro: every 4th round is followed by a long break");
            var q = new Pomodoro(); q.Start(clock); q.Skip(); check(q.Completed == 0 && q.Current == Pomodoro.Phase.ShortBreak, "pomodoro: skipping does not count as a finished round");
            q.Reset(); check(!q.Running && Pomodoro.Format(q.Left) == "05:00", "pomodoro: reset");

            // ================= 网速 =================
            check(NetMeter.Delta(100, 250) == 150 && NetMeter.Delta(4000000000L, 100) == 100 + (uint.MaxValue + 1L) - 4000000000L && NetMeter.Delta(500, 20) == 20, "netspeed: delta handles 32-bit wrap and counter reset");
            var m = new NetMeter(); var t0 = new DateTime(2026, 10, 26, 10, 0, 0);
            m.Update(t0, 1000, 500); check(m.DownSpeed == 0 && m.TodayDown == 0, "netspeed: the first sample only sets the baseline");
            m.Update(t0.AddSeconds(1), 1000 + 2048, 500 + 1024); check(Math.Abs(m.DownSpeed - 2048) < 1 && Math.Abs(m.UpSpeed - 1024) < 1 && m.TodayDown == 2048 && m.TodayUp == 1024, "netspeed: speed and today's total");
            m.Update(t0.AddSeconds(3), 1000 + 2048 + 4096, 500 + 1024); check(Math.Abs(m.DownSpeed - 2048) < 1 && m.UpSpeed == 0 && m.TodayDown == 6144, "netspeed: speeds use the real time between samples");
            for (int i = 0; i < 100; i++) m.Update(t0.AddSeconds(4 + i), 10000 + i * 100, 2000);
            check(m.Down.Count == NetMeter.History, "netspeed: history is capped at 60 samples");
            var snap = m.Snapshot(); var m2 = new NetMeter(); m2.Restore(snap, t0.AddSeconds(5)); check(m2.TodayDown == snap.Down && m2.TodayUp == snap.Up, "netspeed: today's total survives a restart");
            var m3 = new NetMeter(); m3.Restore(snap, t0.AddDays(1)); check(m3.TodayDown == 0, "netspeed: a total from yesterday is not restored");
            m.ResetBaseline(); m.Update(t0.AddDays(1), 1, 1); m.Update(t0.AddDays(1).AddSeconds(1), 1001, 1); check(m.TodayDown == 1000, "netspeed: the daily total restarts at midnight");
            check(NetMeter.Speed(512) == "512 B/s" && NetMeter.Speed(2048) == "2.0 KB/s" && NetMeter.Speed(5 * 1048576) == "5.00 MB/s" && NetMeter.Bytes(900) == "900 B" && NetMeter.Bytes(5L * 1073741824) == "5.00 GB" && NetMeter.Bytes(3 * 1048576) == "3.0 MB", "netspeed: formatting");
            check(NetSource.Counts("Intel(R) Wi-Fi 6 AX201", "WLAN", NetworkInterfaceType.Wireless80211, OperationalStatus.Up) && NetSource.Counts("Realtek PCIe GbE", "以太网", NetworkInterfaceType.Ethernet, OperationalStatus.Up), "netspeed: Wi-Fi and Ethernet count");
            check(!NetSource.Counts("Wintun Userspace Tunnel", "Meta", NetworkInterfaceType.Ethernet, OperationalStatus.Up) && !NetSource.Counts("Hyper-V Virtual Ethernet Adapter", "vEthernet (Default Switch)", NetworkInterfaceType.Ethernet, OperationalStatus.Up) && !NetSource.Counts("Intel", "WLAN", NetworkInterfaceType.Wireless80211, OperationalStatus.Down) && !NetSource.Counts("Loopback", "lo", NetworkInterfaceType.Loopback, OperationalStatus.Up), "netspeed: VPN / virtual / down / loopback adapters do not count twice");

            // ================= 日出日落 / 空气质量 =================
            var hu = Astronomy.Compute(40.81, 111.75, new DateTime(2026, 10, 26), 8);
            check(Near(hu.Sunrise, 6, 56) && Near(hu.Sunset, 17, 37), "sun: Hohhot 2026-10-26 sunrise 06:56 / sunset 17:37 (±3 min) → " + Astronomy.Clock(hu.Sunrise) + " / " + Astronomy.Clock(hu.Sunset));
            var sh = Astronomy.Compute(31.23, 121.48, new DateTime(2026, 10, 26), 8);
            check(Near(sh.Sunrise, 6, 4) && Near(sh.Sunset, 17, 11), "sun: Shanghai 2026-10-26 sunrise 06:04 / sunset 17:11 (±3 min) → " + Astronomy.Clock(sh.Sunrise) + " / " + Astronomy.Clock(sh.Sunset));
            var north = Astronomy.Compute(78.2, 15.6, new DateTime(2026, 12, 21), 1); var north2 = Astronomy.Compute(78.2, 15.6, new DateTime(2026, 6, 21), 2);
            check(north.PolarNight && !north.Sunrise.HasValue && north2.PolarDay, "sun: polar night and polar day are recognised");
            var dp = Astronomy.DayProgress(hu, new DateTime(2026, 10, 26, 12, 17, 0)); check(dp.HasValue && dp.Value > 0.4 && dp.Value < 0.6 && Astronomy.DayProgress(hu, new DateTime(2026, 10, 26, 22, 0, 0)) == null, "sun: day progress is about half at noon and empty at night");
            check(Astronomy.Length(hu.DayLength).StartsWith("10 小时"), "sun: day length text");
            check(AirLogic.Level(30) == "优" && AirLogic.Level(75) == "良" && AirLogic.Level(120) == "轻度污染" && AirLogic.Level(180) == "中度污染" && AirLogic.Level(250) == "重度污染" && AirLogic.Level(400) == "严重污染" && AirLogic.Level(-1) == "—", "air: AQI levels");
            string tencent = "{\"data\":{\"air\":{\"aqi\":57,\"aqi_level\":2,\"aqi_name\":\"良\",\"pm10\":61,\"pm2.5\":33,\"update_time\":\"202610260900\"}},\"message\":\"OK\",\"status\":200}";
            var tr = AirLogic.ParseTencent(tencent); check(tr.Aqi == 57 && tr.Pm25 == 33 && tr.Pm10 == 61 && tr.Time == "09:00" && tr.Level == "良", "air: Tencent AQI is parsed (national scale)");
            string meteo = "{\"latitude\":40.8,\"longitude\":111.75,\"current\":{\"time\":\"2026-10-26T09:00\",\"interval\":3600,\"us_aqi\":64,\"pm2_5\":18.5,\"pm10\":30.2}}";
            var mr = AirLogic.ParseOpenMeteo(meteo); check(mr.Aqi == 64 && Math.Abs(mr.Pm25 - 18.5) < 0.01 && mr.Scale.Contains("美标"), "air: Open-Meteo AQI is parsed (US scale)");
            bool bad1 = false, bad2 = false;
            try { AirLogic.ParseTencent("{\"status\":500}"); } catch (FormatException) { bad1 = true; } try { AirLogic.ParseOpenMeteo("{\"error\":true}"); } catch (FormatException) { bad2 = true; }
            check(bad1 && bad2, "air: an answer without data is an error, not a zero");
            var place = new GeoPlace { Province = "内蒙古自治区", City = "呼和浩特市", District = "赛罕区", CountryCode = "CN", Lat = 40.8, Lon = 111.75 };
            var urls = new List<string>(); var srcs = AirLogic.Sources(place, out urls);
            check(srcs.Count == 3 && urls[0].Contains("county=%E8%B5%9B%E7%BD%97") == false && urls[0].Contains("wis.qq.com") && urls[2].Contains("open-meteo") && srcs[0].Key == "腾讯天气" && srcs[1].Key.Contains("市级"), "air: China → Tencent county, Tencent city, then Open-Meteo");
            var abroad = new GeoPlace { City = "Tokyo", CountryCode = "JP", Lat = 35.7, Lon = 139.7 }; List<string> u2; check(AirLogic.Sources(abroad, out u2).Count == 1 && u2[0].Contains("open-meteo"), "air: outside China only Open-Meteo is used");
            var log = new List<string>(); var calls = new List<string>();
            Func<string, CancellationToken, Task<string>> flaky = (url, ct) => { calls.Add(url); if (url.Contains("wis.qq.com")) throw new InvalidOperationException("boom"); return Task.FromResult(meteo); };
            var fr = AirLogic.FetchAsync(place, flaky, CancellationToken.None, log).GetAwaiter().GetResult();
            check(fr != null && fr.Aqi == 64 && fr.Source == "Open-Meteo" && calls.Count == 3 && log.Count == 3 && log[0].Contains("失败") && log[2].Contains("成功"), "air: both Tencent calls fail → falls back to Open-Meteo");
            var none = AirLogic.FetchAsync(place, (url, ct) => { throw new InvalidOperationException("offline"); }, CancellationToken.None, new List<string>()).GetAwaiter().GetResult();
            check(none == null, "air: when everything fails the result is null (the widget keeps its cache)");
            var county = AirLogic.FetchAsync(place, (url, ct) => Task.FromResult(tencent), CancellationToken.None, null).GetAwaiter().GetResult(); check(county != null && county.Aqi == 57 && county.Source == "腾讯天气", "air: Tencent first, one call is enough");

            // ================= 快捷启动 =================
            string dir = TempDir("launch"); string file = Path.Combine(dir, "readme.txt"); File.WriteAllText(file, "x");
            check(LauncherLogic.Classify("https://example.com/a") == "url" && LauncherLogic.Classify(dir) == "folder" && LauncherLogic.Classify(file) == "file" && LauncherLogic.Classify(@"C:\x\app.exe") == "app" && LauncherLogic.Classify(@"C:\x\a.lnk") == "app" && LauncherLogic.Classify("shell:AppsFolder\\Microsoft.Windows.Photos_8wekyb3d8bbwe!App") == "app", "launcher: item kinds");
            check(LauncherLogic.NormalizeUrl("example.com") == "https://example.com/" && LauncherLogic.NormalizeUrl("http://a.cn/x?y=1") == "http://a.cn/x?y=1" && LauncherLogic.NormalizeUrl("not a url") == null && LauncherLogic.NormalizeUrl("") == null && LauncherLogic.NormalizeUrl("ftp://a.com") == null && LauncherLogic.NormalizeUrl("localhost") == "https://localhost/", "launcher: URL normalisation");
            check(LauncherLogic.NameFor("https://www.bilibili.com/video") == "bilibili.com" && LauncherLogic.NameFor(file) == "readme" && LauncherLogic.NameFor(@"D:\Games\") == "Games", "launcher: display names");
            var ld = new LauncherData(); check(LauncherLogic.Add(ld, file) != null && LauncherLogic.Add(ld, file.ToUpperInvariant()) == null && ld.Items.Count == 1, "launcher: no duplicates");
            for (int i = 0; i < 70; i++) LauncherLogic.Add(ld, "https://site" + i + ".com/"); check(ld.Items.Count == 60, "launcher: at most 60 items");
            check(LauncherLogic.Move(ld.Items, 1, -1) && ld.Items[0].Path.Contains("site0") && !LauncherLogic.Move(ld.Items, 0, -1) && LauncherLogic.Columns(340, 40) == 5 && LauncherLogic.Columns(10, 40) == 1, "launcher: reorder and responsive columns");

            // ================= 图片相册 =================
            string pics = TempDir("album"); Directory.CreateDirectory(Path.Combine(pics, "sub"));
            foreach (var n in new[] { "b.jpg", "a.png", "c.txt", "sub\\d.JPEG", "e.gif" }) File.WriteAllText(Path.Combine(pics, n), "x");
            var top = AlbumLogic.Enumerate(new[] { pics }, false); var all = AlbumLogic.Enumerate(new[] { pics, Path.Combine(pics, "a.png") }, true);
            check(top.Count == 3 && Path.GetFileName(top[0]) == "a.png" && !top.Any(x => x.EndsWith("c.txt")), "album: only images, sorted, no subfolders by default");
            check(all.Count == 4 && all.Count(x => x.EndsWith("a.png")) == 1, "album: subfolders on, duplicates removed");
            check(AlbumLogic.Enumerate(new[] { Path.Combine(pics, "missing") }, true).Count == 0 && AlbumLogic.Enumerate(null, true).Count == 0 && AlbumLogic.Enumerate(new[] { pics }, true, 2).Count == 2, "album: missing folders are ignored and the list is capped");
            var seq = new AlbumLogic.Playlist(3, false, null); check(seq.Next() == 0 && seq.Next() == 1 && seq.Next() == 2 && seq.Next() == 0 && seq.Previous() == 2, "album: sequential order and previous");
            var sh1 = new AlbumLogic.Playlist(5, true, new Random(7)); var firstCycle = Enumerable.Range(0, 5).Select(_ => sh1.Next()).ToList(); int last = firstCycle[4]; int nextFirst = sh1.Next();
            check(firstCycle.Distinct().Count() == 5 && nextFirst != last, "album: a shuffled cycle shows every picture once and does not repeat across the cycle boundary");
            check(new AlbumLogic.Playlist(0, true, null).Next() == -1 && AlbumLogic.ClampInterval(0) == 2 && AlbumLogic.ClampInterval(99999) == 3600 && AlbumLogic.ClampInterval(10) == 10, "album: empty playlist and interval limits");
            check(AlbumLogic.DecodeWidth(320, 1.5) == 480 && AlbumLogic.DecodeWidth(3000, 2) == 1920 && AlbumLogic.DecodeWidth(10, 1) == 64, "album: DecodePixelWidth follows the box size and is capped (memory)");

            // ================= 剪贴板 =================
            Func<string, object> noRead = n => null;
            check(ClipboardHistory.ShouldSkip(new[] { "Text", "ExcludeClipboardContentFromMonitorProcessing" }, noRead, null), "clipboard: the 'exclude from monitoring' format is skipped (password managers set it)");
            check(ClipboardHistory.ShouldSkip(new[] { "Text", "CanIncludeInClipboardHistory" }, n => new MemoryStream(new byte[] { 0, 0, 0, 0 }), null), "clipboard: CanIncludeInClipboardHistory = 0 is skipped");
            check(!ClipboardHistory.ShouldSkip(new[] { "Text", "CanIncludeInClipboardHistory" }, n => new MemoryStream(new byte[] { 1, 0, 0, 0 }), null), "clipboard: CanIncludeInClipboardHistory = 1 is recorded");
            check(ClipboardHistory.ShouldSkip(new[] { "Text" }, noRead, @"C:\Program Files\KeePassXC\KeePassXC.exe") && ClipboardHistory.ShouldSkip(new[] { "Text" }, noRead, @"C:\x\1Password.exe") && ClipboardHistory.ShouldSkip(new[] { "Text" }, noRead, @"C:\x\Bitwarden.exe") && !ClipboardHistory.ShouldSkip(new[] { "Text" }, noRead, @"C:\Windows\notepad.exe") && !ClipboardHistory.ShouldSkip(new[] { "Text" }, noRead, null), "clipboard: password-manager owners are skipped, ordinary apps are not");
            check(ClipboardHistory.ShouldSkip(new[] { "Text", "CanIncludeInClipboardHistory" }, n => { throw new InvalidOperationException(); }, null) == false, "clipboard: an unreadable flag does not crash");
            var h = new ClipboardHistory();
            check(h.AddText("hello", Now) && h.AddText("world", Now.AddSeconds(1)) && h.Items[0].Text == "world", "clipboard: newest first");
            h.AddText("hello", Now.AddSeconds(2)); check(h.Items.Count == 2 && h.Items[0].Text == "hello", "clipboard: copying the same text again moves it to the top, no duplicate");
            check(!h.AddText("   ", Now) && !h.AddText(new string('x', h.MaxChars + 1), Now) && h.Items.Count == 2, "clipboard: blank and oversized text is ignored");
            h.TogglePin(h.Items[1]); var ordered = h.Ordered(); check(ordered[0].Text == "world" && ordered[0].Pinned, "clipboard: pinned entries are listed first");
            h.MaxText = 3; for (int i = 0; i < 6; i++) h.AddText("t" + i, Now.AddMinutes(i + 5)); check(h.Items.Count(i => i.Kind == "text") == 4 && h.Items.Any(i => i.Text == "world"), "clipboard: text cap keeps the newest and never drops pinned entries");
            h.MaxImages = 2; var img = new Func<int, byte[]>(n => Enumerable.Repeat((byte)n, 100).ToArray()); h.AddImage(img(1), Now); h.AddImage(img(2), Now); h.AddImage(img(3), Now);
            check(h.Items.Count(i => i.Kind == "image") == 2 && !h.Items.Any(i => i.Kind == "image" && i.Png[0] == 1), "clipboard: image cap drops the oldest image");
            h.MaxImageBytes = 50; check(!h.AddImage(img(9), Now), "clipboard: an image over the size limit is ignored");
            h.MaxImageBytes = 4 << 20; h.MaxTotalImageBytes = 150; h.AddImage(img(4), Now); check(h.Items.Where(i => i.Kind == "image").Sum(i => i.Png.Length) <= 150, "clipboard: total image memory is capped");
            h.Paused = true; check(!h.AddText("secret", Now) && !h.AddImage(img(5), Now), "clipboard: paused → nothing is recorded"); h.Paused = false;
            string blob = h.Protect(); check(!blob.Contains("world") && blob.Length > 20, "clipboard: the saved form is encrypted (no plain text)");
            var h2 = new ClipboardHistory(); h2.Unprotect(blob); check(h2.Items.Any(i => i.Text == "world" && i.Pinned) && h2.Items.All(i => i.Kind == "text"), "clipboard: encrypted text round-trips (DPAPI), images are never persisted");
            var h3 = new ClipboardHistory(); h3.Unprotect("not base64!!"); check(h3.Items.Count == 0, "clipboard: a damaged file is ignored");
            int cleared = h.Clear(true); check(h.Items.All(i => i.Pinned) && cleared > 0, "clipboard: clear keeps pinned entries"); h.Clear(false); check(h.Items.Count == 0, "clipboard: clear all");
            check(ClipboardHistory.Preview("a\r\nb\t\tc  d") == "a b c d" && ClipboardHistory.Preview(new string('x', 300)).Length == 141, "clipboard: single-line preview");

            // ================= 日程 =================
            DateTime st; string title; bool allDay;
            check(ScheduleLogic.ParseQuick("明天 14:00 项目评审", Now, out st, out title, out allDay) && st == new DateTime(2026, 10, 27, 14, 0, 0) && title == "项目评审" && !allDay, "schedule: '明天 14:00 项目评审'");
            check(ScheduleLogic.ParseQuick("10/28 9:30 面试", Now, out st, out title, out allDay) && st == new DateTime(2026, 10, 28, 9, 30, 0) && title == "面试", "schedule: 'M/d H:mm title'");
            check(ScheduleLogic.ParseQuick("后天 生日聚会", Now, out st, out title, out allDay) && allDay && st == new DateTime(2026, 10, 28) && title == "生日聚会", "schedule: no time → all day");
            check(ScheduleLogic.ParseQuick("18:00 跑步", Now, out st, out title, out allDay) && st == new DateTime(2026, 10, 26, 18, 0, 0), "schedule: no day → today");
            check(!ScheduleLogic.ParseQuick("明天 14:00", Now, out st, out title, out allDay) && !ScheduleLogic.ParseQuick("", Now, out st, out title, out allDay) && !ScheduleLogic.ParseQuick("明天 25:00 x", Now, out st, out title, out allDay), "schedule: a title is required and times are validated");
            var ev = new List<ScheduleEvent> {
                new ScheduleEvent { Title = "午饭", Start = "2026-10-26 12:00", End = "2026-10-26 13:00" }, new ScheduleEvent { Title = "晨会", Start = "2026-10-26 09:00" },
                new ScheduleEvent { Title = "生日", Start = "2026-10-26 00:00", AllDay = true }, new ScheduleEvent { Title = "明天的", Start = "2026-10-27 08:00" },
                new ScheduleEvent { Title = "出差", Start = "2026-10-25 00:00", End = "2026-10-27 00:00", AllDay = true }, new ScheduleEvent { Title = "坏", Start = "xx" } };
            var todayList = ScheduleLogic.On(ev, Now); check(string.Join(",", todayList.Select(e => e.Title)) == "出差,生日,晨会,午饭", "schedule: today = all-day first, then by time, multi-day events included");
            check(ScheduleLogic.On(ev, Now.AddDays(1)).Select(e => e.Title).SequenceEqual(new[] { "出差", "明天的" }) && ScheduleLogic.TimeText(ev[0], Now) == "12:00–13:00" && ScheduleLogic.TimeText(ev[2], Now) == "全天", "schedule: tomorrow and time text");
            string ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nSUMMARY:周会\\, 全员\r\nDTSTART:20261026T020000Z\r\nDTEND:20261026T030000Z\r\nLOCATION:会议室 A\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nSUMMARY:国庆\r\nDTSTART;VALUE=DATE:20261001\r\nDTEND;VALUE=DATE:20261004\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nSUMMARY:一个很长\r\n  的标题\r\nDTSTART;TZID=Asia/Shanghai:20261027T093000\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nSUMMARY:没有开始\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
            var cn = TimeZoneInfo.CreateCustomTimeZone("cn", TimeSpan.FromHours(8), "cn", "cn"); var imported = ScheduleLogic.ParseIcs(ics, cn);
            check(imported.Count == 3 && imported[0].Title == "周会, 全员" && imported[0].Start == "2026-10-26 10:00" && imported[0].End == "2026-10-26 11:00" && imported[0].Place == "会议室 A", "schedule: .ics UTC times are converted to local time; escapes are decoded");
            check(imported[1].AllDay && imported[1].Start == "2026-10-01 00:00" && imported[1].End == "2026-10-03 00:00", "schedule: .ics all-day events (exclusive end)");
            check(imported[2].Title == "一个很长 的标题" && imported[2].Start == "2026-10-27 09:30", "schedule: .ics folded lines and TZID times");
            var sd = new ScheduleData(); sd.Events.Add(new ScheduleEvent { Title = "本地", Start = "2026-10-26 09:00" }); ScheduleLogic.ReplaceIcs(sd, imported); ScheduleLogic.ReplaceIcs(sd, imported);
            check(sd.Events.Count == 4 && sd.Events.Count(e => e.Source == "local") == 1, "schedule: re-importing replaces the imported events and keeps local ones");
            sd.Events.Add(new ScheduleEvent { Title = "很久以前", Start = "2026-01-01 09:00" }); check(ScheduleLogic.Prune(sd, Now) >= 1 && !sd.Events.Any(e => e.Title == "很久以前"), "schedule: old events are pruned");

            // ================= 存储 =================
            string root = TempDir("store"); string oldRoot = ToolStore.TestRoot; ToolStore.TestRoot = root;
            try
            {
                check(ToolStore.Load<TodoData>("todo").Items.Count == 0, "store: a missing file loads as empty");
                var td = new TodoData(); TodoLogic.Add(td, "持久化", new DateTime(2026, 11, 1, 8, 0, 0), Now); td.HideDone = true;
                check(ToolStore.Save("todo", td) && File.Exists(Path.Combine(root, "todo.json")) && !Directory.GetFiles(root, "*.tmp").Any(), "store: atomic save leaves no temp file");
                var back = ToolStore.Load<TodoData>("todo"); check(back.Items.Count == 1 && back.Items[0].Text == "持久化" && back.Items[0].Due == "2026-11-01 08:00" && back.HideDone, "store: todo data round-trips");
                var cd = new CountdownData(); cd.Events.Add(new CountdownEvent { Name = "生日", Date = "1995-03-08", Yearly = true }); cd.WorkMinutes = 40; ToolStore.Save("countdown", cd);
                var cb = ToolStore.Load<CountdownData>("countdown"); check(cb.Events.Count == 1 && cb.Events[0].Yearly && cb.WorkMinutes == 40, "store: countdown data round-trips");
                File.WriteAllText(Path.Combine(root, "launcher.json"), "{ broken"); var lb = ToolStore.Load<LauncherData>("launcher");
                check(lb.Items.Count == 0 && Directory.GetFiles(root, "launcher.json.corrupt-*").Length == 1, "store: a damaged file is kept aside and the widget starts empty");
                var cs = new ClipboardSaved { Protected = h.Protect() }; ToolStore.Save("clipboard-history", cs); check(!File.ReadAllText(Path.Combine(root, "clipboard-history.json")).Contains("world"), "store: clipboard history file holds no plain text");
            }
            finally { ToolStore.TestRoot = oldRoot; try { Directory.Delete(root, true); Directory.Delete(dir, true); Directory.Delete(pics, true); } catch { } }

            // ================= 注册 / 菜单 =================
            check(ToolWidgets.Kinds.Length == 8 && ToolWidgets.Kinds.All(k => ToolWidgets.Descriptions.ContainsKey(k.Key) && ToolWidgets.IsTool(k.Key) && WidgetCatalog.IsKnown(k.Key) && WidgetCatalog.Kinds.Any(c => c.Key == k.Key && c.Value == k.Value)), "widgets: all 8 are registered in the settings list and in the 新建组件 menu");
            check(!ToolWidgets.IsTool("clock") && !ToolWidgets.IsTool("note"), "widgets: the old widgets are not tool widgets");
        }

        static void Snapshot(System.Windows.FrameworkElement element, string name)
        {
            element.UpdateLayout(); int w = (int)Math.Ceiling(element.ActualWidth), h = (int)Math.Ceiling(element.ActualHeight); if (w <= 0 || h <= 0) return;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) { dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(40, 70, 110), Color.FromRgb(15, 25, 45), 90), null, new System.Windows.Rect(0, 0, w, h)); dc.DrawRectangle(new VisualBrush(element), null, new System.Windows.Rect(0, 0, w, h)); }
            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.Combine(Program.Base, "preview"));
            using (var f = File.Create(Path.Combine(Program.Base, "preview", "widget-" + name + ".png"))) enc.Save(f);
        }
        static BitmapSource Tiny() { var b = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 255, 0, 0, 255 }, 4); b.Freeze(); return b; }

        internal static async Task Run(Action<bool, string> check)
        {
            string root = TempDir("win"); string oldRoot = ToolStore.TestRoot; ToolStore.TestRoot = root; bool oldOnline = Program.S.WeatherOnline; Program.S.WeatherOnline = false;
            var oldNotify = TodoWindow.Notify; var opened = new List<GlassWindow>();
            try
            {
                var probe = ToolWidget.Input("提示文字"); var probeWindow = new System.Windows.Window { Content = probe, Width = 200, Height = 80, Left = -4000, Top = -4000, ShowInTaskbar = false }; probeWindow.Show(); probe.ApplyTemplate(); probe.UpdateLayout();
                var hintBlock = probe.Template.FindName("hint", probe) as System.Windows.Controls.TextBlock;
                bool shownEmpty = hintBlock != null && hintBlock.Visibility == System.Windows.Visibility.Visible && hintBlock.Text == "提示文字";
                probe.Text = "x"; probe.UpdateLayout(); bool hiddenTyped = hintBlock != null && hintBlock.Visibility != System.Windows.Visibility.Visible; probeWindow.Close();
                check(shownEmpty && hiddenTyped, "input box: the grey hint shows while empty and disappears when typing");
                foreach (var kind in ToolWidgets.Kinds)
                {
                    var w = ToolWidgets.Create(kind.Key); w.Show(); opened.Add(w);
                    check(w != null && w.IsVisible && w.Width > 100 && w.Height > 80 && ((ToolWidget)w).Kind == kind.Key, "widget window '" + kind.Key + "': is created, shown and registered under its own id");
                }
                foreach (var w in opened.ToArray()) ToolWidgets.Set(((ToolWidget)w).Kind, true);
                check(ToolWidgets.Kinds.All(k => ToolWidgets.On(k.Key)), "widgets: the settings flag is on for every created kind");
                ToolWidgets.Set("todo", false); check(!ToolWidgets.On("todo") && ToolWidgets.On("album"), "widgets: the settings switch turns exactly one kind off"); ToolWidgets.Set("todo", true);
                foreach (var w in opened) w.Close(); opened.Clear();

                // todo: a due item reminds once, the reminder state is saved
                var items = new TodoData(); TodoLogic.Add(items, "交报告", new DateTime(2026, 10, 26, 9, 0, 0), Now); ToolStore.Save("todo", items);
                var notes = new List<string>(); TodoWindow.Notify = (t, x) => notes.Add(t + ":" + x);
                var todo = new TodoWindow { Now = () => Now }; todo.Show(); opened.Add(todo);
                todo.CheckReminders(); todo.CheckReminders();
                check(notes.Count == 1 && notes[0].Contains("交报告") && todo.Reminders.Count == 1 && ToolStore.Load<TodoData>("todo").Items[0].Notified, "todo window: the due item reminds exactly once and is saved as announced");
                // countdown window: pomodoro start / pause
                var cdw = new CountdownWindow { Now = () => Now }; cdw.Show(); opened.Add(cdw);
                cdw.ToggleRun(); bool running = cdw.Timer2.Running; cdw.ToggleRun();
                check(running && !cdw.Timer2.Running, "countdown window: the start button starts and pauses the pomodoro");
                // net speed window
                long rx = 0; var net = new NetSpeedWindow { Now = () => Now, Source = () => new[] { rx, rx / 2 } }; net.Show(); opened.Add(net);
                net.Sample(); rx += 4096; net.Now = () => Now.AddSeconds(1); net.Sample();
                check(net.Meter.TodayDown >= 4096 && net.Meter.DownSpeed > 1000, "netspeed window: samples turn into speed and a daily total");
                // clipboard window
                var clip = new ClipboardWindow { Now = () => Now }; clip.Show(); opened.Add(clip);
                clip.Inject("复制的内容"); clip.Inject("复制的内容");
                check(clip.History.Items.Count == 1 && clip.History.Items[0].Text == "复制的内容", "clipboard window: copied text is listed once");
                check(!File.Exists(Path.Combine(root, "clipboard-history.json")), "clipboard window: nothing is written to disk by default");
                // launcher window
                string f = Path.Combine(root, "a.txt"); File.WriteAllText(f, "x"); var lw = new LauncherWindow(); lw.Show(); opened.Add(lw);
                check(lw.AddPath(f) && !lw.AddPath(f) && lw.Data.Items.Count == 1 && ToolStore.Load<LauncherData>("launcher").Items.Count == 1, "launcher window: adding a file saves it, duplicates are refused");
                // album window with a fake loader (no real decoding)
                string pics = Path.Combine(root, "pics"); Directory.CreateDirectory(pics); File.WriteAllText(Path.Combine(pics, "1.jpg"), "x"); File.WriteAllText(Path.Combine(pics, "2.png"), "x");
                var album = new AlbumWindow { Loader = (p, w) => Tiny(), Animate = false }; album.Show(); opened.Add(album);
                album.AddSource(pics); album.ShowNext(); string first = album.CurrentPath; album.ShowNext();
                check(first != null && album.CurrentPath != null && album.CurrentPath != first && album.ShownCount >= 2 && ToolStore.Load<AlbumData>("album").Sources.Count == 1, "album window: folder is added and saved, the slideshow moves to the next picture");
                // sunrise / air window with fake network
                var sun = new SunAirWindow(); sun.Show(); opened.Add(sun);
                try { await sun.LastRefresh; } catch { }
                var p = new GeoPlace { Province = "内蒙古自治区", City = "呼和浩特市", District = "赛罕区", CountryCode = "CN", Lat = 40.8, Lon = 111.75 };
                var asked = new List<string>();
                sun.Now = () => Now; sun.Locate = ct => Task.FromResult(p);
                sun.Download = (u, ct) => { asked.Add(u); if (u.Contains("wis.qq.com")) throw new InvalidOperationException("blocked"); return Task.FromResult("{\"current\":{\"time\":\"2026-10-26T09:00\",\"us_aqi\":42,\"pm2_5\":9.1,\"pm10\":20}}"); };
                await sun.RefreshAsync();
                var cache = ToolStore.Load<SunAirCache>("sunair");
                check(asked.Count == 3 && cache.Aqi == 42 && cache.Source == "Open-Meteo" && cache.PlaceName == "赛罕区", "sun/air window: falls back to Open-Meteo and caches the result for the next start");
                sun.Download = (u, ct) => { throw new InvalidOperationException("offline"); }; await sun.RefreshAsync();
                check(ToolStore.Load<SunAirCache>("sunair").Aqi == 42, "sun/air window: when every source fails the cached value is kept");
                // schedule window
                var sw = new ScheduleWindow { Now = () => Now }; sw.Show(); opened.Add(sw);
                sw.Data.Events.Add(new ScheduleEvent { Title = "测试日程", Start = "2026-10-26 15:00" });
                check(ScheduleLogic.On(sw.Data.Events, Now).Count == 1, "schedule window: created with a working data set");
                // pictures of every widget (preview folder of the test run) for a visual check
                foreach (var w in opened) { try { Snapshot(w, ((ToolWidget)w).Kind); } catch (Exception e) { Program.Log(e); } }
            }
            finally
            {
                TodoWindow.Notify = oldNotify;
                foreach (var w in opened) { try { w.Close(); } catch { } }
                ToolStore.TestRoot = oldRoot; Program.S.WeatherOnline = oldOnline;
                foreach (var k in ToolWidgets.Kinds) ToolWidgets.Set(k.Key, false);
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
