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

        async Task<T> Run<T>(Func<T> operation, T unavailable, int timeout = 1500)
        {
            Task<T> work;
            lock (gate)
            {
                if (disposed || (pending != null && !pending.IsCompleted)) return unavailable;
                work = Task.Run(() => { try { return disposed ? unavailable : operation(); } catch { return unavailable; } });
                pending = work;
            }
            // A hung accessibility provider must not freeze the card or accumulate workers.
            return await Task.WhenAny(work, Task.Delay(timeout)).ConfigureAwait(false) == work
                ? await work.ConfigureAwait(false) : unavailable;
        }

        readonly PlaybackEstimator clock = new PlaybackEstimator();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        delegate bool EnumProc(IntPtr window, IntPtr data);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr data);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr window, out int pid);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int max);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetClassName(IntPtr window, System.Text.StringBuilder text, int max);

        static IntPtr HiddenWindow(int processId, out string caption)
        {
            IntPtr found = IntPtr.Zero; string text = "";
            EnumWindows((w, d) =>
            {
                int pid; GetWindowThreadProcessId(w, out pid);
                if (pid != processId) return true;
                var b = new System.Text.StringBuilder(512); GetWindowText(w, b, 512);
                string t, a; if (!ParseTitle(b.ToString(), out t, out a)) return true;
                var c = new System.Text.StringBuilder(128); GetClassName(w, c, 128);
                if (c.ToString().IndexOf("Orpheus", StringComparison.OrdinalIgnoreCase) < 0 && found != IntPtr.Zero) return true;
                found = w; text = b.ToString();
                return c.ToString().IndexOf("Orpheus", StringComparison.OrdinalIgnoreCase) < 0;
            }, IntPtr.Zero);
            caption = text; return found;
        }

        sealed class Target
        {
            public Element Root, Play, Seek;
            public IntPtr Window;
            public Element[] Controls;
            public string Key, Title, Artist;
            public bool TitleOnly; // CEF accessibility tree is empty: only the window title is readable
        }

        Target Find(MusicAccessibility ui)
        {
            var processes = Process.GetProcessesByName("cloudmusic");
            Target titleOnly = null;
            foreach (var process in processes)
            {
                using (process)
                {
                    if (disposed) return null;
                    try
                    {
                        IntPtr handle = process.MainWindowHandle;
                        string caption = process.MainWindowTitle, title, artist;
                        // Closed to the tray: the main window is hidden, so MainWindowHandle is 0. Its caption
                        // still carries "title - artist"; look the hidden top-level window up directly.
                        if (handle == IntPtr.Zero || !ParseTitle(caption, out title, out artist)) handle = HiddenWindow(process.Id, out caption);
                        if (handle == IntPtr.Zero || !ParseTitle(caption, out title, out artist)) continue;
                        var root = ui.Window(handle);
                        var play = ui.FindPlayback(root);
                        if (play == null)
                        {
                            // NetEase 3.x's embedded Chromium exposes no web content to accessibility unless it
                            // was started with --force-renderer-accessibility. The window title still carries
                            // "title - artist", which is enough for lyrics (without timeline / controls).
                            if (titleOnly == null) titleOnly = new Target { Root = root, Window = handle, Title = title, Artist = artist, Controls = new Element[0], TitleOnly = true,
                                Key = "netease-ui:" + process.Id + ":" + process.StartTime.ToUniversalTime().Ticks + ":" + handle.ToInt64() };
                            continue;
                        }
                        string playName = MusicAccessibility.Name(play);
                        if (playName != "pause" && playName != "play") continue;
                        var parent = ui.Parent(play);
                        // The progress bar sits a few levels above the transport buttons in the CEF tree.
                        Element seek = null, region = parent;
                        for (int level = 0; level < 4 && region != null && seek == null; level++, region = ui.Parent(region))
                            seek = ui.FindSeekBar(region, 3);
                        return new Target { Root = root, Play = play, Seek = seek, Window = handle, Title = title, Artist = artist, Controls = parent == null ? new Element[0] : ui.Children(parent),
                            Key = "netease-ui:" + process.Id + ":" + process.StartTime.ToUniversalTime().Ticks + ":" + handle.ToInt64() };
                    }
                    catch { }
                }
            }
            return titleOnly;
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
                    if (target.TitleOnly)
                        return new MusicSnapshot { Key = target.Key, Source = "netease", Title = target.Title, Artist = target.Artist,
                            Message = "网易云未开放播放控件：仅显示歌名和歌词。右键 →“以同步模式重启网易云”可启用进度、切歌和逐句同步。" };
                    var view = new MusicSnapshot { Key = target.Key, Source = "netease",
                        Title = target.Title, Artist = target.Artist, Playing = MusicAccessibility.Name(target.Play) == "pause",
                        CanToggle = ui.CanInvoke(target.Play), CanPrevious = ui.CanInvoke(Transport(target, "pre")),
                        CanNext = ui.CanInvoke(Transport(target, "next")) };
                    view.CanSeek = target.Seek != null;
                    var region = ui.Parent(target.Play);
                    for (int level = 0; level < 3 && region != null; level++, region = ui.Parent(region))
                    {
                        var times = ui.Labels(region, 2).ToArray();
                        if (ReadTimeline(times, view)) break;
                    }
                    // Minimized CEF freezes the tree; keep the clock running from the client's audio output.
                    var rawPos = view.Position; float seen = -2; bool iconic = IsIconic(target.Window) || !IsWindowVisible(target.Window);
                    clock.Apply(view, iconic, () => seen = AudioPeak.Peak("cloudmusic"), DateTime.UtcNow, PlaybackEstimator.QueueDuration);
                    MusicTrace.Write("ne iconic=" + iconic + " raw=" + rawPos + " rawPlay=" + (MusicAccessibility.Name(target.Play)) + " peak=" + seen + " pub=" + view.Position + " play=" + view.Playing + " est=" + clock.Estimating);
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
                    if (target == null || target.Key != key || target.TitleOnly || disposed) return false;
                    var button = command == MusicCommand.Toggle ? target.Play : Transport(target, command == MusicCommand.Next ? "next" : "pre");
                    return ui.Invoke(button);
                }
            }, false);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr window, ref POINT point);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT point);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsChild(IntPtr parent, IntPtr child);
        struct POINT { public int X, Y; }

        // NetEase 3.x publishes no SMTC session, so TryChangePlaybackPositionAsync is unavailable.
        // Best effort: post a synthetic click (no real cursor movement, no focus change) at the
        // matching point of the client's own accessible progress bar.
        public Task<bool> SeekAsync(string key, TimeSpan position)
        {
            return Run(() =>
            {
                using (var ui = new MusicAccessibility())
                {
                    if (key == null || key != activeKey) return false;
                    var target = Find(ui);
                    if (target == null || target.Key != key || target.Seek == null || disposed) return false;
                    TimeSpan duration = TimeSpan.Zero;
                    var probe = new MusicSnapshot();
                    var region = ui.Parent(target.Play);
                    for (int level = 0; level < 3 && region != null; level++, region = ui.Parent(region))
                        if (ReadTimeline(ui.Labels(region, 2).ToArray(), probe)) { duration = probe.Duration; break; }
                    if (duration <= TimeSpan.Zero) return false;
                    double ratio = Math.Max(0, Math.Min(1, position.TotalSeconds / duration.TotalSeconds));
                    // Minimized: restore invisibly without activation, click, minimize again (see WindowPeek).
                    return WindowPeek.WithRestored(target.Window, () =>
                    {
                        int left = 0, top = 0, width = 0, height = 0;
                        bool located = WindowPeek.Wait(() =>
                        {
                            using (var fresh = new MusicAccessibility())
                            {
                                var again = Find(fresh);
                                return again != null && again.Seek != null && fresh.Location(again.Seek, out left, out top, out width, out height) && left > -10000;
                            }
                        }, ok => ok, 1500);
                        if (!located) return false;
                        bool clicked = WindowPeek.PostClick(target.Window, left + (int)Math.Round(ratio * (width - 1)), top + height / 2);
                        System.Threading.Thread.Sleep(WindowPeek.SettleMs);
                        return clicked;
                    });
                }
            }, false, 6000);
        }

        internal const string AccessibilitySwitch = "--force-renderer-accessibility";

        // User-initiated: restart NetEase with Chromium accessibility so playback controls become readable.
        public static string RestartWithAccessibility()
        {
            string path = null;
            foreach (var p in Process.GetProcessesByName("cloudmusic"))
                using (p) { try { if (path == null || p.MainWindowHandle != IntPtr.Zero) path = p.MainModule.FileName; } catch { } }
            if (path == null)
            {
                try
                {
                    using (var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"orpheus\shell\open\command"))
                    {
                        string cmd = key == null ? null : key.GetValue("") as string;
                        if (cmd != null) { int a = cmd.IndexOf('"'), b = a >= 0 ? cmd.IndexOf('"', a + 1) : -1; if (b > a) path = cmd.Substring(a + 1, b - a - 1); }
                    }
                }
                catch { }
            }
            if (path == null || !System.IO.File.Exists(path)) return "未找到网易云音乐程序，请先手动打开网易云音乐。";
            foreach (var p in Process.GetProcessesByName("cloudmusic"))
                using (p) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
            System.Threading.Thread.Sleep(800);
            Process.Start(new ProcessStartInfo(path, AccessibilitySwitch) { UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(path) });
            return null;
        }

        public void Dispose() { disposed = true; }
    }
}
