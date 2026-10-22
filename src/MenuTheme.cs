using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace ClearDesk
{
    // Windows 11 desktop-menu look: light surface, 8 px corners, soft shadow, dark text,
    // Fluent line icons, light-grey hover, thin separators, chevrons and checkmarks.
    internal static class MenuTheme
    {
        public const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
        public static readonly Color Text = Color.FromRgb(0x1A, 0x1A, 0x1A);
        public static readonly Color Secondary = Color.FromRgb(0x5F, 0x63, 0x6B);
        public static readonly Color LockedAccent = Color.FromRgb(0xA8, 0x5A, 0x00); // amber, 5.0:1 on #F9F9F9
        static bool hooked;

        // Submenus are separate Popup windows: their surface must be styled explicitly.
        public static void Apply(ResourceDictionary resources)
        {
            var theme = (ResourceDictionary)XamlReader.Parse(@"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
 <DropShadowEffect x:Key='MenuShadow' BlurRadius='16' ShadowDepth='4' Direction='270' Opacity='0.16' Color='#000000'/>
 <Style TargetType='ContextMenu'>
  <Setter Property='Background' Value='#F9F9F9'/>
  <Setter Property='Foreground' Value='#1A1A1A'/>
  <Setter Property='BorderBrush' Value='#1F000000'/>
  <Setter Property='BorderThickness' Value='1'/>
  <Setter Property='Padding' Value='4'/>
  <Setter Property='MinWidth' Value='220'/>
  <Setter Property='FontFamily' Value='Microsoft YaHei UI'/>
  <Setter Property='FontSize' Value='12'/>
  <Setter Property='HasDropShadow' Value='False'/>
  <Setter Property='OverridesDefaultStyle' Value='True'/>
  <Setter Property='SnapsToDevicePixels' Value='True'/>
  <Setter Property='Template'><Setter.Value>
   <ControlTemplate TargetType='ContextMenu'>
    <Border Margin='8,4,8,12' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='8' Padding='{TemplateBinding Padding}' Effect='{StaticResource MenuShadow}'>
     <ScrollViewer CanContentScroll='True' VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Disabled'>
      <ItemsPresenter KeyboardNavigation.DirectionalNavigation='Cycle' Grid.IsSharedSizeScope='True'/>
     </ScrollViewer>
    </Border>
   </ControlTemplate>
  </Setter.Value></Setter>
 </Style>
 <Style TargetType='MenuItem'>
  <Setter Property='Foreground' Value='#1A1A1A'/>
  <Setter Property='Background' Value='Transparent'/>
  <Setter Property='Padding' Value='10,7'/>
  <Setter Property='MinHeight' Value='32'/>
  <Setter Property='FontSize' Value='12'/>
  <Setter Property='OverridesDefaultStyle' Value='True'/>
  <Setter Property='Template'><Setter.Value>
   <ControlTemplate TargetType='MenuItem'>
    <Grid>
     <Border x:Name='Row' Background='{TemplateBinding Background}' CornerRadius='4' Padding='{TemplateBinding Padding}' Margin='0,1'>
      <Grid>
       <Grid.ColumnDefinitions><ColumnDefinition Width='28'/><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='18'/></Grid.ColumnDefinitions>
       <ContentPresenter x:Name='Icon' ContentSource='Icon' Width='16' Height='16' HorizontalAlignment='Left' VerticalAlignment='Center'/>
       <TextBlock x:Name='Check' Visibility='Collapsed' Text='&#xE73E;' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='13' Foreground='#1A1A1A' HorizontalAlignment='Left' VerticalAlignment='Center'/>
       <ContentPresenter Grid.Column='1' ContentSource='Header' RecognizesAccessKey='True' VerticalAlignment='Center'/>
       <TextBlock Grid.Column='2' Text='{TemplateBinding InputGestureText}' Foreground='#6B6B6B' Margin='16,0,2,0' VerticalAlignment='Center'/>
       <TextBlock x:Name='Arrow' Grid.Column='3' Visibility='Collapsed' Text='&#xE76C;' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='10' Foreground='#5F636B' HorizontalAlignment='Right' VerticalAlignment='Center'/>
      </Grid>
     </Border>
     <Popup x:Name='PART_Popup' Placement='Right' HorizontalOffset='-6' VerticalOffset='-9' AllowsTransparency='True' Focusable='False' IsOpen='{Binding IsSubmenuOpen, RelativeSource={RelativeSource TemplatedParent}}' PopupAnimation='Fade'>
      <Border x:Name='SubmenuSurface' Margin='8,4,8,12' Background='#F9F9F9' BorderBrush='#1F000000' BorderThickness='1' CornerRadius='8' Padding='4' MinWidth='200' Effect='{StaticResource MenuShadow}'>
       <ScrollViewer CanContentScroll='True' VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Disabled'>
        <ItemsPresenter x:Name='ItemsPresenter' KeyboardNavigation.DirectionalNavigation='Cycle' Grid.IsSharedSizeScope='True'/>
       </ScrollViewer>
      </Border>
     </Popup>
    </Grid>
    <ControlTemplate.Triggers>
     <Trigger Property='HasItems' Value='True'><Setter TargetName='Arrow' Property='Visibility' Value='Visible'/></Trigger>
     <Trigger Property='IsChecked' Value='True'><Setter TargetName='Check' Property='Visibility' Value='Visible'/><Setter TargetName='Icon' Property='Visibility' Value='Collapsed'/></Trigger>
     <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Row' Property='Background' Value='#0F000000'/></Trigger>
     <Trigger Property='IsSubmenuOpen' Value='True'><Setter TargetName='Row' Property='Background' Value='#0F000000'/></Trigger>
     <Trigger Property='IsPressed' Value='True'><Setter TargetName='Row' Property='Background' Value='#17000000'/></Trigger>
     <Trigger Property='IsEnabled' Value='False'><Setter Property='Foreground' Value='#A0A0A0'/><Setter TargetName='Icon' Property='Opacity' Value='.45'/></Trigger>
    </ControlTemplate.Triggers>
   </ControlTemplate>
  </Setter.Value></Setter>
 </Style>
 <Style TargetType='Separator'>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Separator'><Border Height='1' Margin='-4,4' Background='#14000000'/></ControlTemplate></Setter.Value></Setter>
 </Style>
</ResourceDictionary>");
            foreach (object key in theme.Keys)
                resources[key] = theme[key];
            if (!hooked)
            {
                hooked = true;
                EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(delegate (object s, RoutedEventArgs e) { Decorate((ItemsControl)s); }));
                EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent, new RoutedEventHandler(delegate (object s, RoutedEventArgs e) { Decorate((ItemsControl)s); }));
            }
        }

        static readonly Dictionary<string, string> glyphs = new Dictionary<string, string>
        {
            { "材质设置", "\uE790" }, { "便签颜色", "\uE790" }, { "收缩 / 展开", "\uE73F" }, { "新建组件", "\uE710" }, { "打开设置", "\uE713" },
            { "恢复位置", "\uE72C" }, { "关闭组件", "\uE711" }, { "删除便签", "\uE74D" }, { "删除收纳格", "\uE74D" },
            { "便签", "\uE70B" }, { "桌面收纳格", "\uE8B7" }, { "时钟", "\uE823" }, { "天气", "\uE753" }, { "音乐", "\uE8D6" },
            { "日历", "\uE787" }, { "黄历 / 农历", "\uE787" }, { "性能", "\uE9D9" }, { "快捷控制", "\uE9E9" },
            { "取消固定", "\uE77A" }, { "固定到 Dock", "\uE718" }, { "打开所在位置", "\uE838" }, { "关闭应用", "\uE711" }, { "已打开的窗口", "\uE737" },
            { "发送至桌面收纳", "\uE724" }, { "新建收纳格并添加", "\uE710" }, { "向左移动", "\uE72B" }, { "向右移动", "\uE72A" },
            { "网格 / 列表模式", "\uE80A" }, { "新建收纳格", "\uE710" }, { "刷新桌面项目", "\uE72C" }, { "恢复隐藏的引用", "\uE7B3" },
            { "重命名收纳格", "\uE8AC" }, { "新建", "\uE710" }, { "打开新建文件所在文件夹", "\uE838" },
        };

        public static string GlyphFor(string header)
        {
            string g;
            return header != null && glyphs.TryGetValue(header.Trim(), out g) ? g : null;
        }

        public static TextBlock Glyph(string glyph, Color? color = null)
        {
            return new TextBlock { Text = glyph, FontFamily = new FontFamily(IconFont), FontSize = 14, Foreground = new SolidColorBrush(color ?? Text), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        }

        // Adds Fluent line icons to known commands that have none yet (idempotent).
        public static void Decorate(ItemsControl menu)
        {
            if (menu == null) return;
            foreach (var item in menu.Items.OfType<MenuItem>())
            {
                if (item.Icon == null)
                {
                    var g = GlyphFor(item.Header as string);
                    if (g != null) item.Icon = Glyph(g);
                }
            }
        }
    }

    // Same look for the WinForms tray menu.
    internal sealed class LightMenuRenderer : System.Windows.Forms.ToolStripProfessionalRenderer
    {
        static readonly System.Drawing.Color Back = System.Drawing.Color.FromArgb(249, 249, 249), Hover = System.Drawing.Color.FromArgb(234, 234, 234), Line = System.Drawing.Color.FromArgb(225, 225, 225), Fore = System.Drawing.Color.FromArgb(26, 26, 26);
        public LightMenuRenderer() : base(new Table()) { RoundedEdges = true; }
        sealed class Table : System.Windows.Forms.ProfessionalColorTable
        {
            public override System.Drawing.Color ToolStripDropDownBackground { get { return Back; } }
            public override System.Drawing.Color ImageMarginGradientBegin { get { return Back; } }
            public override System.Drawing.Color ImageMarginGradientMiddle { get { return Back; } }
            public override System.Drawing.Color ImageMarginGradientEnd { get { return Back; } }
            public override System.Drawing.Color MenuBorder { get { return System.Drawing.Color.FromArgb(214, 214, 214); } }
            public override System.Drawing.Color MenuItemBorder { get { return Hover; } }
            public override System.Drawing.Color MenuItemSelected { get { return Hover; } }
            public override System.Drawing.Color SeparatorDark { get { return Line; } }
            public override System.Drawing.Color SeparatorLight { get { return Back; } }
        }
        protected override void OnRenderMenuItemBackground(System.Windows.Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var r = new System.Drawing.Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            using (var path = Round(r, 4))
            using (var b = new System.Drawing.SolidBrush(Hover))
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.FillPath(b, path);
            }
        }
        protected override void OnRenderItemText(System.Windows.Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Fore : System.Drawing.Color.FromArgb(160, 160, 160);
            base.OnRenderItemText(e);
        }
        protected override void OnRenderArrow(System.Windows.Forms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = System.Drawing.Color.FromArgb(95, 99, 107);
            base.OnRenderArrow(e);
        }
        static System.Drawing.Drawing2D.GraphicsPath Round(System.Drawing.Rectangle r, int radius)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath(); int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
            return p;
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        public static void Style(System.Windows.Forms.ToolStripDropDown menu)
        {
            menu.Renderer = new LightMenuRenderer();
            menu.BackColor = Back;
            menu.ForeColor = Fore;
            menu.Font = new System.Drawing.Font("Microsoft YaHei UI", 9f);
            menu.Padding = new System.Windows.Forms.Padding(2, 4, 2, 4);
            menu.Opened += delegate
            {
                int round = 2; // DWMWCP_ROUND
                try { DwmSetWindowAttribute(menu.Handle, 33, ref round, sizeof(int)); } catch { }
            };
            foreach (System.Windows.Forms.ToolStripItem item in menu.Items)
            {
                item.Padding = new System.Windows.Forms.Padding(4, 6, 4, 6);
                var sub = item as System.Windows.Forms.ToolStripMenuItem;
                if (sub != null && sub.HasDropDownItems) Style(sub.DropDown);
            }
        }
    }
}
