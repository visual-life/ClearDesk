using System;
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Linq;

namespace ClearDesk
{
    internal sealed class TaskbarService : IDisposable
    {
        Process process;
        public int ProcessId
        {
            get
            {
                return process == null ? 0 : process.Id;
            }
        }

        /// <summary>Folder of the component; tests point it at a scratch copy.</summary>
        internal string Dir = Path.Combine(Program.Base, "taskbar");
        internal string Args = "";
        /// <summary>Test hook: is a stand-alone TranslucentTB already running?</summary>
        internal Func<bool> ExternalRunning = delegate { return Native.FindWindow("TrayWindow", "TranslucentTB") != IntPtr.Zero; };
        internal string CacheDir = null;
        /// <summary>2.2.4: where the factory copy comes from (default: compiled into ClearDesk.exe; null = this build/test has none, the folder's verified.sha256 decides).</summary>
        internal Func<ComponentPack> PackSource = delegate { return ComponentPack.Current; };
        /// <summary>2.2.4: per-user copy of the factory copy, used when the install folder cannot be repaired.</summary>
        internal string RunDirectory = null;
        /// <summary>2.2.4: user option "allow unverified TranslucentTB" (same version + valid signature only).</summary>
        internal Func<bool> AllowUnverified = delegate { return Program.S != null && Program.S.AllowUnverifiedTaskbar; };
        /// <summary>Why the last TryStart failed (null when it started) and whether trying again later can help.</summary>
        public string Failure { get; private set; }
        public bool Retryable { get; private set; }
        public string Repaired { get; private set; }
        /// <summary>2.2.4: short code of the last failure (TB-01 … TB-09) and the per-file facts, ready for errors.log.</summary>
        public string Code { get; private set; }
        public string Diag { get; private set; }
        /// <summary>The folder the running component was started from (Dir, or the per-user copy).</summary>
        public string RunFrom { get; private set; }
        public DateTime StartedAt { get; private set; }

        public string Exe
        {
            get
            {
                return Path.Combine(RunFrom ?? Dir, "TranslucentTB.exe");
            }
        }

        /// <summary>Exit code of the component once it has ended (-1 when unknown / still running).</summary>
        public int ExitCode
        {
            get { try { return process != null && process.HasExited ? process.ExitCode : -1; } catch { return -1; } }
        }

        public bool Exited
        {
            get
            {
                try { return process != null && process.HasExited; } catch { return true; }
            }
        }

        /// <summary>Starts the component. Never throws: a missing, locked, replaced or damaged component is repaired from the kept good copy,
        /// otherwise Failure explains it and ClearDesk simply carries on without the transparent taskbar.</summary>
        public bool TryStart()
        {
            Failure = null; Retryable = false; Repaired = null; Code = null; Diag = null; Busy = false; BusyFile = null; Adopted = false;
            try
            {
                if (process != null && !process.HasExited)
                    return true;
                if (process != null) { process.Dispose(); process = null; }
                // 2.2.5: a TranslucentTB that already runs from ClearDesk's own component folder (or the per-user copy) is ClearDesk's component, started earlier
                // (its own "start with Windows" option, a previous ClearDesk). It holds TranslucentTB.exe open, so the file may not be readable: that is normal, not damage.
                var copies = Running();
                var own = copies.FirstOrDefault(c => c.Own || IsRunCopy(c.Path));
                if (own != null)
                {
                    try
                    {
                        process = Process.GetProcessById(own.Pid);
                        RunFrom = Path.GetDirectoryName(own.Path);
                        StartedAt = DateTime.Now.AddMinutes(-1);       // not "just started": an exit is not reported as an early crash (TB-08)
                        Adopted = true;
                        Repaired = "TranslucentTB 已经在运行（" + TaskbarProcesses.Describe(own) + "），视为正常，不校验也不修复正在使用的文件。";
                        return true;
                    }
                    catch { process = null; }      // it just ended: continue normally
                }
                if (ExternalRunning())
                {
                    string where = copies.Count == 0 ? "" : "（" + string.Join("；", copies.Select(TaskbarProcesses.Describe)) + "）";
                    Failure = "已有独立的 TranslucentTB 在运行" + where + "；请先退出它，再开启此功能，避免改动它的配置。";
                    Code = "TB-07";
                    Retryable = true;
                    return false;
                }
                string note;
                var pack = PackSource();
                var c = TaskbarComponent.Ensure(Dir, CacheDir ?? TaskbarComponent.CacheDir, pack, AllowUnverified(), RunDirectory ?? TaskbarComponent.RunDir, out note);
                if (!c.Ok)
                {
                    Failure = note ?? c.Text;
                    Code = c.Code;
                    Retryable = true;
                    Busy = c.State == ComponentState.Busy;
                    BusyFile = Busy ? c.File : null;
                    try { Diag = TaskbarComponent.Describe(Dir, CacheDir ?? TaskbarComponent.CacheDir, pack, true); } catch (Exception de) { Diag = "（诊断信息收集失败：" + de.Message + "）"; }
                    return false;
                }
                Repaired = note;
                return Launch(c.Dir ?? Dir, c.UnverifiedAllowed);
            }
            catch (Exception e)
            {
                Failure = "无法启动任务栏组件：" + e.Message;
                Code = "TB-06";
                Retryable = true;
                try { if (process != null) { process.Dispose(); } } catch { }
                process = null;
                return false;
            }
        }

        bool Launch(string from, bool unverified)
        {
            RunFrom = from;
            if (unverified) Code = "TB-09";
            process = Process.Start(new ProcessStartInfo(Exe, Args) { UseShellExecute = false, WorkingDirectory = RunFrom, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            if (process == null) { Failure = "无法启动任务栏组件。"; Code = "TB-06"; Retryable = true; return false; }
            StartedAt = DateTime.Now;
            return true;
        }

        /// <summary>2.2.5: the last TryStart failed only because a file could not be read (TB-04, in use / being scanned).</summary>
        public bool Busy { get; private set; }
        public string BusyFile { get; private set; }
        /// <summary>2.2.5: TryStart found ClearDesk's own TranslucentTB already running and took it over instead of starting another.</summary>
        public bool Adopted { get; private set; }
        /// <summary>Test hook / default: running TranslucentTB copies.</summary>
        internal Func<List<RunningCopy>> RunningCopies;
        List<RunningCopy> Running() { try { return RunningCopies != null ? RunningCopies() : TaskbarProcesses.List(Dir); } catch { return new List<RunningCopy>(); } }
        bool IsRunCopy(string path)
        {
            try { string run = Path.GetFullPath(RunDirectory ?? TaskbarComponent.RunDir).TrimEnd('\\'); return string.Equals(Path.GetDirectoryName(path), run, StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }

        /// <summary>
        /// 2.2.5: the install folder stayed busy through all retries. Put a verified copy into the per-user folder (%LOCALAPPDATA%\ClearDesk\taskbar-run) from the factory
        /// copy or the kept good copy — the busy file itself is never touched — and start from there. Returns false (with Failure / Code) when that is not possible.
        /// </summary>
        public bool TryStartRunCopy()
        {
            Failure = null; Code = null; Repaired = null;
            string run = RunDirectory ?? TaskbarComponent.RunDir, cache = CacheDir ?? TaskbarComponent.CacheDir;
            try
            {
                if (process != null && !process.HasExited) return true;
                var pack = PackSource();
                string how;
                if (pack != null && pack.SelfCheck()) { TaskbarComponent.ExtractMissingOrWrong(pack, run); how = "出厂副本"; }
                else if (TaskbarComponent.Verify(cache, pack).Ok) { TaskbarComponent.CopyTree(cache, run, true); how = "完好备份"; }
                else { Failure = "组件文件一直被占用，而且没有可用的出厂副本（taskbar-factory.zip）或完好备份，无法改用备用目录。"; Code = "TB-04"; Retryable = true; return false; }
                var v = TaskbarComponent.Verify(run, pack);
                if (!v.Ok) { Failure = "备用目录 " + run + " 校验未通过：" + v.Text; Code = v.Code; Retryable = true; return false; }
                Repaired = "安装文件夹里的组件一直被占用，已从" + how + "释放到 " + run + " 并从那里启动（没有改动被占用的文件）。";
                return Launch(run, false);
            }
            catch (Exception e) { Failure = "改用备用目录失败：" + e.Message; Code = "TB-04"; Retryable = true; return false; }
        }

        public void Start()
        {
            if (!TryStart())
                throw new InvalidOperationException(Failure);
        }

        public void Dispose()
        {
            if (process == null)
                return;
            CloseOwned(process.Id, Exe);
            process.Dispose();
            process = null;
        }

        public static void CloseOwned(int pid, string expectedPath)
        {
            if (pid <= 0)
                return;
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    if (!string.Equals(p.MainModule.FileName, expectedPath, StringComparison.OrdinalIgnoreCase))
                        return;
                    var window = Native.FindWindow("TrayWindow", "TranslucentTB");
                    uint actual;
                    if (window != IntPtr.Zero)
                    {
                        Native.GetWindowThreadProcessId(window, out actual);
                        if (actual == (uint)pid)
                            Native.PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero);
                    }
                }
            }
            catch
            {
            }
        }
    }
}
