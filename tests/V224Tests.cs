using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ClearDesk
{
    /// <summary>2.2.4: widget positions survive restarts / logoff / locked files; the TranslucentTB component is verified against the shipped copy and repairs itself.</summary>
    internal static class V224Tests
    {
        internal static void Rules(Action<bool, string> check)
        {
            ClampRules(check);
            StoreRules(check);
        }

        internal static async Task Run(Action<bool, string> check)
        {
            await Task.Yield();
            TaskbarRules(check);
            BlockedSave(check);
            await WidgetRun(check);
        }

        static string Temp(string name) { string d = Path.Combine(Path.GetTempPath(), "cd224-" + name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(d); return d; }
        static void Rm(string d) { try { Directory.Delete(d, true); } catch { } }

        // ================================================================ position rules (pure)
        static void ClampRules(Action<bool, string> check)
        {
            var wa = new List<Rect> { new Rect(0, 0, 1920, 1040), new Rect(-1280, 0, 1280, 984) };
            var p = PositionGuard.Clamp(new Point(5000, 300), new Size(370, 170), wa);
            check(Math.Abs(p.X - (1920 - 370)) < .1 && Math.Abs(p.Y - 300) < .1, "2.2.4 position: a saved place beyond the right edge is moved to the nearest visible place, not to the top-left default (" + p.X + "," + p.Y + ")");
            p = PositionGuard.Clamp(new Point(-2500, -90), new Size(300, 200), wa);
            check(Math.Abs(p.X - (-1280)) < .1 && Math.Abs(p.Y) < .1, "2.2.4 position: a place left of the second monitor goes to that monitor's edge");
            p = PositionGuard.Clamp(new Point(double.NaN, 5), new Size(300, 200), wa);
            check(p.X == 20 && p.Y == 20, "2.2.4 position: an unusable (NaN) saved place is the only case that falls back to the default");
            check(PositionGuard.PixelFix(1000, 200, 144, 144, 1000, 200, new[] { new Rect(0, 0, 3000, 1500) }) == null, "2.2.4 position: device pixels already right: nothing to do");
            var fix = PositionGuard.PixelFix(1000, 200, 144, 144, 640, 130, new[] { new Rect(0, 0, 3000, 1500) });
            check(fix.HasValue && fix.Value.X == 1000 && fix.Value.Y == 200, "2.2.4 position: the same DPI but another place after the settle time: put back to the saved device pixels");
            check(PositionGuard.PixelFix(1000, 200, 144, 96, 640, 130, new[] { new Rect(0, 0, 3000, 1500) }) == null, "2.2.4 position: another DPI than at save time: the pixel record is not used");
            check(PositionGuard.PixelFix(5000, 200, 144, 144, 640, 130, new[] { new Rect(0, 0, 3000, 1500) }) == null && PositionGuard.PixelFix(int.MinValue, 0, 0, 96, 1, 1, new[] { new Rect(0, 0, 100, 100) }) == null, "2.2.4 position: a saved pixel place that is on no monitor now, or no pixel record: nothing to do");
        }

        // ================================================================ settings file
        static Settings Sample(double cx, double cy, double px, double py)
        {
            var s = new Settings();
            s.Widgets["clock"] = new BoxData { Id = "clock", X = cx, Y = cy, Width = 370, Height = 170 };
            s.Widgets["performance"] = new BoxData { Id = "performance", X = px, Y = py, Width = 300, Height = 200 };
            return s;
        }

        static bool Same(Settings s, double cx, double cy, double px, double py)
        {
            BoxData c, p;
            return s != null && s.Widgets.TryGetValue("clock", out c) && s.Widgets.TryGetValue("performance", out p) && c.X == cx && c.Y == cy && p.X == px && p.Y == py;
        }

        static void StoreRules(Action<bool, string> check)
        {
            string dir = Temp("cfg"); string cfg = Path.Combine(dir, "settings.json");
            int oldR = ConfigStore.ReadRetries, oldD = ConfigStore.ReadDelayMs, oldW = ConfigStore.WriteRetries;
            ConfigStore.ReadRetries = 2; ConfigStore.ReadDelayMs = 20; ConfigStore.WriteRetries = 4;
            try
            {
                // fresh profile
                var fresh = ConfigStore.Load(cfg);
                check(fresh.S == null && !fresh.Existed && !fresh.Blocked && !fresh.Corrupt, "2.2.4 settings: fresh profile (no settings.json) is reported as such");

                // saved, process killed without a normal exit, started again
                string err;
                bool w = ConfigStore.Write(cfg, Sample(1234.5, 567, 40, 880), out err);
                var back = ConfigStore.Load(cfg);
                check(w && Same(back.S, 1234.5, 567, 40, 880) && back.Source == cfg && !File.Exists(cfg + ".tmp"), "2.2.4 settings: positions of the clock and performance widgets survive a kill without a normal exit (written when saved) " + err);

                // second save keeps the previous file as .bak
                ConfigStore.Write(cfg, Sample(1, 2, 3, 4), out err);
                check(File.Exists(cfg + ".bak") && Same(ConfigStore.Load(cfg + ".bak").S ?? back.S, 1234.5, 567, 40, 880), "2.2.4 settings: the previous good file is kept as settings.json.bak");

                // zero-byte / corrupt settings.json (power loss, antivirus) is restored from the backup
                File.WriteAllText(cfg, "");
                var z = ConfigStore.Load(cfg);
                check(z.S != null && z.Source == cfg + ".bak" && Same(z.S, 1234.5, 567, 40, 880), "2.2.4 settings: an empty settings.json is restored from settings.json.bak, positions intact");
                File.WriteAllText(cfg, "{\"Widgets\": {\"clock\": ");
                var c2 = ConfigStore.Load(cfg);
                check(c2.S != null && c2.Source == cfg + ".bak" && Same(c2.S, 1234.5, 567, 40, 880), "2.2.4 settings: a truncated settings.json is restored from settings.json.bak");
                File.Delete(cfg + ".bak"); File.WriteAllText(cfg, "{broken");
                var c3 = ConfigStore.Load(cfg);
                check(c3.S == null && c3.Corrupt && !c3.Blocked, "2.2.4 settings: corrupt with no backup is reported as corrupt (defaults), not as locked");

                // locked while reading (antivirus scanning at logon): never treated as "no settings"
                ConfigStore.Write(cfg, Sample(700, 80, 30, 400), out err);
                string before = File.ReadAllText(cfg);
                ConfigStore.BeforeRead = (path, n) => { if (path == cfg) throw new IOException("locked by antivirus"); };
                var lk = ConfigStore.Load(cfg);
                check(lk.Blocked && lk.Existed, "2.2.4 settings: a settings.json that is locked while reading is reported as blocked (not as missing)");
                bool oldBlocked = Program.ConfigBlocked; var oldS = Program.S;
                string oldCfgLoad = null;
                ConfigStore.BeforeRead = null;

                // a temporarily locked file: the read is retried and succeeds
                int reads = 0;
                ConfigStore.ReadRetries = 6;
                ConfigStore.BeforeRead = (path, n) => { if (path == cfg && reads++ < 2) throw new IOException("locked"); };
                var rt = ConfigStore.Load(cfg);
                check(!rt.Blocked && Same(rt.S, 700, 80, 30, 400) && rt.Source == cfg, "2.2.4 settings: a read that is blocked for a moment is retried and the real positions are loaded");
                ConfigStore.BeforeRead = null; ConfigStore.ReadRetries = 2;

                // write: blocked twice, then ok
                int tries = 0;
                ConfigStore.BeforeWrite = a => { tries++; if (a < 2) throw new IOException("sharing violation"); };
                bool wr = ConfigStore.Write(cfg, Sample(11, 22, 33, 44), out err);
                ConfigStore.BeforeWrite = null;
                check(wr && tries == 3 && Same(ConfigStore.Load(cfg).S, 11, 22, 33, 44), "2.2.4 settings: a write that hits sharing violations is retried until it succeeds");
                ConfigStore.BeforeWrite = a => { throw new UnauthorizedAccessException("denied"); };
                bool wd = ConfigStore.Write(cfg, Sample(5, 5, 5, 5), out err);
                ConfigStore.BeforeWrite = null;
                check(!wd && err != null && Same(ConfigStore.Load(cfg).S, 11, 22, 33, 44), "2.2.4 settings: access denied is retried, then reported without touching the existing file");

                // real lock on settings.json held by another process-like handle for 300 ms
                ConfigStore.WriteRetries = 8;
                var held = new FileStream(cfg, FileMode.Open, FileAccess.Read, FileShare.None);
                var rel = new Thread(() => { Thread.Sleep(300); held.Dispose(); }); rel.Start();
                bool wl = ConfigStore.Write(cfg, Sample(77, 88, 99, 111), out err);
                rel.Join();
                check(wl && Same(ConfigStore.Load(cfg).S, 77, 88, 99, 111), "2.2.4 settings: a real exclusive lock (antivirus) for a moment does not lose the save");
                ConfigStore.WriteRetries = 4;

                // legacy 2.0.x / 2.1.x files
                string legacy = "{\"ConfigVersion\":3,\"Clock\":true,\"Widgets\":{\"Clock\":{\"Id\":\"clock\",\"X\":812.5,\"Y\":143,\"Width\":370,\"Height\":170},\"Performance\":{\"X\":50,\"Y\":60,\"Width\":300,\"Height\":200}}}";
                File.WriteAllText(cfg, legacy); File.Delete(cfg + ".bak");
                var lg = ConfigStore.Load(cfg); ConfigStore.Migrate(lg.S); ConfigStore.Sanitize(lg.S);
                check(Same(lg.S, 812.5, 143, 50, 60), "2.2.4 settings: legacy file with mixed-case widget keys keeps the clock and performance positions");
                File.WriteAllText(cfg, "{\"ConfigVersion\":1,\"Clock\":true,\"Performance\":true}");
                var l1 = ConfigStore.Load(cfg);
                check(l1.S != null && l1.S.Widgets != null && l1.S.Widgets.Count == 0 && l1.S.Clock, "2.2.4 settings: a 2.0.x file without widget records loads (defaults are placed once and then saved)");

                // numbers JSON cannot carry
                var nan = Sample(double.NaN, 5, 40, 880);
                bool fx = ConfigStore.Sanitize(nan);
                check(fx && !nan.Widgets.ContainsKey("clock") && nan.Widgets["performance"].X == 40, "2.2.4 settings: a NaN position only resets that widget, the other is kept");
                var inf = Sample(10, 20, 30, 40); inf.Widgets["clock"].Width = double.PositiveInfinity;
                bool wi = ConfigStore.Write(cfg, inf, out err); var li = ConfigStore.Load(cfg);
                check(wi && li.S != null, "2.2.4 settings: an impossible number never produces an unreadable settings.json");

            }
            catch (Exception ex) { check(false, "2.2.4 settings tests crashed: " + ex); }
            finally
            {
                ConfigStore.BeforeRead = null; ConfigStore.BeforeWrite = null;
                ConfigStore.ReadRetries = oldR; ConfigStore.ReadDelayMs = oldD; ConfigStore.WriteRetries = oldW;
                Rm(dir);
            }
        }

        static void BlockedSave(Action<bool, string> check)
        {
            int oldR = ConfigStore.ReadRetries, oldD = ConfigStore.ReadDelayMs;
            ConfigStore.ReadRetries = 2; ConfigStore.ReadDelayMs = 20;
            bool prev = Program.ConfigBlocked;
            try
            {
                string progCfgText = File.Exists(Program.Config) ? File.ReadAllText(Program.Config) : null;
                ConfigStore.BeforeRead = (path, n) => { if (path == Program.Config) throw new IOException("locked"); };
                Program.ConfigBlocked = true;
                Program.Save();
                string progCfgAfter = File.Exists(Program.Config) ? File.ReadAllText(Program.Config) : null;
                check(progCfgText == progCfgAfter, "2.2.4 settings: while settings.json cannot be read nothing is written over it");
            }
            catch (Exception ex) { check(false, "2.2.4 blocked-save test crashed: " + ex); }
            finally { ConfigStore.BeforeRead = null; Program.ConfigBlocked = prev; ConfigStore.ReadRetries = oldR; ConfigStore.ReadDelayMs = oldD; }
        }

        // ================================================================ transparent taskbar component
        static void TaskbarRules(Action<bool, string> check)
        {
            string root = Temp("tb");
            int oldRetries = TaskbarComponent.BusyRetries, oldDelay = TaskbarComponent.BusyDelayMs;
            TaskbarComponent.BusyRetries = 1; TaskbarComponent.BusyDelayMs = 20;
            try
            {
                string good = V223TaskbarTests.Make(root, "factory");
                var pack = ComponentPack.FromFolder(good);
                check(pack.SelfCheck() && pack.Manifest != null && pack.Manifest.Count == 3, "2.2.4 taskbar: a factory copy built from a folder is self-consistent");
                string cache = Path.Combine(root, "good");

                // stale manifest in the folder (files fine, verified.sha256 from some other version)
                string stale = V223TaskbarTests.Make(root, "stale");
                File.WriteAllText(Path.Combine(stale, "verified.sha256"), string.Join("\r\n", new[] { "ExplorerHooks.dll", "TranslucentTB.exe", "Xaml.dll" }.Select(n => new string('A', 64) + "  " + n)) + "\r\n");
                check(TaskbarComponent.Verify(stale).State == ComponentState.Mismatch, "2.2.4 taskbar: (2.2.3 behaviour) a stale verified.sha256 alone made the folder 'mismatched'");
                var cs = TaskbarComponent.Verify(stale, pack);
                check(cs.Ok && cs.Source == "出厂副本" && cs.Code == "TB-00", "2.2.4 taskbar: a stale verified.sha256 in the folder is harmless, the shipped copy is the reference");
                var s1 = new TaskbarService { Dir = stale, CacheDir = cache, Args = "-n 60 127.0.0.1", ExternalRunning = () => false, PackSource = () => pack, AllowUnverified = () => false };
                bool st = s1.TryStart();
                check(st && s1.Failure == null && s1.RunFrom == stale, "2.2.4 taskbar: starts from the install folder although the folder manifest is stale"); V223TaskbarTests.Kill(s1);

                // another TranslucentTB version in the folder (different exe bytes), no good copy kept
                string other = V223TaskbarTests.Make(root, "otherver");
                File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(other, "TranslucentTB.exe"), true);
                V223TaskbarTests.Manifest(other);            // the folder is "consistent" with itself but is not the shipped component
                var co = TaskbarComponent.Verify(other, pack);
                check(co.State == ComponentState.Mismatch && co.File == "TranslucentTB.exe" && co.Code == "TB-03", "2.2.4 taskbar: a different TranslucentTB.exe is a mismatch against the shipped copy (TB-03)");
                var s2 = new TaskbarService { Dir = other, CacheDir = Path.Combine(root, "nogood"), Args = "-n 60 127.0.0.1", ExternalRunning = () => false, PackSource = () => pack, AllowUnverified = () => false };
                bool o2 = s2.TryStart();
                check(o2 && s2.Repaired != null && TaskbarComponent.Verify(other, pack).Ok, "2.2.4 taskbar: no kept good copy needed: the folder is repaired from the shipped copy and then starts (2.2.3 stopped here with 没有可用的完好备份)");
                V223TaskbarTests.Kill(s2);

                // a file modified while ClearDesk was not looking (runtime-modified / antivirus rewrite)
                File.WriteAllBytes(Path.Combine(other, "Xaml.dll"), new byte[] { 1, 2, 3 });
                var s3 = new TaskbarService { Dir = other, CacheDir = cache, Args = "-n 60 127.0.0.1", ExternalRunning = () => false, PackSource = () => pack, AllowUnverified = () => false };
                check(s3.TryStart() && TaskbarComponent.Verify(other, pack).Ok, "2.2.4 taskbar: a runtime-modified component file is repaired and started"); V223TaskbarTests.Kill(s3);

                // the install folder cannot be written at all (a file where the folder should be): per-user copy
                string blocker = Path.Combine(root, "afile"); File.WriteAllText(blocker, "x");
                string run = Path.Combine(root, "taskbar-run");
                var s4 = new TaskbarService { Dir = Path.Combine(blocker, "taskbar"), CacheDir = cache, RunDirectory = run, Args = "-n 60 127.0.0.1", ExternalRunning = () => false, PackSource = () => pack, AllowUnverified = () => false };
                bool o4 = s4.TryStart();
                check(o4 && s4.RunFrom == run && s4.Exe.StartsWith(run, StringComparison.OrdinalIgnoreCase) && TaskbarComponent.Verify(run, pack).Ok, "2.2.4 taskbar: install folder unusable -> the shipped copy is placed in the per-user folder and started from there");
                V223TaskbarTests.Kill(s4);

                // CRLF + BOM manifest written by another tool
                string bom = V223TaskbarTests.Make(root, "bom");
                string mf = Path.Combine(bom, "verified.sha256");
                File.WriteAllText(mf, File.ReadAllText(mf), new UTF8Encoding(true));
                check(TaskbarComponent.Verify(bom).Ok, "2.2.4 taskbar: a manifest with BOM and CRLF is read correctly");

                // damaged shipped copy is never used for repairs
                var broken = ComponentPack.FromFolder(good);
                string ex1 = V223TaskbarTests.Make(root, "bp");
                var bp = new ComponentPack(BrokenZip(good));
                check(!bp.SelfCheck(), "2.2.4 taskbar: a damaged factory copy fails its self-check");

                // allow-unverified: only same version + valid signature
                string note;
                string uv = V223TaskbarTests.Make(root, "uv");
                File.WriteAllBytes(Path.Combine(uv, "Xaml.dll"), new byte[] { 9, 9, 9 });   // differs from the folder manifest
                var unusable = new ComponentPack(BrokenZip(good));
                var off = TaskbarComponent.Ensure(uv, Path.Combine(root, "nogood2"), null, false, null, out note);
                check(!off.Ok && off.Code == "TB-03", "2.2.4 taskbar: unverified component is refused by default (TB-03)");
                var oldSig = TaskbarComponent.SignatureOk;
                try
                {
                    TaskbarComponent.SignatureOk = (path, signer) => true;
                    var on = TaskbarComponent.Ensure(uv, Path.Combine(root, "nogood2"), null, true, null, out note);
                    check(on.Ok && on.UnverifiedAllowed && on.Code == "TB-09", "2.2.4 taskbar: with the option on, a valid-signature component of the shipped version is allowed (TB-09)");
                    TaskbarComponent.SignatureOk = (path, signer) => false;
                    var badSig = TaskbarComponent.Ensure(uv, Path.Combine(root, "nogood2"), null, true, null, out note);
                    check(!badSig.Ok, "2.2.4 taskbar: with the option on, an invalid signature is still refused");
                    TaskbarComponent.SignatureOk = (path, signer) => true;
                    string why;
                    var diffVer = new ComponentPack(ZipOf(good, "version.txt", "0.0.0-other"));
                    check(!TaskbarComponent.UnverifiedAcceptable(uv, diffVer, out why) && why.Length > 0, "2.2.4 taskbar: with the option on, another TranslucentTB version is still refused (" + why + ")");
                    var sameVer = new ComponentPack(ZipOf(good, "version.txt", TaskbarComponent.FileVer(Path.Combine(uv, "TranslucentTB.exe"))));
                    check(TaskbarComponent.UnverifiedAcceptable(uv, sameVer, out why), "2.2.4 taskbar: with the option on, the same version is accepted");
                }
                finally { TaskbarComponent.SignatureOk = oldSig; }

                // diagnostics carry expected / actual hashes per file
                string d = TaskbarComponent.Describe(other, cache, pack, true);
                check(d.Contains("期望") && d.Contains("实际") && d.Contains("TranslucentTB.exe") && d.Contains(other), "2.2.4 taskbar: the diagnostic text names the folder and each file with expected and actual SHA-256");

                // short codes
                check(new ComponentCheck { State = ComponentState.Ok }.Code == "TB-00" && new ComponentCheck { State = ComponentState.Missing }.Code == "TB-02" && new ComponentCheck { State = ComponentState.Mismatch }.Code == "TB-03" && new ComponentCheck { State = ComponentState.NoManifest }.Code == "TB-01", "2.2.4 taskbar: each failure has a short code");

                // nothing to repair from, folder manifest only: still no exception and a precise reason
                string lone = V223TaskbarTests.Make(root, "lone"); File.WriteAllText(Path.Combine(lone, "Xaml.dll"), "bad");
                var s5 = new TaskbarService { Dir = lone, CacheDir = Path.Combine(root, "nocache"), Args = "-n 60 127.0.0.1", ExternalRunning = () => false, PackSource = () => null, AllowUnverified = () => false };
                bool o5 = true; try { o5 = s5.TryStart(); } catch { o5 = true; }
                check(!o5 && s5.Code == "TB-03" && !string.IsNullOrEmpty(s5.Diag) && s5.Diag.Contains("Xaml.dll"), "2.2.4 taskbar: without any shipped copy a mismatch gives TB-03 and a full diagnosis");
            }
            catch (Exception ex) { check(false, "2.2.4 taskbar tests crashed: " + ex); }
            finally
            {
                TaskbarComponent.BusyRetries = oldRetries; TaskbarComponent.BusyDelayMs = oldDelay;
                foreach (var p in Process.GetProcessesByName("TranslucentTB")) { try { if (p.MainModule.FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase)) p.Kill(); } catch { } }
                Thread.Sleep(300);
                Rm(root);
            }
        }

        static byte[] ZipOf(string folder, string extraName, string extraText)
        {
            var ms = new MemoryStream();
            using (var za = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
            {
                foreach (string f in Directory.GetFiles(folder))
                {
                    var e = za.CreateEntry(Path.GetFileName(f)); var b = File.ReadAllBytes(f);
                    using (var o = e.Open()) o.Write(b, 0, b.Length);
                }
                var x = za.CreateEntry(extraName); var xb = Encoding.UTF8.GetBytes(extraText);
                using (var o = x.Open()) o.Write(xb, 0, xb.Length);
            }
            return ms.ToArray();
        }

        /// <summary>A pack whose Xaml.dll no longer matches its own manifest.</summary>
        static byte[] BrokenZip(string folder)
        {
            var ms = new MemoryStream();
            using (var za = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
                foreach (string f in Directory.GetFiles(folder))
                {
                    var e = za.CreateEntry(Path.GetFileName(f)); var b = File.ReadAllBytes(f);
                    if (Path.GetFileName(f) == "Xaml.dll") b = new byte[] { 0, 1, 2 };
                    using (var o = e.Open()) o.Write(b, 0, b.Length);
                }
            return ms.ToArray();
        }

        // ================================================================ real widgets
        static async Task WidgetRun(Action<bool, string> check)
        {
            var opened = new List<GlassWindow>();
            string saveCfgBefore = null;
            try
            {
                var oldClock = Program.S.Widgets.ContainsKey("clock") ? Program.S.Widgets["clock"] : null;
                var oldPerf = Program.S.Widgets.ContainsKey("performance") ? Program.S.Widgets["performance"] : null;
                var clock = Program.NewWidget("clock"); opened.Add(clock);
                var perf = Program.NewWidget("performance"); opened.Add(perf);
                await Task.Delay(300);

                // the user moves both widgets; the debounced save writes them without any further action (no normal exit needed)
                double cx = clock.Left + 61, cy = clock.Top + 37, px = perf.Left - 43, py = perf.Top + 29;
                clock.Left = cx; clock.Top = cy; perf.Left = px; perf.Top = py;
                await Task.Delay(1800);
                var disk = ConfigStore.Load(Program.Config);
                bool onDisk = disk.S != null && disk.S.Widgets.ContainsKey("clock") && disk.S.Widgets.ContainsKey("performance");
                check(onDisk && Math.Abs(disk.S.Widgets["clock"].X - cx) < .6 && Math.Abs(disk.S.Widgets["clock"].Y - cy) < .6 && Math.Abs(disk.S.Widgets["performance"].X - px) < .6 && Math.Abs(disk.S.Widgets["performance"].Y - py) < .6,
                    "2.2.4 position: moving the clock / performance widgets writes the new place to settings.json by itself (a kill right after loses nothing)");

                // "restart": the saved record is read from disk and the widgets open exactly there
                clock.Close(); perf.Close(); opened.Clear();
                Program.S.Widgets["clock"] = disk.S.Widgets["clock"]; Program.S.Widgets["performance"] = disk.S.Widgets["performance"];
                var clock2 = Program.NewWidget("clock"); opened.Add(clock2);
                var perf2 = Program.NewWidget("performance"); opened.Add(perf2);
                await Task.Delay(300);
                check(Math.Abs(clock2.Left - cx) < .6 && Math.Abs(clock2.Top - cy) < .6 && Math.Abs(perf2.Left - px) < .6 && Math.Abs(perf2.Top - py) < .6, "2.2.4 position: after the restart both widgets are exactly where they were saved");

                // session end: unsaved change is flushed by FlushSave / by WM_ENDSESSION reaching the widget window
                double mark = 333.25;
                Program.S.Widgets["performance"].X = mark;
                Program.FlushSave();
                var d2 = ConfigStore.Load(Program.Config);
                check(d2.S != null && d2.S.Widgets["performance"].X == mark, "2.2.4 position: the exit / logoff flush writes everything still unsaved");
                Program.S.Widgets["performance"].X = mark + 100;
                var h = new System.Windows.Interop.WindowInteropHelper(clock2).Handle;
                SendEndSession(h);
                var d3 = ConfigStore.Load(Program.Config);
                check(d3.S != null && d3.S.Widgets["performance"].X == mark + 100, "2.2.4 position: Windows' WM_ENDSESSION (logoff / shutdown) saves the settings");
                Program.S.Widgets["performance"].X = px;
                Program.Save();

                // saved place off every monitor: shown visibly, saved place kept
                clock2.Close(); opened.Clear();
                Program.S.Widgets["clock"].X = 6000; Program.S.Widgets["clock"].Y = 400;
                var clock3 = Program.NewWidget("clock"); opened.Add(clock3);
                await Task.Delay(300);
                var wa = SystemParameters.WorkArea;
                check(clock3.Left + 20 < wa.Right + 2000 && clock3.Left < 6000 && !(Math.Abs(clock3.Left - (wa.Left + 20)) < 1 && Math.Abs(clock3.Top - (wa.Top + 20)) < 1) && Program.S.Widgets["clock"].X == 6000, "2.2.4 position: an unreachable saved place is clamped next to its old place instead of the default corner; the saved value is kept");

                if (oldClock != null) Program.S.Widgets["clock"] = oldClock;
                if (oldPerf != null) Program.S.Widgets["performance"] = oldPerf;
                Program.Save();
            }
            catch (Exception ex) { check(false, "2.2.4 widget position tests crashed: " + ex); }
            finally { foreach (var w in opened) { try { w.Close(); } catch { } } }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        static void SendEndSession(IntPtr h) { SendMessage(h, 0x16, IntPtr.Zero, IntPtr.Zero); }     // WM_ENDSESSION with wParam = FALSE: "the session is NOT ending" - the hook still saves
    }
}
