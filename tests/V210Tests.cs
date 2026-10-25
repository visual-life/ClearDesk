using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ClearDesk
{
    // 2.1.0: online update — version compare, signed manifest, sources, download/resume, package staging, apply / rollback, UI.
    internal static class V210Tests
    {
        // ------------------------------------------------------------------ helpers
        static CngKey NewKey(out string pub)
        {
            var p = new CngKeyCreationParameters { ExportPolicy = CngExportPolicies.AllowPlaintextExport };
            var key = CngKey.Create(CngAlgorithm.ECDsaP256, null, p);
            pub = Convert.ToBase64String(key.Export(CngKeyBlobFormat.EccPublicBlob)); return key;
        }
        static string Sign(CngKey key, byte[] data) { using (var ec = new ECDsaCng(key)) return Convert.ToBase64String(ec.SignData(data, HashAlgorithmName.SHA256)); }
        static void SignManifest(UpdateManifest m, CngKey key) { m.Signature = Sign(key, Encoding.UTF8.GetBytes(m.SignedPayload())); }
        static UpdateManifest Manifest(string version, byte[] zip, CngKey key)
        {
            var m = new UpdateManifest { Latest = version, Date = "2026-11-01", FileName = "ClearDesk-" + version + ".zip", File = "downloads/ClearDesk-" + version + ".zip", Alias = "downloads/ClearDesk-latest.zip", Size = zip.Length, Sha256 = UpdateCrypto.Sha256Hex(zip), SourceUrl = "http://t.test/versions.json" };
            if (key != null) SignManifest(m, key); else m.Signature = "x";
            return m;
        }
        static string ToJson(UpdateManifest m)
        {
            var d = new Dictionary<string, object> { { "latest", m.Latest }, { "date", m.Date }, { "filename", m.FileName }, { "file", m.File }, { "alias", m.Alias }, { "size", m.Size }, { "sha256", m.Sha256 }, { "notes", m.Notes }, { "github", m.Github }, { "signature", m.Signature }, { "updateUrl", m.UpdateUrl }, { "mirrors", m.Mirrors.ToArray() }, { "changes", m.Changes.ToArray() } };
            return new JavaScriptSerializer().Serialize(d);
        }
        static string Scratch() { string d = Path.Combine(Path.GetTempPath(), "cleardesk-v210-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(d); return d; }
        static void Put(string root, string rel, string text) { string p = Path.Combine(root, rel.Replace('/', '\\')); Directory.CreateDirectory(Path.GetDirectoryName(p)); File.WriteAllText(p, text, new UTF8Encoding(false)); }
        static string Get(string root, string rel) { string p = Path.Combine(root, rel.Replace('/', '\\')); return File.Exists(p) ? File.ReadAllText(p) : null; }
        static void WriteSums(string dir, params string[] rels)
        {
            var lines = rels.OrderBy(r => r, StringComparer.Ordinal).Select(r => UpdateCrypto.Sha256Hex(Path.Combine(dir, r.Replace('/', '\\'))) + "  " + r);
            File.WriteAllText(Path.Combine(dir, "SHA256SUMS.txt"), string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
        }
        static byte[] BuildZip(string prefix, Dictionary<string, byte[]> files, bool backslash = false, bool sums = true, Func<string, byte[], byte[]> tamperAfterSums = null, string extraEntry = null)
        {
            var all = new Dictionary<string, byte[]>(files);
            if (sums) all["SHA256SUMS.txt"] = Encoding.UTF8.GetBytes(string.Join("\r\n", files.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => UpdateCrypto.Sha256Hex(files[k]) + "  " + k)) + "\r\n");
            var ms = new MemoryStream();
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                foreach (var kv in all)
                {
                    string name = prefix + kv.Key; if (backslash) name = name.Replace('/', '\\');
                    var e = z.CreateEntry(name); byte[] data = tamperAfterSums != null && kv.Key != "SHA256SUMS.txt" ? tamperAfterSums(kv.Key, kv.Value) : kv.Value;
                    using (var s = e.Open()) s.Write(data, 0, data.Length);
                }
                if (extraEntry != null) { var e = z.CreateEntry(extraEntry); using (var s = e.Open()) s.WriteByte(65); }
            }
            return ms.ToArray();
        }
        static Dictionary<string, byte[]> Payload(string tag)
        {
            return new Dictionary<string, byte[]> { { "ClearDesk.exe", Encoding.ASCII.GetBytes("MZ-" + tag) }, { "ClearDesk.exe.config", Encoding.ASCII.GetBytes("<cfg/>") }, { "taskbar/verified.sha256", Encoding.ASCII.GetBytes("x " + tag) }, { "source/a.cs", Encoding.UTF8.GetBytes("class A{} // " + tag) } };
        }
        static bool Throws(Action a) { try { a(); return false; } catch { return true; } }

        sealed class Flaky : Stream
        {
            readonly byte[] data; int pos; readonly int end; readonly int failAfter; int served;
            public Flaky(byte[] d, int start, int failAfter) { data = d; pos = start; end = d.Length; this.failAfter = failAfter; }
            public override int Read(byte[] b, int o, int c)
            {
                if (failAfter >= 0 && served >= failAfter) throw new IOException("simulated connection reset");
                int n = Math.Min(c, end - pos); if (failAfter >= 0) n = Math.Min(n, failAfter - served); if (n <= 0) return 0;
                Array.Copy(data, pos, b, o, n); pos += n; served += n; return n;
            }
            public override bool CanRead { get { return true; } } public override bool CanSeek { get { return false; } } public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } } public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { } public override long Seek(long o, SeekOrigin s) { throw new NotSupportedException(); } public override void SetLength(long v) { throw new NotSupportedException(); } public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }
        }

        // ------------------------------------------------------------------ pure logic (also part of the --self-test suite)
        public static void Rules(Action<bool, string> check)
        {
            // ---- versions
            check(UpdateVersion.Compare("2.1.0", "2.0.9") > 0 && UpdateVersion.Compare("2.0.9", "2.1.0") < 0 && UpdateVersion.Compare("2.1.0", "2.1.0") == 0, "update: 2.1.0 is newer than 2.0.9");
            check(UpdateVersion.IsNewer("2.1.10", "2.1.9") && !UpdateVersion.IsNewer("2.1.9", "2.1.10"), "update: versions compare numerically (2.1.10 > 2.1.9)");
            check(UpdateVersion.Compare("v2.1.0", "2.1.0") == 0 && UpdateVersion.Compare("2.1", "2.1.0") == 0 && UpdateVersion.IsNewer("2.1.0.1", "2.1.0"), "update: v prefix, missing patch and 4th number");
            check(UpdateVersion.IsNewer("2.1.0", "2.1.0-beta.1") && !UpdateVersion.IsNewer("2.1.0-beta.1", "2.1.0") && UpdateVersion.IsNewer("2.1.0-beta.2", "2.1.0-beta.1") && UpdateVersion.IsNewer("2.1.0-beta.10", "2.1.0-beta.2") && UpdateVersion.IsNewer("2.1.0-rc", "2.1.0-beta"), "update: pre-release ordering follows semver");
            check(!UpdateVersion.IsNewer("abc", "2.1.0") && !UpdateVersion.IsNewer("", "2.1.0") && !UpdateVersion.IsNewer("2.1.1; rm -rf", "2.1.0") && !UpdateVersion.IsNewer("99999999999.1", "2.1.0") && !UpdateVersion.IsNewer("2.1.1", "garbage") && !UpdateVersion.IsNewer(null, "2.1.0"), "update: garbage version text is never 'newer'");
            check(!UpdateVersion.IsNewer("2.1.0", "2.1.0") && !UpdateVersion.IsNewer("2.0.8", "2.1.0") && UpdateVersion.IsNewer("3.0.0", "2.99.99"), "update: equal and older versions are not offered");
            check(UpdateVersion.IsNewer("2.1.0+build.7", "2.0.9") && UpdateVersion.Compare("2.1.0+a", "2.1.0+b") == 0, "update: build metadata is ignored");

            // ---- manifest parsing (the live server format)
            string live = "{\n  \"latest\": \"2.0.9\",\n  \"date\": \"2026-10-24\",\n  \"filename\": \"ClearDesk-2.0.9.zip\",\n  \"file\": \"downloads/ClearDesk-2.0.9.zip\",\n  \"alias\": \"downloads/ClearDesk-latest.zip\",\n  \"size\": 6156947,\n  \"sha256\": \"BAF3B5BA7EAC7721B86E34EAD227817AB851E8780392CBFFB4698B4DB1988F2D\",\n  \"notes\": \"release-notes/2.0.9.html\",\n  \"github\": \"https://github.com/visual-life/ClearDesk/releases\"\n}";
            var lm = UpdateManifest.Parse(live);
            check(lm.Latest == "2.0.9" && lm.Size == 6156947 && lm.Sha256 == "baf3b5ba7eac7721b86e34ead227817ab851e8780392cbffb4698b4db1988f2d" && lm.FileName == "ClearDesk-2.0.9.zip" && lm.Signature == "" && lm.Mirrors.Count == 0, "update: the existing server versions.json parses (unsigned)");
            check(lm.ValidateInstallable() != null && lm.ValidateInstallable().Contains("签名"), "update: an unsigned manifest is not installable");
            check(Throws(() => UpdateManifest.Parse("")) && Throws(() => UpdateManifest.Parse("not json")) && Throws(() => UpdateManifest.Parse("{\"latest\":\"nope\"}")) && Throws(() => UpdateManifest.Parse("[1,2]")) && Throws(() => UpdateManifest.Parse(new string(' ', 300000) + "{}")), "update: malformed / oversized manifests are rejected");
            var rich = UpdateManifest.Parse("{\"latest\":\"2.1.1\",\"size\":\"123\",\"sha256\":\"" + new string('a', 64) + "\",\"filename\":\"ClearDesk-2.1.1.zip\",\"file\":\"downloads/ClearDesk-2.1.1.zip\",\"signature\":\"s\",\"mirrors\":[\"http://a.test/\",\"http://b.test/\"],\"changes\":[\"x\",\"y\"],\"updateUrl\":\"https://cleardesk.test/\"}");
            check(rich.Size == 123 && rich.Mirrors.Count == 2 && rich.Changes.Count == 2 && rich.UpdateUrl == "https://cleardesk.test/" && rich.ValidateInstallable() == null, "update: optional mirrors / changes / updateUrl fields parse");
            var bad = UpdateManifest.Parse("{\"latest\":\"2.1.1\",\"size\":10,\"sha256\":\"" + new string('a', 64) + "\",\"filename\":\"..\\\\evil.zip\",\"file\":\"x\",\"signature\":\"s\"}");
            check(bad.ValidateInstallable() != null, "update: path-like package file names are refused");
            bad.FileName = "ClearDesk-2.1.1.zip"; bad.Sha256 = "zz"; check(bad.ValidateInstallable() != null, "update: invalid sha256 text is refused");
            bad.Sha256 = new string('a', 64); bad.Size = UpdateManifest.MaxPackage + 1; check(bad.ValidateInstallable() != null, "update: absurd package sizes are refused");

            // ---- hashing and signatures
            check(UpdateCrypto.Sha256Hex(Encoding.ASCII.GetBytes("abc")) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "update: SHA-256 known-answer test");
            string pub; string pub2;
            using (var key = NewKey(out pub))
            using (var other = NewKey(out pub2))
            {
                byte[] payload = Encoding.UTF8.GetBytes("hello");
                string sig = Sign(key, payload);
                check(UpdateCrypto.Verify(payload, sig, pub), "update: ECDSA P-256 signature verifies");
                check(!UpdateCrypto.Verify(Encoding.UTF8.GetBytes("hellp"), sig, pub), "update: a changed payload fails verification");
                check(!UpdateCrypto.Verify(payload, sig, pub2), "update: a signature from another key fails");
                check(!UpdateCrypto.Verify(payload, "", pub) && !UpdateCrypto.Verify(payload, "!!notbase64!!", pub) && !UpdateCrypto.Verify(payload, Convert.ToBase64String(new byte[10]), pub) && !UpdateCrypto.Verify(payload, sig, "") && !UpdateCrypto.Verify(payload, sig, "AAAA") && !UpdateCrypto.Verify(null, sig, pub), "update: empty / garbage signatures and keys never verify (and never throw)");
                var zero = new byte[64]; check(!UpdateCrypto.Verify(payload, Convert.ToBase64String(zero), pub), "update: an all-zero signature is rejected");

                var m = Manifest("2.1.1", Encoding.ASCII.GetBytes("zip-bytes"), key);
                check(UpdateCrypto.VerifyManifest(m, pub), "update: a signed manifest verifies");
                Func<Action<UpdateManifest>, bool> tampered = mod => { var c = Manifest("2.1.1", Encoding.ASCII.GetBytes("zip-bytes"), key); c.Signature = m.Signature; mod(c); return UpdateCrypto.VerifyManifest(c, pub); };
                check(!tampered(c => c.Latest = "2.1.2") && !tampered(c => c.Size += 1) && !tampered(c => c.Sha256 = new string('b', 64)) && !tampered(c => c.FileName = "ClearDesk-9.zip") && !tampered(c => c.UpdateUrl = "http://evil.test/") && !tampered(c => c.Mirrors.Add("http://evil.test/")), "update: changing version, size, hash, file name, updateUrl or mirrors invalidates the signature");
                check(tampered(c => { c.Changes.Add("anything"); c.Notes = "n"; c.Date = "x"; c.Github = "g"; }), "update: notes / changes / dates are presentation only (not signed)");
                var stripped = UpdateManifest.Parse(ToJson(m)); stripped.Signature = "";
                check(!UpdateCrypto.VerifyManifest(stripped, pub), "update: a manifest with the signature stripped is rejected");
                using (var prod = CngKey.Import(Convert.FromBase64String(UpdateCrypto.ProductionPublicKey), CngKeyBlobFormat.EccPublicBlob)) check(prod.Algorithm == CngAlgorithm.ECDsaP256, "update: the embedded production public key is a valid ECDSA P-256 key");
                check(!UpdateCrypto.VerifyManifest(m, UpdateCrypto.ProductionPublicKey), "update: a manifest signed with a throw-away key is rejected by the production key");

                // ---- fetch + fallback (network replaced by a delegate)
                var good = Manifest("2.1.1", Encoding.ASCII.GetBytes("z1"), key); good.Changes.Add("c1");
                string goodJson = ToJson(good);
                var evil = Manifest("9.9.9", Encoding.ASCII.GetBytes("z2"), key); evil.Size += 5;   // signature no longer matches
                string evilJson = ToJson(evil);
                var calls = new List<string>();
                UpdateClient.TextFetcher = url => { calls.Add(url); if (url.Contains("dead")) return null; if (url.Contains("evil")) return evilJson; if (url.Contains("unsigned")) return live; if (url.Contains("good")) return goodJson; if (url.Contains("api.github.com")) return null; return null; };
                try
                {
                    var r = UpdateClient.Check("2.1.0", new[] { "http://dead.test/versions.json", "http://good.test/versions.json" }, false, pub);
                    check(r.Completed && r.Newer && r.Manifest.Latest == "2.1.1" && r.Manifest.Signed && r.Source.Contains("good.test") && r.Manifest.Changes[0] == "c1", "update: an unreachable source falls through to the next one");
                    r = UpdateClient.Check("2.1.0", new[] { "http://evil.test/versions.json", "http://good.test/versions.json" }, false, pub);
                    check(r.Completed && r.Manifest.Latest == "2.1.1" && r.Log.Any(l => l.Contains("签名无效")), "update: a tampered manifest is skipped (never trusted) and the next source is used");
                    r = UpdateClient.Check("2.1.0", new[] { "http://evil.test/versions.json" }, false, pub);
                    check(!r.Completed && r.Error.Contains("签名"), "update: only a tampered manifest → no update, clear error");
                    r = UpdateClient.Check("2.1.0", new[] { "http://unsigned.test/versions.json" }, false, pub);
                    check(!r.Completed && r.Error.Contains("没有签名"), "update: an unsigned manifest is refused");
                    r = UpdateClient.Check("2.1.1", new[] { "http://good.test/versions.json" }, false, pub);
                    check(r.Completed && !r.Newer, "update: same version → up to date");
                    r = UpdateClient.Check("2.1.0", new[] { "http://dead.test/versions.json" }, true, pub);
                    check(!r.Completed && r.Error.Length > 0 && calls.Any(c => c.Contains("api.github.com")), "update: all sources down and GitHub down → error, GitHub was tried as the fallback");
                }
                finally { UpdateClient.TextFetcher = null; }

                // ---- GitHub fallback parsing
                string gh = "{\"tag_name\":\"v2.1.1\",\"html_url\":\"https://github.com/visual-life/ClearDesk/releases/tag/v2.1.1\",\"body\":\"- fix\",\"published_at\":\"2026-11-02T10:00:00Z\",\"assets\":[{\"name\":\"ClearDesk-2.1.1.zip\",\"browser_download_url\":\"https://github.com/x/ClearDesk-2.1.1.zip\"}]}";
                var gm = UpdateClient.FromGithub(gh, pub);
                check(gm.NotifyOnly && !gm.Signed && gm.Latest == "2.1.1" && gm.Date == "2026-11-02" && gm.PageUrl.Contains("releases/tag") && gm.ValidateInstallable() != null, "update: a GitHub release without a signed manifest is notify-only, never installable");
                var gsigned = Manifest("2.1.1", Encoding.ASCII.GetBytes("gh-zip"), key); gsigned.File = ""; gsigned.Alias = "";
                string gsJson = ToJson(gsigned);
                string gh2 = "{\"tag_name\":\"v2.1.1\",\"html_url\":\"https://github.com/r\",\"assets\":[{\"name\":\"versions.json\",\"browser_download_url\":\"https://github.com/x/versions.json\"},{\"name\":\"ClearDesk-2.1.1.zip\",\"browser_download_url\":\"https://github.com/x/ClearDesk-2.1.1.zip\"}]}";
                UpdateClient.TextFetcher = url => url.EndsWith("versions.json") ? gsJson : null;
                try { var g2 = UpdateClient.FromGithub(gh2, pub); check(!g2.NotifyOnly && g2.Signed && g2.File == "https://github.com/x/ClearDesk-2.1.1.zip" && UpdateClient.DownloadUrls(g2, null).Contains("https://github.com/x/ClearDesk-2.1.1.zip"), "update: a GitHub release that carries a signed versions.json asset becomes installable"); }
                finally { UpdateClient.TextFetcher = null; }
                check(Throws(() => UpdateClient.FromGithub("{}", pub)) && Throws(() => UpdateClient.FromGithub("{\"tag_name\":\"latest\"}", pub)), "update: malformed GitHub responses are rejected");

                // ---- download: resume, corruption, ignored Range, cancel
                UpdateClient.RetryDelayMs = 0;
                byte[] zip = new byte[300000]; new Random(7).NextBytes(zip);
                var dm = Manifest("2.1.1", zip, key); string dir = Scratch();
                try
                {
                    var starts = new List<long>();
                    UpdateClient.StreamOpener = (url, start) => { starts.Add(start); return new KeyValuePair<int, Stream>(start > 0 ? 206 : 200, new Flaky(zip, (int)start, starts.Count == 1 ? 120000 : -1)); };
                    var dr = UpdateClient.Download(dm, null, dir, null, CancellationToken.None);
                    check(dr.Ok && File.ReadAllBytes(dr.Path).SequenceEqual(zip) && starts.Count >= 2 && starts[0] == 0 && starts[1] == 120000 && !File.Exists(dr.Path + ".part"), "update: an interrupted download resumes from the bytes already stored (Range) and verifies");
                    File.Delete(dr.Path);
                    starts.Clear();
                    UpdateClient.StreamOpener = (url, start) => { starts.Add(start); return new KeyValuePair<int, Stream>(200, new Flaky(zip, 0, starts.Count == 1 ? 50000 : -1)); };   // server ignores Range
                    dr = UpdateClient.Download(dm, null, dir, null, CancellationToken.None);
                    check(dr.Ok && File.ReadAllBytes(dr.Path).SequenceEqual(zip), "update: a server that ignores Range makes the client start over and still verify");
                    var again = UpdateClient.Download(dm, null, dir, null, CancellationToken.None);
                    check(again.Ok && starts.Count == 2, "update: an already downloaded, verified package is not downloaded again");
                    File.Delete(dr.Path);

                    var corrupt = (byte[])zip.Clone(); corrupt[1000] ^= 0xFF;
                    UpdateClient.StreamOpener = (url, start) => new KeyValuePair<int, Stream>(start > 0 ? 206 : 200, new Flaky(corrupt, (int)start, -1));
                    dr = UpdateClient.Download(dm, null, dir, null, CancellationToken.None);
                    check(!dr.Ok && dr.Error.Contains("SHA256") && !File.Exists(Path.Combine(dir, dm.FileName)) && !File.Exists(Path.Combine(dir, dm.FileName) + ".part"), "update: a package whose SHA-256 differs from the signed manifest is rejected and deleted");
                    var longer = zip.Concat(new byte[10]).ToArray();
                    UpdateClient.StreamOpener = (url, start) => new KeyValuePair<int, Stream>(start > 0 ? 206 : 200, new Flaky(longer, (int)start, -1));
                    dr = UpdateClient.Download(dm, null, dir, null, CancellationToken.None);
                    check(!dr.Ok && !File.Exists(Path.Combine(dir, dm.FileName)), "update: more data than the manifest's size is rejected");
                    var shortZip = zip.Take(1000).ToArray();
                    UpdateClient.StreamOpener = (url, start) => new KeyValuePair<int, Stream>(start > 0 ? 206 : 200, new Flaky(shortZip, (int)start, -1));
                    dr = UpdateClient.Download(dm, null, dir, null, CancellationToken.None);
                    check(!dr.Ok && !File.Exists(Path.Combine(dir, dm.FileName)), "update: a short package (wrong size) is rejected");
                    // stale partial file from a different release must not be resumed
                    string part = Path.Combine(dir, dm.FileName) + ".part"; File.WriteAllBytes(part, new byte[500]); File.WriteAllText(part + ".json", "{\"sha256\":\"other\",\"size\":1,\"url\":\"u\"}");
                    starts.Clear(); UpdateClient.StreamOpener = (url, start) => { starts.Add(start); return new KeyValuePair<int, Stream>(start > 0 ? 206 : 200, new Flaky(zip, (int)start, -1)); };
                    dr = UpdateClient.Download(dm, null, dir, null, CancellationToken.None);
                    check(dr.Ok && starts[0] == 0, "update: a leftover partial file that belongs to another release is discarded, not resumed");
                    File.Delete(dr.Path);
                    var cts = new CancellationTokenSource(); cts.Cancel();
                    dr = UpdateClient.Download(dm, null, dir, null, cts.Token);
                    check(!dr.Ok && dr.Error.Contains("取消"), "update: cancelling the download stops it");
                    var unsigned = Manifest("2.1.1", zip, null); unsigned.Signature = "";
                    check(!UpdateClient.Download(unsigned, null, dir, null, CancellationToken.None).Ok, "update: the downloader refuses a manifest without a signature");
                }
                finally { UpdateClient.StreamOpener = null; UpdateClient.RetryDelayMs = 300; try { Directory.Delete(dir, true); } catch { } }
            }

            // ---- sources
            check(UpdateSources.Normalize("http://a.test/") == "http://a.test/versions.json" && UpdateSources.Normalize("https://a.test") == "https://a.test/versions.json" && UpdateSources.Normalize("http://a.test/x/versions.json") == "http://a.test/x/versions.json" && UpdateSources.Normalize("http://a.test/app?x=1") == "http://a.test/app/versions.json", "update: source URLs are normalised to .../versions.json");
            check(UpdateSources.Normalize("ftp://a.test/") == null && UpdateSources.Normalize("javascript:alert(1)") == null && UpdateSources.Normalize("file:///c:/x.json") == null && UpdateSources.Normalize("") == null && UpdateSources.Normalize("not a url") == null && UpdateSources.Normalize(null) == null, "update: only http(s) sources are accepted");
            check(UpdateSources.Defaults.Length == 1 && UpdateSources.Defaults[0] == "http://101.200.206.248/versions.json", "update: default source is the Alibaba Cloud server");
            var order = UpdateSources.Resolve("http://user.test/", new UpdateConfig { Sources = { "http://file.test/versions.json", "http://user.test/versions.json" } }, "https://moved.test/", new[] { "http://m1.test/", "https://moved.test/versions.json" });
            check(order[0] == "http://user.test/versions.json" && order[1] == "http://file.test/versions.json" && order[2] == "https://moved.test/versions.json" && order[3] == "http://m1.test/versions.json" && order.Last() == UpdateSources.Defaults[0] && order.Count == 5, "update: source order is user setting → config file → learned redirect → mirrors → built-in default (no duplicates)");
            check(UpdateSources.Resolve("", null, "", null).SequenceEqual(UpdateSources.Defaults), "update: with nothing configured only the default source is used");
            string cfgA = Scratch(), cfgB = Scratch();
            try
            {
                File.WriteAllText(Path.Combine(cfgA, "update-source.json"), "{\"sources\":[\"https://a.test/\",\"javascript:x\"],\"github\":false}");
                File.WriteAllText(Path.Combine(cfgB, "update-source.json"), "{\"sources\":[\"https://b.test/\"]}");
                var c1 = UpdateSources.ReadConfig(cfgA, cfgB); var c2 = UpdateSources.ReadConfig(Path.Combine(cfgA, "nope"), cfgB);
                check(c1 != null && c1.Sources.Count == 1 && c1.Sources[0] == "https://a.test/versions.json" && !c1.GithubFallback && c2.Sources[0] == "https://b.test/versions.json" && c2.GithubFallback, "update: update-source.json is read (data folder first, then install folder) and can disable the GitHub fallback");
                File.WriteAllText(Path.Combine(cfgA, "update-source.json"), "{ broken");
                check(UpdateSources.ReadConfig(cfgA) == null, "update: a broken update-source.json is ignored");
            }
            finally { try { Directory.Delete(cfgA, true); Directory.Delete(cfgB, true); } catch { } }
            check(UpdateClient.Resolve("http://h.test/a/versions.json", "downloads/x.zip") == "http://h.test/a/downloads/x.zip" && UpdateClient.Resolve("http://h.test/a/versions.json", "https://cdn.test/x.zip") == "https://cdn.test/x.zip" && UpdateClient.Resolve("http://h.test/a/versions.json", "javascript:1") == null && UpdateClient.Resolve("http://h.test/a/versions.json", "file:///c:/x.zip") == null && UpdateClient.Resolve("http://h.test/a/versions.json", "") == null, "update: download paths resolve relative to the manifest, only http(s)");
            var um = new UpdateManifest { Latest = "2.1.1", File = "downloads/ClearDesk-2.1.1.zip", Alias = "downloads/ClearDesk-latest.zip", SourceUrl = "http://old.test/versions.json", Mirrors = { "https://new.test/" } };
            var urls = UpdateClient.DownloadUrls(um, new[] { "http://x.test/versions.json" });
            check(urls[0] == "http://old.test/downloads/ClearDesk-2.1.1.zip" && urls.Contains("https://new.test/downloads/ClearDesk-2.1.1.zip") && urls.Contains("http://x.test/downloads/ClearDesk-latest.zip"), "update: download tries the serving source first, then mirrors and configured sources");

            // ---- package layout and safety
            check(UpdatePackage.SafeRelative("a/b.txt") == "a/b.txt" && UpdatePackage.SafeRelative("a\\b.txt") == "a/b.txt" && UpdatePackage.SafeRelative("dir/") == "dir", "update: normal package paths are accepted (forward or back slashes)");
            check(new[] { "../x", "..\\x", "a/../../x", "/etc/x", "C:\\x", "C:/x", "a/b:stream", "CON", "a/NUL.txt", "a//b", "x.", "a/ ", "", null, "a/./b" }.All(p => UpdatePackage.SafeRelative(p) == null), "update: zip-slip, rooted, drive, stream, device-name and empty paths are refused");
            check(UpdatePackage.FindPrefix(new[] { "ClearDesk-2.1.0/ClearDesk.exe", "ClearDesk-2.1.0/a.txt" }) == "ClearDesk-2.1.0/" && UpdatePackage.FindPrefix(new[] { "ClearDesk.exe", "x.txt" }) == "" && UpdatePackage.FindPrefix(new[] { "ClearDesk-2.1.0\\ClearDesk.exe" }) == "ClearDesk-2.1.0/", "update: payload root is found for a single top folder, a flat zip and backslash entries");
            check(Throws(() => UpdatePackage.FindPrefix(new[] { "readme.txt" })) && Throws(() => UpdatePackage.FindPrefix(new[] { "a/ClearDesk.exe", "b/ClearDesk.exe" })) && Throws(() => UpdatePackage.FindPrefix(new[] { "a/b/ClearDesk.exe" })), "update: packages without / with several / with a deeply nested ClearDesk.exe are refused");
            string pd = Scratch();
            try
            {
                Func<string, byte[], string> stage = (name, bytes) => { string z = Path.Combine(pd, name + ".zip"); File.WriteAllBytes(z, bytes); return z; };
                var pkg = UpdatePackage.Stage(stage("ok", BuildZip("ClearDesk-2.1.1/", Payload("n"))), Path.Combine(pd, "s1"));
                check(pkg.Files.Contains("ClearDesk.exe") && pkg.Files.Contains("taskbar/verified.sha256") && File.Exists(Path.Combine(pd, "s1", "taskbar", "verified.sha256")), "update: a normal package stages, nested folders included");
                pkg = UpdatePackage.Stage(stage("bs", BuildZip("ClearDesk-2.1.1/", Payload("n"), true)), Path.Combine(pd, "s2"));
                check(File.Exists(Path.Combine(pd, "s2", "source", "a.cs")), "update: a zip with backslash entries stages correctly");
                check(Throws(() => UpdatePackage.Stage(stage("tamper", BuildZip("ClearDesk-2.1.1/", Payload("n"), false, true, (k, v) => k == "source/a.cs" ? Encoding.ASCII.GetBytes("evil") : v)), Path.Combine(pd, "s3"))), "update: an entry changed after SHA256SUMS.txt was made is detected");
                check(Throws(() => UpdatePackage.Stage(stage("nosums", BuildZip("ClearDesk-2.1.1/", Payload("n"), false, false)), Path.Combine(pd, "s4"))), "update: a package without SHA256SUMS.txt is refused");
                check(Throws(() => UpdatePackage.Stage(stage("slip", BuildZip("ClearDesk-2.1.1/", Payload("n"), false, true, null, "ClearDesk-2.1.1/../../evil.txt")), Path.Combine(pd, "s5"))) && !File.Exists(Path.Combine(pd, "evil.txt")), "update: a zip-slip entry is refused and nothing is written outside the staging folder");
                check(Throws(() => UpdatePackage.Stage(stage("extra", BuildZip("ClearDesk-2.1.1/", Payload("n"), false, true, null, "ClearDesk-2.1.1/unlisted.dll")), Path.Combine(pd, "s6"))), "update: a file not listed in SHA256SUMS.txt is refused");
                var noExe = Payload("n"); noExe.Remove("ClearDesk.exe"); noExe["ClearDesk.exe.config"] = Encoding.ASCII.GetBytes("x");
                check(Throws(() => UpdatePackage.Stage(stage("noexe", BuildZip("ClearDesk-2.1.1/", noExe)), Path.Combine(pd, "s7"))), "update: a package without ClearDesk.exe is refused");
                var notPe = Payload("n"); notPe["ClearDesk.exe"] = Encoding.ASCII.GetBytes("this is not an exe");
                check(Throws(() => UpdatePackage.Stage(stage("nope", BuildZip("ClearDesk-2.1.1/", notPe)), Path.Combine(pd, "s8"))), "update: a ClearDesk.exe that is not a Windows program is refused");
                File.WriteAllBytes(Path.Combine(pd, "trunc.zip"), BuildZip("ClearDesk-2.1.1/", Payload("n")).Take(200).ToArray());
                check(Throws(() => UpdatePackage.Stage(Path.Combine(pd, "trunc.zip"), Path.Combine(pd, "s9"))), "update: a truncated zip is refused");
            }
            finally { try { Directory.Delete(pd, true); } catch { } }

            // ---- apply / rollback (scratch folders, no processes started)
            ApplyRules(check);
        }

        sealed class Fx { public string Root, Stage, Target, Data; }
        static Fx Fixture()
        {
            var f = new Fx { Root = Scratch() }; f.Stage = Path.Combine(f.Root, "stage"); f.Target = Path.Combine(f.Root, "ClearDesk-2.1.0"); f.Data = Path.Combine(f.Root, "data");
            Put(f.Target, "ClearDesk.exe", "MZ-OLD"); Put(f.Target, "a.txt", "old a"); Put(f.Target, "same.dll", "same"); Put(f.Target, "old-only.txt", "gone soon"); Put(f.Target, "sub/s.txt", "old sub");
            Put(f.Target, "user-notes.txt", "mine"); Put(f.Target, "update-source.json", "{\"sources\":[\"http://mine.test/\"]}"); Put(f.Target, "preview-data/settings.json", "keep");
            WriteSums(f.Target, "ClearDesk.exe", "a.txt", "same.dll", "old-only.txt", "sub/s.txt");
            Put(f.Stage, "ClearDesk.exe", "MZ-NEW"); Put(f.Stage, "a.txt", "new a"); Put(f.Stage, "same.dll", "same"); Put(f.Stage, "newfile.txt", "brand new"); Put(f.Stage, "sub/s.txt", "new sub"); Put(f.Stage, "sub2/deep/n.txt", "deep");
            WriteSums(f.Stage, "ClearDesk.exe", "a.txt", "same.dll", "newfile.txt", "sub/s.txt", "sub2/deep/n.txt");
            Directory.CreateDirectory(f.Data); return f;
        }
        static ApplyOptions Opts(Fx f) { return new ApplyOptions { Stage = f.Stage, Target = f.Target, Data = f.Data, FromVersion = "2.1.0", ToVersion = "2.1.1", NoRestart = true, WaitMs = 300 }; }
        static bool Pristine(Fx f)
        {
            return Get(f.Target, "ClearDesk.exe") == "MZ-OLD" && Get(f.Target, "a.txt") == "old a" && Get(f.Target, "old-only.txt") == "gone soon" && Get(f.Target, "sub/s.txt") == "old sub" && Get(f.Target, "newfile.txt") == null && Get(f.Target, "sub2/deep/n.txt") == null
                && Get(f.Target, "user-notes.txt") == "mine" && !Directory.EnumerateFiles(f.Target, "*.cdold-*", SearchOption.AllDirectories).Any();
        }

        static void ApplyRules(Action<bool, string> check)
        {
            Fx f = Fixture();
            try
            {
                var order = new List<string>(); var o = Opts(f); o.FailFor = rel => { order.Add(rel); return false; };
                var r = UpdateApplier.Apply(o);
                check(r.Ok && Get(f.Target, "ClearDesk.exe") == "MZ-NEW" && Get(f.Target, "a.txt") == "new a" && Get(f.Target, "newfile.txt") == "brand new" && Get(f.Target, "sub/s.txt") == "new sub" && Get(f.Target, "sub2/deep/n.txt") == "deep", "update apply: changed files are replaced, new files (incl. new folders) are added");
                check(Get(f.Target, "old-only.txt") == null, "update apply: a file that existed only in the old release (and was untouched) is removed");
                check(Get(f.Target, "user-notes.txt") == "mine" && Get(f.Target, "update-source.json").Contains("mine.test") && Get(f.Target, "preview-data/settings.json") == "keep", "update apply: files the user owns (notes, update-source.json, preview-data) are never touched");
                check(!order.Contains("same.dll") && order.IndexOf("ClearDesk.exe") == order.Count - 2 && order.Last() == "SHA256SUMS.txt", "update apply: unchanged files are skipped and ClearDesk.exe is replaced last (before the checksum list)");
                check(!Directory.EnumerateFiles(f.Target, "*.cdold-*", SearchOption.AllDirectories).Any() && !File.Exists(UpdateApplier.JournalPath(f.Data)) && !Directory.Exists(f.Stage), "update apply: no leftovers (old copies, journal, staging folder)");
                string bk = Path.Combine(f.Data, "update", "backup", "2.1.0");
                check(File.ReadAllText(Path.Combine(bk, "ClearDesk.exe")) == "MZ-OLD" && File.ReadAllText(Path.Combine(bk, "a.txt")) == "old a" && File.ReadAllText(Path.Combine(bk, "old-only.txt")) == "gone soon" && !File.Exists(Path.Combine(bk, "same.dll")), "update apply: replaced and removed files are backed up under update\\backup\\<old version>");
                check(Get(f.Data, "update-result.json").Contains("\"ok\":true") && Get(f.Target, "SHA256SUMS.txt").Contains("newfile.txt"), "update apply: result file written, checksum list updated");
            }
            finally { try { Directory.Delete(f.Root, true); } catch { } }

            f = Fixture();
            try
            {
                var o = Opts(f); o.FailFor = rel => rel == "ClearDesk.exe";
                var r = UpdateApplier.Apply(o);
                check(!r.Ok && r.RolledBack && Pristine(f) && Get(f.Target, "same.dll") == "same", "update rollback: a failure while swapping the exe restores every file and removes added ones");
                check(r.Message.Contains("恢复") && Get(f.Data, "update-result.json").Contains("\"ok\":false") && !File.Exists(UpdateApplier.JournalPath(f.Data)), "update rollback: message says the old version was restored, journal cleaned");
            }
            finally { try { Directory.Delete(f.Root, true); } catch { } }

            f = Fixture();
            try
            {
                using (var lockExe = new FileStream(Path.Combine(f.Target, "ClearDesk.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var r = UpdateApplier.Apply(Opts(f));
                    check(!r.Ok && r.RolledBack, "update rollback: a genuinely locked ClearDesk.exe makes the swap fail and roll back");
                }
                check(Pristine(f), "update rollback: after a real lock failure the install folder is exactly as before");
            }
            finally { try { Directory.Delete(f.Root, true); } catch { } }

            f = Fixture();
            try
            {
                File.WriteAllText(Path.Combine(f.Stage, "newfile.txt"), "tampered after staging");
                var r = UpdateApplier.Apply(Opts(f));
                check(!r.Ok && !r.RolledBack && r.Message.Contains("没有被改动") && Pristine(f), "update apply: a staged file that no longer matches its checksum aborts before anything is touched");
            }
            finally { try { Directory.Delete(f.Root, true); } catch { } }

            f = Fixture();
            try
            {
                // simulate a crash in the middle of a swap: a.txt moved aside, new file added, journal on disk
                string aOld = Path.Combine(f.Target, "a.txt.cdold-crash0"), nf = Path.Combine(f.Target, "newfile.txt");
                File.Move(Path.Combine(f.Target, "a.txt"), aOld); File.WriteAllText(Path.Combine(f.Target, "a.txt"), "half new"); File.WriteAllText(nf, "half");
                var j = new UpdateJournal { From = "2.1.0", To = "2.1.1" };
                j.Entries.Add(new JournalEntry { Kind = "replace", Dest = Path.Combine(f.Target, "a.txt"), Old = aOld }); j.Entries.Add(new JournalEntry { Kind = "add", Dest = nf });
                Directory.CreateDirectory(Path.GetDirectoryName(UpdateApplier.JournalPath(f.Data))); File.WriteAllText(UpdateApplier.JournalPath(f.Data), new JavaScriptSerializer().Serialize(j));
                check(UpdateApplier.RecoverInterrupted(f.Data, false) && Pristine(f) && !File.Exists(UpdateApplier.JournalPath(f.Data)), "update recovery: an interrupted swap (crash / power loss) is undone at the next start");
                check(!UpdateApplier.RecoverInterrupted(f.Data, false), "update recovery: nothing to do when there is no journal");
            }
            finally { try { Directory.Delete(f.Root, true); } catch { } }

            f = Fixture();
            try
            {
                int restored = 0;
                File.WriteAllText(Path.Combine(f.Data, "recovery-v11.json"), new JavaScriptSerializer().Serialize(new RestoreState { TopTaskbarHidden = true }));
                var o = Opts(f); o.RestoreTaskbar = () => restored++;
                UpdateApplier.Apply(o);
                check(restored == 1 && !File.Exists(Path.Combine(f.Data, "recovery-v11.json")), "update apply: a top taskbar left hidden by a crashed session is handed back before files are replaced");
            }
            finally { try { Directory.Delete(f.Root, true); } catch { } }
            f = Fixture();
            try
            {
                int restored = 0; File.WriteAllText(Path.Combine(f.Data, "recovery-v11.json"), new JavaScriptSerializer().Serialize(new RestoreState { TopTaskbarHidden = false }));
                var o = Opts(f); o.RestoreTaskbar = () => restored++; UpdateApplier.Apply(o);
                check(restored == 0, "update apply: the taskbar is not touched when it was not hidden");
            }
            finally { try { Directory.Delete(f.Root, true); } catch { } }

            string w = Scratch();
            try
            {
                check(UpdateApplier.CanWrite(w) && !UpdateApplier.CanWrite(Path.Combine(w, "missing", "deeper")), "update: write-permission probe detects a usable / unusable install folder");
                string sys = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                bool elevated = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                check(elevated || !UpdateApplier.CanWrite(sys), "update: the Windows folder needs elevation for a standard user, so the app would show a message instead of updating");
            }
            finally { try { Directory.Delete(w, true); } catch { } }
            check(string.Join("|", UpdateApplier.RestartArgsFrom(new[] { "--startup", "--new-clock", "--preview", "--update-e2e", "--apply-update" })) == "--startup|--preview|--update-e2e" && UpdateApplier.RestartArgsFrom(null).Count == 0, "update: only --startup / --preview style switches are carried over to the restarted app");
            var parsed = UpdateApplier.ParseArgs(new[] { "--apply-update", "--stage", "C:\\a b\\s", "--target", "D:\\Clear Desk\\", "--data", "C:\\d", "--pid", "123", "--from", "2.1.0", "--to", "2.1.1", "--rarg", "--startup", "--silent" });
            check(parsed.Stage == "C:\\a b\\s" && parsed.Target == "D:\\Clear Desk" && parsed.Pid == 123 && parsed.RestartArgs.SequenceEqual(new[] { "--startup" }) && parsed.Silent && !parsed.NoRestart && UpdateApplier.QuoteArg("D:\\Clear Desk") == "\"D:\\Clear Desk\"", "update: updater helper arguments round-trip (paths with spaces)");
        }

        // ------------------------------------------------------------------ UI / state (runs inside the regression suite)
        static IEnumerable<T> Find<T>(DependencyObject o) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
            {
                var c = VisualTreeHelper.GetChild(o, i); if (c is T) yield return (T)c;
                foreach (var d in Find<T>(c)) yield return d;
            }
        }
        static async Task<bool> Until(Func<bool> cond, int ms = 6000) { var sw = System.Diagnostics.Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; await Task.Delay(50); } return cond(); }

        public static async Task Run(Action<bool, string> check)
        {
            var s = Program.S;
            string oSkip = s.SkipVersion, oRemind = s.RemindAfter, oLast = s.LastUpdateCheck, oSrc = s.UpdateSource, oRed = s.UpdateRedirect, oAuto = s.AutoUpdateCheck ? "1" : "0"; var oMir = s.UpdateMirrors;
            string oEnv = Environment.GetEnvironmentVariable("CLEARDESK_UPDATE_PUBKEY"); string pub; var key = NewKey(out pub);
            Environment.SetEnvironmentVariable("CLEARDESK_UPDATE_PUBKEY", pub);
            try
            {
                s.SkipVersion = ""; s.RemindAfter = ""; s.LastUpdateCheck = ""; s.UpdateSource = ""; s.UpdateRedirect = ""; s.UpdateMirrors = new List<string>();
                // ---- scheduling rules
                var now = DateTime.UtcNow;
                check(UpdateService.Due(now), "update: never checked → a background check is due");
                s.LastUpdateCheck = now.AddHours(-1).ToString("o"); check(!UpdateService.Due(now), "update: checked an hour ago → not due again (at most once per ~12 h)");
                s.LastUpdateCheck = now.AddHours(-13).ToString("o"); check(UpdateService.Due(now), "update: checked 13 hours ago → due");
                s.LastUpdateCheck = now.AddHours(5).ToString("o"); check(UpdateService.Due(now), "update: a clock set backwards cannot suppress checks forever");
                s.LastUpdateCheck = "garbage"; check(UpdateService.Due(now), "update: unreadable last-check text counts as never");
                var newer = new UpdateManifest { Latest = "99.0.0" }; var same = new UpdateManifest { Latest = UpdateService.Version };
                check(UpdateService.ShouldNotify(newer, now) && !UpdateService.ShouldNotify(same, now) && !UpdateService.ShouldNotify(null, now), "update: only a newer version is announced");
                s.SkipVersion = "99.0.0"; check(!UpdateService.ShouldNotify(newer, now) && UpdateService.ShouldNotify(new UpdateManifest { Latest = "99.0.1" }, now), "update: 跳过此版本 silences that version but not the next one");
                s.SkipVersion = ""; s.RemindAfter = now.AddHours(3).ToString("o"); check(!UpdateService.ShouldNotify(newer, now) && UpdateService.ShouldNotify(newer, now.AddHours(4)), "update: 稍后提醒 silences the dialog until the reminder time");
                s.RemindAfter = "";

                // ---- redirect learning (signed manifests only)
                var learn = Manifest("99.0.0", new byte[] { 1 }, key); learn.UpdateUrl = "https://cleardesk.example/"; learn.Mirrors.Add("http://mirror.example/"); SignManifest(learn, key); learn.Signed = true;
                UpdateService.Record(new UpdateCheckResult { Manifest = learn });
                check(s.UpdateRedirect == "https://cleardesk.example/versions.json" && s.UpdateMirrors.Contains("http://mirror.example/versions.json") && UpdateService.Sources()[0] == "https://cleardesk.example/versions.json" && UpdateService.Sources().Last() == UpdateSources.Defaults[0] && !string.IsNullOrEmpty(s.LastUpdateCheck), "update: updateUrl / mirrors from a signed manifest redirect later checks to the new domain without a new build");
                s.UpdateRedirect = ""; s.UpdateMirrors = new List<string>(); var unsignedLearn = Manifest("99.0.0", new byte[] { 1 }, key); unsignedLearn.UpdateUrl = "http://evil.example/"; unsignedLearn.Signed = false;
                UpdateService.Record(new UpdateCheckResult { Manifest = unsignedLearn });
                check(s.UpdateRedirect == "", "update: an unverified manifest can never change the update source");
                s.LastUpdateCheck = "";

                // ---- settings page + dialog
                var signed = Manifest("99.0.0", new byte[] { 1, 2, 3 }, key); signed.Changes.AddRange(new[] { "新增在线更新", "修复若干问题" }); signed.Notes = "release-notes/99.0.0.html";
                string signedJson = ToJson(signed); var older = Manifest(UpdateService.Version, new byte[] { 9 }, key); string olderJson = ToJson(older);
                string reply = signedJson;
                UpdateClient.TextFetcher = url => url.StartsWith("http://upd.test/") ? reply : null;
                s.UpdateSource = "http://upd.test/";
                var window = new SettingsWindow(delegate { });
                try
                {
                    window.Show(); await Task.Delay(150); window.TestSelect(5); window.UpdateLayout();
                    var checkButton = Find<Button>(window).FirstOrDefault(b => AutomationProperties.GetName(b) == "检查更新");
                    var auto = Find<CheckBox>(window).FirstOrDefault(c => AutomationProperties.GetName(c) == "自动检查更新");
                    var source = Find<TextBox>(window).FirstOrDefault(t => AutomationProperties.GetName(t) == "更新源");
                    check(checkButton != null && auto != null && source != null && Find<TextBlock>(window).Any(t => t.Text.Contains("版本 " + UpdateService.Version)), "update settings: 关于 page has 检查更新, 自动检查更新 and 更新源 controls");
                    check(auto.IsChecked == s.AutoUpdateCheck, "update settings: the auto-check switch shows the saved value");
                    bool oldAuto = s.AutoUpdateCheck; auto.IsChecked = !oldAuto; auto.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); check(s.AutoUpdateCheck == !oldAuto, "update settings: the auto-check switch is saved"); s.AutoUpdateCheck = oldAuto;
                    source.Text = "javascript:alert(1)"; check(s.UpdateSource == "http://upd.test/", "update settings: an invalid update source is not saved");
                    source.Text = "http://upd.test/"; check(s.UpdateSource == "http://upd.test/", "update settings: a valid update source is saved");

                    reply = olderJson;
                    checkButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    check(await Until(() => Find<TextBlock>(window).Any(t => t.Text.Contains("已是最新版本"))), "update settings: 检查更新 reports 'already the latest' when the server has nothing newer");
                    check(!Application.Current.Windows.OfType<UpdateWindow>().Any(), "update settings: no dialog when already up to date");

                    reply = signedJson;
                    checkButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    check(await Until(() => Application.Current.Windows.OfType<UpdateWindow>().Any()), "update settings: a newer signed version opens the update dialog");
                    var dlg = Application.Current.Windows.OfType<UpdateWindow>().First(); await Task.Delay(150); dlg.UpdateLayout();
                    var names = Find<Button>(dlg).Select(b => AutomationProperties.GetName(b)).ToList();
                    check(names.Contains("立即更新") && names.Contains("稍后提醒") && names.Contains("跳过此版本"), "update dialog: has 立即更新 / 稍后提醒 / 跳过此版本");
                    check(Find<TextBlock>(dlg).Any(t => t.Text == "99.0.0") && Find<TextBlock>(dlg).Any(t => t.Text == UpdateService.Version) && dlg.TestNotes.Contains("新增在线更新") && Find<TextBlock>(dlg).Any(t => t.Text.Contains("已通过签名验证")), "update dialog: shows current and new version, release notes and the signature badge");
                    check(Find<TextBlock>(window).Any(t => t.Text.Contains("99.0.0")), "update settings: the status line names the new version");
                    dlg.TestLater.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Task.Delay(100);
                    check(s.RemindAfter.Length > 0 && !UpdateService.ShouldNotify(signed, DateTime.UtcNow) && !Application.Current.Windows.OfType<UpdateWindow>().Any(), "update dialog: 稍后提醒 closes it and delays the next automatic notice");
                    s.RemindAfter = "";
                    var dlg2 = UpdateWindow.ShowFor(signed, true); await Task.Delay(100);
                    dlg2.TestSkip.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Task.Delay(100);
                    check(s.SkipVersion == "99.0.0" && !UpdateService.ShouldNotify(signed, DateTime.UtcNow), "update dialog: 跳过此版本 remembers the version");
                    var np = UpdateWindow.ShowFor(new UpdateManifest { Latest = "99.0.0", NotifyOnly = true, PageUrl = "https://github.com/visual-life/ClearDesk/releases", NotesText = "- something" }, true); await Task.Delay(100);
                    check(np.TestNow.Content.ToString() == "打开下载页" && np.TestNotes.Contains("something"), "update dialog: an unsigned (GitHub-only) release offers 打开下载页 instead of installing");
                    np.Close();

                    // install refusal paths (nothing is downloaded or changed)
                    string err = await UpdateService.InstallAsync(new UpdateManifest { Latest = "99.0.0", NotifyOnly = true }, (a, b) => { }, CancellationToken.None);
                    check(err != null && err.Contains("手动"), "update install: notify-only releases are never installed");
                    var unsignedM = Manifest("99.0.0", new byte[] { 1 }, key); unsignedM.Signed = false;
                    err = await UpdateService.InstallAsync(unsignedM, (a, b) => { }, CancellationToken.None);
                    check(err != null && err.Contains("签名"), "update install: a manifest that did not pass signature verification is refused");
                    var selfM = Manifest(UpdateService.Version, new byte[] { 1 }, key); selfM.Signed = true;
                    err = await UpdateService.InstallAsync(selfM, (a, b) => { }, CancellationToken.None);
                    check(err != null && err.Contains("最新"), "update install: the running version is never 'updated' to itself");
                }
                finally { window.Close(); foreach (var w in Application.Current.Windows.OfType<UpdateWindow>().ToArray()) w.Close(); }
            }
            finally
            {
                UpdateClient.TextFetcher = null; Environment.SetEnvironmentVariable("CLEARDESK_UPDATE_PUBKEY", oEnv);
                key.Dispose();
                s.SkipVersion = oSkip; s.RemindAfter = oRemind; s.LastUpdateCheck = oLast; s.UpdateSource = oSrc; s.UpdateRedirect = oRed; s.UpdateMirrors = oMir ?? new List<string>(); s.AutoUpdateCheck = oAuto == "1";
            }
        }
    }

    // --preview --update-e2e : drives the real check → download → verify → hand-over flow for scripts/test-update.ps1.
    internal static class UpdateE2E
    {
        static string Data { get { return Program.Data; } }
        static void Done(string text) { File.WriteAllText(Path.Combine(Data, "e2e-done.txt"), text, new UTF8Encoding(false)); }
        internal static void Start()
        {
            string marker = Path.Combine(Data, "e2e-ran.txt");
            Program.App.Dispatcher.BeginInvoke(new Action(delegate
            {
                if (File.Exists(marker))
                {
                    string result = File.Exists(UpdateApplier.ResultPath(Data)) ? File.ReadAllText(UpdateApplier.ResultPath(Data)) : "";
                    Done("phase=after\nversion=" + UpdateService.Version + "\nresult=" + result + "\n"); Program.Quit(); return;
                }
                File.WriteAllText(marker, DateTime.Now.ToString("o"));
                Go();
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        static async void Go()
        {
            try
            {
                var r = await UpdateService.CheckAsync(); UpdateService.Record(r);
                string head = "version=" + UpdateService.Version + "\nnewer=" + r.Newer + "\nlatest=" + (r.Manifest == null ? "" : r.Manifest.Latest) + "\nerror=" + r.Error + "\nlog=" + string.Join(" | ", r.Log) + "\nredirect=" + Program.S.UpdateRedirect + "\n";
                if (!r.Newer) { Done("phase=check-only\n" + head); Program.Quit(); return; }
                string err = await UpdateService.InstallAsync(r.Manifest, (s, p) => { }, CancellationToken.None);
                if (err != null) { Done("phase=install-failed\n" + head + "install-error=" + err + "\n"); Program.Quit(); return; }
                File.WriteAllText(Path.Combine(Data, "e2e-handover.txt"), head);
                Program.Quit();
            }
            catch (Exception e) { Done("phase=exception\n" + e + "\n"); Program.Quit(); }
        }
    }
}
