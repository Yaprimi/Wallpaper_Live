using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Media.Control;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Videos.Streams;
using System.Collections.Concurrent;
using System.Threading;
using LibVLCSharp.Shared;

namespace WallpaperMusicPlayer
{
    public partial class MainWindow : Window
    {
        private GlobalSystemMediaTransportControlsSessionManager? _mediaManager;
        private readonly YoutubeClient _youtube = new();
        private readonly string _logPath;
        private DateTime _lastSeekTime = DateTime.MinValue;
        private long _lastVlcRawTime = -1;
        private DateTime _lastVlcUpdateTime = DateTime.MinValue;
        private double _smoothedDiff = 0;
        
        // Покращена синхронізація
        private readonly Queue<double> _diffHistory = new Queue<double>(5); // Зменшено з 10 до 5
        private double _displayDiff = 0; // Для відображення (згладжений)
        private double _syncDiff = 0;    // Для синхронізації (швидкий)

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
        private DateTime _lastSyncTime = DateTime.MinValue;
        private bool _isVideoLoaded = false;
        private bool _isLoopingVideo = false;
        private bool _wasPlaying = false;

        // ВИПРАВЛЕННЯ: Додано локи для thread-safety
        private readonly object _vlcLock = new object();
        private readonly object _cacheLock = new object();

        public MainWindow()
        {
            InitializeComponent();
            _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_log.txt");

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
            _vlcPlayer.EndReached += VlcPlayer_EndReached; // ВИПРАВЛЕННЯ: Додано обробку кінця відео

            InitializeAsync();
        }

        // ВИПРАВЛЕННЯ: Додано обробку кінця відео для лупів
        private void VlcPlayer_EndReached(object? sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (_isLoopingVideo)
                {
                    Log("[VLC] Loop restart", "info");
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

                if (durationMs < 60000)
                {
                    _isLoopingVideo = true;
                    Log($"[VLC] Type: Loop Wallpaper ({TimeSpan.FromMilliseconds(durationMs):mm\\:ss})", "info");
                }
                else
                {
                    _isLoopingVideo = false;
                    Log($"[VLC] Type: Music Video ({TimeSpan.FromMilliseconds(durationMs):mm\\:ss})", "info");
                }
            });
        }

        private void VlcPlayer_Playing(object? sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _isVideoLoaded = true;
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

                                // ВИПРАВЛЕННЯ: Перевірка, що відео не закінчилось
                                if (!_isLoopingVideo && currentAudioTime.TotalSeconds > 1 && _vlcPlayer.Length > 0)
                                {
                                    long targetMs = (long)currentAudioTime.TotalMilliseconds;
                                    if (targetMs < _vlcPlayer.Length)
                                    {
                                        Log($"[SYNC] QuickJump to {currentAudioTime:mm\\:ss}", "warning");
                                        lock (_vlcLock)
                                        {
                                            _vlcPlayer.Time = targetMs;
                                            _lastVlcRawTime = targetMs; // ВИПРАВЛЕННЯ: Оновлюємо після seek
                                            _lastVlcUpdateTime = DateTime.Now;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"[SYNC] QuickJump error: {ex.Message}", "warning");
                }
            });
        }

        private void VlcPlayer_EncounteredError(object? sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                Log("[VLC] Critical Error occurred", "error");
                _isVideoLoaded = false;
                _lastVlcRawTime = -1; // ВИПРАВЛЕННЯ: Скидаємо стан
            });
        }

        private async void InitializeAsync()
        {
            Log("Init Media API (VLC Mode)...", "info");
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

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _monitorTimer.Stop();
                _fadeTimer.Stop(); // Зупиняємо fade таймер
                _searchCts?.Cancel();

                lock (_vlcLock)
                {
                    _vlcPlayer.Stop();
                    _vlcPlayer.Dispose();
                }
                _libVLC.Dispose();
            }
            catch (Exception ex)
            {
                Log($"Cleanup error: {ex.Message}", "error");
            }
            base.OnClosed(e);
        }

        private async void MonitorLoop(object? sender, EventArgs e)
        {
            if (_mediaManager == null) return;

            try
            {
                var session = GetRelevantSession(_mediaManager);

                if (session == null)
                {
                    if (_vlcPlayer.IsPlaying)
                    {
                        Log("[MONITOR] No active session -> Pausing Video", "warning");
                        
                        // Плавне зникнення при паузі
                        _ = Task.Run(async () => 
                        {
                            await StartFadeOutAsync();
                            await Dispatcher.InvokeAsync(() =>
                            {
                                lock (_vlcLock)
                                {
                                    _vlcPlayer.Pause();
                                }
                            });
                        });
                    }
                    return;
                }

                var playbackInfo = session.GetPlaybackInfo();
                bool isMusicPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                if (isMusicPlaying != _wasPlaying)
                {
                    Log($"[STATUS CHANGE] Music is now: {(isMusicPlaying ? "PLAYING" : "PAUSED/STOPPED")}", isMusicPlaying ? "success" : "warning");
                    _wasPlaying = isMusicPlaying;
                }

                if (!isMusicPlaying)
                {
                    if (_vlcPlayer.IsPlaying)
                    {
                        lock (_vlcLock)
                        {
                            _vlcPlayer.Pause();
                        }
                        Log("[MONITOR] Sync Pause executed", "info");
                    }

                    var timelinePaused = session.GetTimelineProperties();
                    TimeSpan pausedTime = timelinePaused?.Position ?? TimeSpan.Zero;
                    UpdateTimingDisplay(pausedTime, TimeSpan.FromMilliseconds(_vlcPlayer.Time), false, false);

                    _lastVlcRawTime = -1;
                    return;
                }

                if (_isVideoLoaded && !_vlcPlayer.IsPlaying)
                {
                    Log("[MONITOR] Resuming Video...", "info");
                    lock (_vlcLock)
                    {
                        _vlcPlayer.Play();
                    }
                    _lastVlcRawTime = -1;
                    _lastVlcUpdateTime = DateTime.Now; // ВИПРАВЛЕННЯ: Оновлюємо час
                }

                var info = await session.TryGetMediaPropertiesAsync();
                string artist = info?.Artist ?? "";
                string title = info?.Title ?? "";
                string currentSong = (!string.IsNullOrEmpty(artist) || !string.IsNullOrEmpty(title))
                                     ? $"{artist} - {title}" : "Unknown";

                if (currentSong != "Unknown" && currentSong != _lastSong)
                {
                    _lastSong = currentSong;

                    _isVideoLoaded = false;
                    _isLoopingVideo = false;
                    _lastVlcRawTime = -1;
                    _smoothedDiff = 0;
                    _syncDiff = 0;
                    _displayDiff = 0;
                    _diffHistory.Clear(); // ВИПРАВЛЕННЯ: Скидаємо фільтр

                    _searchCts?.Cancel();
                    _searchCts = new CancellationTokenSource();

                    Log($"♪ Track Changed: {currentSong}", "info");

                    _ = ProcessSongAsync(currentSong, _searchCts.Token);
                }

                // Математика часу
                var timeline = session.GetTimelineProperties();
                TimeSpan liveAudioTime = TimeSpan.Zero;

                if (timeline != null)
                {
                    liveAudioTime = timeline.Position + (DateTimeOffset.Now - timeline.LastUpdatedTime);
                }

                long currentRawVlcTime = _vlcPlayer.Time;
                TimeSpan smoothVlcTime;

                // ПОКРАЩЕННЯ: Детекція застрягання VLC
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
                        
                        // ВИПРАВЛЕННЯ: Якщо VLC не оновлювався > 500мс, щось не так
                        if (msPassed > 500)
                        {
                            // Примусово оновлюємо
                            _lastVlcRawTime = _vlcPlayer.Time;
                            _lastVlcUpdateTime = DateTime.Now;
                            smoothVlcTime = TimeSpan.FromMilliseconds(_lastVlcRawTime);
                            vlcTimeUpdated = true;
                        }
                        else
                        {
                            double safeRate = Math.Min(_vlcPlayer.Rate, 1.05);
                            double extrapolatedMs = _lastVlcRawTime + (msPassed * safeRate);
                            
                            if (_vlcPlayer.Length > 0 && extrapolatedMs > _vlcPlayer.Length)
                            {
                                extrapolatedMs = _vlcPlayer.Length;
                            }
                            
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
                // ВИПРАВЛЕННЯ: Логуємо критичні помилки
                if (ex is not OperationCanceledException)
                {
                    Log($"[MONITOR] Error: {ex.Message}", "error");
                }
            }
        }

        // ==========================================
        // СИСТЕМА ПЛАВНИХ ПЕРЕХОДІВ
        // ==========================================

        private void StartFadeIn()
        {
            if (_isFading) return;
            
            _isFading = true;
            _currentOpacity = 0.0;
            VideoPlayer.Opacity = 0.0;
            _fadeTimer.Start();
            
            Log("[FADE] Starting fade-in", "info");
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
            
            Log("[FADE] Starting fade-out", "info");
            
            // Чекаємо завершення з таймаутом
            await Task.WhenAny(tcs.Task, Task.Delay(1000));
        }

        private void FadeTimer_Tick(object? sender, EventArgs e)
        {
            // Fade-In (швидше)
            if (_currentOpacity < VideoPlayer.Opacity || (_currentOpacity < 1.0 && VideoPlayer.Opacity >= _currentOpacity))
            {
                _currentOpacity += 0.08; // Швидкість появи (за ~0.3 сек)
                if (_currentOpacity >= 1.0)
                {
                    _currentOpacity = 1.0;
                    VideoPlayer.Opacity = 1.0;
                    _fadeTimer.Stop();
                    _isFading = false;
                    Log("[FADE] Fade-in complete", "success");
                    return;
                }
                VideoPlayer.Opacity = _currentOpacity;
            }
            // Fade-Out (повільніше для плавності)
            else if (_currentOpacity > VideoPlayer.Opacity || (_currentOpacity > 0.0 && VideoPlayer.Opacity <= _currentOpacity))
            {
                _currentOpacity -= 0.05; // Швидкість зникнення (за ~0.5 сек)
                if (_currentOpacity <= 0.0)
                {
                    _currentOpacity = 0.0;
                    VideoPlayer.Opacity = 0.0;
                    // Зупинка відбудеться в StartFadeOutAsync
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
                        {
                            return session;
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }

                return manager.GetCurrentSession();
            }
            catch
            {
                return null; // ВИПРАВЛЕННЯ: Повертаємо null при помилці
            }
        }

        private void SyncVideoState(TimeSpan targetAudio, TimeSpan currentVideo)
        {
            if (_isLoopingVideo) return;

            // ПРИСКОРЕННЯ: Зменшено cooldown з 2.0 до 1.0 секунди
            if ((DateTime.Now - _lastSeekTime).TotalSeconds < 1.0) return;

            try
            {
                double instantDiff = (targetAudio - currentVideo).TotalSeconds;

                if (double.IsNaN(instantDiff) || double.IsInfinity(instantDiff))
                {
                    return;
                }

                // ==========================================
                // ПРИСКОРЕНА СИСТЕМА ФІЛЬТРАЦІЇ
                // ==========================================

                // 1. Додаємо в історію (тепер тільки 5 значень)
                _diffHistory.Enqueue(instantDiff);
                if (_diffHistory.Count > 5) _diffHistory.Dequeue();

                // 2. Медіанний фільтр для відкидання викидів
                var sortedDiffs = _diffHistory.OrderBy(x => x).ToList();
                double medianDiff = sortedDiffs[sortedDiffs.Count / 2];

                // 3. Дві незалежні згладжені величини:
                
                // Для ВІДОБРАЖЕННЯ (плавно, альфа=0.25 - швидше ніж 0.15)
                _displayDiff = (_displayDiff * 0.75) + (medianDiff * 0.25);
                
                // Для СИНХРОНІЗАЦІЇ (дуже швидка реакція, альфа=0.6)
                _syncDiff = (_syncDiff * 0.4) + (medianDiff * 0.6);

                // 4. Використовуємо _displayDiff тільки для UI
                TxtDiff.Text = $"{_displayDiff:+0.00;-0.00;0.00}s";

                // 5. Використовуємо _syncDiff для логіки синхронізації
                double absDiff = Math.Abs(_syncDiff);

                // ==========================================
                // ПРИСКОРЕНА ЛОГІКА СИНХРОНІЗАЦІЇ
                // ==========================================

                // Hard Reset (знизили поріг з 5 до 3 секунд)
                if (absDiff > 3.0)
                {
                    long targetMs = (long)targetAudio.TotalMilliseconds;
                    if (_vlcPlayer.Length > 0 && targetMs >= 0 && targetMs < _vlcPlayer.Length)
                    {
                        Log($"[SYNC] HARD RESET: {absDiff:F2}s", "error");
                        lock (_vlcLock)
                        {
                            _vlcPlayer.Time = targetMs;
                            _vlcPlayer.SetRate(1.0f);
                        }
                        _lastSeekTime = DateTime.Now;
                        _lastVlcRawTime = targetMs;
                        _lastVlcUpdateTime = DateTime.Now;
                        
                        // Скидаємо фільтри
                        _syncDiff = 0;
                        _displayDiff = 0;
                        _diffHistory.Clear();
                    }
                    return;
                }

                // Soft Reset (знизили поріг з 2.5 до 1.5 секунд)
                if (absDiff > 1.5 && absDiff <= 3.0)
                {
                    long targetMs = (long)targetAudio.TotalMilliseconds;
                    if (_vlcPlayer.Length > 0 && targetMs >= 0 && targetMs < _vlcPlayer.Length)
                    {
                        Log($"[SYNC] Soft jump: {absDiff:F2}s", "warning");
                        lock (_vlcLock)
                        {
                            _vlcPlayer.Time = targetMs;
                        }
                        _lastSeekTime = DateTime.Now;
                        _lastVlcRawTime = targetMs;
                        _lastVlcUpdateTime = DateTime.Now;
                        
                        // НЕ скидаємо фільтри повністю, тільки зменшуємо
                        _syncDiff *= 0.3;
                        _displayDiff *= 0.3;
                    }
                    return;
                }

                // Мертва зона (зменшена з 0.08 до 0.05 для швидшої реакції)
                if (absDiff < 0.05)
                {
                    if (Math.Abs(_vlcPlayer.Rate - 1.0f) > 0.005f)
                    {
                        lock (_vlcLock)
                        {
                            _vlcPlayer.SetRate(1.0f);
                        }
                    }
                    return;
                }

                // ==========================================
                // АГРЕСИВНА АДАПТИВНА КОРЕКЦІЯ ШВИДКОСТІ
                // ==========================================

                float targetRate = 1.0f;

                // Використовуємо агресивнішу нелінійну функцію
                if (_syncDiff > 0.05) // Відео відстає
                {
                    if (absDiff > 1.0) targetRate = 1.10f;      // Сильно відстає - максимальне прискорення
                    else if (absDiff > 0.5) targetRate = 1.06f; // Помірно відстає
                    else if (absDiff > 0.2) targetRate = 1.03f; // Трошки відстає
                    else targetRate = 1.01f;                     // Мінімально відстає
                }
                else if (_syncDiff < -0.05) // Відео спішить
                {
                    if (absDiff > 1.0) targetRate = 0.90f;      // Сильно спішить - максимальне уповільнення
                    else if (absDiff > 0.5) targetRate = 0.94f; // Помірно спішить
                    else if (absDiff > 0.2) targetRate = 0.97f; // Трошки спішить
                    else targetRate = 0.99f;                     // Мінімально спішить
                }

                // Застосовуємо зміну швидкості миттєво (зменшено поріг з 0.005 до 0.003)
                if (Math.Abs(_vlcPlayer.Rate - targetRate) > 0.003f)
                {
                    lock (_vlcLock)
                    {
                        _vlcPlayer.SetRate(targetRate);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[SYNC] Error: {ex.Message}", "error");
            }
        }

        private async Task ProcessSongAsync(string query, CancellationToken token)
        {
            try
            {
                string videoId = "";
                string streamUrl = "";

                // Кеш
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
                        else
                        {
                            videoId = cachedData.VideoId;
                        }
                    }
                }

                // Пошук
                if (string.IsNullOrEmpty(videoId))
                {
                    var searchResults = await _youtube.Search.GetVideosAsync(query, token).CollectAsync(1);
                    if (searchResults.Count > 0)
                    {
                        videoId = searchResults[0].Id.Value;
                    }
                    else
                    {
                        Log($"[SEARCH] No results for: {query}", "warning");
                        return; // ВИПРАВЛЕННЯ: Виходимо, якщо нічого не знайдено
                    }
                }

                if (token.IsCancellationRequested || string.IsNullOrEmpty(videoId)) return;

                // Отримання посилання
                try
                {
                    var manifest = await _youtube.Videos.Streams.GetManifestAsync(videoId, token);

                    var videoStream = manifest.GetVideoOnlyStreams()
                        .Where(s => s.Container == Container.Mp4 && s.VideoQuality.MaxHeight <= 1080)
                        .OrderByDescending(s => s.VideoQuality.MaxHeight) // ВИПРАВЛЕННЯ: Сортуємо за якістю
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
                            {
                                var oldestKey = _smartCache.OrderBy(kv => kv.Value.ExpiryTime).First().Key;
                                _smartCache.Remove(oldestKey); // ВИПРАВЛЕННЯ: Видаляємо найстаріший
                            }
                        }
                    }
                    else
                    {
                        Log($"[ERROR] No suitable stream found for: {videoId}", "error");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Log($"[ERROR] Manifest: {ex.Message}", "error");
                    lock (_cacheLock)
                    {
                        _smartCache.Remove(query);
                    }
                    return;
                }

                if (token.IsCancellationRequested) return;

                if (!string.IsNullOrEmpty(streamUrl))
                {
                    Log("[DEBUG] Starting playback...", "info");
                    PlayVideo(streamUrl);
                }
            }
            catch (OperationCanceledException)
            {
                Log("[SEARCH] Cancelled", "info");
            }
            catch (Exception ex)
            {
                Log($"[FATAL] {ex.Message}", "error");
            }
        }

        private void PlayVideo(string url)
        {
            try
            {
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
                
                // Починаємо з нульовою прозорістю для плавної появи
                VideoPlayer.Opacity = 0.0;
                _currentOpacity = 0.0;
                
                lock (_vlcLock)
                {
                    _vlcPlayer.Play(media);
                }
                
                // Fade-in запуститься автоматично в події VlcPlayer_Playing
            }
            catch (Exception ex)
            {
                Log($"[VLC ERROR] {ex.Message}", "error");
                _isVideoLoaded = false;
            }
        }

        private void UpdateTimingDisplay(TimeSpan audio, TimeSpan video, bool isPlaying, bool vlcUpdated = false)
        {
            // ВИПРАВЛЕННЯ: Перевірка на від'ємні значення
            if (audio < TimeSpan.Zero) audio = TimeSpan.Zero;
            if (video < TimeSpan.Zero) video = TimeSpan.Zero;

            TxtAudioTime.Text = audio.ToString(@"mm\:ss\.f");
            TxtVideoTime.Text = video.ToString(@"mm\:ss\.f");

            // ПОКРАЩЕННЯ: Індикатор оновлення VLC
            if (vlcUpdated && _isVideoLoaded)
            {
                TxtVideoTime.Foreground = new SolidColorBrush(Color.FromRgb(80, 250, 123)); // Зелений
            }
            else if (_isVideoLoaded)
            {
                TxtVideoTime.Foreground = new SolidColorBrush(Color.FromRgb(139, 233, 253)); // Блакитний
            }
            else
            {
                TxtVideoTime.Foreground = Brushes.Gray;
            }

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
                // Використовуємо _displayDiff замість прямого розрахунку
                double diff = _displayDiff;
                
                // ВИПРАВЛЕННЯ: Перевірка на NaN
                if (double.IsNaN(diff))
                {
                    TxtDiff.Text = "---";
                    TxtDiff.Foreground = Brushes.Gray;
                    return;
                }

                TxtDiff.Text = $"{diff:+0.00;-0.00;0.00}s";

                double absDiff = Math.Abs(diff);
                
                // Адаптивне забарвлення з плавними переходами
                if (absDiff < 0.3)
                    TxtDiff.Foreground = new SolidColorBrush(Color.FromRgb(80, 250, 123));   // Зелений - ідеально
                else if (absDiff < 1.0)
                    TxtDiff.Foreground = new SolidColorBrush(Color.FromRgb(255, 184, 108));  // Помаранчевий - норм
                else if (absDiff < 2.0)
                    TxtDiff.Foreground = new SolidColorBrush(Color.FromRgb(255, 121, 198));  // Рожевий - погано
                else
                    TxtDiff.Foreground = new SolidColorBrush(Color.FromRgb(255, 85, 85));    // Червоний - критично
            }
            else
            {
                TxtDiff.Text = "LOOP";
                TxtDiff.Foreground = Brushes.Cyan;
            }
        }

        private void Log(string message, string type)
        {
            try
            {
                string timeStr = DateTime.Now.ToString("HH:mm:ss");

                Task.Run(() =>
                {
                    try { File.AppendAllText(_logPath, $"[{timeStr}] {message}{Environment.NewLine}"); } catch { }
                });

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
                        if (LogOutput.Document.Blocks.Count > 100)
                            LogOutput.Document.Blocks.Remove(LogOutput.Document.Blocks.FirstBlock);

                        LogOutput.ScrollToEnd();
                    }
                    catch { }
                });
            }
            catch { }
        }
    }
}