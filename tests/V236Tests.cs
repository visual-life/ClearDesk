using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClearDesk
{
    /// <summary>2.3.6: every tray icon is listed — also the ones whose tooltip is empty (QQ). Filtering / de-duplication / name fallback on simulated UI Automation results, the page's
    /// refresh and cache, and real helper programs whose tray icon has an empty tooltip.</summary>
    internal static class V236Tests
    {
        static TrayAccess.RawIcon R(string name, byte[] png = null) { return new TrayAccess.RawIcon { Full = name, Png = png }; }
        static NotifyEntry E(string exe, string tip, string desc, bool promoted = false, byte[] snap = null) { return new NotifyEntry { Exe = exe, Tooltip = tip, Description = desc, Promoted = promoted, Exists = true, Running = true, Snapshot = snap }; }

        static byte[] Png(Func<int, int, Color> px, int size = 40)
        {
            var buf = new byte[size * size * 4];
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) { var c = px(x, y); int o = (y * size + x) * 4; buf[o] = c.B; buf[o + 1] = c.G; buf[o + 2] = c.R; buf[o + 3] = c.A; }
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, buf, size * 4)));
            using (var ms = new MemoryStream()) { enc.Save(ms); return ms.ToArray(); }
        }
        static byte[] Shot(Color inside) { return Png((x, y) => x >= 6 && x < 34 && y >= 6 && y < 34 ? inside : Color.FromRgb(128, 128, 128)); }       // what the screen copy of a button looks like: icon in the middle, taskbar colour around
        static byte[] Snap(Color c) { return Png((x, y) => c, 32); }

        internal static void Rules(Action<bool, string> check)
        {
            check(new Version(SettingsWindow.Version) >= new Version("2.3.6"), "2.3.6 version string (or later)");
            bool old = TrayAccess.TestNoIconCache; TrayAccess.TestNoIconCache = true;
            try
            {
                // the reported bug: the 6th icon of the flyout has an EMPTY name and was dropped
                var flyout = new[] { R("ClearDesk · 你的桌面"), R("网易UU远程"), R("YueLink · 已连接\n延迟 20ms"), R("NVIDIA 设置"), R("联想智能引擎"), R("") };
                var entries = new List<NotifyEntry> {
                    E(@"C:\Program Files\Tencent\QQNT\QQ.exe", "", "QQ"),
                    E(@"C:\Tools\YueLink\yuelink.exe", "YueLink", "YueLink"),
                    E(@"C:\Program Files\NVIDIA Corporation\NvContainer\nvcontainer.exe", "NVIDIA 设置", "NVIDIA Container") };
                var items = TrayAccess.Build(flyout, true, entries);
                check(items.Count == 6, "2.3.6 build: all 6 icons of the flyout are kept, the nameless one too (" + items.Count + ")");
                check(items[5].Raw == "" && items[5].Name == "QQ" && items[5].Exe != null && items[5].Exe.EndsWith("QQ.exe"), "2.3.6 build: the nameless icon is named after its program by elimination (" + items[5].Name + ")");
                check(items[2].Raw == "YueLink · 已连接" && items[0].Name == "ClearDesk · 你的桌面", "2.3.6 build: named icons keep their first-line name");
                check(items.Select(i => i.Key).Distinct().Count() == 6 && items.All(i => i.Overflow && i.Key.StartsWith("o:")), "2.3.6 build: keys are unique");
                // same names stay separate, nameless ones too
                var dup = TrayAccess.Build(new[] { R("Chat"), R(""), R("Chat"), R(""), R("") }, false, new List<NotifyEntry>());
                check(dup.Count == 5 && dup.Select(i => i.Key).Distinct().Count() == 5, "2.3.6 build: same-name and nameless icons are all kept, each with its own key");
                check(dup.Where(i => i.Raw == "Chat").Select(i => i.Ordinal).SequenceEqual(new[] { 0, 1 }) && dup.Where(i => i.Raw == "").Select(i => i.Ordinal).SequenceEqual(new[] { 0, 1, 2 }), "2.3.6 build: ordinals count per name");
                check(dup.Where(i => i.Raw == "").Select(i => i.Name).SequenceEqual(new[] { "未命名图标 1", "未命名图标 2", "未命名图标 3" }), "2.3.6 build: icons nobody can name are shown as 未命名图标 1, 2, 3 (never dropped)");
                check(TrayAccess.Build(new List<TrayAccess.RawIcon>(), true, new List<NotifyEntry>()).Count == 0, "2.3.6 build: an empty flyout gives an empty list");
                var one = TrayAccess.Build(new[] { R("") }, true, new List<NotifyEntry>());
                check(one.Count == 1 && one[0].Name == "未命名图标", "2.3.6 build: a single unknown icon is 未命名图标");

                // name fallback: tooltip → file description / product name → exe file name
                check(TrayNames.Friendly(E(@"C:\a\app.exe", "Tip\nline2", "Desc")) == "Tip", "2.3.6 name fallback 1: the tooltip");
                check(TrayNames.Friendly(E(@"C:\a\app.exe", "", " Desc ")) == "Desc", "2.3.6 name fallback 2: the exe's file description");
                check(TrayNames.Friendly(E(@"C:\a\App.Name.exe", "", "")) == "App.Name", "2.3.6 name fallback 3: the exe's file name");
                // owners of icons that already have a name are not candidates
                check(TrayNames.HasNamedIcon(E(@"C:\x\yuelink.exe", "", ""), new[] { "YueLink · 已连接" }) && !TrayNames.HasNamedIcon(E(@"C:\x\QQ.exe", "", "QQ"), new[] { "YueLink", "NVIDIA 设置" }), "2.3.6 candidates: the owner of a named icon is excluded, others are not");
                // two nameless icons and two possible programs, no picture: do not guess
                var two = TrayAccess.Build(new[] { R(""), R("") }, true, new List<NotifyEntry> { E(@"C:\a\one.exe", "", ""), E(@"C:\a\two.exe", "", "") });
                check(two.All(i => i.Name.StartsWith("未命名图标")), "2.3.6 build: two nameless icons and two candidate programs without pictures → no guess");
                // by picture
                var red = Color.FromRgb(220, 40, 40); var blue = Color.FromRgb(40, 60, 220); var green = Color.FromRgb(40, 200, 80);
                var pe = new List<NotifyEntry> { E(@"C:\a\redapp.exe", "", "Red App", false, Snap(red)), E(@"C:\a\blueapp.exe", "", "Blue App", false, Snap(blue)) };
                var pics = TrayAccess.Build(new[] { R("", Shot(blue)), R("", Shot(red)) }, true, pe);
                check(pics[0].Name == "Blue App" && pics[1].Name == "Red App", "2.3.6 build: nameless icons are matched with their program by the picture (" + pics[0].Name + ", " + pics[1].Name + ")");
                double same = TrayNames.Diff(Shot(red), Snap(red)), diff = TrayNames.Diff(Shot(red), Snap(blue));
                check(same >= 0 && same < 0.05 && diff > 0.25, "2.3.6 picture score: same icon " + same.ToString("0.000") + ", different icon " + diff.ToString("0.000"));
                var amb = TrayAccess.Build(new[] { R("", Shot(red)) }, true, new List<NotifyEntry> { E(@"C:\a\red1.exe", "", "Red One", false, Snap(red)), E(@"C:\a\red2.exe", "", "Red Two", false, Snap(red)) });
                check(amb[0].Name.StartsWith("未命名图标"), "2.3.6 build: two programs with the same picture → no guess (" + amb[0].Name + ")");
                var far = TrayAccess.Build(new[] { R("", Shot(green)), R("", Shot(red)) }, true, new List<NotifyEntry> { E(@"C:\a\redapp.exe", "", "Red App", false, Snap(red)), E(@"C:\a\blueapp.exe", "", "Blue App", false, Snap(blue)) });
                check(far[1].Name == "Red App" && far[0].Name == "Blue App", "2.3.6 build: the icon that matches nothing gets the program that is left (" + far[0].Name + ")");
                // same colours, different layout (icons change their colours, not their shape): the shape decides
                Func<bool, byte[]> halves = vertical => Png((x, y) => (vertical ? x : y) < 16 ? Color.FromRgb(235, 235, 235) : Color.FromRgb(20, 20, 20), 32);
                Func<bool, byte[]> halvesShot = vertical => Png((x, y) => x >= 6 && x < 34 && y >= 6 && y < 34 ? ((vertical ? x : y) < 20 ? Color.FromRgb(235, 235, 235) : Color.FromRgb(20, 20, 20)) : Color.FromRgb(128, 128, 128));
                var sh = TrayAccess.Build(new[] { R("", halvesShot(true)) }, true, new List<NotifyEntry> { E(@"C:\a\horizontal.exe", "", "Horizontal", false, halves(false)), E(@"C:\a\vertical.exe", "", "Vertical", false, halves(true)) });
                check(sh[0].Name == "Vertical", "2.3.6 build: the shape of the picture decides when the colours are the same (" + sh[0].Name + ")");
                // programs that are not running / explorer are not candidates
                var nr = E(@"C:\a\gone.exe", "", "Gone"); nr.Running = false;
                var nrItems = TrayAccess.Build(new[] { R("") }, true, new List<NotifyEntry> { nr, E(@"C:\Windows\explorer.exe", "", "Windows Explorer") });
                check(nrItems[0].Name == "未命名图标", "2.3.6 candidates: programs that are not running and explorer.exe are never offered");
                // visible and hidden read separately: Windows' known-folder path form
                check(TrayNames.ReadEntries() != null, "2.3.6 the notification-icon list of Windows can be read (" + TrayNames.ReadEntries().Count + " entries)");
            }
            finally { TrayAccess.TestNoIconCache = old; }
            var src = File.Exists(Path.Combine(Program.Base, "src", "TrayAccess233.cs")) ? File.ReadAllText(Path.Combine(Program.Base, "src", "TrayAccess233.cs")) : null;
            if (src != null) check(!src.Contains("if (name.Length == 0) continue;"), "2.3.6 the code that dropped icons with an empty name is gone");
        }

        static TrayItem T(string name, bool overflow, string raw = null, int ordinal = 0) { return new TrayItem { Name = name, Raw = raw ?? name, Ordinal = ordinal, Tooltip = name, Overflow = overflow, Key = (overflow ? "o:" : "v:") + (raw ?? name) + "#" + ordinal }; }

        internal static async Task Run(Action<bool, string> check)
        {
            await PageModel(check);
            await RealEmptyTooltip(check);
        }

        static async Task PageModel(Action<bool, string> check)
        {
            string cache = Path.Combine(Path.GetTempPath(), "cd236-tray-" + Guid.NewGuid().ToString("N") + ".json");
            string scope = Program.S.TrayScope; bool area = Program.S.TrayArea; string problem = TrayAccess.LastProblem;
            try
            {
                Program.S.TrayArea = true; Program.S.TrayScope = "all";
                var page = new TrayPage234(Dispatcher.CurrentDispatcher, delegate { }, u => { }) { CachePath = cache, AutoRead = false };
                page.Toast = (g, m) => { };
                page.SetVisible(new List<TrayItem> { T("A", false), T("未命名图标", false, "") });
                page.SetHidden(new List<TrayItem> { T("A", true), T("B", true), T("QQ", true, "", 0), T("C", true) }, false);
                check(page.RowNames.SequenceEqual(new[] { "A", "未命名图标", "B", "QQ", "C" }), "2.3.6 tray page: a hidden nameless icon is listed even when the taskbar has a nameless one too; named duplicates are listed once (" + string.Join(",", page.RowNames) + ")");
                check(page.TitleText == "托盘图标 · 5", "2.3.6 tray page: the count includes the nameless icons (" + page.TitleText + ")");
                // refresh replaces what is cached: C is gone, D and a new nameless icon appeared
                page.SetHidden(new List<TrayItem> { T("A", true), T("B", true), T("D", true), T("Q Q", true, "", 0) }, false);
                check(page.RowNames.SequenceEqual(new[] { "A", "未命名图标", "B", "D", "Q Q" }), "2.3.6 tray page: a refresh replaces the old hidden list (stale C gone, new icons in) (" + string.Join(",", page.RowNames) + ")");
                // a failed read never wipes the list
                TrayAccess.LastProblem = "隐藏的图标面板没有出现";
                page.SetHidden(new List<TrayItem>(), false);
                check(page.RowNames.SequenceEqual(new[] { "A", "未命名图标", "B", "D", "Q Q" }), "2.3.6 tray page: a failed read keeps the icons that were known");
                TrayAccess.LastProblem = null;
                page.SetHidden(new List<TrayItem>(), false);
                check(page.RowNames.SequenceEqual(new[] { "A", "未命名图标" }), "2.3.6 tray page: a successful read that finds nothing hidden empties the hidden list");
                page.SetHidden(new List<TrayItem> { T("B", true), T("Q Q", true, "", 0), T("Q Q", true, "", 1) }, false);
                // remembered between runs, with their identity
                var again = new TrayPage234(Dispatcher.CurrentDispatcher, delegate { }, u => { }) { CachePath = cache, AutoRead = false }; again.LoadCache(); again.SetVisible(new List<TrayItem>());
                var shown = again.Shown();
                check(shown.Count == 3 && shown[1].Raw == "" && shown[1].Ordinal == 0 && shown[2].Raw == "" && shown[2].Ordinal == 1, "2.3.6 tray page: nameless icons survive the cache with their identity");
                // the clicks reach the right icon
                var clicks = new List<string>();
                again.Clicker = (it, mode) => { clicks.Add(it.Raw + "/" + it.Ordinal + ":" + mode); return null; }; again.Toast = (g, m) => { };
                again.TestClickRow(2, 0);
                check(await V233Tests.Until(() => clicks.Count == 1, 1500) && clicks[0] == "/1:0", "2.3.6 tray page: a click on the second nameless row is for ordinal 1 (" + string.Join(",", clicks) + ")");
                await V233Tests.Until(() => !again.Busy, 1000);
            }
            finally { Program.S.TrayScope = scope; Program.S.TrayArea = area; TrayAccess.LastProblem = problem; try { File.Delete(cache); } catch { } }
        }

        static async Task RealEmptyTooltip(Action<bool, string> check)
        {
            if (Program.Rendering && Environment.GetEnvironmentVariable("CLEARDESK_NO_TRAY_TEST") == "1") return;
            if (!TrayAccess.Available) { check(true, "2.3.6 real tray: no taskbar to read in this session — skipped"); return; }
            string stamp = Guid.NewGuid().ToString("N");
            string[] logs = { Path.Combine(Path.GetTempPath(), "cd236-a-" + stamp + ".log"), Path.Combine(Path.GetTempPath(), "cd236-b-" + stamp + ".log") };
            string[] ctls = { logs[0] + ".ctl", logs[1] + ".ctl" }; var procs = new Process[2];
            Func<int, string[]> lines = n => { try { return File.Exists(logs[n]) ? File.ReadAllLines(logs[n]) : new string[0]; } catch { return new string[0]; } };
            Func<List<TrayItem>> unnamed = () => TrayAccess.ReadVisible(false).Where(i => i.Raw.Length == 0).ToList();
            try
            {
                int before = (await Task.Run(() => unnamed())).Count;
                int beforeHidden = (await Task.Run(() => TrayAccess.ReadOverflow(false))).Count(i => i.Raw.Length == 0);
                for (int n = 0; n < 2; n++) procs[n] = V233Tests.StartProcess("--tray-helper @empty \"" + logs[n] + "\" \"" + ctls[n] + "\" 90");
                check(await V233Tests.Until(() => File.Exists(ctls[0] + ".ready") && File.Exists(ctls[1] + ".ready"), 12000), "2.3.6 real tray: two helper programs are up, their tray icons have an EMPTY tooltip");
                List<TrayItem> now = null; bool inFlyout = false;
                for (int i = 0; i < 40 && (now == null || now.Count < before + 2); i++) { now = await Task.Run(() => unnamed()); if (now.Count < before + 2) await Task.Delay(150); }
                if (now.Count < before + 2) { var hid = await Task.Run(() => TrayAccess.ReadOverflow(true)); inFlyout = true; now = hid.Where(i => i.Raw.Length == 0).ToList(); before = beforeHidden; }
                check(now.Count >= before + 2, "2.3.6 real tray: both nameless icons are listed (" + now.Count + " nameless, " + before + " before" + (inFlyout ? ", found in the hidden icons" : "") + ")");
                check(now.All(i => i.Name.Length > 0) && now.Select(i => i.Key).Distinct().Count() == now.Count, "2.3.6 real tray: nameless icons get a name and an own key (" + string.Join(" | ", now.Select(i => i.Name)) + ")");
                if (before == 0 && now.Count == 2)       // only when the two helpers' icons are the only nameless ones: nothing of the user's can be clicked
                {
                    int b0 = lines(0).Length, b1 = lines(1).Length;
                    string err = await Task.Run(() => TrayAccess.Click(now[1], 0));
                    bool got = await V233Tests.Until(() => lines(0).Length > b0 || lines(1).Length > b1, 4000);
                    bool aGot = lines(0).Length > b0, bGot = lines(1).Length > b1;
                    check(err == null && got && (aGot ^ bGot), "2.3.6 real tray: a click on one nameless icon reaches exactly one of the two programs (" + (err ?? (aGot ? "first" : "") + (bGot ? "second" : "")) + ")");
                    string err2 = await Task.Run(() => TrayAccess.Click(now[0], 0));
                    bool aGot2 = await V233Tests.Until(() => (aGot ? lines(1).Length > b1 : lines(0).Length > b0), 4000);
                    check(err2 == null && aGot2, "2.3.6 real tray: the other nameless icon reaches the other program (" + (err2 ?? "") + ")");
                }
                else check(true, "2.3.6 real tray: the click part is skipped (other nameless icons exist, none of the user's is ever clicked)");
                for (int n = 0; n < 2; n++) Cmd(ctls[n], "exit");
                check(procs[0].WaitForExit(8000) && procs[1].WaitForExit(8000), "2.3.6 real tray: both helpers exit");
            }
            finally
            {
                foreach (var p in procs) { try { if (p != null && !p.HasExited) p.Kill(); } catch { } }
                TrayAccess.CloseFlyout();
                for (int n = 0; n < 2; n++) { try { File.Delete(logs[n]); File.Delete(ctls[n]); File.Delete(ctls[n] + ".ready"); } catch { } }
            }
        }

        static void Cmd(string ctl, string c) { File.AppendAllText(ctl, c + Environment.NewLine); }
    }
}
