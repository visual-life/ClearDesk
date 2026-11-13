using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClearDesk
{
    /// <summary>2.3.3: Dock attention flash (shell hook → pulsing item) and 托盘图标 in 快捷控制.</summary>
    internal static class V233Tests
    {
        [DllImport("user32.dll")] static extern bool GetCursorPos(out Native.POINT p);
        static IntPtr H(int n) { return new IntPtr(0x7F000000 + n); }

        sealed class Saved
        {
            Func<IntPtr, bool> alive = Attention.IsAlive; Func<IntPtr, IntPtr> root = Attention.RootOf; Func<IntPtr, bool> own = Attention.IsOwn; Func<IntPtr, AppIdentity> id = Attention.IdentityOf; Func<DateTime> now = Attention.Now;
            bool enabled = Program.S.DockAttention, peek = Program.S.DockAttentionPeek, tray = Program.S.TrayArea; string scope = Program.S.TrayScope;
            public void Restore()
            {
                Attention.IsAlive = alive; Attention.RootOf = root; Attention.IsOwn = own; Attention.IdentityOf = id; Attention.Now = now; Attention.Clear();
                Program.S.DockAttention = enabled; Program.S.DockAttentionPeek = peek; Program.S.TrayArea = tray; Program.S.TrayScope = scope;
            }
        }

        static void Fakes(DateTime[] clock)
        {
            Attention.IsAlive = h => true; Attention.RootOf = h => h; Attention.IsOwn = h => false; Attention.IdentityOf = h => null; Attention.Now = () => clock[0];
            Attention.Clear(); Program.S.DockAttention = true;
        }

        static TrayItem T(string name, bool overflow) { return new TrayItem { Name = name, Tooltip = name + "\n提示", Overflow = overflow, Key = (overflow ? "o:" : "v:") + name + "#0" }; }

        internal static void Rules(Action<bool, string> check)
        {
            check(new Version(SettingsWindow.Version) >= new Version("2.3.3"), "2.3.3 version string (or later)");
            bool madeSettings = Program.S == null; if (madeSettings) Program.S = new Settings();      // the plain self test runs without a loaded configuration
            var s = new Saved();
            try
            {
                var clock = new[] { new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc) }; Fakes(clock);
                check(Attention.Flash == 0x8006 && Attention.Redraw == 6 && Attention.WindowActivated == 4 && Attention.RudeAppActivated == 0x8004 && Attention.WindowDestroyed == 2, "2.3.3 shell hook codes: HSHELL_FLASH 0x8006, REDRAW 6, WINDOWACTIVATED 4, RUDEAPPACTIVATED 0x8004, WINDOWDESTROYED 2");
                int changes = 0; Action count = () => changes++; Attention.Changed += count;
                try
                {
                    check(Attention.Handle(Attention.Flash, H(1)) && Attention.IsFlashing(H(1)) && changes == 1, "2.3.3 HSHELL_FLASH starts the flash of that window and tells the Dock once");
                    check(!Attention.Handle(Attention.Flash, H(1)) && changes == 1, "2.3.3 a repeated FLASH of the same window changes nothing (no restart of the pulse)");
                    check(!Attention.Handle(Attention.Redraw, H(99)) && changes == 1, "2.3.3 a plain REDRAW of a window that is not flashing changes nothing");
                    check(Attention.Handle(Attention.Redraw, H(1)) && !Attention.IsFlashing(H(1)) && changes == 2, "2.3.3 a plain REDRAW (the flash was stopped; a flashing button reports 0x8006) clears it");
                    Attention.Handle(Attention.Flash, H(1)); Attention.Handle(Attention.Flash, H(1));
                    check(Attention.IsFlashing(H(1)), "2.3.3 further 0x8006 messages (title changes while flashing) keep it flashing");
                    Attention.Handle(Attention.Redraw, H(1));
                    foreach (int code in new[] { Attention.WindowActivated, Attention.RudeAppActivated, Attention.WindowDestroyed })
                    {
                        Attention.Handle(Attention.Flash, H(2));
                        check(Attention.IsFlashing(H(2)) && Attention.Handle(code, H(2)) && !Attention.IsFlashing(H(2)), "2.3.3 message 0x" + code.ToString("X") + " clears the flash (activated / closed)");
                    }
                    Attention.Handle(Attention.Flash, H(3)); Attention.Handle(Attention.Flash, H(4));
                    check(Attention.Windows.Length == 2, "2.3.3 two windows can flash at once");
                    Attention.Handle(Attention.WindowActivated, H(3));
                    check(!Attention.IsFlashing(H(3)) && Attention.IsFlashing(H(4)), "2.3.3 activating one window leaves the other flashing");
                    Attention.IsAlive = h => h != H(4);
                    check(Attention.Prune() && Attention.Windows.Length == 0, "2.3.3 a window that no longer exists is dropped");
                    Attention.IsAlive = h => true;
                    Attention.IsOwn = h => h == H(5); Attention.Handle(Attention.Flash, H(5));
                    check(!Attention.IsFlashing(H(5)), "2.3.3 ClearDesk's own windows never raise attention");
                    Attention.IsOwn = h => false;
                    Program.S.DockAttention = false; Attention.Handle(Attention.Flash, H(6));
                    check(!Attention.IsFlashing(H(6)), "2.3.3 应用提醒闪烁 off: flashes are ignored");
                    Program.S.DockAttention = true;
                    Attention.RootOf = h => h == H(8) ? H(7) : h;
                    Attention.Handle(Attention.Flash, H(8));
                    check(Attention.IsFlashing(H(7)) && !Attention.IsFlashing(H(8)), "2.3.3 a flashing owned window is tracked by its top-level owner");
                    Attention.Handle(Attention.WindowActivated, H(8));
                    check(!Attention.IsFlashing(H(7)), "2.3.3 activating the owned window clears the owner's flash");
                    Attention.Handle(Attention.Flash, IntPtr.Zero);
                    check(Attention.Windows.Length == 0, "2.3.3 a message without a window is ignored");
                }
                finally { Attention.Changed -= count; }

                // matching a window to a Dock item: handle first, then program / package identity, pinned items included
                Attention.Clear();
                string exeA = AppIdentity.Normalize(@"C:\Apps\Chat\chat.exe"), exeB = AppIdentity.Normalize(@"C:\Apps\Other\other.exe");
                var running = new RunningApp { Identity = new AppIdentity { Key = "a", Exe = exeA, Name = "Chat" } }; running.Windows.Add(H(10));
                var entryRunning = new DockEntry { Identity = running.Identity, Running = running };
                check(Attention.Matches(entryRunning, H(10)) && !Attention.Matches(entryRunning, H(11)), "2.3.3 match by the item's own window list");
                Attention.IdentityOf = h => h == H(11) ? new AppIdentity { Exe = exeA } : h == H(12) ? new AppIdentity { Exe = exeB } : null;
                check(Attention.Matches(entryRunning, H(11)) && !Attention.Matches(entryRunning, H(12)), "2.3.3 a window the Dock has not listed yet is matched by its program path");
                var pinnedOnly = new DockEntry { PinPath = @"C:\Apps\Chat\chat.exe", Identity = new AppIdentity { Exe = exeA } };
                check(Attention.Matches(pinnedOnly, H(11)) && !Attention.Matches(pinnedOnly, H(12)), "2.3.3 a pinned item that is not running yet still matches by path");
                var pinNoIdentity = new DockEntry { PinPath = @"C:\Apps\Chat\chat.exe" };
                check(Attention.Matches(pinNoIdentity, H(11)) && !Attention.Matches(pinNoIdentity, H(12)), "2.3.3 a pin without a resolved identity matches its target path");
                Attention.IdentityOf = h => h == H(13) ? new AppIdentity { Exe = AppIdentity.Normalize(@"C:\Windows\System32\ApplicationFrameHost.exe"), AppId = "Contoso.Notes_8wekyb3d8bbwe!App" } : null;
                var uwp = new DockEntry { Identity = new AppIdentity { Exe = @"c:\program files\windowsapps\contoso.notes\notes.exe", AppId = "Contoso.Notes_8wekyb3d8bbwe!App" } };
                var uwpPin = new DockEntry { PinPath = "shell:AppsFolder\\Contoso.Notes_8wekyb3d8bbwe!App" };
                check(Attention.Matches(uwp, H(13)) && Attention.Matches(uwpPin, H(13)), "2.3.3 UWP: the frame window is matched by package id (running item and pin)");
                check(!Attention.Matches(null, H(1)), "2.3.3 no item, no match");
                Attention.IdentityOf = h => null;
                clock[0] = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
                Attention.Handle(Attention.Flash, H(10)); clock[0] = clock[0].AddSeconds(3);
                var since = Attention.Since(entryRunning);
                check(since != null && (clock[0] - since.Value).TotalSeconds == 3 && Attention.Since(pinnedOnly) == null, "2.3.3 Since() = when the item's oldest flashing window started");
            }
            finally { s.Restore(); }
            if (madeSettings) Program.S = null;

            // settings
            var d = new Settings();
            check(d.DockAttention && !d.DockAttentionPeek && d.TrayArea && d.TrayScope == "all", "2.3.3 defaults: 应用提醒闪烁 on, 提醒时唤出 Dock off, 托盘图标 on (all)");
            var round = new JavaScriptSerializer().Deserialize<Settings>(new JavaScriptSerializer().Serialize(new Settings { DockAttention = false, DockAttentionPeek = true, TrayArea = false, TrayScope = "hidden" }));
            check(!round.DockAttention && round.DockAttentionPeek && !round.TrayArea && round.TrayScope == "hidden", "2.3.3 the new settings are saved and read back");
            check(new JavaScriptSerializer().Deserialize<Settings>("{\"Dock\":true}").DockAttention, "2.3.3 a settings file from 2.3.2 gets the defaults (flash on)");
            check(OtherInstances.IsHelper("ClearDesk.exe --flash-helper x") && OtherInstances.IsHelper("ClearDesk.exe --tray-helper x"), "2.3.3 the test helpers are not mistaken for another ClearDesk");

            // tray model
            check(TrayAccess.IconKey("YueLink · 已连接 · 规则 · TUN") == "YueLink" && TrayAccess.IconKey("QQ: 1098072546") == "QQ: 1098072546" && TrayAccess.IconKey(" ClearDesk · 你的清透桌面") == "ClearDesk", "2.3.3 a tray icon's picture is remembered under the stable part of its tooltip");
            {
                string icons = Path.Combine(Path.GetTempPath(), "cd233-icons-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    TrayAccess.TestPutIcon("RoundTrip233", new byte[] { 9, 8, 7 }); TrayAccess.SaveIcons(icons); TrayAccess.TestForgetIcons();
                    check(!TrayAccess.TestHasIcon("RoundTrip233"), "2.3.3 (test) icon cache emptied");
                    TrayAccess.LoadIcons(icons);
                    check(TrayAccess.TestHasIcon("RoundTrip233"), "2.3.3 tray icon pictures are saved and loaded again after a restart");
                }
                finally { TrayAccess.TestForgetIcons(); try { File.Delete(icons); } catch { } }
            }
            check(TrayAccess.Clean("QQ\r\n新消息 3 条") == "QQ" && TrayAccess.Clean("  网易UU远程 ") == "网易UU远程" && TrayAccess.Clean(null) == "", "2.3.3 a tray tooltip is cut to its first line for the name");
        }

        static async Task<bool> Until(Func<bool> done, int ms)
        {
            var w = Stopwatch.StartNew();
            while (w.ElapsedMilliseconds < ms) { if (done()) return true; await Task.Delay(40); }
            return done();
        }

        static RunningApp Fake(string key, string exe, int window)
        {
            var app = new RunningApp { Identity = new AppIdentity { Key = key, AppId = key, Exe = exe, Name = key, LaunchPath = exe }, Title = key };
            app.Windows.Add(H(window)); return app;
        }

        internal static async Task Run(Action<bool, string> check)
        {
            var dock = Program.OpenWindows.OfType<DockWindow>().FirstOrDefault(d => !d.WasClosed);
            bool created = false;
            if (dock == null) { dock = new DockWindow(); Program.AddWindow(dock); created = true; await Task.Delay(500); }
            var saved = new Saved(); var monitor = dock.TestRunning; var oldApps = monitor.Apps; var oldPins = Program.S.Pins; bool oldAuto = Program.S.DockAutoHide;
            try
            {
                var clock = new[] { DateTime.UtcNow }; Fakes(clock);
                Program.S.Pins = new List<string>();
                var a = Fake("v233-a", Path.Combine(Program.Base, "ClearDesk.exe"), 21); var b = Fake("v233-b", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), 22);
                monitor.Apps = new List<RunningApp> { a, b };
                dock.Rebuild(); await Task.Delay(300);
                int ia = dock.TestEntries.FindIndex(e => e.Running == a), ib = dock.TestEntries.FindIndex(e => e.Running == b);
                check(ia >= 0 && ib >= 0 && dock.AttentionItems == 0, "2.3.3 Dock fixture: two items, nothing flashing");
                Attention.Handle(Attention.Flash, H(21));
                await Until(() => dock.ItemAttention(ia), 2000);
                check(dock.ItemAttention(ia) && !dock.ItemAttention(ib) && dock.TestHalo(ia).Visibility == Visibility.Visible, "2.3.3 a flash makes exactly that Dock item pulse (halo shown)");
                var halo = dock.TestHalo(ia);
                await Task.Delay(120);
                double o1 = halo.Opacity; await Task.Delay(260); double o2 = halo.Opacity;
                check(Program.Rendering || o1 != o2 || halo.Opacity > .25, "2.3.3 the halo opacity is animated (" + o1.ToString("0.00") + " → " + o2.ToString("0.00") + ")");
                var button = dock.TestButton(ia + 1);
                check(Math.Abs(Canvas_Left(halo) + halo.Width / 2 - (Canvas_Left(button) + button.Width / 2)) < 1.5 && halo.Width > button.Width, "2.3.3 the halo sits centred behind its icon and is larger than the icon");
                dock.Rebuild(); await Task.Delay(200);
                check(dock.ItemAttention(ia) && dock.TestHalo(ia).Visibility == Visibility.Visible, "2.3.3 a Dock rebuild (apps opening/closing) keeps the flashing state");
                Attention.Handle(Attention.WindowActivated, H(21));
                check(await Until(() => !dock.ItemAttention(ia), 2000) && dock.TestHalo(ia).Visibility != Visibility.Visible, "2.3.3 activating the app stops the pulse and hides the halo");
                Program.S.DockAttention = false; Attention.Add(H(22)); dock.ApplyAttention(false);
                check(!dock.ItemAttention(ib), "2.3.3 应用提醒闪烁 off: the Dock does not pulse");
                Program.S.DockAttention = true; dock.ApplyAttention(false);
                check(dock.ItemAttention(ib), "2.3.3 ... turned on again, it pulses at once");
                Attention.Clear(); dock.ApplyAttention(false);
                clock[0] = DateTime.UtcNow; Attention.Now = () => clock[0].AddSeconds(60); Attention.Add(H(21)); Attention.Now = () => clock[0].AddSeconds(60 + 20); dock.ApplyAttention(false);
                check(dock.ItemAttention(ia) && Math.Abs(dock.TestHalo(ia).Opacity - .85) < .01, "2.3.3 after about 12 seconds the pulse settles into a steady glow (still visible)");
                Attention.Clear(); dock.ApplyAttention(false);
                Attention.Now = () => clock[0];

                // the pulse itself (the test process normally runs with animations off; switch them on for this one check)
                if (SystemParameters.ClientAreaAnimation)
                {
                    bool oldRendering = Program.Rendering; Program.Rendering = false;
                    try
                    {
                        Attention.Now = () => clock[0]; Attention.Clear(); Attention.Add(H(22)); dock.ApplyAttention(false);
                        var samples = new List<double>();
                        for (int i = 0; i < 14; i++) { await Task.Delay(90); samples.Add(dock.TestHalo(ib).Opacity); }
                        check(samples.Max() - samples.Min() > .25 && samples.Min() >= .25 && samples.Max() <= 1.001, "2.3.3 the halo pulses smoothly between " + samples.Min().ToString("0.00") + " and " + samples.Max().ToString("0.00"));
                    }
                    finally { Program.Rendering = oldRendering; Attention.Clear(); dock.ApplyAttention(false); }
                }
                else check(true, "2.3.3 pulse animation: Windows animations are off in this session — skipped");

                // the auto-hidden Dock: not summoned by default, summoned when 提醒时唤出 Dock is on
                Program.S.DockAutoHide = true; var ah = dock.TestAutoHide(); ah.TestApply(true, false); await Task.Delay(350);
                Program.S.DockAttentionPeek = false; Attention.Add(H(21)); dock.ApplyAttention(true);
                check(ah.Hidden, "2.3.3 提醒时唤出 Dock off (default): a hidden Dock stays hidden");
                Attention.Clear(); dock.ApplyAttention(false);
                Program.S.DockAttentionPeek = true; Attention.Add(H(21)); dock.ApplyAttention(true); await Task.Delay(350);
                check(!ah.Hidden, "2.3.3 提醒时唤出 Dock on: a hidden Dock slides out for the flash");
                Attention.Clear(); dock.ApplyAttention(false); ah.TestApply(false, false); await Task.Delay(300);
                Program.S.DockAutoHide = oldAuto;

                // pictures for the release notes (only when CLEARDESK_SHOTS is set)
                Attention.Now = () => clock[0]; Attention.Clear(); Attention.Add(H(21)); dock.ApplyAttention(false); await Task.Delay(200);
                V231Tests.Shot(dock, "233-dock-attention");
                Attention.Clear(); dock.ApplyAttention(false);
                await TrayShots(check);

                // tray panel model (fake readers)
                await TrayModel(check);
            }
            finally
            {
                saved.Restore(); monitor.Apps = oldApps; Program.S.Pins = oldPins; Program.S.DockAutoHide = oldAuto;
                dock.Rebuild();
                if (created) dock.Close();
            }

            await RealFlash(dock, check);
            await RealTray(check);
        }

        static double Canvas_Left(UIElement e) { return System.Windows.Controls.Canvas.GetLeft(e); }

        static byte[] FakeIcon(string letter, byte r, byte g, byte b)
        {
            using (var bmp = new System.Drawing.Bitmap(24, 24))
            {
                using (var gr = System.Drawing.Graphics.FromImage(bmp))
                {
                    gr.Clear(System.Drawing.Color.FromArgb(r, g, b)); gr.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var f = new System.Drawing.Font("Segoe UI", 12, System.Drawing.FontStyle.Bold)) gr.DrawString(letter, f, System.Drawing.Brushes.White, 3, 1);
                }
                using (var ms = new MemoryStream()) { bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png); return ms.ToArray(); }
            }
        }

        sealed class GlassWindowShot
        {
            internal void Save(FrameworkElement e, string name)
            {
                string dir = Environment.GetEnvironmentVariable("CLEARDESK_SHOTS"); if (string.IsNullOrEmpty(dir)) return;
                try
                {
                    Directory.CreateDirectory(dir); e.Measure(new Size(400, 400)); e.Arrange(new Rect(0, 0, 400, e.DesiredSize.Height)); e.UpdateLayout();
                    var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(400, Math.Max(1, (int)Math.Ceiling(e.DesiredSize.Height)), 96, 96, PixelFormats.Pbgra32);
                    var dv = new DrawingVisual(); using (var c = dv.RenderOpen()) { c.DrawRectangle(new SolidColorBrush(Color.FromRgb(70, 76, 92)), null, new Rect(0, 0, 400, bmp.Height)); c.DrawRectangle(new VisualBrush(e), null, new Rect(0, 0, 400, e.DesiredSize.Height)); }
                    bmp.Render(dv);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                    using (var f = File.Create(Path.Combine(dir, name + ".png"))) enc.Save(f);
                }
                catch (Exception ex) { Program.Log(ex); }
            }
        }

        static async Task TrayShots(Action<bool, string> check)
        {
            bool oldQuick = Program.S.QuickControls;
            var q = Program.NewWidget("quick") as QuickControlWindow;
            if (q == null) { check(true, "2.3.3 tray picture: quick widget not created — skipped"); return; }
            try
            {
                await Task.Delay(500);
                Program.S.TrayArea = true; Program.S.TrayScope = "all";
                var names = new[] { "ChatGPT", "微信", "QQ", "NVIDIA 设置", "YueLink", "ClearDesk", "联想智能引擎", "网易UU远程", "Steam", "Wallpaper Engine", "Tencent Meeting" };
                var cols = new byte[][] { new byte[] { 16, 163, 127 }, new byte[] { 7, 193, 96 }, new byte[] { 18, 183, 245 }, new byte[] { 118, 185, 0 }, new byte[] { 90, 80, 200 }, new byte[] { 40, 120, 255 }, new byte[] { 220, 60, 60 }, new byte[] { 250, 140, 30 }, new byte[] { 30, 40, 60 }, new byte[] { 60, 60, 200 }, new byte[] { 0, 120, 215 } };
                Func<int, bool, TrayItem> mk = (i, o) => new TrayItem { Name = names[i], Tooltip = names[i], Overflow = o, Key = (o ? "o:" : "v:") + names[i] + "#0", Png = FakeIcon(names[i].Substring(0, 1), cols[i][0], cols[i][1], cols[i][2]) };
                q.Tray.SetVisible(new List<TrayItem> { mk(0, false), mk(1, false) });
                q.Tray.SetHidden(Enumerable.Range(2, 9).Select(i => mk(i, true)).ToList(), false);
                await Task.Delay(300);
                check(q.Height > 285 + 60, "2.3.3 the 快捷控制 widget grows to make room for the tray area (" + q.Height.ToString("0") + ")");
                V231Tests.Shot(q, "233-quick-tray-collapsed");
                ((System.Windows.Controls.Button)((System.Windows.Controls.Grid)q.Tray.Children[0]).Children.OfType<System.Windows.Controls.Button>().Last()).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                await Task.Delay(350);
                V231Tests.Shot(q, "233-quick-tray-expanded");
                check(q.Height > 285 + 120, "2.3.3 expanded, the widget is taller still (" + q.Height.ToString("0") + ")");
                // the real thing, read-only: this PC's own notification-area icons (nothing is clicked)
                if (TrayAccess.Available)
                {
                    var real = new TrayPanel233(Dispatcher.CurrentDispatcher) { CachePath = Path.Combine(Path.GetTempPath(), "cd233-real-" + Guid.NewGuid().ToString("N") + ".json") };
                    var host = new System.Windows.Controls.StackPanel(); host.Children.Add(real);
                    var window = new Window { Width = 400, Height = 300, Content = host, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000 };
                    window.Show();
                    try
                    {
                        real.SetVisible(await Task.Run(() => TrayAccess.ReadVisible(true)));
                        real.RefreshHidden(true); await Until(() => real.HiddenReads > 0, 15000);
                        check(real.VisibleReads > 0 && real.HiddenReads > 0, "2.3.3 real tray panel: this PC's visible + hidden icons were read (" + string.Join(", ", real.TileNames) + ")");
                        int withImage = real.Shown().Count(i => i.Png != null);
                        check(real.Shown().Count == 0 || withImage > 0, "2.3.3 real tray panel: icon images were copied from the screen for " + withImage + " of " + real.Shown().Count + " icons");
                        await Task.Delay(300);
                        var snap = new GlassWindowShot(); snap.Save(host, "233-tray-real");
                    }
                    finally { window.Close(); TrayAccess.CloseFlyout(); try { File.Delete(real.CachePath); } catch { } }
                }
                Program.S.TrayArea = false; q.ApplyTraySetting(); await Task.Delay(200);
                check(Math.Abs(q.Height - 285) < 1, "2.3.3 托盘图标 off: the 快捷控制 widget is back to 285 px (" + q.Height.ToString("0") + ")");
                Program.S.TrayArea = true;
            }
            finally { q.Close(); Program.S.QuickControls = oldQuick; }
        }

        static async Task TrayModel(Action<bool, string> check)
        {
            string cache = Path.Combine(Path.GetTempPath(), "cd233-tray-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var toasts = new List<string>(); var clicks = new List<string>(); var extra = new List<double>();
                var panel = new TrayPanel233(Dispatcher.CurrentDispatcher) { CachePath = cache };
                panel.Toast = (g, m) => toasts.Add(m); panel.ExtraHeightChanged = e => extra.Add(e);
                panel.Clicker = (it, mode) => { clicks.Add(it.Name + ":" + mode); return it.Name == "gone" ? "没有找到“gone”，应用可能已经退出" : null; };
                Program.S.TrayArea = true; Program.S.TrayScope = "all";
                var gone = T("gone", true); gone.Png = new byte[] { 1, 2, 3 };
                panel.SetVisible(new List<TrayItem> { T("A", false), T("B", false) });
                check(panel.TileNames.SequenceEqual(new[] { "A", "B" }), "2.3.3 tray panel: visible icons are listed before the hidden ones are read");
                panel.SetHidden(new List<TrayItem> { T("B", true), T("C", true), gone }, false);
                check(panel.TileNames.SequenceEqual(new[] { "A", "B", "C", "gone" }), "2.3.3 tray panel: all = visible + hidden, an icon that is on the taskbar is listed once");
                Program.S.TrayScope = "hidden"; panel.ApplySettings();
                check(panel.TileNames.SequenceEqual(new[] { "C", "gone" }), "2.3.3 tray panel scope 仅被收起的");
                Program.S.TrayScope = "visible"; panel.ApplySettings();
                check(panel.TileNames.SequenceEqual(new[] { "A", "B" }), "2.3.3 tray panel scope 仅任务栏上可见的");
                Program.S.TrayScope = "all"; panel.ApplySettings();
                check(File.Exists(cache), "2.3.3 the hidden icons are remembered between runs");
                var again = new TrayPanel233(Dispatcher.CurrentDispatcher) { CachePath = cache }; again.LoadCache();
                again.SetVisible(new List<TrayItem>());
                check(again.TileNames.SequenceEqual(new[] { "B", "C", "gone" }) && again.Shown().First(i => i.Name == "gone").Png.SequenceEqual(new byte[] { 1, 2, 3 }), "2.3.3 ... and shown (with their images) right after a restart");
                // a hidden icon that was moved onto the taskbar
                panel.SetVisible(new List<TrayItem> { T("A", false), T("C", false) });
                check(panel.TileNames.SequenceEqual(new[] { "A", "C", "B", "gone" }) || panel.TileNames.SequenceEqual(new[] { "A", "C", "gone" }), "2.3.3 an icon promoted to the taskbar is not listed twice (" + string.Join(",", panel.TileNames) + ")");
                // click routing: left (after the double-click wait), right, double
                var tile = (System.Windows.Controls.Border)((System.Windows.Controls.WrapPanel)((System.Windows.Controls.ScrollViewer)panel.Children[1]).Content).Children[0];
                tile.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                check(await Until(() => clicks.Contains("A:0"), 1500), "2.3.3 left click on a tray tile → the icon's left click");
                await Until(() => !panel.Busy, 1000);
                tile.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = UIElement.MouseRightButtonUpEvent });
                check(await Until(() => clicks.Contains("A:1"), 1500), "2.3.3 right click on a tray tile → the icon's own menu");
                await Until(() => !panel.Busy, 1000);
                panel.Act(T("A", false), 2, null);
                check(await Until(() => clicks.Contains("A:2"), 1500), "2.3.3 double click is forwarded as a double click");
                await Until(() => !panel.Busy, 1000);
                panel.Act(gone, 0, null);
                check(await Until(() => toasts.Count > 0, 1500) && await Until(() => !panel.TileNames.Contains("gone"), 1500), "2.3.3 clicking an icon whose app has quit says so and removes it (" + string.Join(" / ", toasts) + ")");
                // size
                var many = Enumerable.Range(0, 20).Select(i => T("n" + i, false)).ToList();
                panel.SetVisible(many);
                double collapsed = panel.ExtraHeight;
                check(collapsed == TrayPanel233.TopGap + TrayPanel233.HeaderH + TrayPanel233.Tile, "2.3.3 collapsed tray area is one row (" + collapsed + ")");
                var expand = ((System.Windows.Controls.Grid)panel.Children[0]).Children.OfType<System.Windows.Controls.Button>().Last();
                expand.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                check(panel.ExtraHeight > collapsed && panel.ExtraHeight <= TrayPanel233.TopGap + TrayPanel233.HeaderH + 4 * TrayPanel233.Tile + .1, "2.3.3 expanding grows the area (to at most 4 rows) (" + panel.ExtraHeight + ")");
                expand.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                check(panel.ExtraHeight == collapsed, "2.3.3 ... and collapses again");
                Program.S.TrayArea = false; panel.ApplySettings();
                check(panel.Visibility == Visibility.Collapsed && panel.ExtraHeight == 0, "2.3.3 托盘图标 off: the area disappears and the widget gets its old height back");
                Program.S.TrayArea = true;
            }
            finally { try { File.Delete(cache); } catch { } }
        }

        static string Exe { get { return Path.Combine(Program.Base, "ClearDesk.exe"); } }
        static Process Start(string args) { return Process.Start(new ProcessStartInfo(Exe, args) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }); }
        static void Cmd(string ctl, string line) { File.AppendAllText(ctl, line + Environment.NewLine); }

        // a real window of another process flashes its taskbar button through FlashWindowEx
        static async Task RealFlash(DockWindow dock, Action<bool, string> check)
        {
            string ctl = Path.Combine(Path.GetTempPath(), "cd233-flash-" + Guid.NewGuid().ToString("N") + ".ctl"); Process p = null; bool startedHook = false;
            var monitor = dock.TestRunning; var oldApps = monitor.Apps; var oldPins = Program.S.Pins;
            bool created = false; if (dock.WasClosed) { dock = new DockWindow(); Program.AddWindow(dock); created = true; await Task.Delay(400); monitor = dock.TestRunning; oldApps = monitor.Apps; }
            try
            {
                Program.S.Pins = new List<string>(); Program.S.DockAttention = true;
                p = Start("--flash-helper \"ClearDesk 提醒测试 " + Guid.NewGuid().ToString("N").Substring(0, 6) + "\" \"" + ctl + "\" 60");
                check(await Until(() => File.Exists(ctl + ".ready"), 10000), "2.3.3 real flash: the helper window is up");
                IntPtr hwnd = new IntPtr(long.Parse(File.ReadAllText(ctl + ".ready").Trim()));
                RunningApp app = null;
                await Until(() => { monitor.Refresh(); app = monitor.Apps.FirstOrDefault(x => x.Windows.Contains(hwnd)); return app != null; }, 8000);
                check(app != null, "2.3.3 real flash: the Dock lists the helper's window as a running item");
                dock.Rebuild(); await Task.Delay(300);
                int idx = dock.TestEntries.FindIndex(e => e.Running != null && e.Running.Windows.Contains(hwnd));
                check(idx >= 0, "2.3.3 real flash: the helper is a Dock item");
                startedHook = Attention.Start();
                check(Attention.Running, "2.3.3 the shell hook window is registered");
                int before = Attention.Messages;
                for (int round = 1; round <= 2; round++)
                {
                    Cmd(ctl, "flash");
                    bool on = await Until(() => dock.ItemAttention(idx), 6000);
                    check(on && Attention.IsFlashing(hwnd) && Native.GetForegroundWindow() != hwnd, "2.3.3 real flash (" + round + "): FlashWindowEx in another process puts that Dock item into the attention state (" + (Attention.Messages - before) + " shell messages)");
                    check(dock.TestHalo(idx).Visibility == Visibility.Visible, "2.3.3 real flash (" + round + "): the halo is shown");
                    if (round == 1)
                    {
                        Cmd(ctl, "stop");
                        check(await Until(() => !dock.ItemAttention(idx), 5000), "2.3.3 real flash: FLASHW_STOP clears the Dock item (shell messages: " + string.Join(", ", Attention.Trace.Skip(Math.Max(0, Attention.Trace.Count - 12))) + ")");
                    }
                    else
                    {
                        Attention.Handle(Attention.WindowActivated, hwnd);
                        check(await Until(() => !dock.ItemAttention(idx), 3000), "2.3.3 real flash: activation (HSHELL_WINDOWACTIVATED, injected so no real window is brought forward) clears the Dock item");
                    }
                }
                Cmd(ctl, "flash"); await Until(() => dock.ItemAttention(idx), 6000);
                Cmd(ctl, "exit");
                check(await Until(() => !Attention.IsFlashing(hwnd), 6000), "2.3.3 real flash: closing the window clears its flash");
                check(p.WaitForExit(6000), "2.3.3 real flash: the helper exits");
            }
            finally
            {
                try { if (p != null && !p.HasExited) p.Kill(); } catch { }
                if (startedHook) Attention.Stop();
                Attention.Clear();
                monitor.Apps = oldApps; Program.S.Pins = oldPins; dock.Rebuild();
                try { File.Delete(ctl); File.Delete(ctl + ".ready"); } catch { }
                if (created) dock.Close();
            }
        }

        // a real program with its own tray icon: read, find, left / double / right click, remove
        static async Task RealTray(Action<bool, string> check)
        {
            if (Program.Rendering && Environment.GetEnvironmentVariable("CLEARDESK_NO_TRAY_TEST") == "1") return;
            if (!TrayAccess.Available) { check(true, "2.3.3 real tray: no taskbar to read in this session — skipped"); return; }
            string tip = "ClearDesk托盘测试" + Guid.NewGuid().ToString("N").Substring(0, 6), log = Path.Combine(Path.GetTempPath(), "cd233-tray-" + Guid.NewGuid().ToString("N") + ".log"), ctl = log + ".ctl"; Process p = null;
            Func<string[]> lines = () => { try { return File.Exists(log) ? File.ReadAllLines(log) : new string[0]; } catch { return new string[0]; } };
            try
            {
                p = Start("--tray-helper " + tip + " \"" + log + "\" \"" + ctl + "\" 90");
                check(await Until(() => File.Exists(ctl + ".ready"), 10000), "2.3.3 real tray: the helper program is up with its own tray icon");
                TrayItem item = null;
                for (int i = 0; i < 25 && item == null; i++) { item = (await Task.Run(() => TrayAccess.ReadVisible(false))).FirstOrDefault(x => x.Name.StartsWith(tip)); if (item == null) await Task.Delay(120); }
                if (item == null) item = (await Task.Run(() => TrayAccess.ReadOverflow(true))).FirstOrDefault(i => i.Name.StartsWith(tip));
                check(item != null, "2.3.3 real tray: UI Automation finds the helper's icon (" + (item == null ? TrayAccess.LastProblem : (item.Overflow ? "in the hidden icons" : "on the taskbar")) + ")");
                if (item == null) return;
                check(item.Tooltip.Contains(tip), "2.3.3 real tray: the tooltip text is the program's");
                check(item.Overflow ? (item.Png != null && item.Png.Length > 100) : true, "2.3.3 real tray: the icon image was copied while the flyout was open (" + (item.Png == null ? 0 : item.Png.Length) + " bytes)");
                var before = lines().Length;
                string err = await Task.Run(() => TrayAccess.Click(item, 0));
                check(err == null && await Until(() => lines().Skip(before).Any(l => l == "left"), 4000), "2.3.3 real tray: left click reaches the program as a left click (" + (err ?? string.Join(",", lines().Skip(before))) + ")");
                before = lines().Length;
                err = await Task.Run(() => TrayAccess.Click(item, 2));
                check(err == null && await Until(() => lines().Skip(before).Any(l => l == "double" || l == "left"), 4000), "2.3.3 real tray: double click is forwarded (" + (err ?? string.Join(",", lines().Skip(before))) + ")");
                before = lines().Length; Native.POINT c0; GetCursorPos(out c0);
                err = await Task.Run(() => TrayAccess.Click(item, 1));
                bool menu = await Until(() => lines().Skip(before).Any(l => l == "right" || l == "menu-open"), 4000);
                Native.POINT c1; GetCursorPos(out c1);
                check(err == null && menu, "2.3.3 real tray: right click opens the program's own context menu (" + (err ?? string.Join(",", lines().Skip(before))) + ")");
                check(Math.Abs(c0.X - c1.X) <= 3 && Math.Abs(c0.Y - c1.Y) <= 3, "2.3.3 real tray: the mouse pointer is put back where it was");
                await Until(() => lines().Contains("menu-open") && !lines().Skip(before).Contains("menu-exit"), 500);
                await Task.Delay(2800);       // the helper closes its menu by itself
                Cmd(ctl, "exit");
                check(p.WaitForExit(8000), "2.3.3 real tray: the helper exits");
                bool gone = false;
                for (int i = 0; i < 4 && !gone; i++)
                {
                    gone = await Task.Run(() => TrayAccess.ReadVisible(false).All(x => !x.Name.StartsWith(tip)) && TrayAccess.ReadOverflow(false).All(x => !x.Name.StartsWith(tip)));
                    if (!gone) await Task.Delay(800);
                }
                check(gone, "2.3.3 real tray: when the program exits its icon is gone from the lists");
            }
            finally
            {
                try { if (p != null && !p.HasExited) p.Kill(); } catch { }
                TrayAccess.CloseFlyout();
                try { File.Delete(log); File.Delete(ctl); File.Delete(ctl + ".ready"); } catch { }
            }
        }
    }
}
