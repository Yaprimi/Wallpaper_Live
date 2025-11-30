using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media; // Потрібен для Brushes, Color
using System.Windows.Threading;
using Windows.Media.Control;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Videos.Streams;
using System.Collections.Concurrent;
using System.Threading;
using LibVLCSharp.Shared; // Основна бібліотека VLC

namespace WallpaperMusicPlayer
{
    public partial class MainWindow : Window
    {
        private GlobalSystemMediaTransportControlsSessionManager? _mediaManager;
        private readonly YoutubeClient _youtube = new();
        private readonly string _logPath;
        private DateTime _lastSeekTime = DateTime.MinValue;
        private long _lastVlcRawTime = -1;     // Останній "сирий" час, який віддав VLC
        private DateTime _lastVlcUpdateTime = DateTime.MinValue; // Коли саме VLC оновив цей час
        private double _smoothedDiff = 0; // Відфільтрована різниця

        // VLC об'єкти
        private LibVLC _libVLC;

        // ВИПРАВЛЕННЯ ПОМИЛКИ: Явно вказуємо шлях до класу, щоб не плутати з WPF
        private LibVLCSharp.Shared.MediaPlayer _vlcPlayer;

        // Стан
        private string _lastSong = "";
        private CancellationTokenSource? _searchCts;

        // Кеш
        private readonly Dictionary<string, string> _videoCache = new();
        // Тепер тут зберігається: "Назва пісні" -> "VideoID" (а не URL)

        // Таймери та час
        private readonly DispatcherTimer _monitorTimer = new();
        private DateTime _lastSyncTime = DateTime.MinValue;
        private bool _isVideoLoaded = false;
        private bool _isLoopingVideo = false;

        public MainWindow()
        {
            InitializeComponent();
            _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_log.txt");

            // 1. Ініціалізація Core VLC
            Core.Initialize();

            // 2. Створення інстансу VLC та Плеєра
            _libVLC = new LibVLC();

            // ВИПРАВЛЕННЯ ПОМИЛКИ: Явне створення саме VLC плеєра
            _vlcPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);

            // Прив'язка плеєра до UI елементу (VideoView)
            VideoPlayer.MediaPlayer = _vlcPlayer;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _vlcPlayer.Volume = 0;

            // Підписка на події VLC
            _vlcPlayer.LengthChanged += VlcPlayer_LengthChanged;
            _vlcPlayer.EncounteredError += VlcPlayer_EncounteredError;
            _vlcPlayer.Playing += VlcPlayer_Playing;

            InitializeAsync();
        }

        // 1. Подія: Визначення довжини відео (Loop чи кліп?)
        private void VlcPlayer_LengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
        {
            // Використовуємо InvokeAsync, щоб не зупиняти потік VLC
            Dispatcher.InvokeAsync(() =>
            {
                _isVideoLoaded = true;
                long durationMs = e.Length;

                // Якщо відео коротше 1 хвилини - вважаємо це зацикленими шпалерами
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

        // 2. Подія: Початок відтворення
        private void VlcPlayer_Playing(object? sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _isVideoLoaded = true;
                Log("[VLC] Status: Playing", "success");

                // "Швидкий старт" - спроба одразу підігнати час, якщо пісня вже грає давно
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

                                // Якщо це не короткий луп і ми відстали більше ніж на 1 секунду -> стрибаємо
                                if (!_isLoopingVideo && currentAudioTime.TotalSeconds > 1)
                                {
                                    Log($"[SYNC] QuickJump to {currentAudioTime:mm\\:ss}", "warning");

                                    // Seek (Time = ...) безпечно робити тут, бо це просто setter
                                    _vlcPlayer.Time = (long)currentAudioTime.TotalMilliseconds;
                                }
                            }
                        }
                    }
                }
                catch { /* Ігноруємо помилки синхронізації при старті */ }
            });
        }

        // 3. Подія: Помилка VLC
        private void VlcPlayer_EncounteredError(object? sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                Log("[VLC] Critical Error occurred", "error");
                _isVideoLoaded = false;
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
            _vlcPlayer.Stop();
            _vlcPlayer.Dispose();
            _libVLC.Dispose();
            base.OnClosed(e);
        }

        private bool _wasPlaying = false;
        private async void MonitorLoop(object? sender, EventArgs e)
        {
            if (_mediaManager == null) return;

            try
            {
                // 1. Пошук активної сесії
                var session = GetRelevantSession(_mediaManager);

                // Якщо сесій немає взагалі
                if (session == null)
                {
                    if (_vlcPlayer.IsPlaying)
                    {
                        Log("[MONITOR] No active session. Pausing...", "warning");
                        _vlcPlayer.Pause();
                    }
                    return;
                }

                // 2. Перевірка статусу (Play/Pause)
                var playbackInfo = session.GetPlaybackInfo();
                bool isMusicPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                // Логування зміни статусу (для діагностики)
                if (isMusicPlaying != _wasPlaying)
                {
                    string appName = session.SourceAppUserModelId;
                    Log($"[STATUS] {appName} changed to: {(isMusicPlaying ? "PLAYING" : "PAUSED")}", isMusicPlaying ? "success" : "warning");
                    _wasPlaying = isMusicPlaying;
                }

                // --- ЛОГІКА ПАУЗИ ---
                if (!isMusicPlaying)
                {
                    if (_vlcPlayer.IsPlaying) _vlcPlayer.Pause();

                    // На паузі екстраполяцію не робимо, показуємо статичний час
                    var timelinePaused = session.GetTimelineProperties();
                    TimeSpan pausedAudioTime = timelinePaused?.Position ?? TimeSpan.Zero;
                    UpdateTimingDisplay(pausedAudioTime, TimeSpan.FromMilliseconds(_vlcPlayer.Time), false);

                    // Скидаємо екстраполятор VLC, щоб при відновленні не було стрибка
                    _lastVlcRawTime = -1;
                    return;
                }

                // --- ЛОГІКА ВІДТВОРЕННЯ ---

                // Якщо музика грає, а відео стоїть -> Play
                if (_isVideoLoaded && !_vlcPlayer.IsPlaying)
                {
                    Log("[MONITOR] Resuming Video", "info");
                    _vlcPlayer.Play();
                    _lastVlcRawTime = -1; // Скидання для чистого старту
                }

                // 3. Перевірка зміни треку
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
                    _lastVlcRawTime = -1; // Скидаємо математику відео

                    _searchCts?.Cancel();
                    _searchCts = new CancellationTokenSource();

                    Log($"♪ Track: {currentSong}", "info");
                    _ = ProcessSongAsync(currentSong, _searchCts.Token);
                }

                // =========================================================
                // 4. МАТЕМАТИКА ЧАСУ (ПОДВІЙНА ЕКСТРАПОЛЯЦІЯ)
                // =========================================================

                // А) Розрахунок плавного часу АУДІО (Windows)
                var timeline = session.GetTimelineProperties();
                TimeSpan liveAudioTime = TimeSpan.Zero;

                if (timeline != null)
                {
                    // Формула: Час на момент оновлення + Скільки пройшло з того моменту
                    liveAudioTime = timeline.Position + (DateTimeOffset.Now - timeline.LastUpdatedTime);
                }

                // Б) Розрахунок плавного часу ВІДЕО (VLC)
                long currentRawVlcTime = _vlcPlayer.Time; // "Сирий" час (оновлюється ривками)
                TimeSpan smoothVlcTime;

                if (currentRawVlcTime != _lastVlcRawTime && currentRawVlcTime != -1)
                {
                    // VLC оновив дані! Синхронізуємо базу.
                    _lastVlcRawTime = currentRawVlcTime;
                    _lastVlcUpdateTime = DateTime.Now;
                    smoothVlcTime = TimeSpan.FromMilliseconds(currentRawVlcTime);
                }
                else
                {
                    // VLC ще не оновив дані. Екстраполюємо самі.
                    if (_lastVlcRawTime != -1 && _vlcPlayer.IsPlaying)
                    {
                        double msPassed = (DateTime.Now - _lastVlcUpdateTime).TotalMilliseconds;
                        // Враховуємо поточну швидкість відтворення (Rate)!
                        // Якщо Rate = 1.05, час йде швидше.
                        double extrapolatedMs = _lastVlcRawTime + (msPassed * _vlcPlayer.Rate);
                        smoothVlcTime = TimeSpan.FromMilliseconds(extrapolatedMs);
                    }
                    else
                    {
                        smoothVlcTime = TimeSpan.FromMilliseconds(currentRawVlcTime);
                    }
                }

                // 5. Оновлення UI та Синхронізація
                UpdateTimingDisplay(liveAudioTime, smoothVlcTime, true);

                if (_isVideoLoaded && !_isLoopingVideo)
                {
                    // Передаємо два ПЛАВНИХ значення
                    SyncVideoState(liveAudioTime, smoothVlcTime);
                }
            }
            catch (Exception ex)
            {
                // Ігноруємо помилки доступу до COM-об'єктів при перемиканні треків
            }
        }

        private GlobalSystemMediaTransportControlsSession? GetRelevantSession(GlobalSystemMediaTransportControlsSessionManager manager)
        {
            // 1. Отримуємо список ВСІХ медіа-сесій
            var allSessions = manager.GetSessions();

            // 2. Проходимо по кожній і шукаємо ту, що ГРАЄ прямо зараз
            foreach (var session in allSessions)
            {
                try
                {
                    var info = session.GetPlaybackInfo();
                    if (info != null && info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        // Знайшли програму, яка грає! Повертаємо її.
                        return session;
                    }
                }
                catch
                {
                    // Ігноруємо помилки доступу до окремих сесій
                    continue;
                }
            }

            // 3. Якщо ніхто не грає, повертаємо системну "поточну" сесію (вона може бути на паузі)
            return manager.GetCurrentSession();
        }

        private void SyncVideoState(TimeSpan targetAudio, TimeSpan currentVideo)
        {
            if (_isLoopingVideo) return;

            // Cooldown після перемотки
            if ((DateTime.Now - _lastSeekTime).TotalSeconds < 2.0) return;

            try
            {
                // 1. Миттєва "шумна" різниця (те, що стрибає)
                double instantDiff = (targetAudio - currentVideo).TotalSeconds;

                // 2. ФІЛЬТРАЦІЯ (EMA)
                // Коефіцієнт 0.1 означає: реакція плавна, шум ігнорується.
                // Якщо хочеш швидшу реакцію - став 0.2, якщо плавніше - 0.05.
                _smoothedDiff = (_smoothedDiff * 0.9) + (instantDiff * 0.1);

                // Відображаємо ЗГЛАДЖЕНЕ значення. Тепер цифри не будуть скакати.
                TxtDiff.Text = $"{_smoothedDiff:+0.00;-0.00;0.00}s";

                // 3. ЛОГІКА СИНХРОНІЗАЦІЇ (Працюємо по згладженому значенню)
                double absDiff = Math.Abs(_smoothedDiff);

                // Hard Reset (якщо реально все погано > 3 сек)
                if (absDiff > 3.0)
                {
                    if (_vlcPlayer.Length > 0 && targetAudio.TotalMilliseconds < _vlcPlayer.Length)
                    {
                        _vlcPlayer.Time = (long)targetAudio.TotalMilliseconds;
                        _vlcPlayer.SetRate(1.0f);
                        _lastSeekTime = DateTime.Now;
                        _lastVlcRawTime = -1; // Скидання екстраполяції
                        _smoothedDiff = 0;    // Скидання фільтру
                    }
                    return;
                }

                // Мертва зона (тепер можна ставити дуже маленьку, бо шум відфільтровано!)
                if (absDiff < 0.05) // 50мс
                {
                    if (Math.Abs(_vlcPlayer.Rate - 1.0f) > 0.01f)
                    {
                        _vlcPlayer.SetRate(1.0f);
                    }
                    return;
                }

                // Плавна корекція швидкості
                float targetRate = 1.0f;
                if (_smoothedDiff > 0) targetRate = 1.05f; // Відео відстає
                else targetRate = 0.95f;                   // Відео спішить

                if (Math.Abs(_vlcPlayer.Rate - targetRate) > 0.01f)
                {
                    _vlcPlayer.SetRate(targetRate);
                }
            }
            catch { }
        }

        private async Task ProcessSongAsync(string query, CancellationToken token)
        {
            try
            {
                string videoId = "";
                string streamUrl = "";

                // --- ЕТАП 1: ПОШУК ID ---
                if (_videoCache.TryGetValue(query, out var cachedId))
                {
                    videoId = cachedId;
                    Log($"[DEBUG] Cache Hit: {videoId}", "success");
                }
                else
                {
                    var searchResults = await _youtube.Search.GetVideosAsync(query, token).CollectAsync(1);
                    if (searchResults.Count > 0)
                    {
                        videoId = searchResults[0].Id.Value;
                        if (_videoCache.Count > 100) _videoCache.Remove(_videoCache.Keys.First());
                        _videoCache[query] = videoId;
                    }
                }

                if (token.IsCancellationRequested) return;
                if (string.IsNullOrEmpty(videoId)) return;

                // --- ЕТАП 2: ОТРИМАННЯ ПОСИЛАННЯ ---
                try
                {
                    var manifest = await _youtube.Videos.Streams.GetManifestAsync(videoId, token);
                    var videoStream = manifest.GetVideoOnlyStreams()
                        .Where(s => s.Container == Container.Mp4 && s.VideoQuality.MaxHeight <= 1080)
                        .GetWithHighestVideoQuality()
                        ?? manifest.GetVideoOnlyStreams().GetWithHighestVideoQuality()
                        ?? manifest.GetMuxedStreams().GetWithHighestVideoQuality();

                    if (videoStream != null) streamUrl = videoStream.Url;
                }
                catch (Exception ex)
                {
                    Log($"[ERROR] Manifest: {ex.Message}", "error");
                    _videoCache.Remove(query);
                    return;
                }

                if (token.IsCancellationRequested) return;

                // --- ЕТАП 3: ЗАПУСК (ВИПРАВЛЕНО) ---
                if (!string.IsNullOrEmpty(streamUrl))
                {
                    // ВАЖЛИВО: Ми НЕ використовуємо Dispatcher.Invoke тут.
                    // PlayVideo запускається у фоновому потоці (Task), щоб не блокувати UI.
                    Log("[DEBUG] Calling PlayVideo from Background Thread...", "info");
                    PlayVideo(streamUrl);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"[FATAL] {ex.Message}", "error");
            }
        }

        private void PlayVideo(string url)
        {
            try
            {
                // Цей код виконується у фоновому потоці!
                _isVideoLoaded = false;
                _isLoopingVideo = false;

                // Створюємо медіа
                var media = new LibVLCSharp.Shared.Media(_libVLC, new Uri(url));

                // Опції для стабільності
                // :avcodec-hw=any дозволяє VLC самому вирішити, що безпечно. 
                // d3d11va може конфліктувати з WPF Surface.
                media.AddOption(":avcodec-hw=any");
                media.AddOption(":input-repeat=65535");
                media.AddOption(":no-audio");

                // Збільшуємо кеш мережі, щоб уникнути зависань на старті
                media.AddOption(":network-caching=1500");

                Log("[VLC] Media created. Playing...", "info");

                // Цей метод потокобезпечний у LibVLCSharp
                _vlcPlayer.Play(media);

                // НЕ викликаємо Dispose для media тут, нехай GC розбереться або VLC відпустить його сам
            }
            catch (Exception ex)
            {
                Log($"[VLC ERROR] {ex.Message}", "error");
            }
        }

        private void UpdateTimingDisplay(TimeSpan audio, TimeSpan video, bool isPlaying)
        {
            TxtAudioTime.Text = audio.ToString(@"mm\:ss\.f");
            TxtVideoTime.Text = video.ToString(@"mm\:ss\.f");

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
                double diff = (audio - video).TotalSeconds;
                TxtDiff.Text = $"{diff:+0.00;-0.00;0.00}s";

                double absDiff = Math.Abs(diff);
                if (absDiff < 1.0)
                    TxtDiff.Foreground = new SolidColorBrush(Color.FromRgb(80, 250, 123));
                else if (absDiff < 3.0)
                    TxtDiff.Foreground = new SolidColorBrush(Color.FromRgb(255, 184, 108));
                else
                    TxtDiff.Foreground = new SolidColorBrush(Color.FromRgb(255, 85, 85));
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
                });
            }
            catch { }
        }
    }
}