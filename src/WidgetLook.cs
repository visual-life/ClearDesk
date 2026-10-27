using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace ClearDesk
{
    // 2.2.2: the look options every widget shares — 无边框 / 透明背景, a custom background picture, and the edge metrics that follow the widget's size.
    internal static class WidgetLook
    {
        internal const double MinOpacity = .05, MaxDim = .85, DefaultDim = .25;
        internal static readonly string[] Fits = { "cover", "contain", "stretch" };
        internal static string FitName(string fit) { return fit == "contain" ? "适应（完整显示）" : fit == "stretch" ? "拉伸（铺满）" : "填充（裁剪铺满）"; }
        internal static string NormalizeFit(string fit) { return Fits.Contains(fit) ? fit : "cover"; }
        internal static Stretch StretchFor(string fit) { fit = NormalizeFit(fit); return fit == "contain" ? Stretch.Uniform : fit == "stretch" ? Stretch.Fill : Stretch.UniformToFill; }
        internal static double ClampOpacity(double v) { return double.IsNaN(v) ? 1 : Math.Max(MinOpacity, Math.Min(1, v)); }
        internal static double ClampDim(double v) { return double.IsNaN(v) ? DefaultDim : Math.Max(0, Math.Min(MaxDim, v)); }

        // ---- size → metrics (pure) ----
        /// <summary>The factor every layout unit is multiplied by: the smaller of the width and height ratios, kept inside min..max.</summary>
        internal static double ContentScale(double width, double height, double baseWidth, double baseHeight, double min, double max)
        {
            if (baseWidth <= 0 || baseHeight <= 0 || double.IsNaN(width) || double.IsNaN(height)) return 1;
            double s = Math.Min(width / baseWidth, height / baseHeight);
            return double.IsNaN(s) || s <= 0 ? 1 : Math.Max(min, Math.Min(max, s));
        }
        /// <summary>
        /// The widget's outer padding grows with the room the content scale cannot use (a tall or wide widget): half of the spare room ratio, never more than
        /// doubling, never below 1. A widget at its default shape keeps the 20-unit padding it always had.
        /// </summary>
        internal static double EdgeFactor(double extraWidth, double extraHeight, double baseWidth, double baseHeight)
        {
            if (baseWidth <= 0 || baseHeight <= 0) return 1;
            double spare = Math.Max(Math.Max(0, extraWidth) / baseWidth, Math.Max(0, extraHeight) / baseHeight);
            return Math.Max(1, Math.Min(2, 1 + spare * .5));
        }
        internal const double PadBase = 20;
        internal static double Pad(double edgeFactor) { return Math.Round(PadBase * Math.Max(1, Math.Min(2, edgeFactor)) * 2) / 2; }
        /// <summary>Gap used between stacked blocks, from the vertical room a widget has beyond its default.</summary>
        internal static double Gap(double baseGap, double extraHeight, double baseHeight, double maxFactor = 3)
        {
            if (baseHeight <= 0) return baseGap;
            return baseGap * Math.Max(1, Math.Min(maxFactor, 1 + Math.Max(0, extraHeight) / baseHeight * 1.2));
        }

        // ---- background store ----
        internal static string Folder { get { return Path.Combine(ToolStore.Root, "backgrounds"); } }
        internal const long MaxBytes = 40L * 1024 * 1024;
        internal static bool IsStored(string path)
        {
            try { string f = Path.GetFullPath(Folder).TrimEnd('\\') + "\\"; return Path.GetFullPath(path ?? "").StartsWith(f, StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }
        static string SafeId(string id) { foreach (char c in Path.GetInvalidFileNameChars()) id = (id ?? "w").Replace(c, '_'); return id.Length > 36 ? id.Substring(0, 36) : id; }
        static string Hash(byte[] data) { using (var h = System.Security.Cryptography.SHA1.Create()) return BitConverter.ToString(h.ComputeHash(data)).Replace("-", "").ToLowerInvariant().Substring(0, 12); }
        /// <summary>Copies a picture file into the data folder for this widget. Returns the stored full path or null.</summary>
        internal static string CopyIn(string widgetId, string source)
        {
            try
            {
                if (!AlbumLogic.IsImage(source) || !File.Exists(source)) return null;
                var info = new FileInfo(source); if (info.Length == 0 || info.Length > MaxBytes) return null;
                var data = File.ReadAllBytes(source); if (!Decodes(data)) return null;
                return Write(widgetId, data, Path.GetExtension(source).ToLowerInvariant());
            }
            catch (Exception e) { Program.Log(e); return null; }
        }
        internal static string CopyIn(string widgetId, BitmapSource bitmap)
        {
            try
            {
                if (bitmap == null || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) return null;
                var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bitmap));
                using (var ms = new MemoryStream()) { enc.Save(ms); return ms.Length > MaxBytes ? null : Write(widgetId, ms.ToArray(), ".png"); }
            }
            catch (Exception e) { Program.Log(e); return null; }
        }
        static bool Decodes(byte[] data)
        {
            try { using (var ms = new MemoryStream(data)) { var d = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad); return d.Frames.Count > 0 && d.Frames[0].PixelWidth > 0; } } catch { return false; }
        }
        static string Write(string widgetId, byte[] data, string ext)
        {
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, SafeId(widgetId) + "-" + Hash(data) + ext), tmp = path + ".tmp";
            if (File.Exists(path)) return path;
            File.WriteAllBytes(tmp, data); File.Move(tmp, path);
            return path;
        }
        internal static void Remove(string path) { try { if (IsStored(path) && File.Exists(path)) File.Delete(path); } catch (Exception e) { Program.Log(e); } }
        /// <summary>Deletes every stored background of one widget except the one in use.</summary>
        internal static void Prune(string widgetId, string keep)
        {
            try
            {
                if (!Directory.Exists(Folder)) return;
                foreach (var f in Directory.GetFiles(Folder, SafeId(widgetId) + "-*"))
                    if (!string.Equals(f, keep, StringComparison.OrdinalIgnoreCase)) try { File.Delete(f); } catch { }
            }
            catch { }
        }
        internal static BitmapSource ClipboardImage()
        {
            try { return Clipboard.ContainsImage() ? Clipboard.GetImage() : null; } catch { return null; }
        }
        internal static string[] ClipboardFiles()
        {
            try { return Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().ToArray() : new string[0]; } catch { return new string[0]; }
        }
        /// <summary>The picture, decoded once (not locked on disk); very large photos are decoded at 2400 px wide.</summary>
        internal static BitmapSource Load(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                int w = 0;
                using (var s = File.OpenRead(path)) { var d = BitmapDecoder.Create(s, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None); w = d.Frames[0].PixelWidth; }
                var bi = new BitmapImage();
                bi.BeginInit(); bi.UriSource = new Uri(path); bi.CacheOption = BitmapCacheOption.OnLoad; bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (w > 2400) bi.DecodePixelWidth = 2400;
                bi.EndInit(); bi.Freeze();
                return bi;
            }
            catch (Exception e) { Program.Log(e); return null; }
        }
        internal static bool Droppable(IDataObject d)
        {
            try
            {
                var files = d.GetDataPresent(DataFormats.FileDrop) ? d.GetData(DataFormats.FileDrop) as string[] : null;
                if (files != null) return files.Any(f => AlbumLogic.IsImage(f));
                return d.GetDataPresent(DataFormats.Bitmap) || d.GetDataPresent("PNG");
            }
            catch { return false; }
        }
        internal static BitmapSource BitmapFrom(IDataObject d)
        {
            try
            {
                if (d.GetDataPresent(DataFormats.Bitmap)) { var b = d.GetData(DataFormats.Bitmap) as BitmapSource; if (b != null) return b; }
                if (d.GetDataPresent("PNG")) { var ms = d.GetData("PNG") as MemoryStream; if (ms != null) return new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]; }
            }
            catch { }
            return null;
        }

        // 2.2.3: a tight shadow (2.2.2 used a 7 px, 60 % blur that smeared thin light text into a dark halo). The values are fields so the tests can reproduce the old look.
        internal static double ShadowBlur = 2.5, ShadowOpacity = .55; internal static RenderingBias ShadowBias = RenderingBias.Quality;
        internal static Effect TextShadow() { return new DropShadowEffect { BlurRadius = ShadowBlur, ShadowDepth = 1, Direction = 270, Opacity = ShadowOpacity, Color = Colors.Black, RenderingBias = ShadowBias }; }
    }
}
