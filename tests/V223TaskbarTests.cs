using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ClearDesk
{
    /// <summary>2.2.3: the optional transparent-taskbar component (TranslucentTB) must never stop ClearDesk from starting.</summary>
    internal static class V223TaskbarTests
    {
        internal static string Sha(string path) { using (var f = File.OpenRead(path)) using (var s = SHA256.Create()) return BitConverter.ToString(s.ComputeHash(f)).Replace("-", ""); }

        // a stand-in component: a harmless console program named TranslucentTB.exe plus a couple of data files and a manifest
        internal static string Make(string root, string name)
        {
            string dir = Path.Combine(root, name); Directory.CreateDirectory(dir);
            File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), Path.Combine(dir, "TranslucentTB.exe"), true);
            File.WriteAllBytes(Path.Combine(dir, "Xaml.dll"), Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray());
            File.WriteAllBytes(Path.Combine(dir, "ExplorerHooks.dll"), Enumerable.Range(0, 3000).Select(i => (byte)(i * 3 + 1)).ToArray());
            Manifest(dir);
            return dir;
        }

        internal static void Manifest(string dir)
        {
            File.WriteAllText(Path.Combine(dir, "verified.sha256"), string.Join("\r\n", new[] { "ExplorerHooks.dll", "TranslucentTB.exe", "Xaml.dll" }.Select(n => Sha(Path.Combine(dir, n)) + "  " + n)) + "\r\n");
        }

        static TaskbarService Svc(string dir, string cache)
        {
            return new TaskbarService { Dir = dir, CacheDir = cache, Args = "-n 120 127.0.0.1", ExternalRunning = () => false, PackSource = () => null };
        }

        internal static void Kill(TaskbarService s)
        {
            try { int id = s.ProcessId; if (id > 0) using (var p = Process.GetProcessById(id)) { p.Kill(); p.WaitForExit(3000); } } catch { }
            try { s.Dispose(); } catch { }
        }

        internal static void Run(Action<bool, string> check)
        {
            string root = Path.Combine(Path.GetTempPath(), "cd223-tb-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            int oldRetries = TaskbarComponent.BusyRetries, oldDelay = TaskbarComponent.BusyDelayMs;
            bool oldTaskbar = Program.S.Taskbar;
            TaskbarComponent.BusyRetries = 1; TaskbarComponent.BusyDelayMs = 20;
            try
            {
                string dir = Make(root, "taskbar"), cache = Path.Combine(root, "good");

                // 1. healthy component starts and a verified copy is kept
                var a = Svc(dir, cache);
                bool ok = a.TryStart();
                check(ok && a.Failure == null && a.ProcessId > 0 && TaskbarComponent.Verify(cache).Ok, "2.2.3 taskbar component: healthy start, known-good copy kept");

                // 2. killed in Task Manager mid-run: nothing is left that blocks the next start
                Kill(a); check(a.Exited || a.ProcessId == 0, "2.2.3 taskbar component: killed process is seen as exited");
                File.WriteAllText(Path.Combine(root, "recovery-v11.json"), "{\"TaskbarProcessId\":" + 99999999 + ",\"TaskbarPath\":\"" + a.Exe.Replace("\\", "\\\\") + "\"}");   // stale state of the dead session
                bool noThrow = true; try { TaskbarService.CloseOwned(99999999, a.Exe); TaskbarService.CloseOwned(Process.GetCurrentProcess().Id, a.Exe); } catch { noThrow = false; }
                var b = Svc(dir, cache); bool again = b.TryStart();
                check(noThrow && again && TaskbarComponent.Verify(dir).Ok, "2.2.3 taskbar component: after the process was killed (stale recovery state) a new ClearDesk start still works");
                Kill(b);

                // 3. a corrupted file is repaired from the good copy
                File.WriteAllText(Path.Combine(dir, "Xaml.dll"), "garbage");
                check(TaskbarComponent.Verify(dir).State == ComponentState.Mismatch, "2.2.3 taskbar component: corrupted file is reported as a mismatch");
                var c = Svc(dir, cache); bool rep = c.TryStart();
                check(rep && c.Repaired != null && TaskbarComponent.Verify(dir).Ok, "2.2.3 taskbar component: corrupted file restored from the good copy, start succeeds");
                Kill(c);

                // 4. missing exe (AV quarantine) and missing manifest
                File.Delete(Path.Combine(dir, "TranslucentTB.exe"));
                check(TaskbarComponent.Verify(dir).State == ComponentState.Missing, "2.2.3 taskbar component: missing exe is reported as missing");
                var d = Svc(dir, cache); bool rep2 = d.TryStart(); check(rep2 && TaskbarComponent.Verify(dir).Ok, "2.2.3 taskbar component: quarantined/missing exe restored, start succeeds"); Kill(d);
                File.Delete(Path.Combine(dir, "verified.sha256"));
                check(TaskbarComponent.Verify(dir).State == ComponentState.NoManifest, "2.2.3 taskbar component: missing manifest is reported");
                var e1 = Svc(dir, cache); check(e1.TryStart() && TaskbarComponent.Verify(dir).Ok, "2.2.3 taskbar component: missing manifest restored, start succeeds"); Kill(e1);
                File.WriteAllText(Path.Combine(dir, "verified.sha256"), "not a manifest");
                check(TaskbarComponent.Verify(dir).State == ComponentState.Invalid, "2.2.3 taskbar component: invalid manifest is reported");
                var e2 = Svc(dir, cache); check(e2.TryStart() && TaskbarComponent.Verify(dir).Ok, "2.2.3 taskbar component: invalid manifest restored, start succeeds"); Kill(e2);

                // 5. locked file: a file held open (even exclusively) is never a crash; a loaded (share-delete) file is replaced by rename
                string xaml = Path.Combine(dir, "Xaml.dll");
                using (new FileStream(xaml, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var f = Svc(dir, cache); bool r = false; bool threw = false;
                    try { r = f.TryStart(); } catch { threw = true; }
                    check(!threw && !r && f.Failure != null && f.Retryable && f.ProcessId == 0, "2.2.3 taskbar component: exclusively locked file -> no start, no exception, retry later (" + f.Failure + ")");
                }
                var g = Svc(dir, cache); check(g.TryStart(), "2.2.3 taskbar component: once the lock is gone it starts"); Kill(g);
                using (new FileStream(xaml, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    File.WriteAllText(xaml, "bad");
                    var h = Svc(dir, cache); bool r2 = h.TryStart();
                    check(r2 && TaskbarComponent.Verify(dir).Ok, "2.2.3 taskbar component: damaged file that is still open elsewhere is replaced"); Kill(h);
                }

                // 6. nothing to repair from: no crash, a reason, Start() keeps its old throwing contract
                string lone = Make(root, "lone"); File.WriteAllText(Path.Combine(lone, "Xaml.dll"), "bad");
                var i1 = Svc(lone, Path.Combine(root, "nocache")); bool r3 = true, th = false; try { r3 = i1.TryStart(); } catch { th = true; }
                bool threwOld = false; try { i1.Start(); } catch (InvalidOperationException) { threwOld = true; } catch { }
                check(!th && !r3 && !string.IsNullOrEmpty(i1.Failure) && i1.Retryable && threwOld, "2.2.3 taskbar component: damaged and no good copy -> TryStart false with a reason (no exception); Start() still throws for old callers");

                // 7. a stand-alone TranslucentTB already running is a notice, not a failure of ClearDesk
                var j = Svc(dir, cache); j.ExternalRunning = () => true; bool r4 = j.TryStart();
                check(!r4 && j.Failure != null && j.Retryable, "2.2.3 taskbar component: independent TranslucentTB running -> declined with a notice");

                // 8. DesktopServices: startup path never throws, retries after the problem is fixed, and a user kill is respected
                Program.S.Taskbar = true;
                var ds = new DesktopServices { SkipGuardian = true };
                var ts = ds.TaskbarComponentService; ts.Dir = lone; ts.CacheDir = Path.Combine(root, "nocache"); ts.Args = "-n 120 127.0.0.1"; ts.ExternalRunning = () => false; ts.PackSource = () => null;
                bool started = true, thrown = false;
                try { started = ds.StartTaskbar(true); } catch { thrown = true; }
                check(!thrown && !started && !ds.TaskbarActive, "2.2.3 startup: broken optional component does not throw and does not activate the feature");
                Manifest(lone);   // user fixes / reinstalls the component
                bool second = ds.StartTaskbar(false);
                check(second && ds.TaskbarActive, "2.2.3 startup: the retry brings the feature up once the component is fine again");
                try { using (var p = Process.GetProcessById(ts.ProcessId)) { p.Kill(); p.WaitForExit(3000); } } catch { }   // the user ends TranslucentTB in Task Manager
                try { ds.Tick(); } catch { thrown = true; }
                check(!thrown && !ds.TaskbarActive, "2.2.3 runtime: TranslucentTB killed while ClearDesk runs -> ClearDesk keeps running and notes it");
                ts.Dispose();
                try { File.Delete(Path.Combine(Program.Data, "recovery-v11.json")); } catch { }
            }
            catch (Exception ex) { check(false, "2.2.3 taskbar component tests crashed: " + ex); }
            finally
            {
                TaskbarComponent.BusyRetries = oldRetries; TaskbarComponent.BusyDelayMs = oldDelay; Program.S.Taskbar = oldTaskbar;
                foreach (var p in Process.GetProcessesByName("TranslucentTB")) { try { if (p.MainModule.FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase)) p.Kill(); } catch { } }
                Thread.Sleep(300);
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
