using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

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

        // A tray counts as "top" when it hugs the monitor's top edge and is a horizontal strip.
        public static bool AtTop(IntPtr tray)
        {
            RECT r, mon;
            if (!GetWindowRect(tray, out r) || !Monitor(tray, out mon)) return false;
            return r.T <= mon.T + 1 && r.B >= mon.T - 1 && (r.R - r.L) > (r.B - r.T) * 3 && r.B < mon.T + (mon.B - mon.T) / 3;
        }

        public static void Sync()
        {
            bool want = Program.S != null && Program.S.TopTaskbarAutoHide && !Program.Preview;
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
                var trays = Trays();
                var top = trays.FindAll(AtTop);
                if (top.Count == 0)
                {
                    // Taskbar moved off the top edge (or explorer is restarting): give it back.
                    if (hidden) { Restore(); hidden = revealed = false; Program.Services.MarkTopTaskbar(false); }
                    return;
                }
                if (!hidden) { Program.Services.MarkTopTaskbar(true); hidden = true; revealed = false; animOffset = -1; foreach (var t in top) { if (!IsWindowVisible(t)) ShowWindow(t, SW_SHOWNA); Clip(t, 0); } ClaimWork(top); }

                POINT p; GetCursorPos(out p);
                var now = DateTime.UtcNow;
                bool shell = ShellSurfaceActive();
                if (!revealed)
                {
                    bool atEdge = false;
                    foreach (var t in top) { RECT mon; if (Monitor(t, out mon) && p.X >= mon.L && p.X < mon.R && p.Y <= mon.T + EdgePx - 1 && p.Y >= mon.T - 2) atEdge = true; }
                    if (atEdge && !FullscreenForeground()) { if (edgeSince == DateTime.MinValue) edgeSince = now; }
                    else edgeSince = DateTime.MinValue;
                    if (shell || (edgeSince != DateTime.MinValue && (now - edgeSince).TotalMilliseconds >= DwellMs))
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
                    foreach (var t in top) { RECT r; if (GetWindowRect(t, out r) && p.X >= r.L - 4 && p.X < r.R + 4 && p.Y >= r.T - 4 && p.Y < r.B + 6) inside = true; }
                    if (animating) outsideSince = DateTime.MinValue;
                    else if (inside || shell || MouseButtonsDown()) outsideSince = DateTime.MinValue;
                    else if (outsideSince == DateTime.MinValue) outsideSince = now;
                    else if ((now - outsideSince).TotalMilliseconds >= LeaveMs)
                    {
                        revealed = false; outsideSince = DateTime.MinValue;
                        Slide(top, -1);
                    }
                }
                ClaimWork(top); // cheap: only acts when explorer has re-reserved the strip
            }
            finally { Undpi(old); }
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

        // fraction: 0 = nothing visible, 1 = whole tray visible (region removed).
        static void Clip(IntPtr w, double fraction)
        {
            RECT r; if (!GetWindowRect(w, out r)) return;
            if (fraction >= 1) { SetWindowRgn(w, IntPtr.Zero, true); return; }
            int h = (int)Math.Round((r.B - r.T) * Math.Max(0, fraction));
            var rgn = CreateRectRgn(0, 0, r.R - r.L, h);
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

        // Give the strip the taskbar reserved back to applications on each top-tray monitor.
        // No SPIF_SENDCHANGE: explorer answers that broadcast by re-reserving its strip, which
        // makes the work area flap. Instead the already-maximized windows are resized directly.
        static void ClaimWork(List<IntPtr> top)
        {
            foreach (var t in top)
            {
                RECT mon; if (!Monitor(t, out mon)) continue;
                var mi = new MONITORINFO { Size = Marshal.SizeOf(typeof(MONITORINFO)) };
                GetMonitorInfo(MonitorFromWindow(t, 2), ref mi);
                if (mi.Work.T > mon.T)
                {
                    var w = mi.Work; w.T = mon.T;
                    if (SpiRect(SPI_SETWORKAREA, 0, ref w, 0))
                        ShiftMaximized(MonitorFromWindow(t, 2), mi.Work.T, mon.T);
                }
            }
        }

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr p);
        delegate bool EnumProc(IntPtr h, IntPtr p);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        static void ShiftMaximized(IntPtr monitor, int oldTop, int newTop)
        {
            int delta = newTop - oldTop;
            if (delta == 0) return;
            EnumWindows(delegate (IntPtr h, IntPtr p)
            {
                RECT r;
                if (IsWindowVisible(h) && IsZoomed(h) && MonitorFromWindow(h, 2) == monitor && GetWindowRect(h, out r) && r.T >= oldTop - 24 && r.T <= oldTop + 2)
                    SetWindowPos(h, IntPtr.Zero, r.L, r.T + delta, r.R - r.L, r.B - r.T - delta, 0x4000 | 0x0200 | 0x0010 | 0x0004);
                return true;
            }, IntPtr.Zero);
        }

        // Show every tray and let the top strip be reserved again. Also used by the crash guard.
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
                    if (!AtTop(t)) continue;
                    RECT r, mon; if (!GetWindowRect(t, out r) || !Monitor(t, out mon)) continue;
                    var mi = new MONITORINFO { Size = Marshal.SizeOf(typeof(MONITORINFO)) };
                    GetMonitorInfo(MonitorFromWindow(t, 2), ref mi);
                    if (mi.Work.T < r.B) { var w = mi.Work; w.T = r.B; if (SpiRect(SPI_SETWORKAREA, 0, ref w, 0)) ShiftMaximized(MonitorFromWindow(t, 2), mi.Work.T, r.B); }
                }
            }
            catch { }
            finally { Undpi(old); }
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
