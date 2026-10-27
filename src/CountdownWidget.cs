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
    public class CountdownEvent { public string Id = Guid.NewGuid().ToString("N"), Name = "", Date = ""; public bool Yearly; }
    public class CountdownData
    {
        public List<CountdownEvent> Events = new List<CountdownEvent>();
        public int WorkMinutes = 25, ShortMinutes = 5, LongMinutes = 15, LongEvery = 4;
        public int CompletedToday; public string CompletedDate = ""; public bool PomodoroTab;
    }

    internal static class CountdownLogic
    {
        internal static DateTime? ParseDate(string text, DateTime today)
        {
            string t = (text ?? "").Trim().Replace('／', '/').Replace('－', '-').Replace('年', '-').Replace('月', '-').Replace('日', ' ').Trim();
            if (t.Length == 0) return null;
            var after = Regex.Match(t, @"^(\d{1,4})\s*天后$"); if (after.Success) return today.Date.AddDays(int.Parse(after.Groups[1].Value));
            if (t == "今天") return today.Date; if (t == "明天") return today.Date.AddDays(1); if (t == "后天") return today.Date.AddDays(2);
            var full = Regex.Match(t, @"^(\d{4})[-/](\d{1,2})[-/](\d{1,2})$");
            int y, m, d;
            if (full.Success) { y = int.Parse(full.Groups[1].Value); m = int.Parse(full.Groups[2].Value); d = int.Parse(full.Groups[3].Value); }
            else
            {
                var md = Regex.Match(t, @"^(\d{1,2})[-/](\d{1,2})$"); if (!md.Success) return null;
                m = int.Parse(md.Groups[1].Value); d = int.Parse(md.Groups[2].Value); y = today.Year;
                if (m >= 1 && m <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m) && new DateTime(y, m, d) < today.Date) y++;
            }
            if (y < 1900 || y > 2200 || m < 1 || m > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return null;
            return new DateTime(y, m, d);
        }
        internal static DateTime? EventDate(CountdownEvent e) { DateTime d; return DateTime.TryParseExact(e.Date ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? (DateTime?)d : null; }
        /// <summary>Days from today to the event (negative = already past). A yearly event (birthday) counts to its next occurrence; 29 Feb falls on 28 Feb in other years.</summary>
        internal static int? DaysLeft(CountdownEvent e, DateTime today)
        {
            var d = EventDate(e); if (!d.HasValue) return null;
            var date = d.Value;
            if (e.Yearly)
            {
                Func<int, DateTime> inYear = y => new DateTime(y, date.Month, Math.Min(date.Day, DateTime.DaysInMonth(y, date.Month)));
                date = inYear(today.Year); if (date < today.Date) date = inYear(today.Year + 1);
            }
            return (int)(date.Date - today.Date).TotalDays;
        }
        internal static string DaysText(int days) { return days == 0 ? "今天" : days > 0 ? days + " 天" : "已过 " + (-days) + " 天"; }
        /// <summary>Upcoming first (soonest first), past events last (most recent first).</summary>
        internal static List<CountdownEvent> Sorted(IEnumerable<CountdownEvent> events, DateTime today)
        {
            return events.Where(e => DaysLeft(e, today).HasValue).OrderBy(e => DaysLeft(e, today).Value < 0 ? 1 : 0).ThenBy(e => Math.Abs(DaysLeft(e, today).Value)).ToList();
        }
        internal static bool TryParseDurations(string text, out int work, out int rest, out int longRest)
        {
            work = rest = longRest = 0; var m = Regex.Match((text ?? "").Trim(), @"^(\d{1,3})\s*[/,，]\s*(\d{1,3})(?:\s*[/,，]\s*(\d{1,3}))?$"); if (!m.Success) return false;
            work = int.Parse(m.Groups[1].Value); rest = int.Parse(m.Groups[2].Value); longRest = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : Math.Max(rest * 3, rest);
            return work >= 1 && work <= 180 && rest >= 1 && rest <= 60 && longRest >= 1 && longRest <= 120;
        }
    }

    /// <summary>Pomodoro settings (2.2.1): ranges, presets and validation.</summary>
    internal static class PomodoroSettings
    {
        internal static readonly int[] Min = { 1, 1, 1, 2 }, Max = { 180, 60, 120, 12 };       // focus, short break, long break (minutes), rounds before a long break
        internal static readonly string[] Names = { "专注", "短休息", "长休息", "几轮后长休息" };
        internal sealed class Preset { public string Name; public int Work, Short, Long, Rounds; }
        internal static readonly Preset[] Presets =
        {
            new Preset { Name = "经典 25/5/15", Work = 25, Short = 5, Long = 15, Rounds = 4 },
            new Preset { Name = "50/10/20", Work = 50, Short = 10, Long = 20, Rounds = 4 },
            new Preset { Name = "45/15/30", Work = 45, Short = 15, Long = 30, Rounds = 3 },
            new Preset { Name = "90/20/30", Work = 90, Short = 20, Long = 30, Rounds = 2 },
        };
        internal static int Clamp(int index, int value) { return Math.Max(Min[index], Math.Min(Max[index], value)); }
        internal static string RangeText(int index) { return Min[index] + "–" + Max[index]; }
        /// <summary>Whole number inside the allowed range. Returns false (and a message) for empty, non-numeric or out-of-range text.</summary>
        internal static bool TryParse(int index, string text, out int value, out string error)
        {
            value = 0; error = null;
            if (!int.TryParse((text ?? "").Trim(), System.Globalization.NumberStyles.None, CultureInfo.InvariantCulture, out value)) { error = Names[index] + "请输入 " + RangeText(index) + " 的整数"; return false; }
            if (value < Min[index] || value > Max[index]) { error = Names[index] + "需要在 " + RangeText(index) + (index == 3 ? " 轮" : " 分钟") + "之间"; return false; }
            return true;
        }
        /// <summary>Settings from a saved file: anything out of range falls back to the nearest valid value (a hand-edited file can not break the timer).</summary>
        internal static void Normalize(CountdownData d)
        {
            d.WorkMinutes = d.WorkMinutes <= 0 ? 25 : Clamp(0, d.WorkMinutes); d.ShortMinutes = d.ShortMinutes <= 0 ? 5 : Clamp(1, d.ShortMinutes);
            d.LongMinutes = d.LongMinutes <= 0 ? 15 : Clamp(2, d.LongMinutes); d.LongEvery = d.LongEvery <= 0 ? 4 : Clamp(3, d.LongEvery);
        }
        internal static string Summary(int work, int shortRest, int longRest, int rounds) { return work + " / " + shortRest + " / " + longRest + " 分钟 · 每 " + rounds + " 轮长休息"; }
    }

    /// <summary>Pomodoro state machine driven by an injected clock (testable). After a phase ends the next phase is ready but waits for the user to start it.</summary>
    internal sealed class Pomodoro
    {
        internal enum Phase { Work, ShortBreak, LongBreak }
        internal Phase Current = Phase.Work;
        internal bool Running;
        internal DateTime EndsAt;
        internal TimeSpan Left;
        internal int Completed, WorkMinutes = 25, ShortMinutes = 5, LongMinutes = 15, LongEvery = 4;
        internal event Action<Phase, Phase> PhaseFinished;      // finished phase, next phase

        internal Pomodoro() { Left = TimeSpan.FromMinutes(WorkMinutes); }
        internal TimeSpan Length(Phase p) { return TimeSpan.FromMinutes(p == Phase.Work ? WorkMinutes : p == Phase.ShortBreak ? ShortMinutes : LongMinutes); }
        internal void Configure(int work, int shortRest, int longRest, int longEvery)
        {
            WorkMinutes = work; ShortMinutes = shortRest; LongMinutes = longRest; LongEvery = Math.Max(2, longEvery);
            if (!Running) Left = Length(Current);
        }
        internal void Start(DateTime now) { if (Running) return; if (Left <= TimeSpan.Zero) Left = Length(Current); EndsAt = now + Left; Running = true; }
        internal void Pause(DateTime now) { if (!Running) return; Left = EndsAt - now; if (Left < TimeSpan.Zero) Left = TimeSpan.Zero; Running = false; }
        internal void Reset() { Running = false; Left = Length(Current); }
        internal TimeSpan Remaining(DateTime now) { return Running ? (EndsAt - now > TimeSpan.Zero ? EndsAt - now : TimeSpan.Zero) : Left; }
        internal void Skip() { Finish(false); }
        /// <summary>Call about once a second while running. Returns true when a phase finished.</summary>
        internal bool Tick(DateTime now)
        {
            if (!Running || now < EndsAt) return false;
            Finish(true); return true;
        }
        void Finish(bool counts)
        {
            var done = Current;
            if (done == Phase.Work && counts) Completed++;
            Phase next = done == Phase.Work ? (Completed > 0 && Completed % LongEvery == 0 && counts ? Phase.LongBreak : Phase.ShortBreak) : Phase.Work;
            Current = next; Running = false; Left = Length(next);
            var h = PhaseFinished; if (h != null) h(done, next);
        }
        internal static string Format(TimeSpan t) { int s = (int)Math.Ceiling(Math.Max(0, t.TotalSeconds)); return (s / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (s % 60).ToString("00", CultureInfo.InvariantCulture); }
        internal static string PhaseName(Phase p) { return p == Phase.Work ? "专注" : p == Phase.ShortBreak ? "短休息" : "长休息"; }
    }

    public class CountdownWindow : ToolWidget
    {
        CountdownData data;
        readonly Pomodoro pomo = new Pomodoro();
        readonly StackPanel eventList = new StackPanel();
        readonly Grid countdownView = new Grid(), pomodoroView = new Grid();
        readonly TextBox nameBox, dateBox;
        readonly TextBlock hint = Label("", 11, new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0xA8))), clockText = new TextBlock { FontSize = 46, FontFamily = new FontFamily("Segoe UI Light"), Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
            phaseText = Label("", 16, Dim), doneText = Label("", 14, Dim);
        StackPanel buttons;
        readonly DispatcherTimer second = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        Button tabCount, tabPomo, startButton, summaryButton; bool yearly;
        readonly StackPanel pomoMain = new StackPanel { VerticalAlignment = VerticalAlignment.Center }, pomoSettings = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        readonly TextBox[] stepBoxes = new TextBox[4]; readonly int[] stepValues = new int[4]; readonly TextBlock settingsHint = Label("", 11, new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0xA8)));
        internal Func<DateTime> Now = () => DateTime.Now;
        internal static Action<string, string> Notify = (title, text) => { Program.Balloon(title, text); try { System.Media.SystemSounds.Asterisk.Play(); } catch { } };
        internal Pomodoro Timer2 { get { return pomo; } }
        internal string SummaryText { get { return ((TextBlock)summaryButton.Content).Text; } }
        internal CountdownData Data { get { return data; } }

        public CountdownWindow() : base("countdown", "倒计时 / 番茄钟", SystemParameters.WorkArea.Left + 380, SystemParameters.WorkArea.Top + 80, 320, 320)
        {
            data = ToolStore.Load<CountdownData>(State.Id);
            if (data.CompletedDate != Now().ToString("yyyy-MM-dd")) { data.CompletedToday = 0; data.CompletedDate = Now().ToString("yyyy-MM-dd"); }
            PomodoroSettings.Normalize(data);
            pomo.Completed = data.CompletedToday; pomo.Configure(data.WorkMinutes, data.ShortMinutes, data.LongMinutes, data.LongEvery);
            pomo.PhaseFinished += OnPhaseFinished;
            Heading.Text = "倒计时 / 番茄钟"; Header.Margin = new Thickness(0, 0, 0, 10); Heading.Visibility = Visibility.Collapsed;
            tabPomo = TextButton("番茄钟", () => ShowTab(true)); tabCount = TextButton("倒数日", () => ShowTab(false));
            ((TextBlock)tabPomo.Content).FontSize = 15; ((TextBlock)tabCount.Content).FontSize = 15; tabPomo.Padding = tabCount.Padding = new Thickness(12, 5, 12, 5);
            var tabs = new StackPanel { Orientation = Orientation.Horizontal }; tabs.Children.Add(tabCount); tabs.Children.Add(tabPomo); Header.Children.Add(tabs);

            // 倒数日
            nameBox = Input("名称，如 期末考试"); dateBox = Input("日期：12/26 · 2027-06-07 · 30天后", 12); dateBox.Width = 120; dateBox.Margin = new Thickness(6, 0, 0, 0);
            Button yearlyButton = null;
            yearlyButton = IconButton("\uE7C3", "每年重复（生日）", delegate { yearly = !yearly; ((TextBlock)yearlyButton.Content).Foreground = yearly ? Accent : Brushes.White; }, 12);
            var add = IconButton("\uE710", "添加", delegate { AddEvent(); }); add.Margin = new Thickness(2, 0, 0, 0);
            nameBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) { AddEvent(); e.Handled = true; } };
            dateBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) { AddEvent(); e.Handled = true; } };
            yearlyButton.ToolTip = "每年重复（生日）：点击切换";
            var row = new Grid(); for (int i = 0; i < 4; i++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetColumn(dateBox, 1); Grid.SetColumn(yearlyButton, 2); Grid.SetColumn(add, 3); row.Children.Add(nameBox); row.Children.Add(dateBox); row.Children.Add(yearlyButton); row.Children.Add(add);
            var cd = new DockPanel(); DockPanel.SetDock(row, Dock.Top); DockPanel.SetDock(hint, Dock.Top); hint.Margin = new Thickness(2, 4, 0, 4);
            cd.Children.Add(row); cd.Children.Add(hint); cd.Children.Add(Scroller(eventList)); countdownView.Children.Add(cd);

            // 番茄钟
            startButton = TextButton("开始", ToggleRun, true); startButton.Padding = new Thickness(22, 6, 22, 6);
            var reset = TextButton("重置", delegate { pomo.Reset(); Refresh(); }); var skip = TextButton("跳过", delegate { pomo.Skip(); Refresh(); });
            buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 6) };
            buttons.Children.Add(startButton); buttons.Children.Add(reset); buttons.Children.Add(skip);
            foreach (var pb in new[] { startButton, reset, skip }) ((TextBlock)pb.Content).FontSize = 14;
            summaryButton = TextButton(PomodoroSettings.Summary(pomo.WorkMinutes, pomo.ShortMinutes, pomo.LongMinutes, pomo.LongEvery) + "  \u2699", delegate { ShowPomodoroSettings(true); });
            ((TextBlock)summaryButton.Content).FontSize = 13; summaryButton.HorizontalAlignment = HorizontalAlignment.Center; summaryButton.ToolTip = "自定义专注 / 休息时长";
            phaseText.HorizontalAlignment = HorizontalAlignment.Center; doneText.HorizontalAlignment = HorizontalAlignment.Center;
            pomoMain.Children.Add(phaseText); pomoMain.Children.Add(clockText); pomoMain.Children.Add(buttons); pomoMain.Children.Add(doneText); summaryButton.Margin = new Thickness(0, 8, 0, 0); pomoMain.Children.Add(summaryButton);
            BuildPomodoroSettings();
            pomodoroView.Children.Add(pomoMain); pomodoroView.Children.Add(pomoSettings);
            Body.Children.Add(countdownView); Body.Children.Add(pomodoroView);
            ShowTab(data.PomodoroTab); Refresh();
            second.Tick += delegate { if (pomo.Tick(Now())) Refresh(); else if (pomo.Running) UpdateClock(); };
            Tick(60000, delegate { if (!data.PomodoroTab) RebuildEvents(); });
            ContextMenu.Items.Insert(0, Item("番茄钟设置…", delegate { ShowTab(true); ShowPomodoroSettings(true); }));
            ContextMenu.Items.Insert(1, Item("清空倒数日", delegate { data.Events.Clear(); Save(); RebuildEvents(); }));
        }
        protected override void OnWidgetClosed() { second.Stop(); }
        // 2.2.2: spare height goes into the gaps between the blocks (and the stack is centred) instead of piling up below
        protected override void OnResponsive(double extraW, double extraH)
        {
            if (buttons == null || summaryButton == null) return;
            double h = Math.Max(1, DefaultHeight);
            buttons.Margin = new Thickness(0, WidgetLook.Gap(10, extraH, h), 0, WidgetLook.Gap(6, extraH, h));
            summaryButton.Margin = new Thickness(0, WidgetLook.Gap(8, extraH, h), 0, 0);
            doneText.Margin = new Thickness(0, WidgetLook.Gap(0, extraH, h) + (WidgetLook.Gap(6, extraH, h) - 6), 0, 0);
            pomoMain.VerticalAlignment = extraH > 1 ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        }

        internal void TestShowTab(bool pomodoro) { ShowTab(pomodoro); }
        void Save() { data.CompletedToday = pomo.Completed; data.CompletedDate = Now().ToString("yyyy-MM-dd"); data.WorkMinutes = pomo.WorkMinutes; data.ShortMinutes = pomo.ShortMinutes; data.LongMinutes = pomo.LongMinutes; data.LongEvery = pomo.LongEvery; ToolStore.Save(State.Id, data); }
        void ShowTab(bool pomodoro)
        {
            data.PomodoroTab = pomodoro; countdownView.Visibility = pomodoro ? Visibility.Collapsed : Visibility.Visible; pomodoroView.Visibility = pomodoro ? Visibility.Visible : Visibility.Collapsed;
            ((TextBlock)tabPomo.Content).Foreground = pomodoro ? Brushes.White : Dim; ((TextBlock)tabCount.Content).Foreground = pomodoro ? Dim : Brushes.White;
            ((TextBlock)tabPomo.Content).FontWeight = pomodoro ? FontWeights.SemiBold : FontWeights.Normal; ((TextBlock)tabCount.Content).FontWeight = pomodoro ? FontWeights.Normal : FontWeights.SemiBold;
            ToolStore.Save(State.Id, data);
        }
        internal void ToggleRun() { if (pomo.Running) pomo.Pause(Now()); else pomo.Start(Now()); if (pomo.Running) second.Start(); else second.Stop(); Refresh(); }
        // ---- custom durations (2.2.1): − / + steppers, typed values, presets; saved at once, applied to the next phase while the clock is running ----
        void BuildPomodoroSettings()
        {
            var title = Label("番茄钟设置", 14, Brushes.White); title.FontWeight = FontWeights.SemiBold; title.HorizontalAlignment = HorizontalAlignment.Center; title.Margin = new Thickness(0, 0, 0, 8);
            pomoSettings.Children.Add(title);
            string[] units = { "分钟", "分钟", "分钟", "轮" };
            for (int i = 0; i < 4; i++)
            {
                int index = i; var row = new Grid { Margin = new Thickness(18, 3, 18, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition()); for (int c = 0; c < 4; c++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var name = Label(PomodoroSettings.Names[i], 12.5); name.VerticalAlignment = VerticalAlignment.Center;
                var minus = IconButton("\uE738", "减少", delegate { Step(index, -1); }, 11); var plus = IconButton("\uE710", "增加", delegate { Step(index, 1); }, 11);
                var box = Input(PomodoroSettings.RangeText(i), 13); box.Width = 52; box.TextAlignment = TextAlignment.Center; box.Margin = new Thickness(4, 0, 4, 0); box.MaxLength = 3; stepBoxes[i] = box;
                box.LostFocus += delegate { CommitStep(index); }; box.KeyDown += (s, e) => { if (e.Key == Key.Enter) { CommitStep(index); e.Handled = true; } };
                box.MouseWheel += (s, e) => { Step(index, e.Delta > 0 ? 1 : -1); e.Handled = true; };
                var unit = Label(units[i], 11.5, Dim); unit.Margin = new Thickness(4, 0, 0, 0); unit.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(minus, 1); Grid.SetColumn(box, 2); Grid.SetColumn(plus, 3); Grid.SetColumn(unit, 4);
                row.Children.Add(name); row.Children.Add(minus); row.Children.Add(box); row.Children.Add(plus); row.Children.Add(unit); pomoSettings.Children.Add(row);
            }
            var presets = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(10, 8, 10, 2) };
            foreach (var pr in PomodoroSettings.Presets) { var preset = pr; var b = TextButton(preset.Name, delegate { SetAll(preset.Work, preset.Short, preset.Long, preset.Rounds); }); b.Margin = new Thickness(2); presets.Children.Add(b); }
            settingsHint.HorizontalAlignment = HorizontalAlignment.Center; settingsHint.Margin = new Thickness(0, 4, 0, 4); settingsHint.TextWrapping = TextWrapping.Wrap; settingsHint.TextAlignment = TextAlignment.Center;
            var done = TextButton("完成", delegate { ShowPomodoroSettings(false); }, true); done.HorizontalAlignment = HorizontalAlignment.Center; done.Padding = new Thickness(26, 6, 26, 6);
            pomoSettings.Children.Add(presets); pomoSettings.Children.Add(settingsHint); pomoSettings.Children.Add(done);
        }
        internal void ShowPomodoroSettings(bool show)
        {
            if (show) { stepValues[0] = pomo.WorkMinutes; stepValues[1] = pomo.ShortMinutes; stepValues[2] = pomo.LongMinutes; stepValues[3] = pomo.LongEvery; settingsHint.Text = pomo.Running ? "新的时长从下一个阶段开始生效" : ""; ShowStepValues(); }
            pomoSettings.Visibility = show ? Visibility.Visible : Visibility.Collapsed; pomoMain.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            if (!show) Refresh();
        }
        void ShowStepValues() { for (int i = 0; i < 4; i++) stepBoxes[i].Text = stepValues[i].ToString(CultureInfo.InvariantCulture); }
        internal void Step(int index, int delta) { stepValues[index] = PomodoroSettings.Clamp(index, stepValues[index] + delta); ShowStepValues(); ApplySettings(); }
        internal bool CommitStep(int index)
        {
            int v; string error;
            if (!PomodoroSettings.TryParse(index, stepBoxes[index].Text, out v, out error)) { settingsHint.Text = error; stepBoxes[index].Text = stepValues[index].ToString(CultureInfo.InvariantCulture); return false; }
            stepValues[index] = v; settingsHint.Text = ""; ApplySettings(); return true;
        }
        internal void SetAll(int work, int shortRest, int longRest, int rounds)
        {
            stepValues[0] = PomodoroSettings.Clamp(0, work); stepValues[1] = PomodoroSettings.Clamp(1, shortRest); stepValues[2] = PomodoroSettings.Clamp(2, longRest); stepValues[3] = PomodoroSettings.Clamp(3, rounds);
            ShowStepValues(); ApplySettings();
        }
        void ApplySettings()
        {
            pomo.Configure(stepValues[0], stepValues[1], stepValues[2], stepValues[3]); Save();
            summaryButton.Content = new TextBlock { Text = PomodoroSettings.Summary(pomo.WorkMinutes, pomo.ShortMinutes, pomo.LongMinutes, pomo.LongEvery) + "  \u2699", FontSize = 13, Foreground = Brushes.White };
            UpdateClock();
        }
        void OnPhaseFinished(Pomodoro.Phase done, Pomodoro.Phase next)
        {
            second.Stop(); Save();
            string text = done == Pomodoro.Phase.Work ? "专注结束，已完成 " + pomo.Completed + " 个番茄。休息 " + (int)pomo.Length(next).TotalMinutes + " 分钟吧。" : "休息结束，开始下一个专注。";
            try { Notify("番茄钟", text); Toast("\uE7E7", done == Pomodoro.Phase.Work ? "专注结束" : "休息结束"); } catch { }
        }
        void UpdateClock() { clockText.Text = Pomodoro.Format(pomo.Remaining(Now())); }
        void Refresh()
        {
            UpdateClock(); phaseText.Text = Pomodoro.PhaseName(pomo.Current) + (pomo.Running ? " · 进行中" : " · 已暂停"); if (!pomo.Running && pomo.Left == pomo.Length(pomo.Current)) phaseText.Text = Pomodoro.PhaseName(pomo.Current) + " · 准备开始";
            ((TextBlock)startButton.Content).Text = pomo.Running ? "暂停" : (pomo.Left < pomo.Length(pomo.Current) ? "继续" : "开始");
            doneText.Text = "今日已完成 " + pomo.Completed + " 个番茄"; RebuildEvents();
        }
        internal bool AddEvent()
        {
            var date = CountdownLogic.ParseDate(dateBox.Text, Now());
            if (date == null) { hint.Text = "看不懂这个日期：试试 12/26、2027-06-07 或 30天后"; return false; }
            string name = nameBox.Text.Trim(); if (name.Length == 0) { hint.Text = "请先写上名称"; return false; }
            data.Events.Add(new CountdownEvent { Name = name.Length > 60 ? name.Substring(0, 60) : name, Date = date.Value.ToString("yyyy-MM-dd"), Yearly = yearly });
            nameBox.Text = ""; dateBox.Text = ""; hint.Text = ""; Save(); RebuildEvents(); return true;
        }
        void RebuildEvents()
        {
            eventList.Children.Clear(); var today = Now().Date;
            foreach (var ev in CountdownLogic.Sorted(data.Events, today))
            {
                var e = ev; int days = CountdownLogic.DaysLeft(e, today).Value;
                var g = new Grid { Margin = new Thickness(0, 2, 0, 4), Background = Brushes.Transparent }; g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var left = new StackPanel(); left.Children.Add(new TextBlock { Text = e.Name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = days < 0 ? Dim : Brushes.White });
                left.Children.Add(new TextBlock { Text = e.Date + (e.Yearly ? " · 每年" : ""), FontSize = 10.5, Foreground = Dim });
                var big = new TextBlock { Text = CountdownLogic.DaysText(days), FontSize = days < 0 ? 12 : 18, Foreground = days < 0 ? Dim : days <= 7 ? new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x6B)) : Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
                var del = IconButton("\uE711", "删除", delegate { data.Events.Remove(e); Save(); RebuildEvents(); }, 10); del.Opacity = 0;
                g.MouseEnter += delegate { del.Opacity = 1; }; g.MouseLeave += delegate { del.Opacity = 0; };
                Grid.SetColumn(big, 1); Grid.SetColumn(del, 2); g.Children.Add(left); g.Children.Add(big); g.Children.Add(del); eventList.Children.Add(g);
            }
            if (eventList.Children.Count == 0) eventList.Children.Add(new TextBlock { Text = "还没有倒数日。例如：期末考试  2027-01-10；生日  05/20 并点“每年重复”。", FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 0, 0) });
        }
    }
}
