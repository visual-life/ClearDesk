using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClearDesk
{
    /// <summary>
    /// Text sharpness probe. A widget is drawn at its real size and again at 4× resolution; the 4× picture, averaged down, is what the text would look like with
    /// perfect anti-aliasing and no snapping. Sharpness = edge energy of the real drawing ÷ edge energy of that reference: 1.0 = as crisp as ideal text,
    /// clearly below 1 = blurred (resampling, effects, fractional positions), above 1 = hinted to the pixel grid (crisper).
    /// </summary>
    internal static class TextProbe
    {
        internal static BitmapSource RenderAt(FrameworkElement f, double w, double h, double dpi)
        {
            f.UpdateLayout(); double k = dpi / 96;
            int W = Math.Max(1, (int)Math.Ceiling(w * k)), H = Math.Max(1, (int)Math.Ceiling(h * k));
            var dv = new DrawingVisual(); using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(f) { Stretch = Stretch.Fill }, null, new Rect(0, 0, W / k, H / k));
            var bmp = new RenderTargetBitmap(W, H, dpi, dpi, PixelFormats.Pbgra32); bmp.Render(dv); return bmp;
        }

        static double[] Luma(BitmapSource bmp, out int w, out int h)
        {
            w = bmp.PixelWidth; h = bmp.PixelHeight; var px = new byte[w * h * 4]; new FormatConvertedBitmap(bmp, PixelFormats.Pbgra32, null, 0).CopyPixels(px, w * 4, 0);
            var L = new double[w * h]; for (int i = 0; i < L.Length; i++) L[i] = .114 * px[i * 4] + .587 * px[i * 4 + 1] + .299 * px[i * 4 + 2]; return L;
        }

        static double Energy(double[] L, int w, double[] mask, int x0, int y0, int x1, int y1)
        {
            double e = 0;
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
            {
                double dx = L[y * w + x + 1] - L[y * w + x], dy = L[(y + 1) * w + x] - L[y * w + x]; e += dx * dx + dy * dy;
            }
            return e;
        }

        /// <summary>Sharpness of the text inside the central area of a widget window (the frame, the toast and the footer are left out).</summary>
        internal static double Sharpness(GlassWindow win, out string detail)
        {
            var f = (FrameworkElement)win.Content; double W = win.ActualWidth, H = win.ActualHeight;
            var real = RenderAt(f, W, H, 96); var big = RenderAt(f, W, H, 384);
            int w, h, bw, bh; var A = Luma(real, out w, out h); var B = Luma(big, out bw, out bh);
            var R = new double[w * h];                                           // 4×4 box average
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { double s = 0; for (int j = 0; j < 4; j++) for (int i = 0; i < 4; i++) s += B[Math.Min(bh - 1, y * 4 + j) * bw + Math.Min(bw - 1, x * 4 + i)]; R[y * w + x] = s / 16; }
            int x0 = (int)(w * .10), x1 = (int)(w * .90), y0 = (int)(h * .10), y1 = (int)(h * .62);
            double er = Energy(A, w, null, x0, y0, x1 - 1, y1 - 1), eref = Energy(R, w, null, x0, y0, x1 - 1, y1 - 1);
            detail = string.Format("{0}x{1} real={2:F0} ref={3:F0}", (int)W, (int)H, er, eref);
            return eref < 1 ? 0 : er / eref;
        }


        // ---- the real thing: what is on the screen
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        internal static BitmapSource Capture(int x, int y, int w, int h)
        {
            IntPtr screen = GetDC(IntPtr.Zero), dc = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, w, h), old = SelectObject(dc, bmp);
            try
            {
                BitBlt(dc, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000);
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); src.Freeze(); return src;
            }
            finally { SelectObject(dc, old); DeleteObject(bmp); DeleteDC(dc); ReleaseDC(IntPtr.Zero, screen); }
        }

        /// <summary>
        /// Sharpness of a widget as the screen really shows it: the widget is shown over a flat grey window, the screen is captured, and the edge energy of the capture
        /// is compared with the 4× reference laid over the same grey. Includes the layered-window path and effects that an offscreen render does not exercise.
        /// </summary>
        /// <summary>
        /// How crisp bright text is on a flat backdrop: among the strong text pixels (≥ 60 % of the text brightness), the share that are fully on (≥ 90 %).
        /// A 1-pixel stem on the pixel grid scores 1.0; the same stem spread over two pixels (what a fractional scale does) scores 0.
        /// </summary>
        internal static double InkSolidShare(BitmapSource shot, Color back, Int32Rect area)
        {
            int w, h; var L = Luma(shot, out w, out h); double g = .114 * back.B + .587 * back.G + .299 * back.R, F = 0;
            for (int y = area.Y; y < area.Y + area.Height; y++) for (int x = area.X; x < area.X + area.Width; x++) F = Math.Max(F, L[y * w + x]);
            if (F - g < 40) return 0;
            int strong = 0, solid = 0;
            for (int y = area.Y; y < area.Y + area.Height; y++) for (int x = area.X; x < area.X + area.Width; x++)
            {
                double cov = (L[y * w + x] - g) / (F - g); if (cov >= .6) strong++; if (cov >= .9) solid++;
            }
            return strong < 30 ? 0 : solid / (double)strong;
        }
        internal static double LastInk, LastHalo;
        /// <summary>Area of backdrop pixels the text shadow has darkened (by 6+ luma levels) around the digits, per ink pixel.</summary>
        internal static double Halo(BitmapSource shot, Color back, Int32Rect area)
        {
            int w, h; var L = Luma(shot, out w, out h); double g = .114 * back.B + .587 * back.G + .299 * back.R, F = 0;
            for (int y = area.Y; y < area.Y + area.Height; y++) for (int x = area.X; x < area.X + area.Width; x++) F = Math.Max(F, L[y * w + x]);
            int ink = 0, dark = 0;
            for (int y = area.Y; y < area.Y + area.Height; y++) for (int x = area.X; x < area.X + area.Width; x++)
            { double l = L[y * w + x]; if (l >= g + .6 * (F - g)) ink++; else if (l < g - 6) dark++; }
            return ink < 30 ? 0 : dark / (double)ink;
        }
        internal static string SaveName;
        internal static bool DigitsArea = true;
        internal static async Task<double> ScreenSharpness(GlassWindow win, Color back)
        {
            win.Left = 60; win.Top = 60;   // fully on screen, whatever size it grew to
            var backdrop = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true, Background = new SolidColorBrush(back), Left = win.Left - 20, Top = win.Top - 20, Width = win.ActualWidth + 40, Height = win.ActualHeight + 40 };
            backdrop.Show(); win.Topmost = true; win.Show(); await Task.Delay(150);
            Native.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(win).Handle, new IntPtr(-1), 0, 0, 0, 0, 0x13);
            await Task.Delay(400);
            try
            {
                int W = (int)Math.Ceiling(win.ActualWidth), H = (int)Math.Ceiling(win.ActualHeight);
                var shot = Capture((int)Math.Round(win.Left), (int)Math.Round(win.Top), W, H);
                string dir = Environment.GetEnvironmentVariable("CD_SHOT_DIR"); if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && SaveName != null) { var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(shot)); using (var fs = File.Create(Path.Combine(dir, SaveName + ".png"))) enc.Save(fs); }
                var f = (FrameworkElement)win.Content; var big = RenderAt(f, win.ActualWidth, win.ActualHeight, 384);
                // reference: 4× picture averaged down, laid over the same grey
                int w, h, bw, bh; var A = Luma(shot, out w, out h); var B = Luma(big, out bw, out bh);
                double g = .114 * back.B + .587 * back.G + .299 * back.R;
                var bpx = new byte[bw * bh * 4]; new FormatConvertedBitmap(big, PixelFormats.Pbgra32, null, 0).CopyPixels(bpx, bw * 4, 0);
                var Rf = new double[w * h];
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                {
                    double s = 0; for (int j = 0; j < 4; j++) for (int i = 0; i < 4; i++) { int k = (Math.Min(bh - 1, y * 4 + j) * bw + Math.Min(bw - 1, x * 4 + i)) * 4; double a = bpx[k + 3] / 255.0; s += B[k / 4] + g * (1 - a); }
                    Rf[y * w + x] = s / 16;
                }
                int x0 = (int)(w * .10), x1 = (int)(w * .90), y0 = (int)(h * .10), y1 = (int)(h * .62);
                LastInk = InkSolidShare(shot, back, !DigitsArea ? new Int32Rect(x0, y0, x1 - x0, y1 - y0) : new Int32Rect((int)(w * .25), (int)(h * .45), (int)(w * .52), (int)(h * .15)));   // the time digits (the cursive weekday above them is a different font and would dominate)
                { int ax = (int)(w * .25) - 8, ay = (int)(h * .45) - 8, aw = (int)(w * .52) + 16, ah = (int)(h * .15) + 16; LastHalo = Halo(shot, back, new Int32Rect(Math.Max(0, ax), Math.Max(0, ay), Math.Min(w - Math.Max(0, ax), aw), Math.Min(h - Math.Max(0, ay), ah))); }
                double er = Energy(A, w, null, x0, y0, x1 - 1, y1 - 1), eref = Energy(Rf, w, null, x0, y0, x1 - 1, y1 - 1);
                return eref < 1 ? 0 : er / eref;
            }
            finally { backdrop.Close(); }
        }

        internal static readonly string[] Kinds = { "clock", "calendar", "todo", "countdown", "schedule" };
        internal static readonly double[] Scales = { 1.0, 1.15, 1.6 };

        /// <summary>Sharpness of every probe widget at every probe size, as "kind xScale = value" lines.</summary>
        internal static async Task<List<string>> Measure(Action<GlassWindow> prepare = null)
        {
            var lines = new List<string>();
            foreach (var kind in Kinds)
            {
                GlassWindow w = null;
                try
                {
                    w = Program.NewWidget(kind); if (w == null) continue;
                    await Task.Delay(300);
                    foreach (double s in Scales)
                    {
                        if (s == 1.0) w.ResetSize(false); else w.SetCustomSize(w.DefaultWidth * s, w.DefaultHeight * s, false);
                        if (prepare != null) prepare(w);
                        await Task.Delay(200);
                        string detail; double v = Sharpness(w, out detail);
                        lines.Add(string.Format("{0} x{1:0.00} = {2:F3}  ({3})", kind, s, v, detail));
                    }
                }
                catch (Exception e) { lines.Add(kind + " error " + e.Message); }
                finally { if (w != null) { try { w.ResetSize(false); w.Close(); } catch { } } }
            }
            return lines;
        }
    }
}
