using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;

namespace WallpaperMusicPlayer
{
    /// <summary>
    /// Ізольований контекст для завантаження YoutubeExplode.dll
    /// Дозволяє вивантажувати та перезавантажувати DLL без перезапуску додатка
    /// </summary>
    public class YoutubeLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _appBaseDir;

        // Залежності YoutubeExplode які потрібно завантажити вручну,
        // бо без .deps.json AssemblyDependencyResolver їх не знаходить
        private static readonly string[] _knownDependencies = new[]
        {
            "CliWrap",
            "AngleSharp",
        };

        public YoutubeLoadContext(string pluginPath) : base(isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
            _appBaseDir = AppDomain.CurrentDomain.BaseDirectory;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Спочатку пробуємо через стандартний resolver
            string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath != null)
                return LoadFromAssemblyPath(assemblyPath);

            // Якщо resolver не знайшов — шукаємо вручну у папці застосунку.
            // Це потрібно для CliWrap, AngleSharp та інших залежностей YoutubeExplode
            // які не описані у .deps.json (бо ми завантажуємо DLL динамічно).
            if (assemblyName.Name != null &&
                _knownDependencies.Any(d => assemblyName.Name.StartsWith(d, StringComparison.OrdinalIgnoreCase)))
            {
                string manualPath = Path.Combine(_appBaseDir, assemblyName.Name + ".dll");
                if (File.Exists(manualPath))
                    return LoadFromAssemblyPath(manualPath);
            }

            return null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (libraryPath != null)
                return LoadUnmanagedDllFromPath(libraryPath);

            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Результат пошуку відео
    /// </summary>
    public class VideoSearchResult
    {
        public string VideoId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Author { get; set; } = "";
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// Інформація про стрім
    /// </summary>
    public class VideoStreamInfo
    {
        public string Url { get; set; } = "";
        public string Quality { get; set; } = "";
        public string Container { get; set; } = "";
        public int Height { get; set; }
    }

    /// <summary>
    /// Обгортка для роботи з YouTube через динамічно завантажену YoutubeExplode.dll
    /// Підтримує автоматичне оновлення без перезапуску додатка
    /// ВИПРАВЛЕНО ДЛЯ СУМІСНОСТІ З YOUTUBEEXPLODE 6.5+
    /// </summary>
    public class YoutubeWrapper : IDisposable
    {
        private YoutubeLoadContext? _loadContext;
        private object? _youtubeClient;
        private Type? _youtubeClientType;
        private readonly string _dllPath;
        private readonly object _lock = new object();
        private bool _isDisposed = false;
        private readonly Action<string, string>? _diagLog;

        public bool IsLoaded => _youtubeClient != null && _loadContext != null;

        public YoutubeWrapper(Action<string, string>? diagLog = null)
        {
            _diagLog = diagLog;
            _dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "YoutubeExplode.dll");
            LoadYoutubeExplode();
        }

        /// <summary>
        /// Завантажує YoutubeExplode.dll в ізольований контекст
        /// </summary>
        private void LoadYoutubeExplode()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(_dllPath))
                    {
                        throw new FileNotFoundException($"YoutubeExplode.dll not found at {_dllPath}");
                    }

                    _loadContext = new YoutubeLoadContext(_dllPath);
                    var assembly = _loadContext.LoadFromAssemblyPath(_dllPath);

                    // [ДІАГНОСТИКА] Версія завантаженої DLL
                    var fileVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(_dllPath).FileVersion;
                    var assemblyVersion = assembly.GetName().Version?.ToString() ?? "unknown";
                    _diagLog?.Invoke($"[YOUTUBE DIAG] DLL file version: {fileVersion}, assembly version: {assemblyVersion}", "info");

                    // [ДІАГНОСТИКА] Перевірка залежностей AngleSharp та CliWrap
                    var baseDir = Path.GetDirectoryName(_dllPath)!;
                    foreach (var dep in new[] { "AngleSharp.dll", "CliWrap.dll" })
                    {
                        var depPath = Path.Combine(baseDir, dep);
                        _diagLog?.Invoke($"[YOUTUBE DIAG] Dependency {dep}: {(File.Exists(depPath) ? "FOUND" : "MISSING!")}", File.Exists(depPath) ? "info" : "error");
                    }

                    // [ДІАГНОСТИКА] Всі завантажені assemblies у контексті
                    var loadedAssemblies = _loadContext.Assemblies.Select(a => a.GetName().Name).ToList();
                    _diagLog?.Invoke($"[YOUTUBE DIAG] Loaded assemblies: {string.Join(", ", loadedAssemblies)}", "info");

                    _youtubeClientType = assembly.GetType("YoutubeExplode.YoutubeClient");
                    if (_youtubeClientType == null)
                    {
                        throw new TypeLoadException("Could not find YoutubeClient type in YoutubeExplode.dll");
                    }

                    // Використовуємо конструктор без параметрів — YoutubeClient сам створює свій HttpClient
                    // всередині ізольованого контексту. Це уникає помилки:
                    // "Object of type 'System.Net.Http.HttpClient' cannot be converted to type
                    //  'System.Net.Http.HttpClient'" — яка виникає коли HttpClient з основного
                    // AssemblyLoadContext передається в ізольований YoutubeLoadContext.
                    _youtubeClient = Activator.CreateInstance(_youtubeClientType);
                    _diagLog?.Invoke("[YOUTUBE DIAG] YoutubeClient created with default constructor (no external HttpClient)", "info");

                    // Логуємо після створення клієнта — залежності завантажуються lazily,
                    // тому тут вже мають бути CliWrap та AngleSharp
                    var loadedAfter = _loadContext.Assemblies.Select(a => a.GetName().Name).ToList();
                    _diagLog?.Invoke($"[YOUTUBE DIAG] Assemblies after client init ({loadedAfter.Count}): {string.Join(", ", loadedAfter)}", "info");
                    _diagLog?.Invoke($"[YOUTUBE DIAG] YoutubeClient created successfully", "success");
                }
                catch (Exception ex)
                {
                    throw new Exception($"Failed to load YoutubeExplode: {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// Вивантажує YoutubeExplode.dll з пам'яті
        /// Після цього можна замінити файл на диску
        /// </summary>
        public void UnloadYoutubeExplode()
        {
            // ВИПРАВЛЕНО: GC.Collect, GC.WaitForPendingFinalizers та Thread.Sleep
            // винесені за межі lock — раніше вони блокували _lock на ~1.5 с,
            // фризячи будь-який паралельний виклик SearchVideosAsync/GetVideoStreamAsync.
            lock (_lock)
            {
                _youtubeClient = null;
                _youtubeClientType = null;

                if (_loadContext != null)
                {
                    _loadContext.Unload();
                    _loadContext = null;
                }
            }

            // Примусова очистка пам'яті — поза локом, не блокує інші потоки
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            // Чекаємо щоб файл точно вивантажився — поза локом
            Thread.Sleep(500);
        }

        /// <summary>
        /// Перезавантажує YoutubeExplode.dll (після оновлення)
        /// </summary>
        public void ReloadYoutubeExplode()
        {
            UnloadYoutubeExplode();
            Thread.Sleep(1000); // Чекаємо щоб Windows звільнив файл
            LoadYoutubeExplode();
        }

        /// <summary>
        /// Шукає відео на YouTube за запитом
        /// ВИПРАВЛЕНО: Автоматична адаптація до різних версій API
        /// </summary>
        public async Task<List<VideoSearchResult>> SearchVideosAsync(string query, int maxResults = 1, CancellationToken cancellationToken = default)
        {
            if (!IsLoaded)
                throw new InvalidOperationException("YoutubeExplode is not loaded");

            try
            {
                // youtubeClient.Search
                var searchProp = _youtubeClientType!.GetProperty("Search");
                if (searchProp == null)
                    throw new InvalidOperationException("Search property not found");

                var searchObj = searchProp.GetValue(_youtubeClient);
                if (searchObj == null)
                    throw new InvalidOperationException("Search object is null");

                var searchType = searchObj.GetType();

                // [ДІАГНОСТИКА] Доступні методи Search об'єкту
                var searchMethods = searchType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    .Select(m => m.Name).Distinct().ToList();
                _diagLog?.Invoke($"[YOUTUBE DIAG] Search methods: {string.Join(", ", searchMethods)}", "info");

                // Search.GetVideosAsync - адаптивний виклик
                var getVideosMethod = searchType.GetMethod("GetVideosAsync");
                if (getVideosMethod == null)
                    throw new InvalidOperationException("GetVideosAsync method not found");

                var parameters = getVideosMethod.GetParameters();

                // [ДІАГНОСТИКА] Сигнатура GetVideosAsync
                var paramDesc = string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"));
                _diagLog?.Invoke($"[YOUTUBE DIAG] GetVideosAsync signature: ({paramDesc})", "info");
                object searchResultEnumerable;

                // Автоматичне визначення сигнатури методу
                if (parameters.Length == 2) // (string query, CancellationToken cancellationToken)
                {
                    searchResultEnumerable = getVideosMethod.Invoke(searchObj, new object[] { query, cancellationToken })!;
                }
                else if (parameters.Length == 1) // (string query)
                {
                    searchResultEnumerable = getVideosMethod.Invoke(searchObj, new object[] { query })!;
                }
                else
                {
                    throw new InvalidOperationException($"Unexpected GetVideosAsync signature: {parameters.Length} parameters");
                }

                if (searchResultEnumerable == null)
                    return new List<VideoSearchResult>();

                // Отримуємо тип IAsyncEnumerable
                var enumerableType = searchResultEnumerable.GetType();

                // [ДІАГНОСТИКА] Тип результату enumerable
                _diagLog?.Invoke($"[YOUTUBE DIAG] Enumerable type: {enumerableType.Name}, generic args: {enumerableType.GetGenericArguments().Length}", "info");

                // Спочатку шукаємо AsyncEnumerableExtensions / CollectAsync (старий підхід)
                var allAssemblies = _loadContext!.Assemblies.ToList();
                _diagLog?.Invoke($"[YOUTUBE DIAG] Searching AsyncEnumerableExtensions in {allAssemblies.Count} assemblies...", "info");

                var commonExtType = allAssemblies
                    .SelectMany(a =>
                    {
                        try { return a.GetTypes(); }
                        catch { return Array.Empty<Type>(); }
                    })
                    .FirstOrDefault(t => t.Name == "AsyncEnumerableExtensions");

                _diagLog?.Invoke($"[YOUTUBE DIAG] AsyncEnumerableExtensions: {(commonExtType != null ? $"FOUND in {commonExtType.Namespace}" : "NOT FOUND — will use direct IAsyncEnumerable iteration")}", commonExtType != null ? "info" : "warning");

                if (commonExtType != null)
                {
                    var collectMethod = commonExtType.GetMethods()
                        .FirstOrDefault(m => m.Name == "CollectAsync" && m.GetParameters().Length == 2);

                    _diagLog?.Invoke($"[YOUTUBE DIAG] CollectAsync(2 params): {(collectMethod != null ? "FOUND" : "NOT FOUND")}", collectMethod != null ? "info" : "warning");

                    if (collectMethod == null)
                    {
                        var allMethods = commonExtType.GetMethods().Select(m => $"{m.Name}({m.GetParameters().Length})").ToList();
                        _diagLog?.Invoke($"[YOUTUBE DIAG] Available methods: {string.Join(", ", allMethods)}", "warning");
                    }

                    if (collectMethod != null)
                    {
                        var genericArgs = enumerableType.GetGenericArguments();
                        if (genericArgs.Length == 0)
                            return new List<VideoSearchResult>();

                        var collectGeneric = collectMethod.MakeGenericMethod(genericArgs[0]);
                        var collectTask = (Task)collectGeneric.Invoke(null, new object[] { searchResultEnumerable, maxResults })!;
                        await collectTask.ConfigureAwait(false);

                        var resultProp = collectTask.GetType().GetProperty("Result");
                        if (resultProp == null)
                            return new List<VideoSearchResult>();

                        var videoList = resultProp.GetValue(collectTask) as System.Collections.IList;
                        var results = new List<VideoSearchResult>();

                        _diagLog?.Invoke($"[YOUTUBE DIAG] Raw results from CollectAsync: {videoList?.Count ?? 0}", videoList?.Count > 0 ? "info" : "warning");

                        if (videoList != null && videoList.Count > 0)
                        {
                            var firstVideo = videoList[0];
                            if (firstVideo != null)
                            {
                                var props = firstVideo.GetType().GetProperties().Select(p => p.Name).ToList();
                                _diagLog?.Invoke($"[YOUTUBE DIAG] Video properties: {string.Join(", ", props)}", "info");
                            }
                        }

                        if (videoList != null)
                        {
                            foreach (var video in videoList)
                            {
                                try { results.Add(ParseVideoResult(video)); }
                                catch (Exception videoEx)
                                {
                                    _diagLog?.Invoke($"[YOUTUBE DIAG] Failed to parse video: {videoEx.GetType().Name} - {videoEx.Message}", "warning");
                                }
                            }
                        }

                        _diagLog?.Invoke($"[YOUTUBE DIAG] Parsed results (CollectAsync): {results.Count}", results.Count > 0 ? "success" : "warning");
                        return results;
                    }
                }

                // Запасний підхід: ітеруємо IAsyncEnumerable<T> напряму через рефлексію.
                // Працює для будь-якої версії YoutubeExplode незалежно від залежностей.
                _diagLog?.Invoke($"[YOUTUBE DIAG] Falling back to direct IAsyncEnumerable iteration...", "info");
                return await IterateAsyncEnumerableAsync(searchResultEnumerable, enumerableType, maxResults);
            }
            catch (TargetInvocationException ex)
            {
                // Розгортаємо внутрішній виняток для детального логування
                var innerEx = ex.InnerException ?? ex;
                var detailedMessage = $"{innerEx.GetType().Name}: {innerEx.Message}";

                if (innerEx.InnerException != null)
                {
                    detailedMessage += $" | Inner: {innerEx.InnerException.GetType().Name}: {innerEx.InnerException.Message}";
                }

                if (!string.IsNullOrEmpty(innerEx.StackTrace))
                {
                    var firstStackLine = innerEx.StackTrace.Split('\n').FirstOrDefault()?.Trim();
                    if (firstStackLine != null)
                        detailedMessage += $" | at {firstStackLine}";
                }

                throw new Exception($"YouTube API Error: {detailedMessage}", innerEx);
            }
            catch (Exception ex)
            {
                // Детальне логування загальних помилок
                var detailedMessage = $"{ex.GetType().Name}: {ex.Message}";

                if (ex.InnerException != null)
                {
                    detailedMessage += $" | Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
                }

                throw new Exception($"YouTube Search Error: {detailedMessage}", ex);
            }
        }

        /// <summary>
        /// Парсить об'єкт відео з YoutubeExplode у VideoSearchResult
        /// </summary>
        private VideoSearchResult ParseVideoResult(object video)
        {
            var videoType = video.GetType();
            var idProp = videoType.GetProperty("Id");
            var titleProp = videoType.GetProperty("Title");
            var authorProp = videoType.GetProperty("Author");
            var durationProp = videoType.GetProperty("Duration");

            if (idProp == null || titleProp == null)
                throw new InvalidOperationException("Missing Id or Title property");

            var idObj = idProp.GetValue(video);
            if (idObj == null)
                throw new InvalidOperationException("Id is null");

            // Id може бути як struct з полем Value, так і просто string
            var idValue = idObj.GetType().GetProperty("Value")?.GetValue(idObj)?.ToString()
                          ?? idObj.ToString()
                          ?? "";

            if (string.IsNullOrEmpty(idValue))
                throw new InvalidOperationException("Id value is empty");

            var authorObj = authorProp?.GetValue(video);
            // Author може мати ChannelTitle або просто ToString()
            var authorTitle = authorObj?.GetType().GetProperty("ChannelTitle")?.GetValue(authorObj)?.ToString()
                              ?? authorObj?.ToString()
                              ?? "";

            return new VideoSearchResult
            {
                VideoId = idValue,
                Title = titleProp.GetValue(video)?.ToString() ?? "",
                Author = authorTitle,
                Duration = (TimeSpan?)durationProp?.GetValue(video) ?? TimeSpan.Zero
            };
        }

        /// <summary>
        /// Ітерує IAsyncEnumerable{T} напряму через рефлексію.
        /// Шукає MoveNextAsync/Current на конкретному типі enumerator (не через системний IAsyncEnumerator{T})
        /// щоб уникнути cross-context type mismatch при explicit interface implementation.
        /// </summary>
        private async Task<List<VideoSearchResult>> IterateAsyncEnumerableAsync(
            object asyncEnumerable, Type enumerableType, int maxResults)
        {
            var results = new List<VideoSearchResult>();

            try
            {
                // Крок 1: визначаємо тип елемента через інтерфейси конкретного об'єкту
                var asyncEnumInterface = enumerableType.GetInterfaces()
                    .FirstOrDefault(i => i.IsGenericType && i.Name == "IAsyncEnumerable`1");

                Type? itemType = asyncEnumInterface?.GetGenericArguments()[0]
                                 ?? enumerableType.GetGenericArguments().FirstOrDefault();

                if (itemType == null)
                {
                    _diagLog?.Invoke($"[YOUTUBE DIAG] Cannot determine item type from {enumerableType.Name}", "error");
                    return results;
                }
                _diagLog?.Invoke($"[YOUTUBE DIAG] Item type: {itemType.Name}", "info");

                // Крок 2: GetAsyncEnumerator — шукаємо на конкретному типі та його інтерфейсах
                var getEnumeratorMethod =
                    enumerableType.GetMethod("GetAsyncEnumerator",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    ?? enumerableType.GetInterfaces()
                        .SelectMany(i => i.GetMethods())
                        .FirstOrDefault(m => m.Name == "GetAsyncEnumerator");

                if (getEnumeratorMethod == null)
                {
                    _diagLog?.Invoke($"[YOUTUBE DIAG] GetAsyncEnumerator not found", "error");
                    return results;
                }

                var enumerator = getEnumeratorMethod.GetParameters().Length > 0
                    ? getEnumeratorMethod.Invoke(asyncEnumerable, new object[] { CancellationToken.None })
                    : getEnumeratorMethod.Invoke(asyncEnumerable, null);

                if (enumerator == null)
                {
                    _diagLog?.Invoke($"[YOUTUBE DIAG] GetAsyncEnumerator returned null", "error");
                    return results;
                }

                // Крок 3: знаходимо MoveNextAsync і Current на КОНКРЕТНОМУ типі enumerator.
                // Не використовуємо typeof(IAsyncEnumerator<T>) — уникаємо cross-context mismatch.
                var enumeratorConcreteType = enumerator.GetType();
                var allBindings = System.Reflection.BindingFlags.Public
                                | System.Reflection.BindingFlags.NonPublic
                                | System.Reflection.BindingFlags.Instance;

                var moveNextMethod =
                    enumeratorConcreteType.GetMethod("MoveNextAsync", allBindings)
                    ?? enumeratorConcreteType.GetInterfaces()
                        .SelectMany(i => i.GetMethods())
                        .FirstOrDefault(m => m.Name == "MoveNextAsync");

                var currentProp =
                    enumeratorConcreteType.GetProperty("Current", allBindings)
                    ?? enumeratorConcreteType.GetInterfaces()
                        .Select(i => i.GetProperty("Current"))
                        .FirstOrDefault(p => p != null);

                _diagLog?.Invoke($"[YOUTUBE DIAG] Enumerator: {enumeratorConcreteType.Name} | MoveNextAsync: {(moveNextMethod != null ? "OK" : "MISSING")} | Current: {(currentProp != null ? "OK" : "MISSING")}", moveNextMethod != null && currentProp != null ? "info" : "error");

                if (moveNextMethod == null || currentProp == null)
                {
                    // Детальна діагностика якщо не знайдено
                    var ms = enumeratorConcreteType.GetMethods(allBindings).Select(m => m.Name).Distinct();
                    var ps = enumeratorConcreteType.GetProperties(allBindings).Select(p => p.Name);
                    var ifaces = enumeratorConcreteType.GetInterfaces().Select(i => i.Name);
                    _diagLog?.Invoke($"[YOUTUBE DIAG] Methods: {string.Join(", ", ms)}", "info");
                    _diagLog?.Invoke($"[YOUTUBE DIAG] Properties: {string.Join(", ", ps)}", "info");
                    _diagLog?.Invoke($"[YOUTUBE DIAG] Interfaces: {string.Join(", ", ifaces)}", "info");
                    return results;
                }

                // Крок 4: ітеруємо
                _diagLog?.Invoke($"[YOUTUBE DIAG] Starting iteration (max {maxResults})...", "info");

                while (results.Count < maxResults)
                {
                    // MoveNextAsync() → ValueTask<bool>
                    var valueTask = moveNextMethod.Invoke(enumerator, null)!;
                    var vtType = valueTask.GetType();

                    bool hasNext;
                    var asTaskMethod = vtType.GetMethod("AsTask");
                    if (asTaskMethod != null)
                    {
                        hasNext = await ((Task<bool>)asTaskMethod.Invoke(valueTask, null)!).ConfigureAwait(false);
                    }
                    else
                    {
                        var awaiter = vtType.GetMethod("GetAwaiter")!.Invoke(valueTask, null)!;
                        hasNext = (bool)awaiter.GetType().GetMethod("GetResult")!.Invoke(awaiter, null)!;
                    }

                    if (!hasNext) break;

                    var current = currentProp.GetValue(enumerator);
                    if (current == null) continue;

                    try { results.Add(ParseVideoResult(current)); }
                    catch (Exception parseEx)
                    {
                        _diagLog?.Invoke($"[YOUTUBE DIAG] Parse error: {parseEx.Message}", "warning");
                    }
                }

                _diagLog?.Invoke($"[YOUTUBE DIAG] Parsed results (direct iteration): {results.Count}",
                    results.Count > 0 ? "success" : "warning");

                // Dispose enumerator
                try
                {
                    var disposeAsync = enumeratorConcreteType.GetMethod("DisposeAsync", allBindings)
                        ?? enumeratorConcreteType.GetInterfaces()
                            .SelectMany(i => i.GetMethods())
                            .FirstOrDefault(m => m.Name == "DisposeAsync");

                    if (disposeAsync != null)
                    {
                        var vt = disposeAsync.Invoke(enumerator, null);
                        if (vt != null)
                        {
                            var asTask = vt.GetType().GetMethod("AsTask");
                            if (asTask != null) await ((Task)asTask.Invoke(vt, null)!).ConfigureAwait(false);
                        }
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                _diagLog?.Invoke($"[YOUTUBE DIAG] Direct iteration failed: {ex.GetType().Name}: {ex.Message}", "error");
                if (ex.InnerException != null)
                    _diagLog?.Invoke($"[YOUTUBE DIAG] Inner: {ex.InnerException.Message}", "error");
            }

            return results;
        }

        /// <summary>
        /// Отримує URL стріму для відео — адаптивний виклик для різних версій YoutubeExplode
        /// </summary>
        public async Task<VideoStreamInfo?> GetVideoStreamAsync(string videoId, CancellationToken cancellationToken = default)
        {
            if (!IsLoaded)
                throw new InvalidOperationException("YoutubeExplode is not loaded");

            try
            {
                // youtubeClient.Videos.Streams
                var videosProp = _youtubeClientType!.GetProperty("Videos");
                var videosObj = videosProp?.GetValue(_youtubeClient);
                if (videosObj == null)
                {
                    _diagLog?.Invoke("[YOUTUBE DIAG] Videos property not found on YoutubeClient", "error");
                    return null;
                }

                var streamsProp = videosObj.GetType().GetProperty("Streams");
                var streamsObj = streamsProp?.GetValue(videosObj);
                if (streamsObj == null)
                {
                    _diagLog?.Invoke("[YOUTUBE DIAG] Streams property not found on Videos", "error");
                    return null;
                }

                var streamsType = streamsObj.GetType();

                // GetManifestAsync — адаптивний пошук методу
                // У нових версіях параметр може бути VideoId struct, а не string
                var getManifestMethod = streamsType.GetMethods()
                    .FirstOrDefault(m => m.Name == "GetManifestAsync");

                if (getManifestMethod == null)
                {
                    var available = string.Join(", ", streamsType.GetMethods().Select(m => m.Name).Distinct());
                    _diagLog?.Invoke($"[YOUTUBE DIAG] GetManifestAsync not found. Available: {available}", "error");
                    return null;
                }

                // Перший параметр може бути string або VideoId struct
                var firstParam = getManifestMethod.GetParameters().FirstOrDefault();
                _diagLog?.Invoke($"[YOUTUBE DIAG] GetManifestAsync param type: {firstParam?.ParameterType.Name ?? "none"}", "info");

                object videoIdArg;
                if (firstParam?.ParameterType == typeof(string))
                {
                    videoIdArg = videoId;
                }
                else if (firstParam != null)
                {
                    // VideoId struct — створюємо через implicit conversion або конструктор
                    var videoIdType = firstParam.ParameterType;
                    // Спробуємо через Parse або конструктор з string
                    var parseMethod = videoIdType.GetMethod("Parse", new[] { typeof(string) })
                                  ?? videoIdType.GetMethod("op_Implicit", new[] { typeof(string) });
                    if (parseMethod != null)
                    {
                        videoIdArg = parseMethod.Invoke(null, new object[] { videoId })!;
                    }
                    else
                    {
                        // Пробуємо конструктор з string
                        var ctor = videoIdType.GetConstructor(new[] { typeof(string) });
                        if (ctor != null)
                            videoIdArg = ctor.Invoke(new object[] { videoId });
                        else
                        {
                            _diagLog?.Invoke($"[YOUTUBE DIAG] Cannot construct {videoIdType.Name} from string", "error");
                            return null;
                        }
                    }
                }
                else
                {
                    videoIdArg = videoId;
                }

                // Визначаємо чи метод приймає CancellationToken
                var methodParams = getManifestMethod.GetParameters();
                object[] invokeArgs = methodParams.Length >= 2
                    ? new object[] { videoIdArg, cancellationToken }
                    : new object[] { videoIdArg };

                var manifestRaw = getManifestMethod.Invoke(streamsObj, invokeArgs)!;
                var manifestRawType = manifestRaw.GetType();

                // GetManifestAsync може повертати Task<T> або ValueTask<T> залежно від версії
                object? manifest;
                if (manifestRaw is Task manifestTask)
                {
                    // Task<StreamManifest>
                    await manifestTask.ConfigureAwait(false);
                    manifest = manifestTask.GetType().GetProperty("Result")?.GetValue(manifestTask);
                }
                else
                {
                    // ValueTask<StreamManifest> — конвертуємо через AsTask() або GetAwaiter()
                    var asTaskMethod = manifestRawType.GetMethod("AsTask");
                    if (asTaskMethod != null)
                    {
                        var task = (Task)asTaskMethod.Invoke(manifestRaw, null)!;
                        await task.ConfigureAwait(false);
                        manifest = task.GetType().GetProperty("Result")?.GetValue(task);
                    }
                    else
                    {
                        // Fallback: GetAwaiter().GetResult()
                        var awaiter = manifestRawType.GetMethod("GetAwaiter")!.Invoke(manifestRaw, null)!;
                        manifest = awaiter.GetType().GetMethod("GetResult")!.Invoke(awaiter, null);
                    }
                }
                if (manifest == null)
                {
                    _diagLog?.Invoke("[YOUTUBE DIAG] Manifest result is null", "error");
                    return null;
                }

                var manifestType = manifest.GetType();

                // Знаходимо метод для отримання відео-стрімів — назва змінювалась між версіями
                var streamMethod =
                    manifestType.GetMethod("GetVideoOnlyStreams")
                    ?? manifestType.GetMethod("GetVideoStreams")
                    ?? manifestType.GetMethods().FirstOrDefault(m => m.Name.StartsWith("Get") && m.Name.Contains("Video"));

                if (streamMethod == null)
                {
                    var available = string.Join(", ", manifestType.GetMethods().Select(m => m.Name).Distinct());
                    _diagLog?.Invoke($"[YOUTUBE DIAG] No video stream method found. Available: {available}", "error");
                    return null;
                }

                _diagLog?.Invoke($"[YOUTUBE DIAG] Using stream method: {streamMethod.Name}", "info");

                var videoStreams = streamMethod.Invoke(manifest, null) as System.Collections.IEnumerable;
                if (videoStreams == null)
                    return null;

                // Збираємо і фільтруємо стріми
                var streamList = new List<object>();
                foreach (var stream in videoStreams)
                {
                    try
                    {
                        var videoQualityProp = stream.GetType().GetProperty("VideoQuality");
                        var videoQuality = videoQualityProp?.GetValue(stream);
                        var maxHeightProp = videoQuality?.GetType().GetProperty("MaxHeight");
                        if (maxHeightProp == null) continue;
                        var maxHeight = (int)maxHeightProp.GetValue(videoQuality)!;
                        if (maxHeight <= 1080)
                            streamList.Add(stream);
                    }
                    catch { streamList.Add(stream); } // якщо не вдалось отримати якість — додаємо все одно
                }

                _diagLog?.Invoke($"[YOUTUBE DIAG] Found {streamList.Count} video streams", "info");

                if (streamList.Count == 0)
                {
                    // Якщо GetVideoOnlyStreams порожній — пробуємо GetMuxedStreams
                    var muxedMethod = manifestType.GetMethod("GetMuxedStreams");
                    if (muxedMethod != null)
                    {
                        _diagLog?.Invoke("[YOUTUBE DIAG] Trying GetMuxedStreams as fallback...", "info");
                        var muxed = muxedMethod.Invoke(manifest, null) as System.Collections.IEnumerable;
                        if (muxed != null)
                            foreach (var s in muxed) streamList.Add(s);
                        _diagLog?.Invoke($"[YOUTUBE DIAG] Muxed streams: {streamList.Count}", "info");
                    }
                }

                if (streamList.Count == 0)
                    return null;

                // Сортуємо: MP4 краще, потім по якості
                var sortedStreams = streamList.OrderByDescending(s =>
                {
                    try
                    {
                        var cont = s.GetType().GetProperty("Container")?.GetValue(s);
                        var name = cont?.GetType().GetProperty("Name")?.GetValue(cont)?.ToString() ?? "";
                        return name.ToLower() == "mp4" ? 1 : 0;
                    }
                    catch { return 0; }
                }).ThenByDescending(s =>
                {
                    try
                    {
                        var q = s.GetType().GetProperty("VideoQuality")?.GetValue(s);
                        return (int)(q?.GetType().GetProperty("MaxHeight")?.GetValue(q) ?? 0);
                    }
                    catch { return 0; }
                }).ToList();

                var bestStream = sortedStreams[0];

                var url = bestStream.GetType().GetProperty("Url")?.GetValue(bestStream)?.ToString() ?? "";
                var quality = bestStream.GetType().GetProperty("VideoQuality")?.GetValue(bestStream);
                var label = quality?.GetType().GetProperty("Label")?.GetValue(quality)?.ToString() ?? "";
                var container = bestStream.GetType().GetProperty("Container")?.GetValue(bestStream);
                var containerName = container?.GetType().GetProperty("Name")?.GetValue(container)?.ToString() ?? "";
                var height = (int?)quality?.GetType().GetProperty("MaxHeight")?.GetValue(quality) ?? 0;

                _diagLog?.Invoke($"[YOUTUBE DIAG] Selected stream: {containerName} {label} ({height}p)", "success");

                return new VideoStreamInfo
                {
                    Url = url,
                    Quality = label,
                    Container = containerName,
                    Height = height
                };
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException ?? ex;
                _diagLog?.Invoke($"[YOUTUBE DIAG] GetVideoStreamAsync TargetInvocationException: {inner.GetType().Name}: {inner.Message}", "error");
                throw inner;
            }
            catch (Exception ex)
            {
                _diagLog?.Invoke($"[YOUTUBE DIAG] GetVideoStreamAsync failed: {ex.GetType().Name}: {ex.Message}", "error");
                throw;
            }
        }

        /// <summary>
        /// Тестує чи працює YouTube API — перевіряє ПОВНИЙ цикл: пошук + отримання стріму.
        /// Раніше тестувався лише пошук, що давало хибно-позитивний результат:
        /// пошук працює, але GetManifestAsync повертає 403 — оновлювач вважав що "все ок"
        /// і ніколи не доходив до завантаження нової версії бібліотеки.
        /// </summary>
        public async Task<bool> TestConnectionAsync()
        {
            // ВИПРАВЛЕНО: обидва CancellationTokenSource тепер звільняються через using —
            // раніше вони витікали (кожен утримує kernel handle таймера ОС).
            try
            {
                // Крок 1: пошук відео
                List<VideoSearchResult> searchResults;
                using (var cts = new CancellationTokenSource(15000))
                {
                    searchResults = await SearchVideosAsync("test", 1, cts.Token);
                }

                if (searchResults.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[YOUTUBE TEST] Search returned no results");
                    return false;
                }

                // Крок 2: отримання stream URL — саме тут YouTube повертає 403
                // якщо API зламано, хоча пошук ще працює
                var videoId = searchResults[0].VideoId;
                VideoStreamInfo? streamInfo;
                using (var cts2 = new CancellationTokenSource(15000))
                {
                    streamInfo = await GetVideoStreamAsync(videoId, cts2.Token);
                }

                return streamInfo != null && !string.IsNullOrEmpty(streamInfo.Url);
            }
            catch (Exception ex)
            {
                var detailedMessage = $"{ex.GetType().Name}: {ex.Message}";
                if (ex.InnerException != null)
                {
                    detailedMessage += $" -> {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
                }
                System.Diagnostics.Debug.WriteLine($"[YOUTUBE TEST] Connection test failed: {detailedMessage}");
                return false;
            }
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                UnloadYoutubeExplode();
                _isDisposed = true;
            }
        }
    }
}