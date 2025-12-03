using LibVLCSharp.Shared;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Media.Control;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Videos.Streams;

namespace WallpaperMusicPlayer
{
    public partial class MainWindow : Window
    {
        private GlobalSystemMediaTransportControlsSessionManager? _mediaManager;
        private readonly YoutubeClient _youtube = new();
        private readonly string _logPath;

        // --- Шляхи до локальних відео ---
        private readonly string _idleVideoPath;    // Коли немає джерела (idle.mp4)
        private readonly string _loadingVideoPath; // Коли вантажиться трек (loading.mp4)

        private DateTime _lastSeekTime = DateTime.MinValue;
        private long _lastVlcRawTime = -1;
        private DateTime _lastVlcUpdateTime = DateTime.MinValue;

        // Покращена синхронізація
        private readonly Queue<double> _diffHistory = new Queue<double>(5);
        private double _displayDiff = 0;
        private double _syncDiff = 0;

        // VLC об'єкти
        private LibVLC _libVLC;
        private LibVLCSharp.Shared.MediaPlayer _vlcPlayer;

        // Стан
        private string _lastSong = "";
        private CancellationTokenSource? _searchCts;

        // Плавні переходи
        private readonly DispatcherTimer _fadeTimer = new();
        private bool _isFading = false;
        private double _currentOpacity = 1.0;

        // Кеш
        private struct CachedVideo
        {
            public string VideoId;
            public string StreamUrl;
            public DateTime ExpiryTime;
        }

        private readonly Dictionary<string, CachedVideo> _smartCache = new();

        // Таймери та час
        private readonly DispatcherTimer _monitorTimer = new();
        private bool _isVideoLoaded = false;
        private bool _isLoopingVideo = false;
        private bool _wasPlaying = false;

        // --- Прапор локального лупу ---
        private bool _isLocalLoopPlaying = false;

        private readonly object _vlcLock = new object();
        private readonly object _cacheLock = new object();

        public MainWindow()
        {
            InitializeComponent();
            _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_log.txt");

            // Ініціалізація шляхів
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _idleVideoPath = Path.Combine(baseDir, "idle.mp4");       // Головна заглушка (офлайн)
            _loadingVideoPath = Path.Combine(baseDir, "loading.mp4"); // Перехідна заглушка

            Core.Initialize();
            _libVLC = new LibVLC();
            _vlcPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
            VideoPlayer.MediaPlayer = _vlcPlayer;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _vlcPlayer.Volume = 0;
            _vlcPlayer.LengthChanged += VlcPlayer_LengthChanged;
            _vlcPlayer.EncounteredError += VlcPlayer_EncounteredError;
            _vlcPlayer.Playing += VlcPlayer_Playing;
            _vlcPlayer.EndReached += VlcPlayer_EndReached;

            InitializeAsync();

            // На старті запускаємо idle.mp4
            PlayLocalLoop(_idleVideoPath, "Startup");
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
                    // Прокручуємо лог вниз при відкритті
                    LogOutput.ScrollToEnd();
                }
            }
        }

        private void VlcPlayer_EndReached(object? sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (_isLoopingVideo || _isLocalLoopPlaying)
                {
                    lock (_vlcLock)
                    {
                        _vlcPlayer.Stop();
                        _vlcPlayer.Play();
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
                    Log($"[VLC] Type: Local Loop", "info");
                    return;
                }

                if (durationMs < 60000)
                {
                    _isLoopingVideo = true;
                    Log($"[VLC] Type: Online Loop", "info");
                }
                else
                {
                    _isLoopingVideo = false;
                    Log($"[VLC] Type: Music Video", "info");
                }
            });
        }

        private void VlcPlayer_Playing(object? sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _isVideoLoaded = true;

                // Якщо це локальний луп, просто плавно показуємо
                if (_isLocalLoopPlaying)
                {
                    Log("[VLC] Local Loop Playing", "success");
                    StartFadeIn();
                    return;
                }

                Log("[VLC] Status: Playing", "success");
                StartFadeIn();

                // Швидкий стрибок для онлайн відео
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
                                if (!_isLoopingVideo && currentAudioTime.TotalSeconds > 1 && _vlcPlayer.Length > 0)
                                {
                                    long targetMs = (long)currentAudioTime.TotalMilliseconds;
                                    if (targetMs < _vlcPlayer.Length)
                                    {
                                        lock (_vlcLock)
                                        {
                                            _vlcPlayer.Time = targetMs;
                                            _lastVlcRawTime = targetMs;
                                            _lastVlcUpdateTime = DateTime.Now;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            });
        }

        private void VlcPlayer_EncounteredError(object? sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                Log("[VLC] Critical Error", "error");
                _isVideoLoaded = false;

                // Якщо помилка, пробуємо запустити loading.mp4 як fallback
                if (!_isLocalLoopPlaying)
                {
                    PlayLocalLoop(_loadingVideoPath, "Error Recovery");
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
                Log("Waiting for music...", "success");
            }
            catch (Exception ex)
            {
                Log($"INIT ERROR: {ex.Message}", "error");
            }
        }

        // --- Універсальний метод для локальних файлів (idle або loading) ---
        private void PlayLocalLoop(string path, string reason)
        {
            if (!File.Exists(path))
            {
                Log($"[LOCAL] File not found: {Path.GetFileName(path)}", "error");
                return;
            }

            // Перевіряємо, чи ми вже не граємо цей файл, щоб не перезапускати дарма
            // Але якщо це переключення між idle і loading, треба перезапустити

            try
            {
                Log($"[LOCAL] Playing {Path.GetFileName(path)} ({reason})", "info");

                _isLocalLoopPlaying = true;
                _isLoopingVideo = true;
                _isVideoLoaded = false;
                _lastVlcRawTime = -1;

                // Скидаємо прозорість для фейду
                VideoPlayer.Opacity = 0.0;
                _currentOpacity = 0.0;

                lock (_vlcLock)
                {
                    using var media = new LibVLCSharp.Shared.Media(_libVLC, path);
                    media.AddOption(":input-repeat=65535");
                    media.AddOption(":no-audio");
                    _vlcPlayer.Play(media);
                }
            }
            catch (Exception ex)
            {
                Log($"[LOCAL ERROR] {ex.Message}", "error");
                _isLocalLoopPlaying = false;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _monitorTimer.Stop();
                _fadeTimer.Stop();
                _searchCts?.Cancel();
                lock (_vlcLock) { _vlcPlayer.Stop(); _vlcPlayer.Dispose(); }
                _libVLC.Dispose();
            }
            catch { }
            base.OnClosed(e);
        }

        private async void MonitorLoop(object? sender, EventArgs e)
        {
            if (_mediaManager == null) return;

            try
            {
                var session = GetRelevantSession(_mediaManager);

                // 1. Якщо плеєри закриті (немає сесії)
                if (session == null)
                {
                    // Якщо зараз не грає локальний луп (або якщо грає loading, а треба idle)
                    // Тут спрощення: якщо сесії немає, має грати IDLE.
                    // Перевіряємо, чи ми вже не в режимі IDLE, щоб не спамити Play
                    if (!_isLocalLoopPlaying)
                    {
                        PlayLocalLoop(_idleVideoPath, "No Session");
                    }
                    return;
                }

                var playbackInfo = session.GetPlaybackInfo();
                bool isMusicPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                if (isMusicPlaying != _wasPlaying)
                {
                    Log($"[STATUS] Music: {(isMusicPlaying ? "PLAYING" : "PAUSED")}", isMusicPlaying ? "success" : "warning");
                    _wasPlaying = isMusicPlaying;
                }

                // 2. Якщо музика на ПАУЗІ
                if (!isMusicPlaying)
                {
                    // ВИМОГА: При паузі НЕ показувати idle. 
                    // Просто ставимо відео на паузу (стоп-кадр).
                    if (_vlcPlayer.IsPlaying)
                    {
                        lock (_vlcLock)
                        {
                            _vlcPlayer.Pause();
                        }
                        Log("[MONITOR] Video Paused", "info");
                    }

                    // Оновлюємо таймер (статичний)
                    var timelinePaused = session.GetTimelineProperties();
                    TimeSpan pausedTime = timelinePaused?.Position ?? TimeSpan.Zero;
                    UpdateTimingDisplay(pausedTime, TimeSpan.Zero, false, false);
                    return;
                }

                // Якщо музика грає, а VLC стоїть (з паузи вийшли)
                if (!_vlcPlayer.IsPlaying && _isVideoLoaded)
                {
                    lock (_vlcLock) { _vlcPlayer.Play(); }
                }

                var info = await session.TryGetMediaPropertiesAsync();
                string artist = info?.Artist ?? "";
                string title = info?.Title ?? "";
                string currentSong = (!string.IsNullOrEmpty(artist) || !string.IsNullOrEmpty(title))
                                     ? $"{artist} - {title}" : "Unknown";

                // 3. Зміна треку
                if (currentSong != "Unknown" && currentSong != _lastSong)
                {
                    _lastSong = currentSong;

                    // ВИМОГА: При зміні треку показувати LOADING (інший idle)
                    PlayLocalLoop(_loadingVideoPath, "Track Switch");

                    // Скидаємо змінні
                    _isVideoLoaded = false;
                    _isLoopingVideo = false;
                    _lastVlcRawTime = -1;
                    _displayDiff = 0;
                    _syncDiff = 0;
                    _diffHistory.Clear();

                    _searchCts?.Cancel();
                    _searchCts = new CancellationTokenSource();

                    Log($"♪ Next Track: {currentSong}", "info");

                    // Починаємо пошук, поки грає loading.mp4
                    _ = ProcessSongAsync(currentSong, _searchCts.Token);
                }

                // Якщо грає будь-який локальний файл (loading або idle), пропускаємо сінхронізацію
                if (_isLocalLoopPlaying)
                {
                    UpdateTimingDisplay(TimeSpan.Zero, TimeSpan.Zero, true, false);
                    TxtDiff.Text = "LOCAL";
                    TxtDiff.Foreground = Brushes.Cyan;
                    return;
                }

                // --- Синхронізація (для YouTube відео) ---
                var timeline = session.GetTimelineProperties();
                TimeSpan liveAudioTime = TimeSpan.Zero;
                if (timeline != null)
                {
                    liveAudioTime = timeline.Position + (DateTimeOffset.Now - timeline.LastUpdatedTime);
                }

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
                            double safeRate = Math.Min(_vlcPlayer.Rate, 1.05);
                            double extrapolatedMs = _lastVlcRawTime + (msPassed * safeRate);
                            if (_vlcPlayer.Length > 0 && extrapolatedMs > _vlcPlayer.Length) extrapolatedMs = _vlcPlayer.Length;
                            smoothVlcTime = TimeSpan.FromMilliseconds(extrapolatedMs);
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
                if (ex is not OperationCanceledException) Log($"[MONITOR] Error: {ex.Message}", "error");
            }
        }

        // ==========================================
        // ПЛАВНІ ПЕРЕХОДИ (Без змін)
        // ==========================================
        private void StartFadeIn()
        {
            if (_isFading) return;
            _isFading = true;
            _fadeTimer.Start();
        }

        private async Task StartFadeOutAsync()
        {
            if (_isFading || VideoPlayer.Opacity < 0.1) return;
            _isFading = true;
            var tcs = new TaskCompletionSource<bool>();
            EventHandler? handler = null;
            handler = (s, e) =>
            {
                if (_currentOpacity <= 0.0)
                {
                    _fadeTimer.Tick -= handler;
                    _fadeTimer.Stop();
                    _isFading = false;
                    tcs.TrySetResult(true);
                }
            };
            _fadeTimer.Tick += handler;
            _fadeTimer.Start();
            await Task.WhenAny(tcs.Task, Task.Delay(1000));
        }

        private void FadeTimer_Tick(object? sender, EventArgs e)
        {
            if (_currentOpacity < VideoPlayer.Opacity || (_currentOpacity < 1.0 && VideoPlayer.Opacity >= _currentOpacity))
            {
                _currentOpacity += 0.08;
                if (_currentOpacity >= 1.0)
                {
                    _currentOpacity = 1.0;
                    VideoPlayer.Opacity = 1.0;
                    _fadeTimer.Stop();
                    _isFading = false;
                    return;
                }
                VideoPlayer.Opacity = _currentOpacity;
            }
            else if (_currentOpacity > VideoPlayer.Opacity || (_currentOpacity > 0.0 && VideoPlayer.Opacity <= _currentOpacity))
            {
                _currentOpacity -= 0.05;
                if (_currentOpacity <= 0.0)
                {
                    _currentOpacity = 0.0;
                    VideoPlayer.Opacity = 0.0;
                    return;
                }
                VideoPlayer.Opacity = _currentOpacity;
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
                    catch { continue; }
                }
                return manager.GetCurrentSession();
            }
            catch { return null; }
        }

        private void SyncVideoState(TimeSpan targetAudio, TimeSpan currentVideo)
        {
            if (_isLoopingVideo || _isLocalLoopPlaying) return;

            if ((DateTime.Now - _lastSeekTime).TotalSeconds < 1.0) return;

            try
            {
                double instantDiff = (targetAudio - currentVideo).TotalSeconds;
                if (double.IsNaN(instantDiff) || double.IsInfinity(instantDiff)) return;

                _diffHistory.Enqueue(instantDiff);
                if (_diffHistory.Count > 5) _diffHistory.Dequeue();

                var sortedDiffs = _diffHistory.OrderBy(x => x).ToList();
                double medianDiff = sortedDiffs[sortedDiffs.Count / 2];

                _displayDiff = (_displayDiff * 0.75) + (medianDiff * 0.25);
                _syncDiff = (_syncDiff * 0.4) + (medianDiff * 0.6);

                TxtDiff.Text = $"{_displayDiff:+0.00;-0.00;0.00}s";
                double absDiff = Math.Abs(_syncDiff);

                if (absDiff > 3.0)
                {
                    long targetMs = (long)targetAudio.TotalMilliseconds;
                    if (_vlcPlayer.Length > 0 && targetMs >= 0 && targetMs < _vlcPlayer.Length)
                    {
                        lock (_vlcLock) { _vlcPlayer.Time = targetMs; _vlcPlayer.SetRate(1.0f); }
                        _lastSeekTime = DateTime.Now;
                        _lastVlcRawTime = targetMs;
                        _lastVlcUpdateTime = DateTime.Now;
                        _syncDiff = 0;
                        _displayDiff = 0;
                        _diffHistory.Clear();
                    }
                    return;
                }

                if (absDiff > 1.5 && absDiff <= 3.0)
                {
                    long targetMs = (long)targetAudio.TotalMilliseconds;
                    if (_vlcPlayer.Length > 0 && targetMs >= 0 && targetMs < _vlcPlayer.Length)
                    {
                        lock (_vlcLock) { _vlcPlayer.Time = targetMs; }
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
                    if (Math.Abs(_vlcPlayer.Rate - 1.0f) > 0.005f) lock (_vlcLock) { _vlcPlayer.SetRate(1.0f); }
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

                if (Math.Abs(_vlcPlayer.Rate - targetRate) > 0.003f) lock (_vlcLock) { _vlcPlayer.SetRate(targetRate); }
            }
            catch { }
        }

        private async Task ProcessSongAsync(string query, CancellationToken token)
        {
            try
            {
                string videoId = "";
                string streamUrl = "";

                lock (_cacheLock)
                {
                    if (_smartCache.TryGetValue(query, out var cachedData))
                    {
                        if (DateTime.Now < cachedData.ExpiryTime)
                        {
                            Log($"[CACHE] Fast load: {cachedData.VideoId}", "success");
                            PlayVideo(cachedData.StreamUrl);
                            return;
                        }
                        else videoId = cachedData.VideoId;
                    }
                }

                if (string.IsNullOrEmpty(videoId))
                {
                    var searchResults = await _youtube.Search.GetVideosAsync(query, token).CollectAsync(1);
                    if (searchResults.Count > 0) videoId = searchResults[0].Id.Value;
                    else
                    {
                        Log($"[SEARCH] No results: {query}", "warning");
                        return;
                    }
                }

                if (token.IsCancellationRequested || string.IsNullOrEmpty(videoId)) return;

                try
                {
                    var manifest = await _youtube.Videos.Streams.GetManifestAsync(videoId, token);
                    var videoStream = manifest.GetVideoOnlyStreams()
                        .Where(s => s.Container == Container.Mp4 && s.VideoQuality.MaxHeight <= 1080)
                        .OrderByDescending(s => s.VideoQuality.MaxHeight)
                        .FirstOrDefault();

                    if (videoStream != null)
                    {
                        streamUrl = videoStream.Url;
                        lock (_cacheLock)
                        {
                            _smartCache[query] = new CachedVideo
                            {
                                VideoId = videoId,
                                StreamUrl = streamUrl,
                                ExpiryTime = DateTime.Now.AddHours(5)
                            };
                            if (_smartCache.Count > 100)
                                _smartCache.Remove(_smartCache.OrderBy(kv => kv.Value.ExpiryTime).First().Key);
                        }
                    }
                    else
                    {
                        Log($"[ERROR] Stream not found for: {videoId}", "error");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Log($"[ERROR] Manifest: {ex.Message}", "error");
                    lock (_cacheLock) { _smartCache.Remove(query); }
                    return;
                }

                if (token.IsCancellationRequested) return;

                if (!string.IsNullOrEmpty(streamUrl))
                {
                    Log("[DEBUG] Starting playback...", "info");
                    PlayVideo(streamUrl);
                }
            }
            catch (OperationCanceledException) { Log("[SEARCH] Cancelled", "info"); }
            catch (Exception ex) { Log($"[FATAL] {ex.Message}", "error"); }
        }

        private void PlayVideo(string url)
        {
            try
            {
                // Ми переходимо на основне відео - вимикаємо прапор локального лупа
                _isLocalLoopPlaying = false;

                _isVideoLoaded = false;
                _isLoopingVideo = false;
                _lastVlcRawTime = -1;

                var media = new LibVLCSharp.Shared.Media(_libVLC, new Uri(url));
                media.AddOption(":network-caching=300");
                media.AddOption(":clock-jitter=0");
                media.AddOption(":clock-synchro=0");
                media.AddOption(":avcodec-hw=any");
                media.AddOption(":input-repeat=65535");
                media.AddOption(":no-audio");

                Log("[VLC] Fast Play triggered...", "info");
                VideoPlayer.Opacity = 0.0;
                _currentOpacity = 0.0;

                lock (_vlcLock) { _vlcPlayer.Play(media); }
            }
            catch (Exception ex)
            {
                Log($"[VLC ERROR] {ex.Message}", "error");
                _isVideoLoaded = false;
            }
        }

        private void UpdateTimingDisplay(TimeSpan audio, TimeSpan video, bool isPlaying, bool vlcUpdated = false)
        {
            if (audio < TimeSpan.Zero) audio = TimeSpan.Zero;
            if (video < TimeSpan.Zero) video = TimeSpan.Zero;

            TxtAudioTime.Text = audio.ToString(@"mm\:ss\.f");
            TxtVideoTime.Text = video.ToString(@"mm\:ss\.f");

            if (vlcUpdated && _isVideoLoaded) TxtVideoTime.Foreground = new SolidColorBrush(Color.FromRgb(80, 250, 123));
            else if (_isVideoLoaded) TxtVideoTime.Foreground = new SolidColorBrush(Color.FromRgb(139, 233, 253));
            else TxtVideoTime.Foreground = Brushes.Gray;

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
                if (double.IsNaN(diff)) { TxtDiff.Text = "---"; TxtDiff.Foreground = Brushes.Gray; return; }

                TxtDiff.Text = $"{diff:+0.00;-0.00;0.00}s";
                double absDiff = Math.Abs(diff);
                if (absDiff < 0.3) TxtDiff.Foreground = new SolidColorBrush(Color.FromRgb(80, 250, 123));
                else if (absDiff < 1.0) TxtDiff.Foreground = new SolidColorBrush(Color.FromRgb(255, 184, 108));
                else if (absDiff < 2.0) TxtDiff.Foreground = new SolidColorBrush(Color.FromRgb(255, 121, 198));
                else TxtDiff.Foreground = new SolidColorBrush(Color.FromRgb(255, 85, 85));
            }
            else
            {
                TxtDiff.Text = _isLocalLoopPlaying ? "LOCAL" : "LOOP";
                TxtDiff.Foreground = Brushes.Cyan;
            }
        }

        private void Log(string message, string type)
        {
            try
            {
                string timeStr = DateTime.Now.ToString("HH:mm:ss");
                Task.Run(() => { try { File.AppendAllText(_logPath, $"[{timeStr}] {message}{Environment.NewLine}"); } catch { } });

                Dispatcher.Invoke(() =>
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
                        if (LogOutput.Document.Blocks.Count > 100) LogOutput.Document.Blocks.Remove(LogOutput.Document.Blocks.FirstBlock);
                        LogOutput.ScrollToEnd();
                    }
                    catch { }
                });
            }
            catch { }
        }
    }
}