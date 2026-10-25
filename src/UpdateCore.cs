using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ClearDesk
{
    // 2.1.0 online update — pure logic (no UI): version compare, signed manifest, download with resume, package staging.
    // Trust model: the manifest travels over plain http (no domain / certificate yet), so integrity does NOT come from
    // the transport. versions.json carries an ECDSA P-256 / SHA-256 signature over (version, filename, size, sha256,
    // updateUrl, mirrors). The private key stays on the maintainer's PC; only the public key is compiled in below.
    internal static class UpdateVersion
    {
        internal sealed class Parsed : IComparable<Parsed>
        {
            internal int[] Num = new int[4];
            internal string[] Pre = new string[0];
            public int CompareTo(Parsed o)
            {
                for (int i = 0; i < 4; i++) if (Num[i] != o.Num[i]) return Num[i] < o.Num[i] ? -1 : 1;
                if (Pre.Length == 0 && o.Pre.Length == 0) return 0;
                if (Pre.Length == 0) return 1;           // 2.1.0 > 2.1.0-beta.1
                if (o.Pre.Length == 0) return -1;
                for (int i = 0; i < Math.Max(Pre.Length, o.Pre.Length); i++)
                {
                    if (i >= Pre.Length) return -1;
                    if (i >= o.Pre.Length) return 1;
                    long a, b; bool an = long.TryParse(Pre[i], NumberStyles.None, CultureInfo.InvariantCulture, out a), bn = long.TryParse(o.Pre[i], NumberStyles.None, CultureInfo.InvariantCulture, out b);
                    if (an && bn) { if (a != b) return a < b ? -1 : 1; }
                    else if (an != bn) return an ? -1 : 1;   // numeric identifiers sort before alphanumeric ones
                    else { int c = string.CompareOrdinal(Pre[i], o.Pre[i]); if (c != 0) return c < 0 ? -1 : 1; }
                }
                return 0;
            }
        }

        // "2.1.0", "v2.1.0", "2.1", "2.1.0.3", "2.1.0-beta.1+build5". Returns false for anything else.
        internal static bool TryParse(string text, out Parsed result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            if (t.Length > 64) return false;
            if (t[0] == 'v' || t[0] == 'V') t = t.Substring(1);
            int plus = t.IndexOf('+'); if (plus >= 0) t = t.Substring(0, plus);
            string pre = null; int dash = t.IndexOf('-'); if (dash >= 0) { pre = t.Substring(dash + 1); t = t.Substring(0, dash); if (pre.Length == 0) return false; }
            var parts = t.Split('.');
            if (parts.Length < 1 || parts.Length > 4) return false;
            var p = new Parsed();
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0 || parts[i].Length > 9 || !parts[i].All(c => c >= '0' && c <= '9')) return false;
                p.Num[i] = int.Parse(parts[i], CultureInfo.InvariantCulture);
            }
            if (pre != null)
            {
                p.Pre = pre.Split('.');
                if (p.Pre.Any(s => s.Length == 0 || !s.All(c => char.IsLetterOrDigit(c) && c < 128 || c == '-'))) return false;
            }
            result = p; return true;
        }

        // <0 a older, 0 equal, >0 a newer. Unparseable text counts as the oldest possible.
        internal static int Compare(string a, string b)
        {
            Parsed pa, pb; bool ia = TryParse(a, out pa), ib = TryParse(b, out pb);
            if (!ia && !ib) return 0; if (!ia) return -1; if (!ib) return 1;
            return pa.CompareTo(pb);
        }
        internal static bool IsNewer(string candidate, string current)
        {
            Parsed pa, pb; return TryParse(candidate, out pa) && TryParse(current, out pb) && pa.CompareTo(pb) > 0;
        }
    }

    internal sealed class UpdateManifest
    {
        public string Latest = "", Date = "", FileName = "", File = "", Alias = "", Sha256 = "", Notes = "", Github = "", Signature = "", UpdateUrl = "", NotesText = "";
        public long Size;
        public List<string> Mirrors = new List<string>(), Changes = new List<string>();
        public string SourceUrl = "";       // where this manifest was fetched from
        public bool Signed, NotifyOnly;     // Signed = signature verified; NotifyOnly = newer version known but cannot be installed automatically
        public string PageUrl = "";         // human-readable download / release page

        static string Str(Dictionary<string, object> d, string key) { object o; return d.TryGetValue(key, out o) && o != null ? Convert.ToString(o, CultureInfo.InvariantCulture).Trim() : ""; }
        static List<string> List(Dictionary<string, object> d, string key)
        {
            object o; var r = new List<string>();
            if (d.TryGetValue(key, out o) && o is System.Collections.IEnumerable && !(o is string))
                foreach (var x in (System.Collections.IEnumerable)o) if (x != null) { string s = Convert.ToString(x, CultureInfo.InvariantCulture).Trim(); if (s.Length > 0) r.Add(s); }
            return r;
        }

        internal static UpdateManifest Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > 256 * 1024) throw new FormatException("版本清单为空或过大。");
            var ser = new JavaScriptSerializer { MaxJsonLength = 512 * 1024 };
            var d = ser.DeserializeObject(json.TrimStart('\uFEFF')) as Dictionary<string, object>;
            if (d == null) throw new FormatException("版本清单格式不正确。");
            var m = new UpdateManifest
            {
                Latest = Str(d, "latest"), Date = Str(d, "date"), FileName = Str(d, "filename"), File = Str(d, "file"), Alias = Str(d, "alias"),
                Sha256 = Str(d, "sha256").ToLowerInvariant(), Notes = Str(d, "notes"), Github = Str(d, "github"), Signature = Str(d, "signature"),
                UpdateUrl = Str(d, "updateUrl"), NotesText = Str(d, "notesText"), Mirrors = List(d, "mirrors"), Changes = List(d, "changes")
            };
            long size; if (!long.TryParse(Str(d, "size"), NumberStyles.None, CultureInfo.InvariantCulture, out size)) size = 0; m.Size = size;
            UpdateVersion.Parsed v;
            if (!UpdateVersion.TryParse(m.Latest, out v)) throw new FormatException("版本清单中的版本号无效：" + m.Latest);
            return m;
        }

        // The exact bytes that are signed. Notes / changes / dates are presentation only and not covered.
        internal string SignedPayload()
        {
            var sb = new StringBuilder();
            sb.Append("cleardesk-update-v1\n");
            sb.Append("version=").Append(Latest).Append('\n');
            sb.Append("filename=").Append(FileName).Append('\n');
            sb.Append("size=").Append(Size.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("sha256=").Append(Sha256.ToLowerInvariant()).Append('\n');
            sb.Append("updateUrl=").Append(UpdateUrl ?? "").Append('\n');
            sb.Append("mirrors=").Append(string.Join(",", Mirrors)).Append('\n');
            return sb.ToString();
        }

        static readonly System.Text.RegularExpressions.Regex NameRule = new System.Text.RegularExpressions.Regex(@"^ClearDesk-[0-9A-Za-z.\-]{1,40}\.zip$");
        static readonly System.Text.RegularExpressions.Regex HashRule = new System.Text.RegularExpressions.Regex(@"^[0-9a-f]{64}$");
        internal const long MaxPackage = 250L * 1024 * 1024;

        // Fields an installable manifest must have. Returns null when fine, otherwise the reason.
        internal string ValidateInstallable()
        {
            if (!NameRule.IsMatch(FileName)) return "更新包文件名不合法。";
            if (!HashRule.IsMatch(Sha256.ToLowerInvariant())) return "清单缺少有效的 SHA256。";
            if (Size <= 0 || Size > MaxPackage) return "清单中的文件大小不合法。";
            if (string.IsNullOrEmpty(File) && string.IsNullOrEmpty(Alias)) return "清单缺少下载地址。";
            if (string.IsNullOrEmpty(Signature)) return "清单没有签名，已拒绝。";
            return null;
        }
    }

    internal static class UpdateCrypto
    {
        // ECDSA P-256 public key (BCRYPT_ECCPUBLIC_BLOB, base64). Matching private key: C:\work\update-keys\ on the maintainer's PC.
        internal const string ProductionPublicKey = "RUNTMSAAAABM7tuR0WUUI+/B2R/V3/kCEm/rLcabmF3gByC0rWLjvLthn7PQSJCT8jRBzc0ywEPvu4+tV7PW6X2/qIvVBIEh";
        // Tests (and --preview runs only) may trust a throw-away key instead. A normal run can never be redirected this way.
        internal static string PublicKey
        {
            get
            {
                if (Program.Preview) { string o = Environment.GetEnvironmentVariable("CLEARDESK_UPDATE_PUBKEY"); if (!string.IsNullOrWhiteSpace(o)) return o.Trim(); }
                return ProductionPublicKey;
            }
        }

        internal static bool Verify(byte[] payload, string signatureBase64, string publicKeyBase64)
        {
            try
            {
                if (payload == null || string.IsNullOrWhiteSpace(signatureBase64) || string.IsNullOrWhiteSpace(publicKeyBase64)) return false;
                byte[] sig = Convert.FromBase64String(signatureBase64.Trim());
                if (sig.Length != 64) return false;
                byte[] blob = Convert.FromBase64String(publicKeyBase64.Trim());
                using (var key = CngKey.Import(blob, CngKeyBlobFormat.EccPublicBlob))
                using (var ec = new ECDsaCng(key))
                    return ec.VerifyData(payload, sig, HashAlgorithmName.SHA256);
            }
            catch { return false; }
        }

        internal static bool VerifyManifest(UpdateManifest m, string publicKey = null)
        {
            return Verify(Encoding.UTF8.GetBytes(m.SignedPayload()), m.Signature, publicKey ?? PublicKey);
        }

        internal static string Sha256Hex(string path)
        {
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (var h = SHA256.Create()) return Hex(h.ComputeHash(f));
        }
        internal static string Sha256Hex(byte[] data) { using (var h = SHA256.Create()) return Hex(h.ComputeHash(data)); }
        static string Hex(byte[] b) { var sb = new StringBuilder(b.Length * 2); foreach (var x in b) sb.Append(x.ToString("x2")); return sb.ToString(); }
    }

    internal sealed class UpdateConfig
    {
        public List<string> Sources = new List<string>();
        public bool GithubFallback = true;
        public bool FromFile;
    }

    internal static class UpdateSources
    {
        internal static readonly string[] Defaults = { "http://101.200.206.248/versions.json" };
        internal const string GithubApi = "https://api.github.com/repos/visual-life/ClearDesk/releases/latest";
        internal const string GithubPage = "https://github.com/visual-life/ClearDesk/releases";

        internal static bool IsHttpUrl(string url)
        {
            Uri u; return !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url.Trim(), UriKind.Absolute, out u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrEmpty(u.Host);
        }

        // An update-source value may be a full ".../versions.json" URL or only a site ("https://cleardesk.example/").
        internal static string Normalize(string url)
        {
            if (!IsHttpUrl(url)) return null;
            var u = new Uri(url.Trim());
            if (u.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return u.AbsoluteUri;
            var b = new UriBuilder(u) { Query = "", Fragment = "" };
            string path = b.Path; if (!path.EndsWith("/")) path += "/"; b.Path = path + "versions.json";
            return b.Uri.AbsoluteUri;
        }

        // update-source.json: {"sources":["http://host/versions.json"],"github":true}  (Data folder wins over the install folder).
        internal static UpdateConfig ReadConfig(params string[] folders)
        {
            foreach (var folder in folders)
            {
                try
                {
                    if (string.IsNullOrEmpty(folder)) continue;
                    string path = Path.Combine(folder, "update-source.json");
                    if (!File.Exists(path)) continue;
                    var d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                    if (d == null) continue;
                    var cfg = new UpdateConfig { FromFile = true };
                    object o;
                    if (d.TryGetValue("sources", out o) && o is System.Collections.IEnumerable && !(o is string))
                        foreach (var x in (System.Collections.IEnumerable)o) { string n = Normalize(Convert.ToString(x)); if (n != null) cfg.Sources.Add(n); }
                    if (d.TryGetValue("github", out o) && o is bool) cfg.GithubFallback = (bool)o;
                    if (cfg.Sources.Count > 0) return cfg;
                }
                catch { }
            }
            return null;
        }

        // Order: user setting > update-source.json > source learned from a signed manifest (updateUrl / mirrors) > built-in defaults.
        internal static List<string> Resolve(string userSetting, UpdateConfig file, string redirect, IEnumerable<string> mirrors)
        {
            var list = new List<string>();
            Action<string> add = s => { string n = Normalize(s); if (n != null && !list.Contains(n, StringComparer.OrdinalIgnoreCase)) list.Add(n); };
            if (!string.IsNullOrWhiteSpace(userSetting)) add(userSetting);
            if (file != null) foreach (var s in file.Sources) add(s);
            if (!string.IsNullOrWhiteSpace(redirect)) add(redirect);
            if (mirrors != null) foreach (var s in mirrors) add(s);
            foreach (var s in Defaults) add(s);
            return list;
        }
    }

    internal sealed class UpdateCheckResult
    {
        public UpdateManifest Manifest;        // best manifest found (may be same version as current)
        public bool Newer;                     // Manifest is newer than the running version
        public string Error = "";              // set when nothing usable was found
        public List<string> Log = new List<string>();
        public string Source = "";
        public bool Completed { get { return Manifest != null; } }
    }

    internal static class UpdateClient
    {
        internal const string UserAgent = "ClearDesk/" + SettingsWindow.Version + " (update-check)";

        // Test hook: replaces the network (url -> body text). Return null to simulate "unreachable".
        internal static Func<string, string> TextFetcher;

        static HttpWebRequest Create(string url, int timeoutMs)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.UserAgent = UserAgent; r.Timeout = timeoutMs; r.ReadWriteTimeout = timeoutMs; r.AllowAutoRedirect = true; r.MaximumAutomaticRedirections = 5;
            r.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.BypassCache);
            r.Headers.Add("Cache-Control", "no-cache");
            return r;
        }

        internal static string GetText(string url, int timeoutMs = 8000, int maxBytes = 512 * 1024)
        {
            if (TextFetcher != null) return TextFetcher(url);
            using (var resp = (HttpWebResponse)Create(url, timeoutMs).GetResponse())
            using (var s = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                var buf = new byte[8192]; int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) { ms.Write(buf, 0, n); if (ms.Length > maxBytes) throw new IOException("响应过大。"); }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        // Resolves a manifest "file" / "notes" value against the manifest URL (relative path or absolute http(s) URL).
        internal static string Resolve(string baseUrl, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) return null;
            Uri u;
            if (Uri.TryCreate(relative.Trim(), UriKind.Absolute, out u)) return UpdateSources.IsHttpUrl(u.AbsoluteUri) ? u.AbsoluteUri : null;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out u)) return null;
            Uri r; return Uri.TryCreate(u, relative.Trim(), out r) && UpdateSources.IsHttpUrl(r.AbsoluteUri) ? r.AbsoluteUri : null;
        }

        // Fetches one source and returns a manifest whose signature has been verified. Throws with a user-readable reason.
        internal static UpdateManifest FetchSigned(string url, string publicKey)
        {
            string body = GetText(url);
            if (body == null) throw new IOException("无法连接。");
            var m = UpdateManifest.Parse(body);
            m.SourceUrl = url;
            if (string.IsNullOrEmpty(m.Signature)) throw new InvalidDataException("清单没有签名。");
            if (!UpdateCrypto.VerifyManifest(m, publicKey)) throw new InvalidDataException("清单签名无效（可能被篡改或使用了其他密钥）。");
            m.Signed = true; m.PageUrl = Resolve(url, m.Notes) ?? m.Github;
            return m;
        }

        // GitHub Releases fallback. Only produces an installable manifest when the release carries a signed versions.json asset;
        // otherwise the result is notify-only (we can say "a newer version exists" but never install unsigned code).
        internal static UpdateManifest FromGithub(string json, string publicKey)
        {
            var d = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.DeserializeObject(json) as Dictionary<string, object>;
            if (d == null || !d.ContainsKey("tag_name")) throw new FormatException("GitHub 响应格式不正确。");
            string tag = Convert.ToString(d["tag_name"]);
            UpdateVersion.Parsed pv; if (!UpdateVersion.TryParse(tag, out pv)) throw new FormatException("GitHub 版本号无效：" + tag);
            string html = d.ContainsKey("html_url") ? Convert.ToString(d["html_url"]) : UpdateSources.GithubPage;
            string body = d.ContainsKey("body") && d["body"] != null ? Convert.ToString(d["body"]) : "";
            var m = new UpdateManifest { Latest = tag.TrimStart('v', 'V'), Github = html, PageUrl = html, NotifyOnly = true, NotesText = body, SourceUrl = UpdateSources.GithubApi };
            m.Date = d.ContainsKey("published_at") && d["published_at"] != null ? Convert.ToString(d["published_at"]).Split('T')[0] : "";
            var assets = d.ContainsKey("assets") ? d["assets"] as System.Collections.IEnumerable : null;
            if (assets != null)
                foreach (var a in assets.OfType<Dictionary<string, object>>())
                {
                    string name = a.ContainsKey("name") ? Convert.ToString(a["name"]) : "", url = a.ContainsKey("browser_download_url") ? Convert.ToString(a["browser_download_url"]) : "";
                    if (name.Equals("versions.json", StringComparison.OrdinalIgnoreCase) && UpdateSources.IsHttpUrl(url))
                    {
                        try
                        {
                            var signed = FetchSigned(url, publicKey);
                            if (signed.Latest == m.Latest || UpdateVersion.Compare(signed.Latest, m.Latest) == 0)
                            {
                                // the zip must be a sibling asset of the signed manifest
                                var zip = assets.OfType<Dictionary<string, object>>().FirstOrDefault(x => x.ContainsKey("name") && Convert.ToString(x["name"]) == signed.FileName);
                                if (zip != null) { signed.File = Convert.ToString(zip["browser_download_url"]); signed.Alias = ""; signed.PageUrl = html; return signed; }
                            }
                        }
                        catch { }
                    }
                }
            return m;
        }

        internal static UpdateCheckResult Check(string currentVersion, IList<string> sources, bool githubFallback, string publicKey = null)
        {
            var res = new UpdateCheckResult(); publicKey = publicKey ?? UpdateCrypto.PublicKey;
            foreach (var url in sources)
            {
                try
                {
                    var m = FetchSigned(url, publicKey);
                    res.Manifest = m; res.Source = url; res.Newer = UpdateVersion.IsNewer(m.Latest, currentVersion);
                    res.Log.Add("OK " + url + " → " + m.Latest);
                    return res;
                }
                catch (Exception e) { res.Log.Add("FAIL " + url + "：" + e.Message); }
            }
            if (githubFallback)
            {
                try
                {
                    string json = GetText(UpdateSources.GithubApi, 10000, 4 * 1024 * 1024);
                    if (json == null) throw new IOException("无法连接。");
                    var m = FromGithub(json, publicKey);
                    res.Manifest = m; res.Source = UpdateSources.GithubApi; res.Newer = UpdateVersion.IsNewer(m.Latest, currentVersion);
                    res.Log.Add("OK GitHub → " + m.Latest + (m.Signed ? " (signed)" : " (notify only)"));
                    return res;
                }
                catch (Exception e) { res.Log.Add("FAIL GitHub：" + e.Message); }
            }
            res.Error = sources.Count == 0 && !githubFallback ? "没有可用的更新源。" : "无法获取版本信息：" + string.Join("；", res.Log.Select(l => l.Replace("FAIL ", "")).Take(2));
            return res;
        }

        // ---------------------------------------------------------------- download (resume-safe, hash + size verified)
        internal sealed class DownloadResult { public string Path = "", Error = ""; public bool Ok { get { return Path.Length > 0; } } public string Url = ""; }

        sealed class PartMeta { public string sha256 = "", url = ""; public long size; }
        static void WriteMeta(string part, UpdateManifest m, string url)
        {
            try { File.WriteAllText(part + ".json", new JavaScriptSerializer().Serialize(new PartMeta { sha256 = m.Sha256, size = m.Size, url = url })); } catch { }
        }
        static bool MetaMatches(string part, UpdateManifest m)
        {
            try
            {
                var p = new JavaScriptSerializer().Deserialize<PartMeta>(File.ReadAllText(part + ".json"));
                return p != null && p.sha256 == m.Sha256 && p.size == m.Size;
            }
            catch { return false; }
        }

        internal static List<string> DownloadUrls(UpdateManifest m, IEnumerable<string> extraSources)
        {
            var urls = new List<string>();
            Action<string, string> add = (b, rel) => { string u = Resolve(b, rel); if (u != null && !urls.Contains(u)) urls.Add(u); };
            var bases = new List<string>(); if (!string.IsNullOrEmpty(m.SourceUrl)) bases.Add(m.SourceUrl);
            foreach (var s in m.Mirrors) { string n = UpdateSources.Normalize(s); if (n != null) bases.Add(n); }
            if (extraSources != null) bases.AddRange(extraSources);
            foreach (var b in bases.Distinct(StringComparer.OrdinalIgnoreCase)) { add(b, m.File); add(b, m.Alias); }
            return urls;
        }

        // Test hook for the transfer itself: (url, rangeStart) -> stream, status. Not used in production.
        internal static Func<string, long, KeyValuePair<int, Stream>> StreamOpener;
        internal static int RetryDelayMs = 300;

        internal static DownloadResult Download(UpdateManifest m, IEnumerable<string> extraSources, string folder, Action<long, long> progress, CancellationToken cancel)
        {
            var res = new DownloadResult();
            string bad = m.ValidateInstallable(); if (bad != null) { res.Error = bad; return res; }
            Directory.CreateDirectory(folder);
            string dest = Path.Combine(folder, m.FileName), part = dest + ".part";
            try { if (File.Exists(dest) && new FileInfo(dest).Length == m.Size && UpdateCrypto.Sha256Hex(dest) == m.Sha256) { res.Path = dest; if (progress != null) progress(m.Size, m.Size); return res; } } catch { }
            var urls = DownloadUrls(m, extraSources);
            if (urls.Count == 0) { res.Error = "没有可用的下载地址。"; return res; }
            var errors = new List<string>();
            foreach (var url in urls)
            {
                for (int attempt = 1; attempt <= 4; attempt++)
                {
                    if (cancel.IsCancellationRequested) { res.Error = "已取消。"; return res; }
                    try
                    {
                        long have = 0;
                        if (File.Exists(part)) { if (MetaMatches(part, m)) have = new FileInfo(part).Length; else File.Delete(part); }
                        if (have > m.Size) { File.Delete(part); have = 0; }
                        if (have < m.Size)
                        {
                            WriteMeta(part, m, url);
                            Transfer(url, part, have, m, progress, cancel);
                        }
                        long len = new FileInfo(part).Length;
                        if (len != m.Size) throw new IOException("文件大小不一致（" + len + " / " + m.Size + "）。");
                        string hash = UpdateCrypto.Sha256Hex(part);
                        if (hash != m.Sha256)
                        {
                            try { File.Delete(part); File.Delete(part + ".json"); } catch { }
                            errors.Add(url + "：SHA256 校验失败"); break;   // content is wrong, a retry of the same URL is pointless
                        }
                        if (File.Exists(dest)) File.Delete(dest);
                        File.Move(part, dest); try { File.Delete(part + ".json"); } catch { }
                        res.Path = dest; res.Url = url; return res;
                    }
                    catch (OperationCanceledException) { res.Error = "已取消。"; return res; }
                    catch (Exception e) { errors.Add(url + "：" + e.Message); if (attempt < 4 && RetryDelayMs > 0) Thread.Sleep(Math.Min(1500, RetryDelayMs * attempt)); }
                }
            }
            res.Error = errors.Any(e => e.Contains("SHA256")) ? "下载的更新包校验失败（SHA256 与签名清单不一致），已丢弃，未做任何更改。" : "下载失败：" + (errors.LastOrDefault() ?? "未知错误");
            return res;
        }

        static void Transfer(string url, string part, long have, UpdateManifest m, Action<long, long> progress, CancellationToken cancel)
        {
            Stream input; int status = 200; HttpWebResponse resp = null;
            if (StreamOpener != null) { var o = StreamOpener(url, have); status = o.Key; input = o.Value; }
            else
            {
                var req = Create(url, 20000);
                if (have > 0) req.AddRange(have, m.Size - 1);
                try { resp = (HttpWebResponse)req.GetResponse(); }
                catch (WebException we) when (we.Response is HttpWebResponse && ((HttpWebResponse)we.Response).StatusCode == (HttpStatusCode)416)
                { try { File.Delete(part); } catch { } throw new IOException("服务器不接受续传，将重新下载。"); }
                status = (int)resp.StatusCode; input = resp.GetResponseStream();
            }
            using (resp) using (input)
            {
                FileMode mode = FileMode.Append;
                if (have > 0 && status != 206) { mode = FileMode.Create; have = 0; }    // server ignored Range: start over
                else if (have == 0) mode = FileMode.Create;
                if (resp != null && status == 206)
                {
                    string cr = resp.Headers["Content-Range"] ?? "";   // "bytes 1000-9999/10000"
                    if (!cr.StartsWith("bytes " + have + "-", StringComparison.Ordinal)) { try { File.Delete(part); } catch { } throw new IOException("续传位置不一致，将重新下载。"); }
                }
                using (var f = new FileStream(part, mode, FileAccess.Write, FileShare.Read, 1 << 16))
                {
                    var buf = new byte[1 << 16]; int n; long got = have; var tick = DateTime.UtcNow;
                    while ((n = input.Read(buf, 0, buf.Length)) > 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        f.Write(buf, 0, n); got += n;
                        if (got > m.Size) throw new IOException("收到的数据超过了清单声明的大小。");
                        if (progress != null && (DateTime.UtcNow - tick).TotalMilliseconds > 80) { progress(got, m.Size); tick = DateTime.UtcNow; }
                    }
                    f.Flush();
                    if (progress != null) progress(got, m.Size);
                }
            }
        }
    }

    // ------------------------------------------------------------------- package (zip) layout, staging and verification
    internal sealed class StagedPackage
    {
        public string Dir = "";                          // folder holding the extracted payload (ClearDesk.exe at its root)
        public List<string> Files = new List<string>();  // relative paths, '/' separated
        public Dictionary<string, string> Sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    internal static class UpdatePackage
    {
        internal const int MaxEntries = 6000;
        internal const long MaxTotal = 600L * 1024 * 1024;
        static readonly string[] Reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        // Package-relative path with '/' separators, or null when the entry could escape the folder (zip slip), hit an
        // alternate data stream / device name, or is otherwise unsafe.
        internal static string SafeRelative(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string n = name.Replace('\\', '/');
            if (n.StartsWith("/") || (n.Length > 1 && n[1] == ':') || n.Contains("//")) return null;
            var parts = n.TrimEnd('/').Split('/');
            var bad = Path.GetInvalidFileNameChars();
            foreach (var p in parts)
            {
                if (p.Length == 0 || p == "." || p == ".." || p.IndexOfAny(bad) >= 0 || p.EndsWith(".") || p.EndsWith(" ")) return null;
                string stem = p.Split('.')[0].ToUpperInvariant();
                if (Reserved.Contains(stem)) return null;
            }
            return string.Join("/", parts);
        }

        // Zips built by hand have forward slashes; the .NET "CreateFromDirectory" of older frameworks wrote backslashes. Both work.
        // The payload root is the folder that contains ClearDesk.exe: either the zip root or a single top-level folder.
        internal static string FindPrefix(IEnumerable<string> entryNames)
        {
            var roots = new List<string>();
            foreach (var raw in entryNames)
            {
                string n = SafeRelative(raw); if (n == null) continue;
                var parts = n.Split('/');
                if (parts[parts.Length - 1].Equals("ClearDesk.exe", StringComparison.OrdinalIgnoreCase) && parts.Length <= 2) roots.Add(parts.Length == 1 ? "" : parts[0] + "/");
            }
            roots = roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (roots.Count != 1) throw new InvalidDataException(roots.Count == 0 ? "更新包里找不到 ClearDesk.exe。" : "更新包结构异常（多个 ClearDesk.exe）。");
            return roots[0];
        }

        internal static Dictionary<string, string> ParseSums(string text)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in text.Split('\n'))
            {
                string l = line.Trim(); if (l.Length < 67) continue;
                string hash = l.Substring(0, 64).ToLowerInvariant(), rel = SafeRelative(l.Substring(64).Trim());
                if (rel == null || !hash.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) throw new InvalidDataException("SHA256SUMS.txt 格式不正确。");
                d[rel] = hash;
            }
            return d;
        }

        // Extracts the payload into stageDir and verifies every file against the SHA256SUMS.txt that ships inside the package.
        internal static StagedPackage Stage(string zipPath, string stageDir)
        {
            if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true);
            Directory.CreateDirectory(stageDir);
            var pkg = new StagedPackage { Dir = stageDir };
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                if (zip.Entries.Count > MaxEntries) throw new InvalidDataException("更新包文件数量异常。");
                string prefix = FindPrefix(zip.Entries.Select(e => e.FullName));
                long total = 0;
                foreach (var e in zip.Entries)
                {
                    string full = e.FullName.Replace('\\', '/');
                    if (prefix.Length > 0 && !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    string inner = full.Substring(prefix.Length);
                    if (inner.Length == 0) continue;
                    string rel = SafeRelative(inner); if (rel == null) throw new InvalidDataException("更新包包含不安全的路径：" + e.FullName);
                    string target = Path.GetFullPath(Path.Combine(stageDir, rel.Replace('/', '\\')));
                    if (!target.StartsWith(Path.GetFullPath(stageDir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包包含不安全的路径：" + e.FullName);
                    if (full.EndsWith("/") || e.Name.Length == 0) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var src = e.Open()) using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buf = new byte[1 << 16]; int n;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0) { total += n; if (total > MaxTotal) throw new InvalidDataException("更新包解压后过大。"); dst.Write(buf, 0, n); }
                    }
                    pkg.Files.Add(rel);
                }
            }
            Verify(pkg);
            return pkg;
        }

        // Every file must be listed in SHA256SUMS.txt with a matching hash and every listed file must exist.
        internal static void Verify(StagedPackage pkg)
        {
            string sums = Path.Combine(pkg.Dir, "SHA256SUMS.txt");
            if (!File.Exists(sums)) throw new InvalidDataException("更新包缺少 SHA256SUMS.txt。");
            pkg.Sums = ParseSums(File.ReadAllText(sums, Encoding.UTF8));
            if (!pkg.Files.Contains("ClearDesk.exe", StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("更新包缺少 ClearDesk.exe。");
            foreach (var f in pkg.Files)
            {
                if (f.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) continue;
                string want; if (!pkg.Sums.TryGetValue(f, out want)) throw new InvalidDataException("更新包里的文件未登记在 SHA256SUMS.txt：" + f);
                if (UpdateCrypto.Sha256Hex(Path.Combine(pkg.Dir, f.Replace('/', '\\'))) != want) throw new InvalidDataException("文件校验失败：" + f);
            }
            foreach (var kv in pkg.Sums)
                if (!pkg.Files.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("更新包缺少文件：" + kv.Key);
            byte[] head = new byte[2]; using (var f = File.OpenRead(Path.Combine(pkg.Dir, "ClearDesk.exe"))) f.Read(head, 0, 2);
            if (head[0] != 'M' || head[1] != 'Z') throw new InvalidDataException("ClearDesk.exe 不是有效的程序文件。");
        }
    }
}
