using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClearDesk
{
    /// <summary>2.2.2 tests.</summary>
    internal static class V222Tests
    {
        internal static void Rules(Action<bool, string> check)
        {
            PositionRules(check); DockRules(check); LookRules(check);
        }

        // ---------------------------------------------------------------- C: position stability
        static void PositionRules(Action<bool, string> check)
        {
            var screens = new[] { new Rect(0, 0, 1920, 1080), new Rect(-1280, 0, 1280, 1024) };
            check(PositionGuard.Reachable(new Point(100, 100), screens) && PositionGuard.Reachable(new Point(-900, 200), screens) && PositionGuard.Reachable(new Point(10, 5), screens), "position: widgets on any monitor (also left of the primary, also in the taskbar strip) are reachable");
            check(!PositionGuard.Reachable(new Point(5000, 100), screens) && !PositionGuard.Reachable(new Point(double.NaN, 0), screens) && !PositionGuard.Reachable(new Point(100, 1200), screens), "position: widgets outside every monitor are not reachable");
            check(PositionGuard.IsSystemMove(false, false, true) && !PositionGuard.IsSystemMove(true, false, true) && !PositionGuard.IsSystemMove(false, true, true) && !PositionGuard.IsSystemMove(false, false, false), "position: only moves the system makes while settling are kept out of the settings");
            var at = PositionGuard.Reassert(new Point(400, 300), new Point(700, 300), screens);
            check(at.HasValue && at.Value.X == 400 && at.Value.Y == 300 && PositionGuard.Reassert(new Point(400, 300), new Point(400.2, 300), screens) == null && PositionGuard.Reassert(new Point(9000, 300), new Point(700, 300), screens) == null, "position: a nudged widget goes back to its saved place; an unusable saved place is left alone");
        }

        static void DockRules(Action<bool, string> check)
        {
            var a = new DockEntry { PinPath = @"C:\a.lnk" }; var b = new DockEntry { PinPath = @"C:\b.lnk" };
            check(DockWindow.Signature(new[] { a, b }) == DockWindow.Signature(new[] { a, b }) && DockWindow.Signature(new[] { a, b }) != DockWindow.Signature(new[] { b, a }) && DockWindow.Signature(new[] { a }) != DockWindow.Signature(new[] { a, b }), "dock: the rebuild signature changes with the shown apps and their order only");
            check(DockAutoHide.ShouldDelayShow(false, true, true, false) && !DockAutoHide.ShouldDelayShow(true, true, true, false) && !DockAutoHide.ShouldDelayShow(false, false, true, false) && !DockAutoHide.ShouldDelayShow(false, true, true, true) && !DockAutoHide.ShouldDelayShow(false, true, false, false), "dock: coming back from auto-hide is delayed; hiding and pointer peeks are immediate");
        }

        static string TempDir(string name) { string d = Path.Combine(Path.GetTempPath(), "cd222-" + name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(d); return d; }

        internal static async Task Run(Action<bool, string> check)
        {
            string root = TempDir("win"); string oldRoot = ToolStore.TestRoot; ToolStore.TestRoot = root; var opened = new List<GlassWindow>();
            try
            {
                await PositionRun(check, opened);
                await BackdropRun(check, opened);
                await LookRun(check, opened, root);
                await MultiRun(check, opened, root);
                await Shots(opened);
            }
            finally { foreach (var w in opened) { try { w.Close(); } catch { } } ToolStore.TestRoot = oldRoot; try { Directory.Delete(root, true); } catch { } }
        }

        // a widget of this kind that was created just now (an earlier test may have left one open): its saved state is dropped first
        static GlassWindow Fresh(string kind)
        {
            var first = Program.NewWidget(kind); string id = first.ExportName; first.Close();
            if (ToolWidgets.IsTool(id)) ToolWidgets.Set(id, false);
            Program.S.Widgets.Remove(id);
            return Program.NewWidget(id, PlaceMode.Default, true);
        }

        static async Task PositionRun(Action<bool, string> check, List<GlassWindow> opened)
        {
            // restart simulation for every widget kind: place, save, serialise like settings.json, close, reload, create again
            
            string[] kinds = WidgetCatalog.Kinds.Select(k => k.Key).Where(k => k != "note" && k != "box" && k != "music").ToArray();
            var bad = new List<string>(); int n = 0;
            foreach (var kind in kinds)
            {
                var w = Fresh(kind); opened.Add(w);
                double x = 60 + (n % 5) * 53 + 0.5 * (n % 2), y = 140 + (n % 7) * 31; n++;
                w.PlaceAt(new Point(x, y));
                Program.Save();
                var js = new JavaScriptSerializer(); var copy = js.Deserialize<Settings>(js.Serialize(Program.S));
                w.Close(); opened.Remove(w);
                BoxData saved; if (!copy.Widgets.TryGetValue(w.ExportName, out saved)) { bad.Add(kind + " (not saved)"); continue; }
                Program.S.Widgets[w.ExportName] = saved;
                var again = Program.NewWidget(w.ExportName, PlaceMode.Default, true); opened.Add(again);
                if (Math.Abs(again.Left - x) > .5 || Math.Abs(again.Top - y) > .5 || Math.Abs(again.TestState.X - x) > .5 || Math.Abs(again.TestState.Y - y) > .5) bad.Add(kind + " " + again.Left + "," + again.Top + " != " + x + "," + y);
            }
            check(bad.Count == 0, "position: every widget kind keeps its place across save → restart (" + kinds.Length + " kinds)" + (bad.Count > 0 ? ": " + string.Join("; ", bad) : ""));

            // a new widget put at the right-click spot is written to settings immediately (it used to wait for some later save)
            var old = Fresh("clock"); var dpi0 = VisualTreeHelper.GetDpi(old); old.Close();
            var wa = SystemParameters.WorkArea;
            WidgetPlacement.Record((int)((wa.Left + 333) * dpi0.DpiScaleX), (int)((wa.Top + 222) * dpi0.DpiScaleY), WidgetPlacement.NowMs());
            var placed = Program.NewWidget("clock", PlaceMode.LastRightClick); opened.Add(placed);
            var onDisk = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(Program.Config));
            BoxData diskState;
            check(onDisk.Widgets.TryGetValue("clock", out diskState) && Math.Abs(diskState.X - placed.Left) < .5 && Math.Abs(diskState.Y - placed.Top) < .5, "position: a widget placed at the right-click spot is on disk right away (" + Math.Round(placed.Left) + "," + Math.Round(placed.Top) + ")");
            WidgetPlacement.Forget();

            // unreachable saved position: the window is shown in view but the saved place is not overwritten
            placed.Close(); opened.Remove(placed);
            Program.S.Widgets["clock"].X = -9000; Program.S.Widgets["clock"].Y = -9000;
            var rescued = Program.NewWidget("clock"); opened.Add(rescued);
            Program.Save();
            var after = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(Program.Config));
            check(rescued.Left > -500 && rescued.Top > -500 && after.Widgets["clock"].X == -9000 && after.Widgets["clock"].Y == -9000, "position: an off-screen saved position is only rescued visually; the saved place stays until the user moves the widget");
            rescued.Close(); opened.Remove(rescued);
            Program.S.Widgets["clock"].X = 200; Program.S.Widgets["clock"].Y = 300;

            // the system nudging a widget while displays / scaling settle is not saved and is undone
            var clock = Program.NewWidget("clock"); opened.Add(clock);
            await Task.Delay(100);
            double sx = clock.Left, sy = clock.Top;
            clock.NoteSystemChange();
            clock.Left = sx + 160; clock.Top = sy + 90;
            await Task.Delay(150);
            check(Math.Abs(clock.TestState.X - sx) < .5 && Math.Abs(clock.TestState.Y - sy) < .5, "position: a nudge during a display change is not written to the saved position");
            for (int i = 0; i < 60 && clock.PositionRestores == 0; i++) await Task.Delay(100);
            check(clock.PositionRestores >= 1 && Math.Abs(clock.Left - sx) < .5 && Math.Abs(clock.Top - sy) < .5, "position: the widget is put back where it was saved after the display change settled (" + Math.Round(clock.Left) + "," + Math.Round(clock.Top) + ")");
            clock.Left = sx + 40; clock.Top = sy + 25;
            await Task.Delay(100);
            check(Math.Abs(clock.TestState.X - (sx + 40)) < .5 && Math.Abs(clock.TestState.Y - (sy + 25)) < .5, "position: a normal move is still saved");
        }

        // ---------------------------------------------------------------- E / F / G: look options and edge metrics
        static void LookRules(Action<bool, string> check)
        {
            check(Math.Abs(WidgetLook.ContentScale(640, 640, 320, 320, .6, 4) - 2) < 1e-9 && Math.Abs(WidgetLook.ContentScale(320, 960, 320, 320, .6, 4) - 1) < 1e-9 && WidgetLook.ContentScale(5000, 5000, 320, 320, .6, 4) == 4 && WidgetLook.ContentScale(10, 10, 320, 320, .6, 4) == .6 && WidgetLook.ContentScale(0, 0, 320, 320, .6, 4) == 1 && WidgetLook.ContentScale(100, 100, 0, 0, .6, 4) == 1, "metrics: the content scale is the smaller size ratio and stays within 0.6–4");
            check(WidgetLook.EdgeFactor(0, 0, 320, 320) == 1 && Math.Abs(WidgetLook.EdgeFactor(0, 320, 320, 320) - 1.5) < 1e-9 && WidgetLook.EdgeFactor(5000, 0, 320, 320) == 2 && WidgetLook.EdgeFactor(-50, -50, 320, 320) == 1 && WidgetLook.EdgeFactor(10, 10, 0, 0) == 1, "metrics: outer padding grows with spare room, between 1× and 2×");
            check(WidgetLook.Pad(1) == 20 && WidgetLook.Pad(1.5) == 30 && WidgetLook.Pad(9) == 40 && WidgetLook.Pad(.2) == 20, "metrics: padding is 20 at the default shape and never above 40");
            check(WidgetLook.Gap(10, 0, 320) == 10 && Math.Abs(WidgetLook.Gap(10, 160, 320) - 16) < 1e-9 && WidgetLook.Gap(10, 99999, 320) == 30 && WidgetLook.Gap(10, -50, 320) == 10, "metrics: gaps between blocks grow with spare height, at most 3×");
            check(WidgetLook.NormalizeFit("contain") == "contain" && WidgetLook.NormalizeFit("zzz") == "cover" && WidgetLook.NormalizeFit(null) == "cover" && WidgetLook.StretchFor("stretch") == Stretch.Fill && WidgetLook.StretchFor("contain") == Stretch.Uniform && WidgetLook.StretchFor("cover") == Stretch.UniformToFill, "background: fit modes map to stretch modes; unknown values fall back to 'cover'");
            check(WidgetLook.ClampOpacity(0) == WidgetLook.MinOpacity && WidgetLook.ClampOpacity(3) == 1 && WidgetLook.ClampOpacity(double.NaN) == 1 && WidgetLook.ClampDim(-1) == 0 && WidgetLook.ClampDim(5) == WidgetLook.MaxDim && WidgetLook.ClampDim(double.NaN) == WidgetLook.DefaultDim, "background: opacity and dimming are clamped");
        }

        static string WritePng(string path, int w, int h, byte b, byte g, byte r)
        {
            var px = new byte[w * h * 4]; for (int i = 0; i < px.Length; i += 4) { px[i] = (byte)(b + (i / 4 % w) * 40 / w); px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255; }
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4)));
            using (var f = File.Create(path)) enc.Save(f); return path;
        }
        static BitmapSource Render(FrameworkElement el)
        {
            el.UpdateLayout(); var bmp = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(el.ActualWidth)), Math.Max(1, (int)Math.Ceiling(el.ActualHeight)), 96, 96, PixelFormats.Pbgra32); bmp.Render(el); return bmp;
        }
        /// <summary>The widget as it looks on screen (scaled), not in layout units.</summary>
        static BitmapSource RenderWindow(GlassWindow w)
        {
            var f = (FrameworkElement)w.Content; f.UpdateLayout(); int W = Math.Max(1, (int)Math.Ceiling(w.ActualWidth)), H = Math.Max(1, (int)Math.Ceiling(w.ActualHeight));
            var dv = new DrawingVisual(); using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(f) { Stretch = Stretch.Fill }, null, new Rect(0, 0, W, H));
            var bmp = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32); bmp.Render(dv); return bmp;
        }
        static int AlphaAt(BitmapSource bmp, int x, int y)
        {
            var px = new byte[4]; bmp.CopyPixels(new Int32Rect(Math.Max(0, Math.Min(bmp.PixelWidth - 1, x)), Math.Max(0, Math.Min(bmp.PixelHeight - 1, y)), 1, 1), px, 4, 0); return px[3];
        }
        /// <summary>Opaque-ish pixels in the outer 6 px ring (the frame / panel area).</summary>
        static int RingPixels(BitmapSource bmp)
        {
            int n = 0, w = bmp.PixelWidth, h = bmp.PixelHeight; var all = new byte[w * h * 4]; bmp.CopyPixels(all, w * 4, 0);
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if ((x < 6 || y < 6 || x >= w - 6 || y >= h - 6) && all[(y * w + x) * 4 + 3] > 8) n++;
            return n;
        }
        static int CenterAlpha(BitmapSource bmp) { return AlphaAt(bmp, bmp.PixelWidth / 2, bmp.PixelHeight / 2); }

        static async Task LookRun(Action<bool, string> check, List<GlassWindow> opened, string root)
        {
            // ---- menus ----
            var clock = Fresh("clock"); opened.Add(clock);
            Func<GlassWindow, string[]> heads = w => w.ContextMenu.Items.OfType<MenuItem>().Select(m => m.Header as string).Where(t => t != null).ToArray();
            check(heads(clock).Contains("自定义背景") && heads(clock).Contains("无边框 / 透明背景") && heads(clock).Contains("文字阴影（无边框时）"), "look: the widget menu has 自定义背景 and 无边框 / 透明背景");
            var perfMenu = Fresh("performance"); opened.Add(perfMenu);
            var calMenu = Fresh("calendar"); opened.Add(calMenu);
            check(heads(perfMenu).Contains("无边框 / 透明背景") && heads(calMenu).Contains("自定义背景"), "look: the same options are on the other widgets (performance, calendar)");

            // ---- 无边框: clock with a glass material ----
            clock.TestMaterial("liquid"); clock.SetLocked(false);
            await Task.Delay(150);
            var glassShot = Render(clock.TestFrame);
            check(RingPixels(glassShot) > 100, "frameless: with the glass frame the outer ring is painted (" + RingPixels(glassShot) + " px)");
            clock.SetFrameless(true); await Task.Delay(150);
            var frameless = Render(clock.TestFrame);
            check(RingPixels(frameless) < 1500 && clock.TestSurface.Visibility == Visibility.Collapsed && clock.TestFrame.BorderThickness.Left == 0, "frameless: no panel, no border — the outer ring holds no frame (the glass frame paints ~5900 px; only the overhang of a long weekday name such as Wednesday, ~100 px, may touch it) (" + RingPixels(frameless) + " px)");
            check(clock.TestLayout.Effect != null, "frameless: the content gets a soft text shadow for readability");
            var hit = VisualTreeHelper.HitTest((Visual)clock.Content, new Point(6, clock.ActualHeight / 2));
            check(hit != null, "frameless: empty areas still receive the pointer, so the widget can be dragged / right-clicked");
            clock.SetTextShadow(false); check(clock.TestLayout.Effect == null, "frameless: the text shadow can be switched off");
            clock.SetTextShadow(true);
            clock.SetLocked(true); check(clock.IsLocked && clock.IsFrameless, "frameless: lock works the same"); clock.SetLocked(false);
            var js = new JavaScriptSerializer(); var copy = js.Deserialize<Settings>(js.Serialize(Program.S));
            check(copy.Widgets["clock"].Frameless && !copy.Widgets["clock"].NoTextShadow, "frameless: the option is saved with the widget");
            clock.Close(); opened.Remove(clock);
            Program.S.Widgets["clock"] = copy.Widgets["clock"];
            var again = Program.NewWidget("clock"); opened.Add(again); await Task.Delay(150);
            check(again.IsFrameless && RingPixels(Render(again.TestFrame)) < 1500, "frameless: restored after a restart");
            again.SetFrameless(false); await Task.Delay(100);
            check(RingPixels(Render(again.TestFrame)) > 100 && again.TestSurface.Visibility == Visibility.Visible, "frameless: switching it off brings the frame back");

            // ---- custom background ----
            string pics = Path.Combine(root, "bgpics"); Directory.CreateDirectory(pics);
            string red = WritePng(Path.Combine(pics, "中文 背景.png"), 400, 200, 30, 40, 220), blue = WritePng(Path.Combine(pics, "blue.png"), 300, 300, 220, 60, 30);
            var perf = perfMenu; perf.SetFrameless(true); await Task.Delay(100);
            check(perf.SetBackgroundFile(red) && perf.HasBackgroundImage, "background: a picture file (Chinese name) becomes the widget background");
            string stored = Path.Combine(WidgetLook.Folder, perf.TestState.BgImage);
            check(File.Exists(stored) && WidgetLook.IsStored(stored) && perf.TestState.BgImage != red, "background: the picture is copied into ClearDesk's data folder (" + perf.TestState.BgImage + ")");
            await Task.Delay(150);
            var withBg = Render(perf.TestFrame);
            check(CenterAlpha(withBg) > 200 && RingPixels(withBg) > 0 && AlphaAt(withBg, 1, 1) < 20, "background + frameless: the picture is shown with rounded corners, without a panel (center alpha " + CenterAlpha(withBg) + ")");
            // resize: the picture follows the widget
            double w0 = perf.ActualWidth, h0 = perf.ActualHeight;
            perf.SetCustomSize(perf.DefaultWidth * 1.8, perf.DefaultHeight * 1.8, false); await Task.Delay(150); perf.UpdateLayout();
            double rw = perf.TestRoot.ActualWidth, rh = perf.TestRoot.ActualHeight;
            check(perf.ActualWidth > w0 * 1.5 && Math.Abs(perf.TestBackgroundImage.ActualWidth - rw) < 1 && Math.Abs(perf.TestBackgroundImage.ActualHeight - rh) < 1, "background: the picture scales with the widget (" + Math.Round(perf.TestBackgroundImage.ActualWidth) + "x" + Math.Round(perf.TestBackgroundImage.ActualHeight) + " for " + Math.Round(rw) + "x" + Math.Round(rh) + ")");
            perf.SetBackgroundFit("contain"); check(perf.TestBackgroundBrush.Stretch == Stretch.Uniform && perf.TestState.BgFit == "contain", "background: fit mode 适应");
            perf.SetBackgroundFit("stretch"); check(perf.TestBackgroundBrush.Stretch == Stretch.Fill, "background: fit mode 拉伸");
            perf.SetBackgroundFit("cover");
            perf.SetBackgroundOpacity(.4); perf.SetBackgroundDim(.6);
            check(Math.Abs(perf.TestBackgroundImage.Opacity - .4) < .001 && ((SolidColorBrush)((Border)((Panel)perf.TestBackgroundImage.Parent).Children[1]).Background).Color.A == (byte)Math.Round(.6 * 255), "background: opacity and dimming apply");
            // another widget has its own picture
            var cal = Fresh("calendar"); opened.Add(cal);
            cal.SetBackgroundFile(blue);
            check(cal.TestState.BgImage != perf.TestState.BgImage && File.Exists(Path.Combine(WidgetLook.Folder, perf.TestState.BgImage)) && File.Exists(Path.Combine(WidgetLook.Folder, cal.TestState.BgImage)), "background: every widget keeps its own picture");
            // survives deletion of the original, a restart and a locked widget
            File.Delete(red);
            perf.SetLocked(true);
            var copy2 = js.Deserialize<Settings>(js.Serialize(Program.S));
            perf.Close(); opened.Remove(perf);
            Program.S.Widgets["performance"] = copy2.Widgets["performance"];
            var perf2 = Program.NewWidget("performance"); opened.Add(perf2); await Task.Delay(200);
            check(perf2.HasBackgroundImage && perf2.IsLocked && perf2.IsFrameless && Math.Abs(perf2.TestState.BgOpacity - .4) < .001 && perf2.TestState.CustomW > 0, "background: restored after a restart (original file deleted, locked, own size, opacity)");
            // replacing removes the old copy; clearing removes the file and the layer
            string firstFile = Path.Combine(WidgetLook.Folder, perf2.TestState.BgImage);
            perf2.SetLocked(false); perf2.SetBackgroundFile(blue);
            check(!File.Exists(firstFile) && perf2.HasBackgroundImage, "background: choosing another picture removes the old copy");
            string secondFile = Path.Combine(WidgetLook.Folder, perf2.TestState.BgImage);
            perf2.ClearBackground(); await Task.Delay(50);
            check(!File.Exists(secondFile) && !perf2.HasBackgroundImage && perf2.TestState.BgImage == "" && File.Exists(Path.Combine(WidgetLook.Folder, cal.TestState.BgImage)), "background: removing it deletes the copy and nothing else");
            // drop / paste / bad input
            check(cal.DropBackground(new DataObject(DataFormats.FileDrop, new[] { red, blue })) && cal.HasBackgroundImage, "background: dropping picture files sets the background (first usable file)");
            var txt = Path.Combine(pics, "note.txt"); File.WriteAllText(txt, "x");
            string before = cal.TestState.BgImage;
            check(!cal.SetBackgroundFile(txt) && cal.TestState.BgImage == before && !cal.DropBackground(new DataObject(DataFormats.FileDrop, new[] { txt })), "background: a non-picture is refused and the current background stays");
            File.WriteAllBytes(Path.Combine(pics, "broken.png"), new byte[] { 1, 2, 3, 4, 5 });
            bool threw = false, accepted = false; string keep = cal.TestState.BgImage; try { accepted = cal.SetBackgroundFile(Path.Combine(pics, "broken.png")); } catch { threw = true; }
            check(!threw && !accepted && cal.TestState.BgImage == keep, "background: a broken picture file is refused without an error and the current background stays");
            check(cal.DropBackground(new DataObject(DataFormats.Bitmap, BitmapSource.Create(40, 30, 96, 96, PixelFormats.Bgra32, null, new byte[40 * 30 * 4], 40 * 4))), "background: dropped picture data (not a file) is stored as a PNG");
            bool clip = false; try { Clipboard.SetImage(BitmapSource.Create(20, 20, 96, 96, PixelFormats.Bgra32, null, new byte[20 * 20 * 4], 20 * 4)); clip = true; } catch { }
            if (clip) check(cal.PasteBackground() && cal.HasBackgroundImage, "background: pasting a copied picture sets the background"); else check(true, "background: clipboard busy here — paste check skipped");
            // frameless + background on a widget with the default (glass) look, then un-frame
            cal.SetFrameless(false); await Task.Delay(100);
            check(cal.HasBackgroundImage && (cal.TestSurface == null || cal.TestSurface.Visibility == Visibility.Visible), "background: also works with the glass frame on");
            // the sticky note and the organizer take a background too (but have no frameless switch)
            var noteData = new BoxData { Title = "n", X = 60, Y = 60 }; var note = new NoteWindow(noteData); note.Show(); opened.Add(note);
            check(note.SetBackgroundFile(blue) && note.HasBackgroundImage && !heads(note).Contains("无边框 / 透明背景") && heads(note).Contains("自定义背景"), "background: stickies take a picture too (no frameless switch for stickies)");
            var orgData = new BoxData { Title = "o", X = 80, Y = 80 }; var org = new OrganizerWindow(orgData); org.Show(); opened.Add(org);
            check(org.SetBackgroundFile(blue) && org.HasBackgroundImage && heads(org).Contains("无边框 / 透明背景"), "background: the desktop organizer takes a picture and can be frameless");
            WidgetLook.Prune("zzz-none", null);

            // ---- edge metrics follow the widget size ----
            var p3 = Fresh("calendar"); p3.ClearBackground(); await Task.Delay(100);
            double basePad = p3.TestLayout.Margin.Left;
            p3.SetCustomSize(p3.DefaultWidth * 1, p3.DefaultHeight * 3, false); await Task.Delay(150);
            double tallPad = p3.TestLayout.Margin.Left;
            p3.SetCustomSize(p3.DefaultWidth * 3, p3.DefaultHeight * 3, false); await Task.Delay(150);
            double bigPad = p3.TestLayout.Margin.Left;
            p3.ResetSize(false); await Task.Delay(100);
            check(basePad == 20 && tallPad > 20 && tallPad <= 40 && bigPad == 20 && p3.TestLayout.Margin.Left == 20 && Math.Abs(p3.ContentScale - 1) < .001, "edges: padding is " + basePad + " by default, " + tallPad + " on a tall widget, " + bigPad + " when everything scales together, and returns to 20 after reset");
            var wide = Fresh("calendar"); double s4 = wide.ContentScale;
            wide.SetCustomSize(wide.DefaultWidth * 4, wide.DefaultHeight * 4, false); await Task.Delay(100);
            check(Math.Abs(wide.ContentScale - 4) < .001, "edges: the content keeps growing up to 4× (frame and text together)");
            wide.ResetSize(false);
        }

        // ---------------------------------------------------------------- D: several instances of a widget
        static GlassWindow Shown(string id) { return Program.OpenWindows.FirstOrDefault(w => !w.WasClosed && w.ExportName == id); }
        static void Wipe(string kind)
        {
            foreach (var id in ToolWidgets.Instances(kind).Concat(new[] { kind }).Distinct().ToList())
            {
                foreach (var w in Program.OpenWindows.Where(x => x.ExportName == id).ToList()) w.Close();
                ToolWidgets.Set(id, false); Program.S.Widgets.Remove(id);
                try { File.Delete(Path.Combine(ToolStore.Root, id + ".json")); } catch { }
                try { var d = AlbumStore.FolderFor(id); if (kind == "album" && Directory.Exists(d)) Directory.Delete(d, true); } catch { }
            }
        }

        static async Task MultiRun(Action<bool, string> check, List<GlassWindow> opened, string root)
        {
            check(ToolWidgets.NumberOf("album") == 1 && ToolWidgets.NumberOf("album~3") == 3 && ToolWidgets.KindOf("album~3") == "album" && ToolWidgets.InstanceId("album", 1) == "album" && ToolWidgets.InstanceId("album", 4) == "album~4" && ToolWidgets.IsMulti("todo~2") && !ToolWidgets.IsMulti("clipboard") && !ToolWidgets.IsMulti("netspeed") && ToolWidgets.IsTool("launcher~2") && !ToolWidgets.IsTool("clock~2"), "multi-instance: instance ids (album, album~2 …) and which kinds allow several");
            foreach (var kind in ToolWidgets.Multi) Wipe(kind);
            string pics = Path.Combine(root, "mpics"); Directory.CreateDirectory(pics);
            string[] imgs = Enumerable.Range(0, 4).Select(i => WritePng(Path.Combine(pics, "m" + i + ".png"), 120 + i * 10, 80, (byte)(40 * i), 60, 200)).ToArray();

            // an album as 2.2.1 saved it: album.json + album-images — must come back unchanged as the first instance
            Directory.CreateDirectory(AlbumStore.Folder);
            string legacyPic = Path.Combine(AlbumStore.Folder, "legacy-0000.png"); File.Copy(imgs[0], legacyPic, true);
            ToolStore.Save("album", new AlbumData { IntervalSeconds = 11, Shuffle = true });
            var a1 = Program.NewWidget("album"); opened.Add(a1); await Task.Delay(150);
            var d1 = ((AlbumWindow)a1).Data;
            check(a1.ExportName == "album" && d1.IntervalSeconds == 11 && d1.Shuffle && ((AlbumWindow)a1).TestFileCount == 1, "multi-instance: an existing single album becomes the first instance, with its settings and pictures intact");
            // "new" now makes another one
            var a2 = Program.NewWidget("album"); opened.Add(a2); await Task.Delay(150);
            var a3 = Program.NewWidget("album"); opened.Add(a3); await Task.Delay(150);
            check(a2.ExportName == "album~2" && a3.ExportName == "album~3" && !ReferenceEquals(a1, a2) && !ReferenceEquals(a2, a3) && Shown("album") == a1, "multi-instance: 新建 a second and a third album (album~2, album~3) while the first stays");
            check(a2.Title.EndsWith("图片相册 2") && a3.Title.EndsWith("图片相册 3") && (Math.Abs(a2.TestState.X - a1.TestState.X) > 10 || Math.Abs(a2.TestState.Y - a1.TestState.Y) > 10), "multi-instance: the new ones are numbered and do not sit exactly on top of the first");
            var w2 = (AlbumWindow)a2; var w3 = (AlbumWindow)a3;
            check(w2.TestFileCount == 0 && w3.TestFileCount == 0 && w2.Data.IntervalSeconds != 11, "multi-instance: a new album starts empty with default settings");
            w2.AddPaths(new[] { imgs[1], imgs[2] }); w3.AddPaths(new[] { imgs[3] }); w3.SetInterval(5); w2.SetFill(false);
            check(w2.TestFileCount == 2 && w3.TestFileCount == 1 && ((AlbumWindow)a1).TestFileCount == 1, "multi-instance: pictures are separate per album (" + ((AlbumWindow)a1).TestFileCount + "/" + w2.TestFileCount + "/" + w3.TestFileCount + ")");
            check(Directory.GetFiles(AlbumStore.FolderFor("album~2")).Length == 2 && Directory.GetFiles(AlbumStore.FolderFor("album~3")).Length == 1 && AlbumStore.FolderFor("album~2") != AlbumStore.FolderFor("album~3") && AlbumStore.FolderFor("album") == AlbumStore.Folder, "multi-instance: every album has its own picture folder (the first keeps album-images)");
            check(ToolStore.Load<AlbumData>("album~3").IntervalSeconds == 5 && ToolStore.Load<AlbumData>("album").IntervalSeconds == 11 && !ToolStore.Load<AlbumData>("album~2").Fill && ToolStore.Load<AlbumData>("album").Fill, "multi-instance: settings are saved per album");
            a2.SetBackgroundFile(imgs[0]); a3.SetBackgroundFile(imgs[1]);
            check(a2.TestState.BgImage.StartsWith("album~2-") && a3.TestState.BgImage.StartsWith("album~3-") && a1.TestState.BgImage == "", "multi-instance: custom backgrounds are per instance too");
            a2.TestState.X = 111; a2.TestState.Y = 222; a2.SetLocked(true); a3.SetCustomSize(a3.DefaultWidth * 1.5, a3.DefaultHeight * 1.5, false); Program.Save();

            // settings → restart → the same three come back
            var js = new JavaScriptSerializer(); var copy = js.Deserialize<Settings>(js.Serialize(Program.S));
            check(new[] { "album", "album~2", "album~3" }.All(copy.ToolWidgets.Contains) && copy.Widgets["album~2"].Locked && copy.Widgets["album~3"].CustomW > 0 && !copy.Widgets["album"].Locked, "multi-instance: all instances, their lock and size are in the saved settings");
            foreach (var w in new[] { a1, a2, a3 }) { w.Close(); opened.Remove(w); }
            Program.S.ToolWidgets = copy.ToolWidgets.ToList(); foreach (var kv in copy.Widgets) Program.S.Widgets[kv.Key] = kv.Value;
            var back = new List<GlassWindow>(); foreach (var id in Program.S.ToolWidgets.Where(i => ToolWidgets.KindOf(i) == "album").ToList()) { var w = ToolWidgets.Create(id); Program.AddWindow(w); back.Add(w); opened.Add(w); }
            await Task.Delay(200);
            check(back.Select(w => w.ExportName).OrderBy(x => x).SequenceEqual(new[] { "album", "album~2", "album~3" }) && ((AlbumWindow)back.First(w => w.ExportName == "album~2")).TestFileCount == 2 && ((AlbumWindow)back.First(w => w.ExportName == "album~3")).Data.IntervalSeconds == 5 && ((AlbumWindow)back.First(w => w.ExportName == "album")).Data.IntervalSeconds == 11, "multi-instance: after a restart every album has its own pictures and settings");
            a1 = back.First(w => w.ExportName == "album"); a2 = back.First(w => w.ExportName == "album~2"); a3 = back.First(w => w.ExportName == "album~3");
            check(a2.IsLocked && Math.Abs(a2.Left - 111) < .5 && a2.HasBackgroundImage && a3.HasBackgroundImage && a3.TestState.CustomW > 0 && !a1.IsLocked, "multi-instance: position, lock, size and background of each album are restored");

            // 组件管理
            var entries = WidgetManager.List().Where(e => e.Kind == "album").ToList();
            check(entries.Select(e => e.Id).SequenceEqual(new[] { "album", "album~2", "album~3" }) && entries.Select(e => e.Name).SequenceEqual(new[] { "图片相册", "图片相册 2", "图片相册 3" }) && entries.All(e => e.Visible), "multi-instance: 组件管理 lists every instance");
            var e2 = entries[1]; WidgetManager.SetVisible(e2, false); await Task.Delay(50);
            check(Shown("album~2") == null && Shown("album") != null && Shown("album~3") != null && !WidgetManager.List().First(e => e.Id == "album~2").Visible, "multi-instance: closing one instance leaves the others");
            WidgetManager.SetVisible(WidgetManager.List().First(e => e.Id == "album~2"), true); await Task.Delay(150);
            var a2b = Shown("album~2"); if (a2b != null) opened.Add(a2b);
            check(a2b != null && ((AlbumWindow)a2b).TestFileCount == 2 && Shown("album~3") != null, "multi-instance: showing it again brings back the same pictures (no new instance)");
            string folder2 = AlbumStore.FolderFor("album~2");
            WidgetManager.Delete(WidgetManager.List().First(e => e.Id == "album~2"), null); await Task.Delay(50);
            check(Shown("album~2") == null && !Directory.Exists(folder2) && !File.Exists(Path.Combine(ToolStore.Root, "album~2.json")) && !Program.S.Widgets.ContainsKey("album~2") && !ToolWidgets.On("album~2") && ToolStore.Load<AlbumData>("album").IntervalSeconds == 11 && Directory.GetFiles(AlbumStore.FolderFor("album~3")).Length == 1 && Directory.GetFiles(AlbumStore.Folder).Length == 1, "multi-instance: deleting an extra instance removes only its own data");
            var a2c = Program.NewWidget("album"); opened.Add(a2c); await Task.Delay(100);
            check(a2c.ExportName == "album~2" && ((AlbumWindow)a2c).TestFileCount == 0, "multi-instance: the freed number is reused with an empty album");

            // the other multi kinds
            foreach (var kind in new[] { "todo", "countdown", "schedule", "launcher" })
            {
                var i1 = Program.NewWidget(kind); var i2 = Program.NewWidget(kind); opened.Add(i1); opened.Add(i2);
                check(i1.ExportName == kind && i2.ExportName == kind + "~2" && !ReferenceEquals(i1, i2) && i1.TestState != i2.TestState && Program.S.ToolWidgets.Contains(kind + "~2"), "multi-instance: " + kind + " can be created twice (" + i1.ExportName + ", " + i2.ExportName + ")");
            }
            var t1 = (TodoWindow)Shown("todo"); var t2 = (TodoWindow)Shown("todo~2");
            t1.TestAdd("买牛奶"); t2.TestAdd("写报告"); t2.TestAdd("开会");
            check(ToolStore.Load<TodoData>("todo").Items.Count == 1 && ToolStore.Load<TodoData>("todo~2").Items.Count == 2 && t1.Data.Items[0].Text == "买牛奶" && t2.Data.Items[0].Text == "写报告", "multi-instance: each 待办 list keeps its own items");
            var c1 = (CountdownWindow)Shown("countdown"); var c2 = (CountdownWindow)Shown("countdown~2");
            c1.SetAll(30, 6, 20, 3); c2.SetAll(50, 10, 25, 5);
            check(ToolStore.Load<CountdownData>("countdown").WorkMinutes == 30 && ToolStore.Load<CountdownData>("countdown~2").WorkMinutes == 50 && c1.Timer2.WorkMinutes == 30 && c2.Timer2.WorkMinutes == 50, "multi-instance: each 番茄钟 keeps its own settings and timer");
            var tw = Shown("todo~2"); tw.Close(); WidgetManager.Delete(WidgetManager.List().First(e => e.Id == "todo~2"), null);
            check(ToolStore.Load<TodoData>("todo").Items.Count == 1 && ToolStore.Load<TodoData>("todo~2").Items.Count == 0, "multi-instance: deleting 待办 2 does not touch 待办 1");

            // single-instance widgets stay single
            foreach (var kind in new[] { "clipboard", "netspeed", "sunair" })
            {
                var s1 = Program.NewWidget(kind); var s2 = Program.NewWidget(kind); opened.Add(s1);
                check(ReferenceEquals(s1, s2) && s1.ExportName == kind, "multi-instance: " + kind + " stays a single widget");
            }
            foreach (var kind in ToolWidgets.Multi) Wipe(kind);
        }

        static async Task Shots(List<GlassWindow> opened)
        {
            await Task.Yield();
            string shots = Environment.GetEnvironmentVariable("CD_SHOT_DIR");
            if (string.IsNullOrEmpty(shots) || !Directory.Exists(shots)) return;
            Action<GlassWindow, string> save = (el, name) => { if (el.TestToast != null) el.TestToast.Opacity = 0; var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(RenderWindow(el))); using (var f = File.Create(Path.Combine(shots, name))) enc.Save(f); };
            // pomodoro / countdown at several shapes
            var cd = Program.NewWidget("countdown"); opened.Add(cd);
            var cw = (CountdownWindow)cd; cw.TestShowTab(true);
            foreach (var size in new[] { new[] { 1.0, 1.0 }, new[] { 1.0, 2.0 }, new[] { 2.0, 2.0 }, new[] { 2.5, 1.0 }, new[] { 3.5, 3.5 } })
            {
                cd.SetCustomSize(cd.DefaultWidth * size[0], cd.DefaultHeight * size[1], false); await Task.Delay(250);
                save(cd, "pomodoro-" + size[0] + "x" + size[1] + ".png");
            }
            cd.ResetSize(false);
            string pic = WritePng(Path.Combine(shots, "bg-sample.png"), 640, 360, 200, 120, 60);
            var clock = Program.NewWidget("clock"); clock.TestMaterial("liquid");
            await Task.Delay(200); save(clock, "clock-glass.png");
            clock.SetFrameless(true); await Task.Delay(200); save(clock, "clock-frameless.png");
            clock.SetBackgroundFile(pic); await Task.Delay(200); save(clock, "clock-frameless-bg.png");
            clock.SetFrameless(false); await Task.Delay(200); save(clock, "clock-glass-bg.png");
            clock.SetCustomSize(clock.DefaultWidth * 1.6, clock.DefaultHeight * 2.4, false); await Task.Delay(200); save(clock, "clock-glass-bg-tall.png");
            clock.ClearBackground(); clock.ResetSize(false);
        }

        static async Task BackdropRun(Action<bool, string> check, List<GlassWindow> opened)
        {
            // live blur: the blur window must stay directly under its widget when the widget's z-order is changed (activation / pin to bottom)
            var w = new ClockWindow(); w.Show(); opened.Add(w);
            await Task.Delay(200);
            var live = new LiveBehind((FrameworkElement)w.Content);
            if (!live.Enabled) { live.Dispose(); check(true, "live blur: DWM host backdrop not available here — z-order check skipped"); return; }
            live.Sync(); await Task.Delay(100);
            var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            bool initially = live.IsDirectlyBelowOwner;
            int ok = 0;
            foreach (var after in new[] { new IntPtr(1), new IntPtr(0), new IntPtr(1), new IntPtr(-2) })     // HWND_BOTTOM, HWND_TOP, HWND_BOTTOM, NOTOPMOST
            {
                Native.SetWindowPos(h, after, 0, 0, 0, 0, 0x13); await Task.Delay(30);
                if (live.IsDirectlyBelowOwner) ok++;
            }
            // reproduction: without the reorder hook (2.2.1) the blur window is left above / away from the widget
            int broken = 0; LiveBehind.TestDisableReorder = true;
            try
            {
                foreach (var after in new[] { new IntPtr(1), new IntPtr(0), new IntPtr(1) }) { Native.SetWindowPos(h, after, 0, 0, 0, 0, 0x13); await Task.Delay(30); if (!live.IsDirectlyBelowOwner) broken++; }
            }
            finally { LiveBehind.TestDisableReorder = false; }
            live.Sync(); Native.SetWindowPos(h, new IntPtr(1), 0, 0, 0, 0, 0x13); await Task.Delay(30);
            check(broken > 0, "live blur (reproduction): without the fix the blur window falls out of place on " + broken + " of 3 z-order changes — the flash on activation");
            check(initially && ok == 4, "live blur: the blur window stays directly below its widget through every z-order change (" + ok + "/4)");
            live.Dispose();

            // dock: unrelated window changes must not rebuild the Dock (that reset the magnification and made it jump)
            var dock = Program.OpenWindows.OfType<DockWindow>().FirstOrDefault();
            if (dock != null)
            {
                dock.Rebuild(); int rebuilds = dock.RebuildCount, skipped = dock.SkippedRebuilds; bool any = false;
                for (int i = 0; i < 5; i++) any |= dock.RebuildIfChanged();
                check(!any && dock.RebuildCount == rebuilds && dock.SkippedRebuilds == skipped + 5, "dock: five 'running apps changed' notifications with the same apps cause no rebuild (" + (dock.SkippedRebuilds - skipped) + " skipped)");
            }
        }
    }
}
