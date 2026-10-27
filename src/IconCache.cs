using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClearDesk
{
    internal static class IconCache
    {
        static readonly Dictionary<string, ImageSource> cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, List<WeakReference>> pending = new Dictionary<string, List<WeakReference>>(StringComparer.OrdinalIgnoreCase);
        static readonly BlockingCollection<string> queue = new BlockingCollection<string>();
        static IconCache()
        {
            var worker = new Thread(delegate ()
            {
                foreach (string path in queue.GetConsumingEnumerable())
                {
                    ImageSource source = null;
                    try
                    {
                        source = Native.Icon(path);
                    }
                    catch
                    {
                    }

                    var image = source;
                    if (Program.App == null)
                        continue;
                    Program.App.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (cache.Count < 512)
                            cache[path] = image;
                        List<WeakReference> waiting;
                        if (pending.TryGetValue(path, out waiting))
                        {
                            pending.Remove(path);
                            foreach (var reference in waiting)
                            {
                                var control = reference.Target as Image;
                                if (control != null)
                                    control.Source = image;
                            }
                        }
                    }));
                }
            })
            {
                IsBackground = true,
                Name = "ClearDesk shell icons"
            };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        /// <summary>Forgets a cached icon (the recycle bin changes between empty and full).</summary>
        public static void Forget(string path) { cache.Remove(path); }

        public static void Bind(Image control, string path)
        {
            RenderOptions.SetBitmapScalingMode(control, BitmapScalingMode.HighQuality);
            ImageSource image;
            if (cache.TryGetValue(path, out image))
            {
                control.Source = image;
                return;
            }

            List<WeakReference> waiting;
            if (pending.TryGetValue(path, out waiting))
            {
                waiting.Add(new WeakReference(control));
                return;
            }

            pending[path] = new List<WeakReference>
            {
                new WeakReference(control)
            };
            queue.Add(path);
        }
    }
}
