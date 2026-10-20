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

        public string Exe
        {
            get
            {
                return Path.Combine(Program.Base, "taskbar", "TranslucentTB.exe");
            }
        }

        public void Start()
        {
            if (process != null && !process.HasExited)
                return;
            var existing = Native.FindWindow("TrayWindow", "TranslucentTB");
            if (existing != IntPtr.Zero)
                throw new InvalidOperationException("已有独立的 TranslucentTB 在运行；请先退出它，再开启此功能，避免改动它的配置。");
            string manifest = Path.Combine(Program.Base, "taskbar", "verified.sha256");
            if (!File.Exists(manifest))
                throw new IOException("缺少官方任务栏组件的校验清单。请完整解压新版压缩包。");
            foreach (string line in File.ReadAllLines(manifest))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                string expected = line.Substring(0, 64);
                string name = line.Substring(66);
                if (name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
                    throw new IOException("校验清单文件名无效。");
                using (var file = File.OpenRead(Path.Combine(Path.GetDirectoryName(Exe), name)))
                using (var sha = SHA256.Create())
                {
                    string hash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
                    if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("任务栏组件校验失败：" + name);
                }
            }

            process = Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Exe), CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
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
