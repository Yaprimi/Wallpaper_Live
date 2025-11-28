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
using System.Collections.Concurrent; // Для безпечнішої роботи з колекціями, якщо потрібно
using System.Threading; // Для CancellationToken

namespace WallpaperMusicPlayer
{
    public partial class MainWindow : Window
    {
        private GlobalSystemMediaTransportControlsSessionManager? _mediaManager;
        private readonly YoutubeClient _youtube = new();
        private readonly string _logPath;

        // Стан
        private string _lastSong = "";
        private CancellationTokenSource? _searchCts; // Для скасування попередніх пошуків

        // Кеш (використовуємо звичайний словник, але з розумним очищенням)
        private readonly Dictionary<string, string> _videoCache = new();

        // Таймери та час
        private readonly DispatcherTimer _monitorTimer = new();
        private DateTime _lastSyncTime = DateTime.MinValue;
        private bool _isVideoLoaded = false;
        private bool _isLoopingVideo = false; // Прапорець: це просто "гіфка" чи повноцінний кліп?

        public MainWindow()
        {
            InitializeComponent();
            _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_log.txt");
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            VideoPlayer.Volume = 0; // Шпалери мають бути без звуку
            VideoPlayer.LoadedBehavior = MediaState.Manual;
            VideoPlayer.UnloadedBehavior = MediaState.Manual;

            // Підписка на події плеєра для надійності
            VideoPlayer.MediaOpened += VideoPlayer_MediaOpened;
            VideoPlayer.MediaEnded += VideoPlayer_MediaEnded;
            VideoPlayer.MediaFailed += VideoPlayer_MediaFailed;

            InitializeAsync();
        }

        private async void InitializeAsync()
        {
            Log("Init Media API...", "info");
            try
            {
                _mediaManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

                _monitorTimer.Interval = TimeSpan.FromMilliseconds(100); // 10 FPS для UI
                _monitorTimer.Tick += MonitorLoop;
                _monitorTimer.Start();

                Log("Waiting for music...", "success");
            }
            catch (Exception ex)
            {
                Log($"INIT ERROR: {ex.Message}", "error");
            }
        }

        private async void MonitorLoop(object? sender, EventArgs e)
        {
            if (_mediaManager == null) return;

            try
            {
                // 1. ВИПРАВЛЕННЯ: Шукаємо сесію, яка ГРАЄ, серед усіх програм
                // Windows часто "залипає" на паузі Chrome, навіть коли Spotify грає.
                var session = GetRelevantSession(_mediaManager);

                // Якщо навіть після пошуку нічого не знайшли — значить все тихо
                if (session == null)
                {
                    if (VideoPlayer.CanPause && _isVideoLoaded)
                    {
                        VideoPlayer.Pause();
                        UpdateTimingDisplay(TimeSpan.Zero, VideoPlayer.Position, false);
                    }
                    return;
                }

                // 2. Отримуємо статус
                var playbackInfo = session.GetPlaybackInfo();
                bool isPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                // 3. Логіка Паузи
                if (!isPlaying)
                {
                    if (VideoPlayer.CanPause) VideoPlayer.Pause();

                    // Отримуємо позицію для UI (заморожений час)
                    var timelinePaused = session.GetTimelineProperties();
                    TimeSpan pausedTime = timelinePaused?.Position ?? TimeSpan.Zero;

                    UpdateTimingDisplay(pausedTime, VideoPlayer.Position, false);
                    return;
                }

                // --- Якщо дійшли сюди, значить музика ГРАЄ ---

                // 4. Метадані (пісня)
                var info = await session.TryGetMediaPropertiesAsync();
                // Додаткова перевірка на null, бо інколи info приходить пустим на старті
                string artist = info?.Artist ?? "";
                string title = info?.Title ?? "";
                string currentSong = (!string.IsNullOrEmpty(artist) || !string.IsNullOrEmpty(title))
                                     ? $"{artist} - {title}"
                                     : "Unknown";

                // Ігноруємо "Unknown", якщо це просто тимчасовий глюк API
                if (currentSong != "Unknown" && currentSong != _lastSong)
                {
                    _lastSong = currentSong;
                    _isVideoLoaded = false;

                    _searchCts?.Cancel();
                    _searchCts = new CancellationTokenSource();

                    Log($"♪ New: {currentSong} (App: {session.SourceAppUserModelId})", "info");
                    _ = ProcessSongAsync(currentSong, _searchCts.Token);
                }

                // 5. Розрахунок часу
                var timeline = session.GetTimelineProperties();
                TimeSpan liveAudioTime = TimeSpan.Zero;

                if (timeline != null)
                {
                    liveAudioTime = timeline.Position + (DateTimeOffset.Now - timeline.LastUpdatedTime);
                }

                // 6. Керування відеоплеєром
                if (_isVideoLoaded && VideoPlayer.Source != null)
                {
                    VideoPlayer.Play();
                }

                UpdateTimingDisplay(liveAudioTime, VideoPlayer.Position, true);

                // 7. Синхронізація
                if (_isVideoLoaded && !_isLoopingVideo)
                {
                    if ((DateTime.Now - _lastSyncTime).TotalSeconds > 1.5)
                    {
                        _lastSyncTime = DateTime.Now;
                        SyncVideoState(liveAudioTime, VideoPlayer.Position);
                    }
                }
            }
            catch { }
        }

        // === ДОДАЙТЕ ЦЮ ФУНКЦІЮ У КЛАС MainWindow ===
        private GlobalSystemMediaTransportControlsSession? GetRelevantSession(GlobalSystemMediaTransportControlsSessionManager manager)
        {
            // КРОК 1: Спочатку перевіряємо "Головну" сесію (те, що Windows вважає активним)
            var current = manager.GetCurrentSession();
            if (current != null)
            {
                var info = current.GetPlaybackInfo();
                if (info != null && info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    return current; // Ура, Windows не збрехав, це дійсно грає
                }
            }

            // КРОК 2: Якщо головна сесія мовчить (або null), перевіряємо ВСІ запущені програми
            var allSessions = manager.GetSessions();
            foreach (var session in allSessions)
            {
                try
                {
                    var info = session.GetPlaybackInfo();
                    if (info != null && info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        return session; // Знайшли Spotify (або щось інше), що реально грає!
                    }
                }
                catch { continue; }
            }

            // КРОК 3: Якщо ніхто не грає, повертаємо "Головну" (щоб показувати паузу) або null
            return current ?? allSessions.FirstOrDefault();
        }

        private void SyncVideoState(TimeSpan targetAudio, TimeSpan currentVideo)
        {
            if (_isLoopingVideo) return;

            try
            {
                double diff = (targetAudio - currentVideo).TotalSeconds;
                double absDiff = Math.Abs(diff);

                // 1. ЖОРСТКИЙ РЕСИНК (Тільки якщо розбіжність величезна > 5 сек)
                // Це трапляється, якщо перемотали пісню в Spotify
                if (absDiff > 5.0)
                {
                    // Перевірка меж відео
                    if (VideoPlayer.NaturalDuration.HasTimeSpan && targetAudio < VideoPlayer.NaturalDuration.TimeSpan)
                    {
                        Log($"Hard Resync: {currentVideo:mm\\:ss} -> {targetAudio:mm\\:ss} (Diff: {diff:F1}s)", "warning");
                        VideoPlayer.Position = targetAudio;
                        VideoPlayer.SpeedRatio = 1.0; // Скидаємо швидкість
                    }
                    return;
                }

                // 2. ПЛАВНИЙ РЕСИНК (SpeedRatio)
                // Якщо розбіжність від 0.1с до 5с — змінюємо швидкість відтворення
                if (diff > 0.1) // Відео відстає -> Пришвидшуємо
                {
                    // Якщо відстає сильно (>1с), то 1.25x, якщо трохи - 1.05x
                    double newSpeed = diff > 1.0 ? 1.25 : 1.05;

                    if (VideoPlayer.SpeedRatio != newSpeed)
                    {
                        VideoPlayer.SpeedRatio = newSpeed;
                        // Log($"Speed UP: {newSpeed}x (Lag: {diff:F2}s)", "info"); // Можна розкоментувати для дебагу
                    }
                }
                else if (diff < -0.1) // Відео спішить -> Сповільнюємо
                {
                    double newSpeed = diff < -1.0 ? 0.75 : 0.95;

                    if (VideoPlayer.SpeedRatio != newSpeed)
                    {
                        VideoPlayer.SpeedRatio = newSpeed;
                        // Log($"Speed DOWN: {newSpeed}x (Ahead: {Math.Abs(diff):F2}s)", "info");
                    }
                }
                else // Все ідеально синхронізовано (< 0.1с різниці)
                {
                    if (VideoPlayer.SpeedRatio != 1.0)
                    {
                        VideoPlayer.SpeedRatio = 1.0;
                        // Log("Sync OK. Speed Normal.", "success");
                    }
                }
            }
            catch { }
        }

        private async Task ProcessSongAsync(string query, CancellationToken token)
        {
            try
            {
                string? streamUrl = null;

                // 1. Кеш
                if (_videoCache.TryGetValue(query, out var cachedUrl))
                {
                    Log("Cache Hit", "success");
                    streamUrl = cachedUrl;
                }
                else
                {
                    // 2. Пошук
                    // Використовуємо токен скасування, щоб не вантажити старі запити
                    var searchResults = await _youtube.Search.GetVideosAsync(query, token).CollectAsync(3);
                    if (token.IsCancellationRequested) return;

                    foreach (var video in searchResults)
                    {
                        try
                        {
                            var manifest = await _youtube.Videos.Streams.GetManifestAsync(video.Id, token);
                            var videoStreams = manifest.GetVideoOnlyStreams();

                            // Пріоритет: MP4 <= 1080p
                            var streamInfo = videoStreams
                                .Where(s => s.Container == Container.Mp4 && s.VideoQuality.MaxHeight <= 1080)
                                .GetWithHighestVideoQuality();

                            // Fallback: Будь-що <= 1080p
                            if (streamInfo == null)
                            {
                                streamInfo = videoStreams
                                    .Where(s => s.VideoQuality.MaxHeight <= 1080)
                                    .GetWithHighestVideoQuality();
                            }

                            if (streamInfo != null)
                            {
                                streamUrl = streamInfo.Url;
                                Log($"Selected: {streamInfo.VideoQuality.Label} ({streamInfo.Container})", "info");

                                // Розумне очищення кешу (видаляємо найстаріший/перший, а не все)
                                if (_videoCache.Count > 50)
                                {
                                    var firstKey = _videoCache.Keys.FirstOrDefault();
                                    if (firstKey != null) _videoCache.Remove(firstKey);
                                }

                                _videoCache[query] = streamUrl;
                                break;
                            }
                        }
                        catch { continue; }
                    }
                }

                if (token.IsCancellationRequested) return;

                if (!string.IsNullOrEmpty(streamUrl))
                {
                    // Важливо: Оновлюємо UI в головному потоці
                    Dispatcher.Invoke(() => PlayVideo(streamUrl));
                }
                else
                {
                    Log("Video not found", "error");
                }
            }
            catch (OperationCanceledException)
            {
                // Нормальна ситуація при швидкому перемиканні
            }
            catch (Exception ex)
            {
                Log($"Search Error: {ex.Message}", "error");
            }
        }

        private void PlayVideo(string url)
        {
            try
            {
                _isVideoLoaded = false; // Скидаємо прапорець
                _isLoopingVideo = false; // Скидаємо тип відео

                VideoPlayer.Source = new Uri(url);
                VideoPlayer.Play();
                // Не ставимо _isVideoLoaded = true тут! Чекаємо події MediaOpened
            }
            catch (Exception ex)
            {
                Log($"Play Error: {ex.Message}", "error");
            }
        }

        private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            _isVideoLoaded = true;

            // --- ЛОГІКА ТИПУ ВІДЕО ---
            if (VideoPlayer.NaturalDuration.HasTimeSpan)
            {
                var duration = VideoPlayer.NaturalDuration.TimeSpan;
                if (duration.TotalSeconds < 60)
                {
                    _isLoopingVideo = true;
                    Log($"Type: Loop Wallpaper ({duration:mm\\:ss})", "info");
                    return; // Для лупів не синхронізуємо час
                }
                else
                {
                    _isLoopingVideo = false;
                    Log($"Type: Music Video ({duration:mm\\:ss})", "info");
                }
            }

            // --- ГОЛОВНЕ ВИПРАВЛЕННЯ: Стрибок на старті ---
            // Отримуємо поточний час музики ПРЯМО ЗАРАЗ
            if (_mediaManager != null)
            {
                var session = GetRelevantSession(_mediaManager);
                if (session != null)
                {
                    var timeline = session.GetTimelineProperties();
                    if (timeline != null)
                    {
                        // Рахуємо актуальний час: (час знімка + скільки пройшло з того моменту)
                        TimeSpan currentAudioTime = timeline.Position + (DateTimeOffset.Now - timeline.LastUpdatedTime);

                        // Якщо відео відстає більше ніж на 1 сек (а це завжди так при старті), стрибаємо
                        if (currentAudioTime.TotalSeconds > 1)
                        {
                            Log($"QuickStart: Jump to {currentAudioTime:mm\\:ss}", "success");
                            VideoPlayer.Position = currentAudioTime;
                        }
                    }
                }
            }
        }

        private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            // Циклічне відтворення
            VideoPlayer.Position = TimeSpan.Zero;
            VideoPlayer.Play();
        }

        private void VideoPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            Log($"Media Failed: {e.ErrorException?.Message}", "error");
            _isVideoLoaded = false;
        }

        private void UpdateTimingDisplay(TimeSpan audio, TimeSpan video, bool isPlaying)
        {
            // Оновлюємо текст навіть на паузі, просто сірим кольором
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

            // Рахуємо різницю тільки якщо відео не "луп"
            if (!_isLoopingVideo)
            {
                double diff = (audio - video).TotalSeconds;
                TxtDiff.Text = $"{diff:+0.00;-0.00;0.00}s";

                double absDiff = Math.Abs(diff);
                if (absDiff < 1.0)
                    TxtDiff.Foreground = new SolidColorBrush(Color.FromRgb(80, 250, 123)); // Green
                else if (absDiff < 3.0)
                    TxtDiff.Foreground = new SolidColorBrush(Color.FromRgb(255, 184, 108)); // Yellow
                else
                    TxtDiff.Foreground = new SolidColorBrush(Color.FromRgb(255, 85, 85)); // Red
            }
            else
            {
                TxtDiff.Text = "LOOP";
                TxtDiff.Foreground = Brushes.Cyan;
            }
        }

        private void Log(string message, string type)
        {
            // Логування без змін, але важливо перехоплювати помилки запису файлу
            try
            {
                string timeStr = DateTime.Now.ToString("HH:mm:ss");

                // Асинхронний запис у файл, щоб не блокувати UI (fire and forget)
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