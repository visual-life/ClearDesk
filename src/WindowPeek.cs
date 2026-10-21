using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClearDesk
{
    // Lets seek helpers work on a minimized player without the user noticing:
    // the window is made fully transparent, restored WITHOUT activation, the action runs,
    // then it is minimized again (no activation) and its original style/placement are put back.
    internal static class WindowPeek
    {
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] struct WINDOWPLACEMENT { public int length, flags, showCmd; public POINT min, max; public RECT normal; }
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
        [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetLong(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetLong(IntPtr h, int i, IntPtr v);
        [DllImport("user32.dll")] static extern bool GetLayeredWindowAttributes(IntPtr h, out uint key, out byte alpha, out uint flags);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr h, IntPtr r, IntPtr rgn, uint flags);
        [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] static extern IntPtr ChildWindowFromPointEx(IntPtr h, POINT p, uint flags);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l, uint f, uint t, out IntPtr r);
        static int Env(string name, int fallback) { int v; return int.TryParse(Environment.GetEnvironmentVariable(name), out v) ? v : fallback; }
        internal static int SettleMs { get { return Env("CLEARDESK_PEEK_WAIT", 600); } }
        const int GWL_EXSTYLE = -20; const long WS_EX_LAYERED = 0x80000; const uint LWA_ALPHA = 2;
        static readonly object gate = new object();
        internal static string Last = "";

        internal static T WithRestored<T>(IntPtr window, Func<T> action)
        {
            if (window == IntPtr.Zero || !IsIconic(window)) { Last = ""; var r = action(); Last += "; visible"; return r; }
            lock (gate)
            {
                IntPtr foreground = GetForegroundWindow();
                var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
                GetWindowPlacement(window, ref placement);
                long ex = GetLong(window, GWL_EXSTYLE).ToInt64();
                bool wasLayered = (ex & WS_EX_LAYERED) != 0;
                uint key = 0, flags = 0; byte alpha = 255;
                if (wasLayered) GetLayeredWindowAttributes(window, out key, out alpha, out flags);
                try
                {
                    if (!wasLayered) SetLong(window, GWL_EXSTYLE, new IntPtr(ex | WS_EX_LAYERED));
                    SetLayeredWindowAttributes(window, 0, (byte)Env("CLEARDESK_PEEK_ALPHA", 1), LWA_ALPHA); // ~invisible; alpha 1 keeps it "visible" to occlusion tracking
                    ShowWindow(window, 4);                                // SW_SHOWNOACTIVATE
                    Last = "";
                    // Some players (QQ Music) only re-layout on a position change / WM_SIZE after restore.
                    SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020); // NOSIZE|NOMOVE|NOZORDER|NOACTIVATE|FRAMECHANGED
                    RECT client; if (GetClientRect(window, out client)) { IntPtr ignored; SendMessageTimeout(window, 0x0005, IntPtr.Zero, (IntPtr)(((client.B - client.T) << 16) | ((client.R - client.L) & 0xFFFF)), 2, 500, out ignored); }
                    RedrawWindow(window, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0080 | 0x0100); // invalidate+erase+allchildren+updatenow
                    return action();
                }
                finally
                {
                    ShowWindow(window, 7);                                // SW_SHOWMINNOACTIVE
                    placement.showCmd = 7; SetWindowPlacement(window, ref placement);
                    if (wasLayered) SetLayeredWindowAttributes(window, key, alpha, flags == 0 ? LWA_ALPHA : flags);
                    else { SetLayeredWindowAttributes(window, 0, 255, LWA_ALPHA); SetLong(window, GWL_EXSTYLE, new IntPtr(ex)); }
                    Last += "; peeked; foreground kept=" + (GetForegroundWindow() == foreground) + " iconic=" + IsIconic(window);
                }
            }
        }

        // Post a left click to the deepest child window under a screen point of the given top-level
        // window. Does not depend on z-order, so it works while covered or transparent.
        internal static bool PostClick(IntPtr top, int x, int y)
        {
            IntPtr target = top;
            for (int depth = 0; depth < 8; depth++)
            {
                var pt = new POINT { X = x, Y = y }; ScreenToClient(target, ref pt);
                IntPtr child = ChildWindowFromPointEx(target, pt, 1); // CWP_SKIPINVISIBLE
                if (child == IntPtr.Zero || child == target) break;
                target = child;
            }
            var client = new POINT { X = x, Y = y };
            if (!ScreenToClient(target, ref client)) return false;
            IntPtr l = (IntPtr)((client.Y << 16) | (client.X & 0xFFFF));
            if (Env("CLEARDESK_PEEK_SEND", 1) == 1)
            {
                IntPtr r;
                SendMessageTimeout(target, 0x0200, IntPtr.Zero, l, 2, 500, out r);
                SendMessageTimeout(target, 0x0201, (IntPtr)1, l, 2, 500, out r);
                SendMessageTimeout(target, 0x0202, IntPtr.Zero, l, 2, 500, out r);
            }
            else
            {
                PostMessage(target, 0x0200, IntPtr.Zero, l);
                PostMessage(target, 0x0201, (IntPtr)1, l);
                PostMessage(target, 0x0202, IntPtr.Zero, l);
            }
            Last = "target=" + target;
            return true;
        }

        // Polls until the condition holds (layout/accessibility catch up after restore).
        internal static T Wait<T>(Func<T> probe, Func<T, bool> ok, int milliseconds)
        {
            var until = DateTime.UtcNow.AddMilliseconds(milliseconds); T value;
            do { value = probe(); if (ok(value)) return value; Thread.Sleep(60); } while (DateTime.UtcNow < until);
            return value;
        }
    }
}
