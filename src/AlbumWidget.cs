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
    public class AlbumData { public List<string> Sources = new List<string>(); public int IntervalSeconds = 8; public bool Fill = true, Shuffle, Subfolders; public string Last = ""; }

    internal static class AlbumLogic
    {
        internal static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".jfif" };
        internal static bool IsImage(string path) { return Extensions.Contains(Path.GetExtension(path ?? "").ToLowerInvariant()); }
        /// <summary>Images from the chosen folders and files (sorted by path, no duplicates, at most <paramref name="limit"/>).</summary>
        internal static List<string> Enumerate(IEnumerable<string> sources, bool subfolders, int limit = 2000)
        {
            var list = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
        AlbumData data; readonly Image a = new Image(), b = new Image(); readonly Grid stage = new Grid { ClipToBounds = true }; readonly TextBlock hint = Label("点右上角 ＋ 添加图片文件夹或图片", 12, Dim);
        List<string> files = new List<string>(); AlbumLogic.Playlist list; bool front; string current;
        internal int ShownCount; internal string ShownPath;
        internal bool Animate = true;
        internal Func<string, double, BitmapSource> Loader;
        internal AlbumData Data { get { return data; } }

        public AlbumWindow() : base("album", "图片相册", SystemParameters.WorkArea.Left + 40, SystemParameters.WorkArea.Top + 740, 320, 240)
        {
            data = ToolStore.Load<AlbumData>("album");
            Header.Visibility = Visibility.Collapsed; Layout.Margin = new Thickness(0);
            a.Stretch = b.Stretch = data.Fill ? Stretch.UniformToFill : Stretch.Uniform; a.Opacity = 1; b.Opacity = 0;
            stage.Children.Add(a); stage.Children.Add(b); hint.HorizontalAlignment = HorizontalAlignment.Center; hint.VerticalAlignment = VerticalAlignment.Center; stage.Children.Add(hint);
            var add = IconButton("\uE710", "添加图片文件夹 / 图片", null); add.Click += delegate { AddMenu(add); }; add.HorizontalAlignment = HorizontalAlignment.Right; add.VerticalAlignment = VerticalAlignment.Top; add.Margin = new Thickness(0, 8, 8, 0);
            add.Background = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)); Panel.SetZIndex(add, 5); stage.Children.Add(add);
            Body.Children.Add(stage);
            stage.MouseLeftButtonUp += (s, e) => { if (!WidgetDrag.IsInteractive(e.OriginalSource as DependencyObject, stage)) ShowNext(); };
            ContextMenu.Items.Insert(0, Item("下一张", ShowNext));
            ContextMenu.Items.Insert(1, Item("上一张", ShowPrevious));
            var interval = new MenuItem { Header = "切换间隔" };
            foreach (var sec in AlbumLogic.IntervalChoices) { int v = sec; var mi = Item(v < 60 ? v + " 秒" : (v / 60) + " 分钟", delegate { SetInterval(v); }); interval.Items.Add(mi); }
            ContextMenu.Items.Insert(2, interval);
            ContextMenu.Items.Insert(3, Item("铺满（裁剪） / 完整显示", delegate { data.Fill = !data.Fill; a.Stretch = b.Stretch = data.Fill ? Stretch.UniformToFill : Stretch.Uniform; Save(); }));
            ContextMenu.Items.Insert(4, Item("随机播放", delegate { data.Shuffle = !data.Shuffle; Save(); Reload(); }, data.Shuffle));
            ContextMenu.Items.Insert(5, Item("包含子文件夹", delegate { data.Subfolders = !data.Subfolders; Save(); Reload(); }, data.Subfolders));
            ContextMenu.Items.Insert(6, Item("清空图片来源", delegate { data.Sources.Clear(); Save(); Reload(); }));
            Reload();
            StartTimer();
        }
        void Save() { ToolStore.Save("album", data); }
        void StartTimer() { Tick(AlbumLogic.ClampInterval(data.IntervalSeconds) * 1000, delegate { ShowNext(); }); }   // Tick() skips hidden / collapsed widgets: no decoding while nobody sees it
        internal void SetInterval(int seconds)
        {
            data.IntervalSeconds = AlbumLogic.ClampInterval(seconds); Save();
            if (Timer != null) Timer.Interval = TimeSpan.FromSeconds(data.IntervalSeconds);
        }
        void AddMenu(FrameworkElement anchor)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            menu.Items.Add(Item("添加图片文件夹…", delegate { using (var d = new System.Windows.Forms.FolderBrowserDialog { Description = "选择图片所在的文件夹" }) if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK) AddSource(d.SelectedPath); }));
            menu.Items.Add(Item("添加图片…", delegate { var d = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.jfif" }; if (d.ShowDialog() == true) foreach (var f in d.FileNames) AddSource(f); }));
            menu.IsOpen = true;
        }
        internal void AddSource(string path) { if (!data.Sources.Contains(path, StringComparer.OrdinalIgnoreCase)) data.Sources.Add(path); Save(); Reload(); }
        internal void Reload()
        {
            files = AlbumLogic.Enumerate(data.Sources, data.Subfolders); list = new AlbumLogic.Playlist(files.Count, data.Shuffle, new Random());
            hint.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed; hint.Text = data.Sources.Count == 0 ? "点右上角 ＋ 添加图片文件夹或图片" : "没有找到图片";
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
