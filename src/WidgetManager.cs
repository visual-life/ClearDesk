using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClearDesk
{
    // One row in 设置 → 组件管理.
    internal sealed class WidgetEntry
    {
        public string Kind;      // clock / weather / ... / note / box
        public string Id;        // window ExportName (singleton kind, or note/box id)
        public string Name;
        public string Detail;
        public BoxData Data;     // null for a singleton that has never been placed
        public bool Visible;
        public bool Locked { get { return Data != null && Data.Locked; } }
        public string Status { get { return WidgetManager.StatusText(Visible, Locked); } }
    }

    internal static class WidgetManager
    {
        internal static readonly string[] Singletons = { "clock", "weather", "music", "calendar", "almanac", "performance", "quick" };

        internal static string KindName(string kind)
        {
            var k = WidgetCatalog.Kinds.FirstOrDefault(p => p.Key == kind);
            return k.Value ?? kind;
        }

        internal static string StatusText(bool visible, bool locked)
        {
            if (!visible) return "已关闭";
            return locked ? "已锁定" : "显示中";
        }

        internal static bool SingletonOn(string kind)
        {
            var s = Program.S;
            switch (kind)
            {
                case "clock": return s.Clock;
                case "weather": return s.Weather;
                case "music": return s.Music;
                case "calendar": return s.Calendar;
                case "almanac": return s.Almanac;
                case "performance": return s.Performance;
                case "quick": return s.QuickControls;
            }
            return false;
        }

        static void SetSingleton(string kind, bool on)
        {
            var s = Program.S;
            switch (kind)
            {
                case "clock": s.Clock = on; break;
                case "weather": s.Weather = on; break;
                case "music": s.Music = on; break;
                case "calendar": s.Calendar = on; break;
                case "almanac": s.Almanac = on; break;
                case "performance": s.Performance = on; break;
                case "quick": s.QuickControls = on; break;
            }
        }

        internal static string NotePreview(string text)
        {
            var line = (text ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            if (line == null) return "（空白便签）";
            return line.Length > 24 ? line.Substring(0, 24) + "…" : line;
        }

        internal static int BoxCount(BoxData b)
        {
            IEnumerable<string> all = b.Items;
            if (b.Desktop) all = all.Concat(Program.DesktopItems());
            return all.Distinct(StringComparer.OrdinalIgnoreCase).Count(p => !b.Excluded.Contains(p));
        }

        internal static List<WidgetEntry> List()
        {
            var list = new List<WidgetEntry>();
            foreach (var kind in Singletons)
            {
                BoxData data; Program.S.Widgets.TryGetValue(kind, out data);
                list.Add(new WidgetEntry { Kind = kind, Id = kind, Name = KindName(kind), Data = data, Visible = SingletonOn(kind),
                    Detail = data == null ? "尚未放置到桌面" : "位置 " + (int)data.X + ", " + (int)data.Y });
            }
            foreach (var n in Program.S.Notes)
            {
                int chars = (n.Text ?? "").Replace("\r", "").Replace("\n", "").Length;
                list.Add(new WidgetEntry { Kind = "note", Id = n.Id, Name = "便签 · " + NotePreview(n.Text), Data = n, Visible = n.Enabled,
                    Detail = chars == 0 ? "空白" : chars + " 字" });
            }
            foreach (var b in Program.S.Boxes)
            {
                int count = BoxCount(b);
                list.Add(new WidgetEntry { Kind = "box", Id = b.Id, Name = b.Title, Data = b, Visible = b.Enabled,
                    Detail = count + " 个项目" + (b.Desktop ? " · 含桌面项目" : "") });
            }
            return list;
        }

        internal static GlassWindow Window(WidgetEntry e)
        {
            return Program.OpenWindows.FirstOrDefault(w => !w.WasClosed && w.ExportName == e.Id);
        }

        // 显示 / 关闭: closing only hides the widget; everything is kept and can be shown again.
        internal static void SetVisible(WidgetEntry e, bool visible)
        {
            var w = Window(e);
            if (e.Kind == "note" || e.Kind == "box")
            {
                e.Data.Enabled = visible;
                if (visible && e.Kind == "box") { e.Data.Collapsed = false; e.Data.Height = Math.Max(170, e.Data.Height); }
                Program.Save();
                if (visible && w == null) Program.AddWindow(e.Kind == "note" ? (GlassWindow)new NoteWindow(e.Data) : new OrganizerWindow(e.Data));
                if (!visible && w != null) w.Close();
            }
            else if (visible) Program.NewWidget(e.Kind);
            else
            {
                SetSingleton(e.Kind, false);
                if (e.Data != null) e.Data.Enabled = false;
                Program.Save();
                if (w != null) w.Close();
            }
            e.Visible = visible;
        }

        internal static GlassWindow Locate(WidgetEntry e)
        {
            if (!e.Visible) SetVisible(e, true);
            var w = Window(e);
            if (w != null) w.Flash();
            return w;
        }

        internal static string ConfirmText(WidgetEntry e)
        {
            const string tail = "\n\n若只想暂时隐藏，请改用“关闭”，之后可随时重新显示。";
            if (e.Kind == "note")
            {
                string text = e.Data.Text ?? "";
                return "永久删除这张便签？\n\n" + (string.IsNullOrWhiteSpace(text) ? "这张便签是空白的。" :
                    "便签中的全部文字（" + e.Detail + "，开头为“" + NotePreview(text) + "”）将被删除，无法恢复。") + "\n颜色、位置和尺寸也会一并清除。" + tail;
            }
            if (e.Kind == "box")
            {
                int n = BoxCount(e.Data);
                return "永久删除收纳格“" + e.Data.Title + "”？\n\n将移除其中 " + n + " 个项目的引用，以及名称、位置、尺寸、材质与视图设置。\n" +
                    "只会删除引用：原文件和文件夹不会被删除，仍保留在原来的位置" + (e.Data.Desktop ? "（桌面上的文件也不受影响）" : "") + "。" + tail;
            }
            return "删除“" + e.Name + "”组件？\n\n将从桌面移除它，并清除它保存的位置、尺寸、材质和锁定设置。\n以后可通过“新建组件”重新添加（恢复为默认位置）。" + tail;
        }

        // 删除: permanently removes the instance and its saved data. Never touches real files.
        internal static bool Delete(WidgetEntry e, Func<string, bool> confirm)
        {
            if (confirm != null && !confirm(ConfirmText(e))) return false;
            var w = Window(e);
            if (e.Kind == "note")
            {
                var note = w as NoteWindow;
                if (note != null) note.Delete(false);
                else { Program.S.Notes.Remove(e.Data); Program.Save(); }
            }
            else if (e.Kind == "box")
            {
                Program.S.Boxes.Remove(e.Data);
                Program.Save();
                if (w != null) w.Close();
            }
            else
            {
                SetSingleton(e.Kind, false);
                Program.S.Widgets.Remove(e.Kind);
                Program.Save();
                if (w != null) w.Close();
            }
            e.Visible = false;
            return true;
        }

        internal static bool Rename(WidgetEntry e, string name, bool save)
        {
            if (e.Kind != "box" || string.IsNullOrWhiteSpace(name)) return false;
            var w = Window(e) as OrganizerWindow;
            if (w != null) w.SetTitle(name.Trim());
            else e.Data.Title = name.Trim();
            e.Name = e.Data.Title;
            if (save) Program.Save();
            return true;
        }
    }
}
