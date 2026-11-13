using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClearDesk
{
    /// <summary>2.3.1: frame-time harness for the animations (启动台 open / close, 收纳格 collapse / expand and search slide). `--perf231 [out.json]`.
    /// Counts WPF render callbacks (CompositionTarget.Rendering, de-duplicated by RenderingTime) and UI-thread stalls (a 4 ms DispatcherTimer that should tick every ~4-16 ms).
    /// Preview mode: isolated data folder, no hooks. Synthetic: this is the time WPF needs per frame on this machine, not monitor FPS.</summary>
    internal static class V231Perf
    {
        internal sealed class Stats
        {
            public string Name; public int Frames; public double WallMs, GapP50, GapP95, GapMax, UiStallMax; public int Over20, Over33;
        }

        static Stats Measure(string name, Action start, int ms)
        {
            var gaps = new List<double>(); var stalls = new List<double>();
            var watch = Stopwatch.StartNew(); TimeSpan lastRt = TimeSpan.MinValue; double prev = -1, prevTick = -1;
            EventHandler frame = delegate (object s, EventArgs e)
            {
                var rt = ((RenderingEventArgs)e).RenderingTime; if (rt == lastRt) return; lastRt = rt;
                double now = watch.Elapsed.TotalMilliseconds; if (prev >= 0) gaps.Add(now - prev); prev = now;
            };
            var tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(4) };
            tick.Tick += delegate { double now = watch.Elapsed.TotalMilliseconds; if (prevTick >= 0) stalls.Add(now - prevTick); prevTick = now; };
            CompositionTarget.Rendering += frame; tick.Start();
            start();
            return new Stats { Name = name, WallMs = ms }.With(gaps, stalls, () => { CompositionTarget.Rendering -= frame; tick.Stop(); });
        }
        static Stats With(this Stats s, List<double> gaps, List<double> stalls, Action stop)
        {
            s.Frames = 0; Pending.Add(new Tuple<Stats, List<double>, List<double>, Action>(s, gaps, stalls, stop)); return s;
        }
        static readonly List<Tuple<Stats, List<double>, List<double>, Action>> Pending = new List<Tuple<Stats, List<double>, List<double>, Action>>();
        static void Finish(Stats s)
        {
            var p = Pending.First(x => x.Item1 == s); Pending.Remove(p); p.Item4();
            var g = p.Item2.OrderBy(x => x).ToList(); s.Frames = g.Count + 1;
            s.GapP50 = g.Count == 0 ? 0 : g[g.Count / 2]; s.GapP95 = g.Count == 0 ? 0 : g[(int)((g.Count - 1) * .95)]; s.GapMax = g.Count == 0 ? 0 : g[g.Count - 1];
            s.Over20 = g.Count(x => x > 20); s.Over33 = g.Count(x => x > 33);
            s.UiStallMax = p.Item3.Count == 0 ? 0 : p.Item3.Max();
        }
        internal static readonly List<string> LongOps = new List<string>();
        static bool logOps; static double opsT0; static readonly Stopwatch opsClock = Stopwatch.StartNew();
        static readonly Dictionary<DispatcherOperation, double> opsStart = new Dictionary<DispatcherOperation, double>();
        static string OpName(DispatcherOperation op)
        {
            try
            {
                var f = typeof(DispatcherOperation).GetField("_method", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var d = f == null ? null : f.GetValue(op) as Delegate;
                return d == null ? "?" : (d.Method.DeclaringType == null ? "" : d.Method.DeclaringType.Name) + "." + d.Method.Name + "[" + (d.Target == null ? "static" : d.Target.GetType().Name) + "]";
            }
            catch { return "?"; }
        }
        static void HookOps()
        {
            Dispatcher.CurrentDispatcher.Hooks.OperationStarted += (s, e) => { if (logOps) opsStart[e.Operation] = opsClock.Elapsed.TotalMilliseconds; };
            Dispatcher.CurrentDispatcher.Hooks.OperationCompleted += (s, e) =>
            {
                double st; if (!logOps || !opsStart.TryGetValue(e.Operation, out st)) return; opsStart.Remove(e.Operation);
                double dur = opsClock.Elapsed.TotalMilliseconds - st;
                if (dur >= 6) LongOps.Add("t=" + (st - opsT0).ToString("0") + " " + dur.ToString("0.0") + "ms " + e.Operation.Priority + " " + OpName(e.Operation));
            };
        }
        static async Task<Stats> Run(string name, Action start, int ms)
        {
            logOps = name.EndsWith("-open") || name.EndsWith("-close") || name.EndsWith("-collapse") || name.EndsWith("-expand"); opsT0 = opsClock.Elapsed.TotalMilliseconds; if (logOps) LongOps.Add("--- " + name);
            var s = Measure(name, start, ms); await Task.Delay(ms); Finish(s); logOps = false; return s;
        }

        public static async void Start(string[] args)
        {
            string outPath = args.SkipWhile(a => a != "--perf231").Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? Path.Combine(Program.Base, "perf231.json");
            var rows = new List<Stats>(); string error = null; var prof = new List<string>();
            try
            {
                HookOps();
                await Task.Delay(900);
                // a launchpad with a realistic number of items
                var files = Directory.GetFiles(Environment.SystemDirectory, "*.exe").Take(48).ToList();
                Program.S.LaunchpadItems = files;
                for (int i = 0; i < 6; i++)
                {
                    LaunchpadWindow.Prof.Clear();
                    rows.Add(await Run("launchpad-open", delegate { Program.Launchpad(); }, 520));
                    prof.Add(string.Join(" | ", LaunchpadWindow.Prof));
                    var pad = Program.OpenWindows.OfType<LaunchpadWindow>().FirstOrDefault();
                    rows.Add(await Run("launchpad-close", delegate { if (pad != null) pad.Dismiss(); }, 380));
                    await Task.Delay(150);
                }
                // organizer: collapse / expand and the search slide
                var box = new BoxData { Title = "perf", Desktop = false, X = 60, Y = 120, Width = 420, Height = 300, Items = files.Take(30).ToList() };
                var org = new OrganizerWindow(box); Program.AddWindow(org); org.Show(); await Task.Delay(700);
                for (int i = 0; i < 6; i++)
                {
                    rows.Add(await Run("organizer-collapse", delegate { org.TestToggleCollapsed(); }, 420));
                    rows.Add(await Run("organizer-expand", delegate { org.TestToggleCollapsed(); }, 420));
                    rows.Add(await Run("organizer-search", delegate { org.TestToggleSearch(); }, 360));
                    await Task.Delay(120);
                }
                org.Close();
            }
            catch (Exception e) { error = e.ToString(); }
            var summary = rows.GroupBy(r => r.Name).Select(g => new
            {
                Name = g.Key, Runs = g.Count(), FramesAvg = Math.Round(g.Average(x => x.Frames), 1),
                GapP50 = Math.Round(g.Average(x => x.GapP50), 1), GapP95 = Math.Round(g.Average(x => x.GapP95), 1), GapMaxWorst = Math.Round(g.Max(x => x.GapMax), 1), GapMaxAvg = Math.Round(g.Average(x => x.GapMax), 1),
                Over20Avg = Math.Round(g.Average(x => x.Over20), 1), Over33Avg = Math.Round(g.Average(x => x.Over33), 1), UiStallMaxWorst = Math.Round(g.Max(x => x.UiStallMax), 1)
            }).ToList();
            File.WriteAllText(outPath, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(new { Version = SettingsWindow.Version, Render = System.Windows.Media.RenderOptions.ProcessRenderMode.ToString(), Error = error, Prof = prof, LongOps = LongOps, Summary = summary, Runs = rows }));
            Program.Quit();
        }
    }
}
