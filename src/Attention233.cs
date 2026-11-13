using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace ClearDesk
{
    /// <summary>2.3.3: which windows are asking for attention (FlashWindowEx / taskbar flash), taken from the Windows shell hook. It does not need the taskbar at all, so it keeps
    /// working while the taskbar is hidden (完全隐藏任务栏). The Dock paints a pulsing highlight on the items whose windows are in here until the window is activated or closes.</summary>
    internal static class Attention
    {
        internal const int WindowDestroyed = 2, WindowActivated = 4, Redraw = 6, RudeAppActivated = 0x8004, Flash = 0x8006;
        // HSHELL_REDRAW (6) is "the button was repainted"; with the high bit set (0x8006 = HSHELL_FLASH) the button is flashing. A plain 6 for a flashing window therefore means the flash ended.

        static readonly Dictionary<IntPtr, DateTime> flashing = new Dictionary<IntPtr, DateTime>();
        internal static event Action Changed;
        internal static Func<IntPtr, bool> IsAlive = delegate (IntPtr h) { return Native.IsWindow(h); };
        internal static Func<IntPtr, IntPtr> RootOf = ForegroundTracker.RootOwner;
        internal static Func<IntPtr, bool> IsOwn = delegate (IntPtr h) { uint pid; Native.GetWindowThreadProcessId(h, out pid); return pid == (uint)Process.GetCurrentProcess().Id; };
        internal static Func<IntPtr, AppIdentity> IdentityOf = IdentityOfWindow;
        internal static Func<DateTime> Now = delegate { return DateTime.UtcNow; };
        static readonly Dictionary<IntPtr, KeyValuePair<DateTime, AppIdentity>> identities = new Dictionary<IntPtr, KeyValuePair<DateTime, AppIdentity>>();
        internal static int Messages;
        internal static readonly List<string> Trace = new List<string>();      // the last shell messages (diagnostics / tests)

        /// <summary>Handles one shell hook message (code = wParam, window = lParam). Returns true when the set of flashing windows changed.</summary>
        internal static bool Handle(int code, IntPtr window)
        {
            Messages++;
            lock (Trace) { if (Trace.Count >= 60) Trace.RemoveAt(0); Trace.Add("0x" + code.ToString("X") + " " + window.ToInt64().ToString("X")); }
            if (window == IntPtr.Zero) return false;
            bool changed = false;
            lock (flashing)
            {
                switch (code)
                {
                    case Flash:
                        if (Program.S == null || !Program.S.DockAttention) return false;
                        if (IsOwn(window)) return false;
                        var root = RootOf(window);
                        if (root == Native.GetForegroundWindow()) return false;            // the window the user is looking at does not flash
                        if (!flashing.ContainsKey(root)) { flashing[root] = Now(); changed = true; }
                        break;
                    case Redraw:
                        { var r = RootOf(window); if (flashing.Remove(r)) changed = true; }
                        break;
                    case WindowActivated:
                    case RudeAppActivated:
                    case WindowDestroyed:
                        changed = Remove(window) | Remove(RootOf(window));
                        break;
                }
            }
            if (changed && Changed != null) Changed();
            return changed;
        }

        static bool Remove(IntPtr h) { return flashing.Remove(h); }

        /// <summary>Called by the Dock: forget windows that no longer exist and the one in front.</summary>
        internal static bool Prune()
        {
            bool changed = false;
            lock (flashing)
            {
                var fg = Native.GetForegroundWindow(); var fgRoot = fg == IntPtr.Zero ? fg : RootOf(fg);
                foreach (var h in flashing.Keys.ToArray())
                    if (!IsAlive(h) || (fgRoot != IntPtr.Zero && h == fgRoot)) { flashing.Remove(h); changed = true; }
            }
            return changed;
        }

        internal static void Clear() { lock (flashing) flashing.Clear(); }
        internal static IntPtr[] Windows { get { lock (flashing) return flashing.Keys.ToArray(); } }
        internal static bool IsFlashing(IntPtr h) { lock (flashing) return flashing.ContainsKey(h); }
        internal static void Add(IntPtr h) { lock (flashing) flashing[h] = Now(); }           // test hook

        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, Native.EnumProc callback, IntPtr parameter);
        internal static AppIdentity IdentityOfWindow(IntPtr h)
        {
            KeyValuePair<DateTime, AppIdentity> c;
            if (identities.TryGetValue(h, out c) && c.Key > DateTime.UtcNow) return c.Value;
            AppIdentity id = null;
            try
            {
                uint pid; Native.GetWindowThreadProcessId(h, out pid);
                id = RunningApps.ProcessIdentity(pid);
                if (id != null && AppIcons.IsHostExe(id.Exe))
                {
                    uint found = 0;
                    EnumChildWindows(h, delegate (IntPtr child, IntPtr unused) { uint cp; Native.GetWindowThreadProcessId(child, out cp); if (cp != 0 && cp != pid && Native.Class(child) == "Windows.UI.Core.CoreWindow") { found = cp; return false; } return true; }, IntPtr.Zero);
                    if (found != 0) { var real = RunningApps.ProcessIdentity(found); if (real != null) id = new AppIdentity { Key = id.Key, Exe = real.Exe, AppId = real.AppId ?? id.AppId, Name = real.Name, LaunchPath = real.LaunchPath }; }
                }
            }
            catch { }
            if (identities.Count > 128) identities.Clear();
            identities[h] = new KeyValuePair<DateTime, AppIdentity>(DateTime.UtcNow.AddSeconds(30), id);
            return id;
        }

        /// <summary>Does this flashing window belong to the Dock item? By window handle first (the Dock's own window list), then by program / package identity (pinned items that are running, windows the Dock has not listed yet, UWP frames).</summary>
        internal static bool Matches(DockEntry entry, IntPtr window)
        {
            if (entry == null) return false;
            if (entry.Running != null && entry.Running.Windows.Contains(window)) return true;
            var id = IdentityOf(window);
            if (id == null) return false;
            if (entry.Identity != null && AppIdentity.Matches(id, entry.Identity)) return true;
            if (entry.PinPath != null)
            {
                string pin = AppIdentity.Normalize(entry.PinPath);
                if (!string.IsNullOrEmpty(id.Exe) && string.Equals(pin, id.Exe, StringComparison.OrdinalIgnoreCase)) return true;
                if (!string.IsNullOrEmpty(id.AppId) && entry.PinPath.IndexOf(id.AppId, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        internal static bool IsFlashing(DockEntry entry) { return Since(entry) != null; }

        /// <summary>When the oldest flashing window of this item started flashing (null = not flashing).</summary>
        internal static DateTime? Since(DockEntry entry)
        {
            if (entry == null) return null;
            DateTime? best = null;
            KeyValuePair<IntPtr, DateTime>[] list; lock (flashing) list = flashing.ToArray();
            foreach (var f in list)
                if (Matches(entry, f.Key) && (best == null || f.Value < best)) best = f.Value;
            return best;
        }

        // ---- the shell hook window
        static HwndSource sink; static uint shellMessage;
        [DllImport("user32.dll")] static extern bool RegisterShellHookWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool DeregisterShellHookWindow(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
        internal static bool Running { get { return sink != null; } }

        internal static bool Start()
        {
            if (sink != null) return true;
            try
            {
                shellMessage = RegisterWindowMessage("SHELLHOOK");
                var p = new HwndSourceParameters("ClearDesk attention") { WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80 | 0x08000000, Width = 0, Height = 0, PositionX = -32000, PositionY = -32000 };
                sink = new HwndSource(p);
                sink.AddHook(delegate (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
                {
                    if (shellMessage != 0 && (uint)msg == shellMessage) { try { Handle(unchecked((int)wParam.ToInt64()), lParam); } catch (Exception e) { Program.Log(e); } }
                    return IntPtr.Zero;
                });
                if (!RegisterShellHookWindow(sink.Handle)) { Program.LogText("RegisterShellHookWindow 失败：应用提醒闪烁不可用"); sink.Dispose(); sink = null; return false; }
                return true;
            }
            catch (Exception e) { Program.Log(e); sink = null; return false; }
        }

        internal static void Stop()
        {
            if (sink == null) return;
            try { DeregisterShellHookWindow(sink.Handle); sink.Dispose(); } catch { }
            sink = null; Clear();
        }
    }
}
