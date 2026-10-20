using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Web.Script.Serialization;

namespace ClearDesk
{
    internal static class PopupBenchmark
    {
        public static async void Start()
        {
            var trials = new List<object>();
            try
            {
                await Task.Delay(700);
                for (int i = 0; i < 8; i++)
                {
                    var watch = Stopwatch.StartNew();
                    double first = -1, previous = -1;
                    var gaps = new List<double>();
                    bool settled = false;
                    LaunchpadWindow pad = null;
                    EventHandler frame = delegate
                    {
                        var visible = Program.App.Windows.OfType<LaunchpadWindow>().FirstOrDefault(w => w.IsLoaded && w.IsVisible);
                        if (visible == null)
                            return;
                        double now = watch.Elapsed.TotalMilliseconds;
                        if (first < 0)
                        {
                            first = now;
                            settled = visible.TestGridSettled;
                        }

                        if (previous >= 0)
                            gaps.Add(now - previous);
                        previous = now;
                    };
                    CompositionTarget.Rendering += frame;
                    Program.Launchpad();
                    pad = Program.App.Windows.OfType<LaunchpadWindow>().First();
                    double opened = watch.Elapsed.TotalMilliseconds;
                    await Task.Delay(450);
                    CompositionTarget.Rendering -= frame;
                    trials.Add(new { Trial = i, ShowCallMs = opened, FirstFrameMs = first, InitialGridSettled = settled, FrameGapMaxMs = gaps.Count == 0 ? 0 : gaps.Max(), FrameGapP95Ms = gaps.Count == 0 ? 0 : gaps.OrderBy(x => x).ElementAt((int)((gaps.Count - 1) * .95)) });
                    pad.Close();
                    await Task.Delay(120);
                }

                File.WriteAllText(Path.Combine(Program.Base, "popup-benchmark.json"), new JavaScriptSerializer().Serialize(new { Trials = trials, Note = "Synthetic WPF rendering callbacks; not monitor FPS or user input latency. Preview mode, outside-click hook excluded." }));
            }
            catch (Exception e)
            {
                File.WriteAllText(Path.Combine(Program.Base, "popup-benchmark.json"), e.ToString());
                Environment.ExitCode = 1;
            }
            finally
            {
                Program.Quit();
            }
        }
    }
}
