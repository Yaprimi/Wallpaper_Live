using SkiaSharp;
using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace WallpaperMusicPlayer
{
    public enum AnimatorState { NoPlayer, Idle, Loading }

    /// <summary>
    /// SkiaSharp overlay що живе ПОВЕРХ VLC у власному WriteableBitmap.
    /// VLC рендерить у VideoImage — цей клас рендерить у AnimatorImage (окремий елемент).
    /// Обкладинку отримує напряму з SMTC (GlobalSystemMediaTransportControls).
    /// </summary>
    public sealed class WallpaperAnimator : IDisposable
    {
        // ── власний overlay буфер (не має нічого спільного з VLC) ───────────
        private readonly WriteableBitmap _overlayBitmap;
        private readonly System.Windows.Controls.Image _overlayImage;
        private readonly Dispatcher _dispatcher;
        private readonly int _width = 1920;
        private readonly int _height = 1080;

        private readonly DispatcherTimer _timer;
        private readonly System.Diagnostics.Stopwatch _clock =
            System.Diagnostics.Stopwatch.StartNew();

        // ── стан ─────────────────────────────────────────────────────────────
        private AnimatorState _state = AnimatorState.NoPlayer;
        private string _trackName = "";
        private SKBitmap? _albumArt;
        private readonly object _artLock = new();

        // ВИПРАВЛЕННЯ #3, #4: кешовані SKImage та SKImageFilter —
        // більше не створюються щоразу в DrawBlurredBackground/DrawRoundedCover.
        // Перестворюються тільки при зміні _albumArt.
        private SKImage? _cachedArtImage;
        private SKImageFilter? _cachedBlurIdle;    // blur 28f для Idle
        private SKImageFilter? _cachedBlurLoading; // blur 32f для Loading

        // ── параметри плавання (Idle) ────────────────────────────────────────
        private const float BgFloatAmpX = 32f, BgFloatAmpY = 22f;
        private const float BgFloatSpeedX = 0.11f, BgFloatSpeedY = 0.07f;

        // ── fade контенту (зміна стану) ───────────────────────────────────────
        private float _contentAlpha = 1f;
        private bool _isFading = false;
        private float _fadeProgress = 0f;
        private const float FadeDuration = 0.45f;
        private AnimatorState _pendingState;
        private string _pendingTrack = "";
        private SKBitmap? _pendingArt;

        // Ключ актуальності арту — trackName запиту.
        // Idle="", Loading=trackName. Якщо змінився до завершення завантаження — арт відкидається.
        private string _artRequestKey = "";

        // ВИПРАВЛЕННЯ #5: CancellationTokenSource для скасування попереднього fetch при
        // швидкій зміні треків. Попередній fetch зупиняється одразу, не витрачаючи
        // ресурси на завантаження і декодування арту який вже не потрібен.
        private CancellationTokenSource _fetchCts = new();

        // ── fade overlay↔VLC (показати/сховати поверх кліпу) ─────────────────
        public float OverlayOpacity => _overlayOpacity; // для діагностики
        private float _overlayOpacity = 1f;
        private bool _overlayFadingIn = false;
        private bool _overlayFadingOut = false;
        private float _overlayFadeProgress = 0f;
        private const float OverlayFadeDur = 0.5f;

        // ВИПРАВЛЕННЯ #8: зберігаємо час попереднього тіку для реального delta-time
        private float _lastTickTime = 0f;

        private bool _disposed = false;

        // ── логування (той самий формат що і в MainWindow) ───────────────────
        private readonly Action<string, string> _log;

        // ════════════════════════════════════════════════════════════════════

        public WallpaperAnimator(
            System.Windows.Controls.Image overlayImage,
            Dispatcher dispatcher,
            Action<string, string> log)
        {
            _overlayImage = overlayImage ?? throw new ArgumentNullException(nameof(overlayImage));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _log = log ?? ((_, _) => { }); // якщо null — тихий no-op

            // Власний буфер — VLC про нього не знає
            _overlayBitmap = new WriteableBitmap(
                _width, _height, 96, 96, PixelFormats.Pbgra32, null);
            _overlayImage.Source = _overlayBitmap;
            _overlayImage.Opacity = 1.0;

            _timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(1000.0 / 60.0)
            };
            _timer.Tick += OnTick;

            // ВИПРАВЛЕННЯ #4: blur-фільтри — константні, створюємо один раз
            _cachedBlurIdle = SKImageFilter.CreateBlur(28f, 28f);
            _cachedBlurLoading = SKImageFilter.CreateBlur(32f, 32f);
        }

        // ════════════════════════════════════════════════════════════════════
        //   PUBLIC API
        // ════════════════════════════════════════════════════════════════════

        public void Start()
        {
            _lastTickTime = (float)_clock.Elapsed.TotalSeconds;
            _log("[ANIMATOR] Timer started (60 fps)", "info");
            _timer.Start();
        }

        public void Stop()
        {
            _log("[ANIMATOR] Timer stopped", "info");
            _timer.Stop();
        }

        /// <summary>Плавно показати overlay (викликати коли потрібна заглушка).</summary>
        public void FadeIn()
        {
            if (!_overlayFadingIn)
                _log($"[ANIMATOR] Overlay fade-in started (opacity {_overlayOpacity:F2} → 1.0)", "info");
            _overlayFadingIn = true;
            _overlayFadingOut = false;
            _overlayFadeProgress = 0f;
        }

        /// <summary>
        /// Плавно сховати overlay (викликати коли VLC починає грати кліп).
        /// VLC продовжує рендерити у свій VideoImage — ми просто знімаємо overlay.
        /// </summary>
        public void FadeOut()
        {
            if (!_overlayFadingOut)
                _log($"[ANIMATOR] Overlay fade-out started (opacity {_overlayOpacity:F2} → 0.0)", "info");
            _overlayFadingOut = true;
            _overlayFadingIn = false;
            _overlayFadeProgress = 0f;
        }

        /// <summary>Немає активного плеєра.</summary>
        public void ShowNoPlayer()
        {
            _log("[ANIMATOR] State → NoPlayer", "info");
            CancelPendingFetch();
            BeginContentTransition(AnimatorState.NoPlayer, "", null);
            FadeIn();
        }

        /// <summary>Пауза — розмита обкладинка з плаванням.</summary>
        public void ShowIdle(string trackName = "", GlobalSystemMediaTransportControlsSession? session = null)
        {
            _log($"[ANIMATOR] State → Idle | track: '{trackName}' | session: {(session != null ? session.SourceAppUserModelId : "none")}", "info");
            CancelPendingFetch();
            FadeIn();
            if (session != null)
            {
                var cts = CreateFetchCts();
                _ = FetchArtAndTransitionAsync(AnimatorState.Idle, trackName, session, cts.Token);
            }
            else
            {
                BeginContentTransition(AnimatorState.Idle, trackName, null);
            }
        }

        /// <summary>Завантаження кліпу — нерухома обкладинка + Loading... + назва.</summary>
        public void ShowLoading(string trackName,
            GlobalSystemMediaTransportControlsSession? session = null)
        {
            _log($"[ANIMATOR] State → Loading | track: '{trackName}' | session: {(session != null ? session.SourceAppUserModelId : "none")}", "info");
            CancelPendingFetch();
            FadeIn();
            if (session != null)
            {
                var cts = CreateFetchCts();
                _ = FetchArtAndTransitionAsync(AnimatorState.Loading, trackName, session, cts.Token);
            }
            else
            {
                BeginContentTransition(AnimatorState.Loading, trackName, null);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //   SMTC ALBUM ART
        // ════════════════════════════════════════════════════════════════════

        // ВИПРАВЛЕННЯ #5: скасовує поточний fetch і створює новий CTS
        private CancellationTokenSource CreateFetchCts()
        {
            _fetchCts = new CancellationTokenSource();
            return _fetchCts;
        }

        private void CancelPendingFetch()
        {
            try { _fetchCts.Cancel(); } catch { }
        }

        private async Task FetchArtAndTransitionAsync(
            AnimatorState nextState,
            string trackName,
            GlobalSystemMediaTransportControlsSession session,
            CancellationToken cancellationToken) // ВИПРАВЛЕННЯ #5: передаємо token
        {
            // Одразу переключаємо без арту — overlay вже видно, арт підтягнеться асинхронно
            BeginContentTransition(nextState, trackName, null);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            string requestedTrack = string.IsNullOrEmpty(trackName) ? "(idle)" : trackName;

            // Ключ актуальності: для Loading це назва треку, для Idle — порожній рядок.
            string artKey = trackName;
            await _dispatcher.InvokeAsync(() => _artRequestKey = artKey);

            _log($"[ART] Request: state={nextState}, track='{requestedTrack}'", "info");

            try
            {
                // ── Крок 1: отримуємо MediaProperties ────────────────────────
                cancellationToken.ThrowIfCancellationRequested();
                var props = await session.TryGetMediaPropertiesAsync();

                string propsTrack = props != null
                    ? $"{props.Artist} - {props.Title}".Trim(' ', '-')
                    : "null";
                _log($"[ART] Step 1/4 props received in {sw.ElapsedMilliseconds}ms" +
                     $" | track in props: '{propsTrack}'", "info");

                if (props == null)
                {
                    _log("[ART] ✗ Step 1/4 failed: TryGetMediaPropertiesAsync returned null", "error");
                    return;
                }

                if (props.Thumbnail == null)
                {
                    _log($"[ART] ✗ Step 1/4 failed: Thumbnail is null in props" +
                         $" (track '{propsTrack}' has no cover art in SMTC)", "warning");
                    return;
                }

                // ── Крок 2: відкриваємо WinRT stream ─────────────────────────
                cancellationToken.ThrowIfCancellationRequested();
                _log("[ART] Step 2/4 opening thumbnail stream...", "info");
                using var winrtStream = await props.Thumbnail.OpenReadAsync();

                ulong streamSize = winrtStream.Size;
                string contentType = winrtStream.ContentType;
                _log($"[ART] Step 2/4 stream opened in {sw.ElapsedMilliseconds}ms" +
                     $" | size={streamSize} bytes, contentType={contentType}", "info");

                if (streamSize == 0)
                {
                    _log($"[ART] ✗ Step 2/4 failed: stream size=0" +
                         $" (SMTC returned empty thumbnail for '{propsTrack}')", "warning");
                    return;
                }

                // ВИПРАВЛЕННЯ #6: перевірка на розмір — streamSize є ulong,
                // але масив byte обмежений int.MaxValue (~2 GB).
                // Обкладинка > 64 MB свідчить про помилку — відкидаємо.
                const ulong MaxArtSizeBytes = 64 * 1024 * 1024; // 64 MB
                if (streamSize > MaxArtSizeBytes)
                {
                    _log($"[ART] ✗ Step 2/4 failed: stream size {streamSize} bytes exceeds 64 MB limit", "error");
                    return;
                }

                // ── Крок 3: читаємо байти ─────────────────────────────────────
                cancellationToken.ThrowIfCancellationRequested();
                _log($"[ART] Step 3/4 reading {streamSize} bytes...", "info");
                var bytes = new byte[(int)streamSize];
                using var dotnetStream = winrtStream.AsStreamForRead();
                int total = 0;
                int iteration = 0;
                while (total < bytes.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int n = await dotnetStream.ReadAsync(bytes, total, bytes.Length - total);
                    iteration++;
                    if (n == 0)
                    {
                        _log($"[ART] ✗ Step 3/4 stream ended early:" +
                             $" read {total}/{streamSize} bytes after {iteration} iteration(s)" +
                             $" | missing {streamSize - (ulong)total} bytes", "error");
                        break;
                    }
                    total += n;
                }

                if (total == 0)
                {
                    _log($"[ART] ✗ Step 3/4 failed: read 0 bytes from stream" +
                         $" (stream opened but unreadable, contentType={contentType})", "error");
                    return;
                }

                bool truncated = (ulong)total < streamSize;
                _log($"[ART] Step 3/4 read complete in {sw.ElapsedMilliseconds}ms" +
                     $" | {total}/{streamSize} bytes{(truncated ? " ⚠ TRUNCATED" : "")}",
                     truncated ? "warning" : "info");

                // ── Крок 4: декодуємо SKBitmap ───────────────────────────────
                cancellationToken.ThrowIfCancellationRequested();
                _log("[ART] Step 4/4 decoding SKBitmap...", "info");
                SKBitmap? art;
                try
                {
                    using var ms = new MemoryStream(bytes, 0, total);
                    art = SKBitmap.Decode(ms);
                }
                catch (Exception ex)
                {
                    _log($"[ART] ✗ Step 4/4 SKBitmap.Decode threw exception:" +
                         $" {ex.GetType().Name}: {ex.Message}" +
                         $" | {total} bytes, contentType={contentType}", "error");
                    return;
                }

                if (art == null)
                {
                    _log($"[ART] ✗ Step 4/4 SKBitmap.Decode returned null" +
                         $" | {total} bytes, contentType={contentType}" +
                         $" | possibly unsupported format or corrupted data", "error");
                    return;
                }

                _log($"[ART] Step 4/4 decoded OK in {sw.ElapsedMilliseconds}ms" +
                     $" | {art.Width}x{art.Height} {art.ColorType}", "success");

                // ── Застосовуємо арт на UI thread ────────────────────────────
                cancellationToken.ThrowIfCancellationRequested();
                await _dispatcher.InvokeAsync(() =>
                {
                    if (_disposed)
                    {
                        _log("[ART] ✗ Discarded: animator was disposed during fetch" +
                             $" (fetch took {sw.ElapsedMilliseconds}ms)", "warning");
                        art.Dispose();
                        return;
                    }

                    // Перевіряємо ключ актуальності
                    bool keyMismatch = _artRequestKey != artKey;
                    bool trackSuperseded = _pendingTrack != trackName
                        && _trackName != trackName;

                    if (keyMismatch || trackSuperseded)
                    {
                        string reason = keyMismatch
                            ? $"art key changed (requested='{artKey}', current='{_artRequestKey}')"
                            : $"track superseded (requested='{trackName}', pending='{_pendingTrack}')";
                        _log($"[ART] ✗ Discarded: {reason}" +
                             $" | state: {nextState}→{_state}" +
                             $" | fetch took {sw.ElapsedMilliseconds}ms", "warning");
                        art.Dispose();
                        return;
                    }

                    // Застосовуємо арт напряму в _albumArt/_cachedArtImage.
                    // ApplyPendingState не перезапише його — він пропускає null pendingArt.
                    string artSizeLog;
                    lock (_artLock)
                    {
                        _albumArt?.Dispose();
                        _albumArt = art;
                        _cachedArtImage?.Dispose();
                        _cachedArtImage = SKImage.FromBitmap(_albumArt);
                        artSizeLog = $"{art.Width}x{art.Height}";
                    }

                    _log($"[ART] ✓ Applied: {artSizeLog}" +
                         $" | state={_state}, track='{requestedTrack}'" +
                         $" | total fetch time: {sw.ElapsedMilliseconds}ms", "success");
                });
            }
            catch (OperationCanceledException)
            {
                // ВИПРАВЛЕННЯ #5: нормальний шлях при швидкій зміні треків —
                // не логуємо як помилку
                _log($"[ART] Fetch cancelled for '{requestedTrack}' after {sw.ElapsedMilliseconds}ms", "info");
            }
            catch (Exception ex)
            {
                string inner = ex.InnerException != null
                    ? $" | inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}"
                    : "";
                _log($"[ART] ✗ Unhandled exception at {sw.ElapsedMilliseconds}ms:" +
                     $" {ex.GetType().Name}: {ex.Message}{inner}", "error");
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //   INTERNAL
        // ════════════════════════════════════════════════════════════════════

        private void BeginContentTransition(AnimatorState next, string track, SKBitmap? art)
        {
            _dispatcher.BeginInvoke(() =>
            {
                _pendingState = next;
                _pendingTrack = track;
                _pendingArt = art;
                _isFading = true;
                _fadeProgress = 0f;
                _artRequestKey = track;
            });
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (_disposed) return;

            // ВИПРАВЛЕННЯ #8: рахуємо реальний delta-time замість фіксованого 0.016f
            float now = (float)_clock.Elapsed.TotalSeconds;
            float dt = Math.Clamp(now - _lastTickTime, 0f, 0.1f); // clamp: захист від великих стрибків
            _lastTickTime = now;

            UpdateOverlayFade(dt);
            UpdateContentFade(dt);
            RenderFrame();
        }

        private void UpdateOverlayFade(float dt)
        {
            float step = dt / OverlayFadeDur;
            if (_overlayFadingIn)
            {
                _overlayOpacity = Math.Clamp(_overlayOpacity + step, 0f, 1f);
                _overlayImage.Opacity = _overlayOpacity;
                if (_overlayOpacity >= 1f)
                {
                    _overlayFadingIn = false;
                    _log("[ANIMATOR] Overlay fade-in complete (opacity=1.0)", "success");
                }
            }
            else if (_overlayFadingOut)
            {
                _overlayOpacity = Math.Clamp(_overlayOpacity - step, 0f, 1f);
                _overlayImage.Opacity = _overlayOpacity;
                if (_overlayOpacity <= 0f)
                {
                    _overlayFadingOut = false;
                    _log("[ANIMATOR] Overlay fade-out complete (opacity=0.0) — VLC fully visible", "success");
                }
            }
        }

        private void UpdateContentFade(float dt)
        {
            if (!_isFading) return;
            _fadeProgress += dt;
            float half = FadeDuration * 0.5f;

            if (_fadeProgress < half)
            {
                _contentAlpha = 1f - (_fadeProgress / half);
            }
            else if (_fadeProgress < half + dt + 0.001f && _fadeProgress >= half)
            {
                // Застосовуємо стан рівно один раз при переході через середину
                if (_contentAlpha > 0f)
                {
                    ApplyPendingState();
                    _contentAlpha = 0f;
                }
            }
            else
            {
                _contentAlpha = Math.Clamp((_fadeProgress - half) / half, 0f, 1f);
                if (_contentAlpha >= 1f) _isFading = false;
            }
        }

        private void ApplyPendingState()
        {
            var prevState = _state;

            string artInfo;
            lock (_artLock)
            {
                // Якщо _pendingArt == null — арт вже був застосований напряму через fetch,
                // не перезаписуємо _albumArt щоб не знищити вже встановлений арт.
                if (_pendingArt != null)
                {
                    _albumArt?.Dispose();
                    _albumArt = _pendingArt;
                    _pendingArt = null;

                    _cachedArtImage?.Dispose();
                    _cachedArtImage = SKImage.FromBitmap(_albumArt);
                }

                artInfo = _albumArt != null
                    ? $" (art: {_albumArt.Width}x{_albumArt.Height})"
                    : " (art: none)";
            }

            _state = _pendingState;
            _trackName = _pendingTrack;
            _log($"[ANIMATOR] Content transition applied: {prevState} → {_state}{artInfo}", "info");
        }

        private void RenderFrame()
        {
            if (_overlayOpacity <= 0f) return;

            float t = (float)_clock.Elapsed.TotalSeconds;

            try
            {
                _overlayBitmap.Lock();
                var info = new SKImageInfo(_width, _height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var surface = SKSurface.Create(info, _overlayBitmap.BackBuffer, _width * 4);
                if (surface == null)
                {
                    _log("[ANIMATOR] SKSurface.Create returned null — skipping frame", "error");
                    return;
                }

                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Black);

                switch (_state)
                {
                    case AnimatorState.NoPlayer: DrawNoPlayer(canvas, t); break;
                    case AnimatorState.Idle: DrawIdle(canvas, t); break;
                    case AnimatorState.Loading: DrawLoading(canvas, t); break;
                }

                // Маска для fade між станами
                if (_contentAlpha < 1f)
                {
                    using var fadePaint = new SKPaint
                    {
                        Color = new SKColor(0, 0, 0, (byte)(255 * (1f - _contentAlpha)))
                    };
                    canvas.DrawRect(0, 0, _width, _height, fadePaint);
                }

                _overlayBitmap.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
            }
            finally
            {
                _overlayBitmap.Unlock();
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //   DRAW: NoPlayer
        // ════════════════════════════════════════════════════════════════════

        private void DrawNoPlayer(SKCanvas canvas, float t)
        {
            using var bgShader = SKShader.CreateRadialGradient(
                new SKPoint(_width * 0.5f, _height * 0.5f), _height * 0.75f,
                new[] { new SKColor(20, 20, 30), SKColors.Black },
                null, SKShaderTileMode.Clamp);
            using var bgPaint = new SKPaint { Shader = bgShader };
            canvas.DrawRect(0, 0, _width, _height, bgPaint);

            float lineY = _height - 3f;
            float barW = _width * (0.3f + 0.05f * MathF.Sin(t * 0.5f));
            float barX = (_width - barW) * 0.5f;
            using var linePaint = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(barX, lineY), new SKPoint(barX + barW, lineY),
                    new[] { SKColors.Transparent, new SKColor(120, 100, 200, 180), SKColors.Transparent },
                    null, SKShaderTileMode.Clamp),
                StrokeWidth = 2f,
                Style = SKPaintStyle.Stroke
            };
            canvas.DrawLine(barX, lineY, barX + barW, lineY, linePaint);

            DrawCenteredText(canvas, "No player is running",
                _width * 0.5f, _height * 0.5f - 16f, 32f, new SKColor(200, 200, 210, 220));
            DrawCenteredText(canvas, "Start Spotify, YouTube Music or any other player",
                _width * 0.5f, _height * 0.5f + 28f, 18f, new SKColor(130, 130, 150, 160));
        }

        // ════════════════════════════════════════════════════════════════════
        //   DRAW: Idle
        // ════════════════════════════════════════════════════════════════════

        private void DrawIdle(SKCanvas canvas, float t)
        {
            SKImage? img;
            SKBitmap? art;
            lock (_artLock)
            {
                img = _cachedArtImage;
                art = _albumArt;
            }

            // ВИПРАВЛЕННЯ #9: якщо арт ще не завантажився — малюємо нейтральний
            // темний фон з підписом замість "No player is running"
            if (img == null || art == null)
            {
                DrawIdleNoArt(canvas, t);
                return;
            }

            float bgX = BgFloatAmpX * MathF.Sin(t * BgFloatSpeedX);
            float bgY = BgFloatAmpY * MathF.Sin(t * BgFloatSpeedY + 1.1f);

            // ВИПРАВЛЕННЯ #2, #3: передаємо кешований SKImage замість SKBitmap,
            // не тримаємо lock під час малювання
            DrawBlurredBackground(canvas, img, art.Width, art.Height,
                bgX, bgY, blurRadius: 28f, scale: 1.08f, cachedFilter: _cachedBlurIdle);

            using var mask = new SKPaint { Color = new SKColor(0, 0, 0, 120) };
            canvas.DrawRect(0, 0, _width, _height, mask);

            float fgX = 0f;
            float fgY = 0f;
            float size = MathF.Min(_width, _height) * 0.42f;
            float cx = _width * 0.5f - size * 0.5f;
            float cy = _height * 0.5f - size * 0.5f;

            DrawRoundedCover(canvas, img, art.Width, art.Height,
                new SKRect(cx, cy, cx + size, cy + size), 18f);

            // Текст "Paused" під обкладинкою з легким блиманням
            float pulse = 0.75f + 0.25f * MathF.Sin(t * 1.5f);
            float textY = cy + size + 38f;
            DrawCenteredText(canvas, "Paused",
                _width * 0.5f + fgX, textY,
                22f, new SKColor(200, 190, 220, (byte)(180 * pulse)));
        }

        // ВИПРАВЛЕННЯ #9: окремий fallback для Idle без арту
        private void DrawIdleNoArt(SKCanvas canvas, float t)
        {
            using var bgShader = SKShader.CreateRadialGradient(
                new SKPoint(_width * 0.5f, _height * 0.5f), _height * 0.75f,
                new[] { new SKColor(15, 15, 25), SKColors.Black },
                null, SKShaderTileMode.Clamp);
            using var bgPaint = new SKPaint { Shader = bgShader };
            canvas.DrawRect(0, 0, _width, _height, bgPaint);

            float pulse = 0.6f + 0.4f * MathF.Sin(t * 1.2f);
            DrawCenteredText(canvas, "Paused",
                _width * 0.5f, _height * 0.5f,
                36f, new SKColor(180, 170, 210, (byte)(200 * pulse)));
        }

        // ════════════════════════════════════════════════════════════════════
        //   DRAW: Loading
        // ════════════════════════════════════════════════════════════════════

        private void DrawLoading(SKCanvas canvas, float t)
        {
            SKImage? img;
            SKBitmap? art;
            lock (_artLock)
            {
                img = _cachedArtImage;
                art = _albumArt;
            }

            // ВИПРАВЛЕННЯ #2, #3: малюємо з кешованим SKImage поза lock
            if (img != null && art != null)
            {
                DrawBlurredBackground(canvas, img, art.Width, art.Height,
                    0f, 0f, 32f, 1.05f, cachedFilter: _cachedBlurLoading);
                using var m = new SKPaint { Color = new SKColor(0, 0, 0, 150) };
                canvas.DrawRect(0, 0, _width, _height, m);
            }
            else
            {
                using var s = SKShader.CreateRadialGradient(
                    new SKPoint(_width * 0.5f, _height * 0.5f), _height * 0.8f,
                    new[] { new SKColor(25, 20, 35), SKColors.Black },
                    null, SKShaderTileMode.Clamp);
                using var p = new SKPaint { Shader = s };
                canvas.DrawRect(0, 0, _width, _height, p);
            }

            float coverSize = MathF.Min(_width, _height) * 0.38f;
            float cx = _width * 0.5f - coverSize * 0.5f;
            float cy = _height * 0.5f - coverSize * 0.5f - 40f;

            if (img != null && art != null)
                DrawRoundedCover(canvas, img, art.Width, art.Height,
                    new SKRect(cx, cy, cx + coverSize, cy + coverSize), 18f);
            else
            {
                using var ph = new SKPaint { Color = new SKColor(60, 55, 75), IsAntialias = true };
                canvas.DrawRoundRect(
                    new SKRoundRect(new SKRect(cx, cy, cx + coverSize, cy + coverSize), 18f), ph);
            }

            float below = cy + coverSize + 32f;

            string dots = new string('.', (int)(t * 1.5f) % 4);
            DrawCenteredText(canvas, "Loading" + dots,
                _width * 0.5f, below, 28f, new SKColor(230, 230, 240, 230));

            if (!string.IsNullOrEmpty(_trackName))
                DrawCenteredText(canvas, _trackName,
                    _width * 0.5f, below + 44f, 20f, new SKColor(180, 170, 210, 200),
                    maxWidth: coverSize * 1.4f);

            float barW = coverSize * 1.1f, barH = 3f;
            float barX = _width * 0.5f - barW * 0.5f;
            float barY = below + (string.IsNullOrEmpty(_trackName) ? 36f : 86f);

            using var trackP = new SKPaint { Color = new SKColor(80, 80, 100, 100), IsAntialias = true };
            canvas.DrawRoundRect(
                new SKRoundRect(new SKRect(barX, barY, barX + barW, barY + barH), barH * 0.5f), trackP);

            float phase = (t % 2f) / 2f;
            float bounce = phase < 0.5f ? phase * 2f : (1f - phase) * 2f;
            float indW = barW * 0.3f, indX = barX + (barW - indW) * bounce;

            using var indS = SKShader.CreateLinearGradient(
                new SKPoint(indX, barY), new SKPoint(indX + indW, barY),
                new[] { SKColors.Transparent, new SKColor(160, 140, 220, 220), SKColors.Transparent },
                null, SKShaderTileMode.Clamp);
            using var indP = new SKPaint { Shader = indS, IsAntialias = true };
            canvas.DrawRoundRect(
                new SKRoundRect(new SKRect(indX, barY, indX + indW, barY + barH), barH * 0.5f), indP);
        }

        // ════════════════════════════════════════════════════════════════════
        //   HELPERS
        // ════════════════════════════════════════════════════════════════════

        // ВИПРАВЛЕННЯ #2, #3, #4: приймає SKImage (не SKBitmap) та кешований фільтр.
        // SKImage більше не створюється щокадру — передається ззовні.
        // cachedFilter — null означає що blur не потрібен.
        private void DrawBlurredBackground(
            SKCanvas canvas, SKImage img, int srcW, int srcH,
            float offX, float offY, float blurRadius, float scale,
            SKImageFilter? cachedFilter = null)
        {
            float dW = _width * scale, dH = _height * scale;
            float dX = (_width - dW) * 0.5f + offX;
            float dY = (_height - dH) * 0.5f + offY;

            using var paint = new SKPaint { ImageFilter = cachedFilter };
            canvas.DrawImage(img,
                new SKRect(0, 0, srcW, srcH),
                new SKRect(dX, dY, dX + dW, dY + dH),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
                paint);
        }

        // ВИПРАВЛЕННЯ #2, #3, #7: приймає SKImage (не SKBitmap),
        // параметр shadowAlpha видалено — він не використовувався.
        private static void DrawRoundedCover(
            SKCanvas canvas, SKImage img, int srcW, int srcH,
            SKRect dest, float radius)
        {
            canvas.Save();
            canvas.ClipRoundRect(new SKRoundRect(dest, radius), antialias: true);

            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawImage(img,
                new SKRect(0, 0, srcW, srcH), dest,
                new SKSamplingOptions(SKCubicResampler.Mitchell), paint);

            canvas.Restore();

            using var border = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = new SKColor(255, 255, 255, 30),
                StrokeWidth = 1f,
                IsAntialias = true
            };
            canvas.DrawRoundRect(new SKRoundRect(dest, radius), border);
        }

        private static void DrawCenteredText(
            SKCanvas canvas, string text,
            float cx, float cy, float fontSize, SKColor color,
            float maxWidth = float.MaxValue)
        {
            using var font = new SKFont(SKTypeface.Default, fontSize);
            using var paint = new SKPaint { Color = color, IsAntialias = true };

            if (maxWidth < float.MaxValue)
            {
                while (text.Length > 3 && font.MeasureText(text + "…") > maxWidth)
                    text = text[..^1];
                if (font.MeasureText(text) > maxWidth) text += "…";
            }

            float w = font.MeasureText(text);
            float x = cx - w * 0.5f;
            font.GetFontMetrics(out var m);
            float y = cy - (m.Ascent + m.Descent) * 0.5f;

            using var shadow = new SKPaint
            {
                Color = new SKColor(0, 0, 0, 160),
                IsAntialias = true,
                ImageFilter = SKImageFilter.CreateBlur(3f, 3f)
            };
            canvas.DrawText(text, x + 1f, y + 2f, SKTextAlign.Left, font, shadow);
            canvas.DrawText(text, x, y, SKTextAlign.Left, font, paint);
        }

        // ════════════════════════════════════════════════════════════════════

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();

            // ВИПРАВЛЕННЯ #5: зупиняємо будь-який активний fetch
            try { _fetchCts.Cancel(); } catch { }
            _fetchCts.Dispose();

            lock (_artLock)
            {
                _albumArt?.Dispose(); _albumArt = null;
                _pendingArt?.Dispose(); _pendingArt = null;
                _cachedArtImage?.Dispose(); _cachedArtImage = null;
            }

            // ВИПРАВЛЕННЯ #4: звільняємо кешовані blur-фільтри
            _cachedBlurIdle?.Dispose(); _cachedBlurIdle = null;
            _cachedBlurLoading?.Dispose(); _cachedBlurLoading = null;

            _log("[ANIMATOR] Disposed", "info");
        }
    }
}