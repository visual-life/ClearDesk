using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace ClearDesk
{
    // Windows refuses to auto-hide a taskbar docked at the top edge (Win11 "top" option).
    // ClearDesk emulates it: hide the tray windows, hand the reserved strip back to the
    // work area, and show the taskbar while the pointer dwells on the top edge or while
    // a shell surface (Start, search, tray flyout, jump list) is in the foreground.
    internal static class TopTaskbar
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
        [DllImport("user32.dll")] static extern IntPtr FindWindow(string c, string t);
        [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string t);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO i);
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] static extern bool SpiRect(uint action, uint param, ref RECT r, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder b, int n);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO info);
        [StructLayout(LayoutKind.Sequential)] struct GUITHREADINFO { public int Size; public uint Flags; public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret; public RECT CaretRect; }

        const uint SPI_SETWORKAREA = 0x2F, SPI_GETWORKAREA = 0x30, SPIF_SENDCHANGE = 2;
        const int SW_HIDE = 0, SW_SHOWNA = 8;
        const int EdgePx = 2, DwellMs = 150, LeaveMs = 450;

        static DispatcherTimer timer;
        static bool hidden, revealed;
        static string traySig = "";
        static readonly Dictionary<IntPtr, Edge> edges = new Dictionary<IntPtr, Edge>();
        static Edge EdgeFor(IntPtr t) { Edge e; return edges.TryGetValue(t, out e) ? e : Edge.Top; }
        // Slide animation: offset 0 = resting at the top edge, -height = fully above the screen.
        const double SlideMs = 180;
        static DispatcherTimer anim;
        static readonly Stopwatch animClock = new Stopwatch();
        static List<IntPtr> animTrays = new List<IntPtr>();
        static double animFrom, animTo, animOffset = -1; // offset as a fraction of tray height (-1 hidden .. 0 shown)
        static bool animating;
        static DateTime edgeSince = DateTime.MinValue, outsideSince = DateTime.MinValue, lastWork = DateTime.MinValue;
        static readonly Dictionary<uint, string> procNames = new Dictionary<uint, string>();
        public static bool Active { get { return hidden; } }
        /// <summary>2.3.2: ClearDesk is hiding a tray on this edge right now (the Dock then rests on the physical screen bottom).</summary>
        internal static bool HidesEdge(Edge e) { return hidden && edges.Values.Contains(e); }
        public static bool Revealed { get { return revealed; } }

        static IntPtr Dpi() { try { return SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch { return IntPtr.Zero; } }
        static void Undpi(IntPtr old) { if (old != IntPtr.Zero) try { SetThreadDpiAwarenessContext(old); } catch { } }

        public static List<IntPtr> Trays()
        {
            var list = new List<IntPtr>();
            var main = FindWindow("Shell_TrayWnd", null);
            if (main != IntPtr.Zero) list.Add(main);
            IntPtr h = IntPtr.Zero;
            while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) list.Add(h);
            return list;
        }

        static bool Monitor(IntPtr w, out RECT mon)
        {
            var mi = new MONITORINFO { Size = Marshal.SizeOf(typeof(MONITORINFO)) };
            mon = new RECT();
            if (!GetMonitorInfo(MonitorFromWindow(w, 2), ref mi)) return false;
            mon = mi.Monitor; return true;
        }

        public enum Edge { None, Left, Top, Right, Bottom }

        /// <summary>2.3.1: which monitor edge a tray hugs. A tray is a thin strip (less than a third of the monitor) that touches one edge. Pure geometry, any of the four sides.</summary>
        internal static Edge EdgeOf(RECT r, RECT mon)
        {
            int w = r.R - r.L, h = r.B - r.T, mw = mon.R - mon.L, mh = mon.B - mon.T;
            if (w <= 0 || h <= 0 || mw <= 0 || mh <= 0) return Edge.None;
            // A taskbar that Windows itself auto-hid rests almost completely off the screen (a couple of pixels stay): that is Windows' business, not ours.
            if (w > h * 3 && h < mh / 3)
            {
                int vis = Math.Min(r.B, mon.B) - Math.Max(r.T, mon.T);
                if (vis * 2 < h) return Edge.None;
                if (r.T <= mon.T + 1 && r.B >= mon.T - 1) return Edge.Top;
                if (r.B >= mon.B - 1 && r.T <= mon.B + 1) return Edge.Bottom;
            }
            else if (h > w * 3 && w < mw / 3)
            {
                int vis = Math.Min(r.R, mon.R) - Math.Max(r.L, mon.L);
                if (vis * 2 < w) return Edge.None;
                if (r.L <= mon.L + 1 && r.R >= mon.L - 1) return Edge.Left;
                if (r.R >= mon.R - 1 && r.L <= mon.R + 1) return Edge.Right;
            }
            return Edge.None;
        }

        [StructLayout(LayoutKind.Sequential)] struct APPBARDATA { public uint cbSize; public IntPtr hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public IntPtr lParam; }
        [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
        static DateTime autoHideAt = DateTime.MinValue; static bool autoHide;
        /// <summary>Windows' own "taskbar auto-hide" switch (ABM_GETSTATE / ABS_AUTOHIDE), asked at most twice a second.</summary>
        internal static bool WindowsAutoHide()
        {
            if ((DateTime.UtcNow - autoHideAt).TotalMilliseconds < 500) return autoHide;
            try { var d = new APPBARDATA(); d.cbSize = (uint)Marshal.SizeOf(typeof(APPBARDATA)); autoHide = (((ulong)SHAppBarMessage(4, ref d)) & 1) != 0; } catch { autoHide = false; }
            autoHideAt = DateTime.UtcNow; return autoHide;
        }

        public static Edge EdgeOf(IntPtr tray)
        {
            RECT r, mon;
            if (!GetWindowRect(tray, out r) || !Monitor(tray, out mon)) return Edge.None;
            var e = EdgeOf(r, mon);
            // 2.3.1: bottom / left / right with Windows' own auto-hide switched on: Windows hides and shows it itself - two programs moving one window would fight.
            if (e != Edge.None && e != Edge.Top && WindowsAutoHide()) return Edge.None;
            return e;
        }

        // A tray counts as "top" when it hugs the monitor's top edge and is a horizontal strip.
        public static bool AtTop(IntPtr tray) { return EdgeOf(tray) == Edge.Top; }

        /// <summary>The visible part of a tray window (window coordinates) when <paramref name="fraction"/> of it (0..1) is shown, anchored to the screen edge it hugs.</summary>
        internal static int[] ClipRect(Edge e, int w, int h, double fraction)
        {
            fraction = Math.Max(0, Math.Min(1, fraction));
            int vw = (int)Math.Round(w * fraction), vh = (int)Math.Round(h * fraction);
            switch (e)
            {
                case Edge.Bottom: return new[] { 0, h - vh, w, h };
                case Edge.Left: return new[] { 0, 0, vw, h };
                case Edge.Right: return new[] { w - vw, 0, w, h };
                default: return new[] { 0, 0, w, vh };
            }
        }

        /// <summary>The work area with the strip of this edge handed back to applications (the tray reserved it).</summary>
        internal static RECT Claim(Edge e, RECT mon, RECT work)
        {
            switch (e)
            {
                case Edge.Top: work.T = mon.T; break;
                case Edge.Bottom: work.B = mon.B; break;
                case Edge.Left: work.L = mon.L; break;
                case Edge.Right: work.R = mon.R; break;
            }
            return work;
        }

        /// <summary>The work area with the tray's strip taken out again.</summary>
        internal static RECT Reserve(Edge e, RECT tray, RECT work)
        {
            switch (e)
            {
                case Edge.Top: if (work.T < tray.B) work.T = tray.B; break;
                case Edge.Bottom: if (work.B > tray.T) work.B = tray.T; break;
                case Edge.Left: if (work.L < tray.R) work.L = tray.R; break;
                case Edge.Right: if (work.R > tray.L) work.R = tray.L; break;
            }
            return work;
        }

        /// <summary>A maximized window that fills the old work area follows the change of the work area on this edge; null = not affected.</summary>
        internal static RECT? Shifted(Edge e, RECT r, RECT oldWork, RECT newWork)
        {
            switch (e)
            {
                case Edge.Top: if (newWork.T == oldWork.T || r.T < oldWork.T - 24 || r.T > oldWork.T + 2) return null; r.T += newWork.T - oldWork.T; return r;
                case Edge.Bottom: if (newWork.B == oldWork.B || r.B > oldWork.B + 24 || r.B < oldWork.B - 2) return null; r.B += newWork.B - oldWork.B; return r;
                case Edge.Left: if (newWork.L == oldWork.L || r.L < oldWork.L - 24 || r.L > oldWork.L + 2) return null; r.L += newWork.L - oldWork.L; return r;
                case Edge.Right: if (newWork.R == oldWork.R || r.R > oldWork.R + 24 || r.R < oldWork.R - 2) return null; r.R += newWork.R - oldWork.R; return r;
            }
            return null;
        }

        /// <summary>Which trays ClearDesk takes over: the top ones for 顶部任务栏自动隐藏 (Windows cannot auto-hide a top taskbar), and every edge for 完全隐藏任务栏.</summary>
        internal static bool Managed(Edge e, bool full) { return e == Edge.Top || (full && e != Edge.None); }

        public static void Sync()
        {
            bool want = Program.S != null && (Program.S.TopTaskbarAutoHide || Program.S.TopTaskbarFullHide) && !Program.Preview;
            if (want && timer == null)
            {
                timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
                timer.Tick += delegate { try { Tick(); } catch (Exception e) { Program.Log(e); } };
                timer.Start();
                Tick();
            }
            else if (!want && timer != null)
                Stop();
        }

        public static void Stop()
        {
            if (timer != null) { timer.Stop(); timer = null; }
            StopAnim();
            if (hidden || revealed) Restore();
            hidden = revealed = false;
            if (Program.Services != null) Program.Services.MarkTopTaskbar(false);
        }

        static void Tick()
        {
            var old = Dpi();
            try
            {
                bool full = Program.S != null && Program.S.TopTaskbarFullHide;
                var trays = Trays();
                var top = trays.FindAll(t => Managed(EdgeOf(t), full));
                if (top.Count == 0)
                {
                    // Taskbar moved off the edge it was hidden on (or explorer is restarting): give it back.
                    if (hidden) { Restore(); hidden = revealed = false; edges.Clear(); Program.Services.MarkTopTaskbar(false); }
                    return;
                }
                // 2.3.1: a tray that changed edge (or a new one) is handled from scratch
                string sig = string.Join(",", top.Select(t => t.ToInt64() + ":" + EdgeOf(t)).ToArray());
                if (hidden && sig != traySig) { StopAnim(); Restore(); hidden = revealed = false; }
                traySig = sig;
                if (!hidden)
                {
                    Program.Services.MarkTopTaskbar(true); hidden = true; revealed = false; animOffset = -1;
                    edges.Clear(); foreach (var t in top) edges[t] = EdgeOf(t);
                    foreach (var t in top) { if (!IsWindowVisible(t)) ShowWindow(t, SW_SHOWNA); Clip(t, 0); }
                    ClaimWork(top);
                    Program.RefreshDockOffset();      // 2.3.2: the Dock follows the work area at once
                }

                POINT p; GetCursorPos(out p);
                var now = DateTime.UtcNow;
                bool shell = ShellSurfaceActive();
                bool start = full && StartOrSearchActive();
                if (!revealed)
                {
                    bool atEdge = false;
                    if (!full)      // 2.3.0 完全隐藏: the edge never wakes the taskbar
                    {
                        foreach (var t in top) { RECT mon; if (Monitor(t, out mon) && p.X >= mon.L && p.X < mon.R && p.Y <= mon.T + EdgePx - 1 && p.Y >= mon.T - 2 && Hot(t, p.X)) atEdge = true; }
                        foreach (var t in top) RefreshHot(t, false);
                    }
                    if (atEdge && !FullscreenForeground()) { if (edgeSince == DateTime.MinValue) edgeSince = now; }
                    else edgeSince = DateTime.MinValue;
                    bool dwell = edgeSince != DateTime.MinValue && (now - edgeSince).TotalMilliseconds >= DwellMs;
                    if (ShouldReveal(full, dwell, shell, start))
                    {
                        revealed = true; edgeSince = DateTime.MinValue; outsideSince = DateTime.MinValue;
                        Slide(top, 0);
                    }
                    else if (!animating)
                        foreach (var t in top) if (!IsClippedEmpty(t)) Clip(t, 0); // explorer reset the region or a new tray appeared
                }
                else
                {
                    bool inside = false;
                    foreach (var t in top) { RECT r; if (GetWindowRect(t, out r) && p.X >= r.L - 6 && p.X < r.R + 6 && p.Y >= r.T - 6 && p.Y < r.B + 6) inside = true; }
                    if (animating) outsideSince = DateTime.MinValue;
                    else if (ShouldStay(full, inside, shell, start, MouseButtonsDown())) outsideSince = DateTime.MinValue;
                    else if (outsideSince == DateTime.MinValue) outsideSince = now;
                    else if ((now - outsideSince).TotalMilliseconds >= (full ? FullLeaveMs : LeaveMs))
                    {
                        revealed = false; outsideSince = DateTime.MinValue;
                        Slide(top, -1);
                    }
                }
                ClaimWork(top); // cheap: only acts when explorer has re-reserved the strip
            }
            finally { Undpi(old); }
        }

        // ---- Hot zones: only the parts of the top edge above taskbar content (start / app icons,
        // tray, clock) wake the taskbar; blank stretches do not. Found with UI Automation on a
        // worker thread, cached per tray and refreshed every few seconds; whole edge on failure.
        sealed class HotZones { public List<int[]> Spans; public DateTime At; public bool Busy; }
        static readonly Dictionary<IntPtr, HotZones> hot = new Dictionary<IntPtr, HotZones>();
        const int HotPad = 6, HotMerge = 20;
        const double HotRefreshSeconds = 5;

        internal static bool Hot(IntPtr tray, int x)
        {
            HotZones z;
            lock (hot) { if (!hot.TryGetValue(tray, out z) || z.Spans == null) return true; }
            foreach (var s in z.Spans) if (x >= s[0] && x <= s[1]) return true;
            return false;
        }

        internal static List<int[]> HotSpans(IntPtr tray) { lock (hot) { HotZones z; return hot.TryGetValue(tray, out z) && z.Spans != null ? new List<int[]>(z.Spans) : null; } }

        internal static void RefreshHot(IntPtr tray, bool force)
        {
            HotZones z;
            lock (hot)
            {
                if (!hot.TryGetValue(tray, out z)) { if (hot.Count > 8) hot.Clear(); hot[tray] = z = new HotZones { At = DateTime.MinValue }; }
                if (z.Busy || (!force && (DateTime.UtcNow - z.At).TotalSeconds < HotRefreshSeconds)) return;
                z.Busy = true;
            }
            Task.Run(delegate
            {
                List<int[]> spans = null;
                try { spans = ComputeHot(tray); } catch { spans = null; }
                lock (hot) { z.Spans = spans; z.At = DateTime.UtcNow; z.Busy = false; }
            });
        }

        internal static List<int[]> ComputeHot(IntPtr tray)
        {
            RECT tr; if (!GetWindowRect(tray, out tr)) return null;
            var root = AutomationElement.FromHandle(tray);
            var cond = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.SplitButton),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            var raw = new List<int[]>();
            int width = tr.R - tr.L, height = tr.B - tr.T;
            foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, cond))
            {
                System.Windows.Rect r;
                try { r = e.Current.BoundingRectangle; } catch { continue; }
                if (r.IsEmpty || r.Width < 4 || r.Width > width * .5 || r.Height > height * 1.6 + 4) continue;
                if (r.Right < tr.L || r.Left > tr.R) continue;
                raw.Add(new[] { (int)Math.Floor(r.Left) - HotPad, (int)Math.Ceiling(r.Right) + HotPad });
            }
            return MergeSpans(raw, HotMerge);
        }

        internal static List<int[]> MergeSpans(List<int[]> raw, int gap)
        {
            if (raw == null || raw.Count == 0) return null;
            raw.Sort((a, b) => a[0].CompareTo(b[0]));
            var merged = new List<int[]>();
            foreach (var s in raw)
            {
                if (merged.Count > 0 && s[0] <= merged[merged.Count - 1][1] + gap) merged[merged.Count - 1][1] = Math.Max(merged[merged.Count - 1][1], s[1]);
                else merged.Add(new[] { s[0], s[1] });
            }
            return merged;
        }

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int k);
        static bool MouseButtonsDown() { return (GetAsyncKeyState(1) & 0x8000) != 0 || (GetAsyncKeyState(2) & 0x8000) != 0; }

        // Hiding/revealing uses the tray's window region, never ShowWindow/SetWindowPos: explorer
        // re-reserves its strip (and pushes maximized windows down) whenever the tray is shown,
        // hidden or moved, but ignores region changes.
        [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
        [DllImport("user32.dll")] static extern int GetWindowRgn(IntPtr h, IntPtr rgn);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);

        // fraction: 0 = nothing visible, 1 = whole tray visible (region removed). The visible part is anchored to the screen edge the tray hugs.
        static void Clip(IntPtr w, double fraction)
        {
            RECT r; if (!GetWindowRect(w, out r)) return;
            if (fraction >= 1) { SetWindowRgn(w, IntPtr.Zero, true); return; }
            var c = ClipRect(EdgeFor(w), r.R - r.L, r.B - r.T, fraction);
            var rgn = CreateRectRgn(c[0], c[1], c[2], c[3]);
            if (SetWindowRgn(w, rgn, true) == 0) DeleteObject(rgn);
        }

        static bool IsClippedEmpty(IntPtr w)
        {
            var probe = CreateRectRgn(0, 0, 0, 0);
            try { return GetWindowRgn(w, probe) == 1; } finally { DeleteObject(probe); } // NULLREGION
        }

        static void Slide(List<IntPtr> trays, double to)
        {
            if (!animating) animTrays = new List<IntPtr>(trays);
            foreach (var t in animTrays) if (!IsWindowVisible(t)) ShowWindow(t, SW_SHOWNA);
            animFrom = animOffset; animTo = to; animating = true; animClock.Restart();
            if (anim == null)
            {
                anim = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(8) };
                anim.Tick += delegate { try { Step(); } catch (Exception e) { StopAnim(); Program.Log(e); } };
            }
            anim.Start();
            Step();
        }

        static void Step()
        {
            double t = Math.Min(1, animClock.Elapsed.TotalMilliseconds / (SlideMs * Math.Max(.25, Math.Abs(animTo - animFrom))));
            double e = animTo > animFrom ? 1 - Math.Pow(1 - t, 3) : t * t * t; // ease-out sliding in, ease-in sliding out
            animOffset = animFrom + (animTo - animFrom) * e;
            var old = Dpi();
            try
            {
                foreach (var w in animTrays) Clip(w, 1 + animOffset);
                if (t >= 1) { animating = false; anim.Stop(); }
            }
            finally { Undpi(old); }
        }

        static void StopAnim()
        {
            if (anim != null) anim.Stop();
            animating = false;
            animOffset = -1;
        }

        // Give the strip the taskbar reserved back to applications on each managed-tray monitor (any edge).
        // No SPIF_SENDCHANGE: explorer answers that broadcast by re-reserving its strip, which
        // makes the work area flap. Instead the already-maximized windows are resized directly.
        static void ClaimWork(List<IntPtr> top)
        {
            foreach (var t in top)
            {
                RECT mon; if (!Monitor(t, out mon)) continue;
                var edge = EdgeFor(t);
                var mi = new MONITORINFO { Size = Marshal.SizeOf(typeof(MONITORINFO)) };
                GetMonitorInfo(MonitorFromWindow(t, 2), ref mi);
                var w = Claim(edge, mon, mi.Work);
                if (w.T != mi.Work.T || w.B != mi.Work.B || w.L != mi.Work.L || w.R != mi.Work.R)
                {
                    if (SpiRect(SPI_SETWORKAREA, 0, ref w, 0))
                        ShiftMaximized(MonitorFromWindow(t, 2), edge, mi.Work, w);
                }
            }
        }

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr p);
        delegate bool EnumProc(IntPtr h, IntPtr p);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        static void ShiftMaximized(IntPtr monitor, Edge edge, RECT oldWork, RECT newWork)
        {
            EnumWindows(delegate (IntPtr h, IntPtr p)
            {
                RECT r;
                if (IsWindowVisible(h) && IsZoomed(h) && MonitorFromWindow(h, 2) == monitor && GetWindowRect(h, out r))
                {
                    var n = Shifted(edge, r, oldWork, newWork);
                    if (n.HasValue) SetWindowPos(h, IntPtr.Zero, n.Value.L, n.Value.T, n.Value.R - n.Value.L, n.Value.B - n.Value.T, 0x4000 | 0x0200 | 0x0010 | 0x0004);
                }
                return true;
            }, IntPtr.Zero);
        }

        // Show every tray and let its strip be reserved again (any edge). Also used by the crash guard.
        public static void Restore()
        {
            var old = Dpi();
            try
            {
                foreach (var t in Trays())
                {
                    SetWindowRgn(t, IntPtr.Zero, true);
                    RECT rr, mm;
                    if (GetWindowRect(t, out rr) && Monitor(t, out mm) && rr.T < mm.T && (rr.R - rr.L) > (rr.B - rr.T) * 3)
                        SetWindowPos(t, IntPtr.Zero, rr.L, mm.T, 0, 0, 0x0001 | 0x0004 | 0x0010 | 0x0200 | 0x0400);
                    ShowWindow(t, SW_SHOWNA);
                    RECT r, mon; if (!GetWindowRect(t, out r) || !Monitor(t, out mon)) continue;
                    var edge = EdgeOf(r, mon);
                    if (edge == Edge.None) continue;
                    var mi = new MONITORINFO { Size = Marshal.SizeOf(typeof(MONITORINFO)) };
                    GetMonitorInfo(MonitorFromWindow(t, 2), ref mi);
                    var w = Reserve(edge, r, mi.Work);
                    if (w.T != mi.Work.T || w.B != mi.Work.B || w.L != mi.Work.L || w.R != mi.Work.R)
                        if (SpiRect(SPI_SETWORKAREA, 0, ref w, 0)) ShiftMaximized(MonitorFromWindow(t, 2), edge, mi.Work, w);
                }
            }
            catch { }
            finally { Undpi(old); }
        }

        // ---- 2.3.0 完全隐藏 (full hide): only Start / search (Win key, Ctrl+Esc) bring the taskbar; the top edge does not.
        // No keyboard hook: the Start menu itself becoming the foreground window is the signal, so no key is ever intercepted.
        const int FullLeaveMs = 250;
        /// <summary>Hidden -> shown? Auto-hide: edge dwell or any shell surface (Start, tray flyout…). Full hide: only Start / search.</summary>
        internal static bool ShouldReveal(bool full, bool edgeDwell, bool shellSurface, bool startOrSearch)
        {
            return full ? startOrSearch : (shellSurface || edgeDwell);
        }
        /// <summary>Shown -> keep shown? While Start / search is open, while the pointer is on the taskbar or a button is held,
        /// and (both modes) while a shell surface opened from the taskbar (tray flyout, jump list) is in front.</summary>
        internal static bool ShouldStay(bool full, bool pointerInside, bool shellSurface, bool startOrSearch, bool buttonsDown)
        {
            return startOrSearch || shellSurface || pointerInside || buttonsDown;
        }
        static readonly string[] startProcs = { "startmenuexperiencehost", "searchhost", "searchapp" };
        static bool StartOrSearchActive()
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            uint pid; GetWindowThreadProcessId(fg, out pid);
            return Array.IndexOf(startProcs, ProcName(pid)) >= 0;
        }

        static readonly string[] shellProcs = { "startmenuexperiencehost", "searchhost", "searchapp", "shellexperiencehost", "shellhost" };
        static readonly string[] shellClasses = { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland", "Xaml_WindowedPopupClass", "XamlExplorerHostIslandWindow", "Windows.UI.Core.CoreWindow", "#32768", "TaskListThumbnailWnd", "ControlCenterWindow" };

        static bool ShellSurfaceActive()
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            uint pid; uint tid = GetWindowThreadProcessId(fg, out pid);
            string name = ProcName(pid);
            if (Array.IndexOf(shellProcs, name) >= 0) return true;
            if (name == "explorer")
            {
                var b = new System.Text.StringBuilder(128); GetClassName(fg, b, 128);
                if (Array.IndexOf(shellClasses, b.ToString()) >= 0) return true;
                var gi = new GUITHREADINFO { Size = Marshal.SizeOf(typeof(GUITHREADINFO)) };
                if (GetGUIThreadInfo(tid, ref gi) && (gi.Flags & 0x4) != 0) return true; // menu mode (jump list / tray menu)
            }
            return false;
        }

        static string ProcName(uint pid)
        {
            string n;
            if (procNames.TryGetValue(pid, out n)) return n;
            try { using (var p = Process.GetProcessById((int)pid)) n = p.ProcessName.ToLowerInvariant(); } catch { n = ""; }
            if (procNames.Count > 256) procNames.Clear();
            procNames[pid] = n; return n;
        }

        static bool FullscreenForeground()
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            var b = new System.Text.StringBuilder(64); GetClassName(fg, b, 64);
            string c = b.ToString();
            if (c == "Progman" || c == "WorkerW" || IsZoomed(fg)) return false;
            RECT r, mon; if (!GetWindowRect(fg, out r) || !Monitor(fg, out mon)) return false;
            return r.L <= mon.L && r.T <= mon.T && r.R >= mon.R && r.B >= mon.B;
        }
    }
}
