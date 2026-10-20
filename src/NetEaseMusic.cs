using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Element = ClearDesk.MusicAccessibility.Element;

namespace ClearDesk
{
    // Compatibility for desktop clients that expose accessible transport buttons,
    // but do not publish an SMTC session. Never sends global keys or takes focus.
    internal sealed class NetEaseMusic : IDisposable
    {
        readonly object gate = new object();
        Task pending;
        volatile bool disposed;
        string activeKey;
        public static bool ParseTitle(string text, out string title, out string artist)
        {
            title = artist = "";
            int split = (text ?? "").LastIndexOf(" - ", StringComparison.Ordinal);
            if (split < 1 || split + 3 >= text.Length) return false;
            title = text.Substring(0, split).Trim();
            artist = text.Substring(split + 3).Trim();
            return title.Length > 0 && artist.Length > 0;
        }

        public static bool ParseTime(string text, out TimeSpan value)
        {
            return TimeSpan.TryParseExact(text, new[] { @"m\:ss", @"mm\:ss", @"h\:mm\:ss", @"hh\:mm\:ss" },
                CultureInfo.InvariantCulture, out value);
        }

        async Task<T> Run<T>(Func<T> operation, T unavailable)
        {
            Task<T> work;
            lock (gate)
            {
                if (disposed || (pending != null && !pending.IsCompleted)) return unavailable;
                work = Task.Run(() => { try { return disposed ? unavailable : operation(); } catch { return unavailable; } });
                pending = work;
            }
            // A hung accessibility provider must not freeze the card or accumulate workers.
            return await Task.WhenAny(work, Task.Delay(1500)).ConfigureAwait(false) == work
                ? await work.ConfigureAwait(false) : unavailable;
        }

        sealed class Target
        {
            public Element Root, Play;
            public Element[] Controls;
            public string Key, Title, Artist;
        }

        Target Find(MusicAccessibility ui)
        {
            var processes = Process.GetProcessesByName("cloudmusic");
            foreach (var process in processes)
            {
                using (process)
                {
                    if (disposed) return null;
                    try
                    {
                        IntPtr handle = process.MainWindowHandle;
                        string title, artist;
                        if (handle == IntPtr.Zero || !ParseTitle(process.MainWindowTitle, out title, out artist)) continue;
                        var root = ui.Window(handle);
                        var play = ui.FindPlayback(root);
                        if (play == null) continue;
                        string playName = MusicAccessibility.Name(play);
                        if (playName != "pause" && playName != "play") continue;
                        var parent = ui.Parent(play);
                        return new Target { Root = root, Play = play, Title = title, Artist = artist, Controls = parent == null ? new Element[0] : ui.Children(parent),
                            Key = "netease-ui:" + process.Id + ":" + process.StartTime.ToUniversalTime().Ticks + ":" + handle.ToInt64() };
                    }
                    catch { }
                }
            }
            return null;
        }

        static Element Transport(Target target, string name)
        {
            return target.Controls.FirstOrDefault(e => MusicAccessibility.IsButton(e) && MusicAccessibility.Name(e) == name);
        }

        public Task<MusicSnapshot> ReadAsync()
        {
            return Run(() =>
            {
                using (var ui = new MusicAccessibility())
                {
                    var target = Find(ui);
                    if (target == null) { activeKey = null; return null; }
                    activeKey = target.Key;
                    var view = new MusicSnapshot { Key = target.Key, Source = "netease",
                        Title = target.Title, Artist = target.Artist, Playing = MusicAccessibility.Name(target.Play) == "pause",
                        CanToggle = ui.CanInvoke(target.Play), CanPrevious = ui.CanInvoke(Transport(target, "pre")),
                        CanNext = ui.CanInvoke(Transport(target, "next")) };
                    var region = ui.Parent(target.Play);
                    for (int level = 0; level < 3 && region != null; level++, region = ui.Parent(region))
                    {
                        var times = ui.Labels(region, 2).ToArray();
                        if (ReadTimeline(times, view)) break;
                    }
                    return view;
                }
            }, (MusicSnapshot)null);
        }

        internal static bool ReadTimeline(string[] labels, MusicSnapshot view)
        {
            foreach (string label in labels)
            {
                var pair = label.Split('/');
                TimeSpan position, duration;
                if (pair.Length == 2 && ParseTime(pair[0].Trim(), out position) && ParseTime(pair[1].Trim(), out duration))
                { view.Duration = duration; view.Position = MusicSources.ClampPosition(position, duration); return true; }
            }
            var parsed = labels.Select(t => { TimeSpan value; return ParseTime(t, out value) ? (TimeSpan?)value : null; })
                .Where(t => t.HasValue).Select(t => t.Value).ToArray();
            if (parsed.Length != 2) return false;
            view.Duration = parsed[1];
            view.Position = MusicSources.ClampPosition(parsed[0], view.Duration);
            return true;
        }

        public Task<bool> CommandAsync(string key, MusicCommand command)
        {
            return Run(() =>
            {
                using (var ui = new MusicAccessibility())
                {
                    if (key == null || key != activeKey) return false;
                    var target = Find(ui);
                    if (target == null || target.Key != key || disposed) return false;
                    var button = command == MusicCommand.Toggle ? target.Play : Transport(target, command == MusicCommand.Next ? "next" : "pre");
                    return ui.Invoke(button);
                }
            }, false);
        }

        public void Dispose() { disposed = true; }
    }
}
