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
        /// <summary>Why the last TryStart failed (null when it started) and whether trying again later can help.</summary>
        public string Failure { get; private set; }
        public bool Retryable { get; private set; }
        public string Repaired { get; private set; }

        public string Exe
        {
            get
            {
                return Path.Combine(Dir, "TranslucentTB.exe");
            }
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
            Failure = null; Retryable = false; Repaired = null;
            try
            {
                if (process != null && !process.HasExited)
                    return true;
                if (process != null) { process.Dispose(); process = null; }
                if (ExternalRunning())
                {
                    Failure = "已有独立的 TranslucentTB 在运行；请先退出它，再开启此功能，避免改动它的配置。";
                    Retryable = true;
                    return false;
                }
                string note;
                var c = TaskbarComponent.Ensure(Dir, CacheDir ?? TaskbarComponent.CacheDir, out note);
                if (!c.Ok)
                {
                    Failure = note ?? c.Text;
                    Retryable = true;
                    return false;
                }
                Repaired = note;
                process = Process.Start(new ProcessStartInfo(Exe, Args) { UseShellExecute = false, WorkingDirectory = Dir, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                if (process == null) { Failure = "无法启动任务栏组件。"; Retryable = true; return false; }
                return true;
            }
            catch (Exception e)
            {
                Failure = "无法启动任务栏组件：" + e.Message;
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
