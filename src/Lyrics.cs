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
                request.Headers.UserAgent.ParseAdd("ClearDesk/1.8.2 (+https://github.com/visual-life/ClearDesk)");
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
            string url="https://lrclib.net/api/get?track_name="+Uri.EscapeDataString(song.Title)+"&artist_name="+Uri.EscapeDataString(song.Artist);
            if(!string.IsNullOrWhiteSpace(song.Album)) url+="&album_name="+Uri.EscapeDataString(song.Album);
            double duration=Math.Round(song.Duration.TotalSeconds);
            if(duration>=1 && duration<=3600) url+="&duration="+duration.ToString(CultureInfo.InvariantCulture);
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                string json=await download(url,timeout.Token).ConfigureAwait(false);
                var result=json==null?new LyricsDocument { Message=ClientLyrics.Supports(song)?"请在音乐软件中打开歌词页，加载后会自动同步":"暂无匹配歌词，可右键导入 LRC" }:Decode(song,json);
                cancellation.ThrowIfCancellationRequested();
                lock(cache) { if(cache.Count>=64) cache.Clear(); cache[key]=result; }
                return result;
            }
        }
    }
}
