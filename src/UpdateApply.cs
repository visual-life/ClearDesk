using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClearDesk
{
    internal sealed class ApplyOptions
    {
        public string Stage = "", Target = "", Data = "", FromVersion = "", ToVersion = "";
        public int Pid;
        public List<string> RestartArgs = new List<string>();
        public bool Silent, NoRestart;
        public int WaitMs = 20000;
        public Action<string> Status;
        public Action RestoreTaskbar;          // hands the top taskbar back (TopTaskbar.Restore) when a crashed session left it hidden
        public Func<string, bool> FailFor;     // test hook: simulate a failure while swapping the named file
    }

    internal sealed class ApplyResult
    {
        public bool Ok, RolledBack, Restarted;
        public string Message = "";
        public string From = "", To = "", Time = "";
    }

    public sealed class JournalEntry { public string Kind = "", Dest = "", Old = ""; }     // replace | add | remove
    public sealed class UpdateJournal { public string From = "", To = "", State = "swapping"; public List<JournalEntry> Entries = new List<JournalEntry>(); }

    // Applies a staged update to an unzip-and-run install folder. Runs from a temporary copy of ClearDesk.exe (the real exe is
    // locked while it runs) after the old process, its --guard watchdog and the TranslucentTB helper have exited.
    // Every replaced / removed file is first renamed to "<name>.cdold-*" (renaming works even for DLLs loaded elsewhere);
    // a journal makes the swap rollback-able, also after a crash.
    internal static class UpdateApplier
    {
        internal static readonly string[] Preserve = { "update-source.json", "startup-error.txt" };
        internal static bool IsPreserved(string rel)
        {
            string r = rel.Replace('\\', '/');
            return Preserve.Any(p => r.Equals(p, StringComparison.OrdinalIgnoreCase)) || r.StartsWith("preview-data/", StringComparison.OrdinalIgnoreCase) || r.IndexOf(".cdold", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static string UpdateDir(string data) { return Path.Combine(data, "update"); }
        internal static string JournalPath(string data) { return Path.Combine(UpdateDir(data), "journal.json"); }
        internal static string ResultPath(string data) { return Path.Combine(data, "update-result.json"); }

        // Can this process create files in the folder? (Program Files and similar need elevation.)
        internal static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".cd-write-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                File.WriteAllText(probe, "x"); File.Delete(probe); return true;
            }
            catch { return false; }
        }

        internal static string QuoteArg(string a) { return a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\""; }

        // Which command-line switches survive a restart (never the "create this widget" ones).
        internal static List<string> RestartArgsFrom(string[] args)
        {
            var keep = new List<string>();
            foreach (var a in args ?? new string[0])
                if (a == "--startup" || a == "--preview" || a.StartsWith("--update-e2e", StringComparison.Ordinal)) keep.Add(a);
            return keep;
        }

        static List<Process> RunningFrom(string target, int selfId)
        {
            var list = new List<Process>(); string prefix = target.TrimEnd('\\') + "\\";
            foreach (var name in new[] { "ClearDesk", "TranslucentTB" })
                foreach (var p in Process.GetProcessesByName(name))
                {
                    bool mine = false;
                    try { mine = p.Id != selfId && p.MainModule.FileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase); } catch { }
                    if (mine) list.Add(p); else p.Dispose();
                }
            return list;
        }

        // Waits for ClearDesk (pid), its guard and TranslucentTB to leave the install folder; the guard restores icons / taskbar by itself.
        internal static void WaitForShutdown(ApplyOptions o)
        {
            var end = DateTime.UtcNow.AddMilliseconds(o.WaitMs);
            if (o.Pid > 0) { try { using (var p = Process.GetProcessById(o.Pid)) p.WaitForExit(o.WaitMs); } catch { } }
            int self = Process.GetCurrentProcess().Id;
            while (true)
            {
                var running = RunningFrom(o.Target, self);
                if (running.Count == 0) break;
                if (DateTime.UtcNow > end)
                {
                    foreach (var p in running) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
                    foreach (var p in running) p.Dispose();
                    break;
                }
                foreach (var p in running) p.Dispose();
                Thread.Sleep(200);
            }
            // The watchdog normally deletes its recovery file after restoring; if a crash left the top taskbar hidden, do it here.
            try
            {
                string recovery = Path.Combine(o.Data, "recovery-v11.json");
                if (File.Exists(recovery))
                {
                    var r = new JavaScriptSerializer().Deserialize<RestoreState>(File.ReadAllText(recovery));
                    if (r != null && r.TopTaskbarHidden && o.RestoreTaskbar != null) o.RestoreTaskbar();
                    File.Delete(recovery);
                }
            }
            catch { }
        }

        static void WriteJournal(string data, UpdateJournal j)
        {
            Directory.CreateDirectory(UpdateDir(data));
            string tmp = JournalPath(data) + ".tmp";
            File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(j));
            if (File.Exists(JournalPath(data))) File.Delete(JournalPath(data));
            File.Move(tmp, JournalPath(data));
        }

        // Idempotent: restores every file from its ".cdold-*" copy and removes files this update added. Returns errors.
        internal static List<string> Rollback(UpdateJournal j)
        {
            var errors = new List<string>();
            foreach (var e in Enumerable.Reverse(j.Entries))
            {
                try
                {
                    if (e.Kind == "add") { if (File.Exists(e.Dest)) File.Delete(e.Dest); }
                    else if (File.Exists(e.Old))
                    {
                        if (File.Exists(e.Dest)) File.Delete(e.Dest);
                        File.Move(e.Old, e.Dest);
                    }
                }
                catch (Exception ex) { errors.Add(Path.GetFileName(e.Dest) + "：" + ex.Message); }
            }
            return errors;
        }

        // Called at every start: a crash / power loss in the middle of a swap is undone.
        internal static bool RecoverInterrupted(string data, bool log = true)
        {
            string path = JournalPath(data);
            if (!File.Exists(path)) return false;
            bool created;
            using (var m = new Mutex(true, "Local\\ClearDesk.Updater." + Environment.UserName, out created))
            {
                if (!created) return false;            // an updater is running right now
                try
                {
                    var j = new JavaScriptSerializer().Deserialize<UpdateJournal>(File.ReadAllText(path));
                    var errors = j == null ? new List<string>() : Rollback(j);
                    if (errors.Count == 0) File.Delete(path);
                    if (log) Program.LogText("检测到上次更新未完成，已恢复旧文件" + (errors.Count > 0 ? "（部分失败：" + string.Join("；", errors) + "）" : "。"));
                    return true;
                }
                catch (Exception e) { Program.Log(e); return false; }
                finally { m.ReleaseMutex(); }
            }
        }

        // Removes ".cdold-*" leftovers (files that were locked when the update tried to delete them) and stale helper copies.
        internal static int CleanupLeftovers(string target)
        {
            int n = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(target, "*.cdold-*", SearchOption.AllDirectories).Take(500)) { try { File.Delete(f); n++; } catch { } }
                foreach (var d in Directory.GetDirectories(Path.GetTempPath(), "ClearDeskUpdate-*"))
                    try { if ((DateTime.UtcNow - Directory.GetLastWriteTimeUtc(d)).TotalHours > 6) { Directory.Delete(d, true); n++; } } catch { }
            }
            catch { }
            return n;
        }

        internal static ApplyResult Apply(ApplyOptions o)
        {
            var res = new ApplyResult { From = o.FromVersion, To = o.ToVersion, Time = DateTime.Now.ToString("s") };
            Action<string> say = s => { if (o.Status != null) { try { o.Status(s); } catch { } } };
            UpdateJournal journal = null; var olds = new List<string>(); bool swapped = false;
            bool created; var guard = new Mutex(true, "Local\\ClearDesk.Updater." + Environment.UserName, out created);
            try
            {
                if (!created) { if (!guard.WaitOne(60000)) throw new IOException("另一个更新程序正在运行。"); }
                say("正在校验更新文件…");
                var pkg = new StagedPackage { Dir = o.Stage };
                foreach (var f in Directory.EnumerateFiles(o.Stage, "*", SearchOption.AllDirectories)) pkg.Files.Add(f.Substring(o.Stage.TrimEnd('\\').Length + 1).Replace('\\', '/'));
                UpdatePackage.Verify(pkg);
                say("正在等待 ClearDesk 退出…");
                WaitForShutdown(o);
                if (!CanWrite(o.Target)) throw new UnauthorizedAccessException("没有权限写入安装文件夹（" + o.Target + "）。");

                // ---- plan
                var oldSums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try { string s = Path.Combine(o.Target, "SHA256SUMS.txt"); if (File.Exists(s)) oldSums = UpdatePackage.ParseSums(File.ReadAllText(s, Encoding.UTF8)); } catch { }
                var plan = new List<JournalEntry>(); string stamp = Guid.NewGuid().ToString("N").Substring(0, 8); int idx = 0;
                Func<string, string> oldName = d => d + ".cdold-" + stamp + (idx++).ToString();
                var mid = pkg.Files.Where(f => !IsPreserved(f) && !f.Equals("ClearDesk.exe", StringComparison.OrdinalIgnoreCase) && !f.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.Ordinal).ToList();
                var tail = new[] { "ClearDesk.exe", "SHA256SUMS.txt" };
                Action<string> planFile = rel =>
                {
                    string dest = Path.Combine(o.Target, rel.Replace('/', '\\')), src = Path.Combine(o.Stage, rel.Replace('/', '\\'));
                    if (Directory.Exists(dest)) throw new IOException("目标位置是文件夹：" + rel);
                    if (File.Exists(dest))
                    {
                        string want = rel.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase) ? UpdateCrypto.Sha256Hex(src) : pkg.Sums[rel];
                        if (new FileInfo(dest).Length == new FileInfo(src).Length && UpdateCrypto.Sha256Hex(dest) == want) return;    // unchanged
                        plan.Add(new JournalEntry { Kind = "replace", Dest = dest, Old = oldName(dest) });
                    }
                    else plan.Add(new JournalEntry { Kind = "add", Dest = dest });
                };
                foreach (var rel in mid) planFile(rel);
                foreach (var kv in oldSums)
                {
                    if (IsPreserved(kv.Key) || pkg.Files.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)) continue;
                    string dest = Path.Combine(o.Target, kv.Key.Replace('/', '\\'));
                    if (File.Exists(dest) && UpdateCrypto.Sha256Hex(dest) == kv.Value) plan.Add(new JournalEntry { Kind = "remove", Dest = dest, Old = oldName(dest) });   // only untouched files of the old release
                }
                foreach (var rel in tail) planFile(rel);       // ClearDesk.exe and the checksum list go last

                // ---- backup (kept until the next update) + journal
                say("正在备份旧版本…");
                string backupRoot = Path.Combine(UpdateDir(o.Data), "backup");
                string backup = Path.Combine(backupRoot, string.IsNullOrEmpty(o.FromVersion) ? "previous" : o.FromVersion);
                try
                {
                    if (Directory.Exists(backupRoot)) foreach (var d in Directory.GetDirectories(backupRoot)) { try { Directory.Delete(d, true); } catch { } }
                    foreach (var e in plan.Where(x => x.Kind != "add"))
                    {
                        string rel = e.Dest.Substring(o.Target.TrimEnd('\\').Length + 1), to = Path.Combine(backup, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(to)); File.Copy(e.Dest, to, true);
                    }
                }
                catch (Exception e) { Program.LogText("更新备份未完整：" + e.Message); }
                journal = new UpdateJournal { From = o.FromVersion, To = o.ToVersion, Entries = plan };
                WriteJournal(o.Data, journal);

                // ---- swap
                say("正在安装新版本…");
                swapped = true;
                foreach (var e in plan)
                {
                    string rel = e.Dest.Substring(o.Target.TrimEnd('\\').Length + 1);
                    if (o.FailFor != null && o.FailFor(rel)) throw new IOException("模拟失败：" + rel);
                    if (e.Kind == "remove") { File.Move(e.Dest, e.Old); olds.Add(e.Old); continue; }
                    if (e.Kind == "replace") { File.Move(e.Dest, e.Old); olds.Add(e.Old); }
                    Directory.CreateDirectory(Path.GetDirectoryName(e.Dest));
                    string src = Path.Combine(o.Stage, rel);
                    File.Copy(src, e.Dest, false);
                }
                foreach (var e in plan.Where(x => x.Kind != "remove"))
                {
                    string rel = e.Dest.Substring(o.Target.TrimEnd('\\').Length + 1);
                    string want = rel.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase) ? UpdateCrypto.Sha256Hex(Path.Combine(o.Stage, rel)) : pkg.Sums[rel.Replace('\\', '/')];
                    if (UpdateCrypto.Sha256Hex(e.Dest) != want) throw new IOException("写入后校验失败：" + rel);
                }

                // ---- restart and watch for an immediate crash of the new version
                if (!o.NoRestart)
                {
                    say("正在重新启动 ClearDesk…");
                    var p = StartApp(o);
                    res.Restarted = p != null;
                    if (p != null && p.WaitForExit(4000) && p.ExitCode != 0) throw new IOException("新版本启动后立即退出（代码 " + p.ExitCode + "）。");
                }
                res.Ok = true; res.Message = "已更新到 " + o.ToVersion + "。";
                foreach (var f in olds) { try { File.Delete(f); } catch { } }
                try { File.Delete(JournalPath(o.Data)); } catch { }
            }
            catch (Exception ex)
            {
                res.Ok = false; res.Message = ex.Message;
                if (journal != null && swapped)
                {
                    say("更新失败，正在恢复旧版本…");
                    var errors = Rollback(journal);
                    res.RolledBack = errors.Count == 0;
                    if (errors.Count == 0) { try { File.Delete(JournalPath(o.Data)); } catch { } }
                    res.Message = "更新失败：" + ex.Message + (errors.Count == 0 ? " 已自动恢复为原来的版本，没有丢失任何设置。" : " 恢复旧文件时出现问题：" + string.Join("；", errors));
                }
                else res.Message = "更新失败：" + ex.Message + " 安装文件夹没有被改动。";
                if (!o.NoRestart) { try { res.Restarted = StartApp(o) != null; } catch { res.Restarted = false; } }   // always bring ClearDesk back (old files are restored)
            }
            finally
            {
                try { if (Directory.Exists(o.Stage)) Directory.Delete(o.Stage, true); } catch { }
                try { guard.ReleaseMutex(); } catch { }
                guard.Dispose();
            }
            try
            {
                Directory.CreateDirectory(o.Data);
                File.WriteAllText(ResultPath(o.Data), new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "ok", res.Ok }, { "rolledBack", res.RolledBack }, { "from", res.From }, { "to", res.To }, { "message", res.Message }, { "time", res.Time } }));
            }
            catch { }
            return res;
        }

        static Process StartApp(ApplyOptions o)
        {
            string exe = Path.Combine(o.Target, "ClearDesk.exe");
            var psi = new ProcessStartInfo(exe, string.Join(" ", o.RestartArgs.Select(QuoteArg))) { WorkingDirectory = o.Target, UseShellExecute = false };
            return Process.Start(psi);
        }

        // ---------------------------------------------------------------- helper process entry (ClearDesk.exe --apply-update ...)
        internal static ApplyOptions ParseArgs(string[] args)
        {
            var o = new ApplyOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i]; string next = i + 1 < args.Length ? args[i + 1] : "";
                switch (a)
                {
                    case "--stage": o.Stage = next; i++; break;
                    case "--target": o.Target = next.TrimEnd('\\'); i++; break;
                    case "--data": o.Data = next.TrimEnd('\\'); i++; break;
                    case "--from": o.FromVersion = next; i++; break;
                    case "--to": o.ToVersion = next; i++; break;
                    case "--pid": int.TryParse(next, out o.Pid); i++; break;
                    case "--rarg": o.RestartArgs.Add(next); i++; break;
                    case "--silent": o.Silent = true; break;
                    case "--no-restart": o.NoRestart = true; break;
                }
            }
            return o;
        }

        internal static void RunHelper(string[] args)
        {
            var o = ParseArgs(args);
            o.RestoreTaskbar = delegate { TopTaskbar.Restore(); };
            if (string.IsNullOrEmpty(o.Stage) || string.IsNullOrEmpty(o.Target) || string.IsNullOrEmpty(o.Data) || !Directory.Exists(o.Stage)) return;
            if (o.Silent) { Apply(o); return; }
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var win = new UpdateProgressWindow();
            o.Status = s => win.Dispatcher.BeginInvoke(new Action(() => win.SetStatus(s)));
            win.Loaded += delegate
            {
                Task.Run(() => Apply(o)).ContinueWith(t =>
                {
                    var r = t.Status == TaskStatus.RanToCompletion ? t.Result : new ApplyResult { Message = t.Exception == null ? "未知错误" : t.Exception.GetBaseException().Message };
                    win.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        win.Hide();
                        if (!r.Ok) MessageBox.Show(r.Message, "ClearDesk 更新", MessageBoxButton.OK, MessageBoxImage.Warning);
                        app.Shutdown();
                    }));
                });
            };
            app.Run(win);
        }
    }

    // Small white progress window shown by the updater helper.
    internal sealed class UpdateProgressWindow : Window
    {
        readonly TextBlock status = new TextBlock { Text = "正在准备更新…", FontSize = 13, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5B, 0x67, 0x7C)), Margin = new Thickness(0, 10, 0, 14), TextWrapping = TextWrapping.Wrap };
        internal UpdateProgressWindow()
        {
            Title = "ClearDesk 更新"; Width = 380; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brushes.White; FontFamily = new FontFamily("Microsoft YaHei UI"); Topmost = true;
            try { Icon = AppBrand.Icon; } catch { }
            var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };
            panel.Children.Add(new TextBlock { Text = "正在更新 ClearDesk", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x10, 0x18, 0x28)) });
            panel.Children.Add(status);
            panel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 5, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x16, 0x77, 0xFF)), Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE6, 0xEA, 0xF1)) });
            panel.Children.Add(new TextBlock { Text = "完成后会自动重新打开，请不要关闭此窗口。", FontSize = 11.5, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8C, 0x97, 0xA9)), Margin = new Thickness(0, 12, 0, 0) });
            Content = panel;
        }
        internal void SetStatus(string s) { status.Text = s; }
    }
}
