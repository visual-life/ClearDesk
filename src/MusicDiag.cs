using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Element = ClearDesk.MusicAccessibility.Element;

namespace ClearDesk
{
    // ClearDesk.exe --music-diag [outfile] : read-only diagnostics for NetEase lyrics / seek.
    internal static class MusicDiag
    {
        static int count; static StringBuilder log = new StringBuilder();
        static void W(string s) { log.AppendLine(s); }
        internal static void Run(string output)
        {
            string src = Environment.GetCommandLineArgs().Where(a => a.StartsWith("src=")).Select(a => a.Substring(4)).FirstOrDefault() ?? "netease";
            try
            {
                foreach (var p in Process.GetProcessesByName("cloudmusic"))
                    using (p) { try { W("proc " + p.Id + " hwnd=" + p.MainWindowHandle + " title=[" + p.MainWindowTitle + "] ver=" + p.MainModule.FileVersionInfo.FileVersion); } catch (Exception e) { W("proc " + p.Id + " err " + e.Message); } }
                var sessions = new MusicSessions();
                var snap = sessions.ReadAsync(src).GetAwaiter().GetResult();
                W("snapshot key=" + snap.Key + " src=" + snap.Source + " title=[" + snap.Title + "] artist=[" + snap.Artist + "] album=[" + snap.Album + "] pos=" + snap.Position + " dur=" + snap.Duration + " playing=" + snap.Playing + " canSeek=" + snap.CanSeek + " msg=" + snap.Message);
                if (Environment.GetCommandLineArgs().Contains("toggle") && snap.Key != null) W("toggle ok=" + sessions.CommandAsync(snap.Key, MusicCommand.Toggle).GetAwaiter().GetResult());
                int watch = Environment.GetCommandLineArgs().Where(a => a.StartsWith("watch=")).Select(a => int.Parse(a.Substring(6))).FirstOrDefault();
                for (var started = DateTime.Now; watch > 0 && (DateTime.Now - started).TotalSeconds < watch; Thread.Sleep(1000))
                {
                    var sw = Stopwatch.StartNew(); var v = sessions.ReadAsync(src).GetAwaiter().GetResult();
                    W(DateTime.Now.ToString("HH:mm:ss") + " ms=" + sw.ElapsedMilliseconds + " pos=" + v.Position + " dur=" + v.Duration + " playing=" + v.Playing + " title=[" + v.Title + "] peak=" + AudioPeak.Peak("cloudmusic").ToString("0.000") + " msg=" + v.Message);
                    File.WriteAllText(output, log.ToString(), Encoding.UTF8);
                }
                if (!string.IsNullOrWhiteSpace(snap.Title))
                {
                    W("canSeek=" + snap.CanSeek);
                    var client = new ClientLyrics().Read(snap, CancellationToken.None);
                    W("clientLyrics=" + (client == null ? "null" : client.Credit + " lines=" + client.Lines.Count));
                    var doc = new LyricsProvider().ReadAsync(snap, CancellationToken.None).GetAwaiter().GetResult();
                    W("provider: credit=" + doc.Credit + " msg=" + doc.Message + " lines=" + doc.Lines.Count + " first=[" + (doc.Lines.Count > 3 ? doc.Lines[3].Text : "") + "]");
                }
                if (Environment.GetCommandLineArgs().Contains("seek") && snap.Key != null && snap.Duration > TimeSpan.Zero)
                {
                    double at = Environment.GetCommandLineArgs().Where(a => a.StartsWith("at=")).Select(a => double.Parse(a.Substring(3), System.Globalization.CultureInfo.InvariantCulture)).FirstOrDefault(); if (at <= 0) at = 0.5;
                    var target = TimeSpan.FromSeconds(snap.Duration.TotalSeconds * at);
                    bool ok = sessions.SeekAsync(snap.Key, target).GetAwaiter().GetResult();
                    Thread.Sleep(1500);
                    var after = sessions.ReadAsync(src).GetAwaiter().GetResult();
                    W("seek to " + target + " ok=" + ok + " posAfter=" + after.Position + " qqinfo=" + QQMusicSeek.LastError + " peek=" + WindowPeek.Last); Thread.Sleep(1500); W("posLater=" + sessions.ReadAsync(src).GetAwaiter().GetResult().Position);
                }
                if (src == "qq")
                {
                    IntPtr qw; int qp; var bar = QQMusicSeek.FindBar(out qw, out qp);
                    W("qq bar=" + bar + " hwnd=" + qw + " pid=" + qp);
                    try { var on = new LyricsProvider().ReadQQOnline(snap, CancellationToken.None).GetAwaiter().GetResult(); W("qqOnline=" + (on == null ? "null" : on.Credit + " lines=" + on.Lines.Count)); } catch (Exception ex) { W("qqOnline err " + ex.Message); }
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Tencent\QQMusic\QQMusicCache\QQMusicLyricNew");
                    foreach (var f in Directory.EnumerateFiles(dir, "*_qm.qrc"))
                    {
                        bool m = ClientLyrics.QQMatches(snap, f);
                        if (!m && !f.Contains(snap.Title.Split(' ')[0])) continue;
                        W("qqfile [" + Path.GetFileName(f) + "] match=" + m);
                        try { var d = ClientLyrics.ParseQrc(ClientLyrics.QrcDecode(ClientLyrics.ReadBounded(f))); W("  decoded lines=" + d.Lines.Count); }
                        catch (Exception ex) { W("  decode err " + ex.GetType().Name + ": " + ex.Message); }
                    }
                    if (!Environment.GetCommandLineArgs().Contains("notree")) DumpQQ();
                }
                else if (!Environment.GetCommandLineArgs().Contains("notree")) DumpTree();
            }
            catch (Exception e) { W("EXCEPTION " + e); }
            File.WriteAllText(output, log.ToString(), new UTF8Encoding(true));
        }
        static void DumpQQ()
        {
            foreach (var p in Process.GetProcessesByName("QQMusic"))
                using (p)
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    W("qq hwnd=" + p.MainWindowHandle + " title=[" + p.MainWindowTitle + "]");
                    using (var ui = new MusicAccessibility()) { count = 0; Dump(ui, ui.Window(p.MainWindowHandle), 0); }
                    try
                    {
                        var ae = System.Windows.Automation.AutomationElement.FromHandle(p.MainWindowHandle);
                        var all = ae.FindAll(System.Windows.Automation.TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
                        W("== uia count=" + all.Count);
                        int n = 0;
                        foreach (System.Windows.Automation.AutomationElement e in all)
                        {
                            if (++n > 600) break;
                            var c = e.Current; var r = c.BoundingRectangle;
                            object rv; string range = e.TryGetCurrentPattern(System.Windows.Automation.RangeValuePattern.Pattern, out rv) ? " RANGE=" + ((System.Windows.Automation.RangeValuePattern)rv).Current.Value + "/" + ((System.Windows.Automation.RangeValuePattern)rv).Current.Maximum : "";
                            W("uia type=" + c.ControlType.ProgrammaticName + " name=[" + c.Name + "] id=[" + c.AutomationId + "] cls=[" + c.ClassName + "] rect=" + r + range);
                        }
                    }
                    catch (Exception ex) { W("uia err " + ex.Message); }
                    return;
                }
        }
        static void DumpTree()
        {
            foreach (var p in Process.GetProcessesByName("cloudmusic"))
                using (p)
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    using (var ui = new MusicAccessibility())
                    {
                        var root = ui.Window(p.MainWindowHandle);
                        var play = ui.FindPlayback(root);
                        W("play=" + (play == null ? "null" : MusicAccessibility.Name(play)));
                        if (play == null)
                        {
                            var rwhs = new System.Collections.Generic.List<IntPtr>();
                            EnumChildWindows(p.MainWindowHandle, (hw, _) => { var c = new StringBuilder(64); GetClassName(hw, c, 64); if (c.ToString() == "Chrome_RenderWidgetHostHWND") rwhs.Add(hw); return true; }, IntPtr.Zero);
                            Func<string> probe = () => string.Join(",", rwhs.Select(k => { using (var u = new MusicAccessibility()) { var d = u.Window(k); var ch = u.Children(d); return ch.Length + "/" + ch.Sum(x => u.Children(x).Length); } }));
                            W("probe0 " + probe());
                            foreach (var k in rwhs) { IntPtr r; SendMessageTimeout(k, 0x3D, IntPtr.Zero, (IntPtr)(-25), 2, 1000, out r); }
                            Thread.Sleep(1500); W("after WM_GETOBJECT(UiaRoot) " + probe());
                            foreach (var k in rwhs) { try { var ae = System.Windows.Automation.AutomationElement.FromHandle(k); var all = ae.FindAll(System.Windows.Automation.TreeScope.Children, System.Windows.Automation.Condition.TrueCondition); W("uia children=" + all.Count); } catch (Exception ex) { W("uia err " + ex.Message); } }
                            Thread.Sleep(2000); W("after UIA " + probe());
                            Thread.Sleep(3000); W("after wait " + probe());
                            using (var ui2 = new MusicAccessibility()) { var r2 = ui2.Window(p.MainWindowHandle); var p2 = ui2.FindPlayback(r2); W("play(retry)=" + (p2 == null ? "null" : MusicAccessibility.Name(p2))); }
                            W("== full tree"); return;
                            using (var ui3 = new MusicAccessibility()) { Dump(ui3, ui3.Window(p.MainWindowHandle), 0); }
                            var kids = new System.Collections.Generic.List<IntPtr>();
                            EnumChildWindows(p.MainWindowHandle, (hw, _) => { kids.Add(hw); return kids.Count < 40; }, IntPtr.Zero);
                            foreach (var k in kids)
                            {
                                var cls = new StringBuilder(256); GetClassName(k, cls, 256);
                                W("== child hwnd=" + k + " class=" + cls); count = 0;
                                using (var ui4 = new MusicAccessibility()) Dump(ui4, ui4.Window(k), 1);
                            }
                            return;
                        }
                        var region = ui.Parent(play);
                        for (int i = 0; i < 4 && region != null; i++) region = ui.Parent(region) ?? region;
                        Dump(ui, region, 0);
                        return;
                    }
                }
        }
        delegate bool CB(IntPtr w, IntPtr p);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, CB cb, IntPtr p);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetClassName(IntPtr w, StringBuilder s, int n);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr w, uint m, IntPtr wp, IntPtr lp, uint f, uint t, out IntPtr r);
        static void Dump(MusicAccessibility ui, Element e, int depth)
        {
            if (e == null || depth > 25 || ++count > 4000) return;
            int l, t, w, h; ui.Location(e, out l, out t, out w, out h);
            string value = ""; try { value = e.Accessible.get_accValue(e.Id) ?? ""; } catch { }
            object role = null; try { role = e.Accessible.get_accRole(e.Id); } catch { }
            W(new string(' ', depth * 2) + "role=" + role + " name=[" + MusicAccessibility.Name(e) + "] value=[" + value + "] rect=" + l + "," + t + "," + w + "x" + h);
            foreach (var c in ui.Children(e)) Dump(ui, c, depth + 1);
        }
    }
}
