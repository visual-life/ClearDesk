using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace ClearDesk
{
    // 2.0.9: login autostart — parsing, StartupApproved, stale paths, upgrades, zip/temp, Startup-folder fallback, MOTW.
    internal static class V209Tests
    {
        const string Scratch = @"Software\ClearDeskTest209";
        static string run = Scratch + @"\Run", appRun = Scratch + @"\ApprovedRun", appFolder = Scratch + @"\ApprovedFolder";

        static void Approve(string key, string name, byte first)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(key)) k.SetValue(name, new byte[] { first, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, RegistryValueKind.Binary);
        }
        static string RunValue() { using (var k = Registry.CurrentUser.OpenSubKey(run)) return k == null ? null : k.GetValue("ClearDesk") as string; }
        static void SetRun(string v) { using (var k = Registry.CurrentUser.CreateSubKey(run)) k.SetValue("ClearDesk", v); }
        static string Fake(string root, string folder, DateTime stamp)
        {
            string dir = Path.Combine(root, folder); Directory.CreateDirectory(dir);
            string exe = Path.Combine(dir, "ClearDesk.exe"); File.WriteAllText(exe, "x"); File.SetLastWriteTimeUtc(exe, stamp); return exe;
        }
        static StartupManager Manager(string exe, string folder, string runKey = null) { return new StartupManager(exe, runKey ?? run, appRun, appFolder, folder); }
        static System.Collections.Generic.IEnumerable<T> Find<T>(DependencyObject o) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
            {
                var c = VisualTreeHelper.GetChild(o, i); if (c is T) yield return (T)c;
                foreach (var d in Find<T>(c)) yield return d;
            }
        }

        public static async Task Run(Action<bool, string> check)
        {
            // ---- parsing
            check(StartupManager.ParseTarget("\"D:\\新建 文件夹\\ClearDesk 2.0.9\\ClearDesk.exe\" --startup") == "D:\\新建 文件夹\\ClearDesk 2.0.9\\ClearDesk.exe", "2.0.9: quoted path with spaces and Chinese parses");
            check(StartupManager.ParseTarget("D:\\my apps\\ClearDesk.exe --startup") == "D:\\my apps\\ClearDesk.exe", "unquoted path with spaces parses up to .exe");
            check(StartupManager.ParseTarget("%SystemRoot%\\notepad.exe /x").EndsWith("\\notepad.exe", StringComparison.OrdinalIgnoreCase) && !StartupManager.ParseTarget("%SystemRoot%\\notepad.exe").Contains("%"), "environment variables are expanded");
            check(StartupManager.ParseTarget("") == null, "empty command parses to null");
            check(StartupManager.IsTemporaryLocation(@"C:\Users\a\AppData\Local\Temp\Temp1_ClearDesk-2.0.8.zip\ClearDesk-2.0.8\ClearDesk.exe") && StartupManager.IsTemporaryLocation(@"C:\x\Foo.zip\ClearDesk.exe") && StartupManager.IsTemporaryLocation(Path.Combine(Path.GetTempPath(), "7zO1234", "ClearDesk.exe")), "zip / temp extraction locations are recognised");
            check(!StartupManager.IsTemporaryLocation(@"D:\ClearDesk-2.0.9\ClearDesk.exe") && !StartupManager.IsTemporaryLocation(@"C:\Users\a\Downloads\ClearDesk\ClearDesk.exe"), "normal install folders are not temporary");
            check(!StartupManager.IsDisabledFlag(new byte[] { 2, 0, 0, 0 }) && !StartupManager.IsDisabledFlag(new byte[] { 6, 0 }) && StartupManager.IsDisabledFlag(new byte[] { 3, 0 }) && StartupManager.IsDisabledFlag(new byte[] { 7, 0 }) && StartupManager.IsDisabledFlag(new byte[] { 1, 0 }) && !StartupManager.IsDisabledFlag(null), "StartupApproved: 02/06 enabled, odd values disabled, missing = enabled");

            string root = Path.Combine(Program.Base, "preview-data", "startup-test"); string startupDir = Path.Combine(root, "StartupFolder");
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(Scratch, false); if (Directory.Exists(root)) Directory.Delete(root, true);
                Directory.CreateDirectory(startupDir);
                DateTime now = DateTime.UtcNow;
                string here = Fake(root, "新建 文件夹\\ClearDesk-2.0.9", now);

                // ---- enable / disable
                var m = Manager(here, startupDir);
                check(!m.Enabled && m.Describe().Contains("未开启"), "autostart starts off");
                string message; bool ok = m.Enable(out message);
                var q = m.Query();
                check(ok && q.Mode == StartupMode.Registry && q.PointsHere && q.Exact && q.TargetExists && !q.DisabledByWindows && RunValue() == "\"" + here + "\" --startup", "enable writes a quoted absolute path with --startup (" + RunValue() + ")");
                check(m.Describe().StartsWith("已启用"), "settings text says 已启用");

                // ---- disabled by Windows
                Approve(appRun, "ClearDesk", 3);
                q = m.Query();
                check(q.DisabledByWindows && m.Describe().Contains("被 Windows 禁用"), "StartupApproved 03 is reported as disabled by Windows");
                check(!m.Heal() && m.Query().DisabledByWindows, "self-heal never flips the Windows disabled state");
                ok = m.Enable(out message);
                check(ok && !m.Query().DisabledByWindows, "turning autostart on explicitly lifts the Windows-side disable");

                // ---- stale / upgraded / moved
                SetRun("\"" + Path.Combine(root, "gone", "ClearDesk.exe") + "\" --startup");
                check(!m.Query().TargetExists && m.Describe().Contains("已不存在"), "entry pointing to a deleted folder is detected");
                check(m.Heal() && m.Query().PointsHere && m.Query().TargetExists && m.Log.Contains("修复"), "stale path is repaired to the running copy on launch");
                string old = Fake(root, "ClearDesk-1.9.1", now.AddDays(-20));
                SetRun("\"" + old + "\" --startup");
                check(m.Heal() && m.Query().PointsHere, "entry left on an older install (e.g. 1.9.1) moves to the newer running copy");
                string newer = Fake(root, "ClearDesk-9.9.9", now.AddDays(20));
                SetRun("\"" + newer + "\" --startup");
                check(!m.Heal() && !m.Query().PointsHere, "an entry pointing to a newer install is left alone");
                SetRun("\"" + here + "\"");
                check(m.Heal() && RunValue().EndsWith("--startup"), "missing --startup argument is added (otherwise settings pop up at every login)");
                SetRun(here + " --startup");
                check(m.Heal() && RunValue() == m.Command, "unquoted legacy value is rewritten with quotes");
                check(!m.Heal(), "a correct entry is left untouched");
                m.Disable();
                check(!m.Enabled && RunValue() == null && !m.Heal(), "turned-off autostart is never re-enabled by self-heal");

                // ---- running from a zip / temp folder
                string tempExe = Fake(Path.GetTempPath(), "cleardesk-v209-" + Guid.NewGuid().ToString("N"), now);
                try
                {
                    var t = Manager(tempExe, startupDir);
                    check(!t.Enable(out message) && message.Contains("解压") && RunValue() == null, "enable is refused from a temp / zip folder with a clear message");
                    SetRun("\"" + old + "\" --startup");
                    check(!t.Heal() && RunValue().Contains("1.9.1"), "self-heal does not point the entry at a temp folder");
                    check(t.Describe() != null, "temp location produces a status text");
                }
                finally { try { Directory.Delete(Path.GetDirectoryName(tempExe), true); } catch { } }
                m.Disable();

                // ---- Startup-folder fallback when the Run key cannot be written
                var f = Manager(here, startupDir, new string('x', 400));
                ok = f.Enable(out message);
                q = f.Query();
                check(ok && q.Mode == StartupMode.Folder && q.PointsHere && File.Exists(Path.Combine(startupDir, "ClearDesk.lnk")) && message.Contains("启动”文件夹"), "blocked Run key falls back to a Startup-folder shortcut");
                Approve(appFolder, "ClearDesk.lnk", 3);
                check(f.Query().DisabledByWindows, "Startup-folder shortcut honours StartupApproved\\StartupFolder");
                f.Disable();
                check(!File.Exists(Path.Combine(startupDir, "ClearDesk.lnk")) && f.Query().Mode == StartupMode.None, "disable removes the shortcut");

                // ---- Mark-of-the-Web from a downloaded zip
                string web = Fake(root, "web\\ClearDesk", now); string dll = Path.Combine(Path.GetDirectoryName(web), "ClearDeskShell.dll"); File.WriteAllText(dll, "x");
                StartupManager.AddWebMark(web); StartupManager.AddWebMark(dll);
                check(StartupManager.HasWebMark(web) && StartupManager.HasWebMark(dll), "test files carry a downloaded-from-Internet mark");
                var w = Manager(web, startupDir);
                check(w.Enable(out message) && !StartupManager.HasWebMark(web) && !StartupManager.HasWebMark(dll), "enabling autostart clears the Internet mark on the install folder");
                w.Disable();
            }
            finally
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(Scratch, false); } catch { }
                try { Directory.Delete(root, true); } catch { }
            }

            // ---- login timing
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int waited = StartupGate.WaitForShell(3000);
            check(waited == 0 && sw.ElapsedMilliseconds < 1000, "login gate does not delay when the desktop shell is already up");

            // ---- settings page
            var window = new SettingsWindow(delegate { throw new Exception("autostart must not change in preview"); });
            try
            {
                window.Show(); await Task.Delay(200); window.UpdateLayout();
                check(Find<Button>(window).Any(b => System.Windows.Automation.AutomationProperties.GetName(b) == "打开 Windows 启动应用设置"), "settings has a button that opens Windows 启动应用设置");
                check(Find<TextBlock>(window).Any(t => t.Text.Contains("预览模式")), "settings shows the autostart status line");
            }
            finally { window.Close(); }
        }
    }
}
