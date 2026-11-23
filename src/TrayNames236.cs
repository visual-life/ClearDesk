using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ClearDesk
{
    /// <summary>One notification icon as Windows remembers it (HKCU\Control Panel\NotifyIconSettings): the program, the tooltip it first showed, whether it is on the taskbar, and a PNG of the icon.</summary>
    internal sealed class NotifyEntry
    {
        public string Exe, Tooltip, Description;
        public bool Promoted, Exists, Running;
        public byte[] Snapshot;
    }

    /// <summary>2.3.6: names for tray icons. UI Automation gives some icons (QQ) an EMPTY name; 2.3.3-2.3.5 dropped them from the list. Now every icon is kept; an icon without a name gets
    /// the program's name from Windows' own list of notification icons: the tooltip it first showed, else the exe's file description / product name, else the exe's file name.
    /// Which program an unnamed icon belongs to is found by elimination (programs that already have a named icon are excluded; the program must be running) and by comparing the picture
    /// of the icon with the PNG Windows keeps for each program.</summary>
    internal static class TrayNames
    {
        internal static Func<List<NotifyEntry>> Entries = ReadEntries;
        internal static readonly List<string> Report = new List<string>();
        internal const double MaxScore = 0.25, MinGap = 0.05;       // score (0 = same picture .. 1) of the 16x16 pictures; and how much better than the second-best candidate must be

        internal static string Friendly(NotifyEntry e)
        {
            string t = TrayAccess.Clean(e.Tooltip); if (t.Length > 0) return t;
            if (!string.IsNullOrWhiteSpace(e.Description)) return e.Description.Trim();
            return Path.GetFileNameWithoutExtension(e.Exe ?? "").Trim();
        }

        static bool Mentions(string named, string token) { return token != null && token.Length >= 3 && named.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0; }

        /// <summary>Is this program the owner of an icon that already has a name? (its tooltip, description or file name appears in that name)</summary>
        internal static bool HasNamedIcon(NotifyEntry e, IEnumerable<string> namedRaws)
        {
            string tip = TrayAccess.Clean(e.Tooltip), desc = (e.Description ?? "").Trim(), file = Path.GetFileNameWithoutExtension(e.Exe ?? "");
            foreach (var n in namedRaws)
            {
                if (n.Length == 0) continue;
                if (Mentions(n, tip) || Mentions(n, desc) || Mentions(n, file)) return true;
                if (tip.Length >= 3 && n.Length >= 3 && tip.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        static bool IsSystem(NotifyEntry e) { return string.Equals(Path.GetFileName(e.Exe ?? ""), "explorer.exe", StringComparison.OrdinalIgnoreCase); }

        /// <summary>Fills in Name / Exe / Snapshot of every item whose own name is empty. Items that cannot be identified get "未命名图标" (numbered when there are several).</summary>
        internal static void Resolve(List<TrayItem> items, List<NotifyEntry> entries, IEnumerable<string> alsoNamed = null)
        {
            lock (Report) Report.Clear();
            var unnamed = items.Where(i => string.IsNullOrEmpty(i.Raw)).ToList();
            if (unnamed.Count == 0) return;
            var named = items.Where(i => !string.IsNullOrEmpty(i.Raw)).Select(i => i.Raw).ToList(); if (alsoNamed != null) named.AddRange(alsoNamed);
            var cands = (entries ?? new List<NotifyEntry>()).Where(e => e.Exists && e.Running && !IsSystem(e) && !HasNamedIcon(e, named)).ToList();
            lock (Report) foreach (var c in cands) Report.Add("candidate: " + Friendly(c) + " | " + c.Exe + " | promoted=" + c.Promoted + " | snapshot=" + (c.Snapshot == null ? 0 : c.Snapshot.Length));
            var assigned = new HashSet<TrayItem>();
            // 1. by picture
            var pics = unnamed.Where(i => i.Capture != null).ToList();
            var scores = new Dictionary<TrayItem, Dictionary<NotifyEntry, double>>();
            foreach (var it in pics)
            {
                var d = new Dictionary<NotifyEntry, double>();
                foreach (var c in cands) if (c.Snapshot != null) { double s = Diff(it.Capture, c.Snapshot); if (s >= 0) d[c] = s; }
                scores[it] = d;
                lock (Report) foreach (var kv in d.OrderBy(k => k.Value)) Report.Add("score: item#" + it.Ordinal + " vs " + Friendly(kv.Key) + " = " + kv.Value.ToString("0.000"));
            }
            bool progress = true;
            while (progress)
            {
                progress = false;
                foreach (var it in pics.Where(p => !assigned.Contains(p)))
                {
                    var avail = scores[it].Where(kv => cands.Contains(kv.Key)).OrderBy(kv => kv.Value).ToList();
                    if (avail.Count == 0 || avail[0].Value > MaxScore) continue;
                    if (avail.Count > 1 && avail[1].Value - avail[0].Value < MinGap) continue;
                    Assign(it, avail[0].Key); assigned.Add(it); cands.Remove(avail[0].Key); progress = true; break;
                }
            }
            // 2. by elimination: one unnamed icon left and one program left that can own it
            var left = unnamed.Where(i => !assigned.Contains(i)).ToList();
            if (left.Count == 1 && cands.Count == 1) { Assign(left[0], cands[0]); assigned.Add(left[0]); left.Clear(); }
            // 3. nothing known
            int k2 = 0; foreach (var it in left) { k2++; it.Name = left.Count > 1 ? "未命名图标 " + k2 : "未命名图标"; it.Exe = null; }
        }

        static void Assign(TrayItem it, NotifyEntry e)
        {
            it.Name = Friendly(e); it.Exe = e.Exe; it.Snapshot = e.Snapshot;
            if (string.IsNullOrWhiteSpace(it.Tooltip)) it.Tooltip = it.Name;
        }

        // ---- pictures
        static BitmapSource Decode(byte[] png)
        {
            using (var ms = new MemoryStream(png))
            {
                BitmapSource f = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                if (f.Format != PixelFormats.Bgra32) f = new FormatConvertedBitmap(f, PixelFormats.Bgra32, null, 0);
                f.Freeze(); return f;
            }
        }

        static byte[] Pixels(BitmapSource s, double crop)
        {
            int cw = Math.Max(1, (int)Math.Round(s.PixelWidth * crop)), ch = Math.Max(1, (int)Math.Round(s.PixelHeight * crop));
            var c = new CroppedBitmap(s, new Int32Rect((s.PixelWidth - cw) / 2, (s.PixelHeight - ch) / 2, cw, ch));
            var t = new TransformedBitmap(c, new ScaleTransform(16.0 / cw, 16.0 / ch));
            var buf = new byte[16 * 16 * 4]; t.CopyPixels(buf, 16 * 4, 0); return buf;
        }

        /// <summary>Mean colour difference (0 = same, 1 = opposite) between the screen copy of an icon (the icon takes the middle ~70 %) and the PNG Windows keeps for a program; -1 when a picture cannot be read.</summary>
        internal static double Diff(byte[] captured, byte[] snapshot)
        {
            try
            {
                var cap = Decode(captured); var snap = Decode(snapshot);
                // the colour behind the icon: the average of the outer ring of the screen copy
                long r = 0, g = 0, b = 0, n = 0; var raw = new byte[cap.PixelWidth * cap.PixelHeight * 4]; cap.CopyPixels(raw, cap.PixelWidth * 4, 0);
                int ring = Math.Max(1, cap.PixelWidth / 8);
                for (int y = 0; y < cap.PixelHeight; y++) for (int x = 0; x < cap.PixelWidth; x++)
                    if (x < ring || y < ring || x >= cap.PixelWidth - ring || y >= cap.PixelHeight - ring) { int o = (y * cap.PixelWidth + x) * 4; b += raw[o]; g += raw[o + 1]; r += raw[o + 2]; n++; }
                double br = n == 0 ? 0 : (double)r / n, bg = n == 0 ? 0 : (double)g / n, bb = n == 0 ? 0 : (double)b / n;
                var a = Pixels(cap, 0.69); var p = Pixels(snap, 1.0);
                double sum = 0; var ga = new double[256]; var gp = new double[256];
                for (int i = 0; i < 256; i++)
                {
                    int o = i * 4; double al = p[o + 3] / 255.0;
                    double sr = p[o + 2] * al + br * (1 - al), sg = p[o + 1] * al + bg * (1 - al), sb = p[o] * al + bb * (1 - al);
                    sum += (Math.Abs(a[o + 2] - sr) + Math.Abs(a[o + 1] - sg) + Math.Abs(a[o] - sb)) / (3 * 255.0);
                    ga[i] = (a[o + 2] + a[o + 1] + a[o]) / 3.0; gp[i] = (sr + sg + sb) / 3.0;
                }
                double colour = sum / 256;
                // programs change their icon's colours (QQ: status / festival skins) but not its shape: the brightness pattern (correlation) counts as much as the colours
                double ma = ga.Average(), mp = gp.Average(), va = 0, vp = 0, cv = 0;
                for (int i = 0; i < 256; i++) { va += (ga[i] - ma) * (ga[i] - ma); vp += (gp[i] - mp) * (gp[i] - mp); cv += (ga[i] - ma) * (gp[i] - mp); }
                if (va / 256 < 25 || vp / 256 < 25) return colour;        // a flat picture has no shape to compare
                double corr = cv / Math.Sqrt(va * vp);
                return 0.5 * colour + 0.5 * (1 - corr) / 2;
            }
            catch { return -1; }
        }

        // ---- Windows' list of notification icons
        static List<NotifyEntry> cached; static DateTime cachedAt;
        [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
        [DllImport("ole32.dll")] static extern void CoTaskMemFree(IntPtr p);

        static string Resolve(string exe)
        {
            if (string.IsNullOrEmpty(exe) || exe[0] != '{') return exe;
            int close = exe.IndexOf('}'); Guid g;
            if (close < 0 || !Guid.TryParse(exe.Substring(0, close + 1), out g)) return exe;
            IntPtr p = IntPtr.Zero;
            try { if (SHGetKnownFolderPath(g, 0, IntPtr.Zero, out p) != 0 || p == IntPtr.Zero) return exe; return Path.Combine(Marshal.PtrToStringUni(p), exe.Substring(close + 1).TrimStart('\\')); }
            catch { return exe; }
            finally { if (p != IntPtr.Zero) CoTaskMemFree(p); }
        }

        static HashSet<string> RunningPaths()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                try { string f = p.MainModule.FileName; if (!string.IsNullOrEmpty(f)) set.Add(f); } catch { }
                finally { p.Dispose(); }
            }
            return set;
        }

        internal static List<NotifyEntry> ReadEntries() { return ReadEntries(false); }

        internal static List<NotifyEntry> ReadEntries(bool refresh)
        {
            lock (Report)
            {
                if (!refresh && cached != null && (DateTime.UtcNow - cachedAt).TotalSeconds < 30) return cached;
            }
            var list = new List<NotifyEntry>();
            try
            {
                var running = RunningPaths();
                using (var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
                {
                    if (root != null)
                        foreach (var name in root.GetSubKeyNames())
                            using (var k = root.OpenSubKey(name))
                            {
                                if (k == null) continue;
                                string exe = Resolve(k.GetValue("ExecutablePath") as string);
                                if (string.IsNullOrEmpty(exe)) continue;
                                var e = new NotifyEntry { Exe = exe, Tooltip = k.GetValue("InitialTooltip") as string ?? "" };
                                var promoted = k.GetValue("IsPromoted"); e.Promoted = promoted is int && (int)promoted == 1;
                                try { e.Exists = File.Exists(exe); } catch { }
                                e.Running = e.Exists && running.Contains(exe);
                                if (e.Running)
                                {
                                    e.Snapshot = k.GetValue("IconSnapshot") as byte[];
                                    try { var v = FileVersionInfo.GetVersionInfo(exe); e.Description = !string.IsNullOrWhiteSpace(v.FileDescription) ? v.FileDescription : v.ProductName; } catch { }
                                }
                                list.Add(e);
                            }
                }
            }
            catch (Exception ex) { Program.Log(ex); }
            lock (Report) { cached = list; cachedAt = DateTime.UtcNow; }
            return list;
        }

        // ---- diagnostics: --tray-diag236 <file>: what the new code sees (reads only; the hidden icons are read by opening the flyout for a moment, exactly as the 托盘图标 page does)
        internal static void RunDiag(string[] args)
        {
            string path = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "cd236-tray-diag.txt");
            var sb = new StringBuilder();
            if (args.Length > 4 && args[2] == "score")       // --tray-diag236 <out> score <capture.png> <snapshot.png>: the picture score of two files
            {
                var one = new System.Threading.Thread(() => sb.AppendLine("score=" + Diff(File.ReadAllBytes(args[3]), File.ReadAllBytes(args[4])).ToString("0.000"))); one.SetApartmentState(System.Threading.ApartmentState.STA); one.Start(); one.Join();
                File.WriteAllText(path, sb.ToString()); return;
            }
            if (args.Length > 4 && args[2] == "replay")      // --tray-diag236 <out> replay <capture.png> <name prefix>: the live hidden icons, but the icon whose name starts with the prefix is given an EMPTY name and the capture (what 2.3.5 dropped)
            {
                var rp = new System.Threading.Thread(() =>
                {
                    var raws = new List<TrayAccess.RawIcon>(); byte[] cap = File.ReadAllBytes(args[3]);
                    foreach (var i in TrayAccess.ReadOverflow(false)) raws.Add(i.Raw.StartsWith(args[4]) ? new TrayAccess.RawIcon { Full = "", Png = cap } : new TrayAccess.RawIcon { Full = i.Raw });
                    var built = TrayAccess.Build(raws, true, null);
                    sb.AppendLine("== replay: " + built.Count);
                    foreach (var i in built) sb.AppendLine(string.Format("  raw='{0}' name='{1}' ord={2} exe={3}", i.Raw, i.Name, i.Ordinal, i.Exe));
                    lock (Report) foreach (var l in Report) sb.AppendLine("    " + l);
                }); rp.SetApartmentState(System.Threading.ApartmentState.STA); rp.Start(); rp.Join(60000);
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false)); return;
            }
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    var vis = TrayAccess.ReadVisible(true);
                    sb.AppendLine("== visible: " + vis.Count);
                    foreach (var i in vis) sb.AppendLine(string.Format("  raw='{0}' name='{1}' ord={2} exe={3} png={4}", i.Raw, i.Name, i.Ordinal, i.Exe, i.Png == null ? 0 : i.Png.Length));
                    lock (Report) foreach (var l in Report) sb.AppendLine("    " + l);
                    var hid = TrayAccess.ReadOverflow(true);
                    sb.AppendLine("== hidden: " + hid.Count + (TrayAccess.LastProblem != null ? " (" + TrayAccess.LastProblem + ")" : ""));
                    foreach (var i in hid) sb.AppendLine(string.Format("  raw='{0}' name='{1}' ord={2} exe={3} png={4}", i.Raw, i.Name, i.Ordinal, i.Exe, i.Png == null ? 0 : i.Png.Length));
                    lock (Report) foreach (var l in Report) sb.AppendLine("    " + l);
                    if (args.Length > 2 && args[2] == "dump")      // pictures for calibrating the matching
                    {
                        foreach (var i in hid.Where(x => x.Raw.Length == 0 && x.Capture != null)) File.WriteAllBytes(path + ".cap" + i.Ordinal + ".png", i.Capture);
                        int k = 0; foreach (var e in Entries().Where(x => x.Running && x.Snapshot != null)) File.WriteAllBytes(path + ".snap-" + Path.GetFileNameWithoutExtension(e.Exe) + ".png", e.Snapshot);
                    }
                }
                catch (Exception e) { sb.AppendLine("ERROR " + e); }
            });
            t.SetApartmentState(System.Threading.ApartmentState.STA); t.Start(); t.Join(60000);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
