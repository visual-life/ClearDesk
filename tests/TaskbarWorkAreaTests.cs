using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Edge = ClearDesk.TopTaskbar.Edge;
using Rect = ClearDesk.TopTaskbar.RECT;

namespace ClearDesk
{
    internal static class TaskbarWorkAreaTests
    {
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect bounds, int size);

        static Rect R(int left, int top, int right, int bottom)
        {
            return new Rect { L = left, T = top, R = right, B = bottom };
        }

        static bool Same(Rect? actual, Rect expected)
        {
            return actual.HasValue && actual.Value.L == expected.L && actual.Value.T == expected.T
                && actual.Value.R == expected.R && actual.Value.B == expected.B;
        }

        internal static void Rules(Action<bool, string> check)
        {
            var screen = R(0, 0, 1920, 1080);
            var reserved = R(0, 0, 1920, 1032);
            var raw = R(-8, 0, 1928, 1040);
            check(Same(TopTaskbar.StaleWorkAreaBounds(Edge.Bottom, raw, reserved, reserved, screen, 4), R(-8, 0, 1928, 1088)),
                "a 48-pixel bottom strip is reclaimed while keeping native resize borders");
            check(!TopTaskbar.StaleWorkAreaBounds(Edge.Bottom, reserved, reserved, reserved, reserved, 4).HasValue,
                "keeps the window bounds when the taskbar still reserves its strip");
            check(!TopTaskbar.StaleWorkAreaBounds(Edge.Bottom, screen, screen, reserved, screen, 4).HasValue,
                "a window that already reaches the screen edge stays untouched");
            var snapped = R(0, 0, 960, 1032);
            check(!TopTaskbar.StaleWorkAreaBounds(Edge.Bottom, snapped, snapped, reserved, screen, 4).HasValue,
                "a half-screen snapped window is not treated as full-work-area");

            var top = R(0, 48, 1920, 1080);
            var left = R(-1860, -40, 0, 1040);
            var right = R(-1920, -40, -60, 1040);
            var secondary = R(-1920, -40, 0, 1040);
            check(Same(TopTaskbar.StaleWorkAreaBounds(Edge.Top, top, top, top, screen, 4), screen)
                && Same(TopTaskbar.StaleWorkAreaBounds(Edge.Left, left, left, left, secondary, 4), secondary)
                && Same(TopTaskbar.StaleWorkAreaBounds(Edge.Right, right, right, right, secondary, 4), secondary),
                "the top and side taskbar strips are reclaimed on primary and offset monitors");
        }

        static Rect GetFrame(IntPtr hwnd)
        {
            Rect bounds;
            if (DwmGetWindowAttribute(hwnd, 9, out bounds, Marshal.SizeOf(typeof(Rect))) != 0)
                throw new InvalidOperationException("The test window has no visible DWM frame.");
            return bounds;
        }

        internal static async Task Run(Action<bool, string> check)
        {
            var win = new Window { Title = "ClearDesk work area fixture", Width = 600, Height = 390, ShowActivated = false };
            try
            {
                win.Show();
                win.WindowState = WindowState.Maximized;
                await Task.Delay(250);
                IntPtr hwnd = new WindowInteropHelper(win).Handle;
                Rect work = GetFrame(hwnd);
                Rect reserved = work; reserved.B -= 48;
                Native.RECT raw;
                Native.GetWindowRect(hwnd, out raw);
                Native.SetWindowPos(hwnd, IntPtr.Zero, raw.L, raw.T, raw.R - raw.L, raw.B - raw.T - 48, 0x14);
                await Task.Delay(250);
                check(win.WindowState == WindowState.Maximized && GetFrame(hwnd).B == reserved.B,
                    "a real maximized window reproduces the old 48-pixel work-area gap");
                check(TopTaskbar.FitForeground(hwnd, Edge.Bottom, reserved, work, DateTime.UtcNow),
                    "repairs a late maximized window after the work area has already expanded");
                await Task.Delay(250);
                check(win.WindowState == WindowState.Maximized && GetFrame(hwnd).B == work.B,
                    "the real maximized window fills the freed strip and stays maximized");
                win.WindowState = WindowState.Normal;
                await Task.Delay(120);
                check(!TopTaskbar.FitForeground(hwnd, Edge.Bottom, reserved, work, DateTime.UtcNow),
                    "restored normal windows are left alone");

                var parameters = new HwndSourceParameters("ClearDesk borderless fullscreen fixture",
                    reserved.R - reserved.L, reserved.B - reserved.T)
                {
                    WindowStyle = unchecked((int)0x90000000),
                    PositionX = reserved.L, PositionY = reserved.T
                };
                using (var popup = new HwndSource(parameters))
                {
                    popup.RootVisual = new System.Windows.Controls.Border { Background = System.Windows.Media.Brushes.Teal };
                    Native.ShowWindow(popup.Handle, 8);
                    Native.SetWindowPos(popup.Handle, IntPtr.Zero, reserved.L, reserved.T,
                        reserved.R - reserved.L, reserved.B - reserved.T, 0x14);
                    await Task.Delay(150);
                    var frame = GetFrame(popup.Handle);
                    check(Same(frame, reserved), "borderless fixture matches the old work area (" + frame.L + "," + frame.T + "," + frame.R + "," + frame.B + ")");
                    DateTime now = DateTime.UtcNow;
                    check(TopTaskbar.FitForeground(popup.Handle, Edge.Bottom, reserved, work, now)
                        && !TopTaskbar.FitForeground(popup.Handle, Edge.Bottom, reserved, work, now.AddMilliseconds(200)),
                        "a borderless app still sized to the old work area is fitted once during repeated checks");
                    await Task.Delay(150);
                    var expanded = GetFrame(popup.Handle);
                    check(Same(expanded, work),
                        "a borderless app fills the gap (" + expanded.L + "," + expanded.T + "," + expanded.R + "," + expanded.B + ")");
                    TopTaskbar.RestoreExpandedPopups();
                    await Task.Delay(150);
                    check(Same(GetFrame(popup.Handle), reserved),
                        "restoring the taskbar restores the adjusted borderless window to its previous bounds");
                }
            }
            finally { TopTaskbar.RestoreExpandedPopups(); win.Close(); }
        }
    }
}
