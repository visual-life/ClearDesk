using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ClearDesk
{
    internal enum TrayWakeResult { NotApplicable, Woken, Unavailable }

    internal static class DockTrayWake
    {
        sealed class Profile
        {
            public string[] Processes, Labels;
            public Profile(string[] processes, string[] labels) { Processes = processes; Labels = labels; }
        }

        static readonly Profile qq = new Profile(new[] { "QQ", "QQScLauncher" }, new[] { "QQ", "腾讯QQ", "腾讯 QQ" });
        static readonly Profile wechat = new Profile(new[] { "WeChat", "Weixin" }, new[] { "微信", "WeChat", "Weixin" });
        static readonly int sessionId = Process.GetCurrentProcess().SessionId;
        static readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;

        internal sealed class Target
        {
            internal readonly Dictionary<uint, string> Processes = new Dictionary<uint, string>();
            internal string[] Labels = new string[0];
            internal bool OtherInstall;
            internal bool Known { get { return Labels.Length != 0; } }
            internal bool Owns(string exe)
            {
                return !string.IsNullOrEmpty(exe) && Processes.Values.Contains(AppIdentity.Normalize(exe), StringComparer.OrdinalIgnoreCase);
            }
            internal bool IsAlive() { return Processes.Any(p => IsProcess(p.Key, p.Value)); }
        }

        static string Exe(AppIdentity identity) { return identity == null ? null : identity.RealExe ?? identity.Exe; }

        static Profile ForExe(string exe)
        {
            string stem = Path.GetFileNameWithoutExtension(exe);
            return qq.Processes.Contains(stem, StringComparer.OrdinalIgnoreCase) ? qq
                : wechat.Processes.Contains(stem, StringComparer.OrdinalIgnoreCase) ? wechat : null;
        }

        internal static bool IsKnown(AppIdentity identity) { return ForExe(Exe(identity)) != null; }

        internal static bool IsProcess(uint pid, string exe)
        {
            return !string.IsNullOrEmpty(exe) && string.Equals(ProcessTree.ExeOf(pid), exe, StringComparison.OrdinalIgnoreCase);
        }

        static bool SameInstall(string first, string second)
        {
            string a = Path.GetDirectoryName(first), b = Path.GetDirectoryName(second);
            return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
                && (a == b || a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase));
        }

        internal static Target FindTarget(AppIdentity pin, AppIdentity observed)
        {
            var target = new Target();
            var paths = new[] { Exe(pin), Exe(observed) }.Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(AppIdentity.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length == 0) return target;
            var profile = paths.Select(ForExe).FirstOrDefault(p => p != null);
            if (profile != null) target.Labels = profile.Labels;
            var names = paths.Select(Path.GetFileNameWithoutExtension).Concat(profile?.Processes ?? new string[0])
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
                foreach (var process in Process.GetProcessesByName(name))
                    using (process)
                    {
                        try
                        {
                            if (process.SessionId != sessionId) continue;
                            uint pid = (uint)process.Id;
                            if (pid == ownPid) continue;
                            string actual = ProcessTree.ExeOf(pid);
                            if (actual == null) continue;
                            if (paths.Contains(actual, StringComparer.OrdinalIgnoreCase)
                                || (profile != null && ForExe(actual) == profile && paths.Any(p => ForExe(p) == profile && SameInstall(p, actual))))
                                target.Processes[pid] = actual;
                            else if (profile != null && ForExe(actual) == profile)
                                target.OtherInstall = true;
                        }
                        catch (InvalidOperationException) { }
                    }
            return target;
        }

        internal static TrayWakeResult TryWake(DockEntry entry, AppIdentity observed)
        {
            var target = FindTarget(entry.Identity, observed);
            if (target.Processes.Count == 0) return TrayWakeResult.NotApplicable;
            var window = FindVisibleWindow(target);
            if (window != IntPtr.Zero)
            {
                WinOps.Instance.Foreground(window);
                return TrayWakeResult.Woken;
            }
            var records = TrayNames.Entries();
            if (target.Known && !records.Any(r => r.Running && target.Owns(r.Exe))) records = TrayNames.ReadEntries(true);
            if (!target.Known && !records.Any(r => target.Owns(r.Exe))) return TrayWakeResult.NotApplicable;
            var result = TrayAccess.WakeDockApp(target, records);
            if (result != TrayWakeResult.Woken && !target.IsAlive()) return TrayWakeResult.NotApplicable;
            return result == TrayWakeResult.NotApplicable && target.Known ? TrayWakeResult.Unavailable : result;
        }

        static bool HasLabel(string raw, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(raw)) return false;
            name = TrayAccess.Clean(name);
            raw = TrayAccess.Clean(raw);
            if (string.Equals(raw, name, StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Length == 0 || !raw.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return false;
            string suffix = raw.Substring(name.Length).TrimStart();
            return suffix.Length > 0 && ":：·(（".IndexOf(suffix[0]) >= 0;
        }

        static bool MatchesLabel(string raw, NotifyEntry record)
        {
            return HasLabel(raw, record.Tooltip) || HasLabel(raw, record.Description) || HasLabel(raw, Path.GetFileNameWithoutExtension(record.Exe));
        }

        internal static bool MatchesIcon(Target target, TrayAccess.RawIcon icon, IList<NotifyEntry> records)
        {
            var own = records.Where(r => target.Owns(r.Exe)).ToList();
            var other = records.Where(r => r.Running && !target.Owns(r.Exe)).ToList();
            bool ownLabel = target.Labels.Any(label => HasLabel(icon.Full, label)) || own.Any(r => MatchesLabel(icon.Full, r));
            if (other.Any(r => MatchesLabel(icon.Full, r))) return false;
            if (ownLabel && !target.OtherInstall) return true;
            if (icon.Png == null) return false;

            // The display list can guess an unnamed icon by elimination. A Dock action
            // needs positive evidence from the picture of its registered executable.
            var pictureMatches = own.Where(r => r.Snapshot != null).Select(r => TrayNames.Diff(icon.Png, r.Snapshot)).Where(s => s >= 0).ToList();
            if (pictureMatches.Count == 0) return false;
            double best = pictureMatches.Min();
            return best <= 0.21 && !other.Where(r => r.Snapshot != null)
                .Select(r => TrayNames.Diff(icon.Png, r.Snapshot)).Any(s => s >= 0 && s - best < TrayNames.MinGap);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        internal static bool VisibleWindow(Target target) { return FindVisibleWindow(target) != IntPtr.Zero; }

        static IntPtr FindVisibleWindow(Target target)
        {
            IntPtr found = IntPtr.Zero;
            Native.EnumWindows((hwnd, unused) =>
            {
                uint pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                string exe;
                if (!target.Processes.TryGetValue(pid, out exe) || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)
                    || Native.Class(hwnd) == "Ghost") return true;
                long flags = Native.GetLong(hwnd, -20).ToInt64();
                if ((flags & (0x80 | 0x08000000)) != 0 || GetWindow(hwnd, 4) != IntPtr.Zero || GetWindowTextLength(hwnd) == 0) return true;
                int cloaked;
                if ((DwmGetWindowAttribute(hwnd, 14, out cloaked, 4) == 0 && cloaked != 0) || !IsProcess(pid, exe)) return true;
                found = hwnd;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        internal static bool WaitForWindow(Target target, int polls)
        {
            for (int i = 0; i < polls; i++)
            {
                if (VisibleWindow(target)) return true;
                Thread.Sleep(65);
            }
            return VisibleWindow(target);
        }
    }
}
