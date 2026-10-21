using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace ClearDesk
{
    internal enum MusicCommand { Previous, Toggle, Next }

    internal sealed class MusicSnapshot
    {
        public string Key, Source, LocalPath, Title = "", Artist = "", Album = "", Message = "";
        public BitmapSource Cover;
        public bool Playing, CanPrevious, CanToggle, CanNext, CanSeek;
        public TimeSpan Position, Duration;
    }

    internal interface IMusicSessions : IDisposable
    {
        Task<MusicSnapshot> ReadAsync(string source);
        Task<bool> CommandAsync(string key, MusicCommand command);
        // Jump the selected session to an absolute position (from the start of the track).
        Task<bool> SeekAsync(string key, TimeSpan position);
    }

    internal static class MusicSources
    {
        public static string Identify(string appId)
        {
            string id = (appId ?? "").ToLowerInvariant();
            if (id.Contains("qqmusic")) return "qq";
            if (id.Contains("cloudmusic") || id.Contains("netease")) return "netease";
            if (id.Contains("kugou")) return "kugou";
            return null;
        }

        public static string Name(string source)
        {
            switch (source)
            {
                case "qq": return "QQ 音乐";
                case "netease": return "网易云音乐";
                case "kugou": return "酷狗音乐";
                case "local": return "本地音乐";
                default: return "自动 · 音乐播放器";
            }
        }

        public static string Normalize(string source)
        {
            return new[] { "qq", "netease", "kugou", "local" }.Contains(source) ? source : "auto";
        }

        // Never silently select a browser/video session, or a different explicit source.
        public static int Rank(string requested, string candidate, bool playing, bool current)
        {
            if (candidate == null || (requested != "auto" && requested != candidate)) return -1;
            return (playing ? 4 : 0) + (current ? 2 : 0);
        }

        public static TimeSpan ClampPosition(TimeSpan position, TimeSpan duration)
        {
            return TimeSpan.FromTicks(Math.Max(0, Math.Min(position.Ticks, Math.Max(0, duration.Ticks))));
        }

        public static string Time(TimeSpan value)
        {
            return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
        }
    }

    // All requests run off the UI thread, with bounded waits and an exact session target.
    internal sealed class MusicSessions : IMusicSessions
    {
        GlobalSystemMediaTransportControlsSessionManager manager;
        GlobalSystemMediaTransportControlsSession selected;
        string selectedKey, metadataKey;
        BitmapSource cover;
        DateTime coverRetry;
        volatile bool disposed;
        readonly NetEaseMusic netease = new NetEaseMusic();
        string compatibilityKey;

        static async Task<T> Await<T>(Windows.Foundation.IAsyncOperation<T> operation)
        {
            using (var timeout = new CancellationTokenSource(3000))
                return await operation.AsTask(timeout.Token).ConfigureAwait(false);
        }

        public async Task<MusicSnapshot> ReadAsync(string source)
        {
            if (disposed) return new MusicSnapshot();
            try
            {
                return await Task.Run(async delegate
                {
                    if (manager == null)
                        manager = await Await(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
                    var current = manager.GetCurrentSession();
                    var sessions = manager.GetSessions();
                    var target = sessions
                        .Select(s => new { Session = s, Rank = Rank(source, s, current) })
                        .Where(s => s.Rank >= 0)
                        .OrderByDescending(s => s.Rank)
                        .ThenByDescending(s => ReferenceEquals(s.Session, selected))
                        .Select(s => s.Session).FirstOrDefault();
                    compatibilityKey = null;
                    // Prefer native sessions. Probe the compatibility client only when it
                    // could improve the selection, including a playing client over paused QQ.
                    if ((source == "netease" && target == null) ||
                        (source == "auto" && (target == null || target.GetPlaybackInfo().PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)))
                    {
                        var compatible = await netease.ReadAsync().ConfigureAwait(false);
                        if (compatible != null && (target == null || compatible.Playing))
                        {
                            selected = null;
                            selectedKey = metadataKey = null;
                            cover = null;
                            compatibilityKey = compatible.Key;
                            return compatible;
                        }
                    }
                    if (disposed) return new MusicSnapshot();
                    if (target == null)
                    {
                        selected = null;
                        selectedKey = metadataKey = null;
                        cover = null;
                        return new MusicSnapshot { Message = "请打开所选音乐客户端并播放歌曲。网易云兼容模式需保留客户端窗口；其他客户端需支持系统媒体会话。" };
                    }
                    if (!ReferenceEquals(target, selected))
                    {
                        selected = target;
                        selectedKey = Guid.NewGuid().ToString("N");
                        metadataKey = null;
                        cover = null;
                    }
                    var properties = await Await(target.TryGetMediaPropertiesAsync());
                    var playback = target.GetPlaybackInfo();
                    var timeline = target.GetTimelineProperties();
                    string stamp = selectedKey + "\n" + properties.Title + "\n" + properties.Artist + "\n" + properties.AlbumTitle;
                    if (stamp != metadataKey || (cover == null && DateTime.UtcNow >= coverRetry))
                    {
                        cover = null; // A missing/broken cover must not retain the previous song's artwork.
                        cover = await LoadCover(properties.Thumbnail);
                        metadataKey = stamp;
                        coverRetry = DateTime.UtcNow.AddSeconds(5);
                    }
                    var controls = playback.Controls;
                    var duration = timeline.EndTime - timeline.StartTime;
                    var position = timeline.Position - timeline.StartTime;
                    bool playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    if (playing && timeline.LastUpdatedTime != default(DateTimeOffset))
                    {
                        double elapsed = Math.Max(0, (DateTimeOffset.Now - timeline.LastUpdatedTime).TotalSeconds);
                        position += TimeSpan.FromSeconds(elapsed * (playback.PlaybackRate ?? 1));
                    }
                    return new MusicSnapshot
                    {
                        Key = selectedKey, Source = MusicSources.Identify(target.SourceAppUserModelId),
                        Title = properties.Title ?? "", Artist = properties.Artist ?? "", Album = properties.AlbumTitle ?? "", Cover = cover,
                        Playing = playing, CanPrevious = controls.IsPreviousEnabled,
                        CanToggle = controls.IsPlayPauseToggleEnabled || (playing ? controls.IsPauseEnabled : controls.IsPlayEnabled),
                        CanNext = controls.IsNextEnabled, CanSeek = duration > TimeSpan.Zero && (controls.IsPlaybackPositionEnabled || MusicSources.Identify(target.SourceAppUserModelId) == "qq"), Duration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero,
                        Position = MusicSources.ClampPosition(position, duration),
                        Message = string.IsNullOrWhiteSpace(properties.Title) ? "客户端尚未提供歌曲信息" : ""
                    };
                }).ConfigureAwait(false);
            }
            catch (Exception)
            {
                compatibilityKey = null;
                selected = null;
                selectedKey = metadataKey = null;
                cover = null;
                manager = null;
                return new MusicSnapshot { Message = "暂时无法连接系统媒体服务，请使用 Windows 10 1809 或更新版本；稍后自动重试。" };
            }
        }

        static int Rank(string source, GlobalSystemMediaTransportControlsSession session, GlobalSystemMediaTransportControlsSession current)
        {
            try
            {
                return MusicSources.Rank(source, MusicSources.Identify(session.SourceAppUserModelId),
                    session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    ReferenceEquals(session, current));
            }
            catch { return -1; }
        }

        static async Task<BitmapSource> LoadCover(IRandomAccessStreamReference thumbnail)
        {
            if (thumbnail == null) return null;
            try
            {
                using (var stream = await Await(thumbnail.OpenReadAsync()))
                {
                    if (stream.Size == 0 || stream.Size > 4 * 1024 * 1024) return null;
                    using (var reader = new DataReader(stream.GetInputStreamAt(0)))
                    {
                        uint length = (uint)stream.Size;
                        using (var timeout = new CancellationTokenSource(3000))
                            if (await reader.LoadAsync(length).AsTask(timeout.Token) != length) return null;
                        var bytes = new byte[length];
                        reader.ReadBytes(bytes);
                        using (var memory = new MemoryStream(bytes))
                        {
                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.DecodePixelWidth = 128;
                            bitmap.StreamSource = memory;
                            bitmap.EndInit();
                            bitmap.Freeze();
                            return bitmap;
                        }
                    }
                }
            }
            catch { return null; }
        }

        public async Task<bool> CommandAsync(string key, MusicCommand command)
        {
            if (!disposed && key != null && key == compatibilityKey)
                return await netease.CommandAsync(key, command).ConfigureAwait(false);
            if (disposed || key == null || key != selectedKey || selected == null) return false;
            var target = selected;
            try
            {
                return await Task.Run(async delegate
                {
                    if (disposed || !manager.GetSessions().Any(s => ReferenceEquals(s, target))) return false;
                    var info = target.GetPlaybackInfo();
                    if (command == MusicCommand.Previous)
                        return info.Controls.IsPreviousEnabled && await Await(target.TrySkipPreviousAsync());
                    if (command == MusicCommand.Next)
                        return info.Controls.IsNextEnabled && await Await(target.TrySkipNextAsync());
                    if (info.Controls.IsPlayPauseToggleEnabled)
                        return await Await(target.TryTogglePlayPauseAsync());
                    if (info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        return info.Controls.IsPauseEnabled && await Await(target.TryPauseAsync());
                    return info.Controls.IsPlayEnabled && await Await(target.TryPlayAsync());
                }).ConfigureAwait(false);
            }
            catch { return false; }
        }

        public async Task<bool> SeekAsync(string key, TimeSpan position)
        {
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (!disposed && key != null && key == compatibilityKey)
                return await netease.SeekAsync(key, position).ConfigureAwait(false);
            if (disposed || key == null || key != selectedKey || selected == null) return false;
            var target = selected;
            try
            {
                return await Task.Run(async delegate
                {
                    if (disposed || !manager.GetSessions().Any(s => ReferenceEquals(s, target))) return false;
                    var timeline = target.GetTimelineProperties();
                    if (!target.GetPlaybackInfo().Controls.IsPlaybackPositionEnabled)
                    {
                        // QQ Music: SMTC seeking disabled; click its own progress bar instead.
                        var length = timeline.EndTime - timeline.StartTime;
                        if (MusicSources.Identify(target.SourceAppUserModelId) != "qq" || length <= TimeSpan.Zero) return false;
                        return QQMusicSeek.Seek(position.TotalSeconds / length.TotalSeconds);
                    }
                    // SMTC positions are relative to StartTime; the API takes 100-ns ticks.
                    return await Await(target.TryChangePlaybackPositionAsync((timeline.StartTime + position).Ticks));
                }).ConfigureAwait(false);
            }
            catch { return false; }
        }

        public void Dispose() { disposed = true; netease.Dispose(); }
    }
}
