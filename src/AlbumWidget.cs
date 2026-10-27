using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace ClearDesk
{
    public class AlbumData { public List<string> Sources = new List<string>(); public int IntervalSeconds = 8; public bool Fill = true, Shuffle, Subfolders; public string Last = ""; public List<string> Excluded = new List<string>(); }

    /// <summary>Pictures that were pasted, dropped or picked as single files are copied into ClearDesk's own folder, so deleting the original does not break the widget.</summary>
    internal static class AlbumStore
    {
        internal const int MaxFiles = 300; internal const long MaxBytes = 40L * 1024 * 1024;
        internal static string Folder { get { return Path.Combine(ToolStore.Root, "album-images"); } }
        /// <summary>2.2.2: every album instance has its own folder. The first album ("album") keeps the 2.2.x folder, so nothing has to be moved.</summary>
        internal static string FolderFor(string instanceId) { return string.IsNullOrEmpty(instanceId) || instanceId == "album" ? Folder : Path.Combine(ToolStore.Root, "album-images~" + (instanceId.Contains("~") ? instanceId.Substring(instanceId.IndexOf('~') + 1) : instanceId)); }
        internal static bool IsStored(string path, string folder = null)
        {
            try { string f = Path.GetFullPath(folder ?? Folder).TrimEnd('\\') + "\\"; return Path.GetFullPath(path ?? "").StartsWith(f, StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }
        static string Hash(byte[] data) { using (var h = System.Security.Cryptography.SHA1.Create()) return BitConverter.ToString(h.ComputeHash(data)).Replace("-", "").ToLowerInvariant().Substring(0, 16); }
        /// <summary>Copies a picture file into the store (same content = same file). Returns the stored path or null.</summary>
        internal static string CopyIn(string source, string folder = null)
        {
            try
            {
                if (!AlbumLogic.IsImage(source) || !File.Exists(source)) return null;
                if (IsStored(source, folder)) return source;
                var info = new FileInfo(source); if (info.Length == 0 || info.Length > MaxBytes) return null;
                byte[] data = File.ReadAllBytes(source);
                return Save(data, Path.GetExtension(source).ToLowerInvariant(), Path.GetFileNameWithoutExtension(source), folder ?? Folder);
            }
            catch (Exception e) { Program.Log(e); return null; }
        }
        /// <summary>Stores a bitmap (clipboard / dropped picture data) as PNG.</summary>
        internal static string SaveBitmap(BitmapSource bitmap, string folder = null)
        {
            try
            {
                if (bitmap == null || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) return null;
                var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bitmap));
                using (var ms = new MemoryStream()) { enc.Save(ms); if (ms.Length > MaxBytes) return null; return Save(ms.ToArray(), ".png", "pasted-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), folder ?? Folder); }
            }
            catch (Exception e) { Program.Log(e); return null; }
        }
        static string Save(byte[] data, string ext, string baseName, string Folder)
        {
            Directory.CreateDirectory(Folder);
            string hash = Hash(data);
            var existing = Directory.EnumerateFiles(Folder, "*-" + hash + ".*").FirstOrDefault(); if (existing != null) return existing;
            if (Directory.GetFiles(Folder).Length >= MaxFiles) return null;
            foreach (char c in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(c, '_');
            if (baseName.Length > 40) baseName = baseName.Substring(0, 40);
            string path = Path.Combine(Folder, baseName + "-" + hash + ext), tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data); File.Move(tmp, path);
            return path;
        }
        internal static bool Remove(string path, string folder = null) { try { if (IsStored(path, folder) && File.Exists(path)) { File.Delete(path); return true; } } catch (Exception e) { Program.Log(e); } return false; }
        /// <summary>Picture files named by a drop / clipboard file list, plus folders (kept as sources).</summary>
        internal static void Split(IEnumerable<string> paths, out List<string> images, out List<string> folders)
        {
            images = new List<string>(); folders = new List<string>();
            foreach (var p in paths ?? Enumerable.Empty<string>()) { if (string.IsNullOrEmpty(p)) continue; if (Directory.Exists(p)) folders.Add(p); else if (AlbumLogic.IsImage(p)) images.Add(p); }
        }
    }

    internal static class AlbumLogic
    {
        internal static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".jfif" };
        internal static bool IsImage(string path) { return Extensions.Contains(Path.GetExtension(path ?? "").ToLowerInvariant()); }
        /// <summary>Images from the chosen folders and files (sorted by path, no duplicates, at most <paramref name="limit"/>).</summary>
        internal static List<string> Enumerate(IEnumerable<string> sources, bool subfolders, int limit = 2000, IEnumerable<string> excluded = null)
        {
            var list = new List<string>(); var seen = new HashSet<string>(excluded ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var s in sources ?? Enumerable.Empty<string>())
            {
                try
                {
                    if (Directory.Exists(s)) foreach (var f in Directory.EnumerateFiles(s, "*", subfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Where(IsImage).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) { if (list.Count >= limit) return list; if (seen.Add(f)) list.Add(f); }
                    else if (File.Exists(s) && IsImage(s) && seen.Add(s)) list.Add(s);
                }
                catch { }
                if (list.Count >= limit) break;
            }
            return list;
        }
        /// <summary>Order of play: sequential, or a shuffled cycle where no picture repeats before all were shown (and the first picture of a new cycle differs from the last one).</summary>
        internal sealed class Playlist
        {
            readonly Random rng; readonly bool shuffle; int count, pos = -1; int[] order = new int[0];
            internal Playlist(int count, bool shuffle, Random rng) { this.rng = rng ?? new Random(); this.shuffle = shuffle; Reset(count); }
            internal void Reset(int newCount) { count = newCount; pos = -1; order = Enumerable.Range(0, Math.Max(0, count)).ToArray(); if (shuffle) Shuffle(-1); }
            void Shuffle(int avoidFirst)
            {
                for (int i = order.Length - 1; i > 0; i--) { int j = rng.Next(i + 1); var t = order[i]; order[i] = order[j]; order[j] = t; }
                if (order.Length > 1 && order[0] == avoidFirst) { var t = order[0]; order[0] = order[order.Length - 1]; order[order.Length - 1] = t; }
            }
            internal int Next() { if (count == 0) return -1; int last = pos >= 0 ? order[pos] : -1; pos++; if (pos >= order.Length) { pos = 0; if (shuffle) Shuffle(last); } return order[pos]; }
            internal int Previous() { if (count == 0) return -1; pos = pos <= 0 ? order.Length - 1 : pos - 1; return order[pos]; }
        }
        internal static int[] IntervalChoices = { 3, 5, 8, 15, 30, 60, 300 };
        internal static int ClampInterval(int s) { return Math.Max(2, Math.Min(3600, s)); }
        /// <summary>Decode width in pixels for a picture shown in a box: never more than the box (screen) needs, which keeps memory low for large photos.</summary>
        internal static int DecodeWidth(double boxWidthDip, double dpiScale) { return (int)Math.Max(64, Math.Min(1920, Math.Round(boxWidthDip * dpiScale * 1.0))); }
    }

    public class AlbumWindow : ToolWidget
    {
        AlbumData data; string Folder { get { return AlbumStore.FolderFor(State.Id); } } readonly Image a = new Image(), b = new Image(); readonly Grid stage = new Grid { ClipToBounds = true }; readonly TextBlock hint = Label("还没有图片", 13, Brushes.White); readonly StackPanel emptyPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        internal Func<BitmapSource> ClipboardImage = () => { try { if (System.Windows.Clipboard.ContainsImage()) return System.Windows.Clipboard.GetImage(); } catch { } return null; };
        internal Func<IEnumerable<string>> ClipboardFiles = () => { try { if (System.Windows.Clipboard.ContainsFileDropList()) return System.Windows.Clipboard.GetFileDropList().Cast<string>().ToList(); } catch { } return new List<string>(); };
        List<string> files = new List<string>(); AlbumLogic.Playlist list; bool front; string current;
        internal int ShownCount; internal string ShownPath;
        internal bool Animate = true;
        internal Func<string, double, BitmapSource> Loader;
        internal AlbumData Data { get { return data; } }
        internal int TestFileCount { get { return files.Count; } }

        public AlbumWindow() : base("album", "图片相册", SystemParameters.WorkArea.Left + 40, SystemParameters.WorkArea.Top + 740, 320, 240)
        {
            data = ToolStore.Load<AlbumData>(State.Id);
            Header.Visibility = Visibility.Collapsed; Layout.Margin = new Thickness(0);
            a.Stretch = b.Stretch = data.Fill ? Stretch.UniformToFill : Stretch.Uniform; a.Opacity = 1; b.Opacity = 0;
            stage.Children.Add(a); stage.Children.Add(b);
            hint.HorizontalAlignment = HorizontalAlignment.Center; hint.FontWeight = FontWeights.SemiBold; hint.Margin = new Thickness(0, 0, 0, 6);
            var addBig = TextButton("添加图片…", delegate { PickImages(); }, true); addBig.HorizontalAlignment = HorizontalAlignment.Center; addBig.Padding = new Thickness(18, 6, 18, 6);
            var hint2 = Label("或把图片 / 文件夹拖进来；复制图片后右键 → 粘贴图片", 11, Dim); hint2.TextWrapping = TextWrapping.Wrap; hint2.TextAlignment = TextAlignment.Center; hint2.Margin = new Thickness(14, 8, 14, 0);
            emptyPanel.Children.Add(hint); emptyPanel.Children.Add(addBig); emptyPanel.Children.Add(hint2); stage.Children.Add(emptyPanel);
            AllowDrop = true;
            PreviewDragOver += (s, e) => { e.Effects = Droppable(e.Data) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            PreviewDragEnter += (s, e) => { e.Effects = Droppable(e.Data) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            Drop += (s, e) => { int n = AddFromData(e.Data); Toast(n > 0 ? "\uE710" : "\uE783", n > 0 ? "已添加 " + n + " 张图片" : "没有可添加的图片"); e.Handled = true; };
            PreviewKeyDown += (s, e) => { if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { PasteImage(); e.Handled = true; } };
            var add = IconButton("\uE710", "添加图片文件夹 / 图片", null); add.Click += delegate { AddMenu(add); }; add.HorizontalAlignment = HorizontalAlignment.Right; add.VerticalAlignment = VerticalAlignment.Top; add.Margin = new Thickness(0, 8, 8, 0);
            add.Background = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)); Panel.SetZIndex(add, 5); stage.Children.Add(add);
            Body.Children.Add(stage);
            stage.MouseLeftButtonUp += (s, e) => { if (emptyPanel.Visibility != Visibility.Visible && !WidgetDrag.IsInteractive(e.OriginalSource as DependencyObject, stage)) ShowNext(); };
            ContextMenu.Items.Insert(0, Item("添加 / 更换图片…", delegate { PickImages(); }));
            ContextMenu.Items.Insert(1, Item("粘贴图片", delegate { PasteImage(); }));
            ContextMenu.Items.Insert(2, Item("添加图片文件夹…", delegate { PickFolder(); }));
            ContextMenu.Items.Insert(3, Item("移除当前图片", delegate { RemoveCurrent(); }));
            ContextMenu.Items.Insert(4, new Separator());
            ContextMenu.Items.Insert(5, Item("下一张", ShowNext));
            ContextMenu.Items.Insert(6, Item("上一张", ShowPrevious));
            var interval = new MenuItem { Header = "切换间隔" };
            foreach (var sec in AlbumLogic.IntervalChoices) { int v = sec; var mi = Item(v < 60 ? v + " 秒" : (v / 60) + " 分钟", delegate { SetInterval(v); }); interval.Items.Add(mi); }
            ContextMenu.Items.Insert(7, interval);
            ContextMenu.Items.Insert(8, Item("铺满（裁剪） / 完整显示", delegate { SetFill(!data.Fill); }));
            ContextMenu.Items.Insert(9, Item("随机播放", delegate { data.Shuffle = !data.Shuffle; Save(); Reload(); }, data.Shuffle));
            ContextMenu.Items.Insert(10, Item("包含子文件夹", delegate { data.Subfolders = !data.Subfolders; Save(); Reload(); }, data.Subfolders));
            ContextMenu.Items.Insert(11, Item("清空全部图片", delegate { ClearAll(); }));
            Reload();
            StartTimer();
        }
        void Save() { ToolStore.Save(State.Id, data); }
        void StartTimer() { Tick(AlbumLogic.ClampInterval(data.IntervalSeconds) * 1000, delegate { ShowNext(); }); }   // Tick() skips hidden / collapsed widgets: no decoding while nobody sees it
        internal void SetInterval(int seconds)
        {
            data.IntervalSeconds = AlbumLogic.ClampInterval(seconds); Save();
            if (Timer != null) Timer.Interval = TimeSpan.FromSeconds(data.IntervalSeconds);
        }
        void AddMenu(FrameworkElement anchor)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            menu.Items.Add(Item("添加图片…", delegate { PickImages(); }));
            menu.Items.Add(Item("添加图片文件夹…", delegate { PickFolder(); }));
            menu.Items.Add(Item("粘贴图片", delegate { PasteImage(); }));
            menu.IsOpen = true;
        }
        internal Func<string[]> ImageDialog;      // test hook
        internal void PickImages()
        {
            string[] names;
            if (ImageDialog != null) names = ImageDialog();
            else using (var host = new DialogHost())
            {
                var d = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "选择图片", Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.jfif|所有文件|*.*" };
                names = d.ShowDialog(host.Owner) == true ? d.FileNames : null;
            }
            if (names == null) return;
            int n = AddPaths(names); if (n > 0) Toast("\uE710", "已添加 " + n + " 张图片");
        }
        void PickFolder()
        {
            using (var host = new DialogHost())
            using (var d = new System.Windows.Forms.FolderBrowserDialog { Description = "选择图片所在的文件夹" })
                if (d.ShowDialog(host.Win32) == System.Windows.Forms.DialogResult.OK) AddSource(d.SelectedPath);
        }
        static bool Droppable(IDataObject d)
        {
            try
            {
                var files = d.GetDataPresent(DataFormats.FileDrop) ? d.GetData(DataFormats.FileDrop) as string[] : null;
                if (files != null) { List<string> img, fol; AlbumStore.Split(files, out img, out fol); return img.Count + fol.Count > 0; }
                return d.GetDataPresent(DataFormats.Bitmap) || d.GetDataPresent("PNG");
            }
            catch { return false; }
        }
        /// <summary>Adds picture files (copied into ClearDesk's folder) and folders (kept as sources). Returns how many pictures / folders were added.</summary>
        internal int AddPaths(IEnumerable<string> paths)
        {
            List<string> images, folders; AlbumStore.Split(paths, out images, out folders); int n = 0; string last = null;
            foreach (var f in images) { var stored = AlbumStore.CopyIn(f, Folder); if (stored != null) { n++; last = stored; } }
            foreach (var d in folders) if (!data.Sources.Contains(d, StringComparer.OrdinalIgnoreCase)) { data.Sources.Add(d); n++; }
            if (n > 0) { data.Excluded.RemoveAll(x => images.Concat(new[] { last ?? "" }).Contains(x, StringComparer.OrdinalIgnoreCase)); Save(); Reload(); if (last != null) ShowPath(last); }
            return n;
        }
        /// <summary>Handles a drop: files / folders, or picture data (for example dragged from a browser).</summary>
        internal int AddFromData(IDataObject d)
        {
            try
            {
                var files = d.GetDataPresent(DataFormats.FileDrop) ? d.GetData(DataFormats.FileDrop) as string[] : null;
                if (files != null) return AddPaths(files);
                BitmapSource bmp = null;
                if (d.GetDataPresent(DataFormats.Bitmap)) bmp = d.GetData(DataFormats.Bitmap) as BitmapSource;
                if (bmp == null && d.GetDataPresent("PNG")) { var ms = d.GetData("PNG") as MemoryStream; if (ms != null) { var dec = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); bmp = dec.Frames[0]; } }
                return AddBitmap(bmp) ? 1 : 0;
            }
            catch (Exception e) { Program.Log(e); return 0; }
        }
        internal bool AddBitmap(BitmapSource bmp)
        {
            string stored = AlbumStore.SaveBitmap(bmp, Folder); if (stored == null) return false;
            Save(); Reload(); ShowPath(stored); return true;
        }
        /// <summary>Pastes a copied picture (or copied picture files) from the clipboard.</summary>
        internal bool PasteImage()
        {
            var bmp = ClipboardImage(); if (bmp != null && AddBitmap(bmp)) { Toast("\uE710", "已粘贴图片"); return true; }
            int n = AddPaths(ClipboardFiles()); if (n > 0) { Toast("\uE710", "已粘贴 " + n + " 张图片"); return true; }
            Toast("\uE783", "剪贴板里没有图片"); return false;
        }
        internal void SetFill(bool fill) { data.Fill = fill; a.Stretch = b.Stretch = fill ? Stretch.UniformToFill : Stretch.Uniform; Save(); }
        /// <summary>Removes the picture on screen: a stored copy is deleted, a single-file source is dropped, a picture from a folder is hidden.</summary>
        internal bool RemoveCurrent()
        {
            string p = current; if (p == null) return false;
            if (AlbumStore.IsStored(p, Folder)) AlbumStore.Remove(p, Folder);
            else if (data.Sources.RemoveAll(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase)) == 0) data.Excluded.Add(p);
            current = null; Save(); Reload(); return true;
        }
        void ClearAll()
        {
            try { if (Directory.Exists(Folder)) foreach (var f in Directory.GetFiles(Folder)) AlbumStore.Remove(f, Folder); } catch { }
            data.Sources.Clear(); data.Excluded.Clear(); current = null; Save(); Reload();
        }
        internal void AddSource(string path) { if (!data.Sources.Contains(path, StringComparer.OrdinalIgnoreCase)) data.Sources.Add(path); Save(); Reload(); }
        List<string> AllSources() { var l = new List<string>(data.Sources); if (Directory.Exists(Folder)) l.Add(Folder); return l; }
        void ShowPath(string path) { int i = files.FindIndex(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase)); if (i >= 0) { list = new AlbumLogic.Playlist(files.Count, data.Shuffle, new Random()); Show(files[i]); } }
        internal void Reload()
        {
            files = AlbumLogic.Enumerate(AllSources(), data.Subfolders, 2000, data.Excluded); list = new AlbumLogic.Playlist(files.Count, data.Shuffle, new Random());
            emptyPanel.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed; hint.Text = data.Sources.Count == 0 && !Directory.Exists(Folder) ? "还没有图片" : "没有找到图片";
            if (files.Count == 0) { a.Source = null; b.Source = null; return; }
            ShowNext();
        }
        internal void ShowNext() { if (list == null) return; int i = list.Next(); if (i >= 0) Show(files[i]); }
        internal void ShowPrevious() { if (list == null) return; int i = list.Previous(); if (i >= 0) Show(files[i]); }

        BitmapSource Load(string path)
        {
            double width = Math.Max(ActualWidth, 200) * ContentScale; double dpi = 1; try { dpi = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }
            if (Loader != null) return Loader(path, width);
            var bmp = new BitmapImage();
            bmp.BeginInit(); bmp.UriSource = new Uri(path); bmp.DecodePixelWidth = AlbumLogic.DecodeWidth(width, dpi); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile; bmp.EndInit(); bmp.Freeze();
            return bmp;
        }
        void Show(string path)
        {
            BitmapSource src; try { src = Load(path); } catch { files.Remove(path); if (files.Count == 0) { Reload(); return; } list = new AlbumLogic.Playlist(files.Count, data.Shuffle, new Random()); return; }
            var to = front ? a : b; var from = front ? b : a; front = !front;
            to.Source = src; current = path; ShownPath = path; ShownCount++;
            if (!Animate) { to.Opacity = 1; from.Opacity = 0; from.Source = null; return; }
            var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(700)) { EasingFunction = new QuadraticEase() };
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(700)); fadeOut.Completed += delegate { if (from.Opacity == 0) from.Source = null; };   // release the old bitmap
            to.BeginAnimation(OpacityProperty, fadeIn); from.BeginAnimation(OpacityProperty, fadeOut);
        }
        internal string CurrentPath { get { return current; } }
    }
}
