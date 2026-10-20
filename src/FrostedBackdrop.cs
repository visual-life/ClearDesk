using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClearDesk
{
    // Read wallpaper files, never capture the screen. Only this background layer is
    // blurred; text and icons are siblings and remain sharp. No extra native window.
    internal sealed class FrostedBackdrop : IDisposable
    {
        readonly Grid surface, clip = new Grid
        {
            ClipToBounds = true,
            IsHitTestVisible = false
        };
        readonly Border paper = new Border
        {
            Margin = new Thickness(-30),
            Effect = new BlurEffect
            {
                Radius = 18,
                KernelType = KernelType.Gaussian,
                RenderingBias = RenderingBias.Quality
            }
        };
        readonly Window owner;
        DrawingBrush brush;
        Rect last = Rect.Empty;
        int version = -1;
        bool disposed;
        public static int ActiveCount;
        public bool Enabled
        {
            get
            {
                return brush != null;
            }
        }

        internal Rect SampleBounds
        {
            get
            {
                return last;
            }
        }

        public FrostedBackdrop(Grid element)
        {
            surface = element;
            owner = Window.GetWindow(element);
            clip.Children.Add(paper);
            surface.Children.Insert(0, clip);
            ActiveCount++;
            WallpaperScene.Acquire();
            WallpaperScene.Changed += Changed;
            surface.LayoutUpdated += Layout;
            if (owner != null)
            {
                owner.LocationChanged += Layout;
                owner.Closed += Closed;
            }

            Sync();
        }

        void Changed()
        {
            version = -1;
            Sync();
        }

        void Layout(object sender, EventArgs e)
        {
            Sync();
        }

        void Closed(object sender, EventArgs e)
        {
            Dispose();
        }

        public void Sync()
        {
            if (disposed || owner == null || !owner.IsVisible || !surface.IsVisible || surface.ActualWidth <= 0 || surface.ActualHeight <= 0)
                return;
            var source = PresentationSource.FromVisual(surface);
            if (source == null)
                return;
            Point a = surface.PointToScreen(new Point(-30, -30)), b = surface.PointToScreen(new Point(surface.ActualWidth + 30, surface.ActualHeight + 30));
            var rect = new Rect(a, b);
            if (rect == last && version == WallpaperScene.Version)
                return;
            last = rect;
            if (brush == null || version != WallpaperScene.Version)
            {
                brush = new DrawingBrush(WallpaperScene.Scene)
                {
                    ViewboxUnits = BrushMappingMode.Absolute,
                    ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                    Viewport = new Rect(0, 0, 1, 1),
                    Stretch = Stretch.Fill
                };
                paper.Background = brush;
                version = WallpaperScene.Version;
            }

            brush.Viewbox = rect;
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            surface.LayoutUpdated -= Layout;
            WallpaperScene.Changed -= Changed;
            if (owner != null)
            {
                owner.LocationChanged -= Layout;
                owner.Closed -= Closed;
            }

            surface.Children.Remove(clip);
            ActiveCount--;
            WallpaperScene.Release();
        }
    }

    internal static class WallpaperScene
    {
        [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDesktopWallpaper
        {
            void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitor, [MarshalAs(UnmanagedType.LPWStr)] string path);
            [return: MarshalAs(UnmanagedType.LPWStr)]
            string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitor);
            [return: MarshalAs(UnmanagedType.LPWStr)]
            string GetMonitorDevicePathAt(uint index);
            uint GetMonitorDevicePathCount();
            Native.RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitor);
            void SetBackgroundColor(uint color);
            uint GetBackgroundColor();
            void SetPosition(int position);
            int GetPosition();
        }

        sealed class Monitor
        {
            public Rect Bounds;
            public string Path;
        }

        static readonly Dictionary<string, BitmapSource> images = new Dictionary<string, BitmapSource>();
        static DispatcherTimer timer;
        static int users;
        static string signature;
        public static DrawingGroup Scene { get; private set; }
        public static int Version { get; private set; }

        public static event Action Changed;
        public static void Acquire()
        {
            if (users++ == 0)
            {
                Refresh();
                timer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromSeconds(3)
                };
                timer.Tick += delegate
                {
                    Refresh();
                };
                timer.Start();
            }
        }

        public static void Release()
        {
            if (--users == 0 && timer != null)
            {
                timer.Stop();
                timer = null;
                images.Clear();
            }
        }

        static void Refresh()
        {
            object com = null;
            try
            {
                var monitors = new List<Monitor>();
                int position = 4;
                uint color = 0;
                try
                {
                    com = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD")));
                    var wallpaper = (IDesktopWallpaper)com;
                    position = wallpaper.GetPosition();
                    color = wallpaper.GetBackgroundColor();
                    for (uint i = 0; i < wallpaper.GetMonitorDevicePathCount(); i++)
                    {
                        string id = wallpaper.GetMonitorDevicePathAt(i);
                        var r = wallpaper.GetMonitorRECT(id);
                        if (r.R > r.L && r.B > r.T)
                            monitors.Add(new Monitor { Bounds = new Rect(r.L, r.T, r.R - r.L, r.B - r.T), Path = wallpaper.GetWallpaper(id) });
                    }
                }
                catch
                {
                    monitors.Clear();
                    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Control Panel\\Desktop"))
                    {
                        string path = key == null ? null : key.GetValue("WallPaper") as string;
                        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                        {
                            var r = screen.Bounds;
                            monitors.Add(new Monitor { Bounds = new Rect(r.X, r.Y, r.Width, r.Height), Path = path });
                        }
                    }
                }

                string next = position + "|" + color;
                Rect union = Rect.Empty;
                foreach (var monitor in monitors)
                {
                    union.Union(monitor.Bounds);
                    next += "|" + monitor.Bounds + "|" + monitor.Path + "|" + (File.Exists(monitor.Path) ? File.GetLastWriteTimeUtc(monitor.Path).Ticks : 0);
                }

                if (next == signature && Scene != null)
                    return;
                signature = next;
                images.Clear();
                var group = new DrawingGroup();
                using (var dc = group.Open())
                {
                    if (union.IsEmpty)
                        union = new Rect(0, 0, 1920, 1080);
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)(color & 255), (byte)((color >> 8) & 255), (byte)((color >> 16) & 255))), null, union);
                    foreach (var monitor in monitors)
                    {
                        var image = Read(monitor.Path);
                        if (image == null)
                            continue;
                        var area = position == 5 ? union : monitor.Bounds;
                        dc.PushClip(new RectangleGeometry(monitor.Bounds));
                        if (position == 1)
                        {
                            var tile = new ImageBrush(image)
                            {
                                TileMode = TileMode.Tile,
                                ViewportUnits = BrushMappingMode.Absolute,
                                Viewport = new Rect(union.X, union.Y, image.PixelWidth, image.PixelHeight),
                                Stretch = Stretch.Fill
                            };
                            dc.DrawRectangle(tile, null, monitor.Bounds);
                        }
                        else
                        {
                            double w = area.Width, h = area.Height;
                            if (position != 2)
                            {
                                double scale = position == 0 ? 1 : position == 3 ? Math.Min(w / image.PixelWidth, h / image.PixelHeight) : Math.Max(w / image.PixelWidth, h / image.PixelHeight);
                                w = image.PixelWidth * scale;
                                h = image.PixelHeight * scale;
                            }

                            dc.DrawImage(image, new Rect(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
                        }

                        dc.Pop();
                    }
                }

                group.Freeze();
                Scene = group;
                Version++;
                if (Changed != null)
                    Changed();
            }
            catch (Exception e)
            {
                Program.Log(e);
                if (Scene == null)
                {
                    Scene = new DrawingGroup();
                    using (var dc = Scene.Open())
                        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(42, 42, 44)), null, new Rect(-16384, -16384, 32768, 32768));
                    Scene.Freeze();
                    Version++;
                }
            }
            finally
            {
                if (com != null && Marshal.IsComObject(com))
                    Marshal.ReleaseComObject(com);
            }
        }

        static BitmapSource Read(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;
            BitmapSource cached;
            if (images.TryGetValue(path, out cached))
                return cached;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                images[path] = image;
                return image;
            }
            catch (Exception e)
            {
                Program.Log(e);
                return null;
            }
        }
    }
}
