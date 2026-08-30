using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DynamicIsland.Media
{
    public enum MediaAppSource
    {
        Chrome,
        Spotify,
        AppleMusic,
        Edge,
        YouTube,
        Brave,
        Firefox,
        Generic
    }

    public class TrackInfo
    {
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string AlbumTitle { get; set; } = string.Empty;
        public BitmapImage? Thumbnail { get; set; }
        public bool IsPlaying { get; set; }
        public TimeSpan Position { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTimeOffset LastTimelineUpdate { get; set; } = DateTimeOffset.Now;
        public MediaAppSource AppSource { get; set; } = MediaAppSource.Generic;
        public string SourceAppId { get; set; } = string.Empty;

        public TimeSpan GetCurrentEstimatedPosition()
        {
            if (!IsPlaying) return Position;
            var delta = DateTimeOffset.UtcNow - LastTimelineUpdate;
            if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;
            var est = Position + delta;
            if (Duration > TimeSpan.Zero && est > Duration) return Duration;
            return est;
        }
    }

    public class MediaSessionManager
    {
        public static MediaSessionManager Instance { get; } = new MediaSessionManager();

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
        private const byte VK_MEDIA_PREV_TRACK = 0xB1;
        private const byte VK_MEDIA_STOP = 0xB2;
        private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        public static void SendMediaKey(byte vk)
        {
            try
            {
                keybd_event(vk, 0, 0, UIntPtr.Zero);
                keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch { }
        }

        private GlobalSystemMediaTransportControlsSessionManager? sessionManager;
        private GlobalSystemMediaTransportControlsSession? currentSession;

        public event Action<TrackInfo>? OnTrackChanged;
        public event Action<bool>? OnPlaybackStateChanged;
        public event Action<TimeSpan, TimeSpan>? OnTimelineChanged;

        public TrackInfo CurrentTrack { get; private set; } = new TrackInfo();
        public bool HasActiveSession => currentSession != null && !string.IsNullOrWhiteSpace(CurrentTrack.Title);

        public DateTimeOffset? PausedTimestamp { get; private set; }

        public bool IsPausedOverThreshold(double seconds = 20)
        {
            if (CurrentTrack.IsPlaying) return false;
            if (!PausedTimestamp.HasValue) return false;
            return (DateTimeOffset.Now - PausedTimestamp.Value).TotalSeconds >= seconds;
        }

        public bool ShouldShowCompactMedia => HasActiveSession && (CurrentTrack.IsPlaying || !IsPausedOverThreshold(20));

        public async Task InitializeAsync()
        {
            try
            {
                sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (sessionManager != null)
                {
                    sessionManager.CurrentSessionChanged += SessionManager_CurrentSessionChanged;
                    UpdateCurrentSession();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaSessionManager] Init error: {ex.Message}");
            }
        }

        private void SessionManager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            Application.Current?.Dispatcher.InvokeAsync(UpdateCurrentSession);
        }

        private void UpdateCurrentSession()
        {
            try
            {
                if (currentSession != null)
                {
                    currentSession.MediaPropertiesChanged -= CurrentSession_MediaPropertiesChanged;
                    currentSession.PlaybackInfoChanged -= CurrentSession_PlaybackInfoChanged;
                    currentSession.TimelinePropertiesChanged -= CurrentSession_TimelinePropertiesChanged;
                }

                currentSession = sessionManager?.GetCurrentSession();

                if (currentSession != null)
                {
                    currentSession.MediaPropertiesChanged += CurrentSession_MediaPropertiesChanged;
                    currentSession.PlaybackInfoChanged += CurrentSession_PlaybackInfoChanged;
                    currentSession.TimelinePropertiesChanged += CurrentSession_TimelinePropertiesChanged;
                    
                    _ = RefreshMediaPropertiesAsync();
                    RefreshPlaybackInfo();
                    RefreshTimeline();
                }
                else
                {
                    CurrentTrack = new TrackInfo();
                    OnTrackChanged?.Invoke(CurrentTrack);
                }
            }
            catch { }
        }

        private void CurrentSession_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            Application.Current?.Dispatcher.InvokeAsync(async () => await RefreshMediaPropertiesAsync());
        }

        private void CurrentSession_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            Application.Current?.Dispatcher.InvokeAsync(RefreshPlaybackInfo);
        }

        private void CurrentSession_TimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        {
            Application.Current?.Dispatcher.InvokeAsync(RefreshTimeline);
        }

        public async Task RefreshMediaPropertiesAsync()
        {
            if (currentSession == null)
            {
                currentSession = sessionManager?.GetCurrentSession();
                if (currentSession == null) return;
            }

            try
            {
                var props = await currentSession.TryGetMediaPropertiesAsync();
                string appId = currentSession.SourceAppUserModelId ?? string.Empty;

                if (props != null && !string.IsNullOrWhiteSpace(props.Title))
                {
                    BitmapImage? bmp = null;
                    if (props.Thumbnail != null)
                    {
                        try
                        {
                            using var stream = await props.Thumbnail.OpenReadAsync();
                            if (stream != null && stream.Size > 0)
                            {
                                using var netStream = stream.AsStreamForRead();
                                using var mem = new MemoryStream();
                                await netStream.CopyToAsync(mem);
                                mem.Seek(0, SeekOrigin.Begin);

                                var img = new BitmapImage();
                                img.BeginInit();
                                img.DecodePixelWidth = 176;
                                img.CacheOption = BitmapCacheOption.OnLoad;
                                img.StreamSource = mem;
                                img.EndInit();
                                img.Freeze();
                                bmp = img;
                            }
                        }
                        catch { }
                    }

                    string rawTitle = props.Title ?? string.Empty;
                    string rawArtist = props.Artist ?? string.Empty;

                    // Clean YouTube / web suffixes
                    if (rawTitle.EndsWith(" - YouTube", StringComparison.OrdinalIgnoreCase))
                        rawTitle = rawTitle.Substring(0, rawTitle.Length - 10).Trim();

                    CurrentTrack.Title = rawTitle;
                    CurrentTrack.Artist = rawArtist;
                    CurrentTrack.AlbumTitle = props.AlbumTitle ?? string.Empty;
                    CurrentTrack.Thumbnail = bmp;
                    CurrentTrack.SourceAppId = appId;
                    CurrentTrack.AppSource = DetectAppSource(appId, rawTitle, rawArtist, props.AlbumTitle);

                    RefreshPlaybackInfo();
                    RefreshTimeline();

                    OnTrackChanged?.Invoke(CurrentTrack);
                }
            }
            catch { }
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private static bool CheckBrowserWindowHasKeyword(string keyword)
        {
            bool found = false;
            try
            {
                EnumWindows((hWnd, lParam) =>
                {
                    if (IsWindowVisible(hWnd))
                    {
                        var sb = new StringBuilder(256);
                        GetWindowText(hWnd, sb, 256);
                        string title = sb.ToString();
                        if (title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        {
                            found = true;
                            return false;
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return found;
        }

        private MediaAppSource DetectAppSource(string? appId, string? title, string? artist, string? albumTitle)
        {
            string combinedMeta = $"{appId} {title} {artist} {albumTitle}".ToLowerInvariant();

            // 1. Direct Spotify App or metadata keyword
            if (combinedMeta.Contains("spotify")) return MediaAppSource.Spotify;

            // 2. Direct YouTube keyword
            if (combinedMeta.Contains("youtube") || combinedMeta.Contains("youtu.be")) return MediaAppSource.YouTube;

            // 3. Apple Music / iTunes
            if (combinedMeta.Contains("itunes") || (combinedMeta.Contains("apple") && combinedMeta.Contains("music"))) return MediaAppSource.AppleMusic;

            // 4. If playing from Chrome, Edge, Brave, or Firefox, check open browser tabs/windows
            string lowerApp = (appId ?? string.Empty).ToLowerInvariant();
            if (lowerApp.Contains("chrome") || lowerApp.Contains("edge") || lowerApp.Contains("brave") || lowerApp.Contains("firefox") || string.IsNullOrEmpty(appId))
            {
                if (CheckBrowserWindowHasKeyword("spotify")) return MediaAppSource.Spotify;
                if (CheckBrowserWindowHasKeyword("youtube")) return MediaAppSource.YouTube;
            }

            // 5. Browser specific fallbacks
            if (lowerApp.Contains("chrome")) return MediaAppSource.Chrome;
            if (lowerApp.Contains("edge")) return MediaAppSource.Edge;
            if (lowerApp.Contains("brave")) return MediaAppSource.Brave;
            if (lowerApp.Contains("firefox")) return MediaAppSource.Firefox;
            if (lowerApp.Contains("apple")) return MediaAppSource.AppleMusic;

            return MediaAppSource.Generic;
        }

        public void RefreshPlaybackInfo()
        {
            if (currentSession == null) return;

            try
            {
                var info = currentSession.GetPlaybackInfo();
                if (info != null)
                {
                    bool isPlaying = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    if (isPlaying)
                    {
                        PausedTimestamp = null;
                    }
                    else if (!PausedTimestamp.HasValue)
                    {
                        PausedTimestamp = DateTimeOffset.Now;
                    }

                    CurrentTrack.IsPlaying = isPlaying;
                    CurrentTrack.LastTimelineUpdate = DateTimeOffset.Now;
                    OnPlaybackStateChanged?.Invoke(isPlaying);
                }
            }
            catch { }
        }

        public void RefreshTimeline()
        {
            if (currentSession == null) return;

            try
            {
                var timeline = currentSession.GetTimelineProperties();
                if (timeline != null)
                {
                    CurrentTrack.Position = timeline.Position;
                    if (timeline.EndTime > timeline.StartTime)
                    {
                        CurrentTrack.Duration = timeline.EndTime - timeline.StartTime;
                    }
                    CurrentTrack.LastTimelineUpdate = DateTimeOffset.UtcNow;

                    OnTimelineChanged?.Invoke(CurrentTrack.Position, CurrentTrack.Duration);
                }
            }
            catch { }
        }

        public async Task TogglePlayPauseAsync()
        {
            try
            {
                if (sessionManager != null)
                {
                    var s = sessionManager.GetCurrentSession();
                    if (s != null) currentSession = s;
                }

                if (currentSession != null)
                {
                    bool success = await currentSession.TryTogglePlayPauseAsync();
                    if (!success) SendMediaKey(VK_MEDIA_PLAY_PAUSE);
                }
                else
                {
                    SendMediaKey(VK_MEDIA_PLAY_PAUSE);
                }
            }
            catch
            {
                SendMediaKey(VK_MEDIA_PLAY_PAUSE);
            }
        }

        public async Task NextTrackAsync()
        {
            try
            {
                if (sessionManager != null)
                {
                    var s = sessionManager.GetCurrentSession();
                    if (s != null) currentSession = s;
                }

                if (currentSession != null)
                {
                    bool success = await currentSession.TrySkipNextAsync();
                    if (!success) SendMediaKey(VK_MEDIA_NEXT_TRACK);
                }
                else
                {
                    SendMediaKey(VK_MEDIA_NEXT_TRACK);
                }
            }
            catch
            {
                SendMediaKey(VK_MEDIA_NEXT_TRACK);
            }
        }

        public async Task PreviousTrackAsync()
        {
            try
            {
                if (sessionManager != null)
                {
                    var s = sessionManager.GetCurrentSession();
                    if (s != null) currentSession = s;
                }

                if (currentSession != null)
                {
                    bool success = await currentSession.TrySkipPreviousAsync();
                    if (!success) SendMediaKey(VK_MEDIA_PREV_TRACK);
                }
                else
                {
                    SendMediaKey(VK_MEDIA_PREV_TRACK);
                }
            }
            catch
            {
                SendMediaKey(VK_MEDIA_PREV_TRACK);
            }
        }

        public async Task ReplayAsync()
        {
            try
            {
                if (sessionManager != null)
                {
                    var s = sessionManager.GetCurrentSession();
                    if (s != null) currentSession = s;
                }

                if (currentSession != null)
                {
                    // 1. Seek to 0 ticks
                    try { await currentSession.TryChangePlaybackPositionAsync(0); } catch { }

                    // 2. Skip Previous (resets song to beginning on Spotify / YouTube / VLC)
                    try { await currentSession.TrySkipPreviousAsync(); } catch { }

                    // 3. Play
                    try { await currentSession.TryPlayAsync(); } catch { }
                    try { await currentSession.TryTogglePlayPauseAsync(); } catch { }
                }

                // 4. Send Hardware Windows Media Keys as guaranteed fallback
                SendMediaKey(VK_MEDIA_PREV_TRACK);
                await Task.Delay(40);
                SendMediaKey(VK_MEDIA_PLAY_PAUSE);

                CurrentTrack.Position = TimeSpan.Zero;
                CurrentTrack.IsPlaying = true;
                CurrentTrack.LastTimelineUpdate = DateTimeOffset.Now;

                OnPlaybackStateChanged?.Invoke(true);
                OnTimelineChanged?.Invoke(TimeSpan.Zero, CurrentTrack.Duration);
            }
            catch
            {
                SendMediaKey(VK_MEDIA_PREV_TRACK);
                await Task.Delay(40);
                SendMediaKey(VK_MEDIA_PLAY_PAUSE);
            }
        }

        public async Task SeekAsync(TimeSpan position)
        {
            try
            {
                if (sessionManager != null)
                {
                    var s = sessionManager.GetCurrentSession();
                    if (s != null) currentSession = s;
                }

                CurrentTrack.Position = position;
                CurrentTrack.LastTimelineUpdate = DateTimeOffset.Now;

                if (currentSession != null)
                {
                    long posTicks = (long)(position.TotalSeconds * 10_000_000);
                    await currentSession.TryChangePlaybackPositionAsync(posTicks);
                }
            }
            catch { }
        }
    }
}
