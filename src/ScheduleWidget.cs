using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ClearDesk
{
    public class ScheduleEvent { public string Id = Guid.NewGuid().ToString("N"), Title = "", Start = "", End = "", Place = "", Source = "local"; public bool AllDay; }
    public class ScheduleData { public List<ScheduleEvent> Events = new List<ScheduleEvent>(); public string IcsFile = ""; }

    internal static class ScheduleLogic
    {
        internal const string F = "yyyy-MM-dd HH:mm";
        internal static DateTime? StartOf(ScheduleEvent e) { DateTime d; return DateTime.TryParseExact(e.Start ?? "", F, CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? (DateTime?)d : null; }
        internal static DateTime? EndOf(ScheduleEvent e) { DateTime d; return DateTime.TryParseExact(e.End ?? "", F, CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? (DateTime?)d : null; }

        /// <summary>"明天 14:00 项目评审" · "10/28 9:30 面试" · "18:00 跑步" · "后天 生日聚会" (no time = all day). The title may not be empty.</summary>
        internal static bool ParseQuick(string text, DateTime now, out DateTime start, out string title, out bool allDay)
        {
            start = default(DateTime); title = ""; allDay = true;
            string t = (text ?? "").Trim().Replace('：', ':'); if (t.Length == 0) return false;
            var m = Regex.Match(t, @"^(今天|明天|后天|\d{4}[-/]\d{1,2}[-/]\d{1,2}|\d{1,2}[/-]\d{1,2})?\s*(\d{1,2}:\d{2})?\s*(.*)$");
            if (!m.Success) return false;
            string day = m.Groups[1].Value, time = m.Groups[2].Value; title = m.Groups[3].Value.Trim();
            if (title.Length == 0) return false;
            DateTime date;
            if (day.Length == 0) date = now.Date;
            else
            {
                var dt = TodoLogic.TryParseDue(day, now, out date) ? date.Date : default(DateTime); if (dt == default(DateTime)) return false; date = dt;
                if (day != "今天" && day != "明天" && day != "后天" && Regex.IsMatch(day, @"^\d{1,2}[/-]\d{1,2}$") == false && Regex.IsMatch(day, @"^\d{4}") == false) return false;
            }
            if (time.Length > 0)
            {
                var tm = Regex.Match(time, @"^(\d{1,2}):(\d{2})$"); int h = int.Parse(tm.Groups[1].Value), mi = int.Parse(tm.Groups[2].Value); if (h > 23 || mi > 59) return false;
                allDay = false; start = date.AddHours(h).AddMinutes(mi);
            }
            else { allDay = true; start = date; }
            return true;
        }
        internal static string FormatQuick(ScheduleEvent e, DateTime now)
        {
            var s = StartOf(e); if (!s.HasValue) return e.Title;
            string day = s.Value.Date == now.Date ? "今天" : s.Value.Date == now.Date.AddDays(1) ? "明天" : s.Value.ToString("yyyy-M-d", CultureInfo.InvariantCulture);
            return day + " " + (e.AllDay ? "" : s.Value.ToString("HH:mm", CultureInfo.InvariantCulture) + " ") + e.Title;
        }
        /// <summary>Events touching the given day (multi-day events too): all-day first, then by start time.</summary>
        internal static List<ScheduleEvent> On(IEnumerable<ScheduleEvent> events, DateTime day)
        {
            return events.Where(e => { var s = StartOf(e); if (!s.HasValue) return false; var en = EndOf(e) ?? s.Value; if (e.AllDay) en = en.Date; return s.Value.Date <= day.Date && en.Date >= day.Date; })
                .OrderBy(e => e.AllDay ? 0 : 1).ThenBy(e => StartOf(e).Value).ThenBy(e => e.Title).ToList();
        }
        internal static string TimeText(ScheduleEvent e, DateTime day)
        {
            if (e.AllDay) return "全天"; var s = StartOf(e).Value; var en = EndOf(e);
            string a = s.Date < day.Date ? "00:00" : s.ToString("HH:mm", CultureInfo.InvariantCulture);
            return en.HasValue && en.Value.Date == s.Date && en.Value > s ? a + "–" + en.Value.ToString("HH:mm", CultureInfo.InvariantCulture) : a;
        }

        // ---- .ics import (VEVENT: SUMMARY, DTSTART, DTEND, LOCATION; recurrence rules are not expanded) ----
        internal static List<ScheduleEvent> ParseIcs(string text, TimeZoneInfo local)
        {
            var result = new List<ScheduleEvent>(); if (string.IsNullOrEmpty(text)) return result;
            var lines = new List<string>();
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')) { if ((raw.StartsWith(" ") || raw.StartsWith("\t")) && lines.Count > 0) lines[lines.Count - 1] += raw.Substring(1); else lines.Add(raw); }
            Dictionary<string, string> cur = null;
            foreach (var line in lines)
            {
                if (line == "BEGIN:VEVENT") { cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); continue; }
                if (line == "END:VEVENT") { if (cur != null) { var e = FromIcs(cur, local); if (e != null) result.Add(e); } cur = null; continue; }
                if (cur == null) continue;
                int colon = line.IndexOf(':'); if (colon <= 0) continue;
                string name = line.Substring(0, colon), value = line.Substring(colon + 1); string key = name.Split(';')[0];
                if (key == "DTSTART" || key == "DTEND") { cur[key] = value; cur[key + "_P"] = name; }
                else if (key == "SUMMARY" || key == "LOCATION" || key == "RRULE") cur[key] = Unescape(value);
            }
            return result;
        }
        static string Unescape(string v) { return v.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\"); }
        static DateTime? IcsTime(string value, string param, TimeZoneInfo local, out bool dateOnly)
        {
            dateOnly = false; DateTime d;
            if (Regex.IsMatch(value, @"^\d{8}$")) { dateOnly = true; return DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? (DateTime?)d : null; }
            if (value.EndsWith("Z") && DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d)) return TimeZoneInfo.ConvertTimeFromUtc(d, local);
            return DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? (DateTime?)d : null;   // floating or TZID: taken as local time
        }
        static ScheduleEvent FromIcs(Dictionary<string, string> p, TimeZoneInfo local)
        {
            string v; if (!p.TryGetValue("DTSTART", out v)) return null; bool dateOnly, endDateOnly; string param; p.TryGetValue("DTSTART_P", out param);
            var s = IcsTime(v, param, local, out dateOnly); if (!s.HasValue) return null;
            var e = new ScheduleEvent { Title = p.ContainsKey("SUMMARY") && p["SUMMARY"].Length > 0 ? p["SUMMARY"] : "（无标题）", Place = p.ContainsKey("LOCATION") ? p["LOCATION"] : "", AllDay = dateOnly, Source = "ics", Start = s.Value.ToString(F, CultureInfo.InvariantCulture) };
            string ev; if (p.TryGetValue("DTEND", out ev)) { var en = IcsTime(ev, null, local, out endDateOnly); if (en.HasValue) { if (dateOnly) en = en.Value.AddDays(-1); e.End = en.Value.ToString(F, CultureInfo.InvariantCulture); } }
            if (e.Title.Length > 80) e.Title = e.Title.Substring(0, 80);
            return e;
        }
        internal static int ReplaceIcs(ScheduleData data, List<ScheduleEvent> imported) { data.Events.RemoveAll(e => e.Source == "ics"); data.Events.AddRange(imported.Take(2000)); return Math.Min(2000, imported.Count); }
        /// <summary>Drops local events that ended more than 30 days ago so the file does not grow forever.</summary>
        internal static int Prune(ScheduleData data, DateTime now) { return data.Events.RemoveAll(e => { var s = StartOf(e); if (!s.HasValue) return true; var en = EndOf(e) ?? s.Value; return en.Date < now.Date.AddDays(-30); }); }
    }

    public class ScheduleWindow : ToolWidget
    {
        ScheduleData data; readonly StackPanel list = new StackPanel(); readonly TextBox input; readonly TextBlock hint = Label("", 11, new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0xA8)));
        string editing;
        internal Func<DateTime> Now = () => DateTime.Now;
        internal ScheduleData Data { get { return data; } }

        public ScheduleWindow() : base("schedule", "日程", SystemParameters.WorkArea.Left + 380, SystemParameters.WorkArea.Top + 440 + 270, 320, 330)
        {
            data = ToolStore.Load<ScheduleData>("schedule"); ScheduleLogic.Prune(data, Now());
            Heading.Text = "日程";
            AddHeaderButton(IconButton("\uE8E5", "导入 .ics 日历文件", ImportIcs));
            input = Input("添加日程，如：明天 14:00 项目评审");
            input.KeyDown += (s, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } else if (e.Key == Key.Escape) { editing = null; input.Text = ""; hint.Text = ""; } };
            var add = IconButton("\uE710", "添加", delegate { Commit(); });
            var row = new DockPanel(); DockPanel.SetDock(add, Dock.Right); row.Children.Add(add); row.Children.Add(input);
            var dock = new DockPanel(); DockPanel.SetDock(row, Dock.Bottom); DockPanel.SetDock(hint, Dock.Bottom); hint.Margin = new Thickness(2, 4, 0, 0);
            dock.Children.Add(row); dock.Children.Add(hint); dock.Children.Add(Scroller(list)); Body.Children.Add(dock);
            Tick(60000, Rebuild);   // runs once now, then every minute while visible
            ContextMenu.Items.Insert(0, Item("说明：只读本地日程与 .ics 文件", delegate { Toast("\uE946", "Windows 日历没有免密钥的本地读取接口，请导入 .ics"); }));
        }
        void Save() { ToolStore.Save("schedule", data); }
        internal bool Commit()
        {
            DateTime start; string title; bool allDay;
            if (!ScheduleLogic.ParseQuick(input.Text, Now(), out start, out title, out allDay)) { hint.Text = "格式：[今天|明天|10/28] [14:00] 标题，没写时间就是全天"; return false; }
            if (editing != null) { var e = data.Events.FirstOrDefault(x => x.Id == editing); if (e != null) { e.Title = title; e.AllDay = allDay; e.Start = start.ToString(ScheduleLogic.F, CultureInfo.InvariantCulture); e.End = ""; e.Source = "local"; } editing = null; }
            else data.Events.Add(new ScheduleEvent { Title = title.Length > 80 ? title.Substring(0, 80) : title, AllDay = allDay, Start = start.ToString(ScheduleLogic.F, CultureInfo.InvariantCulture) });
            input.Text = ""; hint.Text = ""; Save(); Rebuild(); return true;
        }
        void ImportIcs()
        {
            var d = new Microsoft.Win32.OpenFileDialog { Filter = "日历文件|*.ics", Title = "导入 .ics 日历" }; if (d.ShowDialog() != true) return;
            try
            {
                var events = ScheduleLogic.ParseIcs(File.ReadAllText(d.FileName, Encoding.UTF8), TimeZoneInfo.Local); int n = ScheduleLogic.ReplaceIcs(data, events); data.IcsFile = d.FileName; Save(); Rebuild(); Toast("\uE73E", "已导入 " + n + " 个日程");
            }
            catch (Exception e) { Toast("\uE783", "导入失败：" + e.Message); }
        }
        void Rebuild()
        {
            list.Children.Clear(); var now = Now();
            foreach (var pair in new[] { new KeyValuePair<string, DateTime>("今天", now.Date), new KeyValuePair<string, DateTime>("明天", now.Date.AddDays(1)) })
            {
                var day = pair.Value; var items = ScheduleLogic.On(data.Events, day);
                var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 10, 0, 4) };
                head.Children.Add(Label(pair.Key, 13)); ((TextBlock)head.Children[0]).FontWeight = FontWeights.SemiBold;
                head.Children.Add(new TextBlock { Text = "  " + day.ToString("M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN")), FontSize = 11, Foreground = Dim, VerticalAlignment = VerticalAlignment.Bottom });
                list.Children.Add(head);
                if (items.Count == 0) list.Children.Add(new TextBlock { Text = "没有日程", FontSize = 11.5, Foreground = Dim, Margin = new Thickness(2, 0, 0, 0) });
                foreach (var it in items)
                {
                    var e = it; var s = ScheduleLogic.StartOf(e).Value; var en = ScheduleLogic.EndOf(e) ?? s;
                    bool past = !e.AllDay && day.Date == now.Date && en < now && (ScheduleLogic.EndOf(e).HasValue || s < now.AddMinutes(-30));
                    var g = new Grid { Margin = new Thickness(0, 0, 0, 3), Background = Brushes.Transparent, Cursor = Cursors.Hand };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) }); g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var time = new TextBlock { Text = ScheduleLogic.TimeText(e, day), FontSize = 12, Foreground = past ? Dim : new SolidColorBrush(Color.FromRgb(0x8F, 0xC4, 0xFF)), Margin = new Thickness(2, 1, 0, 0) };
                    var title = new StackPanel(); title.Children.Add(new TextBlock { Text = e.Title, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = past ? Dim : Brushes.White });
                    if (!string.IsNullOrEmpty(e.Place)) title.Children.Add(new TextBlock { Text = e.Place, FontSize = 10.5, Foreground = Dim, TextTrimming = TextTrimming.CharacterEllipsis });
                    var del = IconButton("\uE711", "删除", delegate { data.Events.Remove(e); Save(); Rebuild(); }, 10); del.Opacity = 0;
                    g.MouseEnter += delegate { del.Opacity = 1; }; g.MouseLeave += delegate { del.Opacity = 0; };
                    g.MouseLeftButtonUp += delegate { if (e.Source != "ics") { editing = e.Id; input.Text = ScheduleLogic.FormatQuick(e, Now()); input.Focus(); input.CaretIndex = input.Text.Length; hint.Text = "正在编辑，回车保存，Esc 取消"; } };
                    Grid.SetColumn(title, 1); Grid.SetColumn(del, 2); g.Children.Add(time); g.Children.Add(title); g.Children.Add(del); list.Children.Add(g);
                }
            }
        }
    }
}
