using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ClearDesk
{
    /// <summary>
    /// 2.2.4: reading and writing settings.json so that a restart never turns into "everything is back to default".
    /// Read: shared (a scanner / sync tool holding the file open is no reason to give up), retried, and falling back to settings.json.bak / .tmp; a file that exists but cannot be read
    /// is NEVER replaced by defaults (Blocked: nothing is written until it can be read again).
    /// Write: temp file, flushed to disk, atomic replace, retried for sharing violations AND access-denied, validated first (a file that could not be read back is never written).
    /// </summary>
    internal static class ConfigStore
    {
        internal static int ReadRetries = 12, ReadDelayMs = 250, WriteRetries = 8;
        /// <summary>Test hook: called before every write attempt (attempt number) — may throw to simulate a lock.</summary>
        internal static Action<int> BeforeWrite;
        /// <summary>Test hook: called before every read attempt of a path — may throw to simulate a lock.</summary>
        internal static Action<string, int> BeforeRead;

        internal sealed class LoadResult
        {
            public Settings S;            // null = nothing usable
            public string Source;         // the file it came from
            public bool Existed;          // settings.json existed
            public bool Blocked;          // it exists but could not be read (locked): the caller must not overwrite it
            public bool Corrupt;          // it exists, was read, and is not valid
            public string Note = "";
        }

        internal static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 200 }; }

        /// <summary>null when the file does not exist; throws the last IO error when it stays unreadable.</summary>
        internal static string ReadShared(string path, int retries, int delayMs)
        {
            for (int i = 0; ; i++)
            {
                try
                {
                    if (BeforeRead != null) BeforeRead(path, i);
                    if (!File.Exists(path)) return null;
                    using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var r = new StreamReader(f, new UTF8Encoding(false), true))
                        return r.ReadToEnd();
                }
                catch (FileNotFoundException) { return null; }
                catch (DirectoryNotFoundException) { return null; }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    if (i >= retries) throw;
                    Thread.Sleep(Math.Min(1000, delayMs * (i + 1) / 2 + 20));
                }
            }
        }

        static Settings Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return Json().Deserialize<Settings>(text);
        }

        internal static LoadResult Load(string config)
        {
            var r = new LoadResult();
            string primaryError = null;
            var candidates = new[] { config, config + ".bak", config + ".tmp" };
            for (int c = 0; c < candidates.Length; c++)
            {
                string path = candidates[c];
                string text;
                try { text = ReadShared(path, c == 0 ? ReadRetries : 2, ReadDelayMs); }
                catch (Exception e)
                {
                    if (c == 0) { r.Existed = true; r.Blocked = true; primaryError = e.Message; }
                    continue;
                }
                if (text == null) continue;
                if (c == 0) r.Existed = true;
                Settings s = null;
                try { s = Parse(text); } catch (Exception e) { if (c == 0) { r.Corrupt = true; primaryError = e.Message; } }
                if (s != null)
                {
                    r.S = s; r.Source = path;
                    if (c == 0) { r.Blocked = false; r.Corrupt = false; }
                    else r.Note = "settings.json " + (r.Blocked ? "暂时读不了（被占用）" : "已损坏或为空") + "，已从 " + Path.GetFileName(path) + " 恢复" + (primaryError != null ? "：" + primaryError : "");
                    return r;
                }
                if (c == 0 && !r.Corrupt && string.IsNullOrWhiteSpace(text)) r.Corrupt = true;   // zero bytes: the classic result of a power loss during a write
            }
            if (r.Blocked) r.Note = "settings.json 被其他程序占用，暂时读不了：" + primaryError + "。ClearDesk 先用默认设置运行，但不会改写这个文件，稍后会自动重试。";
            else if (r.Corrupt) r.Note = "settings.json 已损坏（" + primaryError + "），没有可用的备份，使用默认设置。";
            return r;
        }

        /// <summary>Numbers that JSON cannot carry (NaN, ±Infinity) are replaced; returns true when something was fixed.</summary>
        internal static bool Sanitize(Settings s)
        {
            bool fixedAny = false;
            Action<BoxData, double, double, double, double> box = (b, x, y, w, h) =>
            {
                if (b == null) return;
                if (Bad(b.X) || Bad(b.Y)) { b.X = x; b.Y = y; fixedAny = true; }
                if (Bad(b.Width) || b.Width <= 0) { b.Width = w; fixedAny = true; }
                if (Bad(b.Height) || b.Height <= 0) { b.Height = h; fixedAny = true; }
                if (Bad(b.Scale)) { b.Scale = 1; fixedAny = true; }
                if (Bad(b.CustomW)) { b.CustomW = 0; fixedAny = true; }
                if (Bad(b.CustomH)) { b.CustomH = 0; fixedAny = true; }
                if (Bad(b.BgOpacity)) { b.BgOpacity = 1; fixedAny = true; }
                if (Bad(b.BgDim)) { b.BgDim = .25; fixedAny = true; }
            };
            foreach (var b in s.Boxes) box(b, 70, 100, 420, 300);
            foreach (var b in s.Notes) box(b, 70, 100, 300, 300);
            var bad = new List<string>();
            foreach (var kv in s.Widgets)
            {
                var b = kv.Value; if (b == null) { bad.Add(kv.Key); continue; }
                if (Bad(b.X) || Bad(b.Y) || Bad(b.Width) || Bad(b.Height) || b.Width <= 0 || b.Height <= 0) { bad.Add(kv.Key); continue; }   // recreated at its default place
                box(b, b.X, b.Y, b.Width, b.Height);
            }
            foreach (var k in bad) { s.Widgets.Remove(k); fixedAny = true; }
            if (Bad(s.DockOffset)) { s.DockOffset = 12; fixedAny = true; }
            if (Bad(s.ManualLat)) { s.ManualLat = 0; fixedAny = true; }
            if (Bad(s.ManualLon)) { s.ManualLon = 0; fixedAny = true; }
            return fixedAny;
        }
        static bool Bad(double v) { return double.IsNaN(v) || double.IsInfinity(v) || Math.Abs(v) > 1e7; }

        /// <summary>Old files: widget keys written with another case ("Clock"), duplicates, missing collections. Keys end up lower-case and case-insensitive.</summary>
        internal static void Migrate(Settings s)
        {
            var d = new Dictionary<string, BoxData>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in s.Widgets)
            {
                if (kv.Value == null) continue;
                BoxData existing;
                if (!d.TryGetValue(kv.Key, out existing) || kv.Key == kv.Key.ToLowerInvariant()) d[kv.Key.ToLowerInvariant()] = kv.Value;
            }
            s.Widgets = d;
        }

        /// <summary>
        /// Writes the settings. Returns true when the file on disk is now the new content. Never throws. Refuses to write something that could not be read back.
        /// </summary>
        internal static bool Write(string config, Settings s, out string error)
        {
            error = null;
            string json;
            try
            {
                json = Json().Serialize(s);
                try { Json().Deserialize<Settings>(json); }
                catch
                {
                    Sanitize(s);                                  // NaN / Infinity slipped in: fix them and try once more
                    json = Json().Serialize(s);
                    Json().Deserialize<Settings>(json);
                }
            }
            catch (Exception e) { error = "设置无法序列化：" + e.Message; return false; }
            string dir = Path.GetDirectoryName(config);
            string temp = config + ".tmp";
            byte[] bytes = new UTF8Encoding(false).GetBytes(json);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (BeforeWrite != null) BeforeWrite(attempt);
                    Directory.CreateDirectory(dir);
                    using (var f = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        f.Write(bytes, 0, bytes.Length);
                        f.Flush(true);                              // really on disk before it replaces the old file (a power loss must not leave an empty settings.json)
                    }
                    if (File.Exists(config)) File.Replace(temp, config, config + ".bak");
                    else File.Move(temp, config);
                    return true;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    if (attempt >= WriteRetries - 1) { error = e.Message; return false; }
                    Thread.Sleep(Math.Min(800, 40 * (attempt + 1) * (attempt + 1)));
                }
            }
        }
    }
}
