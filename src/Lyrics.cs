using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ClearDesk
{
    internal sealed class LyricLine { internal double Seconds; internal string Text; }
    internal sealed class LyricsDocument
    {
        internal List<LyricLine> Lines=new List<LyricLine>();
        internal string Plain="", Message="", Credit="";
        internal bool FromClient;
        internal static LyricsDocument Parse(string text)
        {
            var result=new LyricsDocument(); text=text??"";
            var offsetMatch=Regex.Match(text,@"\[offset:([+-]?\d+)\]",RegexOptions.IgnoreCase);
            double offset=offsetMatch.Success ? double.Parse(offsetMatch.Groups[1].Value,CultureInfo.InvariantCulture)/1000 : 0;
            foreach(string raw in text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries))
            {
                var stamps=Regex.Matches(raw,@"\[(\d{1,3}):([0-5]\d)(?:[.:](\d{1,3}))?\]");
                if(stamps.Count==0) continue;
                string line=Regex.Replace(raw,@"\[[^\]]*\]","").Trim();
                foreach(Match stamp in stamps)
                {
                    double fraction=stamp.Groups[3].Success ? double.Parse("0."+stamp.Groups[3].Value,CultureInfo.InvariantCulture) : 0;
                    double second=int.Parse(stamp.Groups[1].Value)*60+int.Parse(stamp.Groups[2].Value)+fraction-offset;
                    result.Lines.Add(new LyricLine { Seconds=Math.Max(0,second),Text=line });
                }
            }
            result.Lines=result.Lines.OrderBy(l=>l.Seconds).GroupBy(l=>l.Seconds).Select(g=>new LyricLine { Seconds=g.Key,Text=string.Join(" / ",g.Select(l=>l.Text).Where(t=>t.Length>0).Distinct()) }).ToList();
            result.Plain=result.Lines.Count>0 ? string.Join("\n",result.Lines.Select(l=>l.Text)) : Regex.Replace(text,@"(?m)^\[(ar|ti|al|by|offset|length):[^\]]*\]\s*", "",RegexOptions.IgnoreCase).Trim();
            return result;
        }
        internal int At(TimeSpan position)
        {
            int low=0,high=Lines.Count-1,found=-1;
            while(low<=high) { int mid=(low+high)/2; if(Lines[mid].Seconds<=position.TotalSeconds) { found=mid; low=mid+1; } else high=mid-1; }
            return found;
        }
        internal static LyricsDocument FromFile(string path)
        {
            var info=new FileInfo(path); if(info.Length>1024*1024) throw new IOException("歌词文件不能超过 1 MB。");
            byte[] bytes=File.ReadAllBytes(path); string text;
            try { text=(bytes.Length>=2 && bytes[0]==255 && bytes[1]==254 ? Encoding.Unicode.GetString(bytes) : bytes.Length>=2 && bytes[0]==254 && bytes[1]==255 ? Encoding.BigEndianUnicode.GetString(bytes) : new UTF8Encoding(false,true).GetString(bytes)).TrimStart('\uFEFF'); }
            catch(DecoderFallbackException) { text=Encoding.GetEncoding(54936).GetString(bytes); }
            var result=Parse(text); result.Credit="本地歌词"; return result;
        }
    }
    internal interface ILyricsProvider
    {
        Task<LyricsDocument> ReadAsync(MusicSnapshot song,CancellationToken cancellation);
        void Invalidate(MusicSnapshot song);
    }
    internal sealed class LyricsProvider : ILyricsProvider
    {
        static readonly HttpClient client=new HttpClient(new HttpClientHandler { AllowAutoRedirect=false }) { Timeout=TimeSpan.FromSeconds(12) };
        readonly Func<string,CancellationToken,Task<string>> download;
        readonly Func<MusicSnapshot,CancellationToken,LyricsDocument> readClient;
        readonly Dictionary<string,LyricsDocument> cache=new Dictionary<string,LyricsDocument>();
        internal LyricsProvider(Func<string,CancellationToken,Task<string>> transport=null,Func<MusicSnapshot,CancellationToken,LyricsDocument> local=null)
        { download=transport??Download; readClient=local??new ClientLyrics().Read; }
        static string CacheKey(MusicSnapshot song) { return song.Source+"\n"+TrackKey(song); }
        internal static string TrackKey(MusicSnapshot track)
        {
            return (track.LocalPath??"")+"\n"+track.Title+"\n"+track.Artist+"\n"+track.Album+"\n"+Math.Round(track.Duration.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }
        static string Normalize(string value) { return Regex.Replace((value??"").Normalize(NormalizationForm.FormKC),@"\s+","").ToLowerInvariant(); }
        internal static LyricsDocument Decode(MusicSnapshot song,string json)
        {
            var data=new JavaScriptSerializer { MaxJsonLength=1024*1024 }.Deserialize<Dictionary<string,object>>(json);
            object value; Func<string,string> str=k=>data.TryGetValue(k,out value)?Convert.ToString(value,CultureInfo.InvariantCulture):"";
            double duration;
            if(Normalize(song.Title)!=Normalize(str("trackName")) || Normalize(song.Artist)!=Normalize(str("artistName")) ||
                (song.Duration.TotalSeconds>=1 && (!double.TryParse(str("duration"),NumberStyles.Float,CultureInfo.InvariantCulture,out duration) || Math.Abs(duration-song.Duration.TotalSeconds)>2)))
                return new LyricsDocument { Message="未找到匹配此版本的歌词" };
            if(data.TryGetValue("instrumental",out value) && value is bool && (bool)value) return new LyricsDocument { Message="纯音乐，请欣赏",Credit="LRCLIB" };
            string synced=str("syncedLyrics"),plain=str("plainLyrics");
            var result=LyricsDocument.Parse(string.IsNullOrWhiteSpace(synced)?plain:synced); result.Credit="LRCLIB";
            if(result.Plain.Length==0) result.Message="暂无匹配歌词";
            return result;
        }
        static async Task<string> Download(string url,CancellationToken cancellation)
        {
            using(var request=new HttpRequestMessage(HttpMethod.Get,url))
            {
                if(url.StartsWith("https://c.y.qq.com/",StringComparison.Ordinal))
                { request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) ClearDesk/2.0.5"); request.Headers.Referrer=new Uri("https://y.qq.com/portal/player.html"); }
                else if(url.StartsWith("https://music.163.com/",StringComparison.Ordinal))
                { request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) ClearDesk/2.0.5"); request.Headers.Referrer=new Uri("https://music.163.com/"); }
                else request.Headers.UserAgent.ParseAdd("ClearDesk/2.0.5 (+https://github.com/visual-life/ClearDesk)");
                using(var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellation).ConfigureAwait(false))
                {
                    if(response.StatusCode==HttpStatusCode.NotFound) return null;
                    response.EnsureSuccessStatusCode();
                    using(var stream=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using(var memory=new MemoryStream())
                    {
                        var buffer=new byte[8192]; int read;
                        while((read=await stream.ReadAsync(buffer,0,buffer.Length,cancellation).ConfigureAwait(false))>0)
                        { if(memory.Length+read>1024*1024) throw new IOException("歌词响应过大。"); memory.Write(buffer,0,read); }
                        return Encoding.UTF8.GetString(memory.ToArray());
                    }
                }
            }
        }
        // Fallback for NetEase: search the public web API by title + artist, then fetch the LRC by song id.
        // Sends only title/artist (and the matched id); no account, cookie or playlist data.
        internal async Task<LyricsDocument> ReadNetEaseOnline(MusicSnapshot song,CancellationToken cancellation)
        {
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                string search=await download("https://music.163.com/api/search/get?type=1&offset=0&limit=20&s="+Uri.EscapeDataString(song.Title+" "+song.Artist),timeout.Token).ConfigureAwait(false);
                string id=PickNetEaseSong(song,search); if(id==null) return null;
                string body=await download("https://music.163.com/api/song/lyric?lv=1&kv=1&tv=-1&id="+id,timeout.Token).ConfigureAwait(false);
                var result=ClientLyrics.ParseNetEaseLyric(body);
                if(result==null || string.IsNullOrWhiteSpace(result.Plain)) return null;
                result.Credit="网易云音乐 · 在线歌词"; return result;
            }
        }
        internal static string PickNetEaseSong(MusicSnapshot song,string json)
        {
            if(string.IsNullOrWhiteSpace(json)) return null;
            var data=new JavaScriptSerializer { MaxJsonLength=1024*1024 }.DeserializeObject(json) as Dictionary<string,object>; object value;
            if(data==null || !data.TryGetValue("result",out value)) return null;
            var result=value as Dictionary<string,object>; if(result==null || !result.TryGetValue("songs",out value)) return null;
            string best=null; int bestScore=-1;
            foreach(var entry in ClientLyrics.Tracks(value))
            {
                var candidate=ClientLyrics.NetEaseTrack(entry); string id=ClientLyrics.Str(entry,"id");
                if(!Regex.IsMatch(id,@"^\d{1,20}$")) continue;
                int score=ClientLyrics.MatchScore(song,candidate.Title,candidate.Artist,candidate.Duration.TotalSeconds);
                if(score>bestScore) { best=id; bestScore=score; }
            }
            return best;
        }
        // QQ Music public web endpoints: search by cleaned title + main artist, then fetch LRC by songmid.
        internal async Task<LyricsDocument> ReadQQOnline(MusicSnapshot song,CancellationToken cancellation)
        {
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                var artists=ClientLyrics.ArtistList(song.Artist);
                string keyword=Regex.Replace(song.Title,@"\s*[\(\[（【][^\)\]）】]*[\)\]）】]","").Trim();
                if(keyword.Length==0) keyword=song.Title;
                string first=(song.Artist??"").Split(new[]{'/','、',',','&','_','，'},StringSplitOptions.RemoveEmptyEntries).Select(a=>a.Trim()).FirstOrDefault()??"";
                string search=await download("https://c.y.qq.com/soso/fcgi-bin/client_search_cp?format=json&p=1&n=20&w="+Uri.EscapeDataString(keyword+" "+first),timeout.Token).ConfigureAwait(false);
                string mid=PickQQSong(song,search); if(mid==null) return null;
                string body=await download("https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?format=json&nobase64=1&g_tk=5381&songmid="+Uri.EscapeDataString(mid),timeout.Token).ConfigureAwait(false);
                var result=ParseQQLyric(body);
                if(result==null || string.IsNullOrWhiteSpace(result.Plain)) return null;
                result.Credit="QQ 音乐 · 在线歌词"; return result;
            }
        }
        internal static string PickQQSong(MusicSnapshot song,string json)
        {
            if(string.IsNullOrWhiteSpace(json)) return null;
            json=Unwrap(json);
            var root=new JavaScriptSerializer { MaxJsonLength=2*1024*1024 }.DeserializeObject(json) as Dictionary<string,object>; object value;
            if(root==null || !root.TryGetValue("data",out value)) return null;
            var data=value as Dictionary<string,object>; if(data==null || !data.TryGetValue("song",out value)) return null;
            var songs=value as Dictionary<string,object>; if(songs==null || !songs.TryGetValue("list",out value)) return null;
            string best=null; int bestScore=-1;
            foreach(var entry in ClientLyrics.Tracks(value))
            {
                string mid=ClientLyrics.Str(entry,"songmid"); if(!Regex.IsMatch(mid,@"^[0-9A-Za-z]{6,20}$")) continue;
                var names=new List<string>(); object singers;
                if(entry.TryGetValue("singer",out singers) && singers is System.Collections.IEnumerable && !(singers is string))
                    foreach(object s in (System.Collections.IEnumerable)singers) { string n=ClientLyrics.Str(s as Dictionary<string,object>,"name"); if(n.Length>0) names.Add(n); }
                double seconds; double.TryParse(ClientLyrics.Str(entry,"interval"),NumberStyles.Float,CultureInfo.InvariantCulture,out seconds);
                int score=ClientLyrics.MatchScore(song,ClientLyrics.Str(entry,"songname"),string.Join("/",names),seconds);
                if(score>bestScore) { best=mid; bestScore=score; }
            }
            return best;
        }
        static string Unwrap(string text)
        {
            text=text.Trim();
            int open=text.IndexOf('('); // JSONP: callback({...})
            if(!text.StartsWith("{") && open>0 && text.EndsWith(")")) text=text.Substring(open+1,text.Length-open-2);
            return text;
        }
        internal static LyricsDocument ParseQQLyric(string body)
        {
            if(string.IsNullOrWhiteSpace(body)) return null;
            var data=new JavaScriptSerializer { MaxJsonLength=1024*1024 }.DeserializeObject(Unwrap(body)) as Dictionary<string,object>;
            if(data==null) return null;
            string lyric=ClientLyrics.Str(data,"lyric"); if(lyric.Length==0) return null;
            if(!lyric.Contains("[") ) { try { lyric=Encoding.UTF8.GetString(Convert.FromBase64String(lyric)); } catch { } }
            lyric=WebUtility.HtmlDecode(lyric);
            return LyricsDocument.Parse(lyric);
        }
        public void Invalidate(MusicSnapshot song) { lock(cache) cache.Remove(CacheKey(song)); }
        public async Task<LyricsDocument> ReadAsync(MusicSnapshot song,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if(!string.IsNullOrEmpty(song.LocalPath))
            {
                string lrc=Path.ChangeExtension(song.LocalPath,".lrc");
                return File.Exists(lrc)?await Task.Run(()=>LyricsDocument.FromFile(lrc),cancellation).ConfigureAwait(false):new LyricsDocument { Message="将同名 LRC 放在音乐旁，或右键导入歌词" };
            }
            if(string.IsNullOrWhiteSpace(song.Title) || string.IsNullOrWhiteSpace(song.Artist)) return new LyricsDocument { Message="歌曲信息不足，可右键导入歌词" };
            string key=CacheKey(song); LyricsDocument cached;
            lock(cache) if(cache.TryGetValue(key,out cached) && cached.FromClient) return cached;
            var direct=await Task.Run(()=>readClient(song,cancellation),cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if(direct!=null)
            {
                direct.FromClient=true;
                lock(cache) { if(cache.Count>=64) cache.Clear(); cache[key]=direct; }
                return direct;
            }
            lock(cache) if(cache.TryGetValue(key,out cached)) return cached;
            // Online sources: the playing client's own catalogue first, then the other one, then LRCLIB.
            var online=song.Source=="qq"
                ? new Func<MusicSnapshot,CancellationToken,Task<LyricsDocument>>[]{ ReadQQOnline, ReadNetEaseOnline }
                : new Func<MusicSnapshot,CancellationToken,Task<LyricsDocument>>[]{ ReadNetEaseOnline, ReadQQOnline };
            foreach(var source in online)
            {
                LyricsDocument found=null;
                try { found=await source(song,cancellation).ConfigureAwait(false); }
                catch(OperationCanceledException) { if(cancellation.IsCancellationRequested) throw; }
                catch { } // Public endpoint changed or offline: try the next source.
                if(found!=null)
                {
                    lock(cache) { if(cache.Count>=64) cache.Clear(); cache[key]=found; }
                    return found;
                }
            }
            string url="https://lrclib.net/api/get?track_name="+Uri.EscapeDataString(song.Title)+"&artist_name="+Uri.EscapeDataString(song.Artist);
            if(!string.IsNullOrWhiteSpace(song.Album)) url+="&album_name="+Uri.EscapeDataString(song.Album);
            double duration=Math.Round(song.Duration.TotalSeconds);
            if(duration>=1 && duration<=3600) url+="&duration="+duration.ToString(CultureInfo.InvariantCulture);
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                string json=await download(url,timeout.Token).ConfigureAwait(false);
                var result=json==null?new LyricsDocument { Message=ClientLyrics.Supports(song)?"未找到歌词：已尝试客户端缓存、QQ 音乐／网易云在线与 LRCLIB，可右键重新匹配或导入 LRC":"暂无匹配歌词，可右键导入 LRC" }:Decode(song,json);
                cancellation.ThrowIfCancellationRequested();
                lock(cache) { if(cache.Count>=64) cache.Clear(); cache[key]=result; }
                return result;
            }
        }
    }
}
