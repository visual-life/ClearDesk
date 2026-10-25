using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClearDesk
{
    internal static class SettingsWindowTests
    {
        static IEnumerable<T> Children<T>(DependencyObject parent) where T: DependencyObject
        {
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            {
                var child=VisualTreeHelper.GetChild(parent,i); if(child is T) yield return (T)child;
                foreach(var nested in Children<T>(child)) yield return nested;
            }
        }
        static void Capture(SettingsWindow window,string name)
        {
            var root=(FrameworkElement)window.Content;
            var bitmap=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(root);
            var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using(var file=File.Create(Path.Combine(Program.Base,name+".png"))) png.Save(file);
        }
        internal static async Task Run(Action<bool,string> check)
        {
            bool oldDock=Program.S.Dock; int oldSize=Program.S.DockSize; string oldSource=Program.S.MusicSource;
            var window=new SettingsWindow(delegate { throw new Exception("Startup must not change during settings preview test"); });
            try
            {
                window.Show(); await Task.Delay(180); window.UpdateLayout();
                check(window.TestCards==2 && Children<CheckBox>(window).Count()==10,"settings general page contains the eight original desktop and window switches plus GPU rendering and top taskbar auto-hide");
                Capture(window,"settings-general");
                bool allPages=true;
                for(int i=0;i<7;i++) { window.TestSelect(i); window.UpdateLayout(); allPages &= window.TestCards>0; Capture(window,"settings-page-"+i); }
                check(allPages,"all seven settings categories (incl. 组件管理) render without losing their cards");
                window.TestSearch("手动选择区县"); window.UpdateLayout();
                check(Children<ComboBox>(window).Count()==0 && Children<TextBox>(window).Any(t=>AutomationProperties.GetName(t)=="手动选择所在地") && Children<Button>(window).Any(b=>AutomationProperties.GetName(b)=="搜索所在地") && Children<Button>(window).Any(b=>AutomationProperties.GetName(b)=="恢复自动定位"),"weather settings offer a county-level place search with an automatic-location reset");
                window.TestSearch("Windows 定位"); window.UpdateLayout();
                check(Children<CheckBox>(window).Any(c=>AutomationProperties.GetName(c)=="使用 Windows 定位") && Children<Button>(window).Any(b=>AutomationProperties.GetName(b)=="重新定位并刷新天气"),"weather settings expose the Windows location switch and a re-locate button");
                window.TestSearch("音乐来源"); window.UpdateLayout();
                check(window.TestCards==1 && Children<ComboBox>(window).Count()==1,"search finds settings outside the selected category");
                var source=Children<ComboBox>(window).Single(); source.SelectedIndex=1;
                check(Program.S.MusicSource=="qq","new settings music source updates existing persisted configuration");
                source.IsDropDownOpen=true; await Task.Delay(60);
                check(source.ItemContainerGenerator.ContainerFromIndex(0)!=null,"light themed source selector generates accessible choices"); source.IsDropDownOpen=false;
                window.TestSelect(2); window.UpdateLayout();
                var toggle=Children<CheckBox>(window).Single(c=>AutomationProperties.GetName(c)=="显示 Dock 栏"); toggle.IsChecked=!oldDock; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                check(Program.S.Dock==!oldDock,"Dock switch retains the original configuration behavior");
                var slider=Children<Slider>(window).Single(s=>AutomationProperties.GetName(s)=="图标大小"); slider.Value=57;
                window.TestSelect(0); window.TestSelect(2); window.UpdateLayout();
                check(Program.S.DockSize==57 && Children<Slider>(window).Single(s=>AutomationProperties.GetName(s)=="图标大小").Value==57,"changing categories preserves the edited Dock slider value");
                window.Width=840; window.Height=570; window.TestSelect(4); window.UpdateLayout(); await Task.Delay(60);
                var main=Children<ScrollViewer>(window).First();
                check(main.ScrollableWidth==0 && Children<Button>(window).Where(b=>(b.Content as string)=="应用设置").Single().ActualWidth>70,"minimum settings size keeps apply action usable without horizontal scrolling");
                Capture(window,"settings-compact");
                window.TestSearch("no-such-setting-9284"); window.UpdateLayout();
                check(Children<TextBlock>(window).Any(t=>t.Text.StartsWith("没有找到相关设置")),"empty search shows a readable recovery message");
            }
            finally { window.Close(); Program.S.Dock=oldDock; Program.S.DockSize=oldSize; Program.S.MusicSource=oldSource; Program.Save(); }
        }
    }
}
