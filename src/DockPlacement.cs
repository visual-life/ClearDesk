using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace ClearDesk
{
    /// <summary>What the Dock needs to know to find the bottom of the screen: the monitor, its work area, the taskbar and the scale. All rectangles in physical pixels.</summary>
    internal struct DockGeometry
    {
        public TopTaskbar.RECT Monitor, Work, Tray;
        public TopTaskbar.Edge TrayEdge;          // edge of the taskbar on this monitor as it is right now (None = no taskbar, or Windows auto-hid it)
        public bool HiddenByClearDesk;            // 完全隐藏任务栏 / 顶部自动隐藏 has clipped it away
        public double Scale;                      // physical pixels per WPF unit (DPI / 96)
        public override string ToString() { return "mon " + Monitor.T + ".." + Monitor.B + " work " + Work.T + ".." + Work.B + " tray " + TrayEdge + (HiddenByClearDesk ? " (hidden)" : "") + " x" + Scale.ToString("0.00"); }
    }

    /// <summary>2.3.2: where the Dock's bottom edge is. 距屏幕底部距离 = 0 means the visible bottom of the Dock plate touches the physical bottom of the monitor when no taskbar is
    /// in the way, and touches the top of the taskbar when it is visible at the bottom. The transparent shadow padding below the plate may hang off the screen.</summary>
    internal static class DockPlacement
    {
        /// <summary>Transparent space below the plate inside the Dock window (WPF units); the plate sits this far above the window's bottom edge.</summary>
        internal const double ShadowPad = 5;

        internal static Func<DockWindow, DockGeometry> Provider = Real;

        /// <summary>Physical pixel row where the Dock's bottom (offset 0) rests.</summary>
        internal static int BottomPx(DockGeometry g)
        {
            bool visibleBottomTaskbar = g.TrayEdge == TopTaskbar.Edge.Bottom && !g.HiddenByClearDesk;
            int bottom = g.Monitor.B;
            if (visibleBottomTaskbar) bottom = Math.Min(g.Work.B, g.Tray.T);
            return Math.Max(g.Monitor.T, Math.Min(g.Monitor.B, bottom));
        }

        static double Scale(DockGeometry g) { return g.Scale > 0 ? g.Scale : 1; }
        internal static double BottomDip(DockGeometry g) { return BottomPx(g) / Scale(g); }
        internal static double MonitorBottomDip(DockGeometry g) { return g.Monitor.B / Scale(g); }

        /// <summary>The window's Top (WPF units) for a Dock window of this height and this 距屏幕底部距离.</summary>
        internal static double Top(DockGeometry g, double height, double offset) { return BottomDip(g) - height - offset + ShadowPad; }

        /// <summary>Horizontal room for the Dock: the work area (left / right taskbar excluded while it is visible), in WPF units.</summary>
        internal static Rect WorkDip(DockGeometry g)
        {
            double s = Scale(g);
            return new Rect(g.Work.L / s, g.Work.T / s, Math.Max(0, (g.Work.R - g.Work.L) / s), Math.Max(0, (g.Work.B - g.Work.T) / s));
        }

        internal static string Signature(DockGeometry g)
        {
            return g.Monitor.L + "," + g.Monitor.T + "," + g.Monitor.R + "," + g.Monitor.B + "|" + g.Work.L + "," + g.Work.T + "," + g.Work.R + "," + g.Work.B + "|" + g.TrayEdge + g.HiddenByClearDesk + "|" + g.Tray.T + "|" + g.Scale.ToString("0.###");
        }

        [StructLayout(LayoutKind.Sequential)] struct MI { public int Size; public TopTaskbar.RECT Monitor, Work; public uint Flags; }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(long pt, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr m, ref MI i);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out TopTaskbar.RECT r);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr m, int type, out uint x, out uint y);

        static DockGeometry Real(DockWindow dock)
        {
            var g = new DockGeometry { Scale = 1 };
            try
            {
                IntPtr hwnd = dock == null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(dock).Handle;
                IntPtr mon = hwnd != IntPtr.Zero ? MonitorFromWindow(hwnd, 1) : MonitorFromPoint(0, 1);
                var mi = new MI { Size = Marshal.SizeOf(typeof(MI)) };
                if (!GetMonitorInfo(mon, ref mi))
                {
                    var sb = System.Windows.Forms.Screen.PrimaryScreen;
                    g.Monitor = new TopTaskbar.RECT { L = sb.Bounds.Left, T = sb.Bounds.Top, R = sb.Bounds.Right, B = sb.Bounds.Bottom };
                    g.Work = new TopTaskbar.RECT { L = sb.WorkingArea.Left, T = sb.WorkingArea.Top, R = sb.WorkingArea.Right, B = sb.WorkingArea.Bottom };
                }
                else { g.Monitor = mi.Monitor; g.Work = mi.Work; }
                uint dx, dy;
                if (dock != null && dock.IsLoaded) g.Scale = VisualTreeHelper.GetDpi(dock).DpiScaleX;
                else if (GetDpiForMonitor(mon, 0, out dx, out dy) == 0 && dx > 0) g.Scale = dx / 96.0;
                foreach (var t in TopTaskbar.Trays())
                {
                    TopTaskbar.RECT r;
                    if (MonitorFromWindow(t, 1) != mon || !GetWindowRect(t, out r)) continue;
                    var e = TopTaskbar.EdgeOf(r, g.Monitor);
                    if (e == TopTaskbar.Edge.None) continue;
                    // Windows' own auto-hide: the taskbar floats over the screen when it shows, it does not reserve room
                    if (e != TopTaskbar.Edge.Top && TopTaskbar.WindowsAutoHide()) continue;
                    g.TrayEdge = e; g.Tray = r; g.HiddenByClearDesk = TopTaskbar.HidesEdge(e);
                    if (e == TopTaskbar.Edge.Bottom) break;
                }
            }
            catch (Exception e) { Program.Log(e); }
            return g;
        }
    }
}
