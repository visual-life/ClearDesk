using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Interop;

namespace ClearDesk
{
    // 2.0.8: lyric font scaling, NetEase minimized clock (audio peak COM clash), locked widgets pinned to the bottom.
    internal static class V208Tests
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class OtherEnumeratorClass { }
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);

        public static async Task Run(Action<bool, string> check)
        {
            // NetEase: another [ComImport] class for the same CLSID used to make AudioPeak throw InvalidCast in-app.
            try { var other = new OtherEnumeratorClass(); GC.KeepAlive(other); } catch { }
            AudioPeak.Peak("cleardesk-no-such-process");
            check(AudioPeak.LastError == null, "2.0.8: audio peak works next to other Core Audio users (was InvalidCast: " + AudioPeak.LastError + ")");
            CoreAudio.Enumerator();
            AudioPeak.Peak("cleardesk-no-such-process");
            check(AudioPeak.LastError == null, "audio peak still works after quick controls created its enumerator");

            // estimator: a paused client must not start advancing just because the tree froze
            var clock = new PlaybackEstimator(); var t0 = new DateTime(2026, 1, 1);
            var v = new MusicSnapshot { Title = "A", Artist = "B", Position = TimeSpan.FromSeconds(30), Duration = TimeSpan.FromSeconds(200), Playing = false };
            clock.Apply(v, true, () => 0f, t0, null);
            for (int i = 1; i <= 4; i++) { v = new MusicSnapshot { Title = "A", Artist = "B", Position = TimeSpan.FromSeconds(30), Duration = TimeSpan.FromSeconds(200), Playing = false }; clock.Apply(v, true, () => 0f, t0.AddSeconds(i * .75), null); }
            check(!v.Playing && v.Position == TimeSpan.FromSeconds(30), "paused NetEase stays paused while minimized");
            clock = new PlaybackEstimator();
            v = new MusicSnapshot { Title = "A", Artist = "B", Position = TimeSpan.FromSeconds(30), Duration = TimeSpan.FromSeconds(200), Playing = true };
            clock.Apply(v, true, () => .3f, t0, null);
            for (int i = 1; i <= 8; i++) { v = new MusicSnapshot { Title = "A", Artist = "B", Position = TimeSpan.FromSeconds(30), Duration = TimeSpan.FromSeconds(200), Playing = true }; clock.Apply(v, true, () => .3f, t0.AddSeconds(i), null); }
            check(v.Playing && v.Position.TotalSeconds > 36 && v.Position.TotalSeconds < 39, "minimized NetEase keeps advancing from its audio (" + v.Position + ")");

            // lyric font scale
            check(MusicWindow.LyricScale(0, 0) == 1, "lyrics keep 100% font at the default size");
            check(MusicWindow.LyricScale(150, 0) > 1.3 && MusicWindow.LyricScale(150, 0) <= 1.5, "taller widget enlarges lyric font (capped by width)");
            check(MusicWindow.LyricScale(150, 200) > MusicWindow.LyricScale(150, 0), "wider widget allows a larger lyric font");
            check(MusicWindow.LyricScale(5000, 5000) == 2.4, "lyric font has a sensible maximum");
            var m = Program.OpenWindows.OfType<MusicWindow>().FirstOrDefault(x => !x.WasClosed);
            if (m != null && Program.S.MusicLyrics)
            {
                m.SetCustomSize(m.DefaultWidth, m.DefaultHeight + 150, false); m.UpdateLayout();
                check(m.LyricFontScale > 1.3, "stretched music widget uses larger lyric text (" + m.LyricFontScale.ToString("0.00") + ")");
                m.ResetSize(false);
                check(m.LyricFontScale == 1, "lyric text returns to normal after 恢复默认大小");
            }

            // locked widgets stay at the bottom
            var w = Program.OpenWindows.OfType<WeatherWindow>().FirstOrDefault(x => !x.WasClosed) ?? (GlassWindow)Program.NewWidget("weather");
            await Task.Delay(400);
            if (w == null) { check(false, "weather widget available"); return; }
            bool was = w.TestState.Locked;
            w.SetLocked(true);
            check(w.PinnedToBottom, "locked widget is pinned to the bottom layer");
            var h = new WindowInteropHelper(w).Handle;
            w.Activate(); await Task.Delay(300);
            int me = System.Diagnostics.Process.GetCurrentProcess().Id, above = 0;
            for (IntPtr x = GetWindow(h, 2); x != IntPtr.Zero; x = GetWindow(x, 2))
            {
                if (!IsWindowVisible(x)) continue; int pid; GetWindowThreadProcessId(x, out pid); if (pid == me) continue;
                var c = new StringBuilder(64); GetClassName(x, c, 64); string cls = c.ToString();
                if (cls == "Progman" || cls == "WorkerW" || (GetWindowLong(x, -20) & 0x8) != 0) continue;
                above++;
            }
            check(above == 0, "activating a locked widget does not raise it above other apps (" + above + " app windows below it)");
            w.SetLocked(false);
            check(!w.PinnedToBottom, "unlocked widget may come forward");
            if (was) w.SetLocked(true);
            check(!Program.OpenWindows.OfType<DockWindow>().Any(d => d.PinnedToBottom) && !Program.OpenWindows.OfType<LaunchpadWindow>().Any(d => d.PinnedToBottom), "Dock / 启动台 are never pinned");
        }
    }
}
