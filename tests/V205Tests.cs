using System;
using System.Linq;
using System.Threading.Tasks;

namespace ClearDesk
{
    // 2.0.5: proportional widget resizing + top taskbar auto-hide defaults.
    internal static class V205Tests
    {
        public static async Task Run(Action<bool, string> check)
        {
            check(!new Settings().TopTaskbarAutoHide && new BoxData().Scale == 1, "2.0.5: top taskbar auto-hide off by default; widgets start at 100%");
            var w = Program.OpenWindows.OfType<ClockWindow>().FirstOrDefault(x => !x.WasClosed) ?? (GlassWindow)Program.NewWidget("clock");
            await Task.Delay(600);
            check(w != null && w.Scalable, "clock widget supports proportional resizing");
            if (w == null) return;
            double bw = w.Width, aw = w.ActualWidth;
            w.SetScale(1.5, false); w.UpdateLayout(); await Task.Delay(250);
            check(Math.Abs(w.ActualWidth - aw * 1.5) < 3 && Math.Abs(w.Width - bw) < .01, "scale 150% enlarges the window while keeping the layout size (" + aw + " -> " + w.ActualWidth + ")");
            w.SetScale(9, false);
            check(Math.Abs(w.ContentScale - GlassWindow.MaxScale) < .001, "scale is clamped to the maximum");
            w.SetScale(.1, false);
            check(Math.Abs(w.ContentScale - GlassWindow.MinScale) < .001, "scale is clamped to the minimum");
            w.SetScale(1, false); w.UpdateLayout(); await Task.Delay(250);
            check(Math.Abs(w.ActualWidth - aw) < 3, "reset returns to the original size");
            check(w.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>().Any(m => Equals(m.Tag, "size")), "widget menu has 调整大小");
            check(!Program.OpenWindows.OfType<OrganizerWindow>().Any(o => o.Scalable) && !Program.OpenWindows.OfType<DockWindow>().Any(d => d.Scalable), "收纳格 / Dock keep their own sizing");
        }
    }
}