using FuzzySharp;
using LibVLCSharp.Shared;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace WallpaperMusicPlayer
{
    public partial class MainWindow : Window
    {
        private GlobalSystemMediaTransportControlsSessionManager? _mediaManager;
        private YoutubeWrapper? _youtubeWrapper;
        private readonly string _logPath;

        private readonly string _idleVideoPath;
        private readonly string _loadingVideoPath;

        private DateTime _lastSeekTime = DateTime.MinValue;
        private long _lastVlcRawTime = -1;
        private DateTime _lastVlcUpdateTime = DateTime.MinValue;

        private readonly Queue<double> _diffHistory = new Queue<double>(5);
        private double _displayDiff = 0;
        private double _syncDiff = 0;

        private LibVLC? _libVLC;
        private LibVLCSharp.Shared.MediaPlayer? _vlcPlayer;

        private WriteableBitmap? _videoBitmap;
        private IntPtr _videoBuffer = IntPtr.Zero;
        private const uint VideoWidth = 1920;
        private const uint VideoHeight = 1080;
        private const uint VideoPitch = VideoWidth * 4;

        private string _lastSong = "";
        private CancellationTokenSource? _searchCts;
        private string? _currentSessionId;

        private struct CachedVideo
        {
            public string VideoId;
            public string StreamUrl;
            public DateTime ExpiryTime;
        }

        private readonly ConcurrentDictionary<string, CachedVideo> _smartCache = new();

        private readonly DispatcherTimer _monitorTimer = new();
        private readonly DispatcherTimer _cacheCleanupTimer = new();
        private readonly DispatcherTimer _diagnosticsTimer = new();
        private readonly DispatcherTimer _youtubeHealthCheckTimer = new();

        private bool _isVideoLoaded = false;
        private bool _isLoopingVideo = false;
        private bool _wasPlaying = false;
        private bool _isLocalLoopPlaying = false;
        private volatile bool _isDisposing = false;
        private bool _isUpdatingYoutube = false;

        private readonly object _vlcLock = new object();
        private readonly object _logFileLock = new object();
        private readonly SemaphoreSlim _videoRenderSemaphore = new SemaphoreSlim(1, 1);

        private int _youtubeRequestCount = 0;
        private DateTime _lastYoutubeRequest = DateTime.MinValue;

        private const int MAX_YOUTUBE_REQUESTS_PER_MINUTE = 10;
        private const int YOUTUBE_CACHE_HOURS = 5;
        private const int MAX_CACHE_SIZE = 100;
        private const int NETWORK_TIMEOUT_SECONDS = 10;

        public MainWindow()
        {
            InitializeComponent();
            _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_log.txt");

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _idleVideoPath = Path.Combine(baseDir, "idle.mp4");
            _loadingVideoPath = Path.Combine(baseDir, "loading.mp4");

            try
            {
                Core.Initialize();
                _libVLC = new LibVLC();
                _vlcPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);

                SetupVideoRendering();
            }
            catch (Exception ex)
            {
                Log($"[INIT ERROR] Failed to initialize VLC: {ex.Message}", "error");
                MessageBox.Show($"Failed to initialize video player: {ex.Message}", "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
            }

            // Ініціалізація YoutubeWrapper
            try
            {
                _youtubeWrapper = new YoutubeWrapper((msg, type) => Log(msg, type));
                Log("[YOUTUBE] Wrapper initialized successfully", "success");
            }
            catch (Exception ex)
            {
                Log($"[YOUTUBE ERROR] Failed to initialize wrapper: {ex.Message}", "error");
                MessageBox.Show($"Failed to initialize YouTube integration: {ex.Message}\n\nThe application may not work correctly.",
                    "YouTube Initialization Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SetupVideoRendering()
        {
            if (_vlcPlayer == null) return;

            try
            {
                _videoBitmap = new WriteableBitmap((int)VideoWidth, (int)VideoHeight, 96, 96, PixelFormats.Pbgra32, null);
                VideoImage.Source = _videoBitmap;
                _videoBuffer = _videoBitmap.BackBuffer;

                _vlcPlayer.SetVideoFormat("RV32", VideoWidth, VideoHeight, VideoPitch);
                _vlcPlayer.SetVideoCallbacks(VideoLock, VideoUnlock, VideoDisplay);
            }
            catch (Exception ex)
            {
                Log($"[RENDER ERROR] Failed to setup video rendering: {ex.Message}", "error");
                throw;
            }
        }

        private IntPtr VideoLock(IntPtr opaque, IntPtr planes)
        {
            if (_videoBuffer == IntPtr.Zero || _isDisposing)
            {
                Log("[VIDEO LOCK] Invalid buffer or disposing", "warning");
                return IntPtr.Zero;
            }

            try
            {
                Marshal.WriteIntPtr(planes, _videoBuffer);
            }
            catch (Exception ex)
            {
                Log($"[VIDEO LOCK ERROR] {ex.Message}", "error");
            }

            return IntPtr.Zero;
        }

        private void VideoUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
        {
            // Cleanup відбувається в Dispose
        }

        private void VideoDisplay(IntPtr opaque, IntPtr picture)
        {
            if (_videoBitmap == null || _isDisposing)
                return;

            try
            {
                if (!_videoRenderSemaphore.Wait(0))
                    return;

                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (_videoBitmap != null && !_isDisposing)
                        {
                            _videoBitmap.Lock();
                            _videoBitmap.AddDirtyRect(new Int32Rect(0, 0, (int)VideoWidth, (int)VideoHeight));
                            _videoBitmap.Unlock();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[VIDEO DISPLAY ERROR] {ex.Message}", "error");
                    }
                    finally
                    {
                        if (!_isDisposing)
                            _videoRenderSemaphore.Release();
                    }
                }, DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                Log($"[VIDEO DISPLAY ERROR] {ex.Message}", "error");
                if (!_isDisposing)
                    _videoRenderSemaphore.Release();
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (_vlcPlayer == null)
            {
                Log("[ERROR] VLC Player not initialized", "error");
                return;
            }

            _vlcPlayer.Volume = 0;
            _vlcPlayer.LengthChanged += VlcPlayer_LengthChanged;
            _vlcPlayer.EncounteredError += VlcPlayer_EncounteredError;
            _vlcPlayer.Playing += VlcPlayer_Playing;
            _vlcPlayer.EndReached += VlcPlayer_EndReached;

            _diagnosticsTimer.Interval = TimeSpan.FromSeconds(1);
            _diagnosticsTimer.Tick += (s, ev) => UpdateWindowDiagnostics();
            _diagnosticsTimer.Start();

            _cacheCleanupTimer.Interval = TimeSpan.FromMinutes(30);
            _cacheCleanupTimer.Tick += (s, ev) => CleanupExpiredCache();
            _cacheCleanupTimer.Start();

            // Таймер перевірки здоров'я YouTube API (кожні 10 хвилин)
            _youtubeHealthCheckTimer.Interval = TimeSpan.FromMinutes(10);
            _youtubeHealthCheckTimer.Tick += async (s, ev) => await CheckYoutubeHealthAsync();
            _youtubeHealthCheckTimer.Start();

            InitializeAsync();

            if (File.Exists(_idleVideoPath))
            {
                PlayLocalLoop(_idleVideoPath, "Startup");
            }
            else
            {
                Log("[WARNING] idle.mp4 not found - application may not work correctly", "warning");
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F12)
            {
                if (DebugPanel.Visibility == Visibility.Visible)
                {
                    DebugPanel.Visibility = Visibility.Collapsed;
                }
                else
                {
                    DebugPanel.Visibility = Visibility.Visible;
                    LogOutput.ScrollToEnd();
                }
            }
        }

        private void VlcPlayer_EndReached(object? sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (_vlcPlayer == null || _isDisposing) return;

                if (_isLoopingVideo || _isLocalLoopPlaying)
                {
                    lock (_vlcLock)
                    {
                        if (_vlcPlayer != null && !_isDisposing)
                        {
                            _vlcPlayer.Stop();
                            _vlcPlayer.Play();
                        }
                    }
                }
            });
        }

        private void VlcPlayer_LengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _isVideoLoaded = true;
                long durationMs = e.Length;

                if (_isLocalLoopPlaying)
                {
                    _isLoopingVideo = true;
                    Log($"[VLC] Type: Local Loop (Duration: {TimeSpan.FromMilliseconds(durationMs):mm\\:ss})", "info");
                    return;
                }

                if (durationMs < 90000)
                {
                    _isLoopingVideo = true;
                    Log($"[VLC] Type: Online Loop (Duration: {TimeSpan.FromMilliseconds(durationMs):mm\\:ss})", "info");
                }
                else
                {
                    _isLoopingVideo = false;
                    Log($"[VLC] Type: Music Video (Duration: {TimeSpan.FromMilliseconds(durationMs):mm\\:ss})", "info");
                }
            });
        }

        private void VlcPlayer_Playing(object? sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _isVideoLoaded = true;
                if (_isLocalLoopPlaying)
                {
                    Log("[VLC] Local Loop Playing", "success");
                    return;
                }

                Log("[VLC] Status: Playing", "success");

                try
                {
                    if (_mediaManager != null)
                    {
                        var session = GetRelevantSession(_mediaManager);
                        if (session != null)
                        {
                            var timeline = session.GetTimelineProperties();
                            if (timeline != null)
                            {
                                TimeSpan currentAudioTime = timeline.Position + (DateTimeOffset.Now - timeline.LastUpdatedTime);
                                if (!_isLoopingVideo && currentAudioTime.TotalSeconds > 1 && _vlcPlayer != null && _vlcPlayer.Length > 0)
                                {
                                    long targetMs = (long)currentAudioTime.TotalMilliseconds;
                                    if (targetMs >= 0 && targetMs < _vlcPlayer.Length)
                                    {
                                        lock (_vlcLock)
                                        {
                                            if (_vlcPlayer != null && !_isDisposing)
                                            {
                                                _vlcPlayer.Time = targetMs;
                                                _lastVlcRawTime = targetMs;
                                                _lastVlcUpdateTime = DateTime.Now;
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                Log("[VLC] Timeline is null - cannot sync initial position", "warning");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"[VLC PLAYING ERROR] {ex.Message}", "error");
                }
            });
        }

        private void VlcPlayer_EncounteredError(object? sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                Log("[VLC] Critical Error - attempting recovery", "error");
                _isVideoLoaded = false;

                if (!_isLocalLoopPlaying && File.Exists(_loadingVideoPath))
                {
                    PlayLocalLoop(_loadingVideoPath, "Error Recovery");
                }
                else if (!File.Exists(_loadingVideoPath))
                {
                    Log("[ERROR] loading.mp4 not found - cannot recover from error", "error");
                }
            });
        }

        private async void InitializeAsync()
        {
            try
            {
                _mediaManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _monitorTimer.Interval = TimeSpan.FromMilliseconds(100);
                _monitorTimer.Tick += MonitorLoop;
                _monitorTimer.Start();
                Log("System media manager initialized - waiting for music...", "success");

                // Перевірка YouTube API при старті
                await CheckYoutubeHealthAsync();
            }
            catch (Exception ex)
            {
                Log($"[INIT ERROR] Failed to initialize media manager: {ex.Message}", "error");
                MessageBox.Show("Failed to connect to system media controls. The application may not work correctly.",
                    "Initialization Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// Перевіряє здоров'я YouTube API та оновлює якщо потрібно
        /// </summary>
        private async Task CheckYoutubeHealthAsync()
        {
            if (_isUpdatingYoutube || _youtubeWrapper == null)
                return;

            try
            {
                _isUpdatingYoutube = true;

                var result = await YoutubeDllUpdater.CheckAndUpdateAsync(_youtubeWrapper, msg => Log(msg, "info"));

                switch (result)
                {
                    case UpdateResult.Success:
                        Log("[YOUTUBE] ✓ API updated and working!", "success");
                        break;
                    case UpdateResult.NoUpdateNeeded:
                        // Все працює - нічого не робимо
                        break;
                    case UpdateResult.Failed:
                        Log("[YOUTUBE] ✗ Update failed, using current version", "warning");
                        break;
                    case UpdateResult.ApiFailedOnLatest:
                        Log("[YOUTUBE] ⚠ Already on latest version but API fails - may be YouTube-side issue", "warning");
                        break;
                    case UpdateResult.UpdatedButStillFails:
                        Log("[YOUTUBE] ⚠ Updated but API still fails - may be temporary YouTube issue", "warning");
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"[YOUTUBE ERROR] Health check failed: {ex.Message}", "error");
            }
            finally
            {
                _isUpdatingYoutube = false;
            }
        }

        private void PlayLocalLoop(string path, string reason)
        {
            if (!File.Exists(path))
            {
                Log($"[LOCAL ERROR] File not found: {Path.GetFileName(path)}", "error");
                return;
            }

            if (_vlcPlayer == null || _isDisposing)
            {
                Log("[LOCAL ERROR] VLC Player not available", "error");
                return;
            }

            try
            {
                Log($"[LOCAL] Playing {Path.GetFileName(path)} ({reason})", "info");
                _isLocalLoopPlaying = true;
                _isLoopingVideo = true;
                _isVideoLoaded = false;
                _lastVlcRawTime = -1;
                VideoImage.Opacity = 1.0;

                lock (_vlcLock)
                {
                    if (_vlcPlayer != null && !_isDisposing)
                    {
                        using var media = new LibVLCSharp.Shared.Media(_libVLC, path);
                        media.AddOption(":input-repeat=65535");
                        media.AddOption(":no-audio");
                        _vlcPlayer.Play(media);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[LOCAL ERROR] Failed to play local video: {ex.Message}", "error");
                _isLocalLoopPlaying = false;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _isDisposing = true;

            try
            {
                _monitorTimer.Stop();
                _diagnosticsTimer.Stop();
                _cacheCleanupTimer.Stop();
                _youtubeHealthCheckTimer.Stop();

                _searchCts?.Cancel();
                _searchCts?.Dispose();
                _searchCts = null;

                // Спочатку відключаємо VLC callbacks — після цього новий Release() більше не надійде
                if (_vlcPlayer != null)
                {
                    _vlcPlayer.SetVideoCallbacks(null, null, null);
                }

                // Тепер безпечно чекати завершення поточного BeginInvoke
                _videoRenderSemaphore.Wait(TimeSpan.FromSeconds(1));

                if (_vlcPlayer != null)
                {
                    lock (_vlcLock)
                    {
                        _vlcPlayer.Stop();
                        _vlcPlayer.Dispose();
                        _vlcPlayer = null;
                    }
                }

                _libVLC?.Dispose();
                _libVLC = null;

                _videoBitmap = null;
                _videoBuffer = IntPtr.Zero;

                _videoRenderSemaphore.Dispose();

                // Очищення YoutubeWrapper
                _youtubeWrapper?.Dispose();
                _youtubeWrapper = null;

                // Очищення старих backup файлів
                YoutubeDllUpdater.CleanupBackups();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during cleanup: {ex.Message}");
            }

            base.OnClosed(e);
        }

        private bool _isMonitorLoopRunning = false;

        private async void MonitorLoop(object? sender, EventArgs e)
        {
            if (_mediaManager == null || _isDisposing) return;
            if (_isMonitorLoopRunning) return;
            _isMonitorLoopRunning = true;
            try
            {
                var session = GetRelevantSession(_mediaManager);

                if (session == null)
                {
                    if (!_isLocalLoopPlaying && File.Exists(_idleVideoPath))
                    {
                        PlayLocalLoop(_idleVideoPath, "No Session");
                    }
                    UpdateTimingDisplay(TimeSpan.Zero, TimeSpan.Zero, false, false);
                    return;
                }

                string currentSessionId = session.SourceAppUserModelId;
                if (_currentSessionId != currentSessionId && _currentSessionId != null)
                {
                    Log($"[SESSION] Changed from {_currentSessionId} to {currentSessionId}", "warning");
                    _diffHistory.Clear();
                    _displayDiff = 0;
                    _syncDiff = 0;
                }
                _currentSessionId = currentSessionId;

                var playbackInfo = session.GetPlaybackInfo();
                bool isMusicPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                if (isMusicPlaying != _wasPlaying)
                {
                    Log($"[STATUS] Music: {(isMusicPlaying ? "PLAYING" : "PAUSED")}", isMusicPlaying ? "success" : "warning");
                    _wasPlaying = isMusicPlaying;
                }

                if (!isMusicPlaying)
                {
                    if (_vlcPlayer != null && _vlcPlayer.IsPlaying && !_isDisposing)
                    {
                        lock (_vlcLock)
                        {
                            if (_vlcPlayer != null && !_isDisposing)
                            {
                                _vlcPlayer.Pause();
                            }
                        }
                        Log("[MONITOR] Video Paused", "info");
                    }
                    var timelinePaused = session.GetTimelineProperties();
                    TimeSpan pausedTime = timelinePaused?.Position ?? TimeSpan.Zero;
                    UpdateTimingDisplay(pausedTime, TimeSpan.Zero, false, false);
                    return;
                }

                if (_vlcPlayer != null && !_vlcPlayer.IsPlaying && _isVideoLoaded && !_isDisposing)
                {
                    lock (_vlcLock)
                    {
                        if (_vlcPlayer != null && !_isDisposing)
                        {
                            _vlcPlayer.Play();
                        }
                    }
                }

                var info = await session.TryGetMediaPropertiesAsync();
                string artist = info?.Artist ?? "";
                string title = info?.Title ?? "";

                string currentSong = "";
                if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
                {
                    currentSong = $"{artist.Trim()} - {title.Trim()}";
                }
                else if (!string.IsNullOrWhiteSpace(title))
                {
                    currentSong = title.Trim();
                }
                else if (!string.IsNullOrWhiteSpace(artist))
                {
                    currentSong = artist.Trim();
                }

                if (!string.IsNullOrEmpty(currentSong) && currentSong != _lastSong)
                {
                    _lastSong = currentSong;

                    if (File.Exists(_loadingVideoPath))
                    {
                        PlayLocalLoop(_loadingVideoPath, "Track Switch");
                    }

                    _isVideoLoaded = false;
                    _isLoopingVideo = false;
                    _lastVlcRawTime = -1;
                    _displayDiff = 0;
                    _syncDiff = 0;
                    _diffHistory.Clear();

                    _searchCts?.Cancel();
                    _searchCts?.Dispose();
                    _searchCts = new CancellationTokenSource();
                    // Зберігаємо token локально — ProcessSongAsync тримає цей екземпляр,
                    // а _searchCts може бути замінено до завершення задачі
                    var token = _searchCts.Token;

                    // Отримуємо тривалість треку для scoring'у
                    var timelineForDuration = session.GetTimelineProperties();
                    TimeSpan trackDuration = timelineForDuration?.EndTime ?? TimeSpan.Zero;

                    Log($"♪ Next Track: {currentSong}", "info");
                    _ = ProcessSongAsync(currentSong, artist.Trim(), title.Trim(), trackDuration, token);
                }

                if (_isLocalLoopPlaying)
                {
                    UpdateTimingDisplay(TimeSpan.Zero, TimeSpan.Zero, true, false);
                    TxtDiff.Text = "LOCAL";
                    TxtDiff.Foreground = Brushes.Cyan;
                    return;
                }

                var timeline = session.GetTimelineProperties();
                TimeSpan liveAudioTime = TimeSpan.Zero;

                if (timeline != null)
                {
                    liveAudioTime = timeline.Position + (DateTimeOffset.Now - timeline.LastUpdatedTime);
                }
                else
                {
                    Log("[MONITOR] Timeline unavailable - sync may be inaccurate", "warning");
                }

                if (_vlcPlayer == null) return;

                long currentRawVlcTime = _vlcPlayer.Time;
                TimeSpan smoothVlcTime;
                bool vlcTimeUpdated = false;

                if (currentRawVlcTime >= 0 && currentRawVlcTime != _lastVlcRawTime)
                {
                    _lastVlcRawTime = currentRawVlcTime;
                    _lastVlcUpdateTime = DateTime.Now;
                    smoothVlcTime = TimeSpan.FromMilliseconds(currentRawVlcTime);
                    vlcTimeUpdated = true;
                }
                else
                {
                    if (_lastVlcRawTime >= 0 && _vlcPlayer.IsPlaying)
                    {
                        double msPassed = (DateTime.Now - _lastVlcUpdateTime).TotalMilliseconds;
                        if (msPassed > 500)
                        {
                            _lastVlcRawTime = _vlcPlayer.Time;
                            _lastVlcUpdateTime = DateTime.Now;
                            smoothVlcTime = TimeSpan.FromMilliseconds(_lastVlcRawTime);
                            vlcTimeUpdated = true;
                        }
                        else
                        {
                            double safeRate = Math.Clamp(_vlcPlayer.Rate, 0.5, 1.5);
                            double extrapolatedMs = _lastVlcRawTime + (msPassed * safeRate);
                            if (_vlcPlayer.Length > 0 && extrapolatedMs > _vlcPlayer.Length)
                                extrapolatedMs = _vlcPlayer.Length;
                            smoothVlcTime = TimeSpan.FromMilliseconds(Math.Max(0, extrapolatedMs));
                        }
                    }
                    else
                    {
                        smoothVlcTime = TimeSpan.FromMilliseconds(Math.Max(0, currentRawVlcTime));
                    }
                }

                UpdateTimingDisplay(liveAudioTime, smoothVlcTime, true, vlcTimeUpdated);

                if (_isVideoLoaded && !_isLoopingVideo)
                {
                    SyncVideoState(liveAudioTime, smoothVlcTime);
                }
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                {
                    Log($"[MONITOR ERROR] {ex.Message}", "error");
                }
            }
            finally
            {
                _isMonitorLoopRunning = false;
            }
        }

        private void UpdateWindowDiagnostics()
        {
            try
            {
                using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
                string pName = currentProcess.ProcessName;
                var processes = System.Diagnostics.Process.GetProcessesByName(pName);

                try
                {
                    TxtProcessCount.Text = processes.Length.ToString();
                    TxtProcessCount.Foreground = processes.Length > 1 ? Brushes.Red : Brushes.White;
                    TxtProcessId.Text = currentProcess.Id.ToString();
                }
                finally
                {
                    // GetProcessesByName повертає масив IDisposable об'єктів —
                    // без утилізації це витік дескрипторів (метод викликається щосекунди)
                    foreach (var p in processes) p.Dispose();
                }

                var windows = Application.Current.Windows;
                TxtWindowCount.Text = windows.Count.ToString();

                if (windows.Count == 1)
                    TxtWindowCount.Foreground = Brushes.Green;
                else
                    TxtWindowCount.Foreground = Brushes.Yellow;

                TxtVisibility.Text = this.Visibility.ToString();
                if (this.Visibility != Visibility.Visible)
                    TxtVisibility.Foreground = Brushes.Red;
                else
                    TxtVisibility.Foreground = Brushes.White;

                TxtWindowState.Text = this.WindowState.ToString();
                TxtTopmost.Text = this.Topmost.ToString();

                TxtRenderSize.Text = $"{this.ActualWidth:F0}x{this.ActualHeight:F0}";

                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                TxtHandle.Text = "0x" + helper.Handle.ToString("X8");
            }
            catch (Exception ex)
            {
                Log($"[DIAGNOSTICS ERROR] {ex.Message}", "error");
            }
        }

        private void CleanupExpiredCache()
        {
            try
            {
                var now = DateTime.Now;
                var expiredKeys = _smartCache.Where(kv => kv.Value.ExpiryTime < now)
                                             .Select(kv => kv.Key)
                                             .ToList();

                foreach (var key in expiredKeys)
                {
                    _smartCache.TryRemove(key, out _);
                }

                if (expiredKeys.Count > 0)
                {
                    Log($"[CACHE] Cleaned {expiredKeys.Count} expired entries", "info");
                }
            }
            catch (Exception ex)
            {
                Log($"[CACHE ERROR] Cleanup failed: {ex.Message}", "error");
            }
        }

        private GlobalSystemMediaTransportControlsSession? GetRelevantSession(GlobalSystemMediaTransportControlsSessionManager manager)
        {
            try
            {
                var allSessions = manager.GetSessions();
                foreach (var session in allSessions)
                {
                    try
                    {
                        var info = session.GetPlaybackInfo();
                        if (info != null && info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                            return session;
                    }
                    catch (Exception ex)
                    {
                        Log($"[SESSION ERROR] Failed to get session info: {ex.Message}", "error");
                        continue;
                    }
                }
                // Повертаємо null якщо жодна сесія не грає.
                // Раніше тут було GetCurrentSession() — це спричиняло зайві пошуки
                // на YouTube для треку що зараз не відтворюється.
                return null;
            }
            catch (Exception ex)
            {
                Log($"[SESSION ERROR] Failed to get sessions: {ex.Message}", "error");
                return null;
            }
        }

        private void SyncVideoState(TimeSpan targetAudio, TimeSpan currentVideo)
        {
            if (_isLoopingVideo || _isLocalLoopPlaying || _vlcPlayer == null || _isDisposing)
                return;

            if ((DateTime.Now - _lastSeekTime).TotalSeconds < 1.0)
                return;

            try
            {
                double instantDiff = (targetAudio - currentVideo).TotalSeconds;
                if (double.IsNaN(instantDiff) || double.IsInfinity(instantDiff))
                    return;

                if (Math.Abs(instantDiff) > 300)
                {
                    Log($"[SYNC WARNING] Extreme diff detected: {instantDiff:F2}s - ignoring", "warning");
                    return;
                }

                _diffHistory.Enqueue(instantDiff);
                if (_diffHistory.Count > 5)
                    _diffHistory.Dequeue();

                var sortedDiffs = _diffHistory.OrderBy(x => x).ToList();
                double medianDiff = sortedDiffs[sortedDiffs.Count / 2];

                _displayDiff = (_displayDiff * 0.75) + (medianDiff * 0.25);
                _syncDiff = (_syncDiff * 0.4) + (medianDiff * 0.6);

                // TxtDiff оновлюється виключно через UpdateTimingDisplay в MonitorLoop,
                // щоб уникнути подвійного запису в один UI-елемент за один цикл.
                double absDiff = Math.Abs(_syncDiff);

                if (absDiff > 3.0)
                {
                    long targetMs = (long)Math.Clamp(targetAudio.TotalMilliseconds, 0, long.MaxValue);
                    if (_vlcPlayer.Length > 0 && targetMs >= 0 && targetMs < _vlcPlayer.Length)
                    {
                        lock (_vlcLock)
                        {
                            if (_vlcPlayer != null && !_isDisposing)
                            {
                                _vlcPlayer.Time = targetMs;
                                _vlcPlayer.SetRate(1.0f);
                            }
                        }
                        _lastSeekTime = DateTime.Now;
                        _lastVlcRawTime = targetMs;
                        _lastVlcUpdateTime = DateTime.Now;
                        _syncDiff = 0;
                        _displayDiff = 0;
                        _diffHistory.Clear();
                        Log($"[SYNC] Hard seek to {TimeSpan.FromMilliseconds(targetMs):mm\\:ss}", "info");
                    }
                    return;
                }

                if (absDiff > 1.5 && absDiff <= 3.0)
                {
                    long targetMs = (long)Math.Clamp(targetAudio.TotalMilliseconds, 0, long.MaxValue);
                    if (_vlcPlayer.Length > 0 && targetMs >= 0 && targetMs < _vlcPlayer.Length)
                    {
                        lock (_vlcLock)
                        {
                            if (_vlcPlayer != null && !_isDisposing)
                            {
                                _vlcPlayer.Time = targetMs;
                            }
                        }
                        _lastSeekTime = DateTime.Now;
                        _lastVlcRawTime = targetMs;
                        _lastVlcUpdateTime = DateTime.Now;
                        _syncDiff *= 0.3;
                        _displayDiff *= 0.3;
                    }
                    return;
                }

                if (absDiff < 0.05)
                {
                    if (Math.Abs(_vlcPlayer.Rate - 1.0f) > 0.005f)
                    {
                        lock (_vlcLock)
                        {
                            if (_vlcPlayer != null && !_isDisposing)
                            {
                                _vlcPlayer.SetRate(1.0f);
                            }
                        }
                    }
                    return;
                }

                float targetRate = 1.0f;
                if (_syncDiff > 0.05)
                {
                    if (absDiff > 1.0) targetRate = 1.10f;
                    else if (absDiff > 0.5) targetRate = 1.06f;
                    else if (absDiff > 0.2) targetRate = 1.03f;
                    else targetRate = 1.01f;
                }
                else if (_syncDiff < -0.05)
                {
                    if (absDiff > 1.0) targetRate = 0.90f;
                    else if (absDiff > 0.5) targetRate = 0.94f;
                    else if (absDiff > 0.2) targetRate = 0.97f;
                    else targetRate = 0.99f;
                }

                if (Math.Abs(_vlcPlayer.Rate - targetRate) > 0.003f)
                {
                    lock (_vlcLock)
                    {
                        if (_vlcPlayer != null && !_isDisposing)
                        {
                            _vlcPlayer.SetRate(targetRate);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[SYNC ERROR] {ex.Message}", "error");
            }
        }

        private bool CheckYouTubeRateLimit()
        {
            var now = DateTime.Now;

            if ((now - _lastYoutubeRequest).TotalMinutes >= 1)
            {
                _youtubeRequestCount = 0;
                _lastYoutubeRequest = now;
            }

            if (_youtubeRequestCount >= MAX_YOUTUBE_REQUESTS_PER_MINUTE)
            {
                Log($"[RATE LIMIT] YouTube requests throttled ({_youtubeRequestCount}/min)", "warning");
                return false;
            }

            _youtubeRequestCount++;
            _lastYoutubeRequest = now;
            return true;
        }

        private async Task<bool> WaitForInternetConnection(CancellationToken token)
        {
            for (int i = 0; i < 3; i++)
            {
                if (System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
                {
                    return true;
                }

                Log($"[NETWORK] No internet connection, retrying... ({i + 1}/3)", "warning");
                await Task.Delay(2000, token);
            }

            return false;
        }

        /// <summary>
        /// Скорує результат пошуку YouTube.
        /// Основа: FuzzySharp TokenSetRatio — релевантність незалежно від порядку слів.
        /// Підтвердження: автор каналу, тривалість, формат відео.
        /// Штрафи: явний сміт + контекстні (official audio / Topic якщо є кращий кандидат).
        /// </summary>
        private int ScoreVideo(VideoSearchResult video, string artist, string title, TimeSpan trackDuration)
        {
            int score = 0;
            var ytTitle = video.Title.ToLower();
            var ytAuthor = video.Author.ToLower();
            var artLow = artist.ToLower();
            var titleLow = title.ToLower();

            // ================================================================
            // ОСНОВНИЙ КРИТЕРІЙ — Fuzzy matching (0–100)
            // TokenSetRatio ігнорує порядок слів і зайві слова в назві —
            // тому "Veilr Rampant Official Video" і "Rampant Veilr" дадуть ~100.
            // ================================================================

            string searchQuery = $"{artLow} {titleLow}";
            int fuzzyScore = Fuzz.TokenSetRatio(searchQuery, ytTitle);

            // Масштабуємо: fuzzy дає 0–100, множимо на 1.5 щоб він домінував над бонусами
            score += (int)(fuzzyScore * 1.5);

            // ================================================================
            // ПІДТВЕРДЖУЮЧІ СИГНАЛИ
            // ================================================================

            // Автор каналу = артист — дуже надійний сигнал
            if (ytAuthor.Contains(artLow)) score += 30;

            // VEVO канал — завжди офіційний кліп
            if (ytAuthor.Contains("vevo")) score += 25;

            // "official video" в назві — явний сигнал кліпу
            // Підняли до +50 щоб завжди перекривав перевагу тривалості
            if (ytTitle.Contains("official video") ||
                ytTitle.Contains("official music video")) score += 50;

            // "music video" без "official" — теж кліп
            // але не рахуємо якщо це небажаний контент
            bool isBadContent = ytTitle.Contains("fan made")
                             || ytTitle.Contains("fan-made")
                             || ytTitle.Contains("karaoke")
                             || ytTitle.Contains("reaction")
                             || ytTitle.Contains("lyrics")
                             || (ytTitle.Contains("cover") && !titleLow.Contains("cover"));

            if (!isBadContent && ytTitle.Contains("music video") &&
                !ytTitle.Contains("official music video")) score += 30;

            // "trailer" + назва треку одночасно — ігровий/анімаційний кліп
            if (ytTitle.Contains("trailer") && ytTitle.Contains(titleLow)) score += 20;

            // ================================================================
            // ЗБІГ ТРИВАЛОСТІ
            // ================================================================

            if (trackDuration > TimeSpan.Zero && video.Duration > TimeSpan.Zero)
            {
                double diffSec = Math.Abs((video.Duration - trackDuration).TotalSeconds);

                if (diffSec < 5) score += 25;
                else if (diffSec < 15) score += 15;
                else if (diffSec < 30) score += 5;
                else if (diffSec > 120) score -= 20;
            }

            // ================================================================
            // ШТРАФИ — явний небажаний контент
            // ================================================================

            if (ytTitle.Contains("karaoke")) score -= 60;
            if (ytTitle.Contains("1 hour")) score -= 60;
            if (ytTitle.Contains("reaction")) score -= 50;
            if (ytTitle.Contains("fan made") ||
                ytTitle.Contains("fan-made")) score -= 50;
            if (ytTitle.Contains("cover") && !titleLow.Contains("cover")) score -= 40;
            if (ytTitle.Contains("lyrics")) score -= 50;

            // YouTube - Topic канали — автоматично згенерований аудіо стрім,
            // завжди штрафуємо бо YouTube і без нього ставить офіційне аудіо першим
            if (ytAuthor.EndsWith("- topic")) score -= 70;

            // Штраф за різний алфавіт у залишку назви відео
            // Наприклад: трек латиницею, а в назві відео є "Караоке" кирилицею
            string remainder = ytTitle
                .Replace(titleLow, "")
                .Replace(artLow, "")
                .Trim();

            if (!AreSameAlphabetGroup(titleLow, remainder)) score -= 50;

            // Штраф за відсутність відео-маркера — пріоритет на кліпах
            // VEVO не штрафуємо бо це завжди кліп
            // Небажаний контент не рахується як валідний маркер
            bool hasVideoMarker = !isBadContent && (
                                   ytTitle.Contains("music video")
                                || ytTitle.Contains("official video")
                                || ytTitle.Contains("official music video")
                                || ytTitle.Contains("trailer")
                                || ytTitle.Contains("clip"))
                                || ytAuthor.Contains("vevo");

            if (!hasVideoMarker) score -= 50;

            return score;
        }

        /// <summary>
        /// Перевіряє чи відео є пріоритетним кліпом:
        /// official video, VEVO, або trailer з назвою треку.
        /// </summary>
        private bool IsVideoPriorityCandidate(VideoSearchResult video, string titleLow)
        {
            var ytTitle = video.Title.ToLower();
            var ytAuthor = video.Author.ToLower();

            return ytTitle.Contains("official video")
                || ytTitle.Contains("official music video")
                || ytAuthor.Contains("vevo")
                || (ytTitle.Contains("trailer") && ytTitle.Contains(titleLow));
        }

        private async Task ProcessSongAsync(string query, string artist, string title, TimeSpan trackDuration, CancellationToken token)
        {
            try
            {
                if (!await WaitForInternetConnection(token))
                {
                    Log("[NETWORK ERROR] No internet connection available", "error");
                    if (File.Exists(_loadingVideoPath))
                    {
                        await Dispatcher.InvokeAsync(() => PlayLocalLoop(_loadingVideoPath, "No Internet"));
                    }
                    return;
                }

                if (_youtubeWrapper == null || !_youtubeWrapper.IsLoaded)
                {
                    Log("[YOUTUBE ERROR] YouTube wrapper not available", "error");

                    // Спроба автооновлення
                    await CheckYoutubeHealthAsync();

                    if (_youtubeWrapper == null || !_youtubeWrapper.IsLoaded)
                    {
                        if (File.Exists(_loadingVideoPath))
                        {
                            await Dispatcher.InvokeAsync(() => PlayLocalLoop(_loadingVideoPath, "YouTube Unavailable"));
                        }
                        return;
                    }
                }

                if (!CheckYouTubeRateLimit())
                {
                    Log("[RATE LIMIT] Waiting 1 minute before retrying...", "warning");
                    await Task.Delay(TimeSpan.FromMinutes(1), token);
                    if (token.IsCancellationRequested) return;

                    if (!CheckYouTubeRateLimit())
                    {
                        Log("[RATE LIMIT] Still throttled after wait, skipping track", "warning");
                        return;
                    }
                }

                string videoId = "";
                string streamUrl = "";

                if (_smartCache.TryGetValue(query, out var cachedData))
                {
                    if (DateTime.Now < cachedData.ExpiryTime)
                    {
                        Log($"[CACHE HIT] {cachedData.VideoId}", "success");
                        await Dispatcher.InvokeAsync(() => PlayVideo(cachedData.StreamUrl));
                        return;
                    }
                    else
                    {
                        videoId = cachedData.VideoId;
                        _smartCache.TryRemove(query, out _);
                        Log($"[CACHE] Expired entry removed for: {query}", "info");
                    }
                }

                if (string.IsNullOrEmpty(videoId))
                {
                    using var searchCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    searchCts.CancelAfter(TimeSpan.FromSeconds(NETWORK_TIMEOUT_SECONDS));

                    try
                    {
                        Log($"[SEARCH] Searching for: '{query}'", "info");

                        // Запитуємо 5 кандидатів замість 1 — щоб мати з чого вибирати
                        var searchResults = await _youtubeWrapper.SearchVideosAsync(query, 5, searchCts.Token);

                        if (searchResults.Count > 0)
                        {
                            string titleLow = title.ToLower();

                            // Перевіряємо чи є серед результатів пріоритетний кліп
                            // (official video / VEVO / trailer + назва треку)
                            bool hasPriorityCandidate = searchResults.Any(v => IsVideoPriorityCandidate(v, titleLow));

                            // Скоруємо кожен результат
                            var scored = searchResults.Select(v =>
                            {
                                int s = ScoreVideo(v, artist, title, trackDuration);

                                // Контекстний штраф: якщо є кращий кандидат —
                                // official audio і Topic канали йдуть на другий план
                                if (hasPriorityCandidate)
                                {
                                    var ytTitle = v.Title.ToLower();
                                    var ytAuthor = v.Author.ToLower();

                                    if (ytTitle.Contains("official audio")) s -= 40;
                                    if (ytAuthor.EndsWith("- topic")) s -= 40;
                                    if (ytTitle.Contains("official lyric video") ||
                                        ytTitle.Contains("lyric video")) s -= 30;
                                }

                                return new { Video = v, Score = s };
                            })
                            .OrderByDescending(x => x.Score)
                            .ToList();

                            // Логуємо всіх кандидатів для діагностики
                            for (int i = 0; i < scored.Count; i++)
                            {
                                var v = scored[i];
                                Log($"[SEARCH] #{i + 1} score={v.Score:+#;-#;0} | '{v.Video.Title}' by {v.Video.Author} ({v.Video.Duration:mm\\:ss})", "info");
                            }

                            var best = scored[0].Video;
                            videoId = best.VideoId;
                            Log($"[SEARCH] Best match: {videoId} - '{best.Title}' by {best.Author} (score={scored[0].Score})", "success");
                        }
                        else
                        {
                            Log($"[SEARCH] No results for: {query}", "warning");
                            return;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Log($"[SEARCH TIMEOUT] Request timed out after {NETWORK_TIMEOUT_SECONDS}s", "warning");
                        return;
                    }
                    catch (Exception ex)
                    {
                        // ДЕТАЛЬНЕ ЛОГУВАННЯ ПОМИЛКИ
                        string errorDetails = $"{ex.GetType().Name}: {ex.Message}";

                        if (ex.InnerException != null)
                        {
                            errorDetails += $"\n    Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
                        }

                        // Витягуємо перший рядок stack trace
                        if (!string.IsNullOrEmpty(ex.StackTrace))
                        {
                            var stackLines = ex.StackTrace.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                            if (stackLines.Length > 0)
                            {
                                errorDetails += $"\n    At: {stackLines[0].Trim()}";
                            }
                        }

                        Log($"[SEARCH ERROR] {errorDetails}", "error");

                        // Можливо YouTube API зламався - перевіряємо
                        _ = Task.Run(() => CheckYoutubeHealthAsync());
                        return;
                    }
                }

                if (token.IsCancellationRequested || string.IsNullOrEmpty(videoId))
                    return;

                try
                {
                    using var manifestCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    manifestCts.CancelAfter(TimeSpan.FromSeconds(NETWORK_TIMEOUT_SECONDS));

                    Log($"[STREAM] Getting stream URL for: {videoId}", "info");
                    var streamInfo = await _youtubeWrapper.GetVideoStreamAsync(videoId, manifestCts.Token);

                    if (streamInfo != null)
                    {
                        streamUrl = streamInfo.Url;

                        _smartCache[query] = new CachedVideo
                        {
                            VideoId = videoId,
                            StreamUrl = streamUrl,
                            ExpiryTime = DateTime.Now.AddHours(YOUTUBE_CACHE_HOURS)
                        };

                        while (_smartCache.Count > MAX_CACHE_SIZE)
                        {
                            var oldestKey = _smartCache.OrderBy(kv => kv.Value.ExpiryTime).First().Key;
                            _smartCache.TryRemove(oldestKey, out _);
                        }

                        Log($"[STREAM] Quality: {streamInfo.Quality}, Container: {streamInfo.Container}, Height: {streamInfo.Height}p", "info");
                    }
                    else
                    {
                        Log($"[ERROR] No suitable stream found for: {videoId}", "error");
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    Log($"[TIMEOUT] Manifest request timed out after {NETWORK_TIMEOUT_SECONDS}s", "warning");
                    return;
                }
                catch (Exception ex)
                {
                    // ДЕТАЛЬНЕ ЛОГУВАННЯ ПОМИЛКИ MANIFEST
                    string errorDetails = $"{ex.GetType().Name}: {ex.Message}";

                    if (ex.InnerException != null)
                    {
                        errorDetails += $"\n    Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
                    }

                    if (!string.IsNullOrEmpty(ex.StackTrace))
                    {
                        var stackLines = ex.StackTrace.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        if (stackLines.Length > 0)
                        {
                            errorDetails += $"\n    At: {stackLines[0].Trim()}";
                        }
                    }

                    Log($"[MANIFEST ERROR] {errorDetails}", "error");
                    _smartCache.TryRemove(query, out _);

                    // Можливо YouTube API зламався
                    _ = Task.Run(() => CheckYoutubeHealthAsync());
                    return;
                }

                if (token.IsCancellationRequested)
                    return;

                if (!string.IsNullOrEmpty(streamUrl))
                {
                    if (Uri.TryCreate(streamUrl, UriKind.Absolute, out var validatedUri))
                    {
                        Log("[PLAYBACK] Starting video playback...", "info");
                        await Dispatcher.InvokeAsync(() => PlayVideo(streamUrl));
                    }
                    else
                    {
                        Log($"[ERROR] Invalid stream URL: {streamUrl}", "error");
                        _smartCache.TryRemove(query, out _);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log("[SEARCH] Operation cancelled", "info");
            }
            catch (Exception ex)
            {
                // ДЕТАЛЬНЕ ЛОГУВАННЯ КРИТИЧНИХ ПОМИЛОК
                string errorDetails = $"{ex.GetType().Name}: {ex.Message}";

                if (ex.InnerException != null)
                {
                    errorDetails += $"\n    Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
                }

                if (!string.IsNullOrEmpty(ex.StackTrace))
                {
                    var stackLines = ex.StackTrace.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (stackLines.Length > 0)
                    {
                        errorDetails += $"\n    At: {stackLines[0].Trim()}";
                    }
                }

                Log($"[FATAL ERROR] ProcessSong:\n{errorDetails}", "error");
            }
        }

        private void PlayVideo(string url)
        {
            if (_vlcPlayer == null || _isDisposing)
            {
                Log("[PLAYBACK ERROR] VLC Player not available", "error");
                return;
            }

            try
            {
                _isLocalLoopPlaying = false;
                _isVideoLoaded = false;
                _isLoopingVideo = false;
                _lastVlcRawTime = -1;
                VideoImage.Opacity = 1.0;

                using var media = new LibVLCSharp.Shared.Media(_libVLC, new Uri(url));
                media.AddOption(":network-caching=1000");
                media.AddOption(":clock-jitter=0");
                media.AddOption(":clock-synchro=0");
                media.AddOption(":avcodec-hw=any");
                media.AddOption(":input-repeat=65535");
                media.AddOption(":no-audio");

                Log("[VLC] Starting playback with optimized settings", "info");

                lock (_vlcLock)
                {
                    if (_vlcPlayer != null && !_isDisposing)
                    {
                        _vlcPlayer.Play(media);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[VLC ERROR] Playback failed: {ex.Message}", "error");
                _isVideoLoaded = false;

                if (File.Exists(_loadingVideoPath))
                {
                    PlayLocalLoop(_loadingVideoPath, "Playback Error Recovery");
                }
            }
        }

        private void UpdateTimingDisplay(TimeSpan audio, TimeSpan video, bool isPlaying, bool vlcUpdated = false)
        {
            if (audio < TimeSpan.Zero) audio = TimeSpan.Zero;
            if (video < TimeSpan.Zero) video = TimeSpan.Zero;

            TxtAudioTime.Text = audio.ToString(@"mm\:ss\.f");
            TxtVideoTime.Text = video.ToString(@"mm\:ss\.f");

            if (vlcUpdated && _isVideoLoaded)
                TxtVideoTime.Foreground = new SolidColorBrush(Color.FromRgb(80, 250, 123));
            else if (_isVideoLoaded)
                TxtVideoTime.Foreground = new SolidColorBrush(Color.FromRgb(139, 233, 253));
            else
                TxtVideoTime.Foreground = Brushes.Gray;

            if (!isPlaying)
            {
                TxtAudioTime.Foreground = Brushes.Gray;
                TxtDiff.Text = "PAUSED";
                TxtDiff.Foreground = Brushes.Gray;
                return;
            }

            TxtAudioTime.Foreground = Brushes.White;

            if (!_isLoopingVideo)
            {
                double diff = _displayDiff;
                if (double.IsNaN(diff))
                {
                    TxtDiff.Text = "---";
                    TxtDiff.Foreground = Brushes.Gray;
                    return;
                }

                TxtDiff.Text = $"{diff:+0.00;-0.00;0.00}s";
                double absDiff = Math.Abs(diff);

                if (absDiff < 0.3)
                    TxtDiff.Foreground = new SolidColorBrush(Color.FromRgb(80, 250, 123));
                else if (absDiff < 1.0)
                    TxtDiff.Foreground = new SolidColorBrush(Color.FromRgb(255, 184, 108));
                else if (absDiff < 2.0)
                    TxtDiff.Foreground = new SolidColorBrush(Color.FromRgb(255, 121, 198));
                else
                    TxtDiff.Foreground = new SolidColorBrush(Color.FromRgb(255, 85, 85));
            }
            else
            {
                TxtDiff.Text = _isLocalLoopPlaying ? "LOCAL" : "LOOP";
                TxtDiff.Foreground = Brushes.Cyan;
            }
        }

        private static string GetAlphabetBlock(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "None";

            foreach (char c in text)
            {
                if (char.IsLetter(c))
                {
                    if (c >= 0x0041 && c <= 0x024F) return "Latin";
                    if (c >= 0x0400 && c <= 0x04FF) return "Cyrillic";
                    if (c >= 0x0370 && c <= 0x03FF) return "Greek";
                    if (c >= 0x0600 && c <= 0x06FF) return "Arabic";
                    if (c >= 0x0590 && c <= 0x05FF) return "Hebrew";
                    return "Other";
                }
            }

            return "None";
        }

        private static bool AreSameAlphabetGroup(string str1, string str2)
        {
            string block1 = GetAlphabetBlock(str1);
            string block2 = GetAlphabetBlock(str2);

            if (block1 == "None" || block2 == "None") return true;

            return block1 == block2;
        }

        private void Log(string message, string type)
        {
            try
            {
                string timeStr = DateTime.Now.ToString("HH:mm:ss.fff");

                Task.Run(() =>
                {
                    try
                    {
                        lock (_logFileLock)
                        {
                            File.AppendAllText(_logPath, $"[{timeStr}] {message}{Environment.NewLine}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to write log: {ex.Message}");
                    }
                });

                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        var paragraph = new Paragraph { Margin = new Thickness(0) };
                        paragraph.Inlines.Add(new Run($"[{timeStr}] ") { Foreground = Brushes.Gray });

                        var runMsg = new Run(message);
                        runMsg.Foreground = type switch
                        {
                            "success" => new SolidColorBrush(Color.FromRgb(80, 250, 123)),
                            "error" => new SolidColorBrush(Color.FromRgb(255, 85, 85)),
                            "warning" => new SolidColorBrush(Color.FromRgb(255, 184, 108)),
                            _ => new SolidColorBrush(Color.FromRgb(139, 233, 253))
                        };

                        paragraph.Inlines.Add(runMsg);
                        LogOutput.Document.Blocks.Add(paragraph);

                        while (LogOutput.Document.Blocks.Count > 100)
                        {
                            LogOutput.Document.Blocks.Remove(LogOutput.Document.Blocks.FirstBlock);
                        }

                        LogOutput.ScrollToEnd();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to display log: {ex.Message}");
                    }
                }, DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Log method failed: {ex.Message}");
            }
        }
    }
}