using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ClearDesk
{
    /// <summary>2.2.0: JSON files for the new widgets' own content (to-dos, events, launcher items …), kept next to settings.json in %LOCALAPPDATA%\ClearDesk\widgets.</summary>
    internal static class ToolStore
    {
        internal static string TestRoot;
        internal static string Root { get { return TestRoot ?? Path.Combine(Program.Data, "widgets"); } }
        internal static T Load<T>(string name) where T : class, new()
        {
            string path = Path.Combine(Root, name + ".json");
            try
            {
                if (File.Exists(path))
                {
                    var o = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8));
                    if (o != null) return o;
                }
            }
            catch (Exception e)
            {
                try { File.Copy(path, path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss")); } catch { }
                Program.Log(e);
            }
            return new T();
        }
        internal static bool Save<T>(string name, T data)
        {
            try
            {
                Directory.CreateDirectory(Root);
                string path = Path.Combine(Root, name + ".json"), temp = path + ".tmp";
                File.WriteAllText(temp, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(data), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
                return true;
            }
            catch (Exception e) { Program.Log(e); return false; }
        }
    }

    /// <summary>The 2.2.0 widget kinds. They are single-instance like clock / weather: on/off is kept in Settings.ToolWidgets, position and size in Settings.Widgets[kind].</summary>
    internal static class ToolWidgets
    {
        internal static readonly KeyValuePair<string, string>[] Kinds =
        {
            new KeyValuePair<string, string>("todo", "待办清单"),
            new KeyValuePair<string, string>("countdown", "倒计时 / 番茄钟"),
            new KeyValuePair<string, string>("schedule", "日程"),
            new KeyValuePair<string, string>("clipboard", "剪贴板历史"),
            new KeyValuePair<string, string>("netspeed", "网速流量"),
            new KeyValuePair<string, string>("launcher", "快捷启动"),
            new KeyValuePair<string, string>("album", "图片相册"),
            new KeyValuePair<string, string>("sunair", "日出日落 / 空气质量"),
        };
        internal static readonly Dictionary<string, string> Descriptions = new Dictionary<string, string>
        {
            { "todo", "添加、勾选、删除、拖动排序待办事项，可设到期时间并弹出提醒。" },
            { "countdown", "考试、生日等倒数日，以及番茄钟（开始 / 暂停、完成提醒）。" },
            { "schedule", "今天和明天的日程；本地添加编辑，可导入 .ics 日历文件。" },
            { "clipboard", "最近复制的文本和图片，点击重新复制，可固定、清空、暂停记录。默认只保存在内存里。" },
            { "netspeed", "实时上传 / 下载速度、今日流量和迷你曲线。" },
            { "launcher", "文件、文件夹、网址和应用的图标网格，拖入添加，点击打开。" },
            { "album", "选择文件夹或图片轮播，淡入淡出，可设间隔和铺放方式。" },
            { "sunair", "所在地的日出日落时间和空气质量（AQI），位置来自天气设置。" },
        };

        internal static bool IsTool(string kind) { return Kinds.Any(k => k.Key == kind); }
        internal static bool On(string kind) { var l = Program.S.ToolWidgets; return l != null && l.Contains(kind); }
        internal static void Set(string kind, bool on)
        {
            if (Program.S.ToolWidgets == null) Program.S.ToolWidgets = new List<string>();
            var l = Program.S.ToolWidgets;
            if (on && !l.Contains(kind)) l.Add(kind);
            if (!on) l.RemoveAll(k => k == kind);
        }
        internal static GlassWindow Create(string kind)
        {
            switch (kind)
            {
                case "todo": return new TodoWindow();
                case "countdown": return new CountdownWindow();
                case "schedule": return new ScheduleWindow();
                case "clipboard": return new ClipboardWindow();
                case "netspeed": return new NetSpeedWindow();
                case "launcher": return new LauncherWindow();
                case "album": return new AlbumWindow();
                case "sunair": return new SunAirWindow();
            }
            return null;
        }
    }

    /// <summary>Common look and helpers for the new widgets (dark glass, white text, flat controls that do not start a drag when clicked).</summary>
    public abstract class ToolWidget : GlassWindow
    {
        protected ToolWidget(string kind, string title, double x, double y, double w, double h) : base(Program.Widget(kind, x, y, w, h), title)
        {
            Body.Margin = new Thickness(0);
            Closed += delegate { OnWidgetClosed(); };
        }
        protected virtual void OnWidgetClosed() { }
        internal string Kind { get { return State.Id; } }

        protected override void Disable()
        {
            ToolWidgets.Set(State.Id, false);
            base.Disable();
        }

        // ---- controls ----
        protected static readonly Brush Soft = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255));
        protected static readonly Brush Hover = new SolidColorBrush(Color.FromArgb(64, 255, 255, 255));
        protected static readonly Brush Dim = new SolidColorBrush(Color.FromArgb(170, 235, 240, 250));
        protected static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x9B, 0xFF));

        static ControlTemplate FlatTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border), "b");
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);
            var t = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; over.Setters.Add(new Setter(Border.BackgroundProperty, Hover, "b")); t.Triggers.Add(over);
            return t;
        }
        static readonly ControlTemplate Flat = FlatTemplate();

        protected static Button IconButton(string glyph, string tip, Action click, double size = 13)
        {
            var b = new Button
            {
                Template = Flat, Cursor = Cursors.Hand, ToolTip = tip, Width = 26, Height = 26, Padding = new Thickness(0), Foreground = Brushes.White, Focusable = false,
                Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = size, Foreground = Brushes.White }
            };
            System.Windows.Automation.AutomationProperties.SetName(b, tip);
            b.Click += delegate { click(); };
            return b;
        }
        protected static Button TextButton(string text, Action click, bool primary = false)
        {
            var b = new Button
            {
                Template = Flat, Cursor = Cursors.Hand, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0), Focusable = false, Foreground = Brushes.White,
                Content = new TextBlock { Text = text, FontSize = 12, Foreground = Brushes.White }
            };
            if (primary) b.Template = PrimaryTemplate();
            b.Click += delegate { click(); };
            return b;
        }
        static ControlTemplate PrimaryTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border), "b");
            border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(200, 0x2F, 0x7B, 0xE8)));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter)); cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            border.AppendChild(cp);
            var t = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; over.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x4C, 0x9B, 0xFF)), "b")); t.Triggers.Add(over);
            return t;
        }
        internal static TextBox Input(string hint, double fontSize = 13)
        {
            var t = new TextBox
            {
                Background = Soft, Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(8, 5, 8, 5), FontSize = fontSize,
                VerticalContentAlignment = VerticalAlignment.Center, ToolTip = hint, SelectionBrush = Accent
            };
            System.Windows.Automation.AutomationProperties.SetName(t, hint);
            t.Tag = hint; t.Template = InputTemplate();
            return t;
        }
        static ControlTemplate inputTemplate;
        /// <summary>Rounded box with a grey hint that disappears as soon as something is typed.</summary>
        static ControlTemplate InputTemplate()
        {
            if (inputTemplate != null) return inputTemplate;
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            var grid = new FrameworkElementFactory(typeof(Grid));
            var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
            host.SetBinding(Control.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden); host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            var hintBlock = new FrameworkElementFactory(typeof(TextBlock), "hint");
            hintBlock.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Tag") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            hintBlock.SetBinding(TextBlock.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            hintBlock.SetBinding(TextBlock.FontSizeProperty, new System.Windows.Data.Binding("FontSize") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            hintBlock.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromArgb(120, 235, 240, 250)));
            hintBlock.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            hintBlock.SetValue(UIElement.IsHitTestVisibleProperty, false); hintBlock.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); hintBlock.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            grid.AppendChild(host); grid.AppendChild(hintBlock); border.AppendChild(grid);
            var t = new ControlTemplate(typeof(TextBox)) { VisualTree = border };
            var empty = new Trigger { Property = TextBox.TextProperty, Value = "" };
            empty.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "hint"));
            t.Triggers.Add(empty); t.Seal();
            return inputTemplate = t;
        }
        protected static TextBlock Label(string text, double size = 12, Brush brush = null)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = brush ?? Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
        }
        protected static ScrollViewer Scroller(UIElement content)
        {
            return new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        }
        /// <summary>Header row buttons (right-aligned in the title bar).</summary>
        protected void AddHeaderButton(UIElement button)
        {
            DockPanel.SetDock(button, Dock.Right);
            Header.Children.Insert(0, button);
        }
        /// <summary>Adds an item at the top of the right-click menu (the white Windows 11 style comes from the shared menu theme).</summary>
        protected void AddMenuItem(string text, Action action)
        {
            ContextMenu.Items.Insert(0, Item(text, action));
        }
    }
}
