using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace ClearDesk
{
    internal enum EdgeSide { None, Left, Right, Top, Bottom }

    /// <summary>2.3.0 贴边隐藏: a widget dragged against an outer screen edge tucks itself behind that edge and leaves a thin strip;
    /// the pointer on the strip slides it out, leaving it slides it back after a delay. Only the window position changes, and those
    /// moves are never saved: the saved position stays the expanded one.</summary>
    internal sealed class EdgeHider
    {
        internal const double Snap = 14, Peek = 6, SlideMs = 170;
        internal const int LeaveDelayMs = 600, EnterDelayMs = 60;

        internal static EdgeSide SideOf(Rect win, Rect screen, double snap = Snap)
        {
            if (win.IsEmpty || screen.IsEmpty) return EdgeSide.None;
            double l = win.Left - screen.Left, r = screen.Right - win.Right, t = win.Top - screen.Top, b = screen.Bottom - win.Bottom;
            double best = snap + 1; var side = EdgeSide.None;
            if (l <= snap && l < best) { best = l; side = EdgeSide.Left; }
            if (r <= snap && r < best) { best = r; side = EdgeSide.Right; }
            if (t <= snap && t < best) { best = t; side = EdgeSide.Top; }
            if (b <= snap && b < best) { best = b; side = EdgeSide.Bottom; }
            return side;
        }
        /// <summary>Top-left of the tucked-away window: only <paramref name="peek"/> DIPs stay on screen.</summary>
        internal static Point Tucked(Rect win, Rect screen, EdgeSide side, double peek = Peek)
        {
            switch (side)
            {
                case EdgeSide.Left: return new Point(screen.Left - win.Width + peek, win.Top);
                case EdgeSide.Right: return new Point(screen.Right - peek, win.Top);
                case EdgeSide.Top: return new Point(win.Left, screen.Top - win.Height + peek);
                case EdgeSide.Bottom: return new Point(win.Left, screen.Bottom - peek);
                default: return win.TopLeft;
            }
        }
        /// <summary>Expanded position: the saved one, pulled flush against the edge it hides behind.</summary>
        internal static Point Shown(Rect win, Rect screen, EdgeSide side)
        {
            switch (side)
            {
                case EdgeSide.Left: return new Point(screen.Left, win.Top);
                case EdgeSide.Right: return new Point(screen.Right - win.Width, win.Top);
                case EdgeSide.Top: return new Point(win.Left, screen.Top);
                case EdgeSide.Bottom: return new Point(win.Left, screen.Bottom - win.Height);
                default: return win.TopLeft;
            }
        }
        /// <summary>Stay out? (hover, drag, menu, file drag, or the leave delay not over yet)</summary>
        internal static bool Hold(bool pointerOver, bool dragging, bool menuOpen, bool dropping, double msSinceLeave)
        {
            return pointerOver || dragging || menuOpen || dropping || msSinceLeave < LeaveDelayMs;
        }

        readonly GlassWindow w;
        readonly DispatcherTimer poll, anim;
        DateTime leftAt = DateTime.MinValue, overSince = DateTime.MinValue, animStart;
        Point animFrom, animTo;
        internal bool Tucked_ { get; private set; }
        internal EdgeSide Side { get; private set; }
        internal bool Dropping;
        internal Func<Point?> TestPointer;           // tests: a fake pointer (DIP); null = real cursor
        internal Func<Rect> TestScreen;              // tests: a fake screen
        internal int Slides;

        internal EdgeHider(GlassWindow window)
        {
            w = window;
            poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
            poll.Tick += delegate { try { Tick(); } catch (Exception e) { Program.Log(e); Stop(); } };
            anim = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(10) };
            anim.Tick += delegate { Step(); };
            w.DragEnter += delegate { Dropping = true; if (Tucked_) SlideOut(); };
            w.DragLeave += delegate { Dropping = false; leftAt = DateTime.UtcNow; };
            w.Drop += delegate { Dropping = false; leftAt = DateTime.UtcNow; };
            w.Closed += delegate { poll.Stop(); anim.Stop(); };
        }

        internal void Start() { leftAt = DateTime.UtcNow; poll.Start(); }
        internal void Stop()
        {
            poll.Stop(); anim.Stop();
            if (Tucked_ || !w.TestAtSaved) w.MoveQuietly(w.TestState.X, w.TestState.Y);
            Tucked_ = false; Side = EdgeSide.None;
        }

        /// <summary>The work area the widget is on (DIP). An edge that touches another monitor is not offered: the widget would slide onto it.</summary>
        Rect Screen()
        {
            if (TestScreen != null) return TestScreen();
            var src = PresentationSource.FromVisual(w);
            var m = src != null && src.CompositionTarget != null ? src.CompositionTarget.TransformFromDevice : System.Windows.Media.Matrix.Identity;
            Func<System.Drawing.Rectangle, Rect> dip = r => new Rect(m.Transform(new Point(r.Left, r.Top)), m.Transform(new Point(r.Right, r.Bottom)));
            var c = new Point(w.TestState.X + w.ActualWidth / 2, w.TestState.Y + w.ActualHeight / 2);
            var screens = System.Windows.Forms.Screen.AllScreens;
            foreach (var s in screens)
            {
                var b = dip(s.Bounds); if (!b.Contains(c)) continue;
                var work = dip(s.WorkingArea);
                // block edges shared with another monitor by pushing them out of reach
                double big = 1e5;
                foreach (var o in screens)
                {
                    if (o.Equals(s)) continue; var ob = dip(o.Bounds);
                    bool vOverlap = ob.Top < b.Bottom && ob.Bottom > b.Top, hOverlap = ob.Left < b.Right && ob.Right > b.Left;
                    if (vOverlap && Math.Abs(ob.Right - b.Left) < 2) work = new Rect(new Point(work.Left - big, work.Top), work.BottomRight);
                    if (vOverlap && Math.Abs(ob.Left - b.Right) < 2) work = new Rect(work.TopLeft, new Point(work.Right + big, work.Bottom));
                    if (hOverlap && Math.Abs(ob.Bottom - b.Top) < 2) work = new Rect(new Point(work.Left, work.Top - big), work.BottomRight);
                    if (hOverlap && Math.Abs(ob.Top - b.Bottom) < 2) work = new Rect(work.TopLeft, new Point(work.Right, work.Bottom + big));
                }
                return work;
            }
            return new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        }
        Rect Saved() { return new Rect(w.TestState.X, w.TestState.Y, Math.Max(1, w.ActualWidth), Math.Max(1, w.ActualHeight)); }
        Point? Pointer()
        {
            if (TestPointer != null) return TestPointer();
            try { return CursorInfo.Dip(w); } catch { return null; }
        }

        void Tick()
        {
            if (w.WasClosed || !w.IsVisible || w.ActualWidth < 1) return;
            bool dragging = w.IsDragging || (!Tucked_ && System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed && w.IsMouseOver && TestPointer == null);
            if (dragging) { Side = EdgeSide.None; return; }       // decide again where it is dropped
            var screen = Screen(); var saved = Saved();
            Side = SideOf(saved, screen);
            if (Side == EdgeSide.None) { if (Tucked_) SlideTo(saved.TopLeft, false); return; }
            var p = Pointer();
            var visible = new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight); visible.Inflate(2, 2);
            bool over = p.HasValue && visible.Contains(p.Value);
            var now = DateTime.UtcNow;
            bool menu = w.ContextMenu != null && w.ContextMenu.IsOpen;
            if (over || menu || Dropping) leftAt = now;
            if (Tucked_)
            {
                if (over) { if (overSince == DateTime.MinValue) overSince = now; } else overSince = DateTime.MinValue;
                if (Dropping || menu || (overSince != DateTime.MinValue && (now - overSince).TotalMilliseconds >= EnterDelayMs)) SlideOut();
                else if (!anim.IsEnabled) { var at = Tucked(saved, screen, Side); if (Math.Abs(w.Left - at.X) > .5 || Math.Abs(w.Top - at.Y) > .5) SlideTo(at, true); }     // moved by something else (display change, Win+D fix): tuck again
            }
            else if (!Hold(over, false, menu, Dropping, (now - leftAt).TotalMilliseconds) && !anim.IsEnabled)
            {
                SlideTo(Tucked(saved, screen, Side), true);
            }
        }

        void SlideOut() { overSince = DateTime.MinValue; leftAt = DateTime.UtcNow; SlideTo(Shown(Saved(), Screen(), Side == EdgeSide.None ? SideOf(Saved(), Screen()) : Side), false); }

        void SlideTo(Point to, bool tuck)
        {
            Tucked_ = tuck; Slides++;
            animFrom = new Point(w.Left, w.Top); animTo = to; animStart = DateTime.UtcNow;
            anim.Start(); Step();
        }
        void Step()
        {
            double t = Math.Min(1, (DateTime.UtcNow - animStart).TotalMilliseconds / SlideMs);
            double e = 1 - Math.Pow(1 - t, 3);
            w.MoveQuietly(animFrom.X + (animTo.X - animFrom.X) * e, animFrom.Y + (animTo.Y - animFrom.Y) * e);
            if (t >= 1) anim.Stop();
        }
        internal void Finish() { if (anim.IsEnabled) { animStart = DateTime.MinValue; Step(); } }
    }
}
