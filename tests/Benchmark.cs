using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Web.Script.Serialization;

namespace ClearDesk
{
    internal static class Benchmark
    {
        static List<double> delays = new List<double>();
        static List<object> phases = new List<object>();
        static Stopwatch watch = new Stopwatch();
        static DispatcherTimer timer;
        static double lastMs;
        static TimeSpan cpu;
        static int phase = -1;
        static int frameCount;
        static TimeSpan lastRender;
        static LaunchpadWindow launchpad;
        static readonly string[] names =
        {
            "warmup",
            "idle",
            "dock-hover",
            "launchpad"
        };
        public static void Start()
        {
            watch.Start();
            cpu = Process.GetCurrentProcess().TotalProcessorTime;
            timer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            timer.Tick += Tick;
            timer.Start();
        }

        static void Frame(object sender, EventArgs e)
        {
            var t = ((RenderingEventArgs)e).RenderingTime;
            if (t != lastRender)
            {
                lastRender = t;
                frameCount++;
            }
        }

        static void Tick(object sender, EventArgs e)
        {
            double ms = watch.Elapsed.TotalMilliseconds;
            int next = ms < 5000 ? 0 : ms < 15000 ? 1 : ms < 25000 ? 2 : ms < 35000 ? 3 : 4;
            if (next != phase)
            {
                if (phase >= 0)
                {
                    var proc = Process.GetCurrentProcess();
                    double elapsed = phase == 0 ? ms : 10000;
                    var ordered = delays.OrderBy(x => x).ToArray();
                    phases.Add(new { Phase = names[phase], CpuPercentOneCore = (proc.TotalProcessorTime - cpu).TotalMilliseconds / elapsed * 100, WorkingSetMB = proc.WorkingSet64 / 1048576.0, UiTickP95Milliseconds = ordered.Length == 0 ? 0 : ordered[(int)((ordered.Length - 1) * .95)], UiTickMaxMilliseconds = ordered.Length == 0 ? 0 : ordered.Last(), CompositionFrames = frameCount });
                }

                phase = next;
                if (phase == 2)
                    CompositionTarget.Rendering += Frame;
                delays.Clear();
                frameCount = 0;
                cpu = Process.GetCurrentProcess().TotalProcessorTime;
                if (phase == 3)
                {
                    Program.Launchpad();
                    launchpad = Program.App.Windows.OfType<LaunchpadWindow>().FirstOrDefault();
                }

                if (phase == 4)
                {
                    timer.Stop();
                    CompositionTarget.Rendering -= Frame;
                    File.WriteAllText(Path.Combine(Program.Base, "benchmark.json"), new JavaScriptSerializer().Serialize(new { RenderTier = RenderCapability.Tier >> 16, LogicalProcessors = Environment.ProcessorCount, Phases = phases, Note = "App process only; synthetic Dock hover; UI timer gaps are not end-to-end input latency or display FPS." }));
                    Program.Quit();
                    return;
                }
            }

            if (lastMs > 0)
                delays.Add(ms - lastMs);
            lastMs = ms;
            if (phase == 2)
            {
                foreach (var dock in Program.App.Windows.OfType<DockWindow>())
                    dock.TestHover(.5 + .38 * Math.Sin(ms / 900));
            }
        }
    }
}
