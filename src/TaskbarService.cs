using System;
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;

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
            Failure = null; Retryable = false; Repaired = null; Code = null; Diag = null;
            try
            {
                if (process != null && !process.HasExited)
                    return true;
                if (process != null) { process.Dispose(); process = null; }
                if (ExternalRunning())
                {
                    Failure = "已有独立的 TranslucentTB 在运行；请先退出它，再开启此功能，避免改动它的配置。";
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
                    try { Diag = TaskbarComponent.Describe(Dir, CacheDir ?? TaskbarComponent.CacheDir, pack, true); } catch (Exception de) { Diag = "（诊断信息收集失败：" + de.Message + "）"; }
                    return false;
                }
                Repaired = note;
                RunFrom = c.Dir ?? Dir;
                if (c.UnverifiedAllowed) Code = "TB-09";
                process = Process.Start(new ProcessStartInfo(Exe, Args) { UseShellExecute = false, WorkingDirectory = RunFrom, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                if (process == null) { Failure = "无法启动任务栏组件。"; Code = "TB-06"; Retryable = true; return false; }
                StartedAt = DateTime.Now;
                return true;
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
