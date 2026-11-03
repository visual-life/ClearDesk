using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ClearDesk
{
    /// <summary>2.2.4: ClearDesk.exe --taskbar-diag [file] — prints (and saves) everything about the transparent-taskbar component. Read-only: nothing is repaired, started or stopped.</summary>
    internal static class TaskbarDiag
    {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

        internal static string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("ClearDesk " + SettingsWindow.Version + " —— 透明任务栏组件诊断（只读）  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("程序：" + System.Reflection.Assembly.GetExecutingAssembly().Location);
            sb.AppendLine("数据文件夹：" + Program.Data);
            try
            {
                string run = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run", "ClearDesk", null) as string;
                sb.AppendLine("开机自启动项：" + (run ?? "（没有）"));
                if (!string.IsNullOrEmpty(run))
                {
                    string exe = run.Trim().StartsWith("\"") ? run.Trim().Substring(1, Math.Max(0, run.Trim().IndexOf('"', 1) - 1)) : run.Trim().Split(' ')[0];
                    string baseDir = Path.GetDirectoryName(exe) ?? "";
                    bool same = string.Equals(baseDir.TrimEnd('\\'), Program.Base.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                    sb.AppendLine("  自启动指向的文件夹" + (same ? "与当前运行的相同。" : "★与当前运行的不同：" + baseDir + "（开机时用的是那个文件夹里的 ClearDesk 和 taskbar）"));
                }
            }
            catch (Exception e) { sb.AppendLine("读取自启动项失败：" + e.Message); }
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("ClearDesk"))
                {
                    string path = ""; try { path = p.MainModule.FileName; } catch { path = "（读不到）"; }
                    sb.AppendLine("正在运行的 ClearDesk：PID " + p.Id + "，" + path);
                    p.Dispose();
                }
            }
            catch { }
            sb.AppendLine("选项：透明任务栏=" + (Program.S != null && Program.S.Taskbar) + "；允许未通过校验的组件=" + (Program.S != null && Program.S.AllowUnverifiedTaskbar));
            sb.AppendLine();
            var pack = ComponentPack.Current;
            string dir = Path.Combine(Program.Base, "taskbar");
            sb.Append(TaskbarComponent.Describe(dir, TaskbarComponent.CacheDir, pack, true));
            sb.Append(Extra(dir, pack));
            return sb.ToString();
        }

        /// <summary>2.2.5: running copies (incl. the Store version), who holds each component file open, readability now and over a few seconds,
        /// the signature of the factory copy's TranslucentTB.exe, and the startup timeline of the running ClearDesk.</summary>
        internal static string Extra(string dir, ComponentPack pack)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("—— 2.2.5 补充 ——");
            try
            {
                var copies = TaskbarProcesses.List(dir);
                if (copies.Count == 0) sb.AppendLine("正在运行的 TranslucentTB：无");
                foreach (var c in copies) sb.AppendLine("正在运行的 TranslucentTB：" + TaskbarProcesses.Describe(c));
            }
            catch (Exception e) { sb.AppendLine("列出 TranslucentTB 进程失败：" + e.Message); }
            var names = pack != null && pack.Manifest != null ? pack.Manifest.Select(m => m.Value).ToList() : new System.Collections.Generic.List<string> { "TranslucentTB.exe" };
            foreach (var n in names)
            {
                string f = Path.Combine(dir, n);
                if (!File.Exists(f)) continue;
                var steps = new System.Collections.Generic.List<string>(); string last = null; var t0 = DateTime.Now;
                for (int i = 0; i < 4; i++)
                {
                    if (i > 0) System.Threading.Thread.Sleep(500 * i);
                    last = Authenticode.Probe(f);
                    steps.Add("+" + (DateTime.Now - t0).TotalSeconds.ToString("0.0") + "s " + (last == null ? "可读" : "读不了"));
                    if (last == null) break;
                }
                var holders = FileLockers.Of(f);
                sb.AppendLine(n + "：读取 " + string.Join("，", steps) + (last != null ? "（" + last + "）" : "") + "；占用者：" + (holders.Count == 0 ? "无（或查不到）" : string.Join("；", holders.Select(h => h.ToString()))));
            }
            if (pack != null && pack.HasFiles)
            {
                string tmp = Path.Combine(Path.GetTempPath(), "cd-factory-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                try
                {
                    File.WriteAllBytes(tmp, pack.Get("TranslucentTB.exe"));
                    string signer; string sig = Authenticode.Status(tmp, out signer);
                    sb.AppendLine("出厂副本里的 TranslucentTB.exe：签名 " + sig + (signer.Length > 0 ? "（" + signer + "）" : "") + "，版本 " + TaskbarComponent.FileVer(tmp) + "，大小 " + new FileInfo(tmp).Length + " 字节");
                }
                catch (Exception e) { sb.AppendLine("检查出厂副本签名失败：" + e.Message); }
                finally { try { File.Delete(tmp); } catch { } }
            }
            sb.AppendLine("说明：签名显示“未知（文件读不了…）”时只表示当时打不开文件，不表示没有签名；2.2.4 会把这种情况误报成“无签名”。");
            try
            {
                string tl = TaskbarTimeline.FilePath;
                sb.AppendLine();
                sb.AppendLine(File.Exists(tl) ? File.ReadAllText(tl).TrimEnd() + Environment.NewLine + "（时间线文件：" + tl + "，" + File.GetLastWriteTime(tl).ToString("yyyy-MM-dd HH:mm:ss") + "）" : "启动时间线：没有记录（这台电脑上组件启动时没有遇到“被占用”）");
            }
            catch { }
            return sb.ToString();
        }

        internal static void Run(string[] args)
        {
            try { Program.Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClearDesk"); } catch { }
            try { if (File.Exists(Program.Config)) Program.S = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(Program.Config)); } catch { }
            string text = Report();
            string path = args.Length > 1 ? args[1] : Path.Combine(Program.Base, "taskbar-diag.txt");
            try { File.WriteAllText(path, text, new UTF8Encoding(true)); }
            catch { try { path = Path.Combine(Program.Data, "taskbar-diag.txt"); Directory.CreateDirectory(Program.Data); File.WriteAllText(path, text, new UTF8Encoding(true)); } catch { path = null; } }
            try { AttachConsole(-1); Console.OutputEncoding = Encoding.UTF8; Console.WriteLine(text); if (path != null) Console.WriteLine("已保存：" + path); } catch { }
        }
    }
}
