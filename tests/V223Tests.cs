using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace ClearDesk
{
    /// <summary>2.2.3 tests: Dock icons, text sharpness.</summary>
    internal static class V223Tests
    {
        internal static void Rules(Action<bool, string> check)
        {
            IconRules(check);
        }

        internal static async Task Run(Action<bool, string> check)
        {
            await Task.Yield();
            V223TaskbarTests.Run(check);
            IconRun(check);
            await TextRun(check);
        }

        // ---------------------------------------------------------------- text sharpness
        /// <summary>The widget as it is drawn at the given dpi (premultiplied BGRA).</summary>
        internal static BitmapSource RenderAt(GlassWindow w, double dpi)
        {
            var f = (FrameworkElement)w.Content; f.UpdateLayout(); double k = dpi / 96;
            int W = Math.Max(1, (int)Math.Ceiling(w.ActualWidth * k)), H = Math.Max(1, (int)Math.Ceiling(w.ActualHeight * k));
            var dv = new DrawingVisual(); using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(f) { Stretch = Stretch.Fill }, null, new Rect(0, 0, W / k, H / k));
            var bmp = new RenderTargetBitmap(W, H, dpi, dpi, PixelFormats.Pbgra32); bmp.Render(dv); return bmp;
        }

        /// <summary>
        /// Crispness of white text drawn on a transparent surface: how much of the text's coverage is "solid" rather than a soft edge.
        /// The coverage of white text is the premultiplied luminance (a dark shadow contributes nothing). 1.0 = every edge pixel is fully on or off; blurred text scores low.
        /// </summary>
        internal static double Crispness(BitmapSource bmp, Int32Rect area)
        {
            var px = new byte[area.Width * area.Height * 4]; new FormatConvertedBitmap(new CroppedBitmap(bmp, area), PixelFormats.Pbgra32, null, 0).CopyPixels(px, area.Width * 4, 0);
            int solid = 0, any = 0;
            for (int i = 0; i < area.Width * area.Height; i++)
            {
                double cov = (px[i * 4] * .114 + px[i * 4 + 1] * .587 + px[i * 4 + 2] * .299) / 255.0;
                if (cov >= .2) any++; if (cov >= .9) solid++;
            }
            return any < 20 ? 0 : solid / (double)any;
        }
        internal static Int32Rect TextArea(BitmapSource bmp) { int w = bmp.PixelWidth, h = bmp.PixelHeight; return new Int32Rect(w / 6, h / 4, w * 2 / 3, h / 2); }

        static string ShotDir() { string d = Environment.GetEnvironmentVariable("CD_SHOT_DIR"); return string.IsNullOrEmpty(d) || !Directory.Exists(d) ? null : d; }
        static void SavePng(BitmapSource b, string name) { string d = ShotDir(); if (d == null) return; var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(b)); using (var f = File.Create(Path.Combine(d, name))) enc.Save(f); }

        static async Task TextRun(Action<bool, string> check)
        {
            var lines = await TextProbe.Measure();
            foreach (var line in lines) check(true, "text sharpness: " + line);
            string d = ShotDir(); if (d != null) File.WriteAllLines(Path.Combine(d, "text-probe.txt"), lines);
            // what the screen really shows: frameless clock, 2.2.2 drawing (TestDisable) against the 2.2.3 drawing
            await ShadowRun(check);
            bool wasEnabled = CrispText.Enabled; CrispText.Enabled = true;   // the counter-scaling experiment is off in the product; measured here
            try { await ScaleRun(check); } finally { CrispText.Enabled = wasEnabled; }
        }

        static async Task ScaleRun(Action<bool, string> check)
        {
            var clock = Program.NewWidget("clock"); if (clock.TestToast != null) clock.TestToast.Opacity = 0; clock.SetFrameless(true); clock.SetTextShadow(false);
            var back = Color.FromRgb(120, 120, 110); var share = new Dictionary<string, double>();
            try
            {
                foreach (double s in new[] { 1.0, 1.15, 1.35, 1.6 })
                    foreach (bool old in new[] { true, false })
                    {
                        if (s == 1.0 && old) continue;
                        CrispText.TestDisable = old;
                        if (s == 1.0) clock.ResetSize(false); else clock.SetCustomSize(clock.DefaultWidth * s, clock.DefaultHeight * s, false);
                        clock.ApplyCrispText(); await Task.Delay(250);
                        TextProbe.SaveName = string.Format("screen-clock-x{0}-{1}", s, old ? "2.2.2" : "2.2.3");
                        double v = await TextProbe.ScreenSharpness(clock, back);
                        share[s + (old ? "o" : "n")] = TextProbe.LastInk;
                        check(true, string.Format("text sharpness (screen): clock x{0:0.00} {1} solid-ink share = {2:F3}", s, old ? "2.2.2" : "2.2.3", TextProbe.LastInk));
                    }
            }
            finally { CrispText.TestDisable = false; clock.SetFrameless(false); clock.SetTextShadow(true); clock.ResetSize(false); try { clock.Close(); } catch { } }
            // the same comparison for the other widgets (frameless, so the glass backdrop does not add noise)
            var gain = new List<double>(); var loss = new List<string>();
            TextProbe.DigitsArea = false;
            try
            {
                foreach (var kind in new[] { "calendar", "todo", "countdown", "schedule", "clock" })
                {
                    var w = Program.NewWidget(kind); if (w == null) continue;
                    try
                    {
                        if (w.TestToast != null) w.TestToast.Opacity = 0; w.SetFrameless(true); w.SetTextShadow(false);
                        foreach (double s in new[] { 1.15, 1.35, 1.6 })
                        {
                            var v = new double[2];
                            for (int m = 0; m < 2; m++)
                            {
                                CrispText.TestDisable = m == 0; w.SetCustomSize(w.DefaultWidth * s, w.DefaultHeight * s, false); w.ApplyCrispText(); await Task.Delay(250);
                                TextProbe.SaveName = null; await TextProbe.ScreenSharpness(w, Color.FromRgb(120, 120, 110)); v[m] = TextProbe.LastInk;
                            }
                            check(true, string.Format("text sharpness (screen, all text): {0} x{1:0.00}  2.2.2 = {2:F3}  2.2.3 = {3:F3}", kind, s, v[0], v[1]));
                            gain.Add(v[1] - v[0]);
                        }
                    }
                    finally { CrispText.TestDisable = false; try { w.SetFrameless(false); w.SetTextShadow(true); w.ResetSize(false); w.Close(); } catch { } }
                }
            }
            finally { TextProbe.DigitsArea = true; }
            if (gain.Count > 0) check(true, string.Format("text sharpness (screen, all text): mean change in solid-ink share = {0:+0.000;-0.000} over {1} cases, {2} better", gain.Average(), gain.Count, gain.Count(g => g > 0)));
        }

        /// <summary>The frameless text shadow: 2.2.2's 7 px blur against the 2.2.3 tight shadow, measured on screen.</summary>
        static async Task ShadowRun(Action<bool, string> check)
        {
            var clock = Program.NewWidget("clock"); if (clock.TestToast != null) clock.TestToast.Opacity = 0; clock.SetFrameless(true);
            double ob = WidgetLook.ShadowBlur, oo = WidgetLook.ShadowOpacity; var obias = WidgetLook.ShadowBias; var back = Color.FromRgb(120, 120, 110);
            var halo = new Dictionary<string, double>(); var solid = new Dictionary<string, double>();
            try
            {
                clock.ResetSize(false); TextProbe.DigitsArea = true; TextProbe.SaveName = null;
                foreach (var v in new[] { new object[] { "none", 0.0, 0.0, RenderingBias.Quality }, new object[] { "2.2.2", 7.0, .6, RenderingBias.Performance }, new object[] { "2.2.3", ob, oo, obias } })
                {
                    string name = (string)v[0];
                    WidgetLook.ShadowBlur = (double)v[1]; WidgetLook.ShadowOpacity = (double)v[2]; WidgetLook.ShadowBias = (RenderingBias)v[3];
                    clock.SetTextShadow(name != "none"); await Task.Delay(250);
                    TextProbe.SaveName = "screen-clock-shadow-" + name;
                    await TextProbe.ScreenSharpness(clock, back); solid[name] = TextProbe.LastInk; halo[name] = TextProbe.LastHalo;
                    check(true, string.Format("text shadow (screen): clock x1.00 shadow {0}: solid-ink share {1:F3}, dark halo {2:F2} px²/ink px", name, solid[name], halo[name]));
                }
                check(halo["2.2.3"] < halo["2.2.2"], string.Format("text shadow: the 2.2.3 shadow smears less than 2.2.2's (halo {0:F2} < {1:F2}, no shadow = {2:F2})", halo["2.2.3"], halo["2.2.2"], halo["none"]));
                check(halo["2.2.3"] > halo["none"], "text shadow: the 2.2.3 shadow is still there (legibility on bright wallpapers)");
            }
            finally { WidgetLook.ShadowBlur = ob; WidgetLook.ShadowOpacity = oo; WidgetLook.ShadowBias = obias; TextProbe.SaveName = null; try { clock.SetFrameless(false); clock.SetTextShadow(true); clock.Close(); } catch { } }
        }

        static List<TextBlock> AllText(DependencyObject root) { var l = new List<TextBlock>(); Walk(root, l); return l; }
        static void Walk(DependencyObject o, List<TextBlock> l) { var tb = o as TextBlock; if (tb != null) l.Add(tb); int n = VisualTreeHelper.GetChildrenCount(o); for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(o, i), l); }

        static BitmapSource Solid(int size, byte alpha)
        {
            var px = new byte[size * size * 4]; for (int i = 0; i < px.Length; i += 4) { px[i] = 200; px[i + 1] = 90; px[i + 2] = 30; px[i + 3] = alpha; }
            var b = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, px, size * 4); b.Freeze(); return b;
        }

        // ---------------------------------------------------------------- icon fallback chain
        static void IconRules(Action<bool, string> check)
        {
            var oldExe = AppIcons.ExeIcon; var oldPkg = AppIcons.PackageIcon; var oldWin = AppIcons.WindowIcon; var oldLink = AppIcons.ShortcutIcon; var oldGeneric = AppIcons.IsGenericExe;
            try
            {
                var calls = new List<string>();
                AppIcons.IsGenericExe = p => false;
                AppIcons.ExeIcon = p => { calls.Add("exe"); return null; };
                AppIcons.PackageIcon = a => { calls.Add("pkg"); return null; };
                AppIcons.WindowIcon = w => { calls.Add("win"); return w.ToInt64() == 2 ? Solid(32, 255) : null; };
                AppIcons.ShortcutIcon = r => { calls.Add("lnk"); return Solid(48, 255); };
                string how;
                var req = new AppIconRequest { Exe = @"C:\apps\anti.exe", AppId = "Anti.App", Name = "Anti", Windows = new[] { new IntPtr(1), new IntPtr(2) } };
                var got = AppIcons.Resolve(req, out how);
                check(got != null && calls.SequenceEqual(new[] { "exe", "win", "win" }) && how.StartsWith("window icon"), "dock icons: exe icon missing → the window's own icon is used (" + how + ")");
                calls.Clear(); AppIcons.WindowIcon = w => { calls.Add("win"); return null; };
                got = AppIcons.Resolve(req, out how);
                check(got != null && calls.SequenceEqual(new[] { "exe", "win", "win", "pkg", "lnk" }) && how == "Start menu shortcut", "dock icons: exe, window, package, then the Start menu shortcut are tried in that order");
                calls.Clear(); AppIcons.ShortcutIcon = r => { calls.Add("lnk"); return null; };
                got = AppIcons.Resolve(req, out how);
                check(got == null && how == null && calls.Last() == "exe", "dock icons: when every source fails the result is null (the Dock then shows a letter tile and retries)");
                // blank images count as failures
                AppIcons.ExeIcon = p => Solid(256, 0); AppIcons.ShortcutIcon = r => Solid(48, 255);
                got = AppIcons.Resolve(req, out how);
                check(got != null && how == "Start menu shortcut", "dock icons: a fully transparent icon is treated as a failed lookup");
                // generic exe icon (an exe without its own icon) is skipped until nothing else is left
                AppIcons.IsGenericExe = p => true; AppIcons.ExeIcon = p => Solid(256, 255); calls.Clear();
                AppIcons.WindowIcon = w => Solid(32, 255); got = AppIcons.Resolve(req, out how);
                check(got != null && how.StartsWith("window icon"), "dock icons: the shell's generic 'application' icon does not win over the window's icon");
                AppIcons.WindowIcon = w => null; AppIcons.ShortcutIcon = r => null; AppIcons.PackageIcon = a => null;
                got = AppIcons.Resolve(req, out how);
                check(got != null && how == "exe icon (generic)", "dock icons: the generic icon is still the last resort");
                // UWP: ApplicationFrameHost windows use the package icon, never the host exe
                AppIcons.IsGenericExe = p => false; calls.Clear(); AppIcons.ExeIcon = p => { calls.Add("exe"); return Solid(256, 255); }; AppIcons.PackageIcon = a => { calls.Add("pkg:" + a); return Solid(256, 255); };
                var uwp = new AppIconRequest { Exe = @"C:\Windows\System32\ApplicationFrameHost.exe", AppId = "Win.Settings!App", Name = "设置", Windows = new[] { new IntPtr(5) } };
                got = AppIcons.Resolve(uwp, out how);
                check(got != null && calls.SequenceEqual(new[] { "pkg:Win.Settings!App" }) && AppIcons.IsHostExe(uwp.Exe) && !AppIcons.IsHostExe(@"C:\x\chrome.exe"), "dock icons: ApplicationFrameHost windows use the app's package icon, not the host's blank window icon");
                var entry = new DockEntry { Identity = new AppIdentity { Exe = uwp.Exe, RealExe = @"C:\Windows\ImmersiveControlPanel\SystemSettings.exe", AppId = "x", Name = "n" }, Running = new RunningApp() };
                check(entry.IconRequest().Exe.EndsWith("SystemSettings.exe") && new DockEntry { Identity = new AppIdentity { Exe = @"C:\a.exe" }, Running = new RunningApp() }.IconRequest().Exe == @"C:\a.exe", "dock icons: a hosted app's icon request uses the app's own exe when it is known");
            }
            finally { AppIcons.ExeIcon = oldExe; AppIcons.PackageIcon = oldPkg; AppIcons.WindowIcon = oldWin; AppIcons.ShortcutIcon = oldLink; AppIcons.IsGenericExe = oldGeneric; }
            // the placeholder tile
            var a1 = AppIcons.Letter("Antigravity") as BitmapSource; var a2 = AppIcons.Letter("") as BitmapSource;
            check(a1 != null && a1.PixelWidth == 128 && !AppIcons.IsBlank(a1) && a2 != null && !AppIcons.IsBlank(a2) && AppIcons.IsBlank(Solid(64, 0)) && !AppIcons.IsBlank(Solid(64, 255)), "dock icons: the letter tile is a real image; blank images are detected");
        }

        static void IconRun(Action<bool, string> check)
        {
            // failure is not cached: the control shows the letter tile, the lookup is repeated and the real icon replaces the tile
            var oldPost = IconCache.Post; var oldSync = IconCache.Synchronous;
            try
            {
                IconCache.Reset(); IconCache.Post = a => a(); IconCache.Synchronous = true;
                bool ready = false; int lookups = 0;
                var image = new Image();
                IconCache.BindWith(image, "test:app", () => { lookups++; return ready ? Solid(64, 255) : null; }, "Antigravity");
                bool tile = image.Source != null && !IconCache.IsCached("test:app");
                check(tile && lookups == 1 && IconCache.FailedLookups == 1, "dock icons: a failed lookup shows a letter tile and is not cached");
                IconCache.RetryNow(); bool still = !IconCache.IsCached("test:app") && lookups == 2;
                ready = true; var before = image.Source; IconCache.RetryNow();
                check(still && IconCache.IsCached("test:app") && !ReferenceEquals(before, image.Source) && ((BitmapSource)image.Source).PixelWidth == 64 && lookups == 3, "dock icons: the retry finds the icon and replaces the tile on the same control");
                // a later bind of the same app is served from the cache without another lookup
                var second = new Image(); IconCache.BindWith(second, "test:app", () => { lookups++; return null; }, "Antigravity");
                check(second.Source == image.Source && lookups == 3, "dock icons: once found, the icon is cached");
                // a new Dock build after a failure tries again immediately (it does not reuse the failure)
                IconCache.Reset(); lookups = 0; ready = false; var a = new Image(); IconCache.BindWith(a, "test:b", () => { lookups++; return ready ? Solid(64, 255) : null; }, "B");
                ready = true; var b = new Image(); IconCache.BindWith(b, "test:b", () => { lookups++; return ready ? Solid(64, 255) : null; }, "B");
                IconCache.RetryNow();
                check(lookups >= 2 && a.Source is BitmapSource && ((BitmapSource)a.Source).PixelWidth == 64 && ((BitmapSource)b.Source).PixelWidth == 64, "dock icons: a failed lookup is retried by the next Dock build and by the retry timer");
                // retries are bounded
                IconCache.Reset(); lookups = 0; var c = new Image(); IconCache.BindWith(c, "test:never", () => { lookups++; return null; }, "N");
                for (int i = 0; i < 10; i++) IconCache.RetryNow();
                check(lookups == 5 && c.Source != null, "dock icons: a lookup that keeps failing is retried 4 times, then left on the letter tile (no endless polling)");
            }
            finally { IconCache.Post = oldPost; IconCache.Synchronous = oldSync; IconCache.Reset(); }
            // real Windows: a packaged / UWP app's icon through the shell (Settings exists on every Windows 10 / 11)
            string settings = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";
            var pkg = ShellApps.Icon(settings); string name = ShellApps.DisplayName(settings);
            if (pkg == null) check(true, "dock icons: package icon lookup skipped — Settings is not registered on this system");
            else check(!AppIcons.IsBlank(pkg) && !string.IsNullOrWhiteSpace(name) && !name.Contains("Frame Host"), "dock icons: the package icon and name of a UWP app come from the shell (" + name + ")");
            var notepad = Native.Icon(Path.Combine(Environment.SystemDirectory, "notepad.exe"));
            check(notepad != null && !AppIcons.IsBlank(notepad) && !Native.HasGenericIcon(Path.Combine(Environment.SystemDirectory, "notepad.exe")) && Native.HasGenericIcon(Path.Combine(Path.GetTempPath(), "cd223-none.exe")) == false, "dock icons: a real exe icon is found and is not mistaken for the generic one");
        }
    }
}
