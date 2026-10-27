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
    }
}
