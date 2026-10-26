using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClearDesk
{
    public class TodoItem
    {
        public string Id = Guid.NewGuid().ToString("N"), Text = "", Due = "", Created = "", DoneAt = "";
        public bool Done, Notified;
    }
    public class TodoData { public List<TodoItem> Items = new List<TodoItem>(); public bool HideDone; }

    /// <summary>To-do list logic (no UI): due-time parsing, ordering, which reminders are due.</summary>
    internal static class TodoLogic
    {
        internal const string DueFormat = "yyyy-MM-dd HH:mm";
        internal static DateTime? ParseDueValue(string due)
        {
            DateTime d; return DateTime.TryParseExact(due ?? "", DueFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? (DateTime?)d : null;
        }

        /// <summary>Understands: 18:30 · 今天 18:30 · 明天 9:00 · 后天 · 10/28 · 10/28 9:00 · 2026-10-28 09:00 · 30m / 2h / 45分钟后 / 2小时后. Date-only means 09:00. A bare time that has already passed means tomorrow.</summary>
        internal static bool TryParseDue(string text, DateTime now, out DateTime due)
        {
            due = default(DateTime);
            string t = (text ?? "").Trim().Replace('：', ':').Replace('／', '/').Replace('－', '-');
            if (t.Length == 0) return false;
            var rel = Regex.Match(t, @"^\+?(\d{1,4})\s*(m|min|分钟|分|h|hr|小时|时)\s*(后)?$", RegexOptions.IgnoreCase);
            if (rel.Success)
            {
                int n = int.Parse(rel.Groups[1].Value, CultureInfo.InvariantCulture); string u = rel.Groups[2].Value.ToLowerInvariant();
                due = TruncateMinute(now.AddMinutes(u.StartsWith("h") || u == "小时" || u == "时" ? n * 60 : n)); return true;
            }
            int addDays = 0; bool dayWord = false;
            foreach (var w in new[] { new KeyValuePair<string, int>("今天", 0), new KeyValuePair<string, int>("明天", 1), new KeyValuePair<string, int>("后天", 2) })
                if (t.StartsWith(w.Key)) { addDays = w.Value; dayWord = true; t = t.Substring(w.Key.Length).Trim(); break; }
            DateTime date = now.Date.AddDays(addDays); bool hasDate = dayWord;
            var full = Regex.Match(t, @"^(\d{4})[-/](\d{1,2})[-/](\d{1,2})\s*(.*)$");
            var md = Regex.Match(t, @"^(\d{1,2})[/-](\d{1,2})\s*(.*)$");
            if (full.Success)
            {
                int y = int.Parse(full.Groups[1].Value), m = int.Parse(full.Groups[2].Value), d = int.Parse(full.Groups[3].Value);
                if (m < 1 || m > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return false;
                date = new DateTime(y, m, d); hasDate = true; t = full.Groups[4].Value.Trim();
            }
            else if (md.Success)
            {
                int m = int.Parse(md.Groups[1].Value), d = int.Parse(md.Groups[2].Value);
                if (m < 1 || m > 12 || d < 1 || d > DateTime.DaysInMonth(now.Year, m)) return false;
                date = new DateTime(now.Year, m, d); if (date < now.Date) date = date.AddYears(1); hasDate = true; t = md.Groups[3].Value.Trim();
            }
            int hour = 9, minute = 0;
            if (t.Length > 0)
            {
                var tm = Regex.Match(t, @"^(\d{1,2})(?::(\d{2}))?$");
                if (!tm.Success) return false;
                hour = int.Parse(tm.Groups[1].Value); minute = tm.Groups[2].Success ? int.Parse(tm.Groups[2].Value) : 0;
                if (hour > 23 || minute > 59) return false;
                if (!hasDate) { date = now.Date; if (date.AddHours(hour).AddMinutes(minute) <= now) date = date.AddDays(1); }
            }
            else if (!hasDate) return false;
            due = date.AddHours(hour).AddMinutes(minute);
            return true;
        }
        static DateTime TruncateMinute(DateTime d) { return new DateTime(d.Year, d.Month, d.Day, d.Hour, d.Minute, 0); }

        internal static string FormatDue(DateTime due, DateTime now)
        {
            string time = due.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (due.Date == now.Date) return "今天 " + time;
            if (due.Date == now.Date.AddDays(1)) return "明天 " + time;
            if (due.Date == now.Date.AddDays(-1)) return "昨天 " + time;
            return due.Year == now.Year ? due.ToString("M月d日 ", CultureInfo.InvariantCulture) + time : due.ToString("yyyy年M月d日 ", CultureInfo.InvariantCulture) + time;
        }

        internal static TodoItem Add(TodoData data, string text, DateTime? due, DateTime now)
        {
            text = (text ?? "").Trim(); if (text.Length == 0) return null;
            if (text.Length > 200) text = text.Substring(0, 200);
            var item = new TodoItem { Text = text, Due = due.HasValue ? due.Value.ToString(DueFormat, CultureInfo.InvariantCulture) : "", Created = now.ToString("s", CultureInfo.InvariantCulture) };
            data.Items.Add(item);
            return item;
        }
        internal static void SetDone(TodoItem item, bool done, DateTime now) { item.Done = done; item.DoneAt = done ? now.ToString("s", CultureInfo.InvariantCulture) : ""; if (!done) item.Notified = false; }
        internal static bool Move(List<TodoItem> items, int index, int delta)
        {
            int to = index + delta; if (index < 0 || index >= items.Count || to < 0 || to >= items.Count) return false;
            var x = items[index]; items.RemoveAt(index); items.Insert(to, x); return true;
        }
        /// <summary>Items whose reminder time has come and that were not announced yet.</summary>
        internal static List<TodoItem> DueNow(IEnumerable<TodoItem> items, DateTime now)
        {
            return items.Where(i => !i.Done && !i.Notified && ParseDueValue(i.Due).HasValue && ParseDueValue(i.Due).Value <= now).ToList();
        }
        internal static int ClearDone(TodoData data) { return data.Items.RemoveAll(i => i.Done); }
        internal static int OpenCount(TodoData data) { return data.Items.Count(i => !i.Done); }
    }

    public class TodoWindow : ToolWidget
    {
        TodoData data;
        readonly StackPanel list = new StackPanel();
        readonly TextBox input, dueBox;
        readonly TextBlock count = Label("", 11, Dim), hint = Label("", 11, new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0xA8)));
        readonly DispatcherTimer reminder = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        Button hideDone;
        internal Func<DateTime> Now = () => DateTime.Now;
        internal List<string> Reminders = new List<string>();      // shown reminders (tests read this)
        internal static Action<string, string> Notify = (title, text) => Program.Balloon(title, text);
        bool narrow;

        public TodoWindow() : base("todo", "待办清单", SystemParameters.WorkArea.Left + 40, SystemParameters.WorkArea.Top + 80, 320, 380)
        {
            data = ToolStore.Load<TodoData>("todo");
            Heading.Text = "待办清单"; Header.Margin = new Thickness(0, 0, 0, 10);
            hideDone = IconButton("\uE7B3", "隐藏 / 显示已完成", delegate { data.HideDone = !data.HideDone; Save(); Rebuild(); });
            AddHeaderButton(IconButton("\uE74D", "清除已完成", delegate { if (TodoLogic.ClearDone(data) > 0) { Save(); Rebuild(); } }));
            AddHeaderButton(hideDone);
            Header.Children.Add(count); count.Margin = new Thickness(8, 2, 0, 0); count.VerticalAlignment = VerticalAlignment.Center;

            input = Input("添加待办，按回车"); dueBox = Input("提醒时间：18:30 / 明天 9:00 / 30m", 12); dueBox.Width = 110; dueBox.Margin = new Thickness(6, 0, 0, 0);
            var add = IconButton("\uE710", "添加", delegate { AddFromInputs(); }); add.Margin = new Thickness(4, 0, 0, 0);
            input.KeyDown += (s, e) => { if (e.Key == Key.Enter) { AddFromInputs(); e.Handled = true; } };
            dueBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) { AddFromInputs(); e.Handled = true; } };
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(dueBox, 1); Grid.SetColumn(add, 2); row.Children.Add(input); row.Children.Add(dueBox); row.Children.Add(add);
            var dock = new DockPanel();
            DockPanel.SetDock(row, Dock.Top); DockPanel.SetDock(hint, Dock.Top); hint.Margin = new Thickness(2, 4, 0, 4);
            dock.Children.Add(row); dock.Children.Add(hint); dock.Children.Add(Scroller(list));
            Body.Children.Add(dock);
            Rebuild();
            reminder.Tick += delegate { CheckReminders(); };
            reminder.Start();
            CheckReminders();
        }
        protected override void OnWidgetClosed() { reminder.Stop(); }
        protected override void OnResponsive(double extraW, double extraH) { bool n = ExtraWidth < 1 && DefaultWidth < 260; if (n != narrow) { narrow = n; } }

        void Save() { ToolStore.Save("todo", data); }

        internal bool AddFromInputs()
        {
            DateTime now = Now(), due = default(DateTime); bool hasDue = false;
            if (dueBox.Text.Trim().Length > 0)
            {
                if (!TodoLogic.TryParseDue(dueBox.Text, now, out due)) { hint.Text = "看不懂这个时间：试试 18:30、明天 9:00、10/28 或 30m"; return false; }
                hasDue = true;
            }
            var item = TodoLogic.Add(data, input.Text, hasDue ? (DateTime?)due : null, now);
            if (item == null) return false;
            input.Text = ""; dueBox.Text = ""; hint.Text = "";
            Save(); Rebuild(); return true;
        }

        internal void CheckReminders()
        {
            var due = TodoLogic.DueNow(data.Items, Now());
            if (due.Count == 0) return;
            foreach (var i in due) i.Notified = true;
            Save();
            string text = due.Count == 1 ? due[0].Text : "有 " + due.Count + " 项待办到期：" + string.Join("、", due.Take(3).Select(d => d.Text)) + (due.Count > 3 ? "…" : "");
            Reminders.Add(text);
            try { Notify("待办提醒", text); Toast("\uE7E7", "待办到期：" + (due.Count == 1 ? due[0].Text : due.Count + " 项")); } catch { }
            Rebuild();
        }

        internal TodoData Data { get { return data; } }
        internal void Reload() { data = ToolStore.Load<TodoData>("todo"); Rebuild(); }

        void Rebuild()
        {
            list.Children.Clear();
            DateTime now = Now();
            for (int index = 0; index < data.Items.Count; index++)
            {
                var item = data.Items[index]; int idx = index;
                if (item.Done && data.HideDone) continue;
                var due = TodoLogic.ParseDueValue(item.Due);
                var row = new Grid { Margin = new Thickness(0, 0, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var check = IconButton(item.Done ? "\uE930" : "\uEA3A", item.Done ? "标记为未完成" : "完成", delegate { TodoLogic.SetDone(item, !item.Done, Now()); Save(); Rebuild(); }, 15);
                check.Foreground = item.Done ? Accent : Brushes.White; ((TextBlock)check.Content).Foreground = item.Done ? Accent : Brushes.White;
                var text = new TextBlock { Text = item.Text, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = item.Done ? Dim : Brushes.White, Margin = new Thickness(2, 3, 6, 3) };
                if (item.Done) text.TextDecorations = TextDecorations.Strikethrough;
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                stack.Children.Add(text);
                if (due.HasValue)
                {
                    bool late = !item.Done && due.Value <= now;
                    stack.Children.Add(new TextBlock { Text = (late ? "已到期 · " : "") + TodoLogic.FormatDue(due.Value, now), FontSize = 10.5, Margin = new Thickness(2, -2, 0, 3), Foreground = late ? new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x8A)) : Dim });
                }
                Grid.SetColumn(stack, 1); Grid.SetColumn(check, 0);
                var tools = new StackPanel { Orientation = Orientation.Horizontal, Opacity = .0, VerticalAlignment = VerticalAlignment.Center };
                tools.Children.Add(IconButton("\uE70E", "上移", delegate { if (TodoLogic.Move(data.Items, idx, -1)) { Save(); Rebuild(); } }, 10));
                tools.Children.Add(IconButton("\uE70D", "下移", delegate { if (TodoLogic.Move(data.Items, idx, 1)) { Save(); Rebuild(); } }, 10));
                Grid.SetColumn(tools, 2);
                var del = IconButton("\uE711", "删除", delegate { data.Items.Remove(item); Save(); Rebuild(); }, 10); Grid.SetColumn(del, 3); del.Opacity = 0;
                row.MouseEnter += delegate { tools.Opacity = 1; del.Opacity = 1; }; row.MouseLeave += delegate { tools.Opacity = 0; del.Opacity = 0; };
                row.Background = Brushes.Transparent;
                row.Children.Add(check); row.Children.Add(stack); row.Children.Add(tools); row.Children.Add(del);
                list.Children.Add(row);
            }
            if (list.Children.Count == 0) list.Children.Add(new TextBlock { Text = data.Items.Count == 0 ? "还没有待办。输入内容，按回车添加。" : "已完成的事项已隐藏。", FontSize = 12, Foreground = Dim, Margin = new Thickness(2, 8, 0, 0) });
            count.Text = TodoLogic.OpenCount(data) + " 项未完成";
        }
    }
}
