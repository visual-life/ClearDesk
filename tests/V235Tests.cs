using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ClearDesk
{
    /// <summary>2.3.5: ClearDesk never leaves the desktop icons or the "hidden icons" flyout changed. Normal exit, tray exit, kill -9 (crash guard, a separate process), next start.</summary>
    internal static class V235Tests
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASS { public uint style; public IntPtr proc; public int cls, wnd; public IntPtr inst, icon, cursor, brush; public string menu, name; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassW(ref WNDCLASS c);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int a, int b, int c, int d);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string n);
        [DllImport("kernel32.dll")] static extern IntPtr GetProcAddress(IntPtr m, string n);
        static string Exe { get { return Path.Combine(Program.Base, "ClearDesk.exe"); } }

        /// <summary>A top-level window of a class of our own (stand-in for the explorer's flyout window).</summary>
        static IntPtr TestWindow(string cls)
        {
            var wc = new WNDCLASS { proc = GetProcAddress(GetModuleHandleW("user32.dll"), "DefWindowProcW"), name = cls };
            RegisterClassW(ref wc);
            return CreateWindowExW(0, cls, "flyout", unchecked((int)0x80000000), 0, 0, 100, 100, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }

        internal static void Rules(Action<bool, string> check)
        {
            check(new Version(SettingsWindow.Version) >= new Version("2.3.5"), "2.3.5 version string (or later)");
            var oh = DesktopIcons.Handle; var ov = DesktopIcons.IsVisible; var os = DesktopIcons.Show; var ow = DesktopIcons.WindowsHides; var on = DesktopIcons.SwitchOn;
            string dir = Path.Combine(Path.GetTempPath(), "cd235-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            string oldBox = Environment.GetEnvironmentVariable("CLEARDESK_ICONS_SANDBOX");
            try
            {
                bool visible = false, hides = true; var calls = new List<string>();
                DesktopIcons.Handle = () => (IntPtr)5; DesktopIcons.IsVisible = h => visible; DesktopIcons.Show = (h, v) => { calls.Add("show" + v); visible = v; };
                DesktopIcons.WindowsHides = () => hides; DesktopIcons.SwitchOn = () => { calls.Add("switch-on"); hides = false; };
                // the reported bug: ClearDesk hid the icons, Windows' own switch was turned off meanwhile, ClearDesk closed → empty desktop
                DesktopIcons.Restore(false);
                check(visible && !hides && calls.SequenceEqual(new[] { "switch-on", "showTrue" }), "2.3.5 icons: closing ClearDesk turns Windows' own switch back on and shows the icons (" + string.Join(",", calls) + ")");
                // the user had them off in Windows before ClearDesk started: that stays
                visible = true; hides = true; calls.Clear(); DesktopIcons.Restore(true);
                check(hides && !visible && calls.SequenceEqual(new[] { "showFalse" }), "2.3.5 icons: off in Windows when the session began → stays off");
                // the old call without information behaves as before
                visible = false; hides = false; calls.Clear(); DesktopIcons.Restore();
                check(visible && calls.SequenceEqual(new[] { "showTrue" }), "2.3.5 icons: Restore() without information still shows them when Windows shows them");
                // marker file: the next start finishes what a dead session could not
                Environment.SetEnvironmentVariable("CLEARDESK_ICONS_SANDBOX", Path.Combine(dir, "icons.txt"));
                DesktopIcons.ClearMarker();
                check(!DesktopIcons.HealAfterLastSession(), "2.3.5 icons: no marker, nothing to heal");
                DesktopIcons.MarkTouched(false); visible = false; hides = true; calls.Clear();
                check(DesktopIcons.HealAfterLastSession() && visible && !hides && calls.Contains("switch-on"), "2.3.5 icons: start-up after a session that changed the icons and never put them back (switch off now) → shown again");
                check(!DesktopIcons.HealAfterLastSession(), "2.3.5 icons: the marker is used once");
                DesktopIcons.MarkTouched(true); visible = false; hides = true; calls.Clear();
                check(!DesktopIcons.HealAfterLastSession() && hides && calls.Count == 0, "2.3.5 icons: off in Windows when that session began → left alone");
                DesktopIcons.MarkTouched(false); visible = true; hides = false; calls.Clear();
                check(!DesktopIcons.HealAfterLastSession() && calls.Count == 0, "2.3.5 icons: switch is on → nothing to do");
                DesktopIcons.MarkTouched(false); DesktopIcons.ClearMarker();
                check(!DesktopIcons.HealAfterLastSession(), "2.3.5 icons: a session that put them back (exit / guard) removes the marker");
                // recovery files of sessions that are gone are applied; those of running programs are not
                string f1 = Path.Combine(Program.Data, "recovery-v12-2147000001.json"), f2 = Path.Combine(Program.Data, "recovery-v12-" + Process.GetCurrentProcess().Id + "1.json");
                Directory.CreateDirectory(Program.Data); visible = false; hides = true; calls.Clear();
                File.WriteAllText(f1, new JavaScriptSerializer().Serialize(new RestoreState { IconsChanged = true, WindowsHidesAtStart = false }));
                int healed = DesktopServices.HealOrphans();
                check(healed >= 1 && !File.Exists(f1) && visible && !hides, "2.3.5 a recovery file of a session that no longer exists is applied and removed at start (" + healed + ")");
                try { File.Delete(f2); } catch { }
            }
            finally
            {
                DesktopIcons.Handle = oh; DesktopIcons.IsVisible = ov; DesktopIcons.Show = os; DesktopIcons.WindowsHides = ow; DesktopIcons.SwitchOn = on;
                Environment.SetEnvironmentVariable("CLEARDESK_ICONS_SANDBOX", oldBox);
                try { Directory.Delete(dir, true); } catch { }
            }
            // the flyout window: only an empty region left behind (by 2.3.4) is put right
            string cls = "CDTestFlyout" + Guid.NewGuid().ToString("N").Substring(0, 8), oldCls = Environment.GetEnvironmentVariable("CLEARDESK_FLYOUT_CLASS");
            Environment.SetEnvironmentVariable("CLEARDESK_FLYOUT_CLASS", cls); IntPtr a = IntPtr.Zero, b = IntPtr.Zero;
            try
            {
                a = TestWindow(cls); b = TestWindow(cls);
                SetWindowRgn(a, CreateRectRgn(0, 0, 0, 0), false); SetWindowRgn(b, CreateRectRgn(0, 0, 50, 50), false);
                check(UiaCom.RegionIsEmpty(a) && !UiaCom.RegionIsEmpty(b), "2.3.5 test windows: one has an empty region, one a normal one");
                int n = UiaCom.HealFlyouts();
                check(n == 1 && !UiaCom.HasRegion(a) && UiaCom.HasRegion(b), "2.3.5 flyout heal: the empty region is removed, a normal region is left alone (" + n + ")");
                check(UiaCom.HealFlyouts() == 0, "2.3.5 flyout heal: nothing to do the second time");
            }
            finally { if (a != IntPtr.Zero) DestroyWindow(a); if (b != IntPtr.Zero) DestroyWindow(b); Environment.SetEnvironmentVariable("CLEARDESK_FLYOUT_CLASS", oldCls); }
            var src = File.Exists(Path.Combine(Program.Base, "src", "UiaCom234.cs")) ? File.ReadAllText(Path.Combine(Program.Base, "src", "UiaCom234.cs")) : null;
            if (src != null) check(!src.Contains("SetWindowRgn(h, CreateRectRgn(0, 0, 0, 0)") && !src.Contains("class OverflowHider"), "2.3.5 the code that made the flyout invisible is gone");
        }

        static Process Start(string exe, string args, Dictionary<string, string> env)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
            return Process.Start(psi);
        }

        internal static async Task Run(Action<bool, string> check)
        {
            // kill -9: the crash guard (its own process) puts everything back, using only the file the session left
            string dir = Path.Combine(Path.GetTempPath(), "cd235g-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            string cls = "CDGuardFlyout" + Guid.NewGuid().ToString("N").Substring(0, 8); IntPtr w = IntPtr.Zero; Process victim = null, guard = null;
            try
            {
                string icons = Path.Combine(dir, "icons.txt"), rec = Path.Combine(dir, "recovery-v12-1.json"), marker = Path.Combine(dir, "desktop-icons-state.json");
                var env = new Dictionary<string, string> { { "CLEARDESK_ICONS_SANDBOX", icons }, { "CLEARDESK_FLYOUT_CLASS", cls } };
                for (int round = 0; round < 2; round++)
                {
                    bool startedHidden = round == 1;
                    File.WriteAllText(icons, "visible=0;hides=1");                       // ClearDesk had hidden the icons and Windows' switch got turned off meanwhile
                    File.WriteAllText(rec, new JavaScriptSerializer().Serialize(new RestoreState { IconsChanged = true, WindowsHidesAtStart = startedHidden }));
                    File.WriteAllText(marker, "{\"StartedHidden\":" + (startedHidden ? "true" : "false") + ",\"Touched\":true}");
                    w = TestWindow(cls); SetWindowRgn(w, CreateRectRgn(0, 0, 0, 0), false);       // a flyout left with an empty region
                    victim = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 120 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                    guard = Start(Exe, "--guard " + victim.Id + " \"" + rec + "\"", env);
                    await Task.Delay(700);
                    check(!guard.HasExited && File.ReadAllText(icons) == "visible=0;hides=1", "2.3.5 guard (round " + round + "): waits while the session is alive and changes nothing");
                    victim.Kill();                                                          // kill -9: no exit handler, no dispose
                    bool done = await Task.Run(() => guard.WaitForExit(15000));
                    string state = File.Exists(icons) ? File.ReadAllText(icons) : "?";
                    if (!startedHidden)
                        check(done && state == "visible=1;hides=0" && !File.Exists(rec) && !File.Exists(marker), "2.3.5 kill -9: the guard turned Windows' icon switch back on, showed the icons and removed its files (" + state + ")");
                    else
                        check(done && state == "visible=0;hides=1" && !File.Exists(rec), "2.3.5 kill -9: icons the user had switched off in Windows before the session stay off (" + state + ")");
                    check(!UiaCom.RegionIsEmpty(w), "2.3.5 kill -9 (round " + round + "): the guard gave a flyout with an empty region its normal shape back");
                    DestroyWindow(w); w = IntPtr.Zero;
                }
                // the guard also runs when the session ended normally and left no file: it only checks the flyout
                w = TestWindow(cls); SetWindowRgn(w, CreateRectRgn(0, 0, 0, 0), false);
                victim = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 120 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                guard = Start(Exe, "--guard " + victim.Id + " \"" + Path.Combine(dir, "missing.json") + "\"", env);
                await Task.Delay(500); victim.Kill(); bool done2 = await Task.Run(() => guard.WaitForExit(15000));
                check(done2 && !UiaCom.RegionIsEmpty(w), "2.3.5 guard without a recovery file: still heals a flyout left with an empty region");
            }
            finally
            {
                try { if (victim != null && !victim.HasExited) victim.Kill(); } catch { }
                try { if (guard != null && !guard.HasExited) guard.Kill(); } catch { }
                if (w != IntPtr.Zero) DestroyWindow(w);
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
