using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClearDesk
{
    public class ClipEntry
    {
        public string Id = Guid.NewGuid().ToString("N"), Kind = "text", Text = "", Hash = "", Time = "";
        public bool Pinned;
        [ScriptIgnore] public byte[] Png;            // images: PNG bytes, memory only
    }
    public class ClipboardConfig { public bool Paused, Persist; }
    public class ClipboardSaved { public string Protected = ""; }

    /// <summary>Clipboard history logic (no UI, no Win32): filtering, dedupe, pin, caps, optional DPAPI-encrypted persistence.</summary>
    internal sealed class ClipboardHistory
    {
        internal readonly List<ClipEntry> Items = new List<ClipEntry>();       // newest first
        internal int MaxText = 40, MaxImages = 6, MaxChars = 20000, MaxImageBytes = 4 * 1024 * 1024, MaxTotalImageBytes = 16 * 1024 * 1024;
        internal bool Paused;

        // Formats that mean "do not record this" (password managers, Windows' own clipboard-history opt-out).
        static readonly string[] ExcludeFormats = { "ExcludeClipboardContentFromMonitorProcessing", "Clipboard Viewer Ignore", "x-kde-passwordManagerHint" };
        static readonly string[] PasswordManagers = { "keepass", "keepassxc", "1password", "bitwarden", "lastpass", "dashlane", "enpass", "nordpass", "roboform", "proton pass", "protonpass", "keeper", "kwallet", "passwordsafe", "psafe", "buttercup", "strongbox" };

        /// <summary>True when the data on the clipboard must not be stored. <paramref name="read"/> returns the data of a format (used for the DWORD flags CanIncludeInClipboardHistory = 0).</summary>
        internal static bool ShouldSkip(IEnumerable<string> formats, Func<string, object> read, string ownerExe)
        {
            var set = new HashSet<string>(formats ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            if (ExcludeFormats.Any(set.Contains)) return true;
            foreach (var flag in new[] { "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
                if (set.Contains(flag) && read != null && IsZeroFlag(SafeRead(read, flag))) return true;
            return IsPasswordManager(ownerExe);
        }
        static object SafeRead(Func<string, object> read, string name) { try { return read(name); } catch { return null; } }
        internal static bool IsZeroFlag(object data)
        {
            var ms = data as MemoryStream; if (ms != null) { var b = ms.ToArray(); return b.Length >= 1 && b.Take(4).All(x => x == 0); }
            var bytes = data as byte[]; if (bytes != null) return bytes.Length >= 1 && bytes.Take(4).All(x => x == 0);
            if (data is int) return (int)data == 0; if (data is uint) return (uint)data == 0;
            return false;
        }
        internal static bool IsPasswordManager(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return false; string n = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
            return PasswordManagers.Any(p => n.Contains(p));
        }

        internal bool AddText(string text, DateTime now)
        {
            if (Paused || string.IsNullOrWhiteSpace(text) || text.Length > MaxChars) return false;
            string hash = Sha1(Encoding.UTF8.GetBytes(text));
            var existing = Items.FirstOrDefault(i => i.Hash == hash && i.Kind == "text");
            if (existing != null) { Items.Remove(existing); existing.Time = now.ToString("s", CultureInfo.InvariantCulture); Items.Insert(0, existing); return true; }
            Items.Insert(0, new ClipEntry { Kind = "text", Text = text, Hash = hash, Time = now.ToString("s", CultureInfo.InvariantCulture) });
            Trim(); return true;
        }
        internal bool AddImage(byte[] png, DateTime now)
        {
            if (Paused || png == null || png.Length == 0 || png.Length > MaxImageBytes) return false;
            string hash = Sha1(png);
            var existing = Items.FirstOrDefault(i => i.Hash == hash && i.Kind == "image");
            if (existing != null) { Items.Remove(existing); existing.Time = now.ToString("s", CultureInfo.InvariantCulture); Items.Insert(0, existing); return true; }
            Items.Insert(0, new ClipEntry { Kind = "image", Png = png, Hash = hash, Text = "图片", Time = now.ToString("s", CultureInfo.InvariantCulture) });
            Trim(); return true;
        }
        void Trim()
        {
            while (Items.Count(i => i.Kind == "text" && !i.Pinned) > MaxText) Items.Remove(Items.Last(i => i.Kind == "text" && !i.Pinned));
            while (Items.Count(i => i.Kind == "image" && !i.Pinned) > MaxImages || Items.Where(i => i.Kind == "image").Sum(i => (long)i.Png.Length) > MaxTotalImageBytes)
            { var old = Items.LastOrDefault(i => i.Kind == "image" && !i.Pinned); if (old == null) break; Items.Remove(old); }
        }
        internal void TogglePin(ClipEntry e) { e.Pinned = !e.Pinned; }
        internal void Remove(ClipEntry e) { Items.Remove(e); }
        internal int Clear(bool keepPinned = true) { return Items.RemoveAll(i => !(keepPinned && i.Pinned)); }
        /// <summary>Pinned first, then newest first.</summary>
        internal List<ClipEntry> Ordered() { return Items.Where(i => i.Pinned).Concat(Items.Where(i => !i.Pinned)).ToList(); }
        internal static string Sha1(byte[] data) { using (var h = SHA1.Create()) return BitConverter.ToString(h.ComputeHash(data)).Replace("-", "").ToLowerInvariant(); }
        internal static string Preview(string text, int max = 140) { string t = (text ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim(); while (t.Contains("  ")) t = t.Replace("  ", " "); return t.Length > max ? t.Substring(0, max) + "…" : t; }

        // ---- optional persistence: text only, encrypted for this Windows user (DPAPI); images are never written ----
        internal string Protect()
        {
            var texts = Items.Where(i => i.Kind == "text").Select(i => new ClipEntry { Id = i.Id, Kind = i.Kind, Text = i.Text, Hash = i.Hash, Time = i.Time, Pinned = i.Pinned }).ToList();
            byte[] raw = Encoding.UTF8.GetBytes(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(texts));
            return Convert.ToBase64String(ProtectedData.Protect(raw, Encoding.UTF8.GetBytes("ClearDesk.clipboard"), DataProtectionScope.CurrentUser));
        }
        internal void Unprotect(string blob)
        {
            try
            {
                byte[] raw = ProtectedData.Unprotect(Convert.FromBase64String(blob), Encoding.UTF8.GetBytes("ClearDesk.clipboard"), DataProtectionScope.CurrentUser);
                var list = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<List<ClipEntry>>(Encoding.UTF8.GetString(raw));
                Items.Clear(); if (list != null) foreach (var e in list.Take(MaxText + 20)) if (e != null && !string.IsNullOrEmpty(e.Text)) Items.Add(e);
            }
            catch { }
        }
    }

    public class ClipboardWindow : ToolWidget
    {
        [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr h);
        [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetClipboardOwner();
        const int WM_CLIPBOARDUPDATE = 0x031D;
        readonly ClipboardHistory history = new ClipboardHistory(); ClipboardConfig config;
        readonly StackPanel list = new StackPanel(); readonly TextBlock hint = Label("", 11, Dim);
        IntPtr handle; HwndSource source; DispatcherTimer debounce; Button pauseButton;
        internal Func<DateTime> Now = () => DateTime.Now;
        internal ClipboardHistory History { get { return history; } }
        internal ClipboardConfig Config { get { return config; } }

        public ClipboardWindow() : base("clipboard", "剪贴板历史", SystemParameters.WorkArea.Left + 720, SystemParameters.WorkArea.Top + 360, 320, 360)
        {
            config = ToolStore.Load<ClipboardConfig>("clipboard-config");
            history.Paused = config.Paused;
            if (config.Persist) history.Unprotect(ToolStore.Load<ClipboardSaved>("clipboard-history").Protected);
            Heading.Text = "剪贴板历史";
            pauseButton = IconButton(config.Paused ? "\uE768" : "\uE769", config.Paused ? "继续记录" : "暂停记录", TogglePause);
            AddHeaderButton(IconButton("\uE74D", "清空（保留已固定）", delegate { history.Clear(true); Changed(); }));
            AddHeaderButton(pauseButton);
            var dock = new DockPanel(); DockPanel.SetDock(hint, Dock.Bottom); hint.Margin = new Thickness(0, 6, 0, 0); hint.TextWrapping = TextWrapping.Wrap;
            dock.Children.Add(hint); dock.Children.Add(Scroller(list)); Body.Children.Add(dock);
            ContextMenu.Items.Insert(0, Item("保存历史到磁盘（加密，仅文本）", delegate { config.Persist = !config.Persist; ToolStore.Save("clipboard-config", config); if (!config.Persist) DeleteSaved(); else SaveHistory(); UpdateHint(); }, config.Persist));
            Loaded += delegate
            {
                handle = new WindowInteropHelper(this).Handle; source = HwndSource.FromHwnd(handle);
                if (source != null) { source.AddHook(Hook); AddClipboardFormatListener(handle); }
            };
            debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) }; debounce.Tick += delegate { debounce.Stop(); Capture(0); };
            Rebuild(); UpdateHint();
        }
        protected override void OnWidgetClosed()
        {
            try { if (handle != IntPtr.Zero) RemoveClipboardFormatListener(handle); if (source != null) source.RemoveHook(Hook); } catch { }
            if (debounce != null) debounce.Stop();
        }
        IntPtr Hook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == WM_CLIPBOARDUPDATE && !history.Paused) { debounce.Stop(); debounce.Start(); }
            return IntPtr.Zero;
        }
        void TogglePause() { config.Paused = !config.Paused; history.Paused = config.Paused; ToolStore.Save("clipboard-config", config); ((TextBlock)pauseButton.Content).Text = config.Paused ? "\uE768" : "\uE769"; pauseButton.ToolTip = config.Paused ? "继续记录" : "暂停记录"; UpdateHint(); Toast(config.Paused ? "\uE769" : "\uE768", config.Paused ? "已暂停记录" : "已继续记录"); }
        void UpdateHint() { hint.Text = config.Paused ? "已暂停记录" : config.Persist ? "文本历史已加密保存到本机（图片不保存）。点击条目重新复制。" : "只保存在内存里，退出即清除。点击条目重新复制。"; }

        string OwnerExe()
        {
            try { IntPtr o = GetClipboardOwner(); if (o == IntPtr.Zero) return null; uint pid; Native.GetWindowThreadProcessId(o, out pid); return ProcessTree.ExeOf(pid); } catch { return null; }
        }
        void Capture(int attempt)
        {
            if (history.Paused) return;
            try
            {
                var data = Clipboard.GetDataObject(); if (data == null) return;
                if (ClipboardHistory.ShouldSkip(data.GetFormats(), f => data.GetData(f), OwnerExe())) return;
                bool changed = false;
                if (data.GetDataPresent(DataFormats.UnicodeText)) { string t = data.GetData(DataFormats.UnicodeText) as string; changed = history.AddText(t, Now()); }
                else if (data.GetDataPresent(DataFormats.Bitmap)) { var img = Clipboard.GetImage(); if (img != null) changed = history.AddImage(EncodePng(img), Now()); }
                if (changed) Changed();
            }
            catch (COMException) { if (attempt < 5) { var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) }; t.Tick += delegate { t.Stop(); Capture(attempt + 1); }; t.Start(); } }   // clipboard busy
            catch (Exception e) { Program.Log(e); }
        }
        internal static byte[] EncodePng(BitmapSource img)
        {
            BitmapSource use = img;
            if (img.PixelWidth > 1600) { double f = 1600.0 / img.PixelWidth; use = new TransformedBitmap(img, new ScaleTransform(f, f)); }
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(use)); using (var ms = new MemoryStream()) { enc.Save(ms); return ms.ToArray(); }
        }
        internal void Inject(string text) { if (history.AddText(text, Now())) Changed(); }
        void Changed() { if (config.Persist) SaveHistory(); Rebuild(); }
        void SaveHistory() { ToolStore.Save("clipboard-history", new ClipboardSaved { Protected = history.Protect() }); }
        void DeleteSaved() { try { File.Delete(Path.Combine(ToolStore.Root, "clipboard-history.json")); } catch { } }

        internal void CopyBack(ClipEntry e)
        {
            try
            {
                if (e.Kind == "text") Clipboard.SetText(e.Text);
                else { var bmp = new BitmapImage(); bmp.BeginInit(); bmp.StreamSource = new MemoryStream(e.Png); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.EndInit(); Clipboard.SetImage(bmp); }
                Toast("\uE73E", "已复制，按 Ctrl+V 粘贴");
            }
            catch (Exception ex) { Toast("\uE783", "复制失败：" + ex.Message); }
        }

        void Rebuild()
        {
            list.Children.Clear();
            foreach (var entry in history.Ordered())
            {
                var e = entry;
                var row = new Grid { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 3), Cursor = Cursors.Hand };
                row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                UIElement content;
                if (e.Kind == "image")
                {
                    var bmp = new BitmapImage(); bmp.BeginInit(); bmp.StreamSource = new MemoryStream(e.Png); bmp.DecodePixelWidth = 220; bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.EndInit(); bmp.Freeze();
                    content = new Image { Source = bmp, MaxHeight = 80, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4) };
                }
                else content = new TextBlock { Text = ClipboardHistory.Preview(e.Text), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxHeight = 38, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.White, Margin = new Thickness(6, 5, 6, 5) };
                var card = new Border { Background = e.Pinned ? new SolidColorBrush(Color.FromArgb(60, 0x4C, 0x9B, 0xFF)) : Soft, CornerRadius = new CornerRadius(8), Child = content, ToolTip = e.Kind == "text" ? ClipboardHistory.Preview(e.Text, 600) : "点击重新复制这张图片" };
                var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Opacity = e.Pinned ? 1 : 0 };
                var pin = IconButton(e.Pinned ? "\uE842" : "\uE840", e.Pinned ? "取消固定" : "固定", delegate { history.TogglePin(e); Changed(); }, 11);
                var del = IconButton("\uE711", "删除", delegate { history.Remove(e); Changed(); }, 10);
                tools.Children.Add(pin); tools.Children.Add(del); Grid.SetColumn(tools, 1);
                row.MouseEnter += delegate { tools.Opacity = 1; }; row.MouseLeave += delegate { if (!e.Pinned) tools.Opacity = 0; };
                card.MouseLeftButtonUp += delegate { CopyBack(e); };
                row.Children.Add(card); row.Children.Add(tools); list.Children.Add(row);
            }
            if (list.Children.Count == 0) list.Children.Add(new TextBlock { Text = config.Paused ? "已暂停。" : "复制文字或图片后会出现在这里。", FontSize = 12, Foreground = Dim, Margin = new Thickness(2, 8, 0, 0) });
        }
    }
}
