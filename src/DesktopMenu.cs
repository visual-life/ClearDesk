using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace ClearDesk
{
    internal static class WidgetCatalog
    {
        // Order is the menu order everywhere (desktop menu, widget menu, tray).
        public static readonly KeyValuePair<string, string>[] Kinds =
        {
            new KeyValuePair<string, string>("note", "便签"),
            new KeyValuePair<string, string>("box", "桌面收纳格"),
            new KeyValuePair<string, string>("clock", "时钟"),
            new KeyValuePair<string, string>("weather", "天气"),
            new KeyValuePair<string, string>("music", "音乐"),
            new KeyValuePair<string, string>("calendar", "日历"),
            new KeyValuePair<string, string>("almanac", "黄历 / 农历"),
            new KeyValuePair<string, string>("performance", "性能"),
            new KeyValuePair<string, string>("quick", "快捷控制"),
            new KeyValuePair<string, string>("todo", "待办清单"),
            new KeyValuePair<string, string>("countdown", "倒计时 / 番茄钟"),
            new KeyValuePair<string, string>("schedule", "日程"),
            new KeyValuePair<string, string>("clipboard", "剪贴板历史"),
            new KeyValuePair<string, string>("netspeed", "网速流量"),
            new KeyValuePair<string, string>("launcher", "快捷启动"),
            new KeyValuePair<string, string>("album", "图片相册"),
            new KeyValuePair<string, string>("sunair", "日出日落 / 空气质量"),
        };

        public static bool IsKnown(string kind)
        {
            return kind != null && Kinds.Any(k => k.Key == kind);
        }

        // Returns the widget kind requested by a command line, or null.
        public static string Requested(string[] args)
        {
            if (args == null) return null;
            int i = Array.IndexOf(args, "--new-widget");
            if (i < 0 || i + 1 >= args.Length) return null;
            string kind = (args[i + 1] ?? "").Trim().ToLowerInvariant();
            return IsKnown(kind) ? kind : null;
        }
    }

    // Classic desktop-background context menu entry (HKCU, no admin, no shell extension DLL).
    // On Windows 11 it appears under “显示更多选项” (Shift+F10); a top-level Win11 entry
    // needs an IExplorerCommand COM server inside a signed (sparse) package.
    internal static class DesktopMenu
    {
        public const string DefaultRoot = @"Software\Classes\DesktopBackground\Shell\ClearDesk";
        public const string Title = "ClearDesk 新建组件";

        public static string Command(string exe, string kind)
        {
            return "\"" + exe + "\" --new-widget " + kind;
        }

        public static void Sync()
        {
            try
            {
                if (Program.Preview) return;
                if (Program.S.DesktopMenu) Register(Path.Combine(Program.Base, "ClearDesk.exe"));
                else Unregister();
                ModernMenu.SetVisible(Program.S.DesktopMenu && Program.S.ModernMenu);
            }
            catch (Exception e) { Program.Log(e); }
        }

        public static void Register(string exe, string root = DefaultRoot)
        {
            Unregister(root);
            using (var k = Registry.CurrentUser.CreateSubKey(root))
            {
                k.SetValue("MUIVerb", Title);
                k.SetValue("Icon", "\"" + exe + "\",0");
                k.SetValue("SubCommands", "");
            }
            int i = 1;
            foreach (var kind in WidgetCatalog.Kinds)
            {
                string key = root + @"\shell\" + (i++).ToString("00") + kind.Key;
                using (var k = Registry.CurrentUser.CreateSubKey(key))
                {
                    k.SetValue("MUIVerb", kind.Value);
                    k.SetValue("Icon", "\"" + exe + "\",0");
                    if (kind.Key == "box") k.SetValue("CommandFlags", 0x40, RegistryValueKind.DWord); // ECF_SEPARATORAFTER
                }
                using (var c = Registry.CurrentUser.CreateSubKey(key + @"\command"))
                    c.SetValue("", Command(exe, kind.Key));
            }
        }

        public static void Unregister(string root = DefaultRoot)
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, false);
        }

        public static bool IsRegistered(string root = DefaultRoot)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(root))
                return k != null;
        }
    }

    // Windows 11 top-level entry: sparse package (ClearDeskShell.msix, external location =
    // the ClearDesk folder) + IExplorerCommand COM DLL. Installing needs the signing
    // certificate in LocalMachine\TrustedPeople once (one UAC prompt); see enable-menu.ps1.
    internal static class ModernMenu
    {
        public const string PackageName = "ClearDesk.ShellMenu";

        public static void SetVisible(bool visible)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\ClearDesk"))
                k.SetValue("ModernMenu", visible ? 1 : 0, RegistryValueKind.DWord);
        }

        public static bool Available
        {
            get { return Environment.OSVersion.Version.Build >= 22000 && new[] { "ClearDeskShell.msix", "ClearDeskShell.dll", "ClearDeskSign.exe", "enable-menu.ps1", "disable-menu.ps1" }.All(f => File.Exists(Path.Combine(Program.Base, f))); }
        }

        // Returns the external location of the registered package, "" if registered without one, or null.
        public static string InstalledLocation()
        {
            try
            {
                var pm = new Windows.Management.Deployment.PackageManager();
                foreach (var p in pm.FindPackagesForUser(string.Empty))
                {
                    try
                    {
                        if (p.Id.Name == PackageName)
                        {
                            try { return p.EffectiveExternalLocation != null ? p.EffectiveExternalLocation.Path : ""; } catch { return ""; }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception e) { Program.Log(e); }
            return null;
        }

        public enum State { Off, On, Moved }

        public static State Status(string location)
        {
            if (location == null) return State.Off;
            return SamePath(location, Program.Base) ? State.On : State.Moved;
        }

        public static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        public static string StatusText(State s)
        {
            switch (s)
            {
                case State.On: return "已启用：桌面右键第一层直接显示“ClearDesk 新建组件”。";
                case State.Moved: return "已启用，但 ClearDesk 文件夹已移动；正在自动重新注册，失败时请点“一键启用”。";
                default: return "未启用，使用「显示更多选项」中的菜单。";
            }
        }

        static string StatusFile { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClearDesk", "shell", "menu-status.txt"); } }

        // Runs enable-menu.ps1 / disable-menu.ps1 hidden and returns (exit code, message).
        public static System.Threading.Tasks.Task<KeyValuePair<int, string>> Run(bool enable, bool repair = false)
        {
            return System.Threading.Tasks.Task.Run(delegate
            {
                string script = Path.Combine(Program.Base, enable ? "enable-menu.ps1" : "disable-menu.ps1");
                try { File.Delete(StatusFile); } catch { }
                var info = new System.Diagnostics.ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\"" + (repair ? " -Repair" : ""))
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Program.Base
                };
                int code = -1;
                using (var process = System.Diagnostics.Process.Start(info))
                {
                    if (!process.WaitForExit(5 * 60 * 1000)) { try { process.Kill(); } catch { } return new KeyValuePair<int, string>(-1, "超时"); }
                    code = process.ExitCode;
                }
                string message = "";
                try
                {
                    var lines = File.ReadAllText(StatusFile, Encoding.UTF8).Split(new[] { '\n' }, 2);
                    message = lines.Length > 1 ? lines[1].Trim() : "";
                }
                catch { }
                return new KeyValuePair<int, string>(code, message);
            });
        }

        // Folder moved since registration: re-register silently (certificate is already trusted, no UAC).
        public static void RepairIfMoved()
        {
            if (!Available || Program.Preview) return;
            System.Threading.Tasks.Task.Run(async delegate
            {
                try
                {
                    if (Status(InstalledLocation()) != State.Moved) return;
                    var r = await Run(true, true);
                    Program.Log(new Exception("ModernMenu repair: " + r.Key + " " + r.Value));
                }
                catch (Exception e) { Program.Log(e); }
            });
        }
    }

    // Single-instance forwarding: a second launch hands its arguments to the running copy.
    internal static class InstancePipe
    {
        static string Name { get { return "ClearDesk.ipc." + Environment.UserName; } }
        static Thread thread;

        public static bool Send(string[] args)
        {
            try
            {
                using (var c = new NamedPipeClientStream(".", Name, PipeDirection.Out))
                {
                    c.Connect(1500);
                    var bytes = Encoding.UTF8.GetBytes(string.Join("\u001f", args ?? new string[0]) + "\n");
                    c.Write(bytes, 0, bytes.Length);
                    c.Flush();
                }
                return true;
            }
            catch { return false; }
        }

        public static void Listen(Action<string[]> handler)
        {
            if (thread != null) return;
            thread = new Thread(delegate ()
            {
                while (true)
                {
                    try
                    {
                        using (var s = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                        {
                            s.WaitForConnection();
                            string line;
                            using (var r = new StreamReader(s, Encoding.UTF8)) line = r.ReadLine() ?? "";
                            if (line.Length > 4096) continue;
                            var args = line.Split('\u001f');
                            if (Program.App != null) Program.App.Dispatcher.BeginInvoke(new Action(delegate { handler(args); }));
                        }
                    }
                    catch (Exception e) { Program.Log(e); Thread.Sleep(1000); }
                }
            }) { IsBackground = true, Name = "ClearDesk instance pipe" };
            thread.Start();
        }
    }
}
