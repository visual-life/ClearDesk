using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace ClearDesk
{
    internal enum ComponentState { Ok, NoManifest, Missing, Mismatch, Busy, Invalid }

    internal sealed class ComponentCheck
    {
        public ComponentState State;
        public string File;     // the file the problem is about
        public string Detail;
        public bool Ok { get { return State == ComponentState.Ok; } }
        public string Text
        {
            get
            {
                switch (State)
                {
                    case ComponentState.Ok: return "正常";
                    case ComponentState.NoManifest: return "缺少组件校验清单";
                    case ComponentState.Missing: return "组件文件缺失或被安全软件移走：" + File;
                    case ComponentState.Mismatch: return "组件文件与校验清单不一致：" + File;
                    case ComponentState.Busy: return "组件文件暂时被占用：" + File;
                    default: return "组件校验清单无效";
                }
            }
        }
    }

    /// <summary>
    /// 2.2.3: the optional "transparent taskbar" component (TranslucentTB) is verified on demand and NEVER blocks ClearDesk from starting.
    /// Verify = compare SHA-256 with verified.sha256; Repair = restore from a known-good copy kept in the data folder; otherwise the caller turns the feature off with a notice and retries later.
    /// </summary>
    internal static class TaskbarComponent
    {
        internal static string CacheDir { get { return Path.Combine(Program.Data, "taskbar-good"); } }
        internal static int BusyRetries = 4, BusyDelayMs = 250;
        /// <summary>Test hook: hash of a file (default reads it shared, so a running or scanned file is still readable).</summary>
        internal static Func<string, string> HashFile = Sha;

        static string Sha(string path)
        {
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "");
        }

        static string HashRetry(string path, out ComponentState bad)
        {
            bad = ComponentState.Ok;
            for (int i = 0; ; i++)
            {
                try { return HashFile(path); }
                catch (FileNotFoundException) { bad = ComponentState.Missing; return null; }
                catch (DirectoryNotFoundException) { bad = ComponentState.Missing; return null; }
                catch (IOException) { if (i >= BusyRetries) { bad = ComponentState.Busy; return null; } Thread.Sleep(BusyDelayMs); }
                catch (UnauthorizedAccessException) { if (i >= BusyRetries) { bad = ComponentState.Busy; return null; } Thread.Sleep(BusyDelayMs); }
            }
        }

        /// <summary>Manifest lines "SHA256  name" -> list; null when the manifest is missing/unreadable/invalid.</summary>
        internal static List<KeyValuePair<string, string>> ReadManifest(string dir, out ComponentState bad)
        {
            bad = ComponentState.Ok;
            string manifest = Path.Combine(dir, "verified.sha256");
            string[] lines;
            try
            {
                if (!File.Exists(manifest)) { bad = ComponentState.NoManifest; return null; }
                lines = File.ReadAllLines(manifest);
            }
            catch { bad = ComponentState.NoManifest; return null; }
            var list = new List<KeyValuePair<string, string>>();
            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Length < 67 || line[64] != ' ' || line[65] != ' ') { bad = ComponentState.Invalid; return null; }
                string name = line.Substring(66).Trim();
                if (name.Length == 0 || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) { bad = ComponentState.Invalid; return null; }
                list.Add(new KeyValuePair<string, string>(line.Substring(0, 64), name));
            }
            if (list.Count == 0) { bad = ComponentState.Invalid; return null; }
            return list;
        }

        public static ComponentCheck Verify(string dir)
        {
            ComponentState bad;
            var list = ReadManifest(dir, out bad);
            if (list == null) return new ComponentCheck { State = bad, File = "verified.sha256" };
            foreach (var e in list)
            {
                string h = HashRetry(Path.Combine(dir, e.Value), out bad);
                if (h == null) return new ComponentCheck { State = bad, File = e.Value };
                if (!h.Equals(e.Key, StringComparison.OrdinalIgnoreCase)) return new ComponentCheck { State = ComponentState.Mismatch, File = e.Value };
            }
            return new ComponentCheck { State = ComponentState.Ok };
        }

        /// <summary>Keep a verified copy so a damaged, replaced or quarantined component can be restored later.</summary>
        public static void Remember(string dir, string cache)
        {
            try
            {
                if (string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(cache).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
                if (Verify(cache).Ok && SameManifest(dir, cache)) return;
                CopyTree(dir, cache, false);
            }
            catch (Exception e) { Program.Log(e); }
        }

        static bool SameManifest(string a, string b)
        {
            try { return File.ReadAllText(Path.Combine(a, "verified.sha256")) == File.ReadAllText(Path.Combine(b, "verified.sha256")); } catch { return false; }
        }

        /// <summary>Restore every file of the known-good copy that is missing or different. Locked/loaded files are renamed aside first.</summary>
        public static bool Repair(string dir, string cache, out string how)
        {
            how = null;
            var good = Verify(cache);
            if (!good.Ok) { how = "没有可用的完好备份"; return false; }
            try { CopyTree(cache, dir, true); }
            catch (Exception e) { how = "还原失败：" + e.Message; return false; }
            how = "已从备份还原组件";
            return true;
        }

        static void CopyTree(string from, string to, bool onlyDifferent)
        {
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from))
            {
                if (f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && onlyDifferent == false) continue;
                string dest = Path.Combine(to, Path.GetFileName(f));
                if (File.Exists(dest))
                {
                    if (onlyDifferent && SameFile(f, dest)) continue;
                    string aside = dest + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                    try { File.Move(dest, aside); } catch { try { File.Delete(dest); } catch { } }
                }
                File.Copy(f, dest, true);
            }
            foreach (string d in Directory.GetDirectories(from))
                CopyTree(d, Path.Combine(to, Path.GetFileName(d)), onlyDifferent);
            // leftovers of earlier repairs (best effort)
            foreach (string old in Directory.GetFiles(to, "*.bad-*")) { try { File.Delete(old); } catch { } }
        }

        static bool SameFile(string a, string b)
        {
            try { var x = new FileInfo(a); var y = new FileInfo(b); return x.Length == y.Length && HashFile(a) == HashFile(b); } catch { return false; }
        }

        /// <summary>Verify; if that fails try to repair; always returns instead of throwing. note describes what happened (null when nothing noteworthy).</summary>
        public static ComponentCheck Ensure(string dir, string cache, out string note)
        {
            note = null;
            var c = Verify(dir);
            if (c.Ok) { Remember(dir, cache); return c; }
            if (c.State == ComponentState.Busy) { note = c.Text; return c; }   // transient: try again later, do not touch
            string how;
            if (Repair(dir, cache, out how))
            {
                var again = Verify(dir);
                if (again.Ok) { note = how; return again; }
                c = again;
            }
            note = c.Text + (how != null ? "（" + how + "）" : "");
            return c;
        }
    }
}
