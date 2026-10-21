using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ClearDesk
{
    // NetEase 3.x (CEF) stops updating its accessibility tree once the window is minimized: the
    // "mm:ss / mm:ss" label and the play/pause button freeze while audio keeps playing. This keeps a
    // local clock from the last live reading and advances it only while the client's audio session is
    // audible. Any live change of the label (e.g. after the window is restored) re-anchors the clock.
    internal sealed class PlaybackEstimator
    {
        static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(1.6), SilenceIsPause = TimeSpan.FromSeconds(2), MaxStep = TimeSpan.FromSeconds(5);
        string song;
        TimeSpan rawPosition, rawDuration, estimate, estimateAtSound, duration;
        DateTime rawAt, last, lastSound;
        bool staleTimeline, playing, primed;

        public bool Estimating { get; private set; }

        // peak: current output peak of the client (0..1), or -1 when it has no audio session.
        public void Apply(MusicSnapshot view, bool minimized, Func<float> peak, DateTime now, Func<string, string, TimeSpan> lookupDuration)
        {
            string id = view.Title + "\n" + view.Artist;
            if (!primed || id != song)
            {
                // Song changed. If the timeline did not move with it, the tree is frozen (title comes from
                // the live window caption): restart from zero and take the duration from the play queue.
                bool frozen = primed && view.Position == rawPosition && view.Duration == rawDuration && (Estimating || minimized);
                song = id; primed = true; rawPosition = view.Position; rawDuration = view.Duration; rawAt = last = lastSound = now;
                staleTimeline = frozen;
                duration = frozen ? Lookup(lookupDuration, view) : view.Duration;
                estimate = estimateAtSound = frozen ? TimeSpan.Zero : view.Position;
                playing = frozen || view.Playing;
                Estimating = frozen;
                if (frozen) Publish(view);
                return;
            }
            if (view.Position != rawPosition || view.Duration != rawDuration)
            {
                // Live reading: trust it and re-anchor.
                rawPosition = view.Position; rawDuration = view.Duration; rawAt = last = now;
                estimate = estimateAtSound = view.Position; duration = view.Duration; staleTimeline = false; Estimating = false;
                playing = view.Playing; if (view.Playing) lastSound = now;
                return;
            }
            if (!staleTimeline && !minimized && now - rawAt < StaleAfter) { last = now; return; }
            if (!staleTimeline && !minimized && !view.Playing && !Estimating) { last = now; estimate = view.Position; return; }
            float level = peak == null ? -1 : peak();
            if (level < 0) { if (!Estimating) { last = now; return; } level = 0; }
            if (level > 0.0005f) lastSound = now;
            bool audible = now - lastSound < SilenceIsPause;
            TimeSpan step = now - last; if (step < TimeSpan.Zero) step = TimeSpan.Zero; if (step > MaxStep) step = MaxStep;
            if (audible)
            {
                if (!playing) estimate = estimateAtSound;
                estimate += step; playing = true;
                if (level > 0.0005f) estimateAtSound = estimate;
            }
            else if (playing) { playing = false; estimate = estimateAtSound; } // paused: drop the silent tail
            last = now; Estimating = true;
            Publish(view);
        }

        void Publish(MusicSnapshot view)
        {
            if (duration > TimeSpan.Zero) view.Duration = duration;
            view.Position = view.Duration > TimeSpan.Zero && estimate > view.Duration ? view.Duration : estimate;
            view.Playing = playing;
        }

        static TimeSpan Lookup(Func<string, string, TimeSpan> lookup, MusicSnapshot view)
        {
            try { return lookup == null ? TimeSpan.Zero : lookup(view.Title, view.Artist); } catch { return TimeSpan.Zero; }
        }

        // Track duration from NetEase's own play queue (webdata\file\playingList), matched by title.
        static string queueText; static DateTime queueStamp;
        public static TimeSpan QueueDuration(string title, string artist)
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"NetEase\CloudMusic\webdata\file\playingList");
            if (!File.Exists(path)) return TimeSpan.Zero;
            var stamp = File.GetLastWriteTimeUtc(path);
            if (queueText == null || stamp != queueStamp)
            {
                var info = new FileInfo(path); if (info.Length > 16 * 1024 * 1024) return TimeSpan.Zero;
                queueText = File.ReadAllText(path, Encoding.UTF8); queueStamp = stamp;
            }
            return QueueDuration(queueText, title, artist);
        }

        internal static TimeSpan QueueDuration(string json, string title, string artist)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(title)) return TimeSpan.Zero;
            string name = "\"name\":\"" + Escape(title) + "\"";
            TimeSpan fallback = TimeSpan.Zero;
            for (int at = json.IndexOf(name, StringComparison.Ordinal); at >= 0; at = json.IndexOf(name, at + 1, StringComparison.Ordinal))
            {
                int from = Math.Max(0, at - 200);
                var matches = Regex.Matches(json.Substring(from, at - from), "\"duration\":(\\d+)");
                if (matches.Count == 0) continue;
                var value = TimeSpan.FromMilliseconds(long.Parse(matches[matches.Count - 1].Groups[1].Value));
                string tail = json.Substring(at, Math.Min(600, json.Length - at));
                string main = (artist ?? "").Split('/')[0].Trim();
                if (main.Length > 0 && tail.Contains("\"name\":\"" + Escape(main) + "\"")) return value;
                if (fallback == TimeSpan.Zero) fallback = value;
            }
            return fallback;
        }

        static string Escape(string text) { return text.Replace("\\", "\\\\").Replace("\"", "\\\""); }
    }
}
