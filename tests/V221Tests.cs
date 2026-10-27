using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClearDesk
{
    /// <summary>2.2.1 tests.</summary>
    internal static class V221Tests
    {
        static string TempDir(string name) { string d = Path.Combine(Path.GetTempPath(), "cd221-" + name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(d); return d; }
        static void WriteImage(string path, int w, int h, bool jpeg)
        {
            var px = new byte[w * h * 4]; for (int i = 0; i < px.Length; i += 4) { px[i] = (byte)(i % 251); px[i + 1] = 90; px[i + 2] = 200; px[i + 3] = 255; }
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            BitmapEncoder enc = jpeg ? (BitmapEncoder)new JpegBitmapEncoder() : new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var f = File.Create(path)) enc.Save(f);
        }

        internal static void Rules(Action<bool, string> check)
        {
            PomodoroRules(check); PlacementRules(check); MixerRules(check); SystemItemRules(check);
        }

        // ---------------------------------------------------------------- item 1: pomodoro settings
        static void PomodoroRules(Action<bool, string> check)
        {
            int v; string err;
            check(PomodoroSettings.TryParse(0, "25", out v, out err) && v == 25 && PomodoroSettings.TryParse(0, " 180 ", out v, out err) && v == 180 && PomodoroSettings.TryParse(0, "1", out v, out err) && v == 1, "pomodoro: 1–180 minutes are accepted");
            bool bad = true; foreach (var t in new[] { "0", "181", "-5", "abc", "", "  ", "1.5", "２５x", "99999999999" }) bad &= !PomodoroSettings.TryParse(0, t, out v, out err) && !string.IsNullOrEmpty(err);
            check(bad, "pomodoro: empty, zero, negative, decimal, text and over-range input is refused with a message");
            check(!PomodoroSettings.TryParse(3, "1", out v, out err) && PomodoroSettings.TryParse(3, "2", out v, out err) && PomodoroSettings.TryParse(3, "12", out v, out err) && !PomodoroSettings.TryParse(3, "13", out v, out err), "pomodoro: rounds before a long break are 2–12");
            check(PomodoroSettings.Clamp(0, 500) == 180 && PomodoroSettings.Clamp(0, -3) == 1 && PomodoroSettings.Clamp(1, 99) == 60, "pomodoro: stepper values are clamped");
            var d = new CountdownData { WorkMinutes = 0, ShortMinutes = 999, LongMinutes = -3, LongEvery = 1 }; PomodoroSettings.Normalize(d);
            check(d.WorkMinutes == 25 && d.ShortMinutes == 60 && d.LongMinutes == 15 && d.LongEvery == 2, "pomodoro: a hand-edited settings file is normalised (" + d.WorkMinutes + "/" + d.ShortMinutes + "/" + d.LongMinutes + "/" + d.LongEvery + ")");
            check(PomodoroSettings.Presets.Length >= 3 && PomodoroSettings.Presets.All(p => PomodoroSettings.Clamp(0, p.Work) == p.Work && PomodoroSettings.Clamp(1, p.Short) == p.Short && PomodoroSettings.Clamp(2, p.Long) == p.Long && PomodoroSettings.Clamp(3, p.Rounds) == p.Rounds), "pomodoro: presets are inside the allowed ranges");
            // the state machine uses the configured lengths and rounds
            var pomo = new Pomodoro(); pomo.Configure(50, 7, 20, 3); var t0 = new DateTime(2026, 10, 26, 9, 0, 0); var now = t0; var seq = new List<string>();
            check(pomo.Length(Pomodoro.Phase.Work) == TimeSpan.FromMinutes(50) && pomo.Length(Pomodoro.Phase.ShortBreak) == TimeSpan.FromMinutes(7) && pomo.Length(Pomodoro.Phase.LongBreak) == TimeSpan.FromMinutes(20), "pomodoro: custom lengths are used");
            for (int i = 0; i < 6; i++)
            {
                pomo.Start(now); now = now.AddMinutes((int)pomo.Length(pomo.Current).TotalMinutes + 1); var phase = pomo.Current; pomo.Tick(now); seq.Add(phase.ToString()[0] + ">" + pomo.Current.ToString()[0]);
            }
            check(string.Join(" ", seq) == "W>S S>W W>S S>W W>L L>W", "pomodoro: a long break comes after every 3rd focus round (" + string.Join(" ", seq) + ")");
        }

        // ---------------------------------------------------------------- item 5: placement
        static void PlacementRules(Action<bool, string> check)
        {
            var areas = new List<Rect> { new Rect(0, 0, 1920, 1040), new Rect(1920, 0, 1280, 1000) };
            var size = new Size(300, 200);
            Func<Point, Point, bool> same = (a, b) => Math.Abs(a.X - b.X) < .01 && Math.Abs(a.Y - b.Y) < .01;
            check(same(WidgetPlacement.Place(new Point(100, 100), size, areas, false), new Point(100, 100)), "placement: top-left at the cursor");
            check(same(WidgetPlacement.Place(new Point(960, 540), size, areas, true), new Point(810, 440)), "placement: centred at the cursor");
            check(same(WidgetPlacement.Place(new Point(1900, 1030), size, areas, false), new Point(1620, 840)), "placement: kept inside the work area at the right / bottom edge");
            check(same(WidgetPlacement.Place(new Point(3190, 990), size, areas, false), new Point(2900, 800)), "placement: second monitor is clamped to its own work area");
            check(same(WidgetPlacement.Place(new Point(5, 5), size, areas, true), new Point(0, 0)), "placement: centred near the corner is clamped to the top-left");
            check(same(WidgetPlacement.Place(new Point(500, 500), new Size(3000, 2000), areas, false), new Point(0, 0)), "placement: a widget bigger than the screen is aligned to its top-left");
            check(same(WidgetPlacement.Place(new Point(-50, 500), size, areas, false), new Point(0, 500)), "placement: a point outside every screen uses the nearest work area");
            var left = new List<Rect> { new Rect(-1920, 0, 1920, 1080), new Rect(0, 0, 2560, 1400) };
            check(same(WidgetPlacement.Place(new Point(-100, 50), size, left, false), new Point(-300, 50)), "placement: monitor left of the primary one (negative coordinates)");
            check(same(WidgetPlacement.ToDip(3000, 1500, 1.5, 1.5), new Point(2000, 1000)) && same(WidgetPlacement.ToDip(30, 30, 0, 0), new Point(30, 30)), "placement: device pixels become DIPs by the DPI scale (zero scale is safe)");
            // snapping to another widget / the screen edge
            var other = new List<Rect> { new Rect(500, 300, 300, 200) }; var snapAreas = new List<Rect> { new Rect(0, 0, 1920, 1080) };
            var s1 = WidgetPlacement.Compute(new Point(803, 100), new Size(300, 100), snapAreas, other, snapAreas, true, false);
            check(same(s1, new Point(800, 100)), "placement: snaps to the edge of another widget (" + s1.X + "," + s1.Y + ")");
            var s2 = WidgetPlacement.Compute(new Point(803, 100), new Size(300, 100), snapAreas, other, snapAreas, false, false);
            check(same(s2, new Point(803, 100)), "placement: no snapping when it is off (Alt held / setting off)");
            var s3 = WidgetPlacement.Compute(new Point(5, 700), new Size(300, 100), snapAreas, new List<Rect>(), snapAreas, true, false);
            check(Math.Abs(s3.X) < .01, "placement: snaps to the screen edge");
            // the right-click record expires
            WidgetPlacement.Record(10, 20, 1000); int x, y;
            check(WidgetPlacement.TryRecent(1000, out x, out y) && x == 10 && y == 20 && WidgetPlacement.TryRecent(1000 + WidgetPlacement.RecentMs, out x, out y) && !WidgetPlacement.TryRecent(1000 + WidgetPlacement.RecentMs + 1, out x, out y) && !WidgetPlacement.TryRecent(999, out x, out y), "placement: a right-click position is valid for 30 seconds only");
            WidgetPlacement.Forget(); check(!WidgetPlacement.TryRecent(1000, out x, out y), "placement: forgotten click gives the default placement");
        }

        // ---------------------------------------------------------------- item 3: mixer model
        sealed class FakeVolume : SessionAudio.ISimpleVolume
        {
            public float Level = .5f; public bool Muted; public int Sets;
            public int SetMasterVolume(float level, ref Guid ctx) { Level = level; Sets++; return 0; }
            public int GetMasterVolume(out float level) { level = Level; return 0; }
            public int SetMute(bool mute, ref Guid ctx) { Muted = mute; return 0; }
            public int GetMute(out bool mute) { mute = Muted; return 0; }
        }
        static RawSession Raw(string exe, int pid, string display = "", string desc = "", bool system = false, int state = 0, float vol = .5f, object control = null)
        {
            return new RawSession { ExePath = exe, Pid = pid, DisplayName = display, FileDescription = desc, ProcessName = exe == null ? null : Path.GetFileNameWithoutExtension(exe), IsSystem = system, State = state, Volume = vol, Control = control };
        }
        static void MixerRules(Action<bool, string> check)
        {
            var rows = MixerModel.Group(new[]
            {
                Raw(@"c:\apps\zed.exe", 11, "", "Zed Player", control: new FakeVolume()),
                Raw(null, 0, "@%SystemRoot%\\system32\\AudioSrv.Dll,-202", "", system: true, control: new FakeVolume()),
                Raw(@"c:\apps\alpha.exe", 12, "", "Alpha", control: new FakeVolume()),
                Raw(@"c:\apps\alpha.exe", 13, "", "Alpha", control: new FakeVolume(), state: 1),
                Raw(@"c:\apps\old.exe", 14, "", "Old", state: 2),
                Raw(null, 0, "", ""),
                Raw(null, 77, "Virtual Mic", ""),
                Raw(@"c:\apps\plain.exe", 15, "@%x%,-1", "  ", control: new FakeVolume()),
            });
            check(rows.Count == 5, "mixer model: expired and nameless sessions are dropped, the rest is grouped (" + rows.Count + " rows)");
            check(rows[0].System && rows[0].Name == "系统声音", "mixer model: system sounds come first");
            check(rows.Skip(1).Select(r => r.Name).SequenceEqual(new[] { "Alpha", "plain", "Virtual Mic", "Zed Player" }, StringComparer.OrdinalIgnoreCase), "mixer model: apps are sorted by name (" + string.Join("|", rows.Select(r => r.Name)) + ")");
            var alpha = rows.First(r => r.Name == "Alpha");
            check(alpha.Controls.Count == 2 && alpha.Active, "mixer model: two sessions of one program are one row, controlled together");
            check(MixerModel.NameOf(Raw(@"c:\a.exe", 1, "My Game", "Desc")) == "My Game" && MixerModel.NameOf(Raw(@"c:\a.exe", 1, "", "Desc")) == "Desc" && MixerModel.NameOf(Raw(@"c:\a.exe", 1, "a.exe", "")) == "a" && MixerModel.NameOf(Raw(null, 55)) == "进程 55", "mixer model: name = session name, else file description, else program name, else pid");
            check(Math.Abs(MixerModel.LevelFromX(50, 200) - .25f) < .001 && MixerModel.LevelFromX(-5, 200) == 0 && MixerModel.LevelFromX(300, 200) == 1 && MixerModel.LevelFromX(10, 0) == 0, "mixer model: slider position becomes a 0–1 level");
            check(MixerModel.Percent(.456f) == "46" && MixerModel.Percent(-1) == "0" && MixerModel.Percent(3) == "100" && Math.Abs(MixerModel.Step(.5f, 120) - .52f) < .001 && MixerModel.Step(.01f, -120) == 0 && MixerModel.Step(.99f, 120) == 1, "mixer model: percent text and wheel steps are clamped");
            var again = MixerModel.Group(new[] { Raw(@"c:\apps\zed.exe", 11, "", "Zed Player"), Raw(null, 0, "x", "", system: true), Raw(@"c:\apps\alpha.exe", 12, "", "Alpha"), Raw(@"c:\apps\plain.exe", 15, "", ""), Raw(null, 77, "Virtual Mic", "") });
            check(MixerModel.SameRows(rows, again) && !MixerModel.SameRows(rows, again.Take(3).ToList()), "mixer model: unchanged rows keep the layout");
            var fake = new FakeVolume(); var one = MixerModel.Group(new[] { Raw(@"c:\a.exe", 1, "", "A", control: fake) })[0];
            check(SessionAudio.SetVolume(one, .3f) && Math.Abs(fake.Level - .3f) < .001 && SessionAudio.SetMuted(one, true) && fake.Muted && SessionAudio.SetVolume(one, .6f) && !fake.Muted, "mixer: set volume / mute reach the session; raising the volume of a muted app unmutes it");
            check(!SessionAudio.SetVolume(new MixerRow(), .5f), "mixer: a row without a live session reports failure");
        }

        // ---------------------------------------------------------------- item 4: system icons
        static void SystemItemRules(Action<bool, string> check)
        {
            check(SystemItems.All.Length == 5 && SystemItems.All.Select(i => i.Name).SequenceEqual(new[] { "此电脑", "回收站", "控制面板", "用户文件夹", "网络" }) && SystemItems.All.All(i => i.Path == "shell:::" + i.Clsid), "system icons: this PC, recycle bin, control panel, user folder, network");
            check(SystemItems.Find("SHELL:::{645ff040-5081-101b-9f08-00aa002f954e}") == SystemItems.RecycleBin && SystemItems.Find("C:\\x") == null && SystemItems.Find(null) == null && !SystemItems.IsSystem("shell:AppsFolder\\x"), "system icons: recognised by their shell name (case-insensitive)");
            check(SystemItems.ParsingName(SystemItems.Computer.Path) == "::" + SystemItems.Computer.Clsid && SystemItems.ParsingName("C:\\a.txt") == "C:\\a.txt", "system icons: parsing name for the shell icon lookup");
            var cmd = SystemItems.OpenCommand(SystemItems.ControlPanel.Path);
            check(cmd != null && cmd.FileName == "explorer.exe" && cmd.Arguments == SystemItems.ControlPanel.Path && SystemItems.OpenCommand("C:\\a.txt") == null, "system icons: opened with explorer.exe shell:::{CLSID}");
            check(Program.DisplayName(SystemItems.Computer.Path) == "此电脑" && Program.DisplayName("C:\\x\\y.lnk") == "y", "system icons: display names");
            var old = SystemItems.DesktopFlag;
            try
            {
                SystemItems.DesktopFlag = c => null; var d0 = SystemItems.OnDesktop();
                SystemItems.DesktopFlag = c => c == SystemItems.Computer.Clsid ? (int?)0 : c == SystemItems.RecycleBin.Clsid ? (int?)1 : c == SystemItems.Network.Clsid ? (int?)0 : null; var d1 = SystemItems.OnDesktop();
                check(d0.Count == 1 && d0[0] == SystemItems.RecycleBin.Path, "system icons: with no setting only the recycle bin counts as on the desktop (Windows default)");
                check(d1.Count == 2 && d1.Contains(SystemItems.Computer.Path) && d1.Contains(SystemItems.Network.Path), "system icons: icons switched on in the desktop icon settings are collected, hidden ones are not");
            }
            finally { SystemItems.DesktopFlag = old; }
            check(SystemItems.StockIconIndex("C:\\a.txt") == -1 && SystemItems.StockIconIndex(SystemItems.Computer.Path) == -1, "system icons: only the recycle bin has a state icon");
        }

        internal static async Task Run(Action<bool, string> check)
        {
            string root = TempDir("win"); string oldRoot = ToolStore.TestRoot; ToolStore.TestRoot = root; var opened = new List<GlassWindow>();
            try
            {
                // reproduction: the real decoder (no fake loader), awkward file names, large photo
                string pics = Path.Combine(root, "pics"); Directory.CreateDirectory(pics);
                string[] names = { "plain.png", "中文 图片.jpg", "a#b.png", "100%.png", "big.jpg" };
                foreach (var n in names) WriteImage(Path.Combine(pics, n), n == "big.jpg" ? 3000 : 120, n == "big.jpg" ? 2000 : 80, n.EndsWith(".jpg"));
                var album = new AlbumWindow { Animate = false }; album.Show(); opened.Add(album);
                album.AddSource(pics);
                var seen = new List<string>();
                for (int i = 0; i < names.Length; i++) { album.ShowNext(); seen.Add(Path.GetFileName(album.CurrentPath)); }
                check(album.ShownCount >= names.Length && seen.Distinct().Count() == names.Length, "album (real decoder): every picture incl. '#', '%', Chinese names and a 3000 px photo shows [" + string.Join("|", seen) + " shown=" + album.ShownCount + "]");

                await AlbumRun(check, root, opened);
                PomodoroWindowRun(check, root, opened);
                MixerWindowRun(check, opened);
                await Shots(opened);
                SystemItemsWindowRun(check, root, opened);
                PlacementWindowRun(check, root, opened);
            }
            finally { foreach (var w in opened) { try { w.Close(); } catch { } } ToolStore.TestRoot = oldRoot; try { Directory.Delete(root, true); } catch { } }
        }

        // ---------------------------------------------------------------- item 2: picture album (图片相册)
        static async Task AlbumRun(Action<bool, string> check, string root, List<GlassWindow> opened)
        {
            await Task.Yield();
            ToolStore.Save("album", new AlbumData()); try { if (Directory.Exists(AlbumStore.Folder)) Directory.Delete(AlbumStore.Folder, true); } catch { }
            string src = Path.Combine(root, "src"); Directory.CreateDirectory(src);
            string one = Path.Combine(src, "photo one.png"), two = Path.Combine(src, "照片 二#2.jpg"), text = Path.Combine(src, "note.txt");
            WriteImage(one, 200, 120, false); WriteImage(two, 800, 600, true); File.WriteAllText(text, "x");
            var album = new AlbumWindow { Animate = false }; album.Show(); opened.Add(album);
            check(album.CurrentPath == null && album.AddPaths(new[] { text }) == 0, "album: starts empty, a text file is not added");
            // file dialog
            album.ImageDialog = () => new[] { one };
            album.PickImages();
            string stored = album.CurrentPath;
            check(stored != null && AlbumStore.IsStored(stored) && File.Exists(stored) && album.Data.Sources.Count == 0, "album: a picked file is copied into ClearDesk's own folder and shown (" + (stored == null ? "null" : Path.GetFileName(stored)) + ")");
            File.Delete(one);
            album.Reload();
            check(File.Exists(stored) && album.CurrentPath == stored, "album: deleting the original does not break the picture");
            album.Close(); opened.Remove(album);
            var again = new AlbumWindow { Animate = false }; again.Show(); opened.Add(again);
            check(again.CurrentPath == stored, "album: the picture is still there after the widget is recreated (persisted)");
            // drag and drop: files, folders and picture data
            int files0 = Directory.GetFiles(AlbumStore.Folder).Length;
            var drop = new DataObject(); drop.SetData(DataFormats.FileDrop, new[] { two });
            check(again.AddFromData(drop) == 1 && Directory.GetFiles(AlbumStore.Folder).Length == files0 + 1, "album: dropping a file with a Chinese name and '#' adds it");
            check(again.AddFromData(drop) == 1 && Directory.GetFiles(AlbumStore.Folder).Length == files0 + 1, "album: the same picture dropped twice is stored once");
            var bmpData = new DataObject(); bmpData.SetImage(BitmapSource.Create(40, 30, 96, 96, PixelFormats.Bgra32, null, new byte[40 * 30 * 4], 40 * 4));
            check(again.AddFromData(bmpData) == 1 && Directory.GetFiles(AlbumStore.Folder).Length == files0 + 2, "album: dropped picture data (not a file) is saved as a PNG");
            check(again.AddFromData(new DataObject(DataFormats.Text, "hello")) == 0, "album: dropping text adds nothing");
            string folder = Path.Combine(root, "folder"); Directory.CreateDirectory(folder); string inFolder = Path.Combine(folder, "f.png"); WriteImage(inFolder, 64, 64, false);
            var folderDrop = new DataObject(); folderDrop.SetData(DataFormats.FileDrop, new[] { folder });
            check(again.AddFromData(folderDrop) == 1 && again.Data.Sources.Contains(folder), "album: a dropped folder becomes a source");
            // clipboard
            var clipBmp = BitmapSource.Create(50, 50, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)77, 50 * 50 * 4).ToArray(), 50 * 4);
            int before = Directory.GetFiles(AlbumStore.Folder).Length;
            again.ClipboardImage = () => clipBmp; again.ClipboardFiles = () => new string[0];
            check(again.PasteImage() && Directory.GetFiles(AlbumStore.Folder).Length == before + 1 && AlbumStore.IsStored(again.CurrentPath), "album: paste image from the clipboard");
            again.ClipboardImage = () => null; again.ClipboardFiles = () => new[] { two };
            check(again.PasteImage(), "album: paste copied picture files from the clipboard");
            again.ClipboardFiles = () => new string[0];
            check(!again.PasteImage(), "album: pasting with nothing useful on the clipboard reports failure");
            // fit / fill
            again.SetFill(false); check(!ToolStore.Load<AlbumData>("album").Fill, "album: 'show whole picture' is saved");
            again.SetFill(true); check(ToolStore.Load<AlbumData>("album").Fill, "album: 'fill' is saved");
            // remove
            string current = again.CurrentPath; bool inStore = AlbumStore.IsStored(current);
            check(again.RemoveCurrent() && inStore && !File.Exists(current), "album: removing a stored picture deletes the copy");
            again.AddSource(folder); again.Reload(); int guard = 0; while (again.CurrentPath != inFolder && guard++ < 10) again.ShowNext();
            check(again.CurrentPath == inFolder && again.RemoveCurrent() && File.Exists(inFolder) && again.Data.Excluded.Contains(inFolder) && AlbumLogic.Enumerate(new[] { folder }, false, 100, again.Data.Excluded).Count == 0, "album: removing a picture of a folder source hides it and never deletes the user's file");
            check(AlbumStore.CopyIn(text) == null && AlbumStore.CopyIn(Path.Combine(src, "missing.png")) == null, "album: only existing picture files are copied");
            // 2.2.2: the widget menu entry creates a new instance every time (the first keeps id "album", the next ones are "album~N")
            var made = Program.NewWidget("album"); var made2 = Program.NewWidget("album");
            check(made != null && made2 != null && !ReferenceEquals(made, made2) && made.ExportName != made2.ExportName, "album: 新建组件 → 图片相册 creates a new instance each time (multi-instance since 2.2.2)");
            if (made != null) { opened.Add(made); }
            if (made2 != null) { opened.Add(made2); }
        }

        // ---------------------------------------------------------------- item 1: pomodoro window
        static void PomodoroWindowRun(Action<bool, string> check, string root, List<GlassWindow> opened)
        {
            ToolStore.Save("countdown", new CountdownData());
            var w = new CountdownWindow { Now = () => new DateTime(2026, 10, 26, 9, 0, 0) }; w.Show(); opened.Add(w);
            w.SetAll(50, 10, 20, 3);
            var saved = ToolStore.Load<CountdownData>("countdown");
            check(saved.WorkMinutes == 50 && saved.ShortMinutes == 10 && saved.LongMinutes == 20 && saved.LongEvery == 3 && w.Timer2.Length(Pomodoro.Phase.Work) == TimeSpan.FromMinutes(50), "pomodoro window: the chosen lengths are applied and saved");
            check(w.SummaryText.Contains("50") && w.SummaryText.Contains("10") && w.SummaryText.Contains("20") && w.SummaryText.Contains("3"), "pomodoro window: the summary shows the settings (" + w.SummaryText + ")");
            w.Step(0, 5); w.Step(0, 1000); check(w.Timer2.WorkMinutes == 180, "pomodoro window: the stepper stops at 180 minutes");
            w.Step(0, -1000); check(w.Timer2.WorkMinutes == 1, "pomodoro window: the stepper stops at 1 minute");
            w.SetAll(0, 999, -4, 99); check(w.Timer2.WorkMinutes == 1 && w.Timer2.ShortMinutes == 60 && w.Timer2.LongMinutes == 1 && w.Timer2.LongEvery == 12, "pomodoro window: out-of-range values are clamped");
            w.Close(); opened.Remove(w);
            var w2 = new CountdownWindow(); w2.Show(); opened.Add(w2);
            check(w2.Timer2.WorkMinutes == 1 && w2.Timer2.LongEvery == 12, "pomodoro window: the settings survive a restart");
            w2.SetAll(25, 5, 15, 4); w2.ToggleRun(); w2.SetAll(30, 5, 15, 4);
            check(w2.Timer2.Running && w2.Timer2.Remaining(DateTime.Now) > TimeSpan.FromMinutes(24) && w2.Timer2.Remaining(DateTime.Now) <= TimeSpan.FromMinutes(25), "pomodoro window: changing the length while running keeps the current round (the new length starts with the next round)");
            w2.ToggleRun();
        }

        // ---------------------------------------------------------------- item 3: mixer page in the quick-controls widget
        static void MixerWindowRun(Action<bool, string> check, List<GlassWindow> opened)
        {
            var fakes = new[] { new FakeVolume { Level = .8f }, new FakeVolume { Level = .2f }, new FakeVolume { Level = .5f } };
            var sessions = new List<RawSession> { Raw(null, 0, "", "", system: true, vol: .8f, control: fakes[0]), Raw(@"c:\apps\music.exe", 21, "", "Music", state: 1, vol: .2f, control: fakes[1]), Raw(@"c:\apps\browser.exe", 22, "", "Browser", vol: .5f, control: fakes[2]) };
            var page = new MixerPage(null, delegate { }, delegate { }, delegate { });
            page.Reader = () => sessions.ToList();
            page.RefreshNow();
            check(page.RowCount == 3 && page.RowName(0) == "系统声音" && page.RowName(1) == "Browser" && page.RowName(2) == "Music", "mixer page: one row per application, system sounds first (" + page.RowCount + ")");
            check(page.TestSetVolume(page.Rows[2].Key, .35f) && Math.Abs(fakes[1].Level - .35f) < .001 && Math.Abs(fakes[0].Level - .8f) < .001, "mixer page: the slider changes only its own application");
            check(page.TestToggleMute(page.Rows[1].Key) && fakes[2].Muted && page.TestToggleMute(page.Rows[1].Key) && !fakes[2].Muted, "mixer page: the mute button toggles");
            sessions.RemoveAt(1); page.RefreshNow();
            check(page.RowCount == 2 && page.Rows.All(r => r.Name != "Music"), "mixer page: an application that stopped disappears");
            sessions.Add(Raw(@"c:\apps\game.exe", 23, "", "Game")); page.RefreshNow();
            check(page.RowCount == 3 && page.Refreshes == 3, "mixer page: a new application appears");
            sessions.Clear(); page.RefreshNow();
            check(page.RowCount == 0 && page.StatusText.Length > 0, "mixer page: no applications shows a hint");
            page.Reader = () => { throw new InvalidOperationException("audio service gone"); };
            page.RefreshNow(); check(page.RowCount == 0, "mixer page: a failing audio service does not throw");
            page.Stop();
            // inside the widget: the 声音 button page opens, polls only while open, and closes
            var qc = new QuickControlWindow(); qc.Show(); opened.Add(qc);
            var mixer = qc.TestMixer; check(mixer != null && !qc.MixerOpen && !mixer.Running, "quick controls: the mixer page is closed at first and does not poll");
            qc.ShowMixer(true); check(qc.MixerOpen && mixer.Running && mixer.Refreshes >= 1, "quick controls: the 声音 button opens the mixer page inside the widget and reads the sessions");
            qc.ShowMixer(false); check(!qc.MixerOpen && !mixer.Running && mixer.RowCount == 0, "quick controls: back closes the page, stops polling and releases the sessions");
            // live (read-only): the real Core Audio session list can be read
            string error; var live = SessionAudio.ReadSessions(out error);
            check(live != null && (error == null || error.Length > 0), "mixer (live): the audio sessions of this PC can be read (" + live.Count + " sessions" + (error == null ? "" : ", " + error) + ")");
            SessionAudio.Release(MixerModel.Group(live));

        }

        static async Task Shots(List<GlassWindow> opened)
        {
            await Task.Yield();
            // screenshots for the maintainer (only when CD_SHOT_DIR is set)
            string shots = Environment.GetEnvironmentVariable("CD_SHOT_DIR");
            if (!string.IsNullOrEmpty(shots) && Directory.Exists(shots))
            {
                var vis = new List<RawSession> { Raw(null, 0, "", "", system: true, vol: .8f, control: new FakeVolume()), Raw(@"c:\windows\explorer.exe", 31, "", "Windows Explorer", state: 1, vol: .45f, control: new FakeVolume { Level = .45f }), Raw(@"c:\windows\system32\notepad.exe", 32, "", "记事本", vol: 1f, control: new FakeVolume { Level = 1f }), Raw(@"c:\windows\system32\cmd.exe", 33, "", "一个名字很长很长很长很长的播放器应用程序", vol: .1f, control: new FakeVolume { Level = .1f }), Raw(null, 78, "麦克风 (Virtual Cable)", "", state: 1, vol: .6f, control: new FakeVolume { Level = .6f }) };
                var shot = new QuickControlWindow(); shot.Show(); opened.Add(shot);
                shot.TestMixer.Reader = () => vis.ToList();
                Action<string> save = name =>
                {
                    var el = (FrameworkElement)shot.Content; el.UpdateLayout(); int w = (int)Math.Ceiling(el.ActualWidth), h = (int)Math.Ceiling(el.ActualHeight);
                    var bmp = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96, 96, PixelFormats.Pbgra32); bmp.Render(el);
                    var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp)); using (var f = File.Create(Path.Combine(shots, name))) enc.Save(f);
                };
                save("quick-main.png"); shot.ShowMixer(true); save("quick-mixer.png");
                shot.Width = 560; shot.Height = 520; save("quick-mixer-large.png"); shot.ShowMixer(false); save("quick-back.png");
                await Task.Delay(100);
            var boxData = new BoxData { Title = "桌面收纳", Desktop = false, X = 80, Y = 80 }; var ow = new OrganizerWindow(boxData); ow.Show(); opened.Add(ow);
                ow.SetSystemItem(SystemItems.Computer.Path, true); ow.SetSystemItem(SystemItems.RecycleBin.Path, true); ow.SetSystemItem(SystemItems.ControlPanel.Path, true); ow.SetSystemItem(SystemItems.UserFiles.Path, true); ow.SetSystemItem(SystemItems.Network.Path, true);
                await Task.Delay(1500);
                var cel = (FrameworkElement)ow.Content; cel.UpdateLayout();
                var ob = new RenderTargetBitmap((int)Math.Ceiling(cel.ActualWidth), (int)Math.Ceiling(cel.ActualHeight), 96, 96, PixelFormats.Pbgra32); ob.Render(cel);
                var oe = new PngBitmapEncoder(); oe.Frames.Add(BitmapFrame.Create(ob)); using (var f = File.Create(Path.Combine(shots, "organizer-system.png"))) oe.Save(f);
            }
        }

        // ---------------------------------------------------------------- item 4: system icons in the organizer
        static void SystemItemsWindowRun(Action<bool, string> check, string root, List<GlassWindow> opened)
        {
            var failed = SystemItems.All.Where(i => Native.Icon(i.Path) == null).Select(i => i.Name).ToList();
            check(failed.Count == 0, "system icons: every system icon has a real shell icon" + (failed.Count > 0 ? " — missing: " + string.Join(",", failed) : ""));
            var oldFull = SystemItems.RecycleBinFull; var oldFlag = SystemItems.DesktopFlag; var oldEmpty = SystemItems.EmptyAction;
            try
            {
                SystemItems.RecycleBinFull = () => false; var e = Native.Icon(SystemItems.RecycleBin.Path);
                SystemItems.RecycleBinFull = () => true; var f = Native.Icon(SystemItems.RecycleBin.Path);
                check(e != null && f != null && !IconBytes(e).SequenceEqual(IconBytes(f)), "system icons: the recycle bin icon differs between empty and full");
                SystemItems.DesktopFlag = c => null;
                var box = new BoxData { Title = "系统图标测试", Desktop = false, X = 40, Y = 40 };
                var w = new OrganizerWindow(box); w.Show(); opened.Add(w);
                check(!w.HasItem(SystemItems.Computer.Path) && w.TestSystemMenu != null && w.TestSystemMenu.Items.Count == 5, "organizer: the menu offers 添加系统图标 with five entries");
                w.SetSystemItem(SystemItems.Computer.Path, true); w.SetSystemItem(SystemItems.RecycleBin.Path, true); w.SetSystemItem(SystemItems.Computer.Path, true);
                check(w.HasItem(SystemItems.Computer.Path) && box.Items.Count == 2, "organizer: system icons are added once");
                check(w.TestGrid != null && SystemItems.All.Length == 5, "organizer: the grid holds the items");
                var menu = w.TestSystemMenu.Items.Cast<MenuItem>().ToList();
                check(menu.First(m => (string)m.Header == "此电脑").IsChecked && menu.First(m => (string)m.Header == "回收站").IsChecked && !menu.First(m => (string)m.Header == "网络").IsChecked, "organizer: the menu shows which system icons are in the box");
                int emptied = 0; SystemItems.EmptyAction = h => { emptied++; return true; }; SystemItems.RecycleBinFull = () => true;
                var binMenu = w.TestItemMenu(SystemItems.RecycleBin.Path); var emptyItem = binMenu.Items.OfType<MenuItem>().FirstOrDefault(m => (string)m.Header == "清空回收站");
                check(binMenu.Items.OfType<MenuItem>().Any(m => (string)m.Header == "打开") && emptyItem != null, "organizer: the recycle bin menu offers 打开 and 清空回收站");
                if (emptyItem != null) emptyItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                check(emptied == 1, "organizer: 清空回收站 asks Explorer to empty the bin");
                check(!w.TestItemMenu(SystemItems.Computer.Path).Items.OfType<MenuItem>().Any(m => (string)m.Header == "清空回收站" || (string)m.Header == "固定到 Dock"), "organizer: other system icons have no empty / pin entries");
                w.TestCheckRecycleBin(); SystemItems.RecycleBinFull = () => false; w.TestCheckRecycleBin();
                w.SetSystemItem(SystemItems.RecycleBin.Path, false); check(!w.HasItem(SystemItems.RecycleBin.Path) && box.Items.Count == 1, "organizer: a system icon can be removed again");
                w.Close(); opened.Remove(w);
                // the desktop collection: system icons shown on the desktop are collected, and can be hidden / restored
                SystemItems.DesktopFlag = c => c == SystemItems.Computer.Clsid || c == SystemItems.Network.Clsid ? (int?)0 : (c == SystemItems.RecycleBin.Clsid ? (int?)1 : null);
                var desk = new BoxData { Title = "桌面收纳测试", Desktop = true, X = 60, Y = 60 };
                var dw = new OrganizerWindow(desk); dw.Show(); opened.Add(dw);
                check(dw.HasItem(SystemItems.Computer.Path) && dw.HasItem(SystemItems.Network.Path) && !dw.HasItem(SystemItems.RecycleBin.Path) && WidgetManager.BoxCount(desk) >= 2, "organizer: 收集桌面 picks up the system icons that are shown on the desktop");
                dw.SetSystemItem(SystemItems.Computer.Path, false); check(!dw.HasItem(SystemItems.Computer.Path) && desk.Excluded.Contains(SystemItems.Computer.Path), "organizer: a collected system icon can be hidden from the box");
                dw.SetSystemItem(SystemItems.Computer.Path, true); check(dw.HasItem(SystemItems.Computer.Path) && !desk.Excluded.Contains(SystemItems.Computer.Path), "organizer: and shown again");
            }
            finally { SystemItems.RecycleBinFull = oldFull; SystemItems.DesktopFlag = oldFlag; SystemItems.EmptyAction = oldEmpty; }
        }
        static byte[] IconBytes(ImageSource source)
        {
            var bmp = source as BitmapSource; if (bmp == null) return new byte[0];
            var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0); var data = new byte[conv.PixelWidth * conv.PixelHeight * 4]; conv.CopyPixels(data, conv.PixelWidth * 4, 0); return data;
        }

        // ---------------------------------------------------------------- item 5: new widgets appear at the right-click position
        static void PlacementWindowRun(Action<bool, string> check, string root, List<GlassWindow> opened)
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            int px = screen.Left + 777, py = screen.Top + 355;
            // default (settings / 组件管理): the right-click is ignored
            WidgetPlacement.Record(px, py, WidgetPlacement.NowMs());
            var plain = Program.NewWidget("todo", PlaceMode.Default); opened.Add(plain);
            double scale = System.Windows.Media.VisualTreeHelper.GetDpi(plain).DpiScaleX;
            check(Math.Abs(plain.Left - px / scale) > 12 || Math.Abs(plain.Top - py / scale) > 12, "new widget (default): keeps the default position (" + Math.Round(plain.Left) + "," + Math.Round(plain.Top) + ")");
            plain.Close(); opened.Remove(plain); Program.S.Widgets.Remove("todo");
            WidgetPlacement.Record(px, py, WidgetPlacement.NowMs());
            var placed = Program.NewWidget("countdown", PlaceMode.LastRightClick); opened.Add(placed);
            double ex = px / scale, ey = py / scale;
            check(Math.Abs(placed.Left - ex) <= MagnetSnap.Threshold + .5 && Math.Abs(placed.Top - ey) <= MagnetSnap.Threshold + .5, "new widget (right-click): appears at the mouse position (" + Math.Round(placed.Left) + "," + Math.Round(placed.Top) + " for " + Math.Round(ex) + "," + Math.Round(ey) + ")");
            check(Math.Abs(placed.TestState.X - placed.Left) < .5 && Math.Abs(placed.TestState.Y - placed.Top) < .5, "new widget (right-click): the position is saved");
            // near the bottom-right corner: clamped into the work area
            placed.Close(); opened.Remove(placed);
            WidgetPlacement.Record(screen.Right - 3, screen.Bottom - 3, WidgetPlacement.NowMs());
            var corner = Program.NewWidget("countdown", PlaceMode.LastRightClick); opened.Add(corner);
            var wa = System.Windows.SystemParameters.WorkArea;
            check(corner.Left >= wa.Left - .5 && corner.Top >= wa.Top - .5 && corner.Left + corner.Width <= wa.Right + .5 && corner.Top + corner.Height <= wa.Bottom + .5, "new widget (right-click at the corner): stays inside the work area");
            corner.Close(); opened.Remove(corner);
            WidgetPlacement.Forget();
            var late = Program.NewWidget("countdown", PlaceMode.LastRightClick); opened.Add(late);
            check(Math.Abs(late.Left - (screen.Right - 3) / scale) > 30 || Math.Abs(late.Top - (screen.Bottom - 3) / scale) > 30, "new widget: an old (expired) right-click is ignored");
            late.Close(); opened.Remove(late);
        }
    }
}
