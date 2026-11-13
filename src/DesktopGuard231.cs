using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Threading;

namespace ClearDesk
{
    /// <summary>2.3.1: the desktop icons are never left hidden by ClearDesk.
    /// Root cause of "桌面消失": a session remembered the icons' state ONCE, when its crash guard started (RestoreState.IconsVisible, taken from the live window). If another
    /// ClearDesk (an older version, a second copy) had hidden the icons at that moment, "hidden" was stored as the state to go back to; the next double-click showed them and
    /// closing ClearDesk (or the crash guard) put them back to "hidden". From 2.3.1 the state to go back to is never read from the live window: it is always "visible",
    /// except when the user turned the icons off themselves in Windows (桌面右键 → 查看 → 显示桌面图标, HKCU\...\Explorer\Advanced\HideIcons = 1).</summary>
    internal static class DesktopIcons
    {
        internal static Func<IntPtr> Handle = Native.Icons;
        internal static Func<IntPtr, bool> IsVisible = Native.IsWindowVisible;
        internal static Action<IntPtr, bool> Show = delegate (IntPtr h, bool v) { Native.ShowWindow(h, v ? 5 : 0); };
        internal static Func<bool> WindowsHides = ReadWindowsHides;

        static bool ReadWindowsHides()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
                {
                    var v = k == null ? null : k.GetValue("HideIcons");
                    return v is int && (int)v == 1;
                }
            }
            catch { return false; }
        }

        /// <summary>The state the icons are put back to: visible, unless Windows' own switch has them off.</summary>
        internal static bool WantedVisible { get { return !WindowsHides(); } }

        /// <summary>Pure rule: show the icons now?</summary>
        internal static bool ShouldShow(bool windowsHides, bool currentlyVisible) { return !windowsHides && !currentlyVisible; }

        /// <summary>Shows the icons when something else (another ClearDesk, a crashed session, an old guard) left them hidden and the user did not turn them off in Windows.</summary>
        internal static bool Heal(string why)
        {
            try
            {
                var h = Handle();
                if (h == IntPtr.Zero || !ShouldShow(WindowsHides(), IsVisible(h))) return false;
                Show(h, true);
                Program.LogText("桌面图标被隐藏着（" + why + "），而 Windows 的“显示桌面图标”是开启的：已恢复显示。");
                return true;
            }
            catch (Exception e) { Program.Log(e); return false; }
        }

        /// <summary>Turns Windows' own "显示桌面图标" back on (what 桌面右键 → 查看 → 显示桌面图标 does: the same command, sent to the desktop's SHELLDLL_DefView; no keystrokes).
        /// 2.3.5: ClearDesk shows and hides the icons window itself, Windows keeps its own switch (HideIcons); when the two disagree and the switch is turned off while ClearDesk
        /// is running, it stayed "off" after ClearDesk closed: the desktop was empty.</summary>
        internal static Action SwitchOn = TurnOnWindowsIcons;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr FindWindowW(string c, string t);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr p, IntPtr a, string c, string t);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);

        static IntPtr DefView()
        {
            IntPtr dv = FindWindowExW(FindWindowW("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
            if (dv != IntPtr.Zero) return dv;
            IntPtr w = IntPtr.Zero;
            while ((w = FindWindowExW(IntPtr.Zero, w, "WorkerW", null)) != IntPtr.Zero)
            {
                dv = FindWindowExW(w, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (dv != IntPtr.Zero) return dv;
            }
            return IntPtr.Zero;
        }

        static void TurnOnWindowsIcons()
        {
            var dv = DefView();
            if (dv == IntPtr.Zero || !WindowsHides()) return;
            PostMessage(dv, 0x111, (IntPtr)0x7402, IntPtr.Zero);      // WM_COMMAND: toggle desktop icons (hidden → shown)
            for (int i = 0; i < 25 && WindowsHides(); i++) System.Threading.Thread.Sleep(100);
        }

        static DesktopIcons()
        {
            // tests only: a file stands in for the desktop ("visible=1;hides=0"), so the crash guard (a separate process) can be tested without touching the real desktop
            var box = Environment.GetEnvironmentVariable("CLEARDESK_ICONS_SANDBOX");
            if (string.IsNullOrEmpty(box)) return;
            Func<string> read = () => { try { return File.ReadAllText(box); } catch { return "visible=1;hides=0"; } };
            Handle = () => (IntPtr)1;
            IsVisible = h => read().Contains("visible=1");
            Show = (h, v) => File.WriteAllText(box, "visible=" + (v ? 1 : 0) + ";hides=" + (read().Contains("hides=1") ? 1 : 0));
            WindowsHides = () => read().Contains("hides=1");
            SwitchOn = () => File.WriteAllText(box, "visible=1;hides=0");
        }

        /// <summary>Puts back what a session changed. <paramref name="startedHidden"/>: Windows' switch was already off when the session began (the user's own choice: it stays off).
        /// Otherwise the icons are visible afterwards, and if Windows' switch was turned off in the meantime it is turned back on. null = look at the switch now (old behaviour).</summary>
        internal static void Restore(bool? startedHidden = null)
        {
            try
            {
                bool keepOff = startedHidden ?? WindowsHides();
                if (!keepOff && WindowsHides())
                {
                    Program.LogText("桌面图标：Windows 的“显示桌面图标”在 ClearDesk 运行期间被关掉了（和 ClearDesk 自己的显示/隐藏不同步）：关闭 ClearDesk 时已重新打开。");
                    SwitchOn();
                }
                var h = Handle(); if (h != IntPtr.Zero) Show(h, !keepOff);
            }
            catch (Exception e) { Program.Log(e); }
        }

        // The state file: written when a session first shows/hides the icons, removed when the session has put them back (normal exit or crash guard).
        // If it is still there at the next start, neither happened (power loss, a killed guard): see HealAfterLastSession.
        static string MarkerPath
        {
            get
            {
                var box = Environment.GetEnvironmentVariable("CLEARDESK_ICONS_SANDBOX");      // tests only
                return Path.Combine(string.IsNullOrEmpty(box) ? Program.Data : Path.GetDirectoryName(box), "desktop-icons-state.json");
            }
        }
        internal static void MarkTouched(bool startedHidden)
        {
            try { Directory.CreateDirectory(Program.Data); File.WriteAllText(MarkerPath, new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "StartedHidden", startedHidden }, { "Touched", true } })); } catch { }
        }
        internal static void ClearMarker() { try { if (File.Exists(MarkerPath)) File.Delete(MarkerPath); } catch { } }

        /// <summary>At start: the last session changed the icons and never put them back, and Windows' switch is off now although it was on when that session began → turn it on.</summary>
        internal static bool HealAfterLastSession()
        {
            try
            {
                if (!File.Exists(MarkerPath)) return false;
                var m = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(MarkerPath));
                ClearMarker();
                bool touched = m != null && m.ContainsKey("Touched") && Equals(m["Touched"], true);
                bool startedHidden = m != null && m.ContainsKey("StartedHidden") && Equals(m["StartedHidden"], true);
                if (!touched || startedHidden || !WindowsHides()) return false;
                Program.LogText("桌面图标：上一次 ClearDesk 改动过桌面图标，却没有来得及恢复，而 Windows 的“显示桌面图标”现在是关闭的：已重新打开。");
                SwitchOn();
                var h = Handle(); if (h != IntPtr.Zero) Show(h, true);
                return true;
            }
            catch (Exception e) { Program.Log(e); return false; }
        }
    }

    internal sealed class ProcInfo
    {
        public int Pid, Session;
        public string Path = "", CommandLine = "";
        public override string ToString() { return Pid + " " + Path; }
    }

    /// <summary>2.3.1: finds other running ClearDesk programs of ANY version (the single-instance mutex is not enough: builds before 1.8.2 do not have it, and a program
    /// that runs with other rights cannot see it) and lets the user close them. Helper processes (--guard crash guards, updaters, diagnostics) and test instances are not
    /// programs and are never counted or touched.</summary>
    internal static class OtherInstances
    {
        internal static Func<List<ProcInfo>> List = WmiList;
        internal static Func<ProcInfo, bool> Kill = KillProcess;
        static readonly string[] Helper = { "--guard", "--apply-update", "--update-test-server", "--dock-activate", "--mixer-diag", "--battery-diag", "--taskbar-diag", "--dock-diag", "--weather-diag", "--flash-helper", "--tray-helper", "--self-test", "--uninstall" };
        static readonly string[] Test = { "--preview", "--popup-benchmark", "--perf231", "--render", "--benchmark", "--integration-test", "--regression-test", "--lyrics-test", "--weather-test", "--update-e2e", "--client-lyrics-test" };

        internal static bool Has(string cmd, string[] flags)
        {
            var parts = (cmd ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Any(p => flags.Contains(p.Trim('"'), StringComparer.OrdinalIgnoreCase));
        }
        internal static bool IsHelper(string cmd) { return Has(cmd, Helper); }
        internal static bool IsTest(string cmd) { return Has(cmd, Test); }

        static List<ProcInfo> WmiList()
        {
            var r = new List<ProcInfo>();
            try
            {
                using (var s = new System.Management.ManagementObjectSearcher("SELECT ProcessId, ExecutablePath, CommandLine, SessionId FROM Win32_Process WHERE Name = 'ClearDesk.exe'"))
                using (var c = s.Get())
                    foreach (System.Management.ManagementObject o in c)
                        using (o)
                            r.Add(new ProcInfo { Pid = Convert.ToInt32(o["ProcessId"]), Path = (o["ExecutablePath"] as string) ?? "", CommandLine = (o["CommandLine"] as string) ?? "", Session = Convert.ToInt32(o["SessionId"] ?? 0) });
            }
            catch (Exception e) { Program.Log(e); }
            return r;
        }

        /// <summary>Other ClearDesk programs in this Windows session (not me, not helpers, not tests). <paramref name="scope"/>: only programs started from this folder (tests).</summary>
        internal static List<ProcInfo> Find(string scope = null)
        {
            int me = Process.GetCurrentProcess().Id, session = Process.GetCurrentProcess().SessionId;
            return List().Where(p => p.Pid != me && p.Session == session && !IsHelper(p.CommandLine) && !IsTest(p.CommandLine)
                && (scope == null || (p.Path ?? "").StartsWith(scope, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        /// <summary>The crash guards that belong to the given programs.</summary>
        internal static List<ProcInfo> GuardsOf(IEnumerable<int> pids, string scope = null)
        {
            var set = new HashSet<string>(pids.Select(x => x.ToString()));
            return List().Where(p => IsHelper(p.CommandLine) && p.CommandLine.IndexOf("--guard", StringComparison.OrdinalIgnoreCase) >= 0
                && (p.CommandLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).SkipWhile(x => !x.Trim('"').Equals("--guard", StringComparison.OrdinalIgnoreCase)).Skip(1).Take(1).Any(x => set.Contains(x.Trim('"'))))
                && (scope == null || (p.Path ?? "").StartsWith(scope, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        static bool KillProcess(ProcInfo p)
        {
            try { using (var x = Process.GetProcessById(p.Pid)) { x.Kill(); return x.WaitForExit(4000); } }
            catch (ArgumentException) { return true; }         // already gone
            catch { return false; }
        }

        internal static string Version(string path)
        {
            try { return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : "?"; } catch { return "?"; }
        }

        internal static string Describe(IEnumerable<ProcInfo> list)
        {
            return string.Join("\n", list.Select(p => "  版本 " + Version(p.Path) + "　" + (string.IsNullOrEmpty(p.Path) ? "（路径未知，可能是管理员权限运行）" : p.Path)));
        }

        /// <summary>Closes the programs, waits for their crash guards (an older guard puts the icons back to what IT remembered, which is what hid them) and then makes the icons visible.
        /// Returns the ones that could not be closed.</summary>
        internal static List<ProcInfo> Close(List<ProcInfo> list, string scope = null, int guardWaitMs = 6000)
        {
            var failed = list.Where(p => !Kill(p)).ToList();
            var done = list.Where(p => !failed.Contains(p)).Select(p => p.Pid).ToList();
            var until = DateTime.UtcNow.AddMilliseconds(guardWaitMs);
            while (done.Count > 0 && DateTime.UtcNow < until && GuardsOf(done, scope).Count > 0) System.Threading.Thread.Sleep(150);
            if (done.Count > 0) DesktopIcons.Heal("另一个 ClearDesk 被关闭之后");
            return failed;
        }

        internal static string Question(List<ProcInfo> others, bool running)
        {
            return (running ? "ClearDesk 发现电脑上还有另一个 ClearDesk 在运行：\n" : "电脑上已经有另一个 ClearDesk 在运行：\n") + Describe(others)
                + "\n\n两个 ClearDesk 同时运行会互相抢着管理桌面图标、任务栏和组件，可能导致桌面图标消失。\n\n要关闭旧的那个吗？\n“是”：关闭它" + (running ? "，继续使用当前这个" : "，然后启动这个") + "\n“否”：" + (running ? "先不管，保持现状" : "不启动这个，继续用旧的");
        }

        // ---- while running: an older copy that was started after us (the mutex of builds before 1.8.2 does not stop it)
        static DispatcherTimer timer;
        static readonly HashSet<int> asked = new HashSet<int>();
        static bool busy;
        internal static void Watch()
        {
            if (timer != null || Program.Preview) return;
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(20) };
            timer.Tick += delegate
            {
                if (busy) return; busy = true;
                System.Threading.Tasks.Task.Run(() => Find()).ContinueWith(t =>
                {
                    try
                    {
                        if (t.IsFaulted) { busy = false; return; }
                        var fresh = t.Result.Where(p => !asked.Contains(p.Pid)).ToList();
                        if (fresh.Count == 0) { busy = false; return; }
                        foreach (var p in fresh) asked.Add(p.Pid);
                        Program.App.Dispatcher.BeginInvoke(new Action(delegate
                        {
                            try
                            {
                                Program.LogText("运行中发现另一个 ClearDesk：" + string.Join("；", fresh.Select(p => p.ToString())));
                                if (MessageBox.Show(Question(fresh, true), "ClearDesk", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes) == MessageBoxResult.Yes)
                                {
                                    var failed = Close(fresh);
                                    if (failed.Count > 0) MessageBox.Show("没能关闭：\n" + Describe(failed) + "\n\n可能是它以管理员权限运行。请在任务管理器里结束 ClearDesk.exe。", "ClearDesk");
                                }
                            }
                            catch (Exception e) { Program.Log(e); }
                            finally { busy = false; }
                        }));
                    }
                    catch (Exception e) { Program.Log(e); busy = false; }
                });
            };
            timer.Start();
        }
        internal static void StopWatch() { if (timer != null) { timer.Stop(); timer = null; } }
    }
}
