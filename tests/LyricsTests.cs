using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ClearDesk
{
    internal static class LyricsTests
    {
        sealed class Sessions : IMusicSessions
        {
            internal MusicSnapshot Value;
            public Task<MusicSnapshot> ReadAsync(string source) { return Task.FromResult(Value); }
            public Task<bool> CommandAsync(string key,MusicCommand command) { return Task.FromResult(true); }
            public Task<bool> SeekAsync(string key,TimeSpan position) { return Task.FromResult(true); }
            public void Dispose() { }
        }
        sealed class Provider : ILyricsProvider
        {
            internal int Reads; internal bool Cancelled; internal TaskCompletionSource<LyricsDocument> Pending;
            public Task<LyricsDocument> ReadAsync(MusicSnapshot song,CancellationToken token)
            {
                Reads++; token.Register(()=>Cancelled=true);
                if(Pending!=null) { var task=Pending.Task; Pending=null; return task; }
                return Task.FromResult(LyricsDocument.Parse("[00:00]微风从桌面经过\n[00:10]把今天慢慢点亮\n[00:20]让喜欢的旋律留下"));
            }
            public void Invalidate(MusicSnapshot song) { }
        }
        static MusicSnapshot Song(string title,double position=0) { return new MusicSnapshot { Key="test",Source="qq",Title=title,Artist="ClearDesk 演示",Duration=TimeSpan.FromSeconds(60),Position=TimeSpan.FromSeconds(position),Playing=true }; }
        internal static async Task Run(DockWindow dock,Action<bool,string> check)
        {
            await ClientLyricsTests.Run(check);
            var lrc=LyricsDocument.Parse("[offset:500]\n[00:01.25][00:05.250]First\n[00:03]Middle\n[00:03]Translation\n[00:06]\n[00:99]Invalid");
            check(lrc.Lines.Count==4 && lrc.Lines[0].Seconds==.75 && lrc.Lines[1].Text=="Middle / Translation","LRC parser handles timestamps offset duplicate timestamps and invalid seconds");
            check(lrc.At(TimeSpan.Zero)==-1 && lrc.At(TimeSpan.FromSeconds(5))==2 && lrc.At(TimeSpan.FromSeconds(2))==0,"lyric lookup tracks both forward playback and backward seeks");
            check(LyricsDocument.Parse("plain\ntext").Lines.Count==0,"plain lyrics remain unsynchronized rather than inventing timestamps");
            string path=Path.Combine(Program.Data,"lyrics-test.lrc"); File.WriteAllText(path,"[00:00]本地歌词测试",Encoding.Unicode);
            check(LyricsDocument.FromFile(path).Lines[0].Text=="本地歌词测试","UTF-16 local lyric files decode correctly");
            int requests=0; string url=null;
            // 1.9.1: QQ / NetEase online sources are asked first; this fixture only answers LRCLIB.
            var provider=new LyricsProvider((address,token)=> { if(!address.Contains("lrclib.net")) return Task.FromResult<string>(null); requests++; url=address; return Task.FromResult("{\"trackName\":\"Demo & Song\",\"artistName\":\"ClearDesk 演示\",\"duration\":60,\"syncedLyrics\":\"[00:00]Original test line\"}"); });
            var track=Song("Demo & Song");
            var document=await provider.ReadAsync(track,CancellationToken.None); await provider.ReadAsync(track,CancellationToken.None);
            check(requests==1 && url.Contains("Demo%20%26%20Song") && document.Lines.Count==1,"lyrics requests encode metadata and reuse cached song results");
            provider.Invalidate(track); await provider.ReadAsync(track,CancellationToken.None);
            check(requests==2,"explicit lyric retry invalidates the previous match cache");
            var wrong=LyricsProvider.Decode(Song("Different song"),"{\"trackName\":\"Other\",\"artistName\":\"ClearDesk 演示\",\"duration\":60,\"syncedLyrics\":\"[00:00]Wrong\"}");
            check(wrong.Lines.Count==0 && wrong.Message.Length>0,"mismatched song metadata never displays unrelated lyrics");
            using(var cancellation=new CancellationTokenSource()) { cancellation.Cancel(); bool rejected=false; try { await provider.ReadAsync(track,cancellation.Token); } catch(OperationCanceledException) { rejected=true; } check(rejected && requests==2,"disabled or cancelled lyric requests do not hit the network"); }
            bool saved=Program.S.MusicLyrics; string source=Program.S.MusicSource; Program.S.MusicLyrics=false; Program.S.MusicSource="qq";
            var backend=new Sessions { Value=Song("慢慢点亮",12) }; var lyrics=new Provider(); var window=new MusicWindow(backend,lyrics);
            try
            {
                window.Show(); await Task.Delay(80); double normalHeight=window.Height;
                check(lyrics.Reads==0 && !window.TestLyricsVisible,"lyrics are opt-in and make no request when hidden");
                window.TestLyrics(true); await Task.Delay(80);
                check(window.TestLyricsVisible && window.TestLyric=="把今天慢慢点亮" && window.Height>normalHeight,"lyric option expands below playback controls and displays the current line");
                FeatureTests.Capture(window,"music-lyrics-example");
                backend.Value.Position=TimeSpan.FromSeconds(2); backend.Value.Playing=false; window.TestRefresh(); await Task.Delay(60);
                check(window.TestLyric=="微风从桌面经过" && lyrics.Reads==1,"pause and backward seek use client position without refetching lyrics");
                var cachePending=new TaskCompletionSource<LyricsDocument>(); lyrics.Pending=cachePending; window.TestLyricsRetry();
                check(window.TestLyric=="微风从桌面经过" && lyrics.Reads==2,"late client-cache retry preserves visible lyrics while loading");
                var clientDoc=LyricsDocument.Parse("[00:00]客户端测试歌词"); clientDoc.FromClient=true; cachePending.SetResult(clientDoc); await Task.Delay(60);
                window.TestLyricsRetry();
                check(window.TestLyric=="客户端测试歌词" && lyrics.Reads==2,"successful client lyrics stop periodic cache retries");
                var pending=new TaskCompletionSource<LyricsDocument>(); lyrics.Pending=pending; backend.Value=Song("第二首",15); window.TestRefresh(); await Task.Delay(60);
                check(window.TestLyric=="正在查找歌词…","track changes clear previous lyrics before the new request completes");
                window.TestLyrics(false); pending.SetResult(LyricsDocument.Parse("[00:00]Late result")); await Task.Delay(60);
                check(lyrics.Cancelled && !window.TestLyricsVisible && window.TestLyric!="Late result","hiding lyrics cancels work and rejects late responses");
                window.TestLyrics(true); await Task.Delay(60); backend.Value=new MusicSnapshot(); window.TestSelect("netease");
                check(window.TestLyric=="播放歌曲后显示歌词","switching music source clears old lyrics immediately");
                backend.Value=new MusicSnapshot(); window.TestRefresh(); await Task.Delay(60);
                check(window.TestLyric=="播放歌曲后显示歌词","disconnected playback never leaves the previous lyrics on screen");
                backend.Value=Song("第三首"); pending=new TaskCompletionSource<LyricsDocument>(); lyrics.Pending=pending; window.TestRefresh(); await Task.Delay(60);
                window.Close(); pending.SetResult(LyricsDocument.Parse("[00:00]After close")); await Task.Delay(60);
                check(window.TestLyric!="After close","closed music component ignores pending lyric responses");
            }
            finally { if(!window.WasClosed) window.Close(); Program.S.MusicLyrics=saved; Program.S.MusicSource=source; Program.Save(); }
            foreach(bool pinned in new[]{false,true})
            {
                var entry=new DockEntry { PinPath=pinned?"test.exe":null,Identity=new AppIdentity { Exe="test.exe",Name="Test" } };
                var menu=dock.TestEntryMenu(entry);
                check(menu.Items[menu.Items.Count-1] is MenuItem && Equals(((MenuItem)menu.Items[menu.Items.Count-1]).Header,"关闭应用") && menu.Items[menu.Items.Count-2] is Separator,"Dock close is the final separated action for "+(pinned?"pinned":"temporary")+" applications");
            }
        }
        internal static async void Live()
        {
            try
            {
                // Public sample metadata, not the user's playback history; never log or render returned lyric text.
                var song=new MusicSnapshot { Title="I Want to Live",Artist="Borislav Slavov",Duration=TimeSpan.FromSeconds(233) };
                var result=await new LyricsProvider().ReadAsync(song,CancellationToken.None);
                bool ok=result.Lines.Count>0 || result.Plain.Length>0;
                File.WriteAllText(Path.Combine(Program.Base,"lyrics-live-test.txt"),(ok?"PASS: ":"FAIL: ")+"public sample metadata returned lyrics through the production service; lyric text not recorded.");
                if(!ok) Environment.ExitCode=1;
            }
            catch(Exception error) { File.WriteAllText(Path.Combine(Program.Base,"lyrics-live-test.txt"),"FAIL: "+error.GetType().Name); Environment.ExitCode=1; }
            finally { Program.Quit(); }
        }
    }
}
