using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace WallpaperMusicPlayer
{
    /// <summary>
    /// Автоматичне оновлення YoutubeExplode.dll без перезапуску додатка
    /// </summary>
    public static class YoutubeDllUpdater
    {
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private static DateTime _lastUpdateCheck = DateTime.MinValue;
        private static readonly TimeSpan UpdateCheckCooldown = TimeSpan.FromHours(1);

        /// <summary>
        /// Перевіряє чи працює YouTube API та оновлює DLL якщо потрібно
        /// </summary>
        public static async Task<UpdateResult> CheckAndUpdateAsync(YoutubeWrapper wrapper, Action<string> logCallback)
        {
            try
            {
                // Перевіряємо наявність критичних залежностей — якщо відсутні,
                // пропускаємо cooldown і одразу завантажуємо
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var missingDeps = new[] { "CliWrap.dll", "AngleSharp.dll" }
                    .Where(d => !File.Exists(Path.Combine(baseDir, d)))
                    .ToList();

                if (missingDeps.Count > 0)
                {
                    logCallback($"[UPDATE] Missing dependencies: {string.Join(", ", missingDeps)} — downloading...");
                    string currentVer = GetCurrentVersion();
                    await DownloadDependenciesAsync(
                        // визначаємо target framework по версії .NET рантайму
                        $"net{System.Environment.Version.Major}.0",
                        baseDir,
                        logCallback);

                    // Перезавантажуємо wrapper щоб підхопити нові DLL
                    wrapper.ReloadYoutubeExplode();

                    bool worksAfterDepFix = await wrapper.TestConnectionAsync();
                    if (worksAfterDepFix)
                    {
                        logCallback("[UPDATE] ✓ Dependencies fixed, API working!");
                        return UpdateResult.Success;
                    }
                    logCallback("[UPDATE] Dependencies downloaded but API still fails — will try full update");
                }

                // Перевіряємо чи не занадто часто перевіряємо оновлення
                if (DateTime.Now - _lastUpdateCheck < UpdateCheckCooldown)
                {
                    logCallback("[UPDATE] Cooldown active, skipping check");
                    return UpdateResult.SkippedCooldown;
                }

                _lastUpdateCheck = DateTime.Now;

                // Тестуємо чи працює поточна версія
                logCallback("[UPDATE] Testing YouTube API...");
                bool isWorking = await wrapper.TestConnectionAsync();

                if (isWorking)
                {
                    logCallback("[UPDATE] YouTube API is working fine");
                    return UpdateResult.NoUpdateNeeded;
                }

                // API не працює - треба оновити
                logCallback("[UPDATE] YouTube API failed, checking for updates...");

                string currentVersion = GetCurrentVersion();
                logCallback($"[UPDATE] Current version: {currentVersion}");

                string? latestVersion = await GetLatestVersionAsync();
                if (latestVersion == null)
                {
                    logCallback("[UPDATE] Failed to get latest version from NuGet");
                    return UpdateResult.Failed;
                }

                logCallback($"[UPDATE] Latest version: {latestVersion}");

                if (currentVersion == latestVersion)
                {
                    logCallback("[UPDATE] Already on latest version, but API still fails");
                    return UpdateResult.ApiFailedOnLatest;
                }

                // Оновлюємо DLL
                logCallback("[UPDATE] Downloading new version...");
                bool downloadSuccess = await DownloadAndReplaceDllAsync(latestVersion, logCallback);

                if (!downloadSuccess)
                {
                    logCallback("[UPDATE] Failed to download/replace DLL");
                    return UpdateResult.Failed;
                }

                // Перезавантажуємо DLL
                logCallback("[UPDATE] Reloading YoutubeExplode.dll...");
                wrapper.ReloadYoutubeExplode();

                // Перевіряємо чи працює після оновлення
                logCallback("[UPDATE] Testing updated API...");
                bool worksAfterUpdate = await wrapper.TestConnectionAsync();

                if (worksAfterUpdate)
                {
                    logCallback("[UPDATE] ✓ Successfully updated and verified!");
                    return UpdateResult.Success;
                }
                else
                {
                    logCallback("[UPDATE] ✗ Update completed but API still fails");
                    return UpdateResult.UpdatedButStillFails;
                }
            }
            catch (Exception ex)
            {
                logCallback($"[UPDATE ERROR] {ex.Message}");
                return UpdateResult.Failed;
            }
        }

        /// <summary>
        /// Отримує поточну версію YoutubeExplode.dll
        /// </summary>
        private static string GetCurrentVersion()
        {
            try
            {
                string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "YoutubeExplode.dll");
                if (!File.Exists(dllPath))
                    return "0.0.0";

                var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(dllPath);
                return versionInfo.FileVersion ?? "0.0.0";
            }
            catch
            {
                return "0.0.0";
            }
        }

        /// <summary>
        /// Отримує останню версію YoutubeExplode з NuGet API
        /// </summary>
        private static async Task<string?> GetLatestVersionAsync()
        {
            try
            {
                string indexUrl = "https://api.nuget.org/v3-flatcontainer/youtubeexplode/index.json";
                string json = await _httpClient.GetStringAsync(indexUrl);

                var document = JsonDocument.Parse(json);
                var versions = document.RootElement.GetProperty("versions");

                var versionList = new System.Collections.Generic.List<string>();
                foreach (var version in versions.EnumerateArray())
                {
                    string? versionStr = version.GetString();
                    if (versionStr != null)
                        versionList.Add(versionStr);
                }

                // Повертаємо останню версію
                return versionList.LastOrDefault();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to get latest version: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Завантажує та заміняє YoutubeExplode.dll
        /// </summary>
        private static async Task<bool> DownloadAndReplaceDllAsync(string version, Action<string> logCallback)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "yt_update_" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(tempDir);

                // Завантажуємо .nupkg пакет YoutubeExplode
                string nupkgUrl = $"https://api.nuget.org/v3-flatcontainer/youtubeexplode/{version}/youtubeexplode.{version}.nupkg";
                logCallback($"[UPDATE] Downloading from: {nupkgUrl}");

                byte[] nupkgData = await _httpClient.GetByteArrayAsync(nupkgUrl);
                string nupkgPath = Path.Combine(tempDir, "package.nupkg");
                await File.WriteAllBytesAsync(nupkgPath, nupkgData);

                logCallback($"[UPDATE] Downloaded {nupkgData.Length / 1024} KB");

                // Розпаковуємо
                string extractDir = Path.Combine(tempDir, "extracted");
                ZipFile.ExtractToDirectory(nupkgPath, extractDir);

                // Логуємо всі знайдені DLL кандидати для діагностики
                var allDlls = Directory.GetFiles(extractDir, "YoutubeExplode.dll", SearchOption.AllDirectories);
                logCallback($"[UPDATE] Found {allDlls.Length} DLL(s) in package: {string.Join(", ", allDlls.Select(p => Path.GetFileName(Path.GetDirectoryName(p))))}");

                // Шукаємо DLL для .NET — від новіших до старіших
                var dllCandidates = allDlls
                    .Where(p =>
                        p.Contains("net9.0") ||
                        p.Contains("net8.0") ||
                        p.Contains("net7.0") ||
                        p.Contains("net6.0") ||
                        p.Contains("netstandard2.1") ||
                        p.Contains("netstandard2.0"))
                    .OrderByDescending(p => p.Contains("net9.0") ? 6 :
                                            p.Contains("net8.0") ? 5 :
                                            p.Contains("net7.0") ? 4 :
                                            p.Contains("net6.0") ? 3 :
                                            p.Contains("netstandard2.1") ? 2 : 1)
                    .ToList();

                if (dllCandidates.Count == 0)
                {
                    logCallback("[UPDATE] No compatible DLL found in package");
                    return false;
                }

                string sourceDll = dllCandidates[0];
                string selectedTarget = Path.GetFileName(Path.GetDirectoryName(sourceDll))!;
                logCallback($"[UPDATE] Selected DLL target: {selectedTarget}");

                // Заміняємо YoutubeExplode.dll
                string targetDir = AppDomain.CurrentDomain.BaseDirectory;
                string targetDll = Path.Combine(targetDir, "YoutubeExplode.dll");
                string backupDll = Path.Combine(targetDir, "YoutubeExplode.dll.backup");

                if (File.Exists(targetDll))
                {
                    if (File.Exists(backupDll))
                        File.Delete(backupDll);
                    File.Move(targetDll, backupDll);
                    logCallback("[UPDATE] Backed up old DLL");
                }

                File.Copy(sourceDll, targetDll, true);
                logCallback("[UPDATE] Replaced DLL successfully");

                // Завантажуємо залежності окремо з NuGet —
                // вони не входять до .nupkg YoutubeExplode, а є окремими пакетами
                await DownloadDependenciesAsync(selectedTarget, targetDir, logCallback);

                return true;
            }
            catch (Exception ex)
            {
                logCallback($"[UPDATE ERROR] Download/replace failed: {ex.Message}");

                // Спроба відновлення з backup
                string targetDll = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "YoutubeExplode.dll");
                string backupDll = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "YoutubeExplode.dll.backup");

                if (File.Exists(backupDll) && !File.Exists(targetDll))
                {
                    File.Move(backupDll, targetDll);
                    logCallback("[UPDATE] Restored from backup");
                }

                return false;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                }
                catch { }
            }
        }

        /// <summary>
        /// Завантажує залежності YoutubeExplode (AngleSharp, CliWrap) напряму з NuGet.
        /// Ці бібліотеки є окремими пакетами і не входять до .nupkg YoutubeExplode.
        /// </summary>
        private static async Task DownloadDependenciesAsync(string targetFramework, string outputDir, Action<string> logCallback)
        {
            // Відомі залежності YoutubeExplode та їх NuGet package id
            var dependencies = new[]
            {
                ("AngleSharp",  "AngleSharp.dll"),
                ("CliWrap",     "CliWrap.dll"),
            };

            foreach (var (packageId, dllName) in dependencies)
            {
                string tempDir = Path.Combine(Path.GetTempPath(), $"dep_{packageId}_{Guid.NewGuid():N}");
                try
                {
                    // Отримуємо останню версію пакету
                    string indexUrl = $"https://api.nuget.org/v3-flatcontainer/{packageId.ToLower()}/index.json";
                    string json = await _httpClient.GetStringAsync(indexUrl);
                    var document = JsonDocument.Parse(json);
                    var latestVersion = document.RootElement
                        .GetProperty("versions")
                        .EnumerateArray()
                        .Select(v => v.GetString())
                        .LastOrDefault(v => v != null && !v.Contains("-")); // лише стабільні

                    if (latestVersion == null)
                    {
                        logCallback($"[UPDATE WARNING] Could not find stable version for {packageId}");
                        continue;
                    }

                    logCallback($"[UPDATE] Downloading dependency {packageId} v{latestVersion}...");

                    string nupkgUrl = $"https://api.nuget.org/v3-flatcontainer/{packageId.ToLower()}/{latestVersion}/{packageId.ToLower()}.{latestVersion}.nupkg";
                    byte[] data = await _httpClient.GetByteArrayAsync(nupkgUrl);

                    Directory.CreateDirectory(tempDir);
                    string nupkgPath = Path.Combine(tempDir, "dep.nupkg");
                    await File.WriteAllBytesAsync(nupkgPath, data);

                    string extractDir = Path.Combine(tempDir, "extracted");
                    ZipFile.ExtractToDirectory(nupkgPath, extractDir);

                    // Шукаємо DLL — спочатку той самий target що й YoutubeExplode, потім нижчі
                    var frameworkPriority = new[] { targetFramework, "net8.0", "net7.0", "net6.0", "netstandard2.1", "netstandard2.0" };
                    var candidates = Directory.GetFiles(extractDir, dllName, SearchOption.AllDirectories);

                    logCallback($"[UPDATE] {packageId}: found {candidates.Length} candidate(s): {string.Join(", ", candidates.Select(p => Path.GetFileName(Path.GetDirectoryName(p))))}");

                    string? bestCandidate = null;
                    foreach (var fw in frameworkPriority)
                    {
                        bestCandidate = candidates.FirstOrDefault(p => p.Contains(fw));
                        if (bestCandidate != null) break;
                    }

                    // Якщо жоден target не збігся — беремо перший доступний
                    if (bestCandidate == null)
                        bestCandidate = candidates.FirstOrDefault();

                    if (bestCandidate != null)
                    {
                        string dest = Path.Combine(outputDir, dllName);
                        File.Copy(bestCandidate, dest, overwrite: true);
                        logCallback($"[UPDATE] Copied {dllName} from {Path.GetFileName(Path.GetDirectoryName(bestCandidate))}");
                    }
                    else
                    {
                        logCallback($"[UPDATE WARNING] {dllName} not found in {packageId} package");
                    }
                }
                catch (Exception ex)
                {
                    logCallback($"[UPDATE WARNING] Failed to download {packageId}: {ex.Message}");
                }
                finally
                {
                    try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        /// <summary>
        /// Очищує старі backup файли
        /// </summary>
        public static void CleanupBackups()
        {
            try
            {
                string backupPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "YoutubeExplode.dll.backup");
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Результат перевірки оновлень
    /// </summary>
    public enum UpdateResult
    {
        Success,                // Успішно оновлено
        NoUpdateNeeded,         // Оновлення не потрібне, все працює
        SkippedCooldown,        // Пропущено через cooldown
        Failed,                 // Помилка при оновленні
        ApiFailedOnLatest,      // API не працює навіть на останній версії
        UpdatedButStillFails    // Оновлено, але API все ще не працює
    }
}