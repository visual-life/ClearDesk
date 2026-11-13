using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace ClearDesk
{
    /// <summary>2.2.5: folder widget, widgets survive Win+D / Show desktop, TB-04 busy-at-boot handling, factory copy outside the exe.</summary>
    internal static class V225Tests
    {
        internal static void Rules(Action<bool, string> check)
        {
            check(SettingsWindow.Version.StartsWith("2."), "2.2.5 version string (" + SettingsWindow.Version + ")");
            check(ToolWidgets.Kinds.Any(k => k.Key == "folder") && ToolWidgets.IsMulti("folder"), "2.2.5 folder widget: registered as a widget kind with several instances");
            check(DesktopServices.BalloonTitle("TB-04").Contains("被占用") && DesktopServices.BalloonTitle("TB-03").Contains("不一致") && DesktopServices.BalloonTitle("TB-04") != DesktopServices.BalloonTitle("TB-03"), "2.2.5 TB-04: the notice title tells 'in use' from 'content differs'");
            check(DesktopServices.BusyBackoffSeconds.SequenceEqual(new[] { 1, 2, 4, 8, 15, 30 }), "2.2.5 TB-04: silent retry backoff is 1,2,4,8,15,30 s");
            var asm = typeof(V225Tests).Assembly;
            check(asm.GetManifestResourceNames().Contains(ComponentPack.AnchorResource) && !asm.GetManifestResourceNames().Contains(ComponentPack.ResourceName), "2.2.5 360: ClearDesk.exe no longer carries the TranslucentTB files (only their manifest)");
            var fv = FileVersionInfo.GetVersionInfo(asm.Location);
            check(fv.FileVersion == SettingsWindow.Version + ".0" && fv.CompanyName == "visual-life" && fv.ProductName == "ClearDesk" && !string.IsNullOrEmpty(fv.LegalCopyright), "2.2.5 360: the exe has a complete version resource (" + fv.FileVersion + ", " + fv.CompanyName + ")");
            Classify(check);
            FolderRules(check);
        }

        internal static async Task Run(Action<bool, string> check)
        {
            await Task.Yield();
            Taskbar(check);
            await Folder(check);
            await Battery(check);
            await ShowDesktop(check);
        }

        static async Task Battery(Action<bool, string> check)
        {
            check(ToolWidgets.Kinds.Any(k => k.Key == "battery"), "2.2.5 battery widget: registered");
            check(BatteryWindow.Columns(180) == 1 && BatteryWindow.Columns(450) == 2, "2.2.5 battery widget: columns follow the width");
            BatteryWindow w = null;
            try
            {
                w = Program.NewWidget("battery") as BatteryWindow;
                w.Source = () => new List<BatteryReading> {
                    new BatteryReading { Key = "a", Name = "Test Mouse", Kind = "罗技接收器", Percent = 15, Source = "test" },
                    new BatteryReading { Key = "b", Name = "Test Keyboard", Kind = "USB 接收器 / 有线", Source = "test", Note = "无报告" } };
                check(!w.Data.LowAlert, "2.2.5 battery widget: low-battery notice is off by default");
                int s0 = w.Scans; w.Refresh();
                for (int i = 0; i < 30 && w.Scans == s0; i++) await Task.Delay(100);
                await Task.Delay(200);
                var texts = w.Rows.Children.OfType<Border>().Select(b => b.Tag as BatteryReading).ToList();
                check(texts.Count == 2 && !texts[1].Readable && texts[0].Percent == 15, "2.2.5 battery widget: readable and unreadable devices are listed; unreadable has no value");
                int b0 = Program.Balloons; w.Refresh(); for (int i = 0; i < 30 && Program.Balloons == b0 && w.Scans < s0 + 2; i++) await Task.Delay(100);
                check(Program.Balloons == b0, "2.2.5 battery widget: no low-battery notice while the option is off");
                w.Data.ShowUnsupported = false; w.Render();
                check(w.Rows.Children.Count == 1, "2.2.5 battery widget: unsupported devices can be hidden");
                w.Width = 500; await Task.Delay(300);
                check(Math.Abs(w.ActualWidth - 500) < 2, "2.2.5 battery widget: width is free");
                var menuText = string.Join("|", w.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>().Select(m => m.Header as string ?? ""));
                check(menuText.Contains("无边框") && menuText.Contains("低电量提示"), "2.2.5 battery widget: menu has frameless / background / low-battery option");
                var real = await Task.Run(() => DeviceBattery.Scan());
                check(real.All(r => r.Readable || r.Percent == null), "INFO: real devices on this PC: " + (real.Count == 0 ? "none" : string.Join(" | ", real.Select(r => r.ToString()))));
            }
            catch (Exception ex) { check(false, "2.2.5 battery widget tests crashed: " + ex); }
            finally { if (w != null) try { string id = w.TestState.Id; w.Close(); ToolWidgets.ForgetData(id); Program.S.Widgets.Remove(id); Program.Save(); } catch { } }
        }

        static string Temp(string name) { string d = Path.Combine(Path.GetTempPath(), "cd225-" + name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(d); return d; }
        static void Rm(string d) { try { Directory.Delete(d, true); } catch { } }

        static void Classify(Action<bool, string> check)
        {
            TaskbarProcesses.Provider = () => new List<RunningCopy> {
                new RunningCopy { Pid = 1, Path = @"C:\Program Files\WindowsApps\28017CharlesMilette.TranslucentTB_2024.3.0.0_x64__v826wp6bftszj\TranslucentTB.exe" },
                new RunningCopy { Pid = 2, Path = @"C:\work\ClearDesk-2.2.5\taskbar\TranslucentTB.exe" },
                new RunningCopy { Pid = 3, Path = @"C:\Tools\TranslucentTB\TranslucentTB.exe" } };
            try
            {
                var l = TaskbarProcesses.List(@"C:\work\ClearDesk-2.2.5\taskbar");
                check(l[0].Store && !l[0].Own && l[1].Own && !l[1].Store && !l[2].Own && !l[2].Store, "2.2.5 TB-04: running TranslucentTB copies are classified (Store version / ClearDesk's own folder / elsewhere)");
                check(TaskbarProcesses.Describe(l[0]).Contains("商店版") && TaskbarProcesses.Describe(l[1]).Contains("ClearDesk 自己"), "2.2.5 TB-04: the description names the Store version and ClearDesk's own copy");
            }
            finally { TaskbarProcesses.Provider = null; }
        }

        // ================================================================ TB-04
        static void Taskbar(Action<bool, string> check)
        {
            string root = Temp("tb");
            int oldRetries = TaskbarComponent.BusyRetries, oldDelay = TaskbarComponent.BusyDelayMs; var oldBackoff = DesktopServices.BusyBackoffSeconds; bool oldTaskbar = Program.S.Taskbar;
            TaskbarComponent.BusyRetries = 1; TaskbarComponent.BusyDelayMs = 20;
            var none = (Func<List<RunningCopy>>)(() => new List<RunningCopy>());
            try
            {
                string factory = V223TaskbarTests.Make(root, "factory");
                var pack = ComponentPack.FromFolder(factory);

                // factory copy outside the exe: only used when it matches the compiled-in manifest
                string anchor = File.ReadAllText(Path.Combine(factory, "verified.sha256")) + "version: 1.0\r\n";
                string zip = Path.Combine(root, "taskbar-factory.zip");
                using (var z = ZipFile.Open(zip, ZipArchiveMode.Create)) foreach (var f in Directory.GetFiles(factory)) z.CreateEntryFromFile(f, Path.GetFileName(f));
                var good = ComponentPack.Build(anchor, zip);
                check(good != null && good.HasFiles && good.SelfCheck() && good.Origin == zip, "2.2.5 factory copy: a taskbar-factory.zip matching the compiled-in manifest is used");
                var mo = ComponentPack.Build(anchor, Path.Combine(root, "missing.zip"));
                check(mo != null && !mo.HasFiles && !mo.SelfCheck() && mo.Manifest.Count == 3 && mo.Version == "1.0", "2.2.5 factory copy: without the zip the manifest is still the reference (cannot repair)");
                string other = V223TaskbarTests.Make(root, "foreign"); File.WriteAllText(Path.Combine(other, "Xaml.dll"), "x"); V223TaskbarTests.Manifest(other);
                string fz = Path.Combine(root, "foreign.zip");
                using (var z = ZipFile.Open(fz, ZipArchiveMode.Create)) foreach (var f in Directory.GetFiles(other)) z.CreateEntryFromFile(f, Path.GetFileName(f));
                var fp = ComponentPack.Build(anchor, fz);
                check(fp != null && !fp.HasFiles && fp.Origin.Contains("不符"), "2.2.5 factory copy: a foreign / swapped zip is not used");
                var mv = TaskbarComponent.Verify(factory, mo);
                check(mv.Ok, "2.2.5 factory copy: verification against the manifest-only pack works");

                // an exclusively locked TranslucentTB.exe (what antivirus scanning at boot looks like)
                string locked = V223TaskbarTests.Make(root, "locked");
                string exe = Path.Combine(locked, "TranslucentTB.exe");
                string before = V223TaskbarTests.Sha(exe); DateTime mtime = File.GetLastWriteTimeUtc(exe);
                string run = Path.Combine(root, "taskbar-run");
                using (var hold = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var s = new TaskbarService { Dir = locked, CacheDir = Path.Combine(root, "nocache"), RunDirectory = run, Args = "-n 60 127.0.0.1", ExternalRunning = () => false, RunningCopies = none, PackSource = () => pack, AllowUnverified = () => false };
                    bool st = s.TryStart();
                    check(!st && s.Busy && s.Code == "TB-04" && s.BusyFile == "TranslucentTB.exe", "2.2.5 TB-04: an exclusively locked exe is 'busy' (TB-04), not a mismatch (code " + s.Code + ")");
                    var lockers = FileLockers.Of(exe);
                    check(lockers.Any(h => h.Pid == Process.GetCurrentProcess().Id), "2.2.5 TB-04: Restart Manager names the process that holds the file (" + string.Join("; ", lockers.Select(h => h.ToString())) + ")");
                    string sig = Authenticode.Status(exe, out string signer);
                    check(sig.StartsWith("未知") && !sig.StartsWith("无签名"), "2.2.5 TB-04: the signature of an unreadable file is 'unknown', not 'no signature' (" + sig + ")");
                    bool rc = s.TryStartRunCopy();
                    check(rc && s.RunFrom == run && s.Exe.StartsWith(run, StringComparison.OrdinalIgnoreCase) && TaskbarComponent.Verify(run, pack).Ok && s.Repaired != null, "2.2.5 TB-04: after the retries the factory copy is extracted to the per-user folder and started from there (" + s.Failure + ")");
                    V223TaskbarTests.Kill(s);
                }
                check(V223TaskbarTests.Sha(exe) == before && File.GetLastWriteTimeUtc(exe) == mtime, "2.2.5 TB-04: the locked file itself was not touched");

                // the real signed TranslucentTB.exe, locked: 2.2.4 said 'no signature'
                string real = @"C:\work\ClearDesk-2.2.4\taskbar\TranslucentTB.exe";
                if (File.Exists(real))
                {
                    string copy = Path.Combine(root, "signed.exe"); File.Copy(real, copy);
                    string free = Authenticode.Status(copy, out string sg1);
                    string busy; using (new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.None)) busy = Authenticode.Status(copy, out string sg2);
                    check(free.StartsWith("有效") && busy.StartsWith("未知"), "2.2.5 TB-04: real signed TranslucentTB.exe: readable -> " + free + "; exclusively locked -> " + busy);
                }
                else check(true, "INFO: real TranslucentTB.exe not found, signed-locked check skipped");

                // adopt: a TranslucentTB already running from the component folder is normal
                string mine = V223TaskbarTests.Make(root, "running");
                var p = Process.Start(new ProcessStartInfo(Path.Combine(mine, "TranslucentTB.exe"), "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true });
                try
                {
                    Thread.Sleep(300);
                    var a = new TaskbarService { Dir = mine, CacheDir = Path.Combine(root, "nocache"), RunDirectory = run, Args = "-n 60 127.0.0.1", ExternalRunning = () => true, PackSource = () => pack, AllowUnverified = () => false };
                    bool ad = a.TryStart();
                    check(ad && a.Adopted && a.ProcessId == p.Id && a.Code == null, "2.2.5 TB-04: a TranslucentTB running from ClearDesk's own folder is taken over as normal (no TB-07 / TB-04)");
                    var b = new TaskbarService { Dir = Path.Combine(root, "elsewhere"), CacheDir = Path.Combine(root, "nocache"), RunDirectory = Path.Combine(root, "run2"), Args = "", ExternalRunning = () => true, PackSource = () => pack, AllowUnverified = () => false };
                    bool bd = b.TryStart();
                    check(!bd && b.Code == "TB-07" && b.Failure.Contains(mine), "2.2.5 TB-07: a copy running elsewhere is reported with its path");
                    a.Dispose();
                }
                finally { try { p.Kill(); p.WaitForExit(3000); } catch { } }

                // DesktopServices: silent retries, then the per-user copy; no balloon until it really fails
                Program.S.Taskbar = true;
                DesktopServices.BusyBackoffSeconds = new[] { 0, 0 };
                using (var hold = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var ds = new DesktopServices { SkipGuardian = true };
                    var ts = ds.TaskbarComponentService; ts.Dir = locked; ts.CacheDir = Path.Combine(root, "nocache"); ts.RunDirectory = Path.Combine(root, "run3"); ts.Args = "-n 60 127.0.0.1"; ts.ExternalRunning = () => false; ts.RunningCopies = none; ts.PackSource = () => pack;
                    int b0 = Program.Balloons;
                    bool r1 = ds.StartTaskbar(true), r2 = ds.StartTaskbar(false);
                    check(!r1 && !r2 && ds.BusyTries == 2 && Program.Balloons == b0 && ds.LastTaskbarCode == "TB-04", "2.2.5 TB-04: while the file is busy ClearDesk retries silently (no notice)");
                    bool r3 = ds.StartTaskbar(false);
                    check(r3 && ds.TaskbarActive && Program.Balloons == b0 && ts.RunFrom == ts.RunDirectory, "2.2.5 TB-04: when the retries run out it starts from the per-user copy, still without a notice");
                    check(TaskbarTimeline.Count >= 4 && TaskbarTimeline.Text.Contains("重试") && TaskbarTimeline.Text.Contains("备用目录"), "2.2.5 TB-04: the retry timeline is recorded:" + Environment.NewLine + TaskbarTimeline.Text);
                    V223TaskbarTests.Kill(ts);

                    var ds2 = new DesktopServices { SkipGuardian = true };
                    var t2 = ds2.TaskbarComponentService; t2.Dir = locked; t2.CacheDir = Path.Combine(root, "nocache"); t2.RunDirectory = Path.Combine(root, "run4"); t2.Args = "-n 60 127.0.0.1"; t2.ExternalRunning = () => false; t2.RunningCopies = none; t2.PackSource = () => mo;
                    int b1 = Program.Balloons;
                    ds2.StartTaskbar(true); ds2.StartTaskbar(false); bool f3 = ds2.StartTaskbar(false);
                    check(!f3 && Program.Balloons == b1 + 1 && Program.LastBalloon.Contains("被占用") && ds2.LastTaskbarCode == "TB-04", "2.2.5 TB-04: only when nothing works one notice says 'in use' (" + Program.LastBalloon + ")");
                    t2.Dispose();

                    // held by ANOTHER ClearDesk (or its TranslucentTB): normal -> no notice, no second TranslucentTB
                    var ds3 = new DesktopServices { SkipGuardian = true };
                    var t3 = ds3.TaskbarComponentService; t3.Dir = locked; t3.CacheDir = Path.Combine(root, "nocache"); t3.RunDirectory = Path.Combine(root, "run5"); t3.Args = "-n 60 127.0.0.1"; t3.ExternalRunning = () => false; t3.RunningCopies = none; t3.PackSource = () => pack;
                    DesktopServices.HoldersOf = f => new List<FileLockers.Holder> { new FileLockers.Holder { Pid = 999999, Name = "ClearDesk", Path = @"C:\work\ClearDesk-2.2.2\ClearDesk.exe" } };
                    int b2 = Program.Balloons;
                    bool g1 = ds3.StartTaskbar(true), g2 = ds3.StartTaskbar(false), g3 = ds3.StartTaskbar(false);
                    DesktopServices.HoldersOf = FileLockers.Of;
                    check(!g1 && !g2 && !g3 && Program.Balloons == b2 && ds3.LastTaskbarCode == null && t3.ProcessId == 0 && !Directory.Exists(t3.RunDirectory), "2.2.5 TB-04: a file held by another ClearDesk instance is normal: no notice and no second TranslucentTB");
                    t3.Dispose();
                }
                check(Program.QuietRun, "2.2.5 tests: this test run shows no tray notices on the user's desktop");
                check(DesktopServices.RoleName.EndsWith(".main") == false, "2.2.5 tests: the test run uses its own taskbar-manager mutex (" + DesktopServices.RoleName + "), never the user's instance's");
                try { File.Delete(Path.Combine(Program.Data, "recovery-v11.json")); } catch { }
            }
            catch (Exception ex) { check(false, "2.2.5 taskbar tests crashed: " + ex); }
            finally
            {
                TaskbarComponent.BusyRetries = oldRetries; TaskbarComponent.BusyDelayMs = oldDelay; DesktopServices.BusyBackoffSeconds = oldBackoff; Program.S.Taskbar = oldTaskbar;
                foreach (var p in Process.GetProcessesByName("TranslucentTB")) { try { if (p.MainModule.FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase)) p.Kill(); } catch { } }
                Thread.Sleep(300); Rm(root);
            }
        }

        // ================================================================ folder widget
        static void FolderRules(Action<bool, string> check)
        {
            string d = Temp("list");
            try
            {
                Directory.CreateDirectory(Path.Combine(d, "b dir")); Directory.CreateDirectory(Path.Combine(d, "A dir"));
                File.WriteAllText(Path.Combine(d, "file10.txt"), ""); File.WriteAllText(Path.Combine(d, "file2.txt"), "");
                string h = Path.Combine(d, "hidden.txt"); File.WriteAllText(h, ""); File.SetAttributes(h, FileAttributes.Hidden);
                int total; string err;
                var l = FolderLogic.List(d, false, out total, out err);
                check(err == null && l.Select(e => e.Name).SequenceEqual(new[] { "A dir", "b dir", "file2.txt", "file10.txt" }), "2.2.5 folder widget: folders first, natural order, hidden files left out (" + string.Join(",", l.Select(e => e.Name)) + ")");
                check(FolderLogic.List(d, true, out total, out err).Count == 5, "2.2.5 folder widget: 'show hidden files' shows them");
                FolderLogic.List(Path.Combine(d, "nope"), false, out total, out err);
                check(err != null, "2.2.5 folder widget: a missing folder gives a message, no crash");
                check(FolderLogic.Columns(380, 44) < FolderLogic.Columns(760, 44), "2.2.5 folder widget: wider widget -> more columns");
            }
            finally { Rm(d); }
        }

        static async Task Folder(Action<bool, string> check)
        {
            string d = Temp("fw");
            var opened = new List<GlassWindow>();
            try
            {
                for (int i = 0; i < 12; i++) File.WriteAllText(Path.Combine(d, "item" + i + ".txt"), "x");
                Directory.CreateDirectory(Path.Combine(d, "sub"));
                var w1 = Program.NewWidget("folder") as FolderWindow; opened.Add(w1);
                var w2 = Program.NewWidget("folder") as FolderWindow; opened.Add(w2);
                check(w1 != null && w2 != null && w1.TestState.Id != w2.TestState.Id, "2.2.5 folder widget: several instances with their own ids (" + (w1 == null ? "" : w1.TestState.Id) + ", " + (w2 == null ? "" : w2.TestState.Id) + ")");
                await Task.Delay(300);
                check(w1.Shown == 0, "2.2.5 folder widget: a new one shows the 'choose a folder' prompt");
                w1.SetFolder(d);
                await Task.Delay(400);
                check(w1.Shown == 13 && w1.Data.Path == d, "2.2.5 folder widget: shows the files and the subfolder (" + w1.Shown + ")");
                check(w1.Scroller.VerticalScrollBarVisibility == ScrollBarVisibility.Hidden, "2.2.5 folder widget: no visible scroll bar (wheel scrolls)");
                var stored = ToolStore.Load<FolderData>(w1.TestState.Id);
                check(stored.Path == d, "2.2.5 folder widget: the chosen folder is saved for this instance");

                // live updates
                int r0 = w1.Refreshes;
                File.WriteAllText(Path.Combine(d, "new.txt"), "x");
                for (int i = 0; i < 30 && w1.Shown != 14; i++) await Task.Delay(100);
                check(w1.Shown == 14 && w1.Refreshes > r0, "2.2.5 folder widget: a new file appears by itself (FileSystemWatcher)");
                File.Delete(Path.Combine(d, "item0.txt"));
                for (int i = 0; i < 30 && w1.Shown != 13; i++) await Task.Delay(100);
                check(w1.Shown == 13, "2.2.5 folder widget: a deleted file disappears by itself");

                // reflow on resize (tile view; 2.2.6 made the list view the default)
                w1.SetView("icons"); await Task.Delay(300);
                w1.Width = 300; await Task.Delay(300);
                double y300 = Rows(w1);
                w1.Width = 700; await Task.Delay(300);
                double y700 = Rows(w1);
                check(y700 < y300, "2.2.5 folder widget: wider -> fewer rows (tiles reflow) " + y300 + " -> " + y700);
                w1.Height = 420; await Task.Delay(200);
                check(Math.Abs(w1.ActualHeight - 420) < 2, "2.2.5 folder widget: height is free");

                // opening
                string openedPath = null; w1.Open = p => openedPath = p;
                var tile = (Border)w1.Tiles.Children[0];
                var mi = (MenuItem)tile.ContextMenu.Items[0];
                mi.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                check(openedPath == ((FolderEntry)tile.Tag).Path, "2.2.5 folder widget: '打开' opens the item (" + openedPath + ")");
                var menuText = string.Join("|", w1.ContextMenu.Items.OfType<MenuItem>().Select(m => m.Header as string ?? ""));
                check(menuText.Contains("无边框") && menuText.Contains("背景") && menuText.Contains("选择文件夹"), "2.2.5 folder widget: widget menu has frameless / background / choose folder (" + menuText + ")");
            }
            catch (Exception ex) { check(false, "2.2.5 folder widget tests crashed: " + ex); }
            finally
            {
                foreach (var w in opened) try { if (w != null) { string id = w.TestState.Id; w.Close(); ToolWidgets.ForgetData(id); Program.S.Widgets.Remove(id); } } catch { }
                Program.Save();
                await Task.Delay(200); Rm(d);
            }
        }

        static double Rows(FolderWindow w)
        {
            var last = w.Tiles.Children[w.Tiles.Children.Count - 1] as FrameworkElement;
            return last.TransformToAncestor(w.Tiles).Transform(new Point(0, 0)).Y;
        }

        // ================================================================ Win+D / Show desktop
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);

        static void Toggle()
        {
            // Shell.Application.ToggleDesktop through COM, from a separate process (called on the test's own UI thread Explorer ignored it)
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"(New-Object -ComObject Shell.Application).ToggleDesktop()\"") { UseShellExecute = false, CreateNoWindow = true };
            using (var p = Process.Start(psi)) p.WaitForExit(15000);
        }

        [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        static string ZOrder(params GlassWindow[] ws)
        {
            var mine = ws.Select(w => new WindowInteropHelper(w).Handle).ToList();
            var sb = new StringBuilder(); int i = 0, vis = 0;
            for (IntPtr h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero && i < 3000; h = GetWindow(h, 2), i++)
            {
                if (!IsWindowVisible(h)) continue; vis++;
                string c = Native.Class(h);
                int k = mine.IndexOf(h);
                if (k >= 0) sb.Append(" [" + vis + ":widget" + k + "]");
                else if (c == "Progman" || c == "WorkerW") sb.Append(" [" + vis + ":" + c + "]");
            }
            return sb.Append(" of " + vis).ToString();
        }

        static bool OnTop(GlassWindow w)
        {
            var h = new WindowInteropHelper(w).Handle;
            var src = PresentationSource.FromVisual(w);
            var c = w.PointToScreen(new Point(w.ActualWidth / 2, Math.Min(20, w.ActualHeight / 2)));
            var hit = WindowFromPoint(new POINT { X = (int)c.X, Y = (int)c.Y });
            return GetAncestor(hit, 2) == h;
        }

        /// <summary>Opens a free and a locked widget, writes their window handles to %TEMP%\cd225-desk.txt and keeps them 40 s for an external ToggleDesktop check.</summary>
        internal static async Task Hold(Action<bool, string> check)
        {
            var free = Program.NewWidget("clock"); var pinned = Program.NewWidget("folder");
            pinned.Left = free.Left; pinned.Top = free.Top + free.ActualHeight + 30; pinned.TestState.Locked = true;
            await Task.Delay(800);
            string f = Path.Combine(Path.GetTempPath(), "cd225-desk.txt");
            File.WriteAllText(f, new WindowInteropHelper(free).Handle.ToInt64() + " " + new WindowInteropHelper(pinned).Handle.ToInt64() + " " + DesktopKeeper.Hooked);
            for (int i = 0; i < 80 && File.Exists(f); i++) await Task.Delay(500);
            check(true, "INFO: hold done, raises " + DesktopKeeper.Raises + ", lowers " + DesktopKeeper.Lowers);
            foreach (var w in new[] { free, pinned }) try { string id = w.TestState.Id; w.Close(); ToolWidgets.ForgetData(id); Program.S.Widgets.Remove(id); } catch { }
            Program.Save();
        }

        static async Task ShowDesktop(Action<bool, string> check)
        {
            check(DesktopKeeper.IsDesktop(IntPtr.Zero) != true, "2.2.5 show desktop: no window is not the desktop");
            var opened = new List<GlassWindow>();
            bool toggled = false;
            try
            {
                var free = Program.NewWidget("clock"); opened.Add(free);
                var pinned = Program.NewWidget("folder"); opened.Add(pinned);
                pinned.Left = free.Left; pinned.Top = free.Top + free.ActualHeight + 30;
                pinned.TestState.Locked = true;
                await Task.Delay(600);
                check(free.IsVisible && pinned.IsVisible, "INFO: show desktop test widgets visible before; z-order normal: " + ZOrder(free, pinned));
                // control: the same Show desktop with the keeper switched off (what 2.2.4 did)
                bool wasHooked = DesktopKeeper.Hooked;
                DesktopKeeper.Stop();
                var seen = new List<string>();
                var tt = Task.Run(() => Toggle()); toggled = true;
                for (int i = 0; i < 25; i++) { await Task.Delay(100); string cn = Native.Class(Native.GetForegroundWindow()); if (seen.Count == 0 || seen[seen.Count - 1] != cn) seen.Add(i * 100 + "ms " + cn); }
                await tt;
                check(true, "INFO: foreground sequence after ToggleDesktop: " + string.Join(" > ", seen));
                var cfg = Native.GetForegroundWindow();
                check(true, "INFO: z-order shown: " + ZOrder(free, pinned));
                check(true, "INFO: control: desktop raised over " + DesktopKeeper.Below(DesktopKeeper.DesktopWindow()).Count + " windows (0-2 = show desktop had no effect this time)");
                check(true, "INFO: control without keeper: foreground " + Native.Class(cfg) + ", clock on top " + OnTop(free) + ", locked on top " + OnTop(pinned) + ", minimized " + IsIconic(new WindowInteropHelper(free).Handle));
                await Task.Run(() => Toggle()); toggled = false; await Task.Delay(1500);
                DesktopKeeper.Start();
                check(true, "INFO: keeper hook was installed by startup: " + wasHooked + ", now " + DesktopKeeper.Hooked);
                int r0 = DesktopKeeper.Raises;
                await Task.Run(() => Toggle()); toggled = true;
                await Task.Delay(1500);
                var fg = Native.GetForegroundWindow();
                check(true, "INFO: after ToggleDesktop foreground " + Native.Class(fg) + " is desktop: " + DesktopKeeper.IsDesktop(fg) + ", raises " + (DesktopKeeper.Raises - r0));
                var under = DesktopKeeper.Below(DesktopKeeper.DesktopWindow());
                check(true, "INFO: with keeper: z-order " + ZOrder(free, pinned) + ", desktop raised over " + under.Count + " windows");
                check(free.IsVisible && !IsIconic(new WindowInteropHelper(free).Handle) && !under.Contains(new WindowInteropHelper(free).Handle), "2.2.5 show desktop (Shell.Application.ToggleDesktop): an ordinary widget is not below the desktop");
                check(pinned.IsVisible && !IsIconic(new WindowInteropHelper(pinned).Handle) && !under.Contains(new WindowInteropHelper(pinned).Handle), "2.2.5 show desktop: a widget locked to the bottom is not below the desktop either");
                foreach (var dock in Program.OpenWindows.Where(x => x is DockWindow || x is LaunchpadWindow))
                    check(true, "INFO: " + dock.GetType().Name + " visible after show desktop: " + dock.IsVisible + ", minimized: " + IsIconic(new WindowInteropHelper(dock).Handle));
                double left = free.Left, top = free.Top;
                await Task.Run(() => Toggle()); toggled = false;
                await Task.Delay(1500);
                check(free.Left == left && free.Top == top && free.IsVisible, "2.2.5 show desktop: restoring the windows does not move the widgets");
                check(!DesktopKeeper.Raised || DesktopKeeper.IsDesktop(Native.GetForegroundWindow()) == true, "INFO: widgets lowered again after restore: raised=" + DesktopKeeper.Raised);
                // simulated sequence (independent of what had the focus): desktop raised -> an app becomes foreground -> locked one goes back to the bottom
                DesktopKeeper.Raise();
                check(pinned.DesktopLayer && !pinned.PinnedToBottom, "2.2.5 show desktop: while the desktop is in front the locked widget is lifted with the others");
                DesktopKeeper.Lower(IntPtr.Zero);
                check(!pinned.DesktopLayer && pinned.PinnedToBottom && !DesktopKeeper.Raised, "2.2.5 show desktop: afterwards the locked widget is pinned to the bottom again");
            }
            catch (Exception ex) { check(false, "2.2.5 show desktop test crashed: " + ex); }
            finally
            {
                if (toggled) try { await Task.Run(() => Toggle()); await Task.Delay(800); } catch { }
                foreach (var w in opened) try { string id = w.TestState.Id; w.Close(); ToolWidgets.ForgetData(id); Program.S.Widgets.Remove(id); } catch { }
                Program.Save();
            }
        }
    }
}
