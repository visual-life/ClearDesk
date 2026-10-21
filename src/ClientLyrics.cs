using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml;

namespace ClearDesk
{
    // Read only the selected song's lyrics. Never read account data or modify a client cache.
    internal sealed class ClientLyrics
    {
        internal const int Limit=1024*1024;
        readonly string qqRoot,neteaseRoot;
        internal ClientLyrics(string qq=null,string netease=null)
        {
            qqRoot=qq??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),@"Tencent\QQMusic\QQMusicCache\QQMusicLyricNew");
            neteaseRoot=netease??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"NetEase\CloudMusic\webdata");
        }
        internal static bool Supports(MusicSnapshot song) { return song.Source=="qq" || song.Source=="netease"; }
        internal LyricsDocument Read(MusicSnapshot song,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(string.IsNullOrWhiteSpace(song.Title) || string.IsNullOrWhiteSpace(song.Artist)) return null;
            try
            {
                LyricsDocument result=song.Source=="qq"?ReadQQ(song,token):song.Source=="netease"?ReadNetEase(song,token):null;
                token.ThrowIfCancellationRequested();
                return result!=null && !string.IsNullOrWhiteSpace(result.Plain)?result:null;
            }
            catch(OperationCanceledException) { throw; }
            catch { return null; } // Cache absent, locked, truncated, or changed by a client update: keep the fallback working.
        }
        internal static byte[] ReadBounded(string path,int limit=Limit)
        {
            using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            {
                if(file.Length>limit) throw new IOException("歌词缓存过大。");
                using(var output=new MemoryStream())
                {
                    byte[] buffer=new byte[8192]; int count;
                    while((count=file.Read(buffer,0,buffer.Length))>0)
                    { if(output.Length+count>limit) throw new IOException("歌词缓存过大。"); output.Write(buffer,0,count); }
                    return output.ToArray();
                }
            }
        }
        internal static string Normalize(string value)
        {
            return Regex.Replace((value??"").Normalize(NormalizationForm.FormKC),@"\s+","").ToLowerInvariant();
        }
        static string FileNameText(string value) { return Regex.Replace(value??"", "[<>:\"/\\\\|?*]", "_"); }
        internal static string Artists(string value)
        {
            return string.Join("/",Regex.Split(Normalize(value),@"[、/&,;_，；]+").Where(x=>x.Length>0).OrderBy(x=>x,StringComparer.Ordinal));
        }
        static bool DurationMatches(MusicSnapshot song,double duration)
        { return song.Duration.TotalSeconds>=1 && Math.Abs(song.Duration.TotalSeconds-duration)<=2; }
        // NetEase compatibility mode often cannot read the timeline (minimized / background), so Duration is 0.
        // Unknown duration must not block an otherwise exact title+artist match.
        static bool DurationCompatible(MusicSnapshot song,double duration)
        { return song.Duration.TotalSeconds<1 || duration<1 || Math.Abs(song.Duration.TotalSeconds-duration)<=3; }
        internal static bool QQMatches(MusicSnapshot song,string path)
        {
            string name=Path.GetFileName(path);
            if(!name.EndsWith("_qm.qrc",StringComparison.OrdinalIgnoreCase)) return false;
            name=name.Substring(0,name.Length-7);
            foreach(Match middle in Regex.Matches(name,@" - (\d+) - "))
            {
                double duration; if(!double.TryParse(middle.Groups[1].Value,out duration) || !DurationMatches(song,duration)) continue;
                string prefix=name.Substring(0,middle.Index),album=name.Substring(middle.Index+middle.Length);
                string titleSuffix=" - "+FileNameText(song.Title);
                if(!Normalize(prefix).EndsWith(Normalize(titleSuffix),StringComparison.Ordinal)) continue;
                // Locate the title boundary without relying on lengths after Unicode/whitespace normalization.
                for(int split=prefix.IndexOf(" - ",StringComparison.Ordinal);split>=0;split=prefix.IndexOf(" - ",split+3,StringComparison.Ordinal))
                {
                    if(Normalize(prefix.Substring(split+3))!=Normalize(FileNameText(song.Title))) continue;
                    if(Artists(prefix.Substring(0,split))!=Artists(FileNameText(song.Artist))) continue;
                    if(!string.IsNullOrWhiteSpace(song.Album) && Normalize(album)!=Normalize(FileNameText(song.Album))) continue;
                    return true;
                }
            }
            return false;
        }
        LyricsDocument ReadQQ(MusicSnapshot song,CancellationToken token)
        {
            if(!Directory.Exists(qqRoot)) return null;
            var matches=new List<string>(); int examined=0;
            foreach(string file in Directory.EnumerateFiles(qqRoot,"*_qm.qrc"))
            {
                token.ThrowIfCancellationRequested(); if(++examined>10000) return null;
                if(QQMatches(song,file)) { matches.Add(file); if(matches.Count>1) return null; }
            }
            if(matches.Count==0)
            {
                // Fuzzy pass: cleaned title ("(Explicit)", "(feat. …)" removed), any shared artist, closest duration.
                string best=null; int bestScore=-1; bool tie=false; examined=0;
                foreach(string file in Directory.EnumerateFiles(qqRoot,"*_qm.qrc"))
                {
                    token.ThrowIfCancellationRequested(); if(++examined>10000) break;
                    int score=QQFuzzyScore(song,file);
                    if(score>bestScore) { best=file; bestScore=score; tie=false; } else if(score==bestScore && score>=0) tie=true;
                }
                if(best==null || bestScore<0 || tie) return null;
                matches.Add(best);
            }
            if(matches.Count!=1) return null;
            var result=ParseQrc(QrcDecode(ReadBounded(matches[0]),token));
            result.Credit="QQ 音乐 · 客户端歌词"; return result;
        }
        // ---- fuzzy matching shared by cache and online providers ----
        internal static string CleanTitle(string value)
        {
            string t=(value??"").Normalize(NormalizationForm.FormKC);
            // Drop bracketed suffixes: (Explicit), (feat. X), [Remastered], （伴奏）, 【Live】 …
            t=Regex.Replace(t,@"\s*[\(\[（【][^\)\]）】]*[\)\]）】]","");
            t=Regex.Replace(t,@"\s+(feat\.?|ft\.?|featuring)\s+.*$","",RegexOptions.IgnoreCase);
            t=Regex.Replace(t,@"\s+-\s+(explicit|clean|remaster(ed)?.*)$","",RegexOptions.IgnoreCase);
            t=Normalize(t);
            return t.Length>0?t:Normalize(value);
        }
        internal static string[] ArtistList(string value)
        {
            return Regex.Split((value??"").Normalize(NormalizationForm.FormKC),@"\s*(?:[、/&,;_，；×]|\s+(?:feat\.?|ft\.?|x|and|with)\s+)\s*",RegexOptions.IgnoreCase)
                .Select(Normalize).Where(x=>x.Length>0).Distinct().ToArray();
        }
        // Higher is better; -1 means "not the same song".
        internal static int MatchScore(MusicSnapshot song,string title,string artists,double durationSeconds)
        {
            int score;
            if(Normalize(title)==Normalize(song.Title)) score=60;
            else if(CleanTitle(title)==CleanTitle(song.Title)) score=40;
            else return -1;
            var want=ArtistList(song.Artist); var have=ArtistList(artists);
            if(want.Length>0 && have.Length>0)
            {
                int shared=want.Count(have.Contains);
                if(shared==0) return -1;
                score+=shared==want.Length?30:15;
                if(want[0]==have[0]) score+=5; // main artist first
            }
            if(song.Duration.TotalSeconds>=1 && durationSeconds>=1)
            {
                double diff=Math.Abs(song.Duration.TotalSeconds-durationSeconds);
                if(diff>8) return -1;
                score+=diff<=2?20:diff<=5?10:0;
            }
            return score;
        }
        internal static int QQFuzzyScore(MusicSnapshot song,string path)
        {
            string name=Path.GetFileName(path);
            if(!name.EndsWith("_qm.qrc",StringComparison.OrdinalIgnoreCase)) return -1;
            name=name.Substring(0,name.Length-7);
            int best=-1;
            foreach(Match middle in Regex.Matches(name,@" - (\d+) - "))
            {
                double duration; if(!double.TryParse(middle.Groups[1].Value,out duration)) continue;
                string prefix=name.Substring(0,middle.Index);
                for(int split=prefix.IndexOf(" - ",StringComparison.Ordinal);split>=0;split=prefix.IndexOf(" - ",split+3,StringComparison.Ordinal))
                    best=Math.Max(best,MatchScore(song,prefix.Substring(split+3),prefix.Substring(0,split),duration));
            }
            return best;
        }
        internal static IEnumerable<Dictionary<string,object>> Tracks(object root)
        {
            var map=root as Dictionary<string,object>;
            if(map!=null)
            {
                object list;
                if(map.TryGetValue("list",out list) || map.TryGetValue("queue",out list) || map.TryGetValue("data",out list) || map.TryGetValue("tracks",out list)) root=list;
            }
            var array=root as IEnumerable; if(array==null || root is string || root is Dictionary<string,object>) yield break;
            foreach(object item in array)
            {
                var entry=item as Dictionary<string,object>; if(entry==null) continue;
                object track; if(entry.TryGetValue("track",out track)) entry=track as Dictionary<string,object>;
                if(entry!=null) yield return entry;
            }
        }
        internal static object Json(string path,int limit=Limit)
        { return new JavaScriptSerializer { MaxJsonLength=limit,RecursionLimit=64 }.DeserializeObject(Encoding.UTF8.GetString(ReadBounded(path,limit)).TrimStart('\uFEFF')); }
        internal static string Str(Dictionary<string,object> map,string key)
        { object value; return map!=null && map.TryGetValue(key,out value)?Convert.ToString(value,CultureInfo.InvariantCulture):""; }
        internal static MusicSnapshot NetEaseTrack(Dictionary<string,object> track)
        {
            object value; var names=new List<string>();
            if((track.TryGetValue("artists",out value) || track.TryGetValue("ar",out value)) && value is IEnumerable && !(value is string))
                foreach(object artist in (IEnumerable)value) { string name=Str(artist as Dictionary<string,object>,"name"); if(name.Length>0) names.Add(name); }
            string ms=Str(track,"duration"); if(ms.Length==0) ms=Str(track,"dt");
            double milliseconds; double.TryParse(ms,NumberStyles.Float,CultureInfo.InvariantCulture,out milliseconds);
            return new MusicSnapshot { Source="netease",Title=Str(track,"name"),Artist=string.Join("/",names),Duration=TimeSpan.FromMilliseconds(Math.Max(0,Math.Min(86400000,milliseconds))) };
        }
        LyricsDocument ReadNetEase(MusicSnapshot song,CancellationToken token)
        {
            var ids=new HashSet<string>(StringComparer.Ordinal);
            // These are playback metadata files, not login/account databases. Read each at most once per lookup.
            foreach(string name in new[]{"playingList","queue","fmPlay","history"})
            {
                token.ThrowIfCancellationRequested(); string path=Path.Combine(neteaseRoot,"file",name); if(!File.Exists(path)) continue;
                try
                {
                    foreach(var track in Tracks(Json(path,4*Limit)))
                    {
                        token.ThrowIfCancellationRequested(); var candidate=NetEaseTrack(track);
                        if(Normalize(candidate.Title)!=Normalize(song.Title) || Artists(candidate.Artist)!=Artists(song.Artist) || !DurationCompatible(song,candidate.Duration.TotalSeconds)) continue;
                        object album; if(!track.TryGetValue("album",out album)) track.TryGetValue("al",out album);
                        if(!string.IsNullOrWhiteSpace(song.Album) && Normalize(song.Album)!=Normalize(Str(album as Dictionary<string,object>,"name"))) continue;
                        string id=Str(track,"id"); if(Regex.IsMatch(id,@"^\d{1,20}$")) ids.Add(id);
                    }
                }
                catch(OperationCanceledException) { throw; }
                catch { } // Another metadata file may still contain a valid mapping.
            }
            // Several matching IDs: refuse to guess (online sources are tried next).
            if(ids.Count!=1) return null;
            string lyric=Path.Combine(neteaseRoot,"lyric",ids.First()); if(!File.Exists(lyric)) return null;
            LyricsDocument result=ParseNetEaseLyric(Encoding.UTF8.GetString(ReadBounded(lyric)).TrimStart('\uFEFF'));
            if(result==null || string.IsNullOrWhiteSpace(result.Plain)) return null;
            result.Credit="网易云音乐 · 客户端歌词"; return result;
        }
        // Accepts {"lrc":{"lyric":...}} (client cache and public API) or a raw LRC text.
        internal static LyricsDocument ParseNetEaseLyric(string text)
        {
            if(string.IsNullOrWhiteSpace(text)) return null;
            string t=text.TrimStart();
            if(t.StartsWith("{"))
            {
                var data=new JavaScriptSerializer { MaxJsonLength=Limit,RecursionLimit=64 }.DeserializeObject(t) as Dictionary<string,object>; object lrc;
                if(data==null) return null;
                if(data.TryGetValue("nolyric",out lrc) && lrc is bool && (bool)lrc) return new LyricsDocument { Message="纯音乐，请欣赏",Plain="纯音乐" };
                if(!data.TryGetValue("lrc",out lrc)) return null;
                string body=lrc is string?(string)lrc:Str(lrc as Dictionary<string,object>,"lyric");
                return string.IsNullOrWhiteSpace(body)?null:LyricsDocument.Parse(body);
            }
            // Raw LRC only when it really has timestamps; anything else is a partial/corrupt cache.
            var raw=LyricsDocument.Parse(text);
            return raw.Lines.Count>0?raw:null;
        }
        internal static LyricsDocument ParseQrc(string xml)
        {
            if(xml==null || xml.Length>Limit) throw new InvalidDataException("歌词内容过大。");
            string text=null;
            try
            {
            // The QRC attribute contains literal newlines; XML attribute normalization would erase line boundaries.
            using(var reader=new XmlTextReader(new StringReader(xml.TrimStart('\uFEFF'))) { DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,Normalization=false })
                while(reader.Read()) if(reader.NodeType==XmlNodeType.Element && reader.Name=="Lyric_1") { text=reader.GetAttribute("LyricContent"); break; }
            }
            catch(XmlException)
            {
                // QQ writes some lyrics (e.g. explicit English songs with "<(" or "&") without escaping,
                // which makes the XML invalid. Pull the attribute out textually instead.
                var found=Regex.Match(xml,"LyricContent=\"([\\s\\S]*)\"\\s*/>");
                if(!found.Success) throw;
                text=System.Net.WebUtility.HtmlDecode(found.Groups[1].Value);
            }
            if(text==null) throw new InvalidDataException("没有歌词内容。");
            var result=new LyricsDocument();
            foreach(string raw in text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries))
            {
                var stamp=Regex.Match(raw,@"^\[(\d{1,9}),(\d{1,9})\](.*)$"); if(!stamp.Success) continue;
                string line=Regex.Replace(stamp.Groups[3].Value,@"\(\d{1,9},\d{1,9}(?:,\d{1,9})?\)","").Trim();
                result.Lines.Add(new LyricLine { Seconds=double.Parse(stamp.Groups[1].Value,CultureInfo.InvariantCulture)/1000,Text=line });
            }
            if(result.Lines.Count==0) return LyricsDocument.Parse(text);
            result.Lines=result.Lines.OrderBy(x=>x.Seconds).ToList(); result.Plain=string.Join("\n",result.Lines.Select(x=>x.Text)); return result;
        }
        // QMC mask and container: jixunmoe-go/qrc (MIT); custom DES: WXRIW/QQMusicDecoder (MIT).
        // Licenses and pinned revisions are preserved in third_party/qrc.
        static readonly byte[] mask={
            0xc3,0x4a,0xd6,0xca,0x90,0x67,0xf7,0x52,0xd8,0xa1,0x66,0x62,0x9f,0x5b,0x09,0x00,
            0xc3,0x5e,0x95,0x23,0x9f,0x13,0x11,0x7e,0xd8,0x92,0x3f,0xbc,0x90,0xbb,0x74,0x0e,
            0xc3,0x47,0x74,0x3d,0x90,0xaa,0x3f,0x51,0xd8,0xf4,0x11,0x84,0x9f,0xde,0x95,0x1d,
            0xc3,0xc6,0x09,0xd5,0x9f,0xfa,0x66,0xf9,0xd8,0xf0,0xf7,0xa0,0x90,0xa1,0xd6,0xf3,
            0xc3,0xf3,0xd6,0xa1,0x90,0xa0,0xf7,0xf0,0xd8,0xf9,0x66,0xfa,0x9f,0xd5,0x09,0xc6,
            0xc3,0x1d,0x95,0xde,0x9f,0x84,0x11,0xf4,0xd8,0x51,0x3f,0xaa,0x90,0x3d,0x74,0x47,
            0xc3,0x0e,0x74,0xbb,0x90,0xbc,0x3f,0x92,0xd8,0x7e,0x11,0x13,0x9f,0x23,0x95,0x5e,
            0xc3,0x00,0x09,0x5b,0x9f,0x62,0x66,0xa1,0xd8,0x52,0xf7,0x67,0x90,0xca,0xd6,0x4a };
        internal static string QrcDecode(byte[] input,CancellationToken cancellation=default(CancellationToken))
        {
            cancellation.ThrowIfCancellationRequested();
            byte[] magic={0x98,0x25,0xb0,0xac,0xe3,0x02,0x83,0x68,0xe8,0xfc,0x6c};
            if(input.Length<19 || input.Length>Limit || !input.Take(11).SequenceEqual(magic) || (input.Length-11)%8!=0) throw new InvalidDataException("无效 QRC 缓存。");
            byte[] data=new byte[input.Length-11];
            for(int i=11;i<input.Length;i++) data[i-11]=(byte)(input[i]^mask[(i>0x7fff?i%0x7fff:i)&0x7f]);
            var schedule=new byte[3][][];
            for(int i=0;i<3;i++) { schedule[i]=new byte[16][]; for(int j=0;j<16;j++) schedule[i][j]=new byte[6]; }
            QrcCodec.DESHelper.TripleDESKeySetup(Encoding.ASCII.GetBytes("!@#)(*$%123ZXC!@!@#)(NHL"),schedule,QrcCodec.DESHelper.DECRYPT);
            var block=new byte[8]; var output=new byte[8];
            for(int i=0;i<data.Length;i+=8) { if((i&1023)==0) cancellation.ThrowIfCancellationRequested(); Buffer.BlockCopy(data,i,block,0,8); QrcCodec.DESHelper.TripleDESCrypt(block,output,schedule); Buffer.BlockCopy(output,0,data,i,8); }
            if((data[0]&15)!=8 || ((data[0]<<8)+data[1])%31!=0 || (data[1]&32)!=0) throw new InvalidDataException("无效压缩头。");
            using(var memory=new MemoryStream(data,2,data.Length-2))
            using(var deflate=new DeflateStream(memory,CompressionMode.Decompress))
            using(var plain=new MemoryStream())
            {
                int count; byte[] buffer=new byte[8192];
                while((count=deflate.Read(buffer,0,buffer.Length))>0) { cancellation.ThrowIfCancellationRequested(); if(plain.Length+count>Limit) throw new InvalidDataException("歌词解码内容过大。"); plain.Write(buffer,0,count); }
                return new UTF8Encoding(false,true).GetString(plain.ToArray()).TrimEnd('\0');
            }
        }
    }
}
