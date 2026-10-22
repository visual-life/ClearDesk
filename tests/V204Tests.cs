using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClearDesk
{
    // 2.0.4: 快捷控制 widget.
    internal static class V204Tests
    {
        public static void Rules(Action<bool, string> check)
        {
            check(QuickLogic.VolumeGlyph(.5, true) == "\uE74F" && QuickLogic.VolumeGlyph(0, false) == "\uE992" && QuickLogic.VolumeGlyph(.5, false) == "\uE767", "quick: volume glyph follows level and mute");
            check(QuickLogic.Percent(.456) == "46%" && QuickLogic.Percent(1.2) == "100%" && QuickLogic.Percent(-1) == "0%", "quick: volume percent is clamped and rounded");
            check(QuickLogic.WifiText(RadioView.On, true, "Home-5G", 4) == "Home-5G · 4/5" && QuickLogic.WifiText(RadioView.On, true, null, null) == "已连接" && QuickLogic.WifiText(RadioView.On, false, null, null) == "未连接" && QuickLogic.WifiText(RadioView.Off, true, "x", 3) == "已关闭", "quick: WLAN status text");
            check(QuickLogic.WifiText(RadioView.Denied, false, null, null).Contains("隐私设置") && QuickLogic.BluetoothText(RadioView.Missing, 0).Contains("未找到蓝牙"), "quick: denied / missing radios explain themselves");
            check(QuickLogic.BluetoothText(RadioView.On, 0) == "已开启 · 无设备连接" && QuickLogic.BluetoothText(RadioView.On, 2) == "已连接 2 个设备" && QuickLogic.BluetoothText(RadioView.Off, 2) == "已关闭", "quick: Bluetooth status text");
            check(QuickLogic.WifiGlyph(true, 1) == "\uE872" && QuickLogic.WifiGlyph(true, 5) == "\uE701", "quick: WLAN glyph follows signal bars");
            check(WidgetCatalog.IsKnown("quick") && WidgetManager.Singletons.Contains("quick") && WidgetManager.KindName("quick") == "快捷控制", "quick: listed in 新建组件 catalog and 组件管理");
            check(!new Settings().QuickControls, "quick: off by default");
        }

        public static async Task Run(Action<bool, string> check)
        {
            var w = (QuickControlWindow)Program.NewWidget("quick");
            await Task.Delay(2500);
            check(w != null && w.IsVisible && Program.S.QuickControls, "新建组件 → 快捷控制 opens the widget");
            check(ReferenceEquals(Program.NewWidget("quick"), w), "快捷控制 is single-instance");
            var v = w.TestVolume;
            if (v != null && v.Available)
                check((w.TestVolumeText == "静音" && v.Muted) || w.TestVolumeText == QuickLogic.Percent(v.Level), "volume shows the default device's real level (" + w.TestVolumeText + ")");
            else
                check(w.TestVolumeText == "—", "without an output device the volume reads —");
            check(!string.IsNullOrEmpty(w.TestDeviceText) && !w.TestDeviceText.StartsWith("正在读取"), "output device name is shown: " + w.TestDeviceText);
            check(w.TestWifiText != "读取中…" && w.TestBluetoothText != "读取中…", "radios resolve their state: " + w.TestWifiText + " / " + w.TestBluetoothText);
            check(w.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>().Any(m => Equals(m.Header, "材质设置")) && w.TestLockItem != null, "快捷控制 has the usual material / lock menu");
            var entry = WidgetManager.List().Single(e => e.Kind == "quick");
            check(entry.Visible && entry.Name == "快捷控制", "组件管理 lists 快捷控制 as 显示中");
            var root = (FrameworkElement)w.Content;
            root.UpdateLayout();
            // compose over a dark wallpaper-like gradient so the white glass text is legible in the preview
            int pw = (int)Math.Ceiling(root.ActualWidth) + 40, ph = (int)Math.Ceiling(root.ActualHeight) + 40;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(38, 58, 96), Color.FromRgb(86, 62, 110), 35), null, new Rect(0, 0, pw, ph));
                dc.DrawRectangle(new VisualBrush(root), null, new Rect(20, 20, root.ActualWidth, root.ActualHeight));
            }
            var bitmap = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.Combine(Program.Base, "preview"));
            using (var f = File.Create(Path.Combine(Program.Base, "preview", "quick-controls.png"))) png.Save(f);
            WidgetManager.SetVisible(entry, false);
            await Task.Delay(100);
            check(w.WasClosed && !Program.S.QuickControls, "关闭 hides 快捷控制 and releases its listeners");
        }
    }
}
