using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ClearDesk
{
    // 2.1.0 online update — orchestration: when to check, what to remember, download → verify → stage → hand over to the updater.
    internal static class UpdateService
    {
        internal const string Version = SettingsWindow.Version;
        internal const int CheckIntervalHours = 12, StartupDelaySeconds = 75;
        static bool busy;
        static DispatcherTimer timer;
        static DateTime nextTry = DateTime.MinValue;
        internal static int TestDelaySeconds = -1;

        internal static string UpdateDir { get { return UpdateApplier.UpdateDir(Program.Data); } }
        internal static UpdateConfig Config { get { return UpdateSources.ReadConfig(Program.Data, Program.Base); } }
        internal static List<string> Sources()
        {
            return UpdateSources.Resolve(Program.S.UpdateSource, Config, Program.S.UpdateRedirect, Program.S.UpdateMirrors);
        }
        internal static bool GithubFallback { get { var c = Config; return c == null || c.GithubFallback; } }

        internal static Task<UpdateCheckResult> CheckAsync()
        {
            var sources = Sources(); bool gh = GithubFallback;       // snapshot on the UI thread
            return Task.Run(() => UpdateClient.Check(Version, sources, gh));
        }

        // UI thread, after a check: remember when we last looked and what a signed manifest told us about new locations.
        internal static void Record(UpdateCheckResult r)
        {
            if (r == null || !r.Completed) return;
            var s = Program.S;
            s.LastUpdateCheck = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var m = r.Manifest;
            if (m.Signed && !m.NotifyOnly)
            {
                string redirect = UpdateSources.Normalize(m.UpdateUrl); if (redirect != null) s.UpdateRedirect = redirect;
                var mirrors = m.Mirrors.Select(UpdateSources.Normalize).Where(x => x != null).Distinct().ToList(); if (mirrors.Count > 0) s.UpdateMirrors = mirrors;
            }
            try { Program.Save(); } catch (Exception e) { Program.Log(e); }
        }

        internal static string LastCheckText()
        {
            DateTime t;
            if (DateTime.TryParse(Program.S.LastUpdateCheck, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t)) return "上次检查：" + t.ToLocalTime().ToString("MM-dd HH:mm");
            return "尚未检查过更新。";
        }

        internal static bool Due(DateTime nowUtc)
        {
            if (nowUtc < nextTry) return false;
            DateTime last;
            if (!DateTime.TryParse(Program.S.LastUpdateCheck, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out last)) return true;
            last = last.ToUniversalTime();
            return last > nowUtc || (nowUtc - last).TotalHours >= CheckIntervalHours;
        }

        // Should a *background* check pop the dialog? (A manual check always does.)
        internal static bool ShouldNotify(UpdateManifest m, DateTime nowUtc)
        {
            if (m == null || !UpdateVersion.IsNewer(m.Latest, Version)) return false;
            if (string.Equals(Program.S.SkipVersion, m.Latest, StringComparison.OrdinalIgnoreCase)) return false;
            DateTime until;
            if (DateTime.TryParse(Program.S.RemindAfter, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out until) && until.ToUniversalTime() > nowUtc) return false;
            return true;
        }

        // Called once the app is running. First check after a short delay, then at most one per CheckIntervalHours.
        internal static void Start()
        {
            if (timer != null) return;
            int delay = TestDelaySeconds >= 0 ? TestDelaySeconds : StartupDelaySeconds;
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
            timer.Tick += delegate
            {
                timer.Interval = TimeSpan.FromMinutes(30);
                if (!Program.S.AutoUpdateCheck || Program.Quitting) return;
                if (Due(DateTime.UtcNow)) AutoCheck();
            };
            timer.Start();
        }

        static async void AutoCheck()
        {
            if (busy) return;
            busy = true;
            try
            {
                var r = await CheckAsync();
                if (!r.Completed) { nextTry = DateTime.UtcNow.AddHours(1); Program.LogText("自动检查更新失败：" + r.Error); return; }
                Record(r);
                if (ShouldNotify(r.Manifest, DateTime.UtcNow)) UpdateWindow.ShowFor(r.Manifest, false);
            }
            catch (Exception e) { Program.Log(e); nextTry = DateTime.UtcNow.AddHours(1); }
            finally { busy = false; }
        }

        internal static void Remind(UpdateManifest m) { Program.S.RemindAfter = DateTime.UtcNow.AddHours(24).ToString("o", CultureInfo.InvariantCulture); Program.Save(); }
        internal static void Skip(UpdateManifest m) { Program.S.SkipVersion = m.Latest; Program.Save(); }

        static string Mb(long n) { return (n / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB"; }

        // Downloads, verifies and stages the update, then starts the updater helper. Returns null once the helper is running
        // (the caller then quits ClearDesk), otherwise a user-readable error. Nothing in the install folder is touched here.
        internal static async Task<string> InstallAsync(UpdateManifest m, Action<string, double> progress, CancellationToken ct)
        {
            if (m.NotifyOnly) return "这个版本需要手动下载安装。";
            if (!m.Signed) return "更新清单没有通过签名验证，已拒绝。";
            string bad = m.ValidateInstallable(); if (bad != null) return bad;
            if (!UpdateVersion.IsNewer(m.Latest, Version)) return "已是最新版本。";
            if (StartupManager.IsTemporaryLocation(Program.Base))
                return "ClearDesk 正从压缩包或临时文件夹里运行，无法原地更新。请先把整个文件夹解压到固定位置（例如 D:\\ClearDesk）再运行。";
            string folder = Program.Base.TrimEnd('\\');
            if (!UpdateApplier.CanWrite(folder))
                return "ClearDesk 所在的文件夹（" + folder + "）需要管理员权限才能写入，自动更新无法替换文件。请把 ClearDesk 放到普通文件夹（如 D:\\ClearDesk），或手动下载新版覆盖。";
            var extra = Sources();
            progress("正在连接服务器…", 0);
            var dl = await Task.Run(() => UpdateClient.Download(m, extra, Path.Combine(UpdateDir, "download"), (got, total) => progress("正在下载 " + Mb(got) + " / " + Mb(total), total > 0 ? (double)got / total : 0), ct));
            if (!dl.Ok) return dl.Error;
            progress("正在校验更新包…", 1);
            string stage = Path.Combine(UpdateDir, "stage-" + m.Latest);
            try { await Task.Run(() => UpdatePackage.Stage(dl.Path, stage)); }
            catch (Exception e)
            {
                try { File.Delete(dl.Path); if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { }
                return "更新包内容校验失败：" + e.Message + "（已丢弃，未做任何更改）";
            }
            string err = LaunchApplier(stage, m.Latest);
            if (err != null) return err;
            try { File.Delete(dl.Path); } catch { }
            return null;
        }

        static string LaunchApplier(string stage, string toVersion)
        {
            try
            {
                string self = Assembly.GetExecutingAssembly().Location;
                string tmp = Path.Combine(Path.GetTempPath(), "ClearDeskUpdate-" + Guid.NewGuid().ToString("N").Substring(0, 10));
                Directory.CreateDirectory(tmp);
                string exe = Path.Combine(tmp, "ClearDesk.exe");
                File.Copy(self, exe);
                if (File.Exists(self + ".config")) File.Copy(self + ".config", exe + ".config");
                var args = new List<string> { "--apply-update", "--stage", stage, "--target", Program.Base.TrimEnd('\\'), "--data", Program.Data, "--pid", Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture), "--from", Version, "--to", toVersion };
                foreach (var a in UpdateApplier.RestartArgsFrom(Program.LaunchArgs)) { args.Add("--rarg"); args.Add(a); }
                if (Program.Preview) args.Add("--silent");
                Process.Start(new ProcessStartInfo(exe, string.Join(" ", args.Select(UpdateApplier.QuoteArg))) { UseShellExecute = false, WorkingDirectory = tmp });
                return null;
            }
            catch (Exception e) { return "无法启动更新程序：" + e.Message; }
        }

        // First start after an update (or a rolled-back one): tell the user once, then forget it.
        internal static void ReportLastResult()
        {
            try
            {
                string path = UpdateApplier.ResultPath(Program.Data);
                if (!File.Exists(path)) return;
                var d = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                File.Delete(path);
                if (d == null) return;
                bool ok = d.ContainsKey("ok") && d["ok"] is bool && (bool)d["ok"];
                string msg = d.ContainsKey("message") ? Convert.ToString(d["message"]) : "";
                Program.LogText("更新结果：" + (ok ? "成功 " : "失败 ") + msg);
                Program.Balloon(ok ? "ClearDesk 已更新" : "ClearDesk 更新未完成", ok ? "现在是 " + Version + "。" : msg);
            }
            catch (Exception e) { Program.Log(e); }
        }

        // Startup housekeeping on a worker thread: undo an interrupted swap, remove leftovers.
        internal static void Housekeeping()
        {
            Task.Run(() =>
            {
                try { UpdateApplier.RecoverInterrupted(Program.Data); } catch (Exception e) { Program.Log(e); }
                Thread.Sleep(15000);
                try { UpdateApplier.CleanupLeftovers(Program.Base.TrimEnd('\\')); } catch { }
            });
        }
    }
}
