using LibVLCSharp.Shared;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices; // Для роботи з пам'яттю
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging; // Для WriteableBitmap
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

        private readonly string _idleVideoPath;
        private readonly string _loadingVideoPath;

        private DateTime _lastSeekTime = DateTime.MinValue;
        private long _lastVlcRawTime = -1;
        private DateTime _lastVlcUpdateTime = DateTime.MinValue;

        private readonly Queue<double> _diffHistory = new Queue<double>(5);
        private double _displayDiff = 0;
        private double _syncDiff = 0;

        private LibVLC _libVLC;
        private LibVLCSharp.Shared.MediaPlayer _vlcPlayer;

        // --- ДЛЯ РЕНДЕРИНГУ В ОДНОМУ ВІКНІ ---
        private WriteableBitmap _videoBitmap;
        private IntPtr _videoBuffer;
        // Налаштування роздільної здатності відео (Full HD)
        // Можна зменшити до 1280x720, якщо слабкий ПК
        private const uint VideoWidth = 1920;
        private const uint VideoHeight = 1080;
        private const uint VideoPitch = VideoWidth * 4; // 4 байти на піксель (RGBA)
        // -------------------------------------

        private string _lastSong = "";
        private CancellationTokenSource? _searchCts;

        private struct CachedVideo
        {
            public string VideoId;
            public string StreamUrl;
            public DateTime ExpiryTime;
        }

        private readonly Dictionary<string, CachedVideo> _smartCache = new();

        private readonly DispatcherTimer _monitorTimer = new();
        private bool _isVideoLoaded = false;
        private bool _isLoopingVideo = false;
        private bool _wasPlaying = false;
        private bool _isLocalLoopPlaying = false;

        private readonly object _vlcLock = new object();
        private readonly object _cacheLock = new object();

        private int _tickCounter = 0;

        public MainWindow()
        {
            InitializeComponent();
            _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_log.txt");

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _idleVideoPath = Path.Combine(baseDir, "idle.mp4");
            _loadingVideoPath = Path.Combine(baseDir, "loading.mp4");

            Core.Initialize();
            _libVLC = new LibVLC();
            _vlcPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);

            // Налаштовуємо "прямий" рендеринг у картинку
            SetupVideoRendering();
        }

        private void SetupVideoRendering()
        {
            // Створюємо бітмап у пам'яті
            _videoBitmap = new WriteableBitmap((int)VideoWidth, (int)VideoHeight, 96, 96, PixelFormats.Pbgra32, null);
            VideoImage.Source = _videoBitmap;
            _videoBuffer = _videoBitmap.BackBuffer;

            // Кажемо VLC: "Видавай відео в форматі RV32 (RGBA), такого розміру"
            _vlcPlayer.SetVideoFormat("RV32", VideoWidth, VideoHeight, VideoPitch);

            // Підписуємось на колбеки (VLC буде викликати ці методи для кожного кадру)
            _vlcPlayer.SetVideoCallbacks(VideoLock, VideoUnlock, VideoDisplay);
        }

        // VLC просить буфер, куди писати
        private IntPtr VideoLock(IntPtr opaque, IntPtr planes)
        {
            // Ми не блокуємо бітмап тут (це робиться в UI потоці), просто даємо адресу
            Marshal.WriteIntPtr(planes, _videoBuffer);
            return IntPtr.Zero;
        }

        // VLC закінчив писати
        private void VideoUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
        {
            // Нічого не робимо
        }

        // VLC каже: "Кадр готовий, показуй!"
        private void VideoDisplay(IntPtr opaque, IntPtr picture)
        {
            // Оскільки ми не в UI потоці, треба попросити UI оновитись
            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        _videoBitmap.Lock();
                        // Позначаємо всю область як змінену, щоб WPF перемалював її
                        _videoBitmap.AddDirtyRect(new Int32Rect(0, 0, (int)VideoWidth, (int)VideoHeight));
                        _videoBitmap.Unlock();
                    }
                    catch { }
                }, DispatcherPriority.Render);
            }
            catch { }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _vlcPlayer.Volume = 0;
            _vlcPlayer.LengthChanged += VlcPlayer_LengthChanged;
            _vlcPlayer.EncounteredError += VlcPlayer_EncounteredError;
            _vlcPlayer.Playing += VlcPlayer_Playing;
            _vlcPlayer.EndReached += VlcPlayer_EndReached;

            InitializeAsync();
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

        private void PlayLocalLoop(string path, string reason)
        {
            if (!File.Exists(path))
            {
                Log($"[LOCAL] File not found: {Path.GetFileName(path)}", "error");
                return;
            }

            try
            {
                Log($"[LOCAL] Playing {Path.GetFileName(path)} ({reason})", "info");
                _isLocalLoopPlaying = true;
                _isLoopingVideo = true;
                _isVideoLoaded = false;
                _lastVlcRawTime = -1;
                // Fade effect removed for simplicity in Bitmap mode (can be added via Opacity on Image)
                VideoImage.Opacity = 1.0;

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
                _searchCts?.Cancel();
                // Важливо скинути колбеки перед виходом, щоб уникнути крашу
                _vlcPlayer.SetVideoCallbacks(null, null, null);
                lock (_vlcLock) { _vlcPlayer.Stop(); _vlcPlayer.Dispose(); }
                _libVLC.Dispose();
            }
            catch { }
            base.OnClosed(e);
        }

        private async void MonitorLoop(object? sender, EventArgs e)
        {
            UpdateWindowDiagnostics();

            if (_mediaManager == null) return;

            try
            {
                var session = GetRelevantSession(_mediaManager);

                if (session == null)
                {
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

                if (!isMusicPlaying)
                {
                    if (_vlcPlayer.IsPlaying)
                    {
                        lock (_vlcLock) { _vlcPlayer.Pause(); }
                        Log("[MONITOR] Video Paused", "info");
                    }
                    var timelinePaused = session.GetTimelineProperties();
                    TimeSpan pausedTime = timelinePaused?.Position ?? TimeSpan.Zero;
                    UpdateTimingDisplay(pausedTime, TimeSpan.Zero, false, false);
                    return;
                }

                if (!_vlcPlayer.IsPlaying && _isVideoLoaded)
                {
                    lock (_vlcLock) { _vlcPlayer.Play(); }
                }

                var info = await session.TryGetMediaPropertiesAsync();
                string artist = info?.Artist ?? "";
                string title = info?.Title ?? "";
                string currentSong = (!string.IsNullOrEmpty(artist) || !string.IsNullOrEmpty(title))
                                     ? $"{artist} - {title}" : "Unknown";

                if (currentSong != "Unknown" && currentSong != _lastSong)
                {
                    _lastSong = currentSong;
                    PlayLocalLoop(_loadingVideoPath, "Track Switch");

                    _isVideoLoaded = false;
                    _isLoopingVideo = false;
                    _lastVlcRawTime = -1;
                    _displayDiff = 0;
                    _syncDiff = 0;
                    _diffHistory.Clear();

                    _searchCts?.Cancel();
                    _searchCts = new CancellationTokenSource();

                    Log($"♪ Next Track: {currentSong}", "info");
                    _ = ProcessSongAsync(currentSong, _searchCts.Token);
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

        private void UpdateWindowDiagnostics()
        {
            try
            {
                _tickCounter++;

                // 1. Інформація про процеси (оновлюємо рідше, щоб не вантажити CPU)
                if (_tickCounter % 10 == 0)
                {
                    var currentProcess = Process.GetCurrentProcess();
                    string pName = currentProcess.ProcessName;
                    var processes = Process.GetProcessesByName(pName);

                    TxtProcessCount.Text = processes.Length.ToString();
                    // Якщо процесів > 1, підсвічуємо червоним
                    TxtProcessCount.Foreground = processes.Length > 1 ? Brushes.Red : Brushes.White;

                    TxtProcessId.Text = currentProcess.Id.ToString();
                }

                // 2. Інформація про вікна
                var windows = Application.Current.Windows;
                TxtWindowCount.Text = windows.Count.ToString();

                // В режимі "Single Window" ми очікуємо рівно 1 вікно.
                if (windows.Count == 1)
                    TxtWindowCount.Foreground = Brushes.Green; // Ідеально
                else
                    TxtWindowCount.Foreground = Brushes.Yellow; // Підозріло (але може бути VS)

                // 3. Детальний стан головного вікна
                TxtVisibility.Text = this.Visibility.ToString();
                if (this.Visibility != Visibility.Visible) TxtVisibility.Foreground = Brushes.Red;
                else TxtVisibility.Foreground = Brushes.White;

                TxtWindowState.Text = this.WindowState.ToString();
                TxtTopmost.Text = this.Topmost.ToString();

                // Реальний розмір вікна
                TxtRenderSize.Text = $"{this.ActualWidth:F0}x{this.ActualHeight:F0}";

                // Системний Handle
                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                TxtHandle.Text = "0x" + helper.Handle.ToString("X8");
            }
            catch { }
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
                _isLocalLoopPlaying = false;
                _isVideoLoaded = false;
                _isLoopingVideo = false;
                _lastVlcRawTime = -1;
                // Fade effect not implemented in Bitmap mode to keep it simple
                VideoImage.Opacity = 1.0;

                var media = new LibVLCSharp.Shared.Media(_libVLC, new Uri(url));
                media.AddOption(":network-caching=300");
                media.AddOption(":clock-jitter=0");
                media.AddOption(":clock-synchro=0");
                media.AddOption(":avcodec-hw=any");
                media.AddOption(":input-repeat=65535");
                media.AddOption(":no-audio");

                Log("[VLC] Fast Play triggered...", "info");

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