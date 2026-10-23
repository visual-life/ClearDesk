using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClearDesk
{
    // 2.0.6: Dock offset, WLAN flyout, resize guides, note images, live desktop blur, taskbar hot zones.
    internal static class V206Tests
    {
        public static async Task Run(Action<bool, string> check)
        {
            check(!new Settings().LiveDesktopBlur, "2.0.6: 动态壁纸实时模糊 is off by default");
            check(new BoxData().Images != null && new BoxData().Images.Count == 0, "notes start without images");

            // resize snapping
            var others = new List<Rect> { new Rect(500, 100, 200, 150) };
            var r = MagnetSnap.SnapResize(new Rect(100, 100, 395, 147), others, new List<Rect>(), MagnetSnap.Threshold);
            check(r.SnappedX && Math.Abs(r.X - 500) < .01 && r.SnappedY && Math.Abs(r.Y - 250) < .01 && r.Guides.Count >= 2, "resize: right / bottom edges snap to a neighbour's edges with guides");
            var same = MagnetSnap.SnapResize(new Rect(100, 400, 204, 80), others, new List<Rect>(), MagnetSnap.Threshold, true, false);
            check(same.SnappedX && Math.Abs(same.X - 300) < .01 && same.Guides.All(g => g.Gap), "resize: width snaps to an equal-size neighbour (dashed dimension guides)");
            var free = MagnetSnap.SnapResize(new Rect(100, 100, 300, 300), others, new List<Rect>(), MagnetSnap.Threshold);
            check(!free.SnappedX && !free.SnappedY && free.Guides.Count == 0, "resize: no snap / guides when nothing is near");

            // taskbar hot zones
            var spans = TopTaskbar.MergeSpans(new List<int[]> { new[] { 900, 940 }, new[] { 950, 990 }, new[] { 1700, 1900 } }, 20);
            check(spans.Count == 2 && spans[0][0] == 900 && spans[0][1] == 990 && spans[1][0] == 1700, "taskbar hot zones: icon spans merge, blank gaps stay cold");
            check(TopTaskbar.MergeSpans(new List<int[]>(), 20) == null && TopTaskbar.Hot(new IntPtr(12345), 10), "taskbar hot zones: unknown / failed detection falls back to the whole edge");

            // note images
            var note = Program.NewNote();
            await Task.Delay(600);
            var bmp = new WriteableBitmap(64, 40, 96, 96, PixelFormats.Bgr32, null);
            note.AddImageBitmap(bmp);
            await Task.Delay(400);
            var data = note.Data;
            string file = data.Images.Count == 1 ? Path.Combine(NoteWindow.ImageFolder, data.Images[0].File) : null;
            check(data.Images.Count == 1 && File.Exists(file) && note.TestImageCount == 1, "便签: inserted image is copied into the app data folder and shown");
            check(note.TestScroller.VerticalScrollBarVisibility == System.Windows.Controls.ScrollBarVisibility.Hidden, "便签: no visible scrollbar (wheel scrolls)");
            check(note.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>().Any(m => Equals(m.Header, "插入图片…")), "便签 menu has 插入图片…");
            note.RemoveImage(data.Images[0]);
            await Task.Delay(200);
            check(data.Images.Count == 0 && !File.Exists(file) && note.TestImageCount == 0, "便签: deleting an image removes its file");
            note.Delete(false);
        }
    }
}
