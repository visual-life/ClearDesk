using System.Windows;
using System.Windows.Markup;

namespace ClearDesk
{
    internal static class MenuTheme
    {
        // Submenus are separate Popup windows: their surface must be styled explicitly.
        public static void Apply(ResourceDictionary resources)
        {
            var theme = (ResourceDictionary)XamlReader.Parse(@"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
 <Style TargetType='ContextMenu'>
  <Setter Property='Background' Value='#F91E2128'/>
  <Setter Property='Foreground' Value='#F2F4F8'/>
  <Setter Property='BorderBrush' Value='#32FFFFFF'/>
  <Setter Property='BorderThickness' Value='1'/>
  <Setter Property='Padding' Value='6'/>
  <Setter Property='MinWidth' Value='208'/>
  <Setter Property='FontFamily' Value='Microsoft YaHei UI'/>
  <Setter Property='FontSize' Value='12'/>
  <Setter Property='HasDropShadow' Value='False'/>
  <Setter Property='OverridesDefaultStyle' Value='True'/>
  <Setter Property='SnapsToDevicePixels' Value='True'/>
  <Setter Property='Template'><Setter.Value>
   <ControlTemplate TargetType='ContextMenu'>
    <Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='13' Padding='{TemplateBinding Padding}'>
     <ScrollViewer CanContentScroll='True' VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Disabled'>
      <ItemsPresenter KeyboardNavigation.DirectionalNavigation='Cycle' Grid.IsSharedSizeScope='True'/>
     </ScrollViewer>
    </Border>
   </ControlTemplate>
  </Setter.Value></Setter>
 </Style>
 <Style TargetType='MenuItem'>
  <Setter Property='Foreground' Value='#F2F4F8'/>
  <Setter Property='Background' Value='Transparent'/>
  <Setter Property='Padding' Value='10,8'/>
  <Setter Property='MinHeight' Value='36'/>
  <Setter Property='FontSize' Value='12'/>
  <Setter Property='OverridesDefaultStyle' Value='True'/>
  <Setter Property='Template'><Setter.Value>
   <ControlTemplate TargetType='MenuItem'>
    <Grid>
     <Border x:Name='Row' Background='{TemplateBinding Background}' CornerRadius='8' Padding='{TemplateBinding Padding}'>
      <Grid>
       <Grid.ColumnDefinitions><ColumnDefinition Width='22'/><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='17'/></Grid.ColumnDefinitions>
       <ContentPresenter x:Name='Icon' ContentSource='Icon' Width='14' Height='14' HorizontalAlignment='Left' VerticalAlignment='Center'/>
       <Path x:Name='Check' Visibility='Collapsed' Data='M 1,5 L 4,8 L 10,1' Stroke='#A7C8FF' StrokeThickness='1.7' StrokeStartLineCap='Round' StrokeEndLineCap='Round' Width='12' Height='10' HorizontalAlignment='Left' VerticalAlignment='Center'/>
       <ContentPresenter Grid.Column='1' ContentSource='Header' RecognizesAccessKey='True' VerticalAlignment='Center'/>
       <TextBlock Grid.Column='2' Text='{TemplateBinding InputGestureText}' Foreground='#AEB6C5' Margin='16,0,2,0' VerticalAlignment='Center'/>
       <Path x:Name='Arrow' Grid.Column='3' Visibility='Collapsed' Data='M 1,1 L 5,5 L 1,9' Stroke='#BCC5D4' StrokeThickness='1.4' StrokeStartLineCap='Round' StrokeEndLineCap='Round' HorizontalAlignment='Right' VerticalAlignment='Center'/>
      </Grid>
     </Border>
     <Popup x:Name='PART_Popup' Placement='Right' HorizontalOffset='3' AllowsTransparency='True' Focusable='False' IsOpen='{Binding IsSubmenuOpen, RelativeSource={RelativeSource TemplatedParent}}' PopupAnimation='Fade'>
      <Border x:Name='SubmenuSurface' Background='#F91E2128' BorderBrush='#32FFFFFF' BorderThickness='1' CornerRadius='13' Padding='6' MinWidth='208'>
       <ScrollViewer CanContentScroll='True' VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Disabled'>
        <ItemsPresenter x:Name='ItemsPresenter' KeyboardNavigation.DirectionalNavigation='Cycle' Grid.IsSharedSizeScope='True'/>
       </ScrollViewer>
      </Border>
     </Popup>
    </Grid>
    <ControlTemplate.Triggers>
     <Trigger Property='HasItems' Value='True'><Setter TargetName='Arrow' Property='Visibility' Value='Visible'/></Trigger>
     <Trigger Property='IsChecked' Value='True'><Setter TargetName='Check' Property='Visibility' Value='Visible'/><Setter TargetName='Icon' Property='Visibility' Value='Collapsed'/></Trigger>
     <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Row' Property='Background' Value='#24FFFFFF'/></Trigger>
     <Trigger Property='IsSubmenuOpen' Value='True'><Setter TargetName='Row' Property='Background' Value='#24FFFFFF'/></Trigger>
     <Trigger Property='IsEnabled' Value='False'><Setter Property='Foreground' Value='#9198A6'/><Setter TargetName='Row' Property='Opacity' Value='.7'/></Trigger>
    </ControlTemplate.Triggers>
   </ControlTemplate>
  </Setter.Value></Setter>
 </Style>
 <Style TargetType='Separator'>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Separator'><Border Height='1' Margin='12,5' Background='#25FFFFFF'/></ControlTemplate></Setter.Value></Setter>
 </Style>
</ResourceDictionary>");
            foreach (object key in theme.Keys)
                resources[key] = theme[key];
        }
    }
}
