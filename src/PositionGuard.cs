using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace ClearDesk
{
    // 2.2.2: widget positions must only change when the user moves a widget. Windows (and WPF) also move windows by themselves while a display, its scaling
    // or the work area changes (login, resume from sleep, docking, the top taskbar being hidden/shown). The old code wrote *every* LocationChanged into
    // settings.json, so such a nudge became the saved position and the widget "was somewhere else after restart".
    internal static class PositionGuard
    {
        public const int SettleMs = 4000, ChangeMs = 2500, ReassertMs = 600;

        /// <summary>True when a location change must NOT be stored: no drag in progress, no mouse button held, and the system is still settling.</summary>
        public static bool IsSystemMove(bool dragging, bool mouseDown, bool settling) { return settling && !dragging && !mouseDown; }

        /// <summary>The point 30 px inside the top-left corner — the old "is this widget reachable" probe — must be inside a screen.</summary>
        public static bool Reachable(Point topLeft, IEnumerable<Rect> screens)
        {
            if (double.IsNaN(topLeft.X) || double.IsNaN(topLeft.Y) || double.IsInfinity(topLeft.X) || double.IsInfinity(topLeft.Y)) return false;
            var probe = new Point(topLeft.X + 30, topLeft.Y + 30);
            return screens.Any(r => r.Contains(probe));
        }

        /// <summary>Whole-screen rectangles in device-independent pixels. v may be null (before the window has a handle): the system DPI is used.</summary>
        public static IList<Rect> Screens(Visual v)
        {
            Matrix m = Matrix.Identity;
            var src = v == null ? null : PresentationSource.FromVisual(v);
            if (src != null && src.CompositionTarget != null) m = src.CompositionTarget.TransformFromDevice;
            else
            {
                try { using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) m = new Matrix(96.0 / g.DpiX, 0, 0, 96.0 / g.DpiY, 0, 0); } catch { }
            }
            var list = new List<Rect>();
            foreach (var s in System.Windows.Forms.Screen.AllScreens)
            {
                var a = m.Transform(new Point(s.Bounds.Left, s.Bounds.Top)); var b = m.Transform(new Point(s.Bounds.Right, s.Bounds.Bottom));
                list.Add(new Rect(a, b));
            }
            return list;
        }

        /// <summary>Where a widget with this saved position should be put again after the system moved it, or null when it is already there / the saved spot is unusable.</summary>
        public static Point? Reassert(Point saved, Point current, IEnumerable<Rect> screens)
        {
            if (!Reachable(saved, screens)) return null;
            if (Math.Abs(saved.X - current.X) < 0.5 && Math.Abs(saved.Y - current.Y) < 0.5) return null;
            return saved;
        }

        /// <summary>Work areas (not the whole screens) in device-independent pixels.</summary>
        public static IList<Rect> WorkAreas(Visual v)
        {
            Matrix m = Matrix.Identity;
            var src = v == null ? null : PresentationSource.FromVisual(v);
            if (src != null && src.CompositionTarget != null) m = src.CompositionTarget.TransformFromDevice;
            else
            {
                try { using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) m = new Matrix(96.0 / g.DpiX, 0, 0, 96.0 / g.DpiY, 0, 0); } catch { }
            }
            var list = new List<Rect>();
            foreach (var s in System.Windows.Forms.Screen.AllScreens)
            {
                var a = m.Transform(new Point(s.WorkingArea.Left, s.WorkingArea.Top)); var b = m.Transform(new Point(s.WorkingArea.Right, s.WorkingArea.Bottom));
                list.Add(new Rect(a, b));
            }
            return list;
        }

        /// <summary>
        /// 2.2.4: a saved place that is no longer on any screen (monitor gone, resolution lowered) is moved to the NEAREST spot that is fully visible — not thrown back to the
        /// top-left default. The saved value itself is untouched, so it returns when the monitor does.
        /// </summary>
        public static Point Clamp(Point topLeft, Size size, IList<Rect> areas)
        {
            if (areas == null || areas.Count == 0 || double.IsNaN(topLeft.X) || double.IsNaN(topLeft.Y)) return new Point(20, 20);
            var centre = new Point(topLeft.X + size.Width / 2, topLeft.Y + size.Height / 2);
            Rect best = areas[0]; double bestD = double.MaxValue;
            foreach (var a in areas)
            {
                double dx = Math.Max(Math.Max(a.Left - centre.X, 0), centre.X - a.Right), dy = Math.Max(Math.Max(a.Top - centre.Y, 0), centre.Y - a.Bottom), d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = a; }
            }
            double x = Math.Max(best.Left, Math.Min(topLeft.X, best.Right - size.Width)), y = Math.Max(best.Top, Math.Min(topLeft.Y, best.Bottom - size.Height));
            if (size.Width >= best.Width) x = best.Left;
            if (size.Height >= best.Height) y = best.Top;
            return new Point(x, y);
        }

        /// <summary>
        /// Device-pixel check after the settle time: where the window should be (saved pixels) when it is somewhere else although the user did not move it. Null when there is
        /// nothing to do: no pixel record, another DPI than at save time, already there, or the saved spot is not on a screen (monitor layout changed).
        /// </summary>
        public static Point? PixelFix(int savedX, int savedY, int savedDpi, int currentDpi, int nowX, int nowY, IEnumerable<Rect> screensPx)
        {
            if (savedX == int.MinValue || savedY == int.MinValue || savedDpi <= 0 || savedDpi != currentDpi) return null;
            if (Math.Abs(savedX - nowX) <= 2 && Math.Abs(savedY - nowY) <= 2) return null;
            var probe = new Point(savedX + 30, savedY + 30);
            if (!screensPx.Any(r => r.Contains(probe))) return null;
            return new Point(savedX, savedY);
        }
    }
}
