using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClearDesk
{
    // 便签: editable, autosaved, resizable, multi-instance (Settings.Notes).
    public class NoteWindow : GlassWindow
    {
        internal static readonly string[][] Palette =
        {
            new[] { "amber", "暖黄", "#FFD666" },
            new[] { "mint", "薄荷", "#7FE0B5" },
            new[] { "sky", "天蓝", "#7CC4FF" },
            new[] { "rose", "樱粉", "#FF9EC4" },
            new[] { "lilac", "丁香", "#C3A6FF" },
            new[] { "none", "无色", null },
        };

        readonly TextBox box;
        readonly Border tint = new Border { IsHitTestVisible = false, CornerRadius = new CornerRadius(22) };
        readonly Button remove;
        readonly DispatcherTimer save;
        readonly Thumb grip;
        bool deleted;

        internal BoxData Data { get { return State; } }
        internal TextBox TestBox { get { return box; } }

        public NoteWindow(BoxData state) : base(state, "便签")
        {
            Root.Children.Insert(Surface != null ? 1 : 0, tint);
            ApplyColor();
            Header.Margin = new Thickness(0, 0, 0, 8);
            remove = new Button
            {
                Content = "\uE711", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Width = 22, Height = 22,
                Foreground = Brushes.White, ToolTip = "删除便签", Opacity = 0, Cursor = Cursors.Hand, Focusable = false,
                Template = (ControlTemplate)XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Border x:Name='b' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='#22FFFFFF' CornerRadius='11'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='#CCE5484D'/></Trigger></ControlTemplate.Triggers></ControlTemplate>")
            };
            remove.Click += delegate { Delete(true); };
            DockPanel.SetDock(remove, Dock.Right);
            Header.Children.Insert(0, remove);
            MouseEnter += delegate { remove.Opacity = 1; };
            MouseLeave += delegate { remove.Opacity = 0; };

            box = new TextBox
            {
                Text = state.Text ?? "", AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White, CaretBrush = Brushes.White,
                SelectionBrush = new SolidColorBrush(Color.FromRgb(120, 170, 255)), FontSize = 14, Padding = new Thickness(0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Microsoft YaHei UI")
            };
            box.Resources[SystemParameters.VerticalScrollBarWidthKey] = 6.0;
            box.Template = (ControlTemplate)XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='TextBox'><ScrollViewer x:Name='PART_ContentHost' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='Transparent'/></ControlTemplate>");
            Body.Children.Add(box);
            var hint = Program.Text("写点什么…", 14, "#99FFFFFF");
            hint.IsHitTestVisible = false;
            Body.Children.Add(hint);
            Action hintSync = delegate { hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
            hintSync();

            save = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            save.Tick += delegate { save.Stop(); Flush(); };
            box.TextChanged += delegate { State.Text = box.Text; hintSync(); save.Stop(); save.Start(); };

            grip = new Thumb
            {
                Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.SizeNWSE, ToolTip = "拖动调整大小",
                Template = (ControlTemplate)XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Thumb'><Grid Background='Transparent'><Path Data='M 14,4 L 4,14 M 14,9 L 9,14' Stroke='#88FFFFFF' StrokeThickness='1.3' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/></Grid></ControlTemplate>")
            };
            Panel.SetZIndex(grip, 50);
            grip.DragDelta += delegate (object s, DragDeltaEventArgs e)
            {
                if (State.Collapsed) return;
                Width = Math.Max(200, Width + e.HorizontalChange);
                Height = Math.Max(140, Height + e.VerticalChange);
                State.Width = Width; State.Height = Height;
            };
            grip.DragCompleted += delegate { Program.Save(); };
            Root.Children.Add(grip);
            SizeChanged += delegate { Fit(); };
            Loaded += delegate { Fit(); };
            Closed += delegate { if (!deleted) Flush(); };
        }

        void Fit()
        {
            box.Height = Math.Max(40, ActualHeight - 20 - 20 - 30 - 8);
            grip.Visibility = State.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        }

        protected override void SetCollapsed()
        {
            base.SetCollapsed();
            if (grip != null) Fit();
        }

        void Flush()
        {
            if (deleted || !Program.S.Notes.Contains(State)) return;
            State.Text = box.Text;
            try { Program.Save(); } catch (Exception e) { Program.Log(e); }
        }

        internal static Color? ColorOf(string id)
        {
            var p = Palette.FirstOrDefault(x => x[0] == id) ?? Palette[0];
            return p[2] == null ? (Color?)null : (Color)ColorConverter.ConvertFromString(p[2]);
        }

        void ApplyColor()
        {
            var c = ColorOf(State.NoteColor);
            tint.Background = c.HasValue ? new SolidColorBrush(Color.FromArgb(56, c.Value.R, c.Value.G, c.Value.B)) : null;
            Frame.BorderBrush = c.HasValue ? new SolidColorBrush(Color.FromArgb(110, c.Value.R, c.Value.G, c.Value.B)) : new SolidColorBrush(Color.FromArgb(55, 235, 242, 255));
        }

        internal void SetColor(string id)
        {
            State.NoteColor = id;
            ApplyColor();
            Program.Save();
        }

        protected override ContextMenu Menu()
        {
            var c = base.Menu();
            var colors = new MenuItem { Header = "便签颜色" };
            foreach (var p in Palette)
            {
                var id = p[0];
                var swatch = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), Background = p[2] == null ? Brushes.Transparent : (Brush)new BrushConverter().ConvertFromString(p[2]) };
                var item = new MenuItem { Header = p[1], Icon = swatch };
                item.Click += delegate { SetColor(id); };
                colors.Items.Add(item);
            }
            c.Items.Insert(1, colors);
            // replace “关闭组件” with delete
            var close = c.Items.OfType<MenuItem>().FirstOrDefault(m => Equals(m.Header, "关闭组件"));
            if (close != null) c.Items.Remove(close);
            c.Items.Add(new Separator());
            c.Items.Add(Item("删除便签", delegate { Delete(true); }));
            return c;
        }

        protected override void Disable()
        {
            Delete(true);
        }

        internal static bool NeedsConfirm(string text)
        {
            return !string.IsNullOrWhiteSpace(text);
        }

        internal bool Delete(bool confirm)
        {
            if (confirm && NeedsConfirm(box.Text) && !Program.Preview &&
                MessageBox.Show("删除这张便签？其中的内容将无法恢复。", "ClearDesk · 删除便签", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return false;
            deleted = true;
            save.Stop();
            Program.S.Notes.Remove(State);
            Program.Save();
            Close();
            return true;
        }
    }
}
