using FuzzySharp;
using SkiaSharp;
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
        /// <summary>
        /// Ваги та пороги для системи скорингу відео.
        /// Винесено в окремий клас для легкого тюнінгу без ризику зламати логіку.
        /// </summary>
        private static class ScoreWeights
        {
            // ── Базовий fuzzy-match ───────────────────────────────────────────────
            public const float FuzzyMultiplier = 1.5f;

            // ── Підтверджуючі бонуси ─────────────────────────────────────────────
            public const int AuthorMatchBonus = 30;
            public const int VevoBonus = 25;
            public const int OfficialVideoBonus = 50;
            public const int MusicVideoBonus = 30;
            public const int TrailerBonus = 20;
            public const int OfficialMVBonus = 50;

            // ── Бонус за позицію в результатах пошуку ────────────────────────────
            // Позиція 0 → +15, 1 → +10, 2 → +5, 3+ → 0
            public const int PositionBonusStep = 5;
            public const int PositionBonusMaxIdx = 3;   // індекси 0..2 отримують бонус

            // ── Збіг тривалості ──────────────────────────────────────────────────
            public const int DurationExactBonus = 25;  // < 5 сек різниці
            public const int DurationCloseBonus = 15;  // < 15 сек
            public const int DurationOkBonus = 5;   // < 30 сек
            public const int DurationFarPenalty = -20; // > 120 сек

            // ── Штрафи за небажаний контент ──────────────────────────────────────
            public const int KaraokePenalty = -60;
            public const int OneHourPenalty = -60;
            public const int ReactionPenalty = -50;
            public const int FanMadePenalty = -50;
            public const int CoverPenalty = -40;
            public const int LyricsPenalty = -100;
            public const int DancePracticePenalty = -170;
            public const int TopicChannelPenalty = -70;
            public const int WrongAlphabetPenalty = -50;
            public const int NoVideoMarkerPenalty = -50;

            // ── Контекстні штрафи (застосовуються у ProcessSongAsync) ────────────
            public const int ContextOfficialAudioPenalty = -40;
            public const int ContextTopicPenalty = -40;
            public const int ContextLyricVideoPenalty = -30;
        }

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

        // ── діагностика зеленого екрану ───────────────────────────────────────
        private volatile bool _waitingForFirstVlcFrame = false; // true після FadeOut
        private volatile int _vlcFrameCount = 0;       // скільки кадрів VLC відрендерив усього
        private volatile int _greenFrameCount = 0;     // зелених кадрів підряд (для відновлення)
        private volatile bool _greenScreenActive = false; // true поки екран залишається зеленим

        // Кількість пікселів для вибірки (сітка NxN по всьому кадру)
        private const int GreenSampleGridSize = 4; // 4×4 = 16 пікселів
        // Зелений = G домінує і G > порогу
        private const byte GreenDominanceThreshold = 100; // мінімальне значення G
        private const byte GreenDominanceMargin = 60;  // наскільки G має бути більшим за R і B
        // Скільки зелених кадрів підряд → вважаємо застряглим і намагаємось відновитись
        private const int GreenRecoveryFrameThreshold = 30; // ~0.5s при 60fps

        // ── відкладений FadeOut — ховаємо overlay тільки після підтвердження
        // що кілька кадрів поспіль нормальні (hw-декодер стабілізувався) ────────
        // Проблема: VLC декодує Frame #1 software, потім ~400-800ms ініціалізує
        // hw-декодер і Frame #2+ виходять зеленими. FadeOut саме в цей момент
        // відкриває зелений екран користувачу.
        // Рішення: тримаємо overlay поверх відео поки не отримаємо
        // StableFramesRequired нормальних кадрів підряд після VLC Status: Playing.
        private volatile bool _pendingFadeOut = false;  // FadeOut відкладено
        private volatile int _stableFrameCount = 0;     // нормальних кадрів підряд
        private const int StableFramesRequired = 8;     // ~250ms при 30fps потоку

        private const int MAX_YOUTUBE_REQUESTS_PER_MINUTE = 10;
        private const int YOUTUBE_CACHE_HOURS = 5;
        private const int MAX_CACHE_SIZE = 100;
        private const int NETWORK_TIMEOUT_SECONDS = 10;

        // ── SkiaSharp animator (замінює idle.mp4 / loading.mp4) ──────────────
        private WallpaperAnimator? _animator;

        // ── поточний Media об'єкт — зберігається щоб VLC міг ним користуватись
        // після повернення з Play(). Звільняється перед наступним відтворенням.
        private LibVLCSharp.Shared.Media? _currentMedia;

        // ── поточний URL відео — для перезапуску без hw-декодера при зеленому екрані
        private string _currentVideoUrl = "";
        // true = перший запуск з hw=any вже дав зелений, другий запуск буде з hw=none
        private bool _hwDecodeAttempted = false;

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

                // Ініціалізація SkiaSharp аніматора
                // AnimatorImage — окремий Image поверх VideoImage, VLC не торкається
                _animator = new WallpaperAnimator(AnimatorImage, Dispatcher, Log);
                Log("[ANIMATOR] SkiaSharp overlay animator initialized", "success");
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
            // VideoLock викликається з нативного VLC-потоку — звертатись до будь-яких
            // WPF-об'єктів (включно з WriteableBitmap.BackBuffer) тут заборонено.
            // _videoBuffer кешується один раз у SetupVideoRendering на UI-потоці,
            // де BackBuffer читати безпечно. Це єдиний коректний підхід.
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

                int frameNum = System.Threading.Interlocked.Increment(ref _vlcFrameCount);

                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (_videoBitmap != null && !_isDisposing)
                        {
                            _videoBitmap.Lock();

                            // ── діагностика зеленого екрану ──────────────────
                            // Моніторинг активний:
                            //  - перші 30 кадрів після кожного PlayVideo (детальний лог)
                            //  - постійно поки _greenScreenActive == true (відновлення)
                            {
                                IntPtr buf = _videoBitmap.BackBuffer;
                                if (buf != IntPtr.Zero)
                                {
                                    bool frameIsGreen = IsFrameGreen(buf);
                                    bool frameIsEmpty = IsFrameEmpty(buf);

                                    if (_waitingForFirstVlcFrame && frameNum <= 30)
                                    {
                                        int cx = (int)(VideoWidth / 2) * 4;
                                        int cy = (int)(VideoHeight / 2) * (int)(VideoWidth * 4);
                                        byte pb = Marshal.ReadByte(buf, cy + cx + 0);
                                        byte pg = Marshal.ReadByte(buf, cy + cx + 1);
                                        byte pr = Marshal.ReadByte(buf, cy + cx + 2);
                                        byte pa = Marshal.ReadByte(buf, cy + cx + 3);

                                        if (frameIsGreen)
                                        {
                                            int gc = System.Threading.Interlocked.Increment(ref _greenFrameCount);
                                            _greenScreenActive = true;
                                            System.Threading.Interlocked.Exchange(ref _stableFrameCount, 0);
                                            Log($"[GREEN DEBUG] ⚠ Frame #{frameNum}: GREEN SCREEN!" +
                                                $" center BGRA=({pb},{pg},{pr},{pa})" +
                                                $" | green frames: {gc}" +
                                                $" | overlay={_animator?.OverlayOpacity:F2}", "error");
                                        }
                                        else if (frameIsEmpty)
                                        {
                                            Log($"[GREEN DEBUG] Frame #{frameNum}: EMPTY" +
                                                $" | VLC не записав кадр ще", "warning");
                                        }
                                        else
                                        {
                                            System.Threading.Interlocked.Exchange(ref _greenFrameCount, 0);
                                            _greenScreenActive = false;
                                            int stable = System.Threading.Interlocked.Increment(ref _stableFrameCount);
                                            if (frameNum == 1)
                                                Log($"[GREEN DEBUG] ✓ Frame #1 нормальний" +
                                                    $" BGRA=({pb},{pg},{pr},{pa}) — sw decode OK", "success");
                                        }

                                        if (frameNum == 30)
                                        {
                                            _waitingForFirstVlcFrame = false;
                                            Log($"[GREEN DEBUG] Діагностика завершена (30 кадрів)" +
                                                $" | зелених: {_greenFrameCount}, стабільних: {_stableFrameCount}" +
                                                $" | green screen: {_greenScreenActive}",
                                                _greenScreenActive ? "error" : "success");
                                        }
                                    }
                                    else if (_greenScreenActive)
                                    {
                                        // ── постійний моніторинг якщо вже є зелений екран ──
                                        if (frameIsGreen)
                                        {
                                            int gc = System.Threading.Interlocked.Increment(ref _greenFrameCount);

                                            // Лог кожні 60 кадрів щоб не спамити
                                            if (gc % 60 == 1)
                                                Log($"[GREEN DEBUG] ⚠ Зелений екран продовжується" +
                                                    $" | кадрів підряд: {gc}" +
                                                    $" | всього VLC кадрів: {frameNum}", "error");

                                            // Спроба відновлення через перезапуск VLC
                                            if (gc == GreenRecoveryFrameThreshold)
                                            {
                                                Log($"[GREEN DEBUG] 🔄 {GreenRecoveryFrameThreshold} зелених кадрів —" +
                                                    $" спроба відновлення через seek до поточної позиції", "error");
                                                TryRecoverFromGreenScreen();
                                            }
                                        }
                                        else if (!frameIsEmpty)
                                        {
                                            // Зелений екран зник сам
                                            System.Threading.Interlocked.Exchange(ref _greenFrameCount, 0);
                                            _greenScreenActive = false;
                                            Log($"[GREEN DEBUG] ✓ Зелений екран зник після {frameNum} кадрів", "success");
                                        }
                                    }
                                }
                            }

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

        /// <summary>
        /// Перевіряє кадр на зеленість через сітку GreenSampleGridSize×GreenSampleGridSize пікселів.
        /// Кадр вважається зеленим якщо більшість пікселів мають G >> R і G >> B.
        /// Викликається з UI-потоку поки bitmap залочений.
        /// </summary>
        private bool IsFrameGreen(IntPtr buf)
        {
            int greenPixels = 0;
            int totalPixels = GreenSampleGridSize * GreenSampleGridSize;
            int stride = (int)(VideoWidth * 4);

            for (int row = 0; row < GreenSampleGridSize; row++)
            {
                int py = (int)(VideoHeight * (row + 1) / (GreenSampleGridSize + 1));
                for (int col = 0; col < GreenSampleGridSize; col++)
                {
                    int px = (int)(VideoWidth * (col + 1) / (GreenSampleGridSize + 1));
                    int offset = py * stride + px * 4;
                    byte b = Marshal.ReadByte(buf, offset + 0);
                    byte g = Marshal.ReadByte(buf, offset + 1);
                    byte r = Marshal.ReadByte(buf, offset + 2);

                    if (g > GreenDominanceThreshold
                        && g - r > GreenDominanceMargin
                        && g - b > GreenDominanceMargin)
                    {
                        greenPixels++;
                    }
                }
            }

            // Зелений екран якщо > 50% пікселів зелені
            return greenPixels > totalPixels / 2;
        }

        /// <summary>
        /// Перевіряє чи буфер порожній (VLC ще не записав жодного кадру).
        /// Читає лише один піксель у центрі — достатньо для швидкої перевірки.
        /// </summary>
        private bool IsFrameEmpty(IntPtr buf)
        {
            int stride = (int)(VideoWidth * 4);
            int offset = (int)(VideoHeight / 2) * stride + (int)(VideoWidth / 2) * 4;
            byte b = Marshal.ReadByte(buf, offset + 0);
            byte g = Marshal.ReadByte(buf, offset + 1);
            byte r = Marshal.ReadByte(buf, offset + 2);
            byte a = Marshal.ReadByte(buf, offset + 3);
            return r == 0 && g == 0 && b == 0 && a == 0;
        }

        /// <summary>
        /// Fallback відновлення від зеленого екрану.
        /// З avcodec-hw=none це не повинно траплятись в нормальних умовах.
        /// Можливі залишкові причини: пошкоджений потік, проблема з мережею.
        /// </summary>
        private void TryRecoverFromGreenScreen()
        {
            if (_vlcPlayer == null || _isDisposing || _isLoopingVideo) return;
            if (string.IsNullOrEmpty(_currentVideoUrl)) return;

            try
            {
                if (!_hwDecodeAttempted)
                {
                    _hwDecodeAttempted = true;
                    Log("[GREEN DEBUG] ⚠ Зелений екран при hw=none — можлива проблема з потоком або мережею", "error");
                    Log("[GREEN DEBUG] 🔄 Спроба: перезапуск з поточної позиції", "warning");

                    long seekPosMs = 0;
                    lock (_vlcLock) { if (_vlcPlayer != null) seekPosMs = _vlcPlayer.Time; }

                    _greenFrameCount = 0;
                    _greenScreenActive = false;
                    _waitingForFirstVlcFrame = true;
                    _vlcFrameCount = 0;
                    _pendingFadeOut = false;
                    _stableFrameCount = 0;

                    var prevMedia = _currentMedia;
                    _currentMedia = new LibVLCSharp.Shared.Media(_libVLC, new Uri(_currentVideoUrl));
                    _currentMedia.AddOption(":network-caching=1000");
                    _currentMedia.AddOption(":clock-jitter=0");
                    _currentMedia.AddOption(":clock-synchro=0");
                    _currentMedia.AddOption(":avcodec-hw=none");
                    _currentMedia.AddOption(":input-repeat=65535");
                    _currentMedia.AddOption(":no-audio");
                    prevMedia?.Dispose();

                    lock (_vlcLock)
                    {
                        if (_vlcPlayer != null && !_isDisposing)
                        {
                            _vlcPlayer.Play(_currentMedia);
                            if (seekPosMs > 1000)
                            {
                                _vlcPlayer.Time = seekPosMs;
                                _lastVlcRawTime = seekPosMs;
                                _lastSeekTime = DateTime.Now;
                            }
                        }
                    }
                    Log($"[GREEN DEBUG] 🔄 Перезапуск з позиції {seekPosMs}ms", "info");
                }
                else
                {
                    Log("[GREEN DEBUG] ⚠ Повторний зелений екран — можливо URL протух або пошкоджений потік", "error");
                    lock (_vlcLock)
                    {
                        if (_vlcPlayer == null || _isDisposing) return;
                        long pos = _vlcPlayer.Time;
                        _vlcPlayer.Time = pos;
                        _lastSeekTime = DateTime.Now;
                        Log($"[GREEN DEBUG] 🔄 Hard seek: {pos}ms", "warning");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[GREEN DEBUG] Відновлення failed: {ex.Message}", "error");
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

            // Запускаємо SkiaSharp аніматор замість idle.mp4
            if (_animator != null)
            {
                _animator.Start();   // один раз на весь час роботи програми
                _animator.ShowNoPlayer();
                _isLocalLoopPlaying = true;
                Log("[ANIMATOR] Started — showing NoPlayer screen", "success");
            }
            else if (File.Exists(_idleVideoPath))
            {
                // Fallback на mp4 якщо аніматор не ініціалізувався
                PlayLocalLoop(_idleVideoPath, "Startup");
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
                            // ВИПРАВЛЕНО: Play() без аргументу не гарантує повторне відтворення
                            // після Stop() — передаємо явно поточний Media об'єкт.
                            _vlcPlayer.Stop();
                            if (_currentMedia != null)
                                _vlcPlayer.Play(_currentMedia);
                            else
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

                // FadeOut тут — VLC підтвердив що реально грає.
                // Працює і для першого старту і для resume після паузи.
                _animator?.FadeOut();
                Log("[ANIMATOR] Fading out overlay — VLC confirmed playing", "info");

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

                if (!_isLocalLoopPlaying)
                {
                    // Показуємо аніматор замість loading.mp4
                    if (_animator != null)
                    {
                        _animator.ShowLoading(_lastSong);
                        _isLocalLoopPlaying = true;
                        Log("[ANIMATOR] Showing Loading (error recovery)", "warning");
                    }
                    else if (File.Exists(_loadingVideoPath))
                    {
                        PlayLocalLoop(_loadingVideoPath, "Error Recovery");
                    }
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
                        // ВИПРАВЛЕНО: не використовуємо 'using' — VLC читає Media асинхронно
                        // після повернення Play(). Об'єкт звільняється VLC самостійно.
                        var media = new LibVLCSharp.Shared.Media(_libVLC, path);
                        media.AddOption(":input-repeat=65535");
                        media.AddOption(":no-audio");
                        _vlcPlayer.Play(media);
                        media.Dispose(); // безпечно: LibVLC внутрішньо тримає власний ref-count
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

                // Звільняємо поточний Media об'єкт
                _currentMedia?.Dispose();
                _currentMedia = null;

                // Очищення аніматора
                _animator?.Dispose();
                _animator = null;

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

                // session == null означає що немає ні Playing ні Paused сесії
                if (session == null)
                {
                    // ВИПРАВЛЕНО: скидаємо _wasPlaying та _lastSong —
                    // інакше при наступній появі сесії переходи стану спрацюють неправильно.
                    _wasPlaying = false;
                    _lastSong = "";

                    if (!_isLocalLoopPlaying && File.Exists(_idleVideoPath))
                    {
                        PlayLocalLoop(_idleVideoPath, "No Session");
                    }
                    else if (!_isLocalLoopPlaying && _animator != null)
                    {
                        _animator.ShowNoPlayer();
                        _isLocalLoopPlaying = true;
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
                // ВИПРАВЛЕНО: GetPlaybackInfo() може повернути null якщо сесія зникла
                // між GetRelevantSession() і цим викликом — без перевірки NullReferenceException.
                if (playbackInfo == null)
                {
                    Log("[MONITOR] PlaybackInfo is null — session may have disappeared", "warning");
                    return;
                }
                bool isMusicPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                // ShowIdle викликається рівно один раз — при переході playing→paused
                // _isLocalLoopPlaying НЕ виставляється — щоб не ламати твій оригінальний
                // блок нижче який перевіряє його для sync display
                if (isMusicPlaying != _wasPlaying)
                {
                    Log($"[STATUS] Music: {(isMusicPlaying ? "PLAYING" : "PAUSED")}", isMusicPlaying ? "success" : "warning");
                    _wasPlaying = isMusicPlaying;

                    if (!isMusicPlaying && _animator != null)
                    {
                        _animator.ShowIdle(_lastSong, session);
                        Log("[ANIMATOR] Showing Idle (paused)", "info");
                    }
                    else if (isMusicPlaying && _animator != null)
                    {
                        // FadeOut після resume робиться в VlcPlayer_Playing —
                        // коли VLC підтвердив що реально відновив відтворення.
                        Log("[ANIMATOR] Resume detected — waiting for VLC Playing event", "info");
                    }
                }

                // ── твій оригінальний блок паузи без жодних змін ─────────────
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

                if (_vlcPlayer != null && !_vlcPlayer.IsPlaying && !_isDisposing)
                {
                    lock (_vlcLock)
                    {
                        if (_vlcPlayer != null && !_isDisposing)
                            _vlcPlayer.Play();
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

                    // Показуємо Loading аніматор замість loading.mp4
                    if (_animator != null)
                    {
                        // Передаємо session — аніматор сам отримає обкладинку з SMTC
                        _animator.ShowLoading(currentSong, session);
                        _isLocalLoopPlaying = true;
                        Log($"[ANIMATOR] Showing Loading for: {currentSong}", "info");
                    }
                    else if (File.Exists(_loadingVideoPath))
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

        private GlobalSystemMediaTransportControlsSession? GetRelevantSession(
            GlobalSystemMediaTransportControlsSessionManager manager)
        {
            try
            {
                var allSessions = manager.GetSessions();

                GlobalSystemMediaTransportControlsSession? pausedSession = null;

                foreach (var session in allSessions)
                {
                    try
                    {
                        var info = session.GetPlaybackInfo();
                        if (info == null) continue;

                        // Пріоритет — активна сесія
                        if (info.PlaybackStatus ==
                            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                            return session;

                        // Запам'ятовуємо паузовану як запасну
                        if (info.PlaybackStatus ==
                            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                            pausedSession = session;
                    }
                    catch (Exception ex)
                    {
                        Log($"[SESSION ERROR] {ex.Message}", "error");
                    }
                }

                // Повертаємо паузовану якщо активної немає
                return pausedSession;
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
        /// Нормалізує варіанти написання featuring у рядку.
        /// "feat.", "ft.", "featuring", "feat " → єдиний токен "feat"
        /// щоб fuzzy-match коректно порівнював назви з колаборацій.
        /// Наприклад: "Calvin Harris feat. Rihanna" ↔ "Rihanna ft. Calvin Harris"
        /// </summary>
        private static string NormalizeFeaturingTags(string input)
        {
            // Порядок важливий: довші варіанти першими щоб уникнути часткових замін
            return input
                .Replace("featuring", "feat")
                .Replace("feat.", "feat")
                .Replace(" ft.", " feat")
                .Replace(" ft ", " feat ");
        }

        /// <summary>
        /// Скорує результат пошуку YouTube.
        /// Основа: FuzzySharp TokenSetRatio — релевантність незалежно від порядку слів.
        /// Підтвердження: автор каналу, тривалість, формат відео, позиція в результатах.
        /// Штрафи: явний сміт + контекстні (official audio / Topic якщо є кращий кандидат).
        ///
        /// ПОКРАЩЕННЯ відносно попередньої версії:
        ///   1. Ваги винесено в ScoreWeights — легко тюнити без ризику зламати логіку.
        ///   2. Нормалізація feat./ft./featuring перед fuzzy-match.
        ///   3. Бонус за позицію в результатах пошуку (YouTube сортує за релевантністю).
        ///   4. Виправлено баг: VEVO більше не отримує штраф NoVideoMarker.
        ///   5. Виправлено мертвий код: "OFFICIAL M/ V" замінено на реальні варіанти.
        ///   6. Логування розбивки балів передається назовні через scoreBreakdown.
        /// </summary>
        private int ScoreVideo(
            VideoSearchResult video,
            string artist,
            string title,
            TimeSpan trackDuration,
            int searchResultIndex = 0,
            List<string>? scoreBreakdown = null)
        {
            int score = 0;

            // ── Нормалізація рядків (один раз, не в кожній умові) ────────────
            var ytTitle = NormalizeFeaturingTags(video.Title.ToLower());
            var ytAuthor = video.Author.ToLower();
            var artLow = NormalizeFeaturingTags(artist.ToLower());
            var titleLow = NormalizeFeaturingTags(title.ToLower());

            void Track(string label, int delta)
            {
                score += delta;
                scoreBreakdown?.Add($"{label}: {delta:+#;-#;0}");
            }

            // ================================================================
            // ОСНОВНИЙ КРИТЕРІЙ — Fuzzy matching (0–150)
            // TokenSetRatio ігнорує порядок слів і зайві слова в назві —
            // тому "Veilr Rampant Official Video" і "Rampant Veilr" дадуть ~100.
            // ================================================================

            string searchQuery = $"{artLow} {titleLow}";
            int fuzzyScore = Fuzz.TokenSetRatio(searchQuery, ytTitle);
            Track("Fuzzy", (int)(fuzzyScore * ScoreWeights.FuzzyMultiplier));

            // ================================================================
            // БОНУС ЗА ПОЗИЦІЮ В РЕЗУЛЬТАТАХ ПОШУКУ
            // YouTube сортує результати за власною релевантністю —
            // перша позиція статистично є найточнішим збігом.
            // Позиція 0 → +15, 1 → +10, 2 → +5, 3+ → 0
            // ================================================================

            int positionBonus = Math.Max(0,
                (ScoreWeights.PositionBonusMaxIdx - searchResultIndex) * ScoreWeights.PositionBonusStep);
            if (positionBonus > 0)
                Track($"Position#{searchResultIndex}", positionBonus);

            // ================================================================
            // ПІДТВЕРДЖУЮЧІ СИГНАЛИ
            // ================================================================

            if (ytAuthor.Contains(artLow))
                Track("AuthorMatch", ScoreWeights.AuthorMatchBonus);

            // ВИПРАВЛЕНО: VEVO перевіряємо окремо від isBadContent —
            // VEVO-канал за визначенням не може бути karaoke/reaction,
            // тому не повинен отримувати штраф NoVideoMarker через isBadContent.
            bool isVevo = ytAuthor.Contains("vevo");
            if (isVevo)
                Track("VEVO", ScoreWeights.VevoBonus);

            if (ytTitle.Contains("official video") ||
                ytTitle.Contains("official music video"))
                Track("OfficialVideo", ScoreWeights.OfficialVideoBonus);

            bool isBadContent = ytTitle.Contains("fan made")
                             || ytTitle.Contains("fan-made")
                             || ytTitle.Contains("karaoke")
                             || ytTitle.Contains("reaction")
                             || ytTitle.Contains("lyrics")
                             || (ytTitle.Contains("cover") && !titleLow.Contains("cover"));

            if (!isBadContent && ytTitle.Contains("music video") &&
                !ytTitle.Contains("official music video"))
                Track("MusicVideo", ScoreWeights.MusicVideoBonus);

            if (ytTitle.Contains("trailer") && ytTitle.Contains(titleLow))
                Track("Trailer", ScoreWeights.TrailerBonus);

            // ВИПРАВЛЕНО: перевіряємо ytTitle (не ytAuthor) на "official mv" —
            // це типова назва відео, не каналу. Прибрано мертвий рядок "OFFICIAL M/ V".
            if (ytTitle.Contains("official mv") ||
                (ytTitle.Contains(" mv") && ytTitle.Contains(titleLow)))
                Track("OfficialMV", ScoreWeights.OfficialMVBonus);

            // ================================================================
            // ЗБІГ ТРИВАЛОСТІ
            // ================================================================

            if (trackDuration > TimeSpan.Zero && video.Duration > TimeSpan.Zero)
            {
                double diffSec = Math.Abs((video.Duration - trackDuration).TotalSeconds);

                if (diffSec < 5) Track("Duration<5s", ScoreWeights.DurationExactBonus);
                else if (diffSec < 15) Track("Duration<15s", ScoreWeights.DurationCloseBonus);
                else if (diffSec < 30) Track("Duration<30s", ScoreWeights.DurationOkBonus);
                else if (diffSec > 120) Track("Duration>120s", ScoreWeights.DurationFarPenalty);
            }

            // ================================================================
            // ШТРАФИ — явний небажаний контент
            // ================================================================

            if (ytTitle.Contains("karaoke"))
                Track("Karaoke", ScoreWeights.KaraokePenalty);
            if (ytTitle.Contains("1 hour"))
                Track("1Hour", ScoreWeights.OneHourPenalty);
            if (ytTitle.Contains("reaction"))
                Track("Reaction", ScoreWeights.ReactionPenalty);
            if (ytTitle.Contains("fan made") || ytTitle.Contains("fan-made"))
                Track("FanMade", ScoreWeights.FanMadePenalty);
            if (ytTitle.Contains("cover") && !titleLow.Contains("cover"))
                Track("Cover", ScoreWeights.CoverPenalty);
            if (ytTitle.Contains("lyrics"))
                Track("Lyrics", ScoreWeights.LyricsPenalty);
            if (ytTitle.Contains("dance practice"))
                Track("DancePractice", ScoreWeights.DancePracticePenalty);

            if (ytAuthor.EndsWith("- topic"))
                Track("TopicChannel", ScoreWeights.TopicChannelPenalty);

            // Штраф за різний алфавіт у залишку назви відео.
            // Використовуємо \b-аналог через пробіли щоб не вирізати частини слів.
            string remainder = ytTitle
                .Replace(" " + titleLow + " ", " ")
                .Replace(" " + artLow + " ", " ")
                .Trim();

            if (!AreSameAlphabetGroup(titleLow, remainder))
                Track("WrongAlphabet", ScoreWeights.WrongAlphabetPenalty);

            // ВИПРАВЛЕНО: VEVO завжди вважається відео-маркером незалежно від isBadContent —
            // раніше через operator precedence vevo міг не рятувати від штрафу.
            bool hasVideoMarker = isVevo || (!isBadContent && (
                                   ytTitle.Contains("music video")
                                || ytTitle.Contains("official video")
                                || ytTitle.Contains("official music video")
                                || ytTitle.Contains("trailer")
                                || ytTitle.Contains("clip")));

            if (!hasVideoMarker)
                Track("NoVideoMarker", ScoreWeights.NoVideoMarkerPenalty);

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
                    if (_animator != null)
                    {
                        await Dispatcher.InvokeAsync(() => { _animator.ShowLoading(query); _isLocalLoopPlaying = true; });
                    }
                    else if (File.Exists(_loadingVideoPath))
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
                        if (_animator != null)
                        {
                            await Dispatcher.InvokeAsync(() => { _animator.ShowLoading(query); _isLocalLoopPlaying = true; });
                        }
                        else if (File.Exists(_loadingVideoPath))
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

                            // Скоруємо кожен результат.
                            // Передаємо індекс для бонусу за позицію та список breakdown для логів.
                            var scored = searchResults.Select((v, idx) =>
                            {
                                var breakdown = new List<string>();
                                int s = ScoreVideo(v, artist, title, trackDuration,
                                    searchResultIndex: idx,
                                    scoreBreakdown: breakdown);

                                // Контекстний штраф: якщо є кращий кандидат —
                                // official audio і Topic канали йдуть на другий план
                                if (hasPriorityCandidate)
                                {
                                    var ytTitleCtx = v.Title.ToLower();
                                    var ytAuthorCtx = v.Author.ToLower();

                                    if (ytTitleCtx.Contains("official audio"))
                                    {
                                        s += ScoreWeights.ContextOfficialAudioPenalty;
                                        breakdown.Add($"CtxOfficialAudio: {ScoreWeights.ContextOfficialAudioPenalty}");
                                    }
                                    if (ytAuthorCtx.EndsWith("- topic"))
                                    {
                                        s += ScoreWeights.ContextTopicPenalty;
                                        breakdown.Add($"CtxTopic: {ScoreWeights.ContextTopicPenalty}");
                                    }
                                    if (ytTitleCtx.Contains("official lyric video") ||
                                        ytTitleCtx.Contains("lyric video"))
                                    {
                                        s += ScoreWeights.ContextLyricVideoPenalty;
                                        breakdown.Add($"CtxLyricVideo: {ScoreWeights.ContextLyricVideoPenalty}");
                                    }
                                }

                                return new { Video = v, Score = s, Index = idx, Breakdown = breakdown };
                            })
                            .OrderByDescending(x => x.Score)
                            .ToList();

                            // ── ЛОГУВАННЯ РОЗБИВКИ БАЛІВ ─────────────────────
                            // Спочатку всі кандидати в оригінальному порядку пошуку (Index 0..N),
                            // потім окремо переможець з повною розбивкою балів.
                            var winner = scored[0];

                            foreach (var v in scored.OrderBy(x => x.Index))
                            {
                                bool isWinner = v.Index == winner.Index;
                                string star = isWinner ? " ★ WINNER" : "";
                                Log($"[SCORE] #{v.Index + 1}{star} score={v.Score:+#;-#;0} | '{v.Video.Title}' by {v.Video.Author} ({v.Video.Duration:mm\\:ss})", isWinner ? "success" : "info");
                            }

                            // Детальна розбивка переможця окремим рядком
                            var breakdownStr = string.Join(" | ", winner.Breakdown);
                            Log($"[SCORE] breakdown #{winner.Index + 1}: {breakdownStr}", "info");

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

                // ── Отримання stream URL з retry ─────────────────────────────
                // YouTube іноді навмисно сповільнює або блокує GetManifestAsync.
                // Повторюємо до 3 разів з затримкою що збільшується (2s, 4s).
                // videoId вже відомий — повторюємо тільки цей запит, не пошук.
                const int MaxStreamRetries = 3;
                const int RetryDelayBaseSeconds = 2;

                for (int attempt = 1; attempt <= MaxStreamRetries; attempt++)
                {
                    if (token.IsCancellationRequested) return;

                    try
                    {
                        using var manifestCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        manifestCts.CancelAfter(TimeSpan.FromSeconds(NETWORK_TIMEOUT_SECONDS));

                        if (attempt > 1)
                            Log($"[STREAM] Retry {attempt}/{MaxStreamRetries} for: {videoId}", "warning");
                        else
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
                            break; // успіх — виходимо з циклу retry
                        }
                        else
                        {
                            Log($"[ERROR] No suitable stream found for: {videoId}", "error");
                            return;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (token.IsCancellationRequested) return; // трек змінився — не ретраємо

                        Log($"[TIMEOUT] Manifest request timed out after {NETWORK_TIMEOUT_SECONDS}s (attempt {attempt}/{MaxStreamRetries})", "warning");

                        if (attempt == MaxStreamRetries)
                        {
                            Log($"[STREAM] All {MaxStreamRetries} attempts failed for: {videoId}", "error");
                            return;
                        }

                        int delaySeconds = RetryDelayBaseSeconds * attempt; // 2s, 4s
                        Log($"[STREAM] Waiting {delaySeconds}s before retry...", "info");
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token);
                    }
                    catch (Exception ex)
                    {
                        // ДЕТАЛЬНЕ ЛОГУВАННЯ ПОМИЛКИ MANIFEST
                        string errorDetails = $"{ex.GetType().Name}: {ex.Message}";

                        if (ex.InnerException != null)
                            errorDetails += $"\n    Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";

                        if (!string.IsNullOrEmpty(ex.StackTrace))
                        {
                            var stackLines = ex.StackTrace.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                            if (stackLines.Length > 0)
                                errorDetails += $"\n    At: {stackLines[0].Trim()}";
                        }

                        Log($"[MANIFEST ERROR] {errorDetails}", "error");
                        _smartCache.TryRemove(query, out _);

                        if (attempt == MaxStreamRetries)
                        {
                            _ = Task.Run(() => CheckYoutubeHealthAsync());
                            return;
                        }

                        int delaySeconds = RetryDelayBaseSeconds * attempt;
                        Log($"[STREAM] Waiting {delaySeconds}s before retry...", "info");
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token);
                    }
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
                // FadeOut перенесено в VlcPlayer_Playing — overlay зникає тільки
                // коли VLC підтвердив що реально грає, а не одразу при старті.
                _pendingFadeOut = false;
                _stableFrameCount = 0;
                _waitingForFirstVlcFrame = true;
                _vlcFrameCount = 0;
                _greenFrameCount = 0;
                _greenScreenActive = false;
                Log($"[GREEN DEBUG] Очікуємо перший VLC кадр (розмір: {VideoWidth}x{VideoHeight})" +
                    $" | hw-decode=none (SetVideoCallbacks несумісний з hw-decode)", "info");

                _isLocalLoopPlaying = false;
                _isVideoLoaded = false;
                _isLoopingVideo = false;
                _lastVlcRawTime = -1;
                VideoImage.Opacity = 1.0;

                // Зберігаємо URL для можливого перезапуску без hw-декодера
                _currentVideoUrl = url;
                _hwDecodeAttempted = false;

                // ВИПРАВЛЕНО: не використовуємо 'using' — VLC читає Media асинхронно.
                // Зберігаємо у полі щоб контролювати час звільнення.
                // Попередній Media звільняємо перед створенням нового.
                var prevMedia = _currentMedia;
                _currentMedia = new LibVLCSharp.Shared.Media(_libVLC, new Uri(url));
                _currentMedia.AddOption(":network-caching=1000");
                _currentMedia.AddOption(":clock-jitter=0");
                _currentMedia.AddOption(":clock-synchro=0");
                // ВИПРАВЛЕНО: avcodec-hw=any видалено — SetVideoCallbacks передає
                // власний буфер (WriteableBitmap.BackBuffer) через VideoLock callback.
                // hw-декодери (DXVA2, D3D11, NVDEC тощо) ігнорують цей вказівник і
                // пишуть у власний GPU-буфер — звідси BGRA=(0,135,0) зелений екран.
                // software decode завжди пише в наш буфер коректно.
                _currentMedia.AddOption(":avcodec-hw=none");
                _currentMedia.AddOption(":input-repeat=65535");
                _currentMedia.AddOption(":no-audio");
                prevMedia?.Dispose();

                Log("[VLC] Starting playback | hw-decode=none (software decode)", "info");

                lock (_vlcLock)
                {
                    if (_vlcPlayer != null && !_isDisposing)
                    {
                        _vlcPlayer.Play(_currentMedia);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[VLC ERROR] Playback failed: {ex.Message}", "error");
                _isVideoLoaded = false;

                if (_animator != null)
                {
                    _animator.ShowLoading(_lastSong);
                    _isLocalLoopPlaying = true;
                }
                else if (File.Exists(_loadingVideoPath))
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