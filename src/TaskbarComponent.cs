using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ClearDesk
{
    internal enum ComponentState { Ok, NoManifest, Missing, Mismatch, Busy, Invalid }

    /// <summary>One file of the component as it was found (2.2.4 diagnostics).</summary>
    internal sealed class ComponentFile
    {
        public string Name, Expected, Actual, Error, Version, Signature;
        public long Size = -1;
        public DateTime Mtime;
        public ComponentState State = ComponentState.Ok;
        public string Attributes = "";
        public bool Zone;
    }

    internal sealed class ComponentCheck
    {
        public ComponentState State;
        public string File;     // the file the problem is about
        public string Detail;
        public string Dir;      // the folder that was checked (2.2.4: may be the per-user copy after a fallback)
        public string Source = "";   // where the expected hashes came from: "出厂副本" (embedded) or "校验清单文件"
        public List<ComponentFile> Files = new List<ComponentFile>();
        public bool UnverifiedAllowed;   // started although the hash differs: allowed by the user's option, same version and a valid signature
        public bool Ok { get { return State == ComponentState.Ok; } }
        /// <summary>Short code shown in the notice and logged: look it up in the 先读我 / release notes.</summary>
        public string Code
        {
            get
            {
                switch (State)
                {
                    case ComponentState.Ok: return UnverifiedAllowed ? "TB-09" : "TB-00";
                    case ComponentState.NoManifest: return "TB-01";
                    case ComponentState.Missing: return "TB-02";
                    case ComponentState.Mismatch: return "TB-03";
                    case ComponentState.Busy: return "TB-04";
                    default: return "TB-05";
                }
            }
        }
        public string Text
        {
            get
            {
                switch (State)
                {
                    case ComponentState.Ok: return UnverifiedAllowed ? "按你的设置使用了未通过哈希校验的组件：" + File : "正常";
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
    /// 2.2.4: the factory copy of the transparent-taskbar component, compiled into ClearDesk.exe (resource ClearDesk.taskbar.zip, made by build.ps1 from the verified
    /// TranslucentTB package). Its manifest is the one thing that decides "is this file the shipped one" — a stale or hand-copied verified.sha256 in the folder can no longer
    /// make a good component look bad, and a damaged folder can always be repaired from here without any download or backup.
    /// </summary>
    internal sealed class ComponentPack
    {
        internal const string ResourceName = "ClearDesk.taskbar.zip";
        readonly byte[] zip;
        Dictionary<string, byte[]> files;
        List<KeyValuePair<string, string>> manifest;
        string version = "";
        public string Version { get { try { Load(); } catch { } return version; } }
        public ComponentPack(byte[] zipBytes) { zip = zipBytes; }

        static ComponentPack embedded; static bool embeddedLoaded;
        /// <summary>Test hook; null = the compiled-in pack (when this build has one).</summary>
        internal static Func<ComponentPack> Provider;
        public static ComponentPack Current
        {
            get
            {
                if (Provider != null) return Provider();
                if (!embeddedLoaded)
                {
                    embeddedLoaded = true;
                    try
                    {
                        using (var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
                            if (s != null) { var ms = new MemoryStream(); s.CopyTo(ms); embedded = new ComponentPack(ms.ToArray()); }
                    }
                    catch (Exception e) { Program.Log(e); }
                }
                return embedded;
            }
        }

        public static ComponentPack FromFolder(string dir)
        {
            var ms = new MemoryStream();
            using (var za = new ZipArchive(ms, ZipArchiveMode.Create, true))
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    string rel = f.Substring(dir.TrimEnd('\\').Length + 1).Replace('\\', '/');
                    if (rel.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    var e = za.CreateEntry(rel, CompressionLevel.Optimal);
                    using (var o = e.Open()) { var b = File.ReadAllBytes(f); o.Write(b, 0, b.Length); }
                }
            return new ComponentPack(ms.ToArray());
        }

        Dictionary<string, byte[]> Load()
        {
            if (files != null) return files;
            var d = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            using (var za = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
                foreach (var e in za.Entries)
                {
                    if (e.FullName.EndsWith("/")) continue;
                    using (var s = e.Open()) { var ms = new MemoryStream(); s.CopyTo(ms); d[e.FullName.Replace('\\', '/')] = ms.ToArray(); }
                }
            files = d;
            byte[] v; if (d.TryGetValue("version.txt", out v)) version = Encoding.UTF8.GetString(v).Trim();
            return d;
        }

        public IEnumerable<string> Names { get { return Load().Keys; } }
        public byte[] Get(string rel) { byte[] b; return Load().TryGetValue(rel.Replace('\\', '/'), out b) ? b : null; }

        /// <summary>The pack's own manifest (SHA-256, name); null when it has none or it is unreadable.</summary>
        public List<KeyValuePair<string, string>> Manifest
        {
            get
            {
                if (manifest != null) return manifest;
                try
                {
                    var b = Get("verified.sha256"); if (b == null) return null;
                    ComponentState bad; manifest = TaskbarComponent.ParseManifest(Encoding.UTF8.GetString(b).Split(new[] { '\n' }, StringSplitOptions.None), out bad);
                }
                catch { manifest = null; }
                return manifest;
            }
        }

        /// <summary>Do the pack's files match the pack's own manifest? A damaged pack must never be used to "repair" anything.</summary>
        public bool SelfCheck()
        {
            var m = Manifest; if (m == null) return false;
            foreach (var e in m)
            {
                var b = Get(e.Value); if (b == null) return false;
                if (!TaskbarComponent.HashBytes(b).Equals(e.Key, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
    }

    internal static class Authenticode
    {
        [StructLayout(LayoutKind.Sequential)] struct WINTRUST_FILE_INFO { public int cbStruct; [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath; public IntPtr hFile, pgKnownSubject; }
        [StructLayout(LayoutKind.Sequential)] struct WINTRUST_DATA { public int cbStruct; public IntPtr pPolicyCallbackData, pSIPClientData; public int dwUIChoice, fdwRevocationChecks, dwUnionChoice; public IntPtr pFile; public int dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference; public int dwProvFlags, dwUIContext; public IntPtr pSignatureSettings; }
        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)] static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

        /// <summary>"有效" / "无签名" / "无效(0x…)" — checked offline (no revocation lookup, no UI).</summary>
        public static string Status(string path, out string signer)
        {
            signer = "";
            try
            {
                try { var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path)); signer = cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false); } catch { }
                var fi = new WINTRUST_FILE_INFO { pcwszFilePath = path };
                fi.cbStruct = Marshal.SizeOf(typeof(WINTRUST_FILE_INFO));
                IntPtr p = Marshal.AllocHGlobal(fi.cbStruct);
                try
                {
                    Marshal.StructureToPtr(fi, p, false);
                    var d = new WINTRUST_DATA { dwUIChoice = 2, fdwRevocationChecks = 0, dwUnionChoice = 1, pFile = p, dwStateAction = 0, dwProvFlags = 0x00000010 | 0x00000100 };   // no UI; cache-only / no network
                    d.cbStruct = Marshal.SizeOf(typeof(WINTRUST_DATA));
                    var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
                    int r = WinVerifyTrust(new IntPtr(-1), ref action, ref d);
                    if (r == 0) return "有效";
                    if (unchecked((uint)r) == 0x800B0100) return "无签名";
                    return "无效(0x" + r.ToString("X8") + ")";
                }
                finally { Marshal.FreeHGlobal(p); }
            }
            catch (Exception e) { return "无法检查(" + e.GetType().Name + ")"; }
        }
    }

    /// <summary>
    /// The optional "transparent taskbar" component (TranslucentTB) is verified on demand and NEVER blocks ClearDesk from starting.
    /// 2.2.4: verify against the factory copy compiled into ClearDesk.exe (the folder's verified.sha256 is only used by builds without one), repair from the kept good copy
    /// or from the factory copy, and fall back to a per-user copy; every failure carries a code and the per-file facts.
    /// </summary>
    internal static class TaskbarComponent
    {
        internal static string CacheDir { get { return Path.Combine(Program.Data, "taskbar-good"); } }
        internal static string RunDir { get { return Path.Combine(Program.Data, "taskbar-run"); } }
        internal static int BusyRetries = 4, BusyDelayMs = 250;
        /// <summary>Test hook: hash of a file (default reads it shared, so a running or scanned file is still readable).</summary>
        internal static Func<string, string> HashFile = Sha;

        static string Sha(string path)
        {
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "");
        }

        internal static string HashBytes(byte[] b) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(b)).Replace("-", ""); }

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

        /// <summary>Manifest lines "SHA256  name" -> list; null when invalid. Accepts CRLF/LF, a BOM, blank lines.</summary>
        internal static List<KeyValuePair<string, string>> ParseManifest(IEnumerable<string> lines, out ComponentState bad)
        {
            bad = ComponentState.Ok;
            var list = new List<KeyValuePair<string, string>>();
            foreach (string raw in lines)
            {
                string line = raw.TrimEnd('\r', '\n').TrimStart('\uFEFF');
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Length < 67 || line[64] != ' ' || line[65] != ' ') { bad = ComponentState.Invalid; return null; }
                string name = line.Substring(66).Trim();
                if (name.Length == 0 || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) { bad = ComponentState.Invalid; return null; }
                list.Add(new KeyValuePair<string, string>(line.Substring(0, 64), name));
            }
            if (list.Count == 0) { bad = ComponentState.Invalid; return null; }
            return list;
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
                using (var f = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(f, Encoding.UTF8, true))
                    lines = r.ReadToEnd().Split('\n');
            }
            catch { bad = ComponentState.NoManifest; return null; }
            return ParseManifest(lines, out bad);
        }

        /// <summary>The hashes a folder must match: the factory copy when this build has one, otherwise the folder's own manifest.</summary>
        internal static List<KeyValuePair<string, string>> Expected(string dir, ComponentPack pack, out ComponentState bad, out string source)
        {
            bad = ComponentState.Ok; source = "校验清单文件";
            if (pack != null)
            {
                var m = pack.Manifest;
                if (m != null) { source = "出厂副本"; return m; }
            }
            return ReadManifest(dir, out bad);
        }

        /// <summary>Folder manifest only (builds / tests without a factory copy).</summary>
        public static ComponentCheck Verify(string dir) { return Verify(dir, null); }

        public static ComponentCheck Verify(string dir, ComponentPack pack)
        {
            ComponentState bad; string source;
            var list = Expected(dir, pack, out bad, out source);
            if (list == null) return new ComponentCheck { State = bad, File = "verified.sha256", Dir = dir, Source = source };
            var res = new ComponentCheck { State = ComponentState.Ok, Dir = dir, Source = source };
            foreach (var e in list)
            {
                var f = new ComponentFile { Name = e.Value, Expected = e.Key.ToUpperInvariant() };
                string path = Path.Combine(dir, e.Value);
                string h = HashRetry(path, out bad);
                if (h == null) { f.State = bad; f.Error = bad == ComponentState.Missing ? "文件不存在" : "文件被占用或无法读取"; }
                else { f.Actual = h.ToUpperInvariant(); if (!f.Actual.Equals(f.Expected, StringComparison.OrdinalIgnoreCase)) f.State = ComponentState.Mismatch; }
                res.Files.Add(f);
                if (f.State != ComponentState.Ok && res.State == ComponentState.Ok) { res.State = f.State; res.File = f.Name; }
            }
            return res;
        }

        /// <summary>Keep a verified copy so a damaged, replaced or quarantined component can be restored later.</summary>
        public static void Remember(string dir, string cache) { Remember(dir, cache, null); }
        public static void Remember(string dir, string cache, ComponentPack pack)
        {
            try
            {
                if (string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(cache).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
                if (Verify(cache, pack).Ok && SameManifest(dir, cache)) return;
                CopyTree(dir, cache, false);
            }
            catch (Exception e) { Program.Log(e); }
        }

        static bool SameManifest(string a, string b)
        {
            try { return File.ReadAllText(Path.Combine(a, "verified.sha256")) == File.ReadAllText(Path.Combine(b, "verified.sha256")); } catch { return false; }
        }

        /// <summary>Restore every file of the known-good copy that is missing or different. Locked/loaded files are renamed aside first.</summary>
        public static bool Repair(string dir, string cache, out string how) { return Repair(dir, cache, null, out how); }

        public static bool Repair(string dir, string cache, ComponentPack pack, out string how)
        {
            how = null;
            var good = cache == null ? null : Verify(cache, pack);
            bool cacheOk = good != null && good.Ok;
            bool packOk = pack != null && pack.SelfCheck();
            if (!cacheOk && !packOk) { how = "没有可用的完好备份"; return false; }
            try
            {
                if (cacheOk) CopyTree(cache, dir, true);
                if (packOk) ExtractMissingOrWrong(pack, dir);
            }
            catch (Exception e) { how = "还原失败：" + e.Message; return false; }
            how = cacheOk && !packOk ? "已从备份还原组件" : "已从 ClearDesk 内置的出厂副本还原组件";
            return true;
        }

        /// <summary>Write the factory copy into dir: every file that is missing, or listed in the manifest and different. Existing files are renamed aside, never half-overwritten.</summary>
        internal static void ExtractMissingOrWrong(ComponentPack pack, string dir)
        {
            Directory.CreateDirectory(dir);
            var want = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in pack.Manifest) want[e.Value] = e.Key;
            foreach (string rel in pack.Names)
            {
                string dest = Path.Combine(dir, rel.Replace('/', '\\'));
                string dirPart = Path.GetDirectoryName(dest); if (!Directory.Exists(dirPart)) Directory.CreateDirectory(dirPart);
                byte[] data = pack.Get(rel);
                bool exists = File.Exists(dest), manifestFile = want.ContainsKey(rel);
                if (exists)
                {
                    if (!manifestFile && !rel.Equals("verified.sha256", StringComparison.OrdinalIgnoreCase)) continue;      // settings.json etc. that exist are the user's
                    string h; try { h = HashFile(dest); } catch { h = null; }
                    if (h != null && h.Equals(HashBytes(data), StringComparison.OrdinalIgnoreCase)) continue;
                }
                string tmp = dest + ".new-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                File.WriteAllBytes(tmp, data);
                if (exists)
                {
                    string aside = dest + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                    try { File.Move(dest, aside); }
                    catch { try { File.Delete(dest); } catch { try { File.Delete(tmp); } catch { } throw new IOException("无法替换 " + Path.GetFileName(dest) + "（被占用）"); } }
                }
                File.Move(tmp, dest);
            }
            foreach (string old in Directory.GetFiles(dir, "*.bad-*", SearchOption.AllDirectories)) { try { File.Delete(old); } catch { } }
        }

        static void CopyTree(string from, string to, bool onlyDifferent)
        {
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from))
            {
                if (f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && onlyDifferent == false) continue;
                if (f.IndexOf(".bad-", StringComparison.OrdinalIgnoreCase) >= 0 || f.IndexOf(".new-", StringComparison.OrdinalIgnoreCase) >= 0) continue;
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
        public static ComponentCheck Ensure(string dir, string cache, out string note) { return Ensure(dir, cache, null, false, null, out note); }

        /// <summary>
        /// 2.2.4 order: (1) verify the folder against the factory copy; (2) repair it in place from the kept good copy or the factory copy; (3) if that cannot be written
        /// (read-only / locked / not allowed) materialise the factory copy in runDir and use that; (4) only when the user allowed it: a TranslucentTB.exe that has the shipped
        /// version and a valid signature is used although its hash differs. The result's Dir is the folder to start from.
        /// </summary>
        public static ComponentCheck Ensure(string dir, string cache, ComponentPack pack, bool allowUnverified, string runDir, out string note)
        {
            note = null;
            var c = Verify(dir, pack);
            if (c.Ok) { Remember(dir, cache, pack); return c; }
            if (c.State == ComponentState.Busy) { note = c.Text; return c; }   // transient: try again later, do not touch
            string how;
            var first = c;
            if (Repair(dir, cache, pack, out how))
            {
                var again = Verify(dir, pack);
                if (again.Ok) { note = how; Remember(dir, cache, pack); return again; }
                if (again.State == ComponentState.Busy) { note = again.Text; return again; }
                c = again;
            }
            // (3) per-user copy of the factory copy
            if (pack != null && runDir != null && pack.SelfCheck())
            {
                try
                {
                    ExtractMissingOrWrong(pack, runDir);
                    var alt = Verify(runDir, pack);
                    if (alt.Ok) { note = "安装文件夹里的组件无法修复（" + c.Text + (how != null ? "；" + how : "") + "），已改用 ClearDesk 内置副本释放到：" + runDir; return alt; }
                    if (alt.State == ComponentState.Busy) { note = alt.Text; return alt; }
                }
                catch (Exception e) { how = (how == null ? "" : how + "；") + "内置副本释放失败：" + e.Message; }
            }
            // (4) opt-in: shipped version + valid signature
            if (allowUnverified && c.State == ComponentState.Mismatch)
            {
                string why;
                if (UnverifiedAcceptable(dir, pack, out why))
                {
                    c.UnverifiedAllowed = true; c.State = ComponentState.Ok; note = "按设置允许使用未通过哈希校验的 TranslucentTB（" + why + "）";
                    return c;
                }
                how = (how == null ? "" : how + "；") + "未通过校验的文件也不符合“版本相同且签名有效”：" + why;
            }
            note = c.Text + (how != null ? "（" + how + "）" : "");
            return c;
        }

        /// <summary>Same file version as the shipped TranslucentTB.exe (when known) and a valid Authenticode signature.</summary>
        internal static Func<string, string, bool> SignatureOk;   // test hook (path, signer) -> valid
        internal static bool UnverifiedAcceptable(string dir, ComponentPack pack, out string why)
        {
            why = "";
            string exe = Path.Combine(dir, "TranslucentTB.exe");
            try
            {
                if (!File.Exists(exe)) { why = "没有 TranslucentTB.exe"; return false; }
                string signer; string sig = Authenticode.Status(exe, out signer);
                bool sigOk = SignatureOk != null ? SignatureOk(exe, signer) : sig == "有效";
                if (!sigOk) { why = "签名：" + sig; return false; }
                string have = FileVer(exe);
                string want = pack != null ? pack.Version : "";
                if (!string.IsNullOrEmpty(want) && !string.Equals(have, want, StringComparison.OrdinalIgnoreCase)) { why = "版本 " + have + " 与出厂版本 " + want + " 不同"; return false; }
                why = "版本 " + have + "，签名有效" + (signer.Length > 0 ? "（" + signer + "）" : "");
                return true;
            }
            catch (Exception e) { why = e.Message; return false; }
        }

        internal static string FileVer(string path) { try { var v = FileVersionInfo.GetVersionInfo(path); return v.FileVersion ?? ""; } catch { return ""; } }

        // ------------------------------------------------------------------ diagnostics
        /// <summary>Everything that can explain a failure, in plain text: which folder, expected vs actual SHA-256 per file, size, time, version, signature, who is running.</summary>
        public static string Describe(string dir, string cache, ComponentPack pack, bool full)
        {
            var sb = new StringBuilder();
            sb.AppendLine("ClearDesk 所在文件夹：" + Program.Base);
            sb.AppendLine("检查的组件文件夹：" + dir + (Directory.Exists(dir) ? "" : "（不存在）"));
            sb.AppendLine("出厂副本：" + (pack == null ? "此版本未内置（只能用文件夹里的校验清单）" : (pack.SelfCheck() ? "内置，完好" + (string.IsNullOrEmpty(pack.Version) ? "" : "，TranslucentTB " + pack.Version) : "内置，但自检未通过")));
            sb.AppendLine("完好备份：" + CacheText(cache, pack));
            sb.AppendLine("每用户副本：" + (Directory.Exists(RunDir) ? RunDir : "无"));
            var c = Verify(dir, pack);
            sb.AppendLine("校验依据：" + c.Source + "；结果：" + c.Code + " " + c.Text);
            foreach (var f in c.Files)
            {
                string path = Path.Combine(dir, f.Name);
                try
                {
                    if (File.Exists(path))
                    {
                        var fi = new FileInfo(path); f.Size = fi.Length; f.Mtime = fi.LastWriteTime; f.Attributes = fi.Attributes.ToString();
                        f.Version = FileVer(path);
                        string signer; f.Signature = Authenticode.Status(path, out signer) + (signer.Length > 0 ? " " + signer : "");
                        f.Zone = File.Exists(path + ":Zone.Identifier") || ZoneExists(path);
                    }
                }
                catch (Exception e) { f.Error = (f.Error ?? "") + " " + e.Message; }
                sb.AppendLine("  " + f.Name + "  " + (f.State == ComponentState.Ok ? "一致" : f.State == ComponentState.Mismatch ? "★不一致" : f.State == ComponentState.Missing ? "★缺失" : "★被占用"));
                sb.AppendLine("     期望 " + f.Expected);
                sb.AppendLine("     实际 " + (f.Actual ?? "（读不到：" + f.Error + "）"));
                if (f.Size >= 0) sb.AppendLine("     大小 " + f.Size + " 字节，修改时间 " + f.Mtime.ToString("yyyy-MM-dd HH:mm:ss") + "，版本 " + f.Version + "，签名 " + f.Signature + (f.Zone ? "，带“来自网络”标记" : "") + "，属性 " + f.Attributes);
            }
            ComponentState bad;
            var folderManifest = ReadManifest(dir, out bad);
            if (folderManifest == null) sb.AppendLine("文件夹里的 verified.sha256：" + (bad == ComponentState.NoManifest ? "缺失或读不了" : "格式无效"));
            else if (pack != null && pack.Manifest != null)
            {
                bool same = folderManifest.Count == pack.Manifest.Count && folderManifest.All(e => pack.Manifest.Any(p => p.Value == e.Value && p.Key.Equals(e.Key, StringComparison.OrdinalIgnoreCase)));
                sb.AppendLine("文件夹里的 verified.sha256：" + (same ? "与出厂清单相同" : "与出厂清单不同（可能是从别的版本复制来的旧清单；已不影响判断）"));
            }
            if (full) sb.Append(Running(dir));
            return sb.ToString();
        }

        static bool ZoneExists(string path)
        {
            try { using (var s = new FileStream(path + ":Zone.Identifier", FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) return true; } catch { return false; }
        }

        static string CacheText(string cache, ComponentPack pack)
        {
            if (cache == null || !Directory.Exists(cache)) return "无（" + (cache ?? "") + "）";
            var v = Verify(cache, pack);
            return cache + (v.Ok ? "，完好" : "，不可用：" + v.Text);
        }

        /// <summary>Running TranslucentTB processes, and the ExplorerTAP / ExplorerHooks DLLs that Explorer has loaded (TranslucentTB refuses to start when an older copy is still loaded there).</summary>
        public static string Running(string dir)
        {
            var sb = new StringBuilder();
            try
            {
                foreach (var p in Process.GetProcessesByName("TranslucentTB"))
                {
                    string path = ""; try { path = p.MainModule.FileName; } catch (Exception e) { path = "（读不到：" + e.Message + "）"; }
                    sb.AppendLine("正在运行的 TranslucentTB：PID " + p.Id + "，" + path + (path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ? "（就是这个文件夹里的）" : "（不是这个文件夹里的）"));
                    p.Dispose();
                }
            }
            catch { }
            try
            {
                foreach (var ex in Process.GetProcessesByName("explorer"))
                {
                    try
                    {
                        foreach (ProcessModule m in ex.Modules)
                            if (m.ModuleName.StartsWith("ExplorerTAP", StringComparison.OrdinalIgnoreCase) || m.ModuleName.StartsWith("ExplorerHooks", StringComparison.OrdinalIgnoreCase))
                            {
                                string h; try { h = HashFile(m.FileName); } catch { h = "读不到"; }
                                string mine = ""; try { string own = Path.Combine(dir, m.ModuleName); if (File.Exists(own)) mine = h.Equals(HashFile(own), StringComparison.OrdinalIgnoreCase) ? "，与这个文件夹里的相同" : "，★与这个文件夹里的不同（旧版 TranslucentTB 的残留，需要注销或重启 Windows 才会卸载）"; } catch { }
                                sb.AppendLine("资源管理器 (PID " + ex.Id + ") 已加载：" + m.FileName + "，SHA-256 " + h + mine);
                            }
                    }
                    catch { }
                    ex.Dispose();
                }
            }
            catch { }
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), "TranslucentTB");
                if (Directory.Exists(tmp)) sb.AppendLine("临时目录 " + tmp + "：" + string.Join("、", Directory.GetFiles(tmp).Select(f => Path.GetFileName(f) + "(" + new FileInfo(f).Length + ")")));
            }
            catch { }
            return sb.ToString();
        }
    }
}
