using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ClearDesk
{
    internal sealed class SnapGuide
    {
        public Point A, B;
        public bool Gap;
        public override string ToString() { return (Gap ? "gap " : "line ") + A + "-" + B; }
    }

    internal sealed class SnapResult
    {
        public double X, Y;
        public bool SnappedX, SnappedY;
        public readonly List<SnapGuide> Guides = new List<SnapGuide>();
    }

    // Pure snapping maths (DIPs). Moving rect anchors (left/centre/right, top/middle/bottom)
    // attract to other widgets' edges and centres, to work-area/screen edges and centres,
    // and to gaps that already exist between other widgets (equal spacing).
    internal static class MagnetSnap
    {
        public const double Threshold = 8;

        sealed class Candidate
        {
            public double Delta;
            public int Kind; // 0 edge/centre of widget, 1 area, 2 gap
            public double Line;
            public Rect Ref;
            public double GapFrom, GapTo; // gap: reference gap segment (for the guide)
            public double RefGapA, RefGapB, RefCross;
        }

        static double A(Rect r, int i, bool x) { return x ? (i == 0 ? r.Left : i == 1 ? r.Left + r.Width / 2 : r.Right) : (i == 0 ? r.Top : i == 1 ? r.Top + r.Height / 2 : r.Bottom); }
        static bool Overlap(double a1, double a2, double b1, double b2) { return Math.Min(a2, b2) - Math.Max(a1, b1) > 0; }

        public static SnapResult Snap(Rect moving, IList<Rect> others, IList<Rect> areas, double threshold)
        {
            var result = new SnapResult { X = moving.X, Y = moving.Y };
            others = others ?? new Rect[0];
            areas = areas ?? new Rect[0];
            var bx = Best(moving, others, areas, threshold, true);
            var by = Best(moving, others, areas, threshold, false);
            if (bx != null) { result.X += bx.Delta; result.SnappedX = true; }
            if (by != null) { result.Y += by.Delta; result.SnappedY = true; }
            var final = new Rect(result.X, result.Y, moving.Width, moving.Height);
            if (bx != null) result.Guides.AddRange(Guides(bx, final, true));
            if (by != null) result.Guides.AddRange(Guides(by, final, false));
            return result;
        }

        static Candidate Best(Rect m, IList<Rect> others, IList<Rect> areas, double t, bool x)
        {
            Candidate best = null;
            Action<Candidate> offer = delegate (Candidate c)
            {
                if (Math.Abs(c.Delta) <= t && (best == null || Math.Abs(c.Delta) < Math.Abs(best.Delta) - 0.01 || (Math.Abs(Math.Abs(c.Delta) - Math.Abs(best.Delta)) <= 0.01 && c.Kind < best.Kind)))
                    best = c;
            };
            foreach (var o in others)
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                    {
                        if (i == 1 ^ j == 1) continue; // centre-to-centre, edge-to-edge
                        double target = A(o, j, x);
                        offer(new Candidate { Delta = target - A(m, i, x), Kind = 0, Line = target, Ref = o });
                    }
            foreach (var a in areas)
                for (int i = 0; i < 3; i++)
                {
                    double target = A(a, i, x);
                    offer(new Candidate { Delta = target - A(m, i, x), Kind = 1, Line = target, Ref = a });
                }
            // equal spacing: gaps between pairs of widgets that share a row (x) / column (y)
            var gaps = new List<Candidate>();
            for (int p = 0; p < others.Count; p++)
                for (int q = 0; q < others.Count; q++)
                {
                    if (p == q) continue;
                    Rect P = others[p], Q = others[q];
                    bool cross = x ? Overlap(P.Top, P.Bottom, Q.Top, Q.Bottom) : Overlap(P.Left, P.Right, Q.Left, Q.Right);
                    double g = x ? Q.Left - P.Right : Q.Top - P.Bottom;
                    if (cross && g > 1) gaps.Add(new Candidate { RefGapA = x ? P.Right : P.Bottom, RefGapB = x ? Q.Left : Q.Top, RefCross = x ? (Math.Max(P.Top, Q.Top) + Math.Min(P.Bottom, Q.Bottom)) / 2 : (Math.Max(P.Left, Q.Left) + Math.Min(P.Right, Q.Right)) / 2, Delta = g });
                }
            foreach (var o in others)
            {
                bool cross = x ? Overlap(m.Top, m.Bottom, o.Top, o.Bottom) : Overlap(m.Left, m.Right, o.Left, o.Right);
                if (!cross) continue;
                foreach (var g in gaps)
                {
                    double gap = g.Delta;
                    // moving after o
                    double after = (x ? o.Right : o.Bottom) + gap;
                    offer(new Candidate { Delta = after - A(m, 0, x), Kind = 2, Ref = o, GapFrom = x ? o.Right : o.Bottom, GapTo = after, RefGapA = g.RefGapA, RefGapB = g.RefGapB, RefCross = g.RefCross });
                    // moving before o
                    double before = (x ? o.Left : o.Top) - gap;
                    offer(new Candidate { Delta = before - A(m, 2, x), Kind = 2, Ref = o, GapFrom = before, GapTo = x ? o.Left : o.Top, RefGapA = g.RefGapA, RefGapB = g.RefGapB, RefCross = g.RefCross });
                }
            }
            return best;
        }

        // Resizing from the bottom-right: snaps the right / bottom edge to other widgets' edges and
        // centres, screen / work-area edges, and to the same width / height as another widget.
        // X / Y of the result are the new Right / Bottom.
        public static SnapResult SnapResize(Rect m, IList<Rect> others, IList<Rect> areas, double threshold, bool snapX = true, bool snapY = true)
        {
            var res = new SnapResult { X = m.Right, Y = m.Bottom };
            foreach (bool x in new[] { true, false })
            {
                if (x ? !snapX : !snapY) continue;
                double edge = x ? m.Right : m.Bottom, start = x ? m.Left : m.Top;
                double best = threshold + .001, line = 0; Rect refRect = Rect.Empty; int kind = -1;
                Action<double, Rect, int> offer = (v, r, k) => { double d = Math.Abs(v - edge); if (v > start + 40 && d < best) { best = d; line = v; refRect = r; kind = k; } };
                foreach (var o in others)
                {
                    for (int i = 0; i < 3; i++) offer(A(o, i, x), o, 0);
                    offer(start + (x ? o.Width : o.Height), o, 3); // same size
                }
                foreach (var a in areas) { offer(x ? a.Right : a.Bottom, a, 1); offer(x ? a.Left + a.Width / 2 : a.Top + a.Height / 2, a, 1); }
                if (kind < 0) continue;
                if (x) { res.X = line; res.SnappedX = true; } else { res.Y = line; res.SnappedY = true; }
                var mm = x ? new Rect(m.Left, m.Top, Math.Max(0, line - m.Left), m.Height) : new Rect(m.Left, m.Top, m.Width, Math.Max(0, line - m.Top));
                if (kind == 3)
                {
                    // equal size: dashed dimension lines under/along both widgets
                    if (x)
                    {
                        res.Guides.Add(new SnapGuide { A = new Point(mm.Left, mm.Bottom + 6), B = new Point(mm.Right, mm.Bottom + 6), Gap = true });
                        res.Guides.Add(new SnapGuide { A = new Point(refRect.Left, refRect.Bottom + 6), B = new Point(refRect.Right, refRect.Bottom + 6), Gap = true });
                    }
                    else
                    {
                        res.Guides.Add(new SnapGuide { A = new Point(mm.Right + 6, mm.Top), B = new Point(mm.Right + 6, mm.Bottom), Gap = true });
                        res.Guides.Add(new SnapGuide { A = new Point(refRect.Right + 6, refRect.Top), B = new Point(refRect.Right + 6, refRect.Bottom), Gap = true });
                    }
                }
                else
                {
                    var span = kind == 1 ? mm : Rect.Union(mm, refRect);
                    res.Guides.Add(x ? new SnapGuide { A = new Point(line, span.Top), B = new Point(line, span.Bottom) } : new SnapGuide { A = new Point(span.Left, line), B = new Point(span.Right, line) });
                }
            }
            return res;
        }

        static IEnumerable<SnapGuide> Guides(Candidate c, Rect m, bool x)
        {
            if (c.Kind == 2)
            {
                double cross = x ? (Math.Max(m.Top, c.Ref.Top) + Math.Min(m.Bottom, c.Ref.Bottom)) / 2 : (Math.Max(m.Left, c.Ref.Left) + Math.Min(m.Right, c.Ref.Right)) / 2;
                yield return x ? new SnapGuide { A = new Point(c.GapFrom, cross), B = new Point(c.GapTo, cross), Gap = true } : new SnapGuide { A = new Point(cross, c.GapFrom), B = new Point(cross, c.GapTo), Gap = true };
                yield return x ? new SnapGuide { A = new Point(c.RefGapA, c.RefCross), B = new Point(c.RefGapB, c.RefCross), Gap = true } : new SnapGuide { A = new Point(c.RefCross, c.RefGapA), B = new Point(c.RefCross, c.RefGapB), Gap = true };
                yield break;
            }
            double lo = x ? Math.Min(m.Top, c.Ref.Top) : Math.Min(m.Left, c.Ref.Left);
            double hi = x ? Math.Max(m.Bottom, c.Ref.Bottom) : Math.Max(m.Right, c.Ref.Right);
            yield return x ? new SnapGuide { A = new Point(c.Line, lo), B = new Point(c.Line, hi) } : new SnapGuide { A = new Point(lo, c.Line), B = new Point(hi, c.Line) };
        }
    }

    // Thin, click-through guide lines shown only while dragging; closed afterwards so the
    // full-screen layered surface does not stay resident.
    internal static class GuideOverlay
    {
        static Window window;
        static Canvas canvas;
        static readonly Brush line = Freeze(new SolidColorBrush(Color.FromArgb(230, 77, 163, 255)));
        static Brush Freeze(Brush b) { b.Freeze(); return b; }

        public static void Show(IList<SnapGuide> guides)
        {
            if (guides == null || guides.Count == 0) { Hide(); return; }
            if (window == null)
            {
                canvas = new Canvas { IsHitTestVisible = false };
                window = new Window
                {
                    WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
                    ResizeMode = ResizeMode.NoResize, Focusable = false, IsHitTestVisible = false, Title = "ClearDesk · 对齐参考线",
                    Left = SystemParameters.VirtualScreenLeft, Top = SystemParameters.VirtualScreenTop, Width = SystemParameters.VirtualScreenWidth, Height = SystemParameters.VirtualScreenHeight,
                    Content = canvas
                };
                window.SourceInitialized += delegate
                {
                    var h = new WindowInteropHelper(window).Handle;
                    Native.SetLong(h, -20, new IntPtr(Native.GetLong(h, -20).ToInt64() | 0x20 | 0x80 | 0x08000000));
                };
                window.Show();
            }
            canvas.Children.Clear();
            double ox = window.Left, oy = window.Top;
            foreach (var g in guides)
            {
                var l = new Line { X1 = g.A.X - ox, Y1 = g.A.Y - oy, X2 = g.B.X - ox, Y2 = g.B.Y - oy, Stroke = line, StrokeThickness = 1, SnapsToDevicePixels = true };
                if (g.Gap) { l.StrokeDashArray = new DoubleCollection { 3, 2 }; l.StrokeThickness = 1.5; }
                canvas.Children.Add(l);
            }
        }

        public static int VisibleCount { get { return window == null ? 0 : canvas.Children.Count; } }

        public static void Hide()
        {
            if (window == null) return;
            var w = window; window = null; canvas = null;
            w.Close();
        }
    }

    internal static class CursorInfo
    {
        [StructLayout(LayoutKind.Sequential)] struct P { public int X, Y; }
        [DllImport("user32.dll")] static extern bool GetCursorPos(out P p);
        public static Point Dip(Visual v)
        {
            P p; GetCursorPos(out p);
            var src = PresentationSource.FromVisual(v);
            var pt = new Point(p.X, p.Y);
            return src != null && src.CompositionTarget != null ? src.CompositionTarget.TransformFromDevice.Transform(pt) : pt;
        }
        public static IList<Rect> Areas(Visual v)
        {
            var src = PresentationSource.FromVisual(v);
            var m = src != null && src.CompositionTarget != null ? src.CompositionTarget.TransformFromDevice : Matrix.Identity;
            var list = new List<Rect>();
            foreach (var s in System.Windows.Forms.Screen.AllScreens)
            {
                foreach (var r in new[] { s.WorkingArea, s.Bounds })
                {
                    var a = m.Transform(new Point(r.Left, r.Top)); var b = m.Transform(new Point(r.Right, r.Bottom));
                    var rect = new Rect(a, b);
                    if (!list.Contains(rect)) list.Add(rect);
                }
            }
            return list;
        }
    }
}
