using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace ClearDesk
{
    /// <summary>Where a widget created from a menu should appear: at the mouse position of the right-click that opened the menu (2.2.1).</summary>
    public enum PlaceMode { Default, DesktopClick, LastRightClick }

    /// <summary>Pure placement maths (DIPs) plus the record of the last right-click (device pixels).</summary>
    internal static class WidgetPlacement
    {
        internal const int RecentMs = 30000;
        static readonly object gate = new object();
        static int lastX, lastY; static long lastAt = long.MinValue;
        internal static long NowMs() { return (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency); }

        // ---- last right-click (written from the mouse hook thread: only two assignments under a lock) ----
        internal static void Record(int x, int y, long atMs) { lock (gate) { lastX = x; lastY = y; lastAt = atMs; } }
        internal static void Forget() { lock (gate) { lastAt = long.MinValue; } }
        internal static bool TryRecent(long nowMs, out int x, out int y)
        {
            lock (gate) { x = lastX; y = lastY; return lastAt != long.MinValue && nowMs - lastAt >= 0 && nowMs - lastAt <= RecentMs; }
        }

        internal static Point ToDip(double px, double py, double scaleX, double scaleY) { return new Point(px / (scaleX <= 0 ? 1 : scaleX), py / (scaleY <= 0 ? 1 : scaleY)); }

        /// <summary>The work area the point is in; otherwise the nearest one.</summary>
        internal static Rect AreaFor(Point p, IList<Rect> areas)
        {
            if (areas == null || areas.Count == 0) return new Rect(0, 0, 1920, 1080);
            foreach (var a in areas) if (a.Contains(p)) return a;
            Func<Rect, double> dist = a => { double dx = Math.Max(Math.Max(a.Left - p.X, 0), p.X - a.Right), dy = Math.Max(Math.Max(a.Top - p.Y, 0), p.Y - a.Bottom); return dx * dx + dy * dy; };
            return areas.OrderBy(dist).First();
        }

        /// <summary>Top-left (or centred) at the cursor, kept fully inside the work area (a widget bigger than the area is aligned to its top-left).</summary>
        internal static Point Place(Point cursor, Size size, IList<Rect> areas, bool centered)
        {
            var area = AreaFor(cursor, areas);
            double x = centered ? cursor.X - size.Width / 2 : cursor.X, y = centered ? cursor.Y - size.Height / 2 : cursor.Y;
            x = Math.Max(area.Left, Math.Min(x, area.Right - size.Width));
            y = Math.Max(area.Top, Math.Min(y, area.Bottom - size.Height));
            if (size.Width >= area.Width) x = area.Left;
            if (size.Height >= area.Height) y = area.Top;
            return new Point(x, y);
        }

        /// <summary>Magnet snapping for the new widget (other widgets' edges and the screen edges), then clamped again.</summary>
        internal static Point Snap(Point topLeft, Size size, IList<Rect> others, IList<Rect> snapAreas, IList<Rect> workAreas)
        {
            var r = MagnetSnap.Snap(new Rect(topLeft, size), others, snapAreas, MagnetSnap.Threshold);
            return Place(new Point(r.X, r.Y), size, workAreas, false);
        }

        internal static Point Compute(Point cursor, Size size, IList<Rect> workAreas, IList<Rect> others, IList<Rect> snapAreas, bool snap, bool centered)
        {
            var p = Place(cursor, size, workAreas, centered);
            return snap ? Snap(p, size, others, snapAreas, workAreas) : p;
        }
    }

    /// <summary>Remembers where the mouse was when the right button went down (low-level hook on its own thread; the callback does nothing else).</summary>
    internal sealed class RightClickTracker : IDisposable
    {
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", SetLastError = true)] static extern bool PostThreadMessage(uint thread, uint message, IntPtr w, IntPtr l);
        Thread thread; System.Windows.Forms.ApplicationContext context; Native.MouseProc callback; IntPtr hook; uint threadId; volatile bool stopped;
        readonly ManualResetEvent ready = new ManualResetEvent(false);

        internal RightClickTracker()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "ClearDesk right-click position" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            ready.WaitOne(2000);
        }
        void Run()
        {
            threadId = GetCurrentThreadId(); context = new System.Windows.Forms.ApplicationContext(); callback = OnMouse;
            hook = Native.SetWindowsHookEx(14, callback, Native.GetModuleHandle(null), 0);
            ready.Set();
            if (hook == IntPtr.Zero) return;
            try { System.Windows.Forms.Application.Run(context); }
            finally { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; context.Dispose(); }
        }
        IntPtr OnMouse(int code, IntPtr w, IntPtr l)
        {
            if (!stopped && code >= 0 && w.ToInt64() == 0x204)   // WM_RBUTTONDOWN
            {
                var m = (Native.MOUSE)Marshal.PtrToStructure(l, typeof(Native.MOUSE));
                WidgetPlacement.Record(m.pt.X, m.pt.Y, WidgetPlacement.NowMs());
            }
            return Native.CallNextHookEx(hook, code, w, l);
        }
        public void Dispose()
        {
            stopped = true;
            if (threadId != 0) PostThreadMessage(threadId, 0x12, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
