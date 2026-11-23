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
    /// <summary>Window operations the Dock needs, behind an interface so the restore logic can be tested without real foreign windows.</summary>
    internal interface IWindowOps
    {
        bool IsWindow(IntPtr h);
        bool IsVisible(IntPtr h);
        bool IsIconic(IntPtr h);
        bool IsResponsive(IntPtr h);
        bool SysCommand(IntPtr h, int command);
        bool ShowAsync(IntPtr h, int cmd);
        bool Foreground(IntPtr h);
    }

    /// <summary>Uses standard window commands on running, taskbar-visible windows. Run this outside the Dock dispatcher.</summary>
    internal static class DockRestorer
    {
        internal const int SC_MINIMIZE = 0xF020, SC_RESTORE = 0xF120;
        internal enum Result { Restored, Failed, Gone, Hidden, NotResponding }

        internal static Result Restore(IntPtr w, IWindowOps ops, Action<int> sleep, List<string> log)
        {
            Action<string> L = s => { if (log != null) log.Add(s); };
            if (!ops.IsWindow(w)) { L("window is gone"); return Result.Gone; }
            if (!ops.IsVisible(w)) { L("hidden: leave activation to the application"); return Result.Hidden; }
            if (!ops.IsResponsive(w)) { L("window did not respond"); return Result.NotResponding; }
            if (ops.IsIconic(w))
            {
                ops.SysCommand(w, SC_RESTORE); L("minimized → SC_RESTORE");
                for (int i = 0; i < 5 && ops.IsWindow(w) && ops.IsVisible(w) && ops.IsIconic(w); i++) sleep(60);
                if (!ops.IsWindow(w)) return Result.Gone;
                if (!ops.IsVisible(w)) return Result.Hidden;
                if (!ops.IsResponsive(w)) return Result.NotResponding;
                if (ops.IsIconic(w))
                {
                    ops.ShowAsync(w, 9); L("still minimized → ShowWindowAsync(SW_RESTORE)");
                    for (int i = 0; i < 5 && ops.IsWindow(w) && ops.IsVisible(w) && ops.IsIconic(w); i++) sleep(60);
                }
            }
            if (!ops.IsWindow(w)) return Result.Gone;
            if (!ops.IsVisible(w)) return Result.Hidden;
            if (ops.IsIconic(w)) return Result.Failed;
            bool focused = ops.Foreground(w);
            L(focused ? "foreground requested" : "Windows did not grant foreground activation");
            return focused ? Result.Restored : Result.Failed;
        }

        /// <summary>Minimizes through WM_SYSCOMMAND (works on elevated windows), falling back to ShowWindow.</summary>
        internal static bool Minimize(IntPtr w, IWindowOps ops)
        {
            if (!ops.IsWindow(w) || !ops.IsVisible(w) || !ops.IsResponsive(w)) return false;
            if (ops.SysCommand(w, SC_MINIMIZE)) return true;
            return ops.ShowAsync(w, 6);
        }

        /// <summary>A hidden top-level window that looks like an application's main window (what a tray app leaves behind).</summary>
        internal static bool IsMainWindowCandidate(long style, long exStyle, IntPtr owner, bool visible, int titleLength, int width, int height)
        {
            if (visible || owner != IntPtr.Zero || titleLength == 0) return false;
            if ((style & 0xC00000) != 0xC00000) return false;                       // needs a caption (title bar): excludes IME, message and helper windows
            if ((exStyle & 0x80) != 0 || (exStyle & 0x08000000) != 0) return false;  // tool window / no-activate
            return width >= 120 && height >= 80;
        }

        // Ancestors that say nothing about which app a window belongs to.
        static readonly string[] NeutralParents = { "explorer.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "conhost.exe", "svchost.exe", "services.exe", "wininit.exe", "winlogon.exe", "wt.exe", "windowsterminal.exe", "cleardesk.exe", "sihost.exe", "taskhostw.exe", "runtimebroker.exe", "applicationframehost.exe" };
        /// <summary>A pinned launcher (RiotClientServices.exe) owns the window of a process it started (LeagueClientUx.exe) when it is one of its ancestors.</summary>
        internal static bool AncestorMatch(string pinExe, IEnumerable<string> ancestorExes)
        {
            if (string.IsNullOrEmpty(pinExe) || ancestorExes == null) return false;
            string name = Path.GetFileName(pinExe).ToLowerInvariant();
            if (NeutralParents.Contains(name)) return false;
            return ancestorExes.Any(a => string.Equals(a, pinExe, StringComparison.OrdinalIgnoreCase));
        }
    }

    internal sealed class WinOps : IWindowOps
    {
        internal static readonly WinOps Instance = new WinOps();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint command);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SendMessageTimeout(IntPtr h, uint message, IntPtr w, IntPtr l, uint flags, uint ms, out IntPtr result);
        [DllImport("kernel32.dll")] static extern void SetLastError(uint error);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        public bool IsWindow(IntPtr h) { return Native.IsWindow(h); }
        public bool IsVisible(IntPtr h) { return Native.IsWindowVisible(h); }
        public bool IsIconic(IntPtr h) { return Native.IsIconic(h); }
        public bool SysCommand(IntPtr h, int command) { return Native.PostMessage(h, 0x112, (IntPtr)command, IntPtr.Zero); }
        public bool ShowAsync(IntPtr h, int cmd) { return Native.IsWindowVisible(h) && ShowWindowAsync(h, cmd); }
        public bool IsResponsive(IntPtr h)
        {
            if (!Native.IsWindow(h) || IsHungAppWindow(h) || Native.Class(h) == "Ghost") return false;
            uint pid;
            uint thread = Native.GetWindowThreadProcessId(h, out pid);
            if (thread == 0) return false;
            if (thread == GetCurrentThreadId()) return true;
            const uint WM_NULL = 0, SMTO_BLOCK = 1, SMTO_ABORTIFHUNG = 2, SMTO_ERRORONEXIT = 0x20;
            IntPtr result;
            SetLastError(0);
            var sent = SendMessageTimeout(h, WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_BLOCK | SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT, 175, out result);
            // An elevated window may decline the probe. Its asynchronous window commands can still work.
            return sent != IntPtr.Zero || Marshal.GetLastWin32Error() == 5;
        }

        public bool Foreground(IntPtr h)
        {
            if (!IsResponsive(h)) return false;
            if (!IsWindowEnabled(h))
            {
                const uint GW_ENABLEDPOPUP = 6;
                IntPtr dialog = GetWindow(h, GW_ENABLEDPOPUP);
                uint appPid, dialogPid;
                Native.GetWindowThreadProcessId(h, out appPid);
                Native.GetWindowThreadProcessId(dialog, out dialogPid);
                if (dialog != IntPtr.Zero && dialog != h && appPid != 0 && appPid == dialogPid
                    && Native.IsWindowVisible(dialog) && IsWindowEnabled(dialog) && IsResponsive(dialog))
                    h = dialog;
            }
            return Native.IsWindowVisible(h) && SetForegroundWindow(h);
        }
    }

    /// <summary>Process parents (Toolhelp snapshot works for elevated processes too, unlike NtQueryInformationProcess).</summary>
    internal static class ProcessTree
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ENTRY { public uint size, usage, pid; public IntPtr heap; public uint module, threads, parent; public int pri; public uint flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string exe; }
        [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32First(IntPtr s, ref ENTRY e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32Next(IntPtr s, ref ENTRY e);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder path, ref int length);
        [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);

        static Dictionary<uint, uint> Parents()
        {
            var map = new Dictionary<uint, uint>();
            IntPtr snap = CreateToolhelp32Snapshot(2, 0);
            if (snap == IntPtr.Zero || snap == (IntPtr)(-1)) return map;
            try { var e = new ENTRY { size = (uint)Marshal.SizeOf(typeof(ENTRY)) }; if (Process32First(snap, ref e)) do map[e.pid] = e.parent; while (Process32Next(snap, ref e)); }
            finally { CloseHandle(snap); }
            return map;
        }
        static long CreateTime(uint pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid); if (h == IntPtr.Zero) return 0;
            try { long c, x, k, u; return GetProcessTimes(h, out c, out x, out k, out u) ? c : 0; } finally { CloseHandle(h); }
        }
        internal static string ExeOf(uint pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid); if (h == IntPtr.Zero) return null;
            try { var sb = new StringBuilder(1024); int n = sb.Capacity; return QueryFullProcessImageName(h, 0, sb, ref n) ? AppIdentity.Normalize(sb.ToString()) : null; } finally { CloseHandle(h); }
        }
        /// <summary>Executable paths of up to <paramref name="depth"/> ancestors (a parent that started after its child is a recycled PID and stops the walk).</summary>
        internal static List<string> AncestorExes(uint pid, int depth)
        {
            var result = new List<string>();
            try
            {
                var map = Parents(); uint cur = pid; long childStart = CreateTime(pid);
                for (int i = 0; i < depth; i++)
                {
                    uint parent; if (!map.TryGetValue(cur, out parent) || parent == 0 || parent == cur) break;
                    long start = CreateTime(parent); if (start == 0 || (childStart != 0 && start > childStart)) break;
                    string exe = ExeOf(parent); if (exe != null) result.Add(exe);
                    cur = parent; childStart = start;
                }
            }
            catch { }
            return result;
        }
    }

    /// <summary>ClearDesk.exe --dock-diag [file] : lists the windows the Dock sees (or skips) and whether each belongs to an elevated process. Changes nothing.</summary>
    internal static class DockDiag
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint c);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int s);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint a, bool i, uint pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint a, out IntPtr t);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr t, int c, out int info, int len, out int ret);
        internal static string Elevation(uint pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid); if (h == IntPtr.Zero) return "无法打开";
            try
            {
                IntPtr t; if (!OpenProcessToken(h, 8, out t)) return "管理员（令牌被拒绝）";
                try { int v, r; return GetTokenInformation(t, 20, out v, 4, out r) ? (v != 0 ? "管理员" : "普通") : "未知"; } finally { CloseHandle(t); }
            }
            finally { CloseHandle(h); }
        }

        /// <summary>ClearDesk.exe --dock-activate exe-fragment [file] [minimize-after] : runs the Dock's own restore code on one running app (used to verify the fix on real apps).</summary>
        internal static void Activate(string[] args)
        {
            string fragment = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            string path = args.Length > 2 ? args[2] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dock-activate.txt");
            bool minimizeAfter = args.Skip(3).Any(a => a == "minimize-after");
            var o = new StringBuilder();
            using (var apps = new RunningApps())
            {
                apps.Refresh();
                var app = apps.Apps.FirstOrDefault(a => (a.Identity.Exe ?? "").ToLowerInvariant().Contains(fragment) || (a.Identity.AppId ?? "").ToLowerInvariant().Contains(fragment));
                IntPtr w = IntPtr.Zero; string how = "visible window";
                if (app != null) w = app.Preferred;
                else { var hidden = apps.HiddenWindowsFor(new AppIdentity { Exe = null, AppId = null }, fragment); if (hidden.Count > 0) { w = hidden[0]; how = "hidden (tray) window"; } }
                if (w == IntPtr.Zero) { o.AppendLine("没有找到包含 \"" + fragment + "\" 的应用窗口。"); File.WriteAllText(path, o.ToString(), new UTF8Encoding(false)); return; }
                uint pid; Native.GetWindowThreadProcessId(w, out pid);
                o.AppendLine(string.Format("{0}  hwnd=0x{1:X}  {2}  [{3}]  最小化={4}  前台=0x{5:X}", how, w.ToInt64(), Path.GetFileName(ProcessTree.ExeOf(pid) ?? "?"), Elevation(pid), Native.IsIconic(w), Native.GetForegroundWindow().ToInt64()));
                var log = new List<string>();
                var result = DockRestorer.Restore(w, WinOps.Instance, Thread.Sleep, log);
                o.AppendLine("结果：" + result + "；步骤：" + string.Join(" → ", log));
                o.AppendLine(string.Format("之后：最小化={0}  可见={1}  前台=0x{2:X}（目标窗口 0x{3:X}）", Native.IsIconic(w), Native.IsWindowVisible(w), Native.GetForegroundWindow().ToInt64(), w.ToInt64()));
                if (minimizeAfter)
                {
                    bool m = DockRestorer.Minimize(w, WinOps.Instance); Thread.Sleep(800);
                    o.AppendLine("恢复原状（最小化）：" + m + "  最小化=" + Native.IsIconic(w));
                }
            }
            File.WriteAllText(path, o.ToString(), new UTF8Encoding(false));
        }
        internal static void Run(string[] args)
        {
            string path = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dock-diag.txt");
            var o = new StringBuilder();
            o.AppendLine("ClearDesk " + SettingsWindow.Version + " Dock 窗口诊断  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            o.AppendLine("本程序权限：" + Elevation((uint)Process.GetCurrentProcess().Id) + "。普通权限的 ClearDesk 不能对管理员权限的应用调用 ShowWindow，2.2.0 起改用 WM_SYSCOMMAND（可跨权限）。\n");
            Native.EnumWindows(delegate (IntPtr h, IntPtr unused)
            {
                try
                {
                    var t = new StringBuilder(256); GetWindowText(h, t, 256);
                    bool vis = Native.IsWindowVisible(h); long style = Native.GetLong(h, -16).ToInt64(), ex = Native.GetLong(h, -20).ToInt64();
                    IntPtr owner = GetWindow(h, 4);
                    bool main = vis ? (ex & 0x80) == 0 && (ex & 0x08000000) == 0 && (owner == IntPtr.Zero || (ex & 0x40000) != 0) : DockRestorer.IsMainWindowCandidate(style, ex, owner, false, t.Length, 200, 200);
                    if (t.Length == 0 || !main) return true;
                    uint pid; Native.GetWindowThreadProcessId(h, out pid); int cloaked; DwmGetWindowAttribute(h, 14, out cloaked, 4);
                    string exe = ProcessTree.ExeOf(pid) ?? "(读不到路径)";
                    o.AppendLine(string.Format("{0}  [{1}] {2}  可见={3} 最小化={4} 被隐藏(cloaked)={5}  PID {6}  {7}", Path.GetFileName(exe), Elevation(pid), t, vis, Native.IsIconic(h), cloaked != 0, pid, vis ? "" : "← 隐藏窗口（托盘类应用）"));
                    if (vis) o.AppendLine("    祖先进程：" + string.Join(" ← ", ProcessTree.AncestorExes(pid, 4).Select(Path.GetFileName)));
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            // 2.2.3: what the icon lookup returns for every app the Dock shows
            o.AppendLine("\n图标解析（每个正在运行的应用，逐级来源；→ 为最终采用）：");
            try
            {
                using (var apps = new RunningApps())
                {
                    apps.Refresh();
                    foreach (var app in apps.Apps)
                    {
                        var i = app.Identity;
                        o.AppendLine(string.Format("  {0}  exe={1}  真实exe={2}  AppId={3}  启动路径={4}", i.Name, i.Exe, i.RealExe ?? "-", i.AppId ?? "-", i.LaunchPath ?? "-"));
                        var log = new List<string>(); var request = new DockEntry { Identity = i, Running = app }.IconRequest();
                        var icon = AppIcons.Trace(request, log, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), "dock-diag-icons"), i.Name);
                        foreach (var line in log) o.AppendLine(line);
                        o.AppendLine(icon == null ? "    结果：全部失败 → 显示首字母图标，之后会重试" : "    结果：已找到图标");
                    }
                }
            }
            catch (Exception e) { o.AppendLine("图标解析失败：" + e.Message); }
            File.WriteAllText(path, o.ToString(), new UTF8Encoding(false));
        }
    }
}
