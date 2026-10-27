using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClearDesk
{
    /// <summary>
    /// Icons for the Dock / Launchpad. Lookups run on a worker thread. 2.2.3: a failed lookup is never cached (2.2.2 and earlier stored the null forever,
    /// so an app whose icon could not be read once — still starting, locked exe — stayed blank until ClearDesk restarted); the control shows a letter
    /// tile meanwhile and the lookup is repeated after 2 / 5 / 15 / 45 s and on every later bind.
    /// </summary>
    internal static class IconCache
    {
        sealed class Job { public string Key; public Func<ImageSource> Resolve; public string Fallback; public int Attempts; public DateTime Next; public List<WeakReference> Controls = new List<WeakReference>(); }
        static readonly Dictionary<string, ImageSource> cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Job> pending = new Dictionary<string, Job>(StringComparer.OrdinalIgnoreCase);   // queued or waiting for a retry
        static readonly BlockingCollection<Job> queue = new BlockingCollection<Job>();
        static readonly int[] retrySeconds = { 2, 5, 15, 45 };
        static DispatcherTimer retryTimer;
        internal static int FailedLookups, Retries;                         // diagnostics / tests
        internal static Action<Action> Post = a => { if (Program.App != null) Program.App.Dispatcher.BeginInvoke(a); };
        internal static bool Synchronous;                                    // tests: run lookups inline

        static IconCache()
        {
            var worker = new Thread(delegate ()
            {
                foreach (var job in queue.GetConsumingEnumerable())
                {
                    ImageSource source = null;
                    try { source = job.Resolve(); } catch { }
                    var done = job; var image = source;
                    if (Post != null) Post(delegate { Complete(done, image); });
                }
            })
            {
                IsBackground = true,
                Name = "ClearDesk shell icons"
            };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        static void Complete(Job job, ImageSource image)
        {
            if (image != null)
            {
                if (cache.Count < 512) cache[job.Key] = image;
                pending.Remove(job.Key);
                foreach (var reference in job.Controls) { var control = reference.Target as Image; if (control != null) control.Source = image; }
                return;
            }
            // failure: show the letter tile, remember nothing, try again later
            FailedLookups++;
            job.Controls.RemoveAll(r => !r.IsAlive);
            var tile = job.Fallback == null ? null : AppIcons.Letter(job.Fallback);
            foreach (var reference in job.Controls) { var control = reference.Target as Image; if (control != null) control.Source = tile; }
            if (job.Attempts >= retrySeconds.Length || job.Controls.Count == 0) { pending.Remove(job.Key); return; }
            job.Next = DateTime.UtcNow.AddSeconds(retrySeconds[job.Attempts]); job.Attempts++;
            pending[job.Key] = job;
            StartTimer();
        }

        static void StartTimer()
        {
            if (retryTimer != null || Program.App == null) return;
            retryTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            retryTimer.Tick += delegate
            {
                var due = pending.Values.Where(j => j.Next != default(DateTime) && j.Next <= DateTime.UtcNow).ToList();
                foreach (var j in due) { j.Next = default(DateTime); Retries++; Enqueue(j); }
                if (!pending.Values.Any(j => j.Next != default(DateTime))) { retryTimer.Stop(); retryTimer = null; }
            };
            retryTimer.Start();
        }

        static void Enqueue(Job job)
        {
            if (Synchronous) { ImageSource s = null; try { s = job.Resolve(); } catch { } Complete(job, s); return; }
            queue.Add(job);
        }

        /// <summary>Forgets a cached icon (the recycle bin changes between empty and full).</summary>
        public static void Forget(string path) { cache.Remove(path); }
        /// <summary>Runs every waiting retry now (tests; the timer does this after 2 / 5 / 15 / 45 s).</summary>
        internal static void RetryNow() { foreach (var j in pending.Values.Where(x => x.Next != default(DateTime)).ToList()) { j.Next = default(DateTime); Retries++; Enqueue(j); } }
        internal static bool IsCached(string key) { return cache.ContainsKey(key); }
        internal static void Reset() { cache.Clear(); pending.Clear(); FailedLookups = 0; Retries = 0; }

        public static void Bind(Image control, string path) { BindWith(control, path, () => Native.Icon(path), null); }

        /// <summary>Shows the icon for <paramref name="key"/>; <paramref name="resolve"/> runs on the worker thread and returns null when it fails.</summary>
        public static void BindWith(Image control, string key, Func<ImageSource> resolve, string fallbackName)
        {
            RenderOptions.SetBitmapScalingMode(control, BitmapScalingMode.HighQuality);
            ImageSource image;
            if (cache.TryGetValue(key, out image) && image != null)
            {
                control.Source = image;
                return;
            }
            cache.Remove(key);
            Job job;
            if (pending.TryGetValue(key, out job))
            {
                job.Controls.Add(new WeakReference(control));
                if (fallbackName != null) control.Source = AppIcons.Letter(fallbackName);
                if (job.Next != default(DateTime) && job.Next - DateTime.UtcNow > TimeSpan.FromSeconds(3)) { job.Next = DateTime.UtcNow.AddSeconds(1); StartTimer(); }
                return;
            }
            job = new Job { Key = key, Resolve = resolve, Fallback = fallbackName };
            job.Controls.Add(new WeakReference(control));
            pending[key] = job;
            Enqueue(job);
        }
    }
}
