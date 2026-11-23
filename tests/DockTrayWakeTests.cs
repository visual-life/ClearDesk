using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClearDesk
{
    internal static class DockTrayWakeTests
    {
        static byte[] Pic(byte red, byte blue)
        {
            byte[] pixels = new byte[24 * 24 * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = blue;
                pixels[i + 2] = red;
                pixels[i + 3] = 255;
            }
            var source = BitmapSource.Create(24, 24, 96, 96, PixelFormats.Bgra32, null, pixels, 24 * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var stream = new MemoryStream()) { encoder.Save(stream); return stream.ToArray(); }
        }

        static AppIdentity Identity(string path)
        {
            return new AppIdentity { Exe = AppIdentity.Normalize(path), Key = path, LaunchPath = path, Name = Path.GetFileNameWithoutExtension(path) };
        }

        internal static void Rules(Action<bool, string> check)
        {
            var qq = Identity(@"C:\test\Tencent\QQ.exe");
            var wechat = Identity(@"C:\test\Tencent\Weixin.exe");
            var music = Identity(@"C:\test\Tencent\QQMusic.exe");
            check(DockTrayWake.IsKnown(qq) && DockTrayWake.IsKnown(wechat) && DockTrayWake.IsKnown(Identity(@"C:\test\WeChat.exe"))
                && !DockTrayWake.IsKnown(music), "Dock recognizes QQ and both WeChat executable names without matching QQ Music");

            var target = new DockTrayWake.Target { Labels = new[] { "QQ", "腾讯QQ" } };
            target.Processes[7] = qq.Exe;
            var entries = new List<NotifyEntry>
            {
                new NotifyEntry { Exe = qq.Exe, Tooltip = "", Snapshot = Pic(255, 0), Running = true },
                new NotifyEntry { Exe = music.Exe, Tooltip = "QQ 音乐", Snapshot = Pic(0, 255), Running = true }
            };
            check(DockTrayWake.MatchesIcon(target, new TrayAccess.RawIcon { Full = "QQ：已登录" }, entries)
                && !DockTrayWake.MatchesIcon(target, new TrayAccess.RawIcon { Full = "QQ 音乐" }, entries),
                "Dock recognizes a QQ account tooltip and rejects a similarly named tray icon");
            check(DockTrayWake.MatchesIcon(target, new TrayAccess.RawIcon { Full = "", Png = Pic(255, 0) }, entries)
                && !DockTrayWake.MatchesIcon(target, new TrayAccess.RawIcon { Full = "", Png = Pic(0, 255) }, entries),
                "Dock can identify a nameless QQ icon by its executable's registered tray image");
            check(!DockTrayWake.MatchesIcon(target, new TrayAccess.RawIcon { Full = "" }, entries),
                "Dock never activates an unnamed tray icon based only on elimination");
            entries.Add(new NotifyEntry { Exe = @"C:\other\other.exe", Running = true, Snapshot = Pic(255, 0) });
            check(!DockTrayWake.MatchesIcon(target, new TrayAccess.RawIcon { Full = "", Png = Pic(255, 0) }, entries),
                "identical pictures from unrelated tray owners are treated as ambiguous");
            entries[2].Tooltip = "QQ：已登录";
            check(!DockTrayWake.MatchesIcon(target, new TrayAccess.RawIcon { Full = "QQ：已登录" }, entries),
                "a tray label claimed by another running executable is not clicked");
            target.OtherInstall = true;
            check(!DockTrayWake.MatchesIcon(target, new TrayAccess.RawIcon { Full = "QQ" }, new List<NotifyEntry>()),
                "matching a known label alone is not enough when two installations are running");

            target = new DockTrayWake.Target { Labels = new[] { "微信", "Weixin", "WeChat" } };
            target.Processes[19] = wechat.Exe;
            check(DockTrayWake.MatchesIcon(target, new TrayAccess.RawIcon { Full = "微信" }, entries)
                && !DockTrayWake.MatchesIcon(target, new TrayAccess.RawIcon { Full = "微信键盘" }, entries),
                "the WeChat tray label does not accidentally match WeChat Keyboard");

            var visible = new RunningApp { Identity = qq, Title = "QQ" };
            visible.WindowProcesses[(IntPtr)9] = 7;
            visible.Windows.Add((IntPtr)9);
            var groups = new Dictionary<string, RunningApp> { [qq.Key] = visible };
            var saved = new Dictionary<string, RunningApp>();
            RunningApps.KeepTrayResidents(groups, saved, (pid, exe) => true);
            groups.Clear();
            RunningApps.KeepTrayResidents(groups, saved, (pid, exe) => pid == 7 && exe == qq.Exe);
            var resident = groups[qq.Key];
            using (var monitor = new RunningApps())
            {
                monitor.Apps = groups.Values.ToList();
                var icon = monitor.Entries(new string[0]).Single();
                check(icon.IsRunning && !icon.Pinned && resident.Windows.Count == 0 && resident.InTray,
                    "a previously opened QQ stays as a clickable temporary Dock icon after hiding to the tray");
            }
            groups.Clear();
            RunningApps.KeepTrayResidents(groups, saved, (pid, exe) => false);
            check(groups.Count == 0 && saved.Count == 0, "exiting the actual process removes the retained Dock icon");
        }

        static async Task Route(TrayWakeResult result, Action<bool, string> check)
        {
            var entry = new DockEntry { Identity = Identity(@"C:\test\Tencent\Weixin.exe"), PinPath = @"C:\test\WeChat.lnk" };
            using (var running = new RunningApps())
            {
                var requested = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var notice = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var found = new TaskCompletionSource<DockEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
                int attempts = 0;
                running.WakeTray = (clicked, observed) => { attempts++; found.TrySetResult(clicked); return result; };
                running.OpenLaunchItem = path => { requested.TrySetResult(path); return Task.CompletedTask; };
                running.ActivationNotice += message => notice.TrySetResult(message);
                running.ActivateOrLaunch(entry);
                running.ActivateOrLaunch(entry);
                await Task.WhenAny(found.Task, Task.Delay(1500));
                check(found.Task.IsCompleted && ReferenceEquals(found.Task.Result, entry) && attempts == 1,
                    "a Dock click checks tray activation once while that app request is pending");
                if (result == TrayWakeResult.NotApplicable)
                {
                    await Task.WhenAny(requested.Task, Task.Delay(1000));
                    check(requested.Task.IsCompleted && requested.Task.Result == entry.PinPath, "an exited app still starts using the original Dock shortcut");
                }
                else if (result == TrayWakeResult.Unavailable)
                {
                    await Task.WhenAny(notice.Task, Task.Delay(1000));
                    check(notice.Task.IsCompleted && !requested.Task.IsCompleted, "a running tray app with an unavailable icon does not open another login");
                }
                else
                {
                    await Task.Delay(130);
                    check(!requested.Task.IsCompleted && !notice.Task.IsCompleted, "a successful tray wake bypasses the launcher");
                }
            }
        }

        internal static async Task Run(Action<bool, string> check)
        {
            Rules(check);
            await CheckRealProcessIdentity(check);
            await Route(TrayWakeResult.Woken, check);
            await Route(TrayWakeResult.Unavailable, check);
            await Route(TrayWakeResult.NotApplicable, check);
        }

        static async Task CheckRealProcessIdentity(Action<bool, string> check)
        {
            string folder = Path.Combine(Program.Base, "tray-identity-fixture");
            Directory.CreateDirectory(folder);
            string exe = Path.Combine(folder, "Weixin.exe");
            File.Copy(Path.Combine(Program.Base, "ClearDesk.exe"), exe, true);
            File.Copy(Path.Combine(Program.Base, "ClearDesk.exe.config"), exe + ".config", true);
            using (var child = Process.Start(new ProcessStartInfo(exe, "--dock-fixture --hidden-fixture") { UseShellExecute = false }))
            try
            {
                string marker = Path.Combine(folder, "dock-fixture-" + child.Id + ".hwnd");
                for (int i = 0; i < 60 && !File.Exists(marker); i++) await Task.Delay(50);
                check(File.Exists(marker), "a real background process fixture with a WeChat-style executable name started");
                var target = DockTrayWake.FindTarget(Identity(exe), null);
                check(target.Processes.ContainsKey((uint)child.Id) && target.IsAlive(),
                    "the background app is matched by both its live process and exact executable path");
                var other = DockTrayWake.FindTarget(Identity(Path.Combine(Program.Base, "different-install", "Weixin.exe")), null);
                check(!other.Processes.ContainsKey((uint)child.Id), "another installation with the same executable name is excluded");
            }
            finally { if (!child.HasExited) child.Kill(); }
        }
    }
}
