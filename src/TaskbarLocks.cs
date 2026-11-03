using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ClearDesk
{
    /// <summary>2.2.5: who has a file open (Restart Manager, read-only query: nothing is closed or restarted), and which TranslucentTB copies are running.</summary>
    internal static class FileLockers
    {
        [StructLayout(LayoutKind.Sequential)] struct RM_UNIQUE_PROCESS { public int dwProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
            public int ApplicationType, AppStatus, TSSessionId;
            [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
        }
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmStartSession(out uint handle, int flags, StringBuilder key);
        [DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint handle);
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmRegisterResources(uint handle, uint nFiles, string[] files, uint nApps, RM_UNIQUE_PROCESS[] apps, uint nServices, string[] services);
        [DllImport("rstrtmgr.dll")] static extern int RmGetList(uint handle, out uint needed, ref uint count, [In, Out] RM_PROCESS_INFO[] info, ref uint reasons);

        internal sealed class Holder { public int Pid; public string Name = "", Path = "", Service = ""; public override string ToString() { return Name + " (PID " + Pid + (Service.Length > 0 ? "，服务 " + Service : "") + ")" + (Path.Length > 0 ? " " + Path : ""); } }

        /// <summary>Test hook.</summary>
        internal static Func<string, List<Holder>> Provider;

        /// <summary>Processes that have the file open. Empty when none / unknown (another user's or a protected process may not be reported).</summary>
        internal static List<Holder> Of(string path)
        {
            if (Provider != null) return Provider(path);
            var list = new List<Holder>();
            uint h;
            try
            {
                if (RmStartSession(out h, 0, new StringBuilder(64)) != 0) return list;
                try
                {
                    if (RmRegisterResources(h, 1, new[] { path }, 0, null, 0, null) != 0) return list;
                    uint needed, count = 0, reasons = 0;
                    int r = RmGetList(h, out needed, ref count, null, ref reasons);
                    if (r == 234 && needed > 0)       // ERROR_MORE_DATA
                    {
                        var info = new RM_PROCESS_INFO[needed]; count = needed;
                        if (RmGetList(h, out needed, ref count, info, ref reasons) == 0)
                            for (int i = 0; i < count; i++)
                            {
                                var x = new Holder { Pid = info[i].Process.dwProcessId, Name = info[i].strAppName ?? "", Service = info[i].strServiceShortName ?? "" };
                                try { using (var p = Process.GetProcessById(x.Pid)) { if (x.Name.Length == 0) x.Name = p.ProcessName; try { x.Path = p.MainModule.FileName; } catch { } } } catch { }
                                list.Add(x);
                            }
                    }
                }
                finally { RmEndSession(h); }
            }
            catch { }
            return list;
        }
    }

    internal sealed class RunningCopy { public int Pid; public string Path = ""; public bool Store; public bool Own; }

    internal static class TaskbarProcesses
    {
        /// <summary>Test hook.</summary>
        internal static Func<List<RunningCopy>> Provider;

        /// <summary>Every running TranslucentTB with its path. Store version: the path is under WindowsApps (or cannot be read at all).</summary>
        internal static List<RunningCopy> List(string ownDir)
        {
            if (Provider != null) return Mark(Provider(), ownDir);
            var list = new List<RunningCopy>();
            foreach (var p in Process.GetProcessesByName("TranslucentTB"))
            {
                var c = new RunningCopy { Pid = p.Id };
                try { c.Path = p.MainModule.FileName; } catch { c.Path = QueryPath(p.Id) ?? ""; }
                list.Add(c); p.Dispose();
            }
            return Mark(list, ownDir);
        }

        static List<RunningCopy> Mark(List<RunningCopy> list, string ownDir)
        {
            string own = ownDir == null ? null : Path.GetFullPath(ownDir).TrimEnd('\\') + "\\";
            foreach (var c in list)
            {
                c.Store = c.Path.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0;
                c.Own = own != null && c.Path.StartsWith(own, StringComparison.OrdinalIgnoreCase) && c.Path.Substring(own.Length).IndexOf('\\') < 0;
            }
            return list;
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        static string QueryPath(int pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);      // PROCESS_QUERY_LIMITED_INFORMATION (works for Store / elevated processes)
            if (h == IntPtr.Zero) return null;
            try { var sb = new StringBuilder(1024); int n = sb.Capacity; return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null; }
            finally { CloseHandle(h); }
        }

        internal static string Describe(RunningCopy c)
        {
            return "PID " + c.Pid + "，" + (c.Path.Length > 0 ? c.Path : "（路径读不到）") + (c.Own ? "（ClearDesk 自己的组件文件夹）" : c.Store ? "（商店版 TranslucentTB）" : "（其他位置）");
        }
    }

    /// <summary>2.2.5: what happened while the component was busy at startup, kept for --taskbar-diag (also written to Data\taskbar-timeline.txt).</summary>
    internal static class TaskbarTimeline
    {
        static readonly List<string> lines = new List<string>();
        static DateTime start = DateTime.MinValue;
        internal static string FilePath { get { return Path.Combine(Program.Data ?? Path.GetTempPath(), "taskbar-timeline.txt"); } }
        internal static void Begin(string what) { lock (lines) { lines.Clear(); start = DateTime.Now; } Add(what); }
        internal static void Add(string what)
        {
            lock (lines)
            {
                if (start == DateTime.MinValue) start = DateTime.Now;
                lines.Add(DateTime.Now.ToString("HH:mm:ss.fff") + "  +" + ((DateTime.Now - start).TotalSeconds).ToString("0.0") + "s  " + what);
                try { if (!Program.Preview) File.WriteAllText(FilePath, "ClearDesk " + SettingsWindow.Version + " 透明任务栏组件启动时间线（" + start.ToString("yyyy-MM-dd HH:mm:ss") + " 开始）" + Environment.NewLine + string.Join(Environment.NewLine, lines) + Environment.NewLine, new UTF8Encoding(false)); } catch { }
            }
        }
        internal static string Text { get { lock (lines) return string.Join(Environment.NewLine, lines); } }
        internal static int Count { get { lock (lines) return lines.Count; } }
    }
}
