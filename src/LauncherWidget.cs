using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ClearDesk
{
    public class LauncherItem { public string Id = Guid.NewGuid().ToString("N"), Path = "", Name = ""; }
    public class LauncherData { public List<LauncherItem> Items = new List<LauncherItem>(); public int IconSize = 44; }

    internal static class LauncherLogic
    {
        internal static string Classify(string path)
        {
            string p = (path ?? "").Trim();
            if (p.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || p.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "url";
            if (p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return "app";
            if (Directory.Exists(p)) return "folder";
            string ext = Path.GetExtension(p).ToLowerInvariant();
            return ext == ".exe" || ext == ".lnk" || ext == ".bat" || ext == ".cmd" ? "app" : "file";
        }
        internal static string NormalizeUrl(string text)
        {
            string t = (text ?? "").Trim(); if (t.Length == 0 || t.Contains(" ")) return null;
            if (!t.Contains("://")) t = "https://" + t;
            Uri u; if (!Uri.TryCreate(t, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https") || !u.Host.Contains(".") && u.Host != "localhost") return null;
            return u.AbsoluteUri;
        }
        internal static string NameFor(string path)
        {
            string kind = Classify(path);
            if (kind == "url") { Uri u; return Uri.TryCreate(path, UriKind.Absolute, out u) ? u.Host.StartsWith("www.") ? u.Host.Substring(4) : u.Host : path; }
            if (path.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase)) return path.Substring(17);
            string n = Path.GetFileNameWithoutExtension(path.TrimEnd('\\', '/'));
            return n.Length > 0 ? n : path;
        }
        /// <summary>Adds an item (no duplicates, at most 60). Returns the new item or null.</summary>
        internal static LauncherItem Add(LauncherData data, string path, string name = null)
        {
            string p = (path ?? "").Trim().Trim('"'); if (p.Length == 0 || data.Items.Count >= 60) return null;
            if (data.Items.Any(i => string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase))) return null;
            var item = new LauncherItem { Path = p, Name = string.IsNullOrWhiteSpace(name) ? NameFor(p) : name.Trim() };
            data.Items.Add(item); return item;
        }
        internal static bool Move(List<LauncherItem> items, int index, int delta)
        {
            int to = index + delta; if (index < 0 || index >= items.Count || to < 0 || to >= items.Count) return false;
            var x = items[index]; items.RemoveAt(index); items.Insert(to, x); return true;
        }
        internal static int Columns(double width, int iconSize) { return Math.Max(1, (int)(width / (iconSize + 28))); }
    }

    public class LauncherWindow : ToolWidget
    {
        LauncherData data;
        readonly WrapPanel grid = new WrapPanel();
        readonly TextBlock empty = Label("把文件、文件夹、网址或应用拖到这里；也可点右上角 ＋ 添加。", 12, Dim);
        readonly TextBox urlBox;
        readonly DockPanel urlRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        internal Action<string> Open = p => { if (LauncherLogic.Classify(p) == "url") Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); else Program.OpenItem(p); };
        internal LauncherData Data { get { return data; } }

        public LauncherWindow() : base("launcher", "快捷启动", SystemParameters.WorkArea.Left + 720, SystemParameters.WorkArea.Top + 80, 340, 240)
        {
            data = ToolStore.Load<LauncherData>("launcher");
            Heading.Text = "快捷启动";
            var add = IconButton("\uE710", "添加文件 / 文件夹 / 网址", null);
            add.Click += delegate { ShowAddMenu(add); };
            AddHeaderButton(add);
            var wrap = Scroller(grid); grid.Margin = new Thickness(0, 0, 0, 4);
            var root = new Grid(); root.Children.Add(wrap); empty.Margin = new Thickness(2, 6, 0, 0); empty.TextWrapping = TextWrapping.Wrap; root.Children.Add(empty);
            urlBox = Input("输入网址，按回车添加");
            var ok = IconButton("\uE73E", "添加网址", CommitUrl); DockPanel.SetDock(ok, Dock.Right); urlRow.Children.Add(ok); urlRow.Children.Add(urlBox);
            urlBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) { CommitUrl(); e.Handled = true; } else if (e.Key == Key.Escape) urlRow.Visibility = Visibility.Collapsed; };
            var host = new DockPanel(); DockPanel.SetDock(urlRow, Dock.Bottom); host.Children.Add(urlRow); host.Children.Add(root);
            Body.Children.Add(host);
            AllowDrop = true;
            Drop += OnDrop; DragOver += (s, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text) ? DragDropEffects.Link : DragDropEffects.None; e.Handled = true; };
            Rebuild();
        }

        void Save() { ToolStore.Save("launcher", data); }
        void OnDrop(object sender, DragEventArgs e)
        {
            int added = 0;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null) foreach (var f in files) if (AddPath(f)) added++;
            if (files == null && e.Data.GetDataPresent(DataFormats.Text)) { string url = LauncherLogic.NormalizeUrl(Convert.ToString(e.Data.GetData(DataFormats.Text))); if (url != null && AddPath(url)) added++; }
            if (added > 0) { Toast("\uE710", "已添加 " + added + " 项"); }
            e.Handled = true;
        }
        internal bool AddPath(string path)
        {
            var item = LauncherLogic.Add(data, path); if (item == null) return false;
            Save(); Rebuild(); return true;
        }
        void ShowAddMenu(FrameworkElement anchor)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            menu.Items.Add(Item("添加文件或应用…", delegate { var d = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "选择文件或应用" }; if (d.ShowDialog() == true) foreach (var f in d.FileNames) AddPath(f); }));
            menu.Items.Add(Item("添加文件夹…", delegate { using (var d = new System.Windows.Forms.FolderBrowserDialog()) if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK) AddPath(d.SelectedPath); }));
            menu.Items.Add(Item("添加网址…", delegate { AskUrl(); }));
            menu.IsOpen = true;
        }
        void AskUrl() { urlRow.Visibility = Visibility.Visible; urlBox.Text = ""; urlBox.Focus(); }
        void CommitUrl() { string u = LauncherLogic.NormalizeUrl(urlBox.Text); if (u == null) { Toast("\uE783", "网址格式不对"); return; } AddPath(u); urlRow.Visibility = Visibility.Collapsed; }

        void Rebuild()
        {
            grid.Children.Clear(); empty.Visibility = data.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            int size = Math.Max(28, Math.Min(96, data.IconSize));
            foreach (var it in data.Items.ToList())
            {
                var item = it; string kind = LauncherLogic.Classify(item.Path);
                UIElement icon;
                if (kind == "url") icon = new TextBlock { Text = "\uE774", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = size * .62, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                else { ImageSource src = null; try { src = Native.Icon(item.Path); } catch { } icon = new Image { Source = src, Width = size, Height = size, Stretch = Stretch.Uniform, SnapsToDevicePixels = true }; }
                var name = new TextBlock { Text = item.Name, FontSize = 11, Foreground = Brushes.White, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = size + 22, Margin = new Thickness(0, 4, 0, 0) };
                var holder = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center }; holder.Children.Add(icon);
                var stack = new StackPanel(); stack.Children.Add(holder); stack.Children.Add(name);
                var tile = new Button
                {
                    Content = stack, Width = size + 28, Padding = new Thickness(4, 8, 4, 6), Margin = new Thickness(1), Cursor = Cursors.Hand, ToolTip = item.Name + "\n" + item.Path, Focusable = false, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = Brushes.White
                };
                tile.Template = TileTemplate();
                tile.Click += delegate { try { Open(item.Path); } catch (Exception ex) { Toast("\uE783", "无法打开：" + ex.Message); } };
                var menu = new ContextMenu();
                menu.Items.Add(Item("打开", delegate { try { Open(item.Path); } catch { } }));
                if (kind != "url" && kind != "app" || File.Exists(item.Path)) menu.Items.Add(Item("在文件夹中显示", delegate { try { Process.Start("explorer.exe", "/select,\"" + item.Path + "\""); } catch { } }));
                menu.Items.Add(Item("前移", delegate { int i = data.Items.IndexOf(item); if (LauncherLogic.Move(data.Items, i, -1)) { Save(); Rebuild(); } }));
                menu.Items.Add(Item("后移", delegate { int i = data.Items.IndexOf(item); if (LauncherLogic.Move(data.Items, i, 1)) { Save(); Rebuild(); } }));
                menu.Items.Add(Item("移除", delegate { data.Items.Remove(item); Save(); Rebuild(); }));
                tile.ContextMenu = menu;
                grid.Children.Add(tile);
            }
        }
        static ControlTemplate TileTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border), "b"); border.SetValue(Border.BackgroundProperty, Brushes.Transparent); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
            border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter)); border.AppendChild(cp);
            var t = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; over.Setters.Add(new Setter(Border.BackgroundProperty, Hover, "b")); t.Triggers.Add(over); return t;
        }
    }
}
