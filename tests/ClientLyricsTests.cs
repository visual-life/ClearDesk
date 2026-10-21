using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ClearDesk
{
    internal static class ClientLyricsTests
    {
        internal static async Task Run(Action<bool,string> check)
        {
            var song=new MusicSnapshot { Source="qq",Title="Test - Song",Artist="Artist A / Artist B",Album="Demo",Duration=TimeSpan.FromSeconds(60) };
            check(ClientLyrics.QQMatches(song,"Artist A_Artist B - Test - Song - 60 - Demo_qm.qrc"),"QQ cache maps title with hyphens, multiple artists, duration and album");
            check(!ClientLyrics.QQMatches(song,"Artist A_Artist B - Other - 60 - Demo_qm.qrc") && !ClientLyrics.QQMatches(song,"Other - Test - Song - 60 - Demo_qm.qrc"),"QQ cache rejects a different song or artist");
            check(!ClientLyrics.QQMatches(song,"Artist A_Artist B - Test - Song - 80 - Demo_qm.qrc") && !ClientLyrics.QQMatches(song,"Artist A_Artist B - Test - Song - 60 - Other_qm.qrc"),"QQ cache rejects a different recording duration or album");
            string xml="<QrcInfos><Lyric_1 LyricContent=\"[1000,2000]Hello(1000,800) world(1800,1200)&#10;[4000,1000]Next(4000,1000)\"/></QrcInfos>";
            var qrc=ClientLyrics.ParseQrc(xml);
            check(qrc.Lines.Count==2 && qrc.Lines[0].Text=="Hello world" && qrc.Lines[1].Seconds==4,"QRC word timings become readable synchronized lines");
            var literal=ClientLyrics.ParseQrc("\uFEFF"+xml.Replace("&#10;","\n"));
            check(literal.Lines.Count==2 && literal.Lines[1].Text=="Next","QQ cache UTF-8 BOM and literal XML attribute newlines preserve lyric lines");
            bool rejected=false;
            try { ClientLyrics.ParseQrc("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///does-not-exist'>]><x>&e;</x>"); } catch(System.Xml.XmlException) { rejected=true; }
            check(rejected,"QRC XML cannot load external entities");
            rejected=false; try { ClientLyrics.QrcDecode(new byte[19]); } catch(InvalidDataException) { rejected=true; }
            check(rejected,"invalid QRC header is rejected before decryption");

            string root=Path.Combine(Program.Data,"client-lyrics-tests"),net=Path.Combine(root,"netease"),qq=Path.Combine(root,"qq");
            Directory.CreateDirectory(Path.Combine(net,"file")); Directory.CreateDirectory(Path.Combine(net,"lyric")); Directory.CreateDirectory(qq);
            string track="{\"id\":123,\"name\":\"Demo\",\"duration\":60000,\"artists\":[{\"name\":\"Tester\"}],\"album\":{\"name\":\"Album\"}}";
            string metadata=Path.Combine(net,"file","playingList"),lyric=Path.Combine(net,"lyric","123");
            File.WriteAllText(metadata,"{\"list\":[{\"track\":"+track+"}]}");
            File.WriteAllText(lyric,"{\"lrc\":{\"lyric\":\"[00:01]Original fixture\\n[00:03]Second fixture\"}}");
            var cache=new ClientLyrics(qq,net);
            var netSong=new MusicSnapshot { Source="netease",Title="Demo",Artist="Tester",Album="Album",Duration=TimeSpan.FromSeconds(60) };
            var doc=cache.Read(netSong,CancellationToken.None);
            check(doc!=null && doc.Lines.Count==2 && doc.Credit.StartsWith("网易云"),"NetEase playing list IDs map to the selected song's cached LRC");
            netSong.Artist="Wrong"; check(cache.Read(netSong,CancellationToken.None)==null,"NetEase does not use cached lyrics for the wrong artist"); netSong.Artist="Tester";
            netSong.Duration=TimeSpan.FromSeconds(80); check(cache.Read(netSong,CancellationToken.None)==null,"NetEase rejects another recording length"); netSong.Duration=TimeSpan.FromSeconds(60);
            netSong.Source="qq"; check(cache.Read(netSong,CancellationToken.None)==null,"QQ playback never uses NetEase's cache"); netSong.Source="netease";
            File.WriteAllText(metadata,"{\"list\":[{\"track\":"+track+"},{\"track\":"+track.Replace("123","456")+"}]}");
            check(cache.Read(netSong,CancellationToken.None)==null,"ambiguous matching NetEase song IDs do not guess a lyric file");
            File.WriteAllText(metadata,"broken"); File.WriteAllText(Path.Combine(net,"file","history"),"[{\"track\":"+track+"}]");
            check(cache.Read(netSong,CancellationToken.None)!=null,"a corrupt playlist does not block valid playback-history metadata");
            File.WriteAllText(lyric,new string('x',ClientLyrics.Limit+1));
            check(cache.Read(netSong,CancellationToken.None)==null,"oversized lyric cache files fall back safely");
            File.WriteAllText(lyric,"broken"); check(cache.Read(netSong,CancellationToken.None)==null,"partially written client caches fall back safely");
            using(var cancel=new CancellationTokenSource())
            {
                cancel.Cancel(); rejected=false; try { cache.Read(netSong,cancel.Token); } catch(OperationCanceledException) { rejected=true; }
                check(rejected,"client lyric lookups honor cancellation");
            }
            int downloads=0,reads=0; bool available=false;
            var provider=new LyricsProvider((url,token)=> { downloads++; return Task.FromResult<string>(null); },(value,token)=> { reads++; return available?LyricsDocument.Parse("[00:01]Late client cache"):null; });
            await provider.ReadAsync(netSong,CancellationToken.None); int firstDownloads=downloads; available=true;
            var direct=await provider.ReadAsync(netSong,CancellationToken.None); await provider.ReadAsync(netSong,CancellationToken.None);
            // 1.9.1: one miss may query several online sources (NetEase, QQ, LRCLIB); later reads must add none.
            check(direct.FromClient && firstDownloads>=1 && downloads==firstDownloads && reads==2,"late client cache replaces a cached online miss without additional network requests");
            available=false; var other=new MusicSnapshot { Source="qq",Title=netSong.Title,Artist=netSong.Artist,Album=netSong.Album,Duration=netSong.Duration };
            check(!(await provider.ReadAsync(other,CancellationToken.None)).FromClient && downloads>firstDownloads,"provider caches stay separate when switching clients for the same song");
            provider.Invalidate(netSong); available=true; await provider.ReadAsync(netSong,CancellationToken.None);
            check(reads==4,"manual retry refreshes a previous direct-client result");
        }
        // Explicit read-only diagnostic. Logs only counts, never titles, lyrics, IDs or private paths.
        internal static void Live()
        {
            var lines=new List<string>(); var watch=Stopwatch.StartNew();
            try
            {
                var cache=new ClientLyrics();
                string qq=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),@"Tencent\QQMusic\QQMusicCache\QQMusicLyricNew");
                int count=0;
                if(Directory.Exists(qq)) foreach(string path in Directory.EnumerateFiles(qq,"*_qm.qrc"))
                {
                    var match=Regex.Match(Path.GetFileName(path),@"^(.*?) - (.*?) - (\d+) - (.*)_qm\.qrc$"); if(!match.Success) continue;
                    var song=new MusicSnapshot { Source="qq",Artist=match.Groups[1].Value,Title=match.Groups[2].Value,Duration=TimeSpan.FromSeconds(int.Parse(match.Groups[3].Value)),Album=match.Groups[4].Value };
                    var result=cache.Read(song,CancellationToken.None);
                    if(result==null && !lines.Any(x=>x.StartsWith("INFO: QQ")))
                    {
                        try { string decoded=ClientLyrics.QrcDecode(ClientLyrics.ReadBounded(path)); lines.Add("INFO: QQ format: xml="+decoded.TrimStart('\uFEFF',' ','\r','\n').StartsWith("<")+" qrc stamps="+Regex.Matches(decoded,@"\[\d+,\d+\]").Count+" lrc stamps="+Regex.Matches(decoded,@"\[\d+:\d+").Count); var parsed=ClientLyrics.ParseQrc(decoded); lines.Add("INFO: QQ match="+ClientLyrics.QQMatches(song,path)+" decoded lines="+parsed.Lines.Count); }
                        catch(Exception error) { var xmlError=error as System.Xml.XmlException; lines.Add("INFO: QQ match="+ClientLyrics.QQMatches(song,path)+" decode exception="+error.GetType().Name+(xmlError==null?"":" line="+xmlError.LineNumber+" column="+xmlError.LinePosition)); }
                    }
                    if(result!=null && result.Lines.Count>0) { count++; if(count==3) break; }
                }
                lines.Add((count>0?"PASS: ":"FAIL: ")+"QQ Music local cache decoded and matched; samples="+count);
                string net=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"NetEase\CloudMusic\webdata"); count=0;
                foreach(string name in new[]{"playingList","queue","fmPlay","history"})
                {
                    string path=Path.Combine(net,"file",name); if(!File.Exists(path)) continue;
                    foreach(var track in ClientLyrics.Tracks(ClientLyrics.Json(path,4*ClientLyrics.Limit)))
                    {
                        var song=ClientLyrics.NetEaseTrack(track); var result=cache.Read(song,CancellationToken.None);
                        if(result!=null && result.Lines.Count>0) { count++; if(count==3) break; }
                    }
                    if(count==3) break;
                }
                lines.Add((count>0?"PASS: ":"FAIL: ")+"NetEase local metadata and lyric cache matched; samples="+count);
                lines.Add("INFO: cache-only validation; no client launched or controlled; no lyric text recorded; Kugou not tested.");
                lines.Add("INFO: total milliseconds="+watch.ElapsedMilliseconds);
            }
            catch(Exception error) { lines.Add("FAIL: "+error.GetType().Name); }
            if(lines.Any(x=>x.StartsWith("FAIL:"))) Environment.ExitCode=1;
            File.WriteAllLines(Path.Combine(Program.Base,"client-lyrics-test.txt"),lines);
        }
    }
}
