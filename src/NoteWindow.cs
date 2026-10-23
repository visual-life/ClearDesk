using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows.Media.Imaging;
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
        readonly ScrollViewer scroller;
        readonly StackPanel content = new StackPanel();
        readonly StackPanel gallery = new StackPanel();
        TextBlock hint;
        internal ScrollViewer TestScroller { get { return scroller; } }
        internal int TestImageCount { get { return gallery.Children.Count; } }
        static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff" };
        internal static string ImageFolder { get { return Path.Combine(Program.Data, "note-images"); } }
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
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, FontFamily = new FontFamily("Microsoft YaHei UI")
            };
            if (State.Images == null) State.Images = new List<NoteImage>();
            box.Resources[SystemParameters.VerticalScrollBarWidthKey] = 6.0;
            box.Template = (ControlTemplate)XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='TextBox'><ScrollViewer x:Name='PART_ContentHost' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='Transparent'/></ControlTemplate>");
            // One scrolling column: text, then images. No scrollbar is drawn; the wheel scrolls.
            gallery.Margin = new Thickness(0, 4, 0, 6);
            content.Children.Add(box);
            content.Children.Add(gallery);
            scroller = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Background = Brushes.Transparent };
            scroller.PreviewMouseWheel += delegate (object s, MouseWheelEventArgs e) { scroller.ScrollToVerticalOffset(scroller.VerticalOffset - e.Delta * .45); e.Handled = true; };
            Body.Children.Add(scroller);
            hint = Program.Text("写点什么…（可粘贴或拖入图片）", 14, "#99FFFFFF");
            hint.IsHitTestVisible = false;
            hint.VerticalAlignment = VerticalAlignment.Top;
            Body.Children.Add(hint);
            Action hintSync = delegate { hint.Visibility = box.Text.Length == 0 && State.Images.Count == 0 ? Visibility.Visible : Visibility.Collapsed; };
            box.SelectionChanged += delegate { Dispatcher.BeginInvoke(new Action(FollowCaret), DispatcherPriority.Loaded); };
            box.PreviewKeyDown += delegate (object s, KeyEventArgs e)
            {
                bool paste = (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0) || (e.Key == Key.Insert && (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                if (paste && TryPasteImage()) e.Handled = true;
            };
            CommandManager.AddPreviewExecutedHandler(box, delegate (object s, ExecutedRoutedEventArgs e) { if (e.Command == ApplicationCommands.Paste && TryPasteImage()) e.Handled = true; });
            AllowDrop = true;
            PreviewDragOver += delegate (object s, DragEventArgs e) { if (DroppedImages(e.Data).Any() || e.Data.GetDataPresent(DataFormats.Bitmap)) { e.Effects = DragDropEffects.Copy; e.Handled = true; } };
            PreviewDragEnter += delegate (object s, DragEventArgs e) { if (DroppedImages(e.Data).Any()) { e.Effects = DragDropEffects.Copy; e.Handled = true; } };
            PreviewDrop += delegate (object s, DragEventArgs e)
            {
                var files = DroppedImages(e.Data).ToList();
                if (files.Count > 0) { foreach (var f in files) AddImageFile(f); e.Handled = true; }
                else if (e.Data.GetDataPresent(DataFormats.Bitmap)) { var b = e.Data.GetData(DataFormats.Bitmap) as BitmapSource; if (b != null) { AddImageBitmap(b); e.Handled = true; } }
            };
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
            Point gripStart = new Point(); double startW = 0, startH = 0;
            grip.DragStarted += delegate { gripStart = CursorInfo.Dip(this); startW = Width; startH = Height; };
            grip.DragDelta += delegate (object s, DragDeltaEventArgs e)
            {
                if (State.Collapsed || State.Locked) return;
                var now = CursorInfo.Dip(this);
                double w = startW + now.X - gripStart.X, h = startH + now.Y - gripStart.Y;
                if (Program.S.Magnet && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
                {
                    var r = MagnetSnap.SnapResize(new Rect(Left, Top, Math.Max(1, w), Math.Max(1, h)), Program.SnapTargets(this), CursorInfo.Areas(this), MagnetSnap.Threshold);
                    if (r.SnappedX) w = r.X - Left;
                    if (r.SnappedY) h = r.Y - Top;
                    GuideOverlay.Show(r.Guides);
                }
                else GuideOverlay.Hide();
                Width = Math.Max(200, w);
                Height = Math.Max(140, h);
                State.Width = Width; State.Height = Height;
            };
            grip.DragCompleted += delegate { GuideOverlay.Hide(); Program.Save(); };
            Root.Children.Add(grip);
            SizeChanged += delegate { Fit(); };
            Loaded += delegate { RenderImages(); Fit(); };
            Closed += delegate { if (!deleted) Flush(); };
        }

        void Fit()
        {
            double view = Math.Max(40, ActualHeight - 20 - 20 - 30 - 8);
            scroller.Height = view;
            box.MinHeight = State.Images.Count == 0 ? view : 28;
            double avail = Math.Max(60, ActualWidth - 40);
            foreach (FrameworkElement cell in gallery.Children) SizeCell(cell, avail);
            grip.Visibility = State.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        }

        protected override void SetCollapsed()
        {
            base.SetCollapsed();
            if (grip != null) Fit();
        }

        void FollowCaret()
        {
            if (!box.IsKeyboardFocused) return;
            var r = box.GetRectFromCharacterIndex(box.CaretIndex);
            if (r.IsEmpty) return;
            var top = box.TranslatePoint(r.TopLeft, content).Y;
            if (top < scroller.VerticalOffset) scroller.ScrollToVerticalOffset(top);
            else if (top + r.Height > scroller.VerticalOffset + scroller.ViewportHeight) scroller.ScrollToVerticalOffset(top + r.Height - scroller.ViewportHeight + 4);
        }

        internal static bool IsImageFile(string path)
        {
            return !string.IsNullOrEmpty(path) && ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()) && File.Exists(path);
        }

        static IEnumerable<string> DroppedImages(IDataObject data)
        {
            var files = data.GetDataPresent(DataFormats.FileDrop) ? data.GetData(DataFormats.FileDrop) as string[] : null;
            return files == null ? Enumerable.Empty<string>() : files.Where(IsImageFile);
        }

        bool TryPasteImage()
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList().Cast<string>().Where(IsImageFile).ToList();
                    if (files.Count == 0) return false;
                    foreach (var f in files) AddImageFile(f);
                    return true;
                }
                if (Clipboard.ContainsData("PNG"))
                {
                    var png = Clipboard.GetData("PNG") as MemoryStream;
                    if (png != null) { AddImageBytes(png.ToArray(), ".png"); return true; }
                }
                if (Clipboard.ContainsImage())
                {
                    var b = Clipboard.GetImage();
                    if (b != null) { AddImageBitmap(b); return true; }
                }
            }
            catch (Exception e) { Program.Log(e); }
            return false;
        }

        internal void AddImageFile(string path)
        {
            try { AddImageBytes(File.ReadAllBytes(path), Path.GetExtension(path).ToLowerInvariant()); }
            catch (Exception e) { Program.Log(e); }
        }

        internal void AddImageBitmap(BitmapSource bitmap)
        {
            var encoder = new PngBitmapEncoder();
            // clipboard DIBs often carry a garbage alpha channel; flatten to opaque BGR
            encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0)));
            using (var ms = new MemoryStream()) { encoder.Save(ms); AddImageBytes(ms.ToArray(), ".png"); }
        }

        void AddImageBytes(byte[] bytes, string extension)
        {
            if (bytes == null || bytes.Length == 0) return;
            Directory.CreateDirectory(ImageFolder);
            string name = Guid.NewGuid().ToString("N") + extension;
            File.WriteAllBytes(Path.Combine(ImageFolder, name), bytes);
            State.Images.Add(new NoteImage { File = name, Scale = 1 });
            RenderImages();
            Flush();
            Dispatcher.BeginInvoke(new Action(delegate { scroller.ScrollToEnd(); }), DispatcherPriority.Loaded);
        }

        internal void RemoveImage(NoteImage image)
        {
            State.Images.Remove(image);
            DeleteFile(image);
            RenderImages();
            Flush();
        }

        static void DeleteFile(NoteImage image)
        {
            try
            {
                if (image == null || string.IsNullOrEmpty(image.File) || image.File.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) return;
                string path = Path.Combine(ImageFolder, image.File);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e) { Program.Log(e); }
        }

        internal static void DeleteImages(BoxData note)
        {
            if (note == null || note.Images == null) return;
            foreach (var i in note.Images.ToArray()) DeleteFile(i);
            note.Images.Clear();
        }

        void SetImageScale(NoteImage image, double scale)
        {
            image.Scale = Math.Max(.2, Math.Min(1, scale));
            Fit();
        }

        void SizeCell(FrameworkElement cell, double avail)
        {
            var image = cell.Tag as NoteImage;
            if (image != null) cell.Width = Math.Max(48, avail * Math.Max(.2, Math.Min(1, image.Scale)));
        }

        void RenderImages()
        {
            gallery.Children.Clear();
            double avail = Math.Max(60, ActualWidth - 40);
            foreach (var image in State.Images.ToArray())
            {
                BitmapImage bmp = null;
                try
                {
                    string path = Path.Combine(ImageFolder, image.File ?? "");
                    if (!File.Exists(path)) continue;
                    bmp = new BitmapImage();
                    bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile; bmp.DecodePixelWidth = 900; bmp.UriSource = new Uri(path); bmp.EndInit(); bmp.Freeze();
                }
                catch (Exception e) { Program.Log(e); continue; }
                var img = image;
                var picture = new Image { Source = bmp, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both };
                RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
                var frame = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), Child = picture };
                frame.SizeChanged += delegate { picture.Clip = new RectangleGeometry(new Rect(0, 0, picture.ActualWidth, picture.ActualHeight), 9, 9); };
                var cell = new Grid { Tag = img, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
                cell.Children.Add(frame);
                var close = new Button
                {
                    Content = "\uE711", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 9, Width = 22, Height = 22, Margin = new Thickness(0, 6, 6, 0),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Foreground = Brushes.White, ToolTip = "删除图片", Opacity = 0, Cursor = Cursors.Hand, Focusable = false,
                    Template = remove.Template
                };
                close.Click += delegate { RemoveImage(img); };
                var resize = new Thumb
                {
                    Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 4, 4),
                    Cursor = Cursors.SizeNWSE, ToolTip = "拖动调整图片大小", Opacity = 0, Template = grip.Template
                };
                double startWidth = 0; Point start = new Point();
                resize.DragStarted += delegate { startWidth = cell.ActualWidth; start = CursorInfo.Dip(this); };
                resize.DragDelta += delegate { var now = CursorInfo.Dip(this); SetImageScale(img, (startWidth + now.X - start.X) / Math.Max(60, ActualWidth - 40)); };
                resize.DragCompleted += delegate { Flush(); };
                cell.Children.Add(close); cell.Children.Add(resize);
                cell.MouseEnter += delegate { close.Opacity = 1; resize.Opacity = State.Locked ? 0 : 1; };
                cell.MouseLeave += delegate { close.Opacity = 0; resize.Opacity = 0; };
                var menu = new ContextMenu();
                foreach (var size in new[] { new { T = "原宽", V = 1.0 }, new { T = "中等", V = .6 }, new { T = "小图", V = .35 } })
                {
                    var v = size.V; var item = new MenuItem { Header = size.T };
                    item.Click += delegate { SetImageScale(img, v); Flush(); };
                    menu.Items.Add(item);
                }
                menu.Opened += delegate { foreach (MenuItem m in menu.Items.OfType<MenuItem>().Take(3)) m.IsChecked = false; };
                menu.Items.Add(new Separator());
                var copy = new MenuItem { Header = "复制图片" };
                copy.Click += delegate { try { Clipboard.SetImage(bmp); } catch (Exception e) { Program.Log(e); } };
                menu.Items.Add(copy);
                var open = new MenuItem { Header = "用默认程序打开" };
                open.Click += delegate { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(ImageFolder, img.File)) { UseShellExecute = true }); } catch (Exception e) { Program.Log(e); } };
                menu.Items.Add(open);
                var del = new MenuItem { Header = "删除图片" };
                del.Click += delegate { RemoveImage(img); };
                menu.Items.Add(del);
                cell.ContextMenu = menu;
                SizeCell(cell, avail);
                gallery.Children.Add(cell);
            }
            if (hint != null) hint.Visibility = box.Text.Length == 0 && State.Images.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (IsLoaded) Fit();
        }

        void InsertImageDialog()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "插入图片", Multiselect = true, Filter = "图片|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.tif;*.tiff" };
            if (dialog.ShowDialog(this) == true) foreach (var f in dialog.FileNames) AddImageFile(f);
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
            c.Items.Insert(2, Item("插入图片…", InsertImageDialog));
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
            if (confirm && (NeedsConfirm(box.Text) || State.Images.Count > 0) && !Program.Preview &&
                MessageBox.Show("删除这张便签？其中的内容将无法恢复。", "ClearDesk · 删除便签", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return false;
            deleted = true;
            save.Stop();
            Program.S.Notes.Remove(State);
            DeleteImages(State);
            Program.Save();
            Close();
            return true;
        }
    }
}
