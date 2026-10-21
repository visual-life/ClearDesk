using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClearDesk
{
    internal static class MusicTests
    {
        sealed class FakeSessions : IMusicSessions
        {
            public TaskCompletionSource<MusicSnapshot> Pending;
            public MusicSnapshot Value = new MusicSnapshot();
            public bool Disposed, Accept = true;
            public readonly List<string> Commands = new List<string>();
            public Task<MusicSnapshot> ReadAsync(string source)
            {
                var pending = Pending;
                Pending = null;
                return pending == null ? Task.FromResult(Value) : pending.Task;
            }
            public Task<bool> CommandAsync(string key, MusicCommand command)
            {
                Commands.Add(key + ":" + command);
                return Task.FromResult(Accept);
            }
            public readonly List<TimeSpan> Seeks = new List<TimeSpan>();
            public Task<bool> SeekAsync(string key, TimeSpan position) { Seeks.Add(position); return Task.FromResult(Accept); }
            public void Dispose() { Disposed = true; }
        }

        public static void Rules(Action<bool, string> check)
        {
            string title, artist;
            TimeSpan time;
            check(NetEaseMusic.ParseTitle("A - B - Artist", out title, out artist) && title == "A - B" && artist == "Artist" &&
                !NetEaseMusic.ParseTitle("网易云音乐", out title, out artist), "NetEase compatibility ignores idle windows and preserves song title separators");
            check(NetEaseMusic.ParseTime("04:15", out time) && time.TotalSeconds == 255 &&
                !NetEaseMusic.ParseTime("200", out time), "NetEase timeline does not interpret comment counts as playback time");
            var timeline = new MusicSnapshot();
            check(NetEaseMusic.ReadTimeline(new[] { "999+", "01:02", "04:15" }, timeline) && timeline.Position.TotalSeconds == 62 &&
                timeline.Duration.TotalSeconds == 255 && NetEaseMusic.ReadTimeline(new[] { "01:10 / 03:20" }, timeline) &&
                timeline.Duration.TotalSeconds == 200, "NetEase playback and lyrics views parse their distinct timeline formats");
            {
                // 1.9.1: minimized NetEase freezes its tree; the estimator keeps time from the audio meter.
                var clock = new PlaybackEstimator(); var t0 = new DateTime(2026, 1, 1); float level = 0.4f;
                Func<double, MusicSnapshot> frozen = s => { var v = new MusicSnapshot { Title = "Song", Artist = "Artist", Position = TimeSpan.FromSeconds(10), Duration = TimeSpan.FromSeconds(180), Playing = true }; clock.Apply(v, true, () => level, t0.AddSeconds(s), (a, b) => TimeSpan.FromSeconds(200)); return v; };
                frozen(0); MusicSnapshot e = null; for (int i = 1; i <= 60; i++) e = frozen(i);
                check(clock.Estimating && Math.Abs(e.Position.TotalSeconds - 70) < 0.01 && e.Playing, "minimized NetEase keeps advancing from the last live position while audio plays");
                level = 0; frozen(61); var paused = frozen(64);
                check(!paused.Playing && Math.Abs(paused.Position.TotalSeconds - 70) < 0.01 && Math.Abs(frozen(70).Position.TotalSeconds - 70) < 0.01, "silent audio session pauses the estimate without drifting");
                level = 0.3f; check(Math.Abs(frozen(75).Position.TotalSeconds - 75) < 0.01, "resumed audio continues the estimate");
                var next = new MusicSnapshot { Title = "Next", Artist = "Artist", Position = TimeSpan.FromSeconds(10), Duration = TimeSpan.FromSeconds(180), Playing = true };
                clock.Apply(next, true, () => level, t0.AddSeconds(76), (a, b) => TimeSpan.FromSeconds(200));
                check(next.Position == TimeSpan.Zero && next.Duration.TotalSeconds == 200, "song change from the window title restarts the estimate with the queue duration");
                var live = new MusicSnapshot { Title = "Next", Artist = "Artist", Position = TimeSpan.FromSeconds(3), Duration = TimeSpan.FromSeconds(200), Playing = true };
                clock.Apply(live, false, () => level, t0.AddSeconds(79), null);
                check(!clock.Estimating && live.Position.TotalSeconds == 3, "a live reading after restore replaces the estimate");
                check(PlaybackEstimator.QueueDuration("{\"track\":{\"duration\":188117,\"mvid\":\"\",\"name\":\"Demo\",\"artists\":[{\"id\":\"1\",\"name\":\"Singer\"}]}}", "Demo", "Singer").TotalMilliseconds == 188117, "NetEase play queue supplies track durations");
            }
            check(MusicSources.Identify("QQMusic.exe") == "qq" && MusicSources.Identify("cloudmusic.exe") == "netease" && MusicSources.Identify("KuGou.exe") == "kugou", "music identifies all three requested clients");
            check(MusicSources.Rank("qq", "netease", true, true) < 0 && MusicSources.Rank("auto", null, true, true) < 0, "explicit music source never redirects to another client or browser");
            check(MusicSources.Rank("auto", "qq", true, false) > MusicSources.Rank("auto", "netease", false, true), "automatic music selection prefers playing clients");
            check(MusicSources.ClampPosition(TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(60)).TotalSeconds == 60 && MusicSources.ClampPosition(TimeSpan.FromSeconds(-5), TimeSpan.Zero) == TimeSpan.Zero, "music timeline clamps stale or invalid positions");
            check(MusicSources.Normalize("unknown") == "auto" && MusicSources.Time(TimeSpan.FromSeconds(3661)) == "1:01:01", "music settings and long-track timestamps normalize safely");
        }

        public static async Task Run(Action<bool, string> check)
        {
            Rules(check);
            string saved = Program.S.MusicSource;
            Program.S.MusicSource = "qq";
            var backend = new FakeSessions();
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 255, 100, 0, 255 }, 4);
            bitmap.Freeze();
            backend.Value = new MusicSnapshot { Key = "qq-session-1", Source = "qq", Title = "Test song", Artist = "Test artist", Cover = bitmap, Playing = true, CanToggle = true, CanNext = true, CanPrevious = true };
            var window = new MusicWindow(backend);
            try
            {
                window.Show();
                await Task.Delay(100);
                check(window.TestTitle == "Test song" && window.TestHasCover && window.TestCanNext, "music view displays current song, cover and client controls");
                await window.TestCommand(MusicCommand.Next);
                await window.TestCommand(MusicCommand.Previous);
                await window.TestCommand(MusicCommand.Toggle);
                check(string.Join(",", backend.Commands) == "qq-session-1:Next,qq-session-1:Previous,qq-session-1:Toggle", "all music controls route to the displayed session key");
                backend.Pending = new TaskCompletionSource<MusicSnapshot>();
                var waitingRead = backend.Pending;
                window.TestRefresh();
                var queuedCommand = window.TestCommand(MusicCommand.Next);
                await Task.Delay(60);
                check(backend.Commands.Count == 3, "music command waits for in-flight metadata instead of racing it");
                waitingRead.SetResult(backend.Value);
                await queuedCommand;
                check(backend.Commands.Count == 4 && backend.Commands[3] == "qq-session-1:Next", "music click during refresh is delivered once to its original session");
                backend.Accept = false;
                await window.TestCommand(MusicCommand.Next);
                check(window.TestStatus.Contains("未接受"), "music command rejection is visible instead of claiming success");
                backend.Pending = new TaskCompletionSource<MusicSnapshot>();
                var pending = backend.Pending;
                window.TestRefresh();
                window.TestSelect("netease");
                pending.SetResult(new MusicSnapshot { Key = "old", Title = "Stale QQ song" });
                await Task.Delay(100);
                check(window.TestTitle != "Stale QQ song" && !window.TestCanNext && !window.TestHasCover, "source changes reject late metadata and clear old artwork and controls");
                backend.Value = new MusicSnapshot { Message = "Disconnected" };
                window.TestRefresh();
                await Task.Delay(100);
                check(window.TestTitle == "暂无播放" && !window.TestCanNext, "disconnected music sessions clear playback state");
                backend.Pending = new TaskCompletionSource<MusicSnapshot>();
                pending = backend.Pending;
                window.TestRefresh();
                window.Close();
                pending.SetResult(new MusicSnapshot { Title = "Late result" });
                await Task.Delay(100);
                check(backend.Disposed && window.TestTitle != "Late result", "closing music view disposes backend and ignores in-flight results");
            }
            finally
            {
                if (!window.WasClosed) window.Close();
                Program.S.MusicSource = saved;
            }
        }
    }
}
