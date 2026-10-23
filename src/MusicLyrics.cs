using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace ClearDesk
{
    public sealed partial class MusicWindow
    {
        readonly ILyricsProvider lyrics;
        readonly Button lyricsButton=new Button { Content="歌词",Height=25,FontSize=11,Padding=new Thickness(9,2,9,2),Margin=new Thickness(0,0,6,0) };
        readonly TextBlock lyricCurrent=Program.Text("播放歌曲后显示歌词",14),lyricNext=Program.Text("",11),lyricCredit=Program.Text("",9);
        readonly TextBlock lyricPlain=Program.Text("",13);
        readonly ScrollViewer lyricScroll=new ScrollViewer { VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };
        readonly StackPanel lyricLines=new StackPanel { VerticalAlignment=VerticalAlignment.Center };
        Border lyricsPanel;
        int lyricNextCount=1;
        internal int TestLyricNextLines { get { return lyricNextCount; } }
        internal double TestLyricsHeight { get { return lyricsPanel==null?0:lyricsPanel.Height; } }
        // 2.0.8: a larger music widget gives the room to the lyrics card: the lyric font grows with the
        // extra height (capped by the available width so lines don't wrap into fragments), and the
        // remaining height is filled with as many upcoming lines as fit.
        internal double LyricFontScale { get; private set; }
        internal static double LyricScale(double room,double extraW)
        {
            if(room<1) return 1;
            double width=Math.Max(0,280+extraW);
            return Math.Max(1,Math.Min(2.4,Math.Min(1+room/160,width/280*1.5)));
        }
        protected override void OnResponsive(double extraW,double extraH)
        {
            if(lyricsPanel==null) return;
            double room=Program.S.MusicLyrics?extraH:0, k=LyricScale(room,extraW);
            LyricFontScale=k;
            lyricsPanel.Height=126+room;
            lyricCurrent.FontSize=14*k; lyricNext.FontSize=11*k; lyricPlain.FontSize=13*k; lyricCredit.FontSize=Math.Min(12,9*Math.Sqrt(k));
            if(room<1) { lyricCurrent.MaxHeight=44; lyricNext.MaxHeight=32; lyricNextCount=1; }
            else
            {
                double inner=126+room-20-lyricCredit.FontSize*1.4-7;
                lyricCurrent.MaxHeight=Math.Max(44,14*k*1.4*2+2);
                double rest=Math.Max(0,inner-lyricCurrent.MaxHeight-6);
                lyricNext.MaxHeight=Math.Max(32,rest);
                lyricNextCount=Math.Max(1,(int)(rest/(11*k*1.4)));
            }
            if(lyricView!=null) RenderLyrics(lyricView);
        }
        MenuItem lyricMenu;
        LyricsDocument lyricDocument;
        MusicSnapshot lyricView;
        CancellationTokenSource lyricCancellation;
        string lyricKey;
        int lyricRevision;
        DateTime lyricClientRetryAt;
        internal string TestLyric { get { return lyricCurrent.Text; } }
        internal bool TestLyricsVisible { get { return lyricsPanel.Visibility==Visibility.Visible; } }
        internal void TestLyrics(bool enabled) { SetLyrics(enabled); }
        internal void TestLyricsRender(MusicSnapshot value) { RenderLyrics(value); }
        internal void TestLyricsRetry() { lyricClientRetryAt=DateTime.MinValue; RenderLyrics(lyricView); }

        void SetupLyrics(StackPanel panel)
        {
            lyricsButton.Template=RoundedButtonTemplate(13);
            lyricsButton.ToolTip="优先读取 QQ 音乐／网易云客户端已加载的歌词；找不到时联网匹配（LRCLIB），也可右键导入 LRC";
            System.Windows.Automation.AutomationProperties.SetName(lyricsButton,"显示或隐藏歌词");
            lyricsButton.Click+=delegate { SetLyrics(!Program.S.MusicLyrics); };
            DockPanel.SetDock(lyricsButton,Dock.Right); Header.Children.Insert(1,lyricsButton);
            var content=new Grid(); content.RowDefinitions.Add(new RowDefinition()); content.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            lyricCurrent.TextWrapping=TextWrapping.Wrap; lyricCurrent.TextAlignment=TextAlignment.Center; lyricCurrent.FontWeight=FontWeights.SemiBold; lyricCurrent.MaxHeight=44;
            lyricNext.TextWrapping=TextWrapping.Wrap; lyricNext.TextAlignment=TextAlignment.Center; lyricNext.Opacity=.5; lyricNext.Margin=new Thickness(0,6,0,0); lyricNext.MaxHeight=32;
            lyricLines.Children.Add(lyricCurrent); lyricLines.Children.Add(lyricNext); content.Children.Add(lyricLines);
            lyricPlain.TextWrapping=TextWrapping.Wrap; lyricPlain.TextAlignment=TextAlignment.Center; lyricScroll.Content=lyricPlain; content.Children.Add(lyricScroll);
            lyricCredit.Opacity=.5; lyricCredit.TextAlignment=TextAlignment.Center; lyricCredit.Margin=new Thickness(0,7,0,0); Grid.SetRow(lyricCredit,1); content.Children.Add(lyricCredit);
            lyricsPanel=new Border { Height=126,Margin=new Thickness(0,10,0,0),Padding=new Thickness(12,10,12,10),CornerRadius=new CornerRadius(12),Background=new SolidColorBrush(Color.FromArgb(18,255,255,255)),Child=content };
            panel.Children.Add(lyricsPanel);
            lyricMenu=Item("显示歌词",delegate { SetLyrics(!Program.S.MusicLyrics); }); lyricMenu.IsCheckable=true;
            ContextMenu.Items.Insert(0,lyricMenu);
            ContextMenu.Items.Insert(1,Item("导入当前歌曲歌词…",ImportLyrics));
            ContextMenu.Items.Insert(2,Item("以同步模式重启网易云",delegate
            {
                if(MessageBox.Show("将关闭并重新打开网易云音乐（附加 --force-renderer-accessibility），以便读取播放进度、切歌和跳转。当前播放会中断，是否继续？","网易云同步模式",MessageBoxButton.OKCancel)!=MessageBoxResult.OK) return;
                string error=NetEaseMusic.RestartWithAccessibility(); if(error!=null) MessageBox.Show(error,"网易云同步模式");
            }));
            ContextMenu.Items.Insert(3,Item("重新匹配歌词",delegate { if(lyricView!=null) { lyrics.Invalidate(lyricView); Program.S.MusicLyricFiles.Remove(LyricsProvider.TrackKey(lyricView)); Program.Save(); } lyricKey=null; lyricDocument=null; Render(); }));
        }
        void SetLyrics(bool enabled)
        {
            Program.S.MusicLyrics=enabled; Program.Save();
            if(!enabled) CancelLyrics();
            Render();
        }
        void CancelLyrics()
        {
            lyricRevision++; if(lyricCancellation!=null) { lyricCancellation.Cancel(); lyricCancellation=null; }
            lyricDocument=null; lyricKey=null;
        }
        void RenderLyrics(MusicSnapshot view)
        {
            lyricView=view;
            bool enabled=Program.S.MusicLyrics;
            lyricsPanel.Visibility=enabled?Visibility.Visible:Visibility.Collapsed;
            lyricMenu.IsChecked=enabled;
            lyricsButton.Background=new SolidColorBrush(Color.FromArgb((byte)(enabled?50:20),255,255,255));
            if(!enabled) { if(lyricKey!=null) CancelLyrics(); return; }
            if(string.IsNullOrWhiteSpace(view.Title)) { CancelLyrics(); ShowLyricStatus("播放歌曲后显示歌词"); return; }
            string key=(view.Key??view.Source??selectedSource)+"\n"+LyricsProvider.TrackKey(view);
            if(key!=lyricKey)
            {
                CancelLyrics(); lyricKey=key; ShowLyricStatus("正在查找歌词…");
                _ = LoadLyrics(view,key,lyricRevision); return;
            }
            DisplayLyrics(view);
            if(lyricCancellation==null && lyricDocument!=null && !lyricDocument.FromClient && ClientLyrics.Supports(view) && DateTime.UtcNow>=lyricClientRetryAt && !Program.S.MusicLyricFiles.ContainsKey(LyricsProvider.TrackKey(view)))
                _ = LoadLyrics(view,key,lyricRevision);
        }
        void ShowLyricStatus(string text)
        {
            lyricLines.Visibility=Visibility.Visible; lyricScroll.Visibility=Visibility.Collapsed;
            lyricCurrent.Text=text; lyricNext.Text=""; lyricCredit.Text="";
        }
        async Task LoadLyrics(MusicSnapshot view,string key,int generation)
        {
            var cancellation=new CancellationTokenSource(); lyricCancellation=cancellation;
            try
            {
                string path; LyricsDocument result;
                if(Program.S.MusicLyricFiles.TryGetValue(LyricsProvider.TrackKey(view),out path))
                    result=await Task.Run(()=>LyricsDocument.FromFile(path),cancellation.Token);
                else result=await lyrics.ReadAsync(view,cancellation.Token);
                if(closed || generation!=lyricRevision || key!=lyricKey || !Program.S.MusicLyrics) return;
                lyricDocument=result; DisplayLyrics(lyricView);
            }
            catch(OperationCanceledException)
            {
                if(!closed && generation==lyricRevision && !cancellation.IsCancellationRequested) { lyricDocument=new LyricsDocument { Message="歌词请求超时，可右键重试" }; DisplayLyrics(lyricView); }
            }
            catch
            {
                if(!closed && generation==lyricRevision) { lyricDocument=new LyricsDocument { Message="歌词暂不可用，可右键重试或导入" }; DisplayLyrics(lyricView); }
            }
            finally
            {
                if(ReferenceEquals(lyricCancellation,cancellation)) { lyricCancellation=null; lyricClientRetryAt=DateTime.UtcNow.AddSeconds(15); }
                cancellation.Dispose();
            }
        }
        void DisplayLyrics(MusicSnapshot view)
        {
            var doc=lyricDocument; if(doc==null) return;
            if(doc.Message.Length>0) { ShowLyricStatus(doc.Message); lyricCredit.Text=doc.Credit; return; }
            if(doc.Lines.Count==0 || view.Duration<=TimeSpan.Zero)
            {
                lyricLines.Visibility=Visibility.Collapsed; lyricScroll.Visibility=Visibility.Visible;
                if(lyricPlain.Text!=doc.Plain) { lyricPlain.Text=doc.Plain; lyricScroll.ScrollToTop(); }
                lyricCredit.Text=doc.Credit+" · "+(doc.Lines.Count==0?"无时间轴 · 可滚动":"未提供播放进度 · 可滚动"); return;
            }
            lyricLines.Visibility=Visibility.Visible; lyricScroll.Visibility=Visibility.Collapsed;
            int index=doc.At(view.Position);
            lyricCurrent.Text=index>=0?(string.IsNullOrWhiteSpace(doc.Lines[index].Text)?"♪":doc.Lines[index].Text):"前奏 ♪";
            var upcoming=new List<string>();
            for(int k=index+1;k<doc.Lines.Count && upcoming.Count<lyricNextCount;k++) if(!string.IsNullOrWhiteSpace(doc.Lines[k].Text) || upcoming.Count==0) upcoming.Add(doc.Lines[k].Text);
            lyricNext.Text=string.Join("\n",upcoming);
            lyricCredit.Text=doc.Credit+" · "+(view.Playing?"同步歌词":"已暂停");
        }
        void ImportLyrics()
        {
            if(lyricView==null || string.IsNullOrWhiteSpace(lyricView.Title)) { MessageBox.Show("请先播放一首歌曲，再导入对应歌词。","歌词"); return; }
            string key=LyricsProvider.TrackKey(lyricView);
            var dialog=new OpenFileDialog { Filter="歌词文件|*.lrc;*.txt",Title="选择当前歌曲对应的歌词" };
            if(dialog.ShowDialog(this)!=true) return;
            try
            {
                var document=LyricsDocument.FromFile(dialog.FileName);
                if(string.IsNullOrWhiteSpace(document.Plain)) throw new IOException("文件中没有可显示的歌词。");
                if(lyricView==null || key!=LyricsProvider.TrackKey(lyricView)) throw new IOException("歌曲已切换，请为当前歌曲重新选择歌词。");
                Program.S.MusicLyricFiles[key]=dialog.FileName; CancelLyrics(); SetLyrics(true);
            }
            catch(Exception error) { MessageBox.Show(error.Message,"未能导入歌词"); }
        }
    }
}
