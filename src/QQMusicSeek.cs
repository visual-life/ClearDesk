using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;

namespace ClearDesk
{
    // QQ Music publishes an SMTC session but leaves IsPlaybackPositionEnabled off, so
    // TryChangePlaybackPositionAsync is refused. Its own player bar is visible to UI Automation:
    // "[ 01:45] [progress pane] [ 03:38]". Seek by posting a click on that pane (no cursor move, no focus change).
    internal static class QQMusicSeek
    {
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr window, ref POINT point);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        struct POINT { public int X, Y; }
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        struct RECT { public int L, T, R, B; }
        // UIA can report the bar's stale pre-minimize position for a moment after restore; only trust
        // a rectangle that lies inside the window's current bounds.
        static bool Inside(IntPtr window, Rect bar)
        {
            RECT r; if (bar.IsEmpty || !GetWindowRect(window, out r)) return false;
            return bar.Left >= r.L && bar.Right <= r.R && bar.Top >= r.T && bar.Bottom <= r.B;
        }
        static readonly Regex Time = new Regex(@"^\s*\d{1,3}:\d{2}\s*$");

        // Returns the screen rectangle of QQ Music's progress bar, or Rect.Empty.
        internal static Rect FindBar(out IntPtr window, out int processId)
        {
            window = IntPtr.Zero; processId = 0;
            foreach (var p in Process.GetProcessesByName("QQMusic"))
                using (p)
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    var root = AutomationElement.FromHandle(p.MainWindowHandle);
                    var texts = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                        .Cast<AutomationElement>().Select(e => new { e.Current.Name, e.Current.BoundingRectangle })
                        .Where(t => Time.IsMatch(t.Name ?? "") && !t.BoundingRectangle.IsEmpty).ToArray();
                    // Pick the pair of time labels on the same row with the widest gap: elapsed (left) and total (right).
                    Rect best = Rect.Empty;
                    foreach (var a in texts)
                        foreach (var b in texts)
                        {
                            if (b.BoundingRectangle.Left <= a.BoundingRectangle.Right || Math.Abs(a.BoundingRectangle.Top - b.BoundingRectangle.Top) > 6) continue;
                            var gap = new Rect(a.BoundingRectangle.Right, a.BoundingRectangle.Top, b.BoundingRectangle.Left - a.BoundingRectangle.Right, a.BoundingRectangle.Height);
                            if (gap.Width > 60 && (best.IsEmpty || gap.Width > best.Width)) best = gap;
                        }
                    if (best.IsEmpty) continue;
                    // Prefer the exact pane that sits in that gap (the bar itself).
                    var panes = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane))
                        .Cast<AutomationElement>().Select(e => e.Current.BoundingRectangle)
                        .Where(r => !r.IsEmpty && r.Left >= best.Left - 2 && r.Right <= best.Right + 2 && Math.Abs((r.Top + r.Height / 2) - (best.Top + best.Height / 2)) < 8 && r.Width > best.Width * .6)
                        .OrderByDescending(r => r.Width).ToArray();
                    window = p.MainWindowHandle; processId = p.Id;
                    return panes.Length > 0 ? panes[0] : best;
                }
            return Rect.Empty;
        }

        internal static string LastError = "";
        static string LayoutFile { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClearDesk", "qqmusic-seekbar.txt"); } }
        // Stores the bar rectangle relative to the window: width height left top barWidth barHeight.
        static void Remember(IntPtr window, Rect bar)
        {
            RECT r; if (!GetWindowRect(window, out r)) return;
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LayoutFile));
                System.IO.File.WriteAllText(LayoutFile, string.Join(" ", new[] { r.R - r.L, r.B - r.T, (int)bar.Left - r.L, (int)bar.Top - r.T, (int)bar.Width, (int)bar.Height }));
            }
            catch { }
        }
        static Rect Recall(IntPtr window)
        {
            RECT r; if (!GetWindowRect(window, out r)) return Rect.Empty;
            try
            {
                var v = System.IO.File.ReadAllText(LayoutFile).Split(' ').Select(int.Parse).ToArray();
                if (v.Length != 6 || v[0] != r.R - r.L || v[1] != r.B - r.T) return Rect.Empty; // window size changed: layout unknown
                LastError += " (learned layout)";
                return new Rect(r.L + v[2], r.T + v[3], v[4], v[5]);
            }
            catch { return Rect.Empty; }
        }
        internal static bool Seek(double ratio)
        {
            LastError = "QQ window not found";
            IntPtr main = IntPtr.Zero;
            foreach (var p in Process.GetProcessesByName("QQMusic")) using (p) if (main == IntPtr.Zero && p.MainWindowHandle != IntPtr.Zero) main = p.MainWindowHandle;
            if (main == IntPtr.Zero) return false;
            ratio = Math.Max(0, Math.Min(1, ratio));
            // Minimized: restore invisibly without activation, click, minimize again (see WindowPeek).
            return WindowPeek.WithRestored(main, () =>
            {
                IntPtr window = IntPtr.Zero; int pid = 0;
                var started = DateTime.UtcNow;
                var bar = WindowPeek.Wait(() => FindBar(out window, out pid), r => Inside(main, r), 2000);
                RECT wr; GetWindowRect(main, out wr);
                LastError = "waited " + (int)(DateTime.UtcNow - started).TotalMilliseconds + "ms last=" + bar + " win=" + wr.L + "," + wr.T + "," + wr.R + "," + wr.B;
                if (Inside(main, bar)) Remember(main, bar);
                else
                {
                    // UIA kept a stale layout: use the bar position learned for this window size, (a guessed position is not used:
                    // QQ ignores clicks while its own layout is stale, which would only fake a success).
                    bar = Recall(main);
                }
                if (bar.IsEmpty) { LastError += " bar not found"; return false; }
                int x = (int)Math.Round(bar.Left + 1 + ratio * (bar.Width - 2)), y = (int)Math.Round(bar.Top + bar.Height / 2);
                bool ok = WindowPeek.PostClick(window, x, y);
                LastError += " bar=" + bar + " point=" + x + "," + y + " " + WindowPeek.Last;
                System.Threading.Thread.Sleep(WindowPeek.SettleMs); // let QQ handle the click before it is minimized again
                return ok;
            });
        }
    }
}
