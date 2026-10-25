using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ClearDesk
{
    // 开机自启动（2.0.9）。登录时由资源管理器读取 HKCU\...\Run 并启动；Windows「启动应用」设置 / 任务管理器 /
    // 安全软件会通过 StartupApproved 把条目标记为已禁用，此时 Run 里仍有值，但不会启动。
    internal enum StartupMode { None, Registry, Folder }

    internal sealed class StartupInfo
    {
        public StartupMode Mode;
        public string Command, Target;
        public bool TargetExists, PointsHere, DisabledByWindows, Exact;
    }

    internal sealed class StartupManager
    {
        internal const string ValueName = "ClearDesk", ShortcutName = "ClearDesk.lnk", Arguments = "--startup";
        internal const string DefaultRun = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal const string DefaultApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        internal const string DefaultApprovedFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

        readonly string exe, runKey, approvedRun, approvedFolder, folder;
        internal string Log = "";

        internal StartupManager(string exePath = null, string run = null, string approvedRunKey = null, string approvedFolderKey = null, string startupFolder = null)
        {
            exe = exePath ?? Path.Combine(Program.Base, "ClearDesk.exe");
            runKey = run ?? DefaultRun; approvedRun = approvedRunKey ?? DefaultApprovedRun; approvedFolder = approvedFolderKey ?? DefaultApprovedFolder;
            folder = startupFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        }

        internal string Command { get { return "\"" + exe + "\" " + Arguments; } }
        string ShortcutPath { get { return Path.Combine(folder, ShortcutName); } }

        // ---------------------------------------------------------------- parsing helpers
        internal static string ParseTarget(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            string text = Environment.ExpandEnvironmentVariables(command.Trim());
            if (text.StartsWith("\"")) { int end = text.IndexOf('"', 1); return end > 1 ? text.Substring(1, end - 1) : text.Substring(1); }
            int exeAt = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeAt > 0) return text.Substring(0, exeAt + 4);   // unquoted path with spaces / Chinese
            int space = text.IndexOf(' ');
            return space > 0 ? text.Substring(0, space) : text;
        }

        // Explorer's "open zip" extraction, e-mail / browser caches and the temp folder disappear later.
        internal static bool IsTemporaryLocation(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p = path.Replace('/', '\\').ToLowerInvariant();
            try { string t = Path.GetTempPath().ToLowerInvariant(); if (p.StartsWith(t)) return true; } catch { }
            return p.Contains("\\appdata\\local\\temp\\") || p.Contains("\\temp1_") || p.Contains(".zip\\") || p.Contains("\\content.outlook\\") || p.Contains("\\inetcache\\") || p.Contains("\\7zo");
        }

        // StartupApproved data: first byte 02/06 = enabled, 03/07 (and any odd value) = disabled.
        internal static bool IsDisabledFlag(byte[] data) { return data != null && data.Length > 0 && (data[0] & 1) == 1; }

        static string Normalize(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd('\\'); } catch { return path ?? ""; }
        }

        // ---------------------------------------------------------------- Mark-of-the-Web
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool DeleteFileW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        internal static bool HasWebMark(string file)
        {
            using (var h = CreateFileW(file + ":Zone.Identifier", 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero)) return !h.IsInvalid;
        }
        internal static void AddWebMark(string file)
        {
            using (var h = CreateFileW(file + ":Zone.Identifier", 0x40000000, 7, IntPtr.Zero, 2, 0x80, IntPtr.Zero))
            using (var s = new FileStream(h, FileAccess.Write)) { var b = System.Text.Encoding.ASCII.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\n"); s.Write(b, 0, b.Length); }
        }
        // Unblock-File for our own install folder: a zip downloaded from GitHub marks every extracted file as
        // "from the Internet", which can make Windows prompt (or silently refuse) when the app is started at logon.
        internal static int Unblock(string directory)
        {
            int cleared = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Take(4000))
                    if (DeleteFileW(file + ":Zone.Identifier")) cleared++;
            }
            catch { }
            return cleared;
        }

        // ---------------------------------------------------------------- state
        byte[] Approval(string key, string name)
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(key)) return k == null ? null : k.GetValue(name) as byte[]; } catch { return null; }
        }

        string ShortcutTarget()
        {
            try
            {
                if (!File.Exists(ShortcutPath)) return null;
                var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                var link = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { ShortcutPath });
                return link.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null) as string;
            }
            catch { return null; }
        }

        internal StartupInfo Query()
        {
            var info = new StartupInfo { Mode = StartupMode.None };
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(runKey))
                {
                    var value = k == null ? null : k.GetValue(ValueName) as string;
                    if (value != null)
                    {
                        info.Mode = StartupMode.Registry; info.Command = value; info.Target = ParseTarget(value);
                        info.DisabledByWindows = IsDisabledFlag(Approval(approvedRun, ValueName));
                    }
                }
            }
            catch { }
            if (info.Mode == StartupMode.None)
            {
                string target = ShortcutTarget();
                if (target != null)
                {
                    info.Mode = StartupMode.Folder; info.Target = target; info.Command = target;
                    info.DisabledByWindows = IsDisabledFlag(Approval(approvedFolder, ShortcutName));
                }
            }
            if (info.Mode != StartupMode.None)
            {
                info.TargetExists = !string.IsNullOrEmpty(info.Target) && File.Exists(info.Target);
                info.PointsHere = Normalize(info.Target).Equals(Normalize(exe), StringComparison.OrdinalIgnoreCase);
                info.Exact = info.Mode == StartupMode.Folder || string.Equals(info.Command, Command, StringComparison.Ordinal);
            }
            return info;
        }

        internal bool Enabled { get { var q = Query(); return q.Mode != StartupMode.None; } }

        // Text for the settings page.
        internal string Describe()
        {
            var q = Query();
            if (q.Mode == StartupMode.None) return IsTemporaryLocation(exe) ? "当前从压缩包或临时目录运行，请先把整个文件夹解压到固定位置再开启。" : "未开启。开启后登录 Windows 时自动启动 ClearDesk。";
            if (q.DisabledByWindows) return "已设置，但被 Windows 禁用（任务管理器 / 设置 → 启动应用，或被安全软件关闭）。请点右侧按钮打开“启动应用”，把 ClearDesk 打开。";
            if (!q.TargetExists) return "已设置，但指向的程序已不存在：" + q.Target + "。重新打开 ClearDesk 会自动修复。";
            if (!q.PointsHere) return "已启用，当前指向：" + q.Target + (q.Mode == StartupMode.Folder ? "（启动文件夹）" : "") + "。";
            return q.Mode == StartupMode.Folder ? "已启用（通过启动文件夹）。" : "已启用。登录 Windows 时会自动启动。";
        }

        // ---------------------------------------------------------------- changes
        void ClearApproval(string key, string name)
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(key, true)) if (k != null) k.DeleteValue(name, false); } catch { }
        }

        bool WriteRun()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(runKey)) k.SetValue(ValueName, Command);
                using (var k = Registry.CurrentUser.OpenSubKey(runKey)) return k != null && string.Equals(k.GetValue(ValueName) as string, Command, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        bool WriteShortcut()
        {
            try
            {
                Directory.CreateDirectory(folder);
                var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                var link = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { ShortcutPath });
                var t = link.GetType();
                t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { exe });
                t.InvokeMember("Arguments", BindingFlags.SetProperty, null, link, new object[] { Arguments });
                t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(exe) });
                t.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "ClearDesk" });
                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
                return File.Exists(ShortcutPath);
            }
            catch { return false; }
        }

        // User turned autostart on. Returns false with a message when it cannot work.
        internal bool Enable(out string message)
        {
            if (IsTemporaryLocation(exe)) { message = "当前是从压缩包或临时目录运行，重启后该位置会消失。请先把整个文件夹解压到固定位置（如 D:\\ClearDesk），再从那里运行并开启。"; return false; }
            if (!File.Exists(exe)) { message = "找不到 ClearDesk.exe。"; return false; }
            Unblock(Path.GetDirectoryName(exe));
            if (WriteRun())
            {
                ClearApproval(approvedRun, ValueName);          // an explicit "on" also lifts an earlier Windows-side disable
                try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); } catch { }
                message = "已开启开机自启动。"; return true;
            }
            // Registry write blocked or reverted (policy / security software): fall back to the Startup folder.
            if (WriteShortcut())
            {
                ClearApproval(approvedFolder, ShortcutName);
                message = "注册表启动项被拦截，已改用“启动”文件夹开启开机自启动。"; return true;
            }
            message = "无法写入启动项（可能被安全软件拦截），请在安全软件里允许 ClearDesk 开机启动。"; return false;
        }

        internal void Disable()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(runKey, true)) if (k != null) k.DeleteValue(ValueName, false); } catch { }
            try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); } catch { }
            ClearApproval(approvedRun, ValueName); ClearApproval(approvedFolder, ShortcutName);
        }

        // At every launch: if the user has autostart on, make sure it points at a working copy of ClearDesk.
        // Fixes folders that were deleted/moved/renamed after an upgrade and entries left on an older version
        // (e.g. 1.9.1) that still exists. Never changes the Windows enable/disable state.
        internal bool Heal()
        {
            Log = "";
            var q = Query();
            if (q.Mode == StartupMode.None) return false;
            if (IsTemporaryLocation(exe) || !File.Exists(exe)) return false;
            bool replace;
            if (q.PointsHere) replace = !q.Exact;
            else if (!q.TargetExists) replace = true;
            else
            {
                try { replace = File.GetLastWriteTimeUtc(exe) > File.GetLastWriteTimeUtc(q.Target); } catch { replace = false; }
            }
            if (!replace) return false;
            Unblock(Path.GetDirectoryName(exe));
            bool ok = q.Mode == StartupMode.Registry ? WriteRun() : WriteShortcut();
            Log = (q.TargetExists ? "更新" : "修复失效的") + "开机自启动：" + (q.Target ?? "?") + " → " + exe + (ok ? "" : "（失败）");
            return ok;
        }
    }

    // Autostart runs as soon as Explorer starts the Run entries; wait (bounded) until the taskbar and desktop windows exist.
    internal static class StartupGate
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        internal static int WaitForShell(int timeoutMs = 30000)
        {
            int waited = 0;
            while (waited < timeoutMs && (FindWindow("Shell_TrayWnd", null) == IntPtr.Zero || FindWindow("Progman", null) == IntPtr.Zero))
            { System.Threading.Thread.Sleep(250); waited += 250; }
            if (waited > 0) System.Threading.Thread.Sleep(1500);
            return waited;
        }
    }
}
