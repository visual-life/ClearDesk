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
            sb.Append(TaskbarComponent.Describe(Path.Combine(Program.Base, "taskbar"), TaskbarComponent.CacheDir, pack, true));
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
