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
        private struct CachedVideo
        {
            public string VideoId;
            public string StreamUrl;
            public DateTime ExpiryTime;
        }

        // "Назва пісні" -> Дані про відео
        private readonly Dictionary<string, CachedVideo> _smartCache = new();

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
                // 1. Отримуємо активну сесію
                var session = GetRelevantSession(_mediaManager);

                if (session == null)
                {
                    // Якщо музики немає взагалі - ставимо паузу
                    if (_vlcPlayer.IsPlaying)
                    {
                        Log("[MONITOR] No active session -> Pausing Video", "warning");
                        _vlcPlayer.Pause();
                    }
                    return;
                }

                // 2. Перевіряємо статус (Грає чи Пауза?)
                var playbackInfo = session.GetPlaybackInfo();
                bool isMusicPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                // Логуємо зміну статусу (тільки якщо він змінився) — як у ВЕРХНЬОМУ коді
                if (isMusicPlaying != _wasPlaying)
                {
                    Log($"[STATUS CHANGE] Music is now: {(isMusicPlaying ? "PLAYING" : "PAUSED/STOPPED")}", isMusicPlaying ? "success" : "warning");
                    _wasPlaying = isMusicPlaying;
                }

                // --- ЛОГІКА ПАУЗИ (ВЗЯТО З ВЕРХНЬОГО КОДУ) ---
                if (!isMusicPlaying)
                {
                    // Якщо музика стоїть, а відео грає -> ПАУЗА
                    if (_vlcPlayer.IsPlaying)
                    {
                        _vlcPlayer.Pause();
                        Log("[MONITOR] Sync Pause executed", "info");
                    }

                    // Оновлюємо таймер на екрані (статичний час)
                    var timelinePaused = session.GetTimelineProperties();
                    TimeSpan pausedTime = timelinePaused?.Position ?? TimeSpan.Zero;
                    UpdateTimingDisplay(pausedTime, TimeSpan.FromMilliseconds(_vlcPlayer.Time), false);

                    // Скидаємо екстраполятор VLC (з нижнього), щоб при відновленні не було стрибка
                    _lastVlcRawTime = -1;
                    return; // Виходимо
                }

                // --- ЛОГІКА ВІДТВОРЕННЯ (ВЗЯТО З ВЕРХНЬОГО КОДУ) ---

                // 1. Якщо відео завантажено, але стоїть на паузі -> PLAY
                if (_isVideoLoaded && !_vlcPlayer.IsPlaying)
                {
                    Log("[MONITOR] Resuming Video...", "info");
                    _vlcPlayer.Play();
                    _lastVlcRawTime = -1; // Скидання для чистого старту
                }

                // 2. Отримання інфо про трек
                var info = await session.TryGetMediaPropertiesAsync();
                string artist = info?.Artist ?? "";
                string title = info?.Title ?? "";
                string currentSong = (!string.IsNullOrEmpty(artist) || !string.IsNullOrEmpty(title))
                                     ? $"{artist} - {title}" : "Unknown";

                // Якщо пісня змінилася
                if (currentSong != "Unknown" && currentSong != _lastSong)
                {
                    _lastSong = currentSong;

                    // Скидаємо прапорці
                    _isVideoLoaded = false;
                    _isLoopingVideo = false;
                    _lastVlcRawTime = -1; // Скидаємо математику відео

                    _searchCts?.Cancel();
                    _searchCts = new CancellationTokenSource();

                    Log($"♪ Track Changed: {currentSong}", "info");

                    // Запускаємо пошук нового відео
                    _ = ProcessSongAsync(currentSong, _searchCts.Token);
                }

                // =========================================================
                // 4. МАТЕМАТИКА ЧАСУ (ТОЧНА ЕКСТРАПОЛЯЦІЯ З НИЖНЬОГО КОДУ)
                // =========================================================

                // А) Розрахунок плавного часу АУДІО
                var timeline = session.GetTimelineProperties();
                TimeSpan liveAudioTime = TimeSpan.Zero;

                if (timeline != null)
                {
                    liveAudioTime = timeline.Position + (DateTimeOffset.Now - timeline.LastUpdatedTime);
                }

                // Б) Розрахунок плавного часу ВІДЕО
                long currentRawVlcTime = _vlcPlayer.Time;
                TimeSpan smoothVlcTime;

                if (currentRawVlcTime != _lastVlcRawTime && currentRawVlcTime != -1)
                {
                    _lastVlcRawTime = currentRawVlcTime;
                    _lastVlcUpdateTime = DateTime.Now;
                    smoothVlcTime = TimeSpan.FromMilliseconds(currentRawVlcTime);
                }
                else
                {
                    if (_lastVlcRawTime != -1 && _vlcPlayer.IsPlaying)
                    {
                        double msPassed = (DateTime.Now - _lastVlcUpdateTime).TotalMilliseconds;
                        double extrapolatedMs = _lastVlcRawTime + (msPassed * _vlcPlayer.Rate);
                        smoothVlcTime = TimeSpan.FromMilliseconds(extrapolatedMs);
                    }
                    else
                    {
                        smoothVlcTime = TimeSpan.FromMilliseconds(currentRawVlcTime);
                    }
                }

                // 5. Оновлення UI
                UpdateTimingDisplay(liveAudioTime, smoothVlcTime, true);

                // Синхронізуємо швидкість (якщо це не короткий луп)
                // ТУТ БЕЗ ТАЙМЕРА 1.5с — ПРАЦЮЄ КОЖЕН ТІК (ДЛЯ ТОЧНОСТІ 0.01)
                if (_isVideoLoaded && !_isLoopingVideo)
                {
                    SyncVideoState(liveAudioTime, smoothVlcTime);
                }
            }
            catch (Exception ex)
            {
                // Ігноруємо помилки
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

                // --- ЕТАП 1: РОЗУМНИЙ КЕШ (Миттєвий старт для повторів) ---
                if (_smartCache.TryGetValue(query, out var cachedData))
                {
                    // Посилання YouTube живуть близько 6 годин. Перевіряємо, чи не протухло воно.
                    if (DateTime.Now < cachedData.ExpiryTime)
                    {
                        Log($"[CACHE] Fast load: {cachedData.VideoId}", "success");
                        PlayVideo(cachedData.StreamUrl);
                        return; // Виходимо, бо ми вже запустили відео
                    }
                    else
                    {
                        // Посилання застаріло, але ID відео ми пам'ятаємо
                        videoId = cachedData.VideoId;
                    }
                }

                // --- ЕТАП 2: ПОШУК ID (Якщо його немає) ---
                if (string.IsNullOrEmpty(videoId))
                {
                    var searchResults = await _youtube.Search.GetVideosAsync(query, token).CollectAsync(1);
                    if (searchResults.Count > 0)
                    {
                        videoId = searchResults[0].Id.Value;
                    }
                }

                if (token.IsCancellationRequested || string.IsNullOrEmpty(videoId)) return;

                // --- ЕТАП 3: ОТРИМАННЯ ПОСИЛАННЯ ---
                try
                {
                    var manifest = await _youtube.Videos.Streams.GetManifestAsync(videoId, token);

                    // ОПТИМІЗАЦІЯ: Беремо потік, який швидше вантажиться (MaxHeight 1080 - це компроміс).
                    // Muxed (відео+аудіо в одному) часто стартує швидше, ніж VideoOnly, бо VLC не треба зводити два потоки,
                    // але ми використовуємо VideoOnly, щоб економити трафік (бо звук нам не треба).
                    var videoStream = manifest.GetVideoOnlyStreams()
                        .Where(s => s.Container == Container.Mp4 && s.VideoQuality.MaxHeight <= 1080)
                        .GetWithHighestVideoQuality();

                    if (videoStream != null)
                    {
                        streamUrl = videoStream.Url;

                        // Зберігаємо в кеш на 5 годин (з запасом, бо живуть 6)
                        _smartCache[query] = new CachedVideo
                        {
                            VideoId = videoId,
                            StreamUrl = streamUrl,
                            ExpiryTime = DateTime.Now.AddHours(5)
                        };

                        // Чистка кешу, якщо занадто великий
                        if (_smartCache.Count > 100) _smartCache.Remove(_smartCache.Keys.First());
                    }
                }
                catch (Exception ex)
                {
                    Log($"[ERROR] Manifest: {ex.Message}", "error");
                    _smartCache.Remove(query);
                    return;
                }

                if (token.IsCancellationRequested) return;

                // --- ЕТАП 4: ЗАПУСК ---
                if (!string.IsNullOrEmpty(streamUrl))
                {
                    Log("[DEBUG] Starting playback...", "info");
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
                _isVideoLoaded = false;
                _isLoopingVideo = false;

                var media = new LibVLCSharp.Shared.Media(_libVLC, new Uri(url));

                // --- ОПТИМІЗАЦІЯ ШВИДКОДІЇ VLC ---

                // 1. Зменшуємо буфер мережі з 1500 до 300-500 мс.
                // Це миттєво зменшує час "чорного екрану" на 1 секунду.
                // Якщо інтернет поганий і будуть ривки, можна підняти до 800.
                media.AddOption(":network-caching=300");

                // 2. Вимикаємо синхронізацію годинника для потоку. 
                // Це дозволяє відео стартувати, не чекаючи ідеального таймінгу (ми все одно рівняємо його самі).
                media.AddOption(":clock-jitter=0");
                media.AddOption(":clock-synchro=0");

                // 3. Інші налаштування
                media.AddOption(":avcodec-hw=any"); // Апаратне прискорення
                media.AddOption(":input-repeat=65535");
                media.AddOption(":no-audio"); // Точно не качаємо аудіо

                Log("[VLC] Fast Play triggered...", "info");
                _vlcPlayer.Play(media);
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