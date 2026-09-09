// OptiScaler Client - A frontend for managing OptiScaler installations
// Copyright (C) 2026 Agustín Montaña (Agustinm28)
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using SharpCompress.Archives;
using SharpCompress.Common;
using OptiscalerManager.Core.Models;
using OptiscalerManager.Core.Prompts;

namespace OptiscalerManager.Core.Services
{
    /// <summary>
    /// Manages OptiScaler, Fakenvapi, and NukemFG components
    /// </summary>
    public class ComponentManagementService
    {
        private static readonly object _downloadLock = new();
        private static readonly System.Collections.Generic.HashSet<string> _activeOptiDownloads = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _configLock = new();
        private static AppConfiguration? _sharedConfig;
        private readonly string _baseDir;
        private readonly string _cacheDir;
        private readonly string _versionFile;
        private readonly string _configFile;
        private readonly string _releasesCacheFile;
        private HttpClient _httpClient => NetworkService.GetHttpClient();

        public AppConfiguration Config => _config;
        private AppConfiguration _config = new();
        private ComponentVersions _localVersions = new();
        private ComponentVersions _remoteVersions = new();

        private static System.Collections.Generic.List<string>? _cachedOptiScalerVersions = null;
        private static System.Collections.Generic.HashSet<string> _cachedBetaVersions = new();
        private static string? _cachedLatestBetaVersion = null;
        private static string? _cachedLatestStableVersion = null;
        private static string? _cachedFakenvapiVersion = null;
        private static string? _cachedNukemFGVersion = null;
        private static DateTime _lastApiCheckTime = DateTime.MinValue;
        // Allows only one CheckForUpdatesAsync to run at a time across all instances.
        // If a check is already in-flight, subsequent callers wait for it to finish
        // rather than launching concurrent GitHub API requests.
        private static readonly System.Threading.SemaphoreSlim _checkSemaphore = new(1, 1);
        // Persistent local cache of release metadata (version names + download URLs)
        private static OptiScalerReleasesCache _releasesCache = new();
        // Persistent local cache of OptiScaler Extras (FSR4 INT8 mod) release metadata
        private static ExtrasReleasesCache _extrasCache = new();
        private static System.Collections.Generic.List<string>? _cachedExtrasVersions = null;
        private static string? _cachedLatestExtrasVersion = null;
        // Persistent local cache of OptiPatcher release metadata
        private static OptiPatcherReleasesCache _optiPatcherCache = new();
        private static System.Collections.Generic.List<string>? _cachedOptiPatcherVersions = null;
        private static string? _cachedLatestOptiPatcherVersion = null;
        // Persistent local cache of Fakenvapi release metadata
        private static FakenvapiReleasesCache _fakenvapiCache = new();
        private static System.Collections.Generic.List<string>? _cachedFakenvapiVersions = null;
        private static string? _cachedLatestFakenvapiVersion = null;

        public System.Collections.Generic.List<string> OptiScalerAvailableVersions
        {
            get
            {
                var baseList = _cachedOptiScalerVersions ?? GetDownloadedOptiScalerVersions();
                var custom = _config.CustomOptiScalerVersions;
                if (custom.Count == 0) return baseList;
                var merged = new System.Collections.Generic.List<string>(baseList);
                foreach (var cv in custom)
                    if (!merged.Contains(cv, StringComparer.OrdinalIgnoreCase))
                        merged.Add(cv);
                return merged;
            }
        }
        public System.Collections.Generic.HashSet<string> BetaVersions => _cachedBetaVersions;
        public string? LatestBetaVersion => _cachedLatestBetaVersion;
        public string? LatestStableVersion => _cachedLatestStableVersion;

        /// <summary>All available OptiScaler Extras (FSR4 INT8 mod) versions from the remote cache.</summary>
        public System.Collections.Generic.List<string> ExtrasAvailableVersions
            => _cachedExtrasVersions ?? new System.Collections.Generic.List<string>();
        /// <summary>The latest (first) Extras version tag, or null if none fetched yet.</summary>
        public string? LatestExtrasVersion => _cachedLatestExtrasVersion;

        /// <summary>
        /// Available Extras releases with their upstream pre-release flag, so the UI can
        /// mark builds that their author has not declared stable.
        /// </summary>
        public System.Collections.Generic.List<ExtrasReleaseEntry> ExtrasAvailableReleases
            => _extrasCache.Releases;

        public System.Collections.Generic.List<string> ExtrasDownloadedVersions
            => GetDownloadedExtrasVersions();

        /// <summary>All available OptiPatcher versions from the remote cache.</summary>
        public System.Collections.Generic.List<string> OptiPatcherAvailableVersions
            => _cachedOptiPatcherVersions ?? new System.Collections.Generic.List<string>();
        /// <summary>The latest OptiPatcher version tag, or null if none fetched yet.</summary>
        public string? LatestOptiPatcherVersion => _cachedLatestOptiPatcherVersion;

        /// <summary>All available Fakenvapi versions from the remote cache.</summary>
        public System.Collections.Generic.List<string> FakenvapiAvailableVersions
            => _cachedFakenvapiVersions ?? new System.Collections.Generic.List<string>();
        /// <summary>The latest Fakenvapi version tag, or null if none fetched yet.</summary>
        public string? LatestFakenvapiVersion => _cachedLatestFakenvapiVersion;

        public string? OptiScalerVersion => _localVersions.OptiScalerVersion;
        public string? FakenvapiVersion => _localVersions.FakenvapiVersion;
        public string? NukemFGVersion => _localVersions.NukemFGVersion;

        /// <summary>Latest Nukem FG version tag known from its repo (null until a check runs).</summary>
        public string? LatestNukemFGVersion => _cachedNukemFGVersion;

        public bool IsOptiScalerUpdateAvailable { get; private set; }
        public bool IsFakenvapiUpdateAvailable { get; private set; }
        public bool IsNukemFGUpdateAvailable { get; private set; }

        /// <summary>
        /// True if the NukemFG DLL is present in local cache.
        /// </summary>
        public bool IsNukemFGInstalled => File.Exists(GetNukemFGDllPath());

        public event Action? OnStatusChanged;
        public Exception? LastError { get; private set; }

        // Host-supplied callback used for components that cannot be downloaded
        // automatically (currently only Nukem's DLSSG-to-FSR3 mod). Defaults to a
        // provider that declines every request so headless / test runs never block.
        private readonly IManualComponentProvider _manualProvider;

        public ComponentManagementService() : this(null) { }

        public ComponentManagementService(IManualComponentProvider? manualProvider)
        {
            _manualProvider = manualProvider ?? new NullManualComponentProvider();
            _baseDir = AppDataPaths.Root;
            _cacheDir = AppDataPaths.Cache;
            _versionFile = Path.Combine(_baseDir, "versions.json");
            _configFile = Path.Combine(_baseDir, "config.json");
            _releasesCacheFile = Path.Combine(_baseDir, "releases_cache.json");

            Directory.CreateDirectory(_cacheDir);

            LoadConfiguration();
            NetworkService.Configure(_config.Network);
            LoadLocalVersions();
            LoadReleasesCache();
            LoadExtrasCache();
            LoadOptiPatcherCache();
            LoadFakenvapiCache();
        }

        private void LoadConfiguration()
        {
            try
            {
                lock (_configLock)
                {
                    if (_sharedConfig != null)
                    {
                        _config = _sharedConfig;
                        return;
                    }

                    // PRIORITY 1: Load from AppData (persistent user settings)
                    if (File.Exists(_configFile))
                    {
                        var json = File.ReadAllText(_configFile);
                        _config = JsonSerializer.Deserialize(json, OptimizerContext.Default.AppConfiguration) ?? new();
                        System.Diagnostics.Debug.WriteLine($"[Config] Loaded from AppData: {_configFile}");

                        // If core repos are empty (e.g. config was generated with blank defaults),
                        // merge them from the install-dir template so the app stays functional.
                        // Also re-merge if any individual repo is missing (e.g. OptiPatcher added in a later version).
                        bool needsMerge = string.IsNullOrEmpty(_config.OptiScaler.RepoOwner)
                                       || string.IsNullOrEmpty(_config.OptiPatcher.RepoOwner);
                        if (needsMerge)
                        {
                            MergeReposFromTemplate(_config);
                            try
                            {
                                var normalized = JsonSerializer.Serialize(_config, OptimizerContext.Default.AppConfiguration);
                                File.WriteAllText(_configFile, normalized);
                            }
                            catch (Exception ex)
                            {
                                Log.Write($"[Config] Failed to save normalized config: {ex.Message}");
                            }
                        }
                    }
                    // No AppData config exists yet — seed from the install-dir config.json.
                    // That file is the developer-maintained template with repo configs,
                    // scan exclusions, etc. User preferences edited later are saved back
                    // to AppData and the install-dir file is never read again.
                    else
                    {
                        _config = new AppConfiguration();
                        MergeReposFromTemplate(_config);

                        // Persist to AppData — this is the only time the install-dir file is read.
                        try
                        {
                            var normalized = JsonSerializer.Serialize(_config, OptimizerContext.Default.AppConfiguration);
                            File.WriteAllText(_configFile, normalized);
                        }
                        catch (Exception ex)
                        {
                            Log.Write($"[Config] Failed to persist initial config: {ex.Message}");
                        }
                    }

                    // The App repo is written to the user's config on first run and never
                    // refreshed from the template, so a repository rename would leave every
                    // existing install querying a name we no longer publish to. Retarget
                    // whenever the stored name is one we have moved away from.
                    if (AppRepository.NeedsRetargeting(_config.App))
                    {
                        _config.App = AppRepository.Current;
                        try
                        {
                            var json = JsonSerializer.Serialize(_config, OptimizerContext.Default.AppConfiguration);
                            File.WriteAllText(_configFile, json);
                            Log.Write($"[Config] Retargeted App update repo to {AppRepository.Current.RepoOwner}/{AppRepository.Current.RepoName}.");
                        }
                        catch (Exception ex)
                        {
                            Log.Write($"[Config] Failed to persist App repo migration: {ex.Message}");
                        }
                    }

                    _sharedConfig = _config;
                }
            }
            catch (Exception ex)
            {
                Log.Write($"[Config] Failed to load configuration, using defaults: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads the install-dir config.json (if present) and copies any non-empty
        /// RepositoryConfig values into <paramref name="target"/>. User preferences
        /// (language, debug, window state, etc.) already in target are left untouched.
        /// </summary>
        private static void MergeReposFromTemplate(AppConfiguration target)
        {
            try
            {
                var currentDirConfig = Path.Combine(Environment.CurrentDirectory, "config.json");
                var baseDirConfig    = Path.Combine(AppContext.BaseDirectory, "config.json");
                var templatePath     = File.Exists(currentDirConfig) ? currentDirConfig
                                     : File.Exists(baseDirConfig)    ? baseDirConfig
                                     : null;

                if (templatePath == null) return;

                var json     = File.ReadAllText(templatePath);
                var template = JsonSerializer.Deserialize(json, OptimizerContext.Default.AppConfiguration);
                if (template == null) return;

                if (!string.IsNullOrEmpty(template.App.RepoOwner))            target.App            = template.App;
                if (!string.IsNullOrEmpty(template.OptiScaler.RepoOwner))     target.OptiScaler     = template.OptiScaler;
                if (!string.IsNullOrEmpty(template.OptiScalerBetas.RepoOwner))target.OptiScalerBetas= template.OptiScalerBetas;
                if (!string.IsNullOrEmpty(template.OptiScalerExtras.RepoOwner))target.OptiScalerExtras = template.OptiScalerExtras;
                if (!string.IsNullOrEmpty(template.Fakenvapi.RepoOwner))      target.Fakenvapi      = template.Fakenvapi;
                if (!string.IsNullOrEmpty(template.NukemFG.RepoOwner))        target.NukemFG        = template.NukemFG;
                if (!string.IsNullOrEmpty(template.OptiPatcher.RepoOwner))    target.OptiPatcher    = template.OptiPatcher;

                if (target.ScanExclusions.Count == 0 && template.ScanExclusions.Count > 0)
                    target.ScanExclusions = template.ScanExclusions;
            }
            catch (Exception ex)
        {
            Log.Write($"[Config] Failed to merge repos from template: {ex.Message}");
        }
        }

        public void SaveConfiguration()
        {            try
            {
                lock (_configLock)
                {
                    var json = JsonSerializer.Serialize(_config, OptimizerContext.Default.AppConfiguration);
                    File.WriteAllText(_configFile, json);
                    System.Diagnostics.Debug.WriteLine($"[Config] Saved to: {_configFile}");
                    System.Diagnostics.Debug.WriteLine($"[Config] WindowMaximized: {_config.WindowMaximized}, PreferGridView: {_config.PreferGridView}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Config] Save error: {ex.Message}");
            }
        }

        private void LoadLocalVersions()
        {
            if (File.Exists(_versionFile))
            {
                try
                {
                    var json = File.ReadAllText(_versionFile);
                    _localVersions = JsonSerializer.Deserialize(json, OptimizerContext.Default.ComponentVersions) ?? new();
                }
                catch (Exception ex) { Log.Write($"[Config] Corrupt versions file: {ex.Message}"); }
            }
        }

        private void SaveLocalVersions()
        {
            try
            {
                var json = JsonSerializer.Serialize(_localVersions, OptimizerContext.Default.ComponentVersions);
                File.WriteAllText(_versionFile, json);
            }
            catch (Exception ex) { Log.Write($"[Config] Failed to save local versions: {ex.Message}"); }
        }

        // ── Release-cache persistence ────────────────────────────────────────────

        /// <summary>
        /// Reads one of the release caches from disk, or null when it is missing or
        /// unreadable. A corrupt cache is never fatal: we log it and refetch.
        ///
        /// The caches live in static fields, so each caller checks whether its own is
        /// already populated first and reads the file at most once per process.
        /// </summary>
        private T? ReadReleasesCache<T>(string fileName, string label, JsonTypeInfo<T> typeInfo)
            where T : class, IReleasesCacheFile
        {
            var file = Path.Combine(_baseDir, fileName);
            if (!File.Exists(file)) return null;
            try
            {
                var loaded = JsonSerializer.Deserialize(File.ReadAllText(file), typeInfo);
                if (loaded is not null)
                    Log.Write($"[{label}] Loaded {loaded.ReleaseCount} entries from local cache.");
                return loaded;
            }
            catch (Exception ex)
            {
                Log.Write($"[{label}] Failed to load: {ex.Message}");
                return null;
            }
        }

        /// <summary>Writes a release cache back to disk. Failing to save is not fatal either.</summary>
        private void WriteReleasesCache<T>(string fileName, string label, JsonTypeInfo<T> typeInfo, T cache)
            where T : class, IReleasesCacheFile
        {
            try
            {
                File.WriteAllText(Path.Combine(_baseDir, fileName), JsonSerializer.Serialize(cache, typeInfo));
                Log.Write($"[{label}] Saved {cache.ReleaseCount} entries.");
            }
            catch (Exception ex)
            {
                Log.Write($"[{label}] Failed to save: {ex.Message}");
            }
        }

        private void LoadReleasesCache()
        {
            if (_releasesCache.Releases.Count > 0) return;
            if (ReadReleasesCache("optiscaler_releases_cache.json", "ReleasesCache", OptimizerContext.Default.OptiScalerReleasesCache) is not { } loaded) return;
            _releasesCache = loaded;
            RebuildInMemoryCacheFromReleases();
        }

        private void SaveReleasesCache()
            => WriteReleasesCache("optiscaler_releases_cache.json", "ReleasesCache", OptimizerContext.Default.OptiScalerReleasesCache, _releasesCache);

        // ── Extras (FSR4 INT8) cache ──────────────────────────────────────────────

        private void LoadExtrasCache()
        {
            if (_extrasCache.Releases.Count > 0) return;
            if (ReadReleasesCache("extras_cache.json", "ExtrasCache", OptimizerContext.Default.ExtrasReleasesCache) is not { } loaded) return;
            _extrasCache = loaded;
            RebuildInMemoryExtrasCache();
        }

        private void SaveExtrasCache()
            => WriteReleasesCache("extras_cache.json", "ExtrasCache", OptimizerContext.Default.ExtrasReleasesCache, _extrasCache);

        /// <summary>
        /// The two things the UI wants from a single-stream component's release list: which
        /// version is newest, and every version it could offer, newest first.
        ///
        /// Returns null for an empty list so callers leave the last known "latest" alone —
        /// a failed refresh should not erase what we already knew.
        /// </summary>
        private static (string? Latest, System.Collections.Generic.List<string> Versions)? SummariseReleases<T>(
            System.Collections.Generic.List<T>? releases, string label) where T : IReleaseEntry
        {
            if (releases is null || releases.Count == 0) return null;

            var latest = releases.FirstOrDefault(r => r.IsLatest)?.Version ?? releases[0].Version;
            var versions = VersionOrder.Newest(releases.Select(r => r.Version));
            Log.Write($"[{label}] Rebuilt in-memory: {versions.Count} version(s), latest={latest}");
            return (latest, versions);
        }

        private void RebuildInMemoryExtrasCache()
        {
            if (SummariseReleases(_extrasCache.Releases, "ExtrasCache") is not { } summary)
            {
                _cachedExtrasVersions = new System.Collections.Generic.List<string>();
                return;
            }
            (_cachedLatestExtrasVersion, _cachedExtrasVersions) = summary;
        }

        /// <summary>
        /// Merges newly fetched release entries into the persistent cache.
        /// Adds any versions not already present; never removes existing ones.
        /// </summary>
        private void MergeIntoReleasesCache(System.Collections.Generic.IEnumerable<OptiScalerReleaseEntry> newEntries)
        {
            var existingVersions = new System.Collections.Generic.HashSet<string>(
                _releasesCache.Releases.Select(r => r.Version),
                StringComparer.OrdinalIgnoreCase);

            // Reset latest flags before updating
            foreach (var existing in _releasesCache.Releases)
            {
                existing.IsLatestStable = false;
                existing.IsLatestBeta = false;
            }

            foreach (var entry in newEntries)
            {
                if (!existingVersions.Contains(entry.Version))
                {
                    _releasesCache.Releases.Add(entry);
                    existingVersions.Add(entry.Version);
                }
                else
                {
                    // Update download URL and flags for existing entry if missing
                    var existing = _releasesCache.Releases.FirstOrDefault(
                        r => string.Equals(r.Version, entry.Version, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        if (string.IsNullOrEmpty(existing.DownloadUrl))
                            existing.DownloadUrl = entry.DownloadUrl;
                        existing.IsLatestStable = entry.IsLatestStable;
                        existing.IsLatestBeta = entry.IsLatestBeta;
                        existing.IsBeta = entry.IsBeta;
                    }
                }
            }

            _releasesCache.LastUpdated = DateTime.Now;
        }

        /// <summary>
        /// Rebuilds the static in-memory version lists from the persistent releases cache.
        /// </summary>
        private void RebuildInMemoryCacheFromReleases()
        {
            if (_releasesCache.Releases.Count == 0) return;

            var all = _releasesCache.Releases;

            var stablesList = all.Where(r => !r.IsBeta)
                                 .OrderBy(r => r.Version, VersionOrder.Descending)
                                 .ToList();

            var betasList = all.Where(r => r.IsBeta)
                               .OrderBy(r => r.Version, VersionOrder.Descending)
                               .ToList();

            _cachedBetaVersions = new System.Collections.Generic.HashSet<string>(
                betasList.Select(r => r.Version), StringComparer.OrdinalIgnoreCase);

            _cachedLatestBetaVersion = all.FirstOrDefault(r => r.IsLatestBeta)?.Version
                ?? betasList.FirstOrDefault()?.Version;

            _cachedLatestStableVersion = all.FirstOrDefault(r => r.IsLatestStable)?.Version
                ?? stablesList.FirstOrDefault()?.Version;

            // Stable versions first (highest to lowest), then betas (highest to lowest)
            var merged = new System.Collections.Generic.List<string>();
            merged.AddRange(stablesList.Select(r => r.Version));
            merged.AddRange(betasList.Select(r => r.Version));

            if (merged.Count > 0)
                _cachedOptiScalerVersions = merged.Distinct().ToList();

            Log.Write($"[ReleasesCache] Rebuilt in-memory cache: {stablesList.Count} stable + {betasList.Count} beta versions");
        }

        // ── Download helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Executes an HTTP GET with per-attempt timeout and exponential-backoff retries on
        /// transient network errors. Does NOT retry on HTTP error status codes (e.g. 404).
        /// </summary>
        private static async Task<HttpResponseMessage> GetWithRetryAsync(
            Func<HttpClient> getClient, string url,
            int maxRetries = 3, int timeoutSeconds = 30,
            CancellationToken cancellationToken = default)
        {
            int[] backoff = { 1000, 3000, 7000 };
            Exception? lastEx = null;
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                try
                {
                    var resp = await getClient().GetAsync(url, cts.Token);
                    if ((int)resp.StatusCode == 403)
                        throw new GitHubRateLimitException();
                    return resp;
                }
                catch (GitHubRateLimitException)
                {
                    throw; // propagate immediately, no retry
                }
                catch (Exception ex) when (ex is HttpRequestException
                    || ex is ObjectDisposedException  // HttpClient replaced mid-flight; retry picks up the new client
                    || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    lastEx = ex is OperationCanceledException
                        ? new TimeoutException($"Request timed out after {timeoutSeconds}s (attempt {attempt + 1})")
                        : ex;
                    Log.Write($"[HTTP] Attempt {attempt + 1}/{maxRetries + 1} failed for {url}: {lastEx.Message}");
                }
                if (attempt < maxRetries)
                    await Task.Delay(backoff[Math.Min(attempt, backoff.Length - 1)], cancellationToken);
            }
            throw lastEx!;
        }

        /// <summary>
        /// Validates that an archive entry path stays inside <paramref name="destinationDir"/>
        /// (path traversal prevention). Returns the safe full destination path.
        /// </summary>
        private static string SafeDestinationPath(string destinationDir, string entryPath)
        {
            if (string.IsNullOrEmpty(entryPath))
                throw new InvalidOperationException("Archive entry has an empty path.");
            var fullDest = Path.GetFullPath(Path.Combine(destinationDir, entryPath));
            var root = Path.GetFullPath(destinationDir);
            if (!fullDest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(fullDest, root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Archive entry '{entryPath}' would extract outside destination directory.");
            return fullDest;
        }

        /// <summary>
        /// Streams a file from <paramref name="url"/> directly to <paramref name="destPath"/> using
        /// a 64 KB buffer. Applies a per-attempt timeout and retries with exponential backoff.
        /// Partial files are deleted before each retry.
        /// </summary>
        private static async Task StreamToFileAsync(
            Func<HttpClient> getClient, string url, string destPath,
            IProgress<double>? progress = null, long estimatedBytes = 20 * 1024 * 1024,
            int maxRetries = 3, int timeoutSeconds = 120,
            CancellationToken cancellationToken = default)
        {
            int[] backoff = { 2000, 5000, 10000 };
            Exception? lastEx = null;
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                if (attempt > 0)
                {
                    Log.Write($"[Download] Retry {attempt}/{maxRetries} for {Path.GetFileName(url)}");
                    try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
                }
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                try
                {
                    using var response = await getClient().GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    response.EnsureSuccessStatusCode();
                    var totalBytes = response.Content.Headers.ContentLength ?? estimatedBytes;
                    long totalRead = 0;
                    using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
                    using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                    var buffer = new byte[65536];
                    int read;
                    while ((read = await stream.ReadAsync(buffer.AsMemory(), cts.Token)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                        totalRead += read;
                        progress?.Report((double)totalRead / totalBytes * 100.0);
                    }
                    progress?.Report(100.0);
                    return;
                }
                catch (Exception ex) when (ex is HttpRequestException
                    || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    lastEx = ex is OperationCanceledException
                        ? new TimeoutException($"Download timed out after {timeoutSeconds}s (attempt {attempt + 1})")
                        : ex;
                    Log.Write($"[Download] Attempt {attempt + 1}/{maxRetries + 1} failed: {lastEx.Message}");
                }
                if (attempt < maxRetries)
                    await Task.Delay(backoff[Math.Min(attempt, backoff.Length - 1)], cancellationToken);
            }
            throw lastEx!;
        }

        // ─────────────────────────────────────────────────────────────────────────

        public async Task CheckForUpdatesAsync()
        {
            await _checkSemaphore.WaitAsync();
            try
            {
            LastError = null;
            try
            {
                // To avoid spamming GitHub API (rate limits), only check every 15 minutes max.
                // _lastApiCheckTime covers in-session deduplication; _config.LastApiCheckTime
                // persists across restarts so the cooldown survives app close/reopen.
                var lastCheck = _config.LastApiCheckTime.HasValue && _config.LastApiCheckTime.Value > _lastApiCheckTime
                    ? _config.LastApiCheckTime.Value
                    : _lastApiCheckTime;

                if ((_cachedOptiScalerVersions == null || _cachedOptiScalerVersions.Count == 0) ||
                    (DateTime.Now - lastCheck).TotalMinutes > 15)
                {
                    Log.Write($"[ComponentCheck] Fetching updates from GitHub API (last check: {(DateTime.Now - lastCheck).ToString(@"hh\:mm\:ss")} ago)");

                    // Record the attempt time BEFORE making any calls so the cooldown
                    // persists even when all requests fail with 403.
                    _lastApiCheckTime = DateTime.Now;
                    _config.LastApiCheckTime = DateTime.Now;
                    SaveConfiguration();

                    try
                    {
                        // Stagger requests by 150 ms each to avoid triggering GitHub's burst
                        // detection — all 5 requests still complete in under 1 second.
                        var optiVersionsTask = FetchAllReleasesWithUrlAsync(_config.OptiScaler, isBeta: false);
                        await Task.Delay(150);
                        var optiBetasTask = FetchAllReleasesWithUrlAsync(_config.OptiScalerBetas, isBeta: true);
                        await Task.Delay(150);
                        var fakeTask = FetchFakenvapiReleasesAsync();
                        await Task.Delay(150);
                        var extrasTask = FetchExtrasReleasesAsync();
                        await Task.Delay(150);
                        var optiPatcherTask = FetchOptiPatcherReleasesAsync();
                        await Task.Delay(150);
                        // Nukem's mod is bring-your-own (imported by hand), but its repo
                        // publishes versioned releases — so we can still tell the user
                        // when the copy they imported is behind the latest.
                        var nukemCheckTask = CheckComponentUpdateAsync("NukemFG", _config.NukemFG);

                        await Task.WhenAll(optiVersionsTask, optiBetasTask, fakeTask, extrasTask, optiPatcherTask, nukemCheckTask);

                        _cachedNukemFGVersion = await nukemCheckTask;

                        var stableEntries = await optiVersionsTask;
                        var betaEntries = await optiBetasTask;
                        var allNewEntries = stableEntries.Concat(betaEntries).ToList();

                        if (allNewEntries.Count > 0)
                        {
                            MergeIntoReleasesCache(allNewEntries);
                            SaveReleasesCache();
                            RebuildInMemoryCacheFromReleases();
                        }

                        var newExtras = await extrasTask;
                        if (newExtras.Count > 0)
                        {
                            // Merge extras: add new, never remove old
                            var existing = new System.Collections.Generic.HashSet<string>(
                                _extrasCache.Releases.Select(r => r.Version), StringComparer.OrdinalIgnoreCase);
                            // Reset IsLatest flags
                            foreach (var e in _extrasCache.Releases) e.IsLatest = false;
                            foreach (var entry in newExtras)
                            {
                                if (!existing.Contains(entry.Version))
                                    _extrasCache.Releases.Add(entry);
                                else
                                {
                                    var ex = _extrasCache.Releases.FirstOrDefault(
                                        r => string.Equals(r.Version, entry.Version, StringComparison.OrdinalIgnoreCase));
                                    if (ex != null)
                                    {
                                        if (string.IsNullOrEmpty(ex.DownloadUrl)) ex.DownloadUrl = entry.DownloadUrl;
                                        ex.IsLatest = entry.IsLatest;
                                    }
                                }
                            }
                            _extrasCache.LastUpdated = DateTime.Now;
                            SaveExtrasCache();
                            RebuildInMemoryExtrasCache();
                        }

                        var newFakenvapi = await fakeTask;
                        if (newFakenvapi.Count > 0)
                        {
                            var existingFake = new System.Collections.Generic.HashSet<string>(
                                _fakenvapiCache.Releases.Select(r => r.Version), StringComparer.OrdinalIgnoreCase);
                            foreach (var e in _fakenvapiCache.Releases) e.IsLatest = false;
                            foreach (var entry in newFakenvapi)
                            {
                                if (!existingFake.Contains(entry.Version))
                                    _fakenvapiCache.Releases.Add(entry);
                                else
                                {
                                    var ex = _fakenvapiCache.Releases.FirstOrDefault(
                                        r => string.Equals(r.Version, entry.Version, StringComparison.OrdinalIgnoreCase));
                                    if (ex != null)
                                    {
                                        if (string.IsNullOrEmpty(ex.DownloadUrl)) ex.DownloadUrl = entry.DownloadUrl;
                                        ex.IsLatest = entry.IsLatest;
                                    }
                                }
                            }
                            _fakenvapiCache.LastUpdated = DateTime.Now;
                            SaveFakenvapiCache();
                            RebuildInMemoryFakenvapiCache();
                        }
                        _cachedFakenvapiVersion = _cachedLatestFakenvapiVersion ?? _cachedFakenvapiVersion;

                        var newOptiPatcher = await optiPatcherTask;
                        if (newOptiPatcher.Count > 0)
                        {
                            var existingOp = new System.Collections.Generic.HashSet<string>(
                                _optiPatcherCache.Releases.Select(r => r.Version), StringComparer.OrdinalIgnoreCase);
                            foreach (var e in _optiPatcherCache.Releases) e.IsLatest = false;
                            foreach (var entry in newOptiPatcher)
                            {
                                if (!existingOp.Contains(entry.Version))
                                    _optiPatcherCache.Releases.Add(entry);
                                else
                                {
                                    var ex = _optiPatcherCache.Releases.FirstOrDefault(
                                        r => string.Equals(r.Version, entry.Version, StringComparison.OrdinalIgnoreCase));
                                    if (ex != null)
                                    {
                                        if (string.IsNullOrEmpty(ex.DownloadUrl)) ex.DownloadUrl = entry.DownloadUrl;
                                        ex.IsLatest = entry.IsLatest;
                                    }
                                }
                            }
                            _optiPatcherCache.LastUpdated = DateTime.Now;
                            SaveOptiPatcherCache();
                            RebuildInMemoryOptiPatcherCache();
                        }

                    }
                    catch (Exception apiEx)
                    {
                        // API failed — keep using whatever is already in the cache
                        Log.Write($"[ComponentCheck] GitHub API call failed (will use local cache): {apiEx.Message}");
                        LastError = apiEx;
                        // Still rebuild from cache in case it was just loaded
                        RebuildInMemoryCacheFromReleases();
                        RebuildInMemoryExtrasCache();
                        RebuildInMemoryOptiPatcherCache();
                        RebuildInMemoryFakenvapiCache();
                        // Rate limit must propagate so the UI can show a warning dialog
                        if (apiEx is GitHubRateLimitException) throw;
                    }
                }

                // Default to latest stable version from GitHub
                _remoteVersions.OptiScalerVersion = _cachedLatestStableVersion ?? OptiScalerAvailableVersions.FirstOrDefault();
                _remoteVersions.FakenvapiVersion = _cachedFakenvapiVersion;
                _remoteVersions.NukemFGVersion = _cachedNukemFGVersion;

                // Check if updates are available
                IsOptiScalerUpdateAvailable = IsUpdateAvailable(_localVersions.OptiScalerVersion, _remoteVersions.OptiScalerVersion);
                IsFakenvapiUpdateAvailable = IsUpdateAvailable(_localVersions.FakenvapiVersion, _remoteVersions.FakenvapiVersion);
                IsNukemFGUpdateAvailable = IsUpdateAvailable(_localVersions.NukemFGVersion, _remoteVersions.NukemFGVersion);

                Log.Write($"[ComponentUpdate] Status: Opti={IsOptiScalerUpdateAvailable} (Local={_localVersions.OptiScalerVersion}, Remote={_remoteVersions.OptiScalerVersion})");
                Log.Write($"[ComponentUpdate] Status: Fake={IsFakenvapiUpdateAvailable} (Local={_localVersions.FakenvapiVersion}, Remote={_remoteVersions.FakenvapiVersion})");
                Log.Write($"[ComponentUpdate] Status: Nukem={IsNukemFGUpdateAvailable} (Local={_localVersions.NukemFGVersion}, Remote={_remoteVersions.NukemFGVersion})");

                OnStatusChanged?.Invoke();
            }
            catch (Exception ex)
            {
                LastError = ex;
                throw;
            }
            }
            finally
            {
                _checkSemaphore.Release();
            }
        }

        private async Task<string?> CheckComponentUpdateAsync(string componentName, RepositoryConfig config)
        {
            try
            {
                var url = $"https://api.github.com/repos/{config.RepoOwner}/{config.RepoName}/releases/latest";
                var response = await GetWithRetryAsync(() => _httpClient, url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("tag_name", out var tagName))
                {
                    var version = tagName.GetString();
                    Log.Write($"[ComponentCheck] {componentName} Raw Tag: {version}");
                    // Strip the conventional "v" prefix (e.g. "v0.7.1" → "0.7.1")
                    if (version != null && version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                        version = version.Substring(1);
                    return version;
                }
            }
            catch (Exception ex)
            {
                Log.Write($"[ComponentCheck] {componentName} failed: {ex.Message}");
            }

            return null;
        }

        private async Task<System.Collections.Generic.List<OptiScalerReleaseEntry>> FetchAllReleasesWithUrlAsync(
            RepositoryConfig config, bool isBeta)
        {
            var entries = new System.Collections.Generic.List<OptiScalerReleaseEntry>();
            var repoLabel = $"{config.RepoOwner}/{config.RepoName}";
            bool latestStableMarked = false;
            bool latestBetaMarked = false;

            try
            {
                if (string.IsNullOrEmpty(config.RepoOwner) || string.IsNullOrEmpty(config.RepoName))
                {
                    Log.Write($"[FetchVersions] Skipping {repoLabel}: empty config");
                    return entries;
                }

                var url = $"https://api.github.com/repos/{config.RepoOwner}/{config.RepoName}/releases?per_page=30";
                Log.Write($"[FetchVersions] GET {url}");
                var response = await GetWithRetryAsync(() => _httpClient, url);
                Log.Write($"[FetchVersions] {repoLabel} → HTTP {(int)response.StatusCode}");
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (!element.TryGetProperty("tag_name", out var tagName)) continue;
                    var version = tagName.GetString();
                    if (string.IsNullOrEmpty(version)) continue;

                    if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                        version = version.Substring(1);

                    // Find best download URL from assets
                    string? downloadUrl = null;
                    if (element.TryGetProperty("assets", out var assets))
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            if (asset.TryGetProperty("browser_download_url", out var urlProp))
                            {
                                var assetUrl = urlProp.GetString();
                                if (assetUrl != null &&
                                    (assetUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                                     assetUrl.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)))
                                {
                                    downloadUrl = assetUrl;
                                    break;
                                }
                            }
                        }
                    }

                    bool isPrerelease = element.TryGetProperty("prerelease", out var pr) && pr.GetBoolean();

                    bool isThisLatestStable = false;
                    bool isThisLatestBeta = false;

                    if (isBeta)
                    {
                        if (!latestBetaMarked)
                        {
                            isThisLatestBeta = true;
                            latestBetaMarked = true;
                        }
                    }
                    else
                    {
                        if (!latestStableMarked && !isPrerelease)
                        {
                            isThisLatestStable = true;
                            latestStableMarked = true;
                        }
                    }

                    entries.Add(new OptiScalerReleaseEntry
                    {
                        Version = version,
                        DownloadUrl = downloadUrl,
                        // Beta = from the dedicated betas repo OR marked prerelease on
                        // the main repo (nightlies/pre tags) — otherwise pre-releases
                        // get sorted in among the stable versions.
                        IsBeta = isBeta || isPrerelease,
                        IsLatestStable = isThisLatestStable,
                        IsLatestBeta = isThisLatestBeta,
                    });
                }

                Log.Write($"[FetchVersions] {repoLabel} → {entries.Count} release(s) fetched");
            }
            catch (Exception ex)
            {
                Log.Write($"[FetchVersions] {repoLabel} → ERROR: {ex.Message}");
                throw; // Let CheckForUpdatesAsync handle the fallback
            }

            return entries;
        }

        // ── OptiScaler Extras (FSR4 INT8) ────────────────────────────────────────

        /// <summary>
        /// Fetches all releases from the OptiScaler Extras repo.
        /// </summary>
        private async Task<System.Collections.Generic.List<ExtrasReleaseEntry>> FetchExtrasReleasesAsync()
        {
            var entries = new System.Collections.Generic.List<ExtrasReleaseEntry>();
            var config = _config.OptiScalerExtras;
            var repoLabel = $"{config.RepoOwner}/{config.RepoName}";

            try
            {
                if (string.IsNullOrEmpty(config.RepoOwner) || string.IsNullOrEmpty(config.RepoName))
                {
                    Log.Write($"[ExtrasVersions] Skipping {repoLabel}: empty config");
                    return entries;
                }

                var url = $"https://api.github.com/repos/{config.RepoOwner}/{config.RepoName}/releases?per_page=30";
                var response = await GetWithRetryAsync(() => _httpClient, url);
                Log.Write($"[ExtrasVersions] GET {url} → HTTP {(int)response.StatusCode}");
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                bool latestMarked = false;

                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    Log.Write($"[ExtrasVersions] ERROR: Expected JSON array, got {doc.RootElement.ValueKind}");
                    return entries;
                }

                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (!element.TryGetProperty("tag_name", out var tagName))
                    {
                        Log.Write("[ExtrasVersions] Skipping release: no tag_name");
                        continue;
                    }

                    var version = tagName.GetString();
                    if (string.IsNullOrEmpty(version))
                    {
                        Log.Write("[ExtrasVersions] Skipping release: empty tag_name");
                        continue;
                    }

                    if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                        version = version.Substring(1);

                    // Get download URL (first .zip or .7z asset)
                    string? downloadUrl = null;
                    if (element.TryGetProperty("assets", out var assets))
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            if (asset.TryGetProperty("browser_download_url", out var urlProp))
                            {
                                var assetUrl = urlProp.GetString();
                                if (assetUrl != null &&
                                    (assetUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                                     assetUrl.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)))
                                {
                                    downloadUrl = assetUrl;
                                    break;
                                }
                            }
                        }
                    }

                    entries.Add(new ExtrasReleaseEntry
                    {
                        Version = version,
                        DownloadUrl = downloadUrl,
                        IsLatest = !latestMarked,
                        IsPreRelease = element.TryGetProperty("prerelease", out var pre)
                                       && pre.ValueKind == JsonValueKind.True,
                    });
                    latestMarked = true;
                }

                Log.Write($"[ExtrasVersions] {repoLabel} → {entries.Count} release(s)");
            }
            catch (Exception ex)
            {
                Log.Write($"[ExtrasVersions] {repoLabel} → ERROR: {ex.Message}");
                // Do NOT rethrow — return empty list so the rest of CheckForUpdatesAsync continues normally
            }

            return entries;
        }

        /// <summary>
        /// Returns the cache directory for a specific Extras (FSR4 INT8) DLL version.
        /// </summary>
        public string GetExtrasDllCachePath(string version)
            => Path.Combine(_cacheDir, "Extras", version);

        /// <summary>
        /// Downloads the Extras zip for the given version and extracts its INT8 DLL into
        /// the per-version cache folder. Returns the path to the DLL file, which keeps
        /// whichever name the release ships (see <see cref="Components.Fsr4Int8Build"/>).
        /// </summary>
        /// <summary>
        /// The URL we already know for a version, from the cached release list.
        /// </summary>
        private static string? CachedAssetUrl<T>(System.Collections.Generic.IEnumerable<T> releases, string version)
            where T : IReleaseEntry =>
            releases.FirstOrDefault(r => string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase))?.DownloadUrl;

        /// <summary>
        /// Asks GitHub which file to download for one release, when the cached list does
        /// not already say. <paramref name="wantsAsset"/> picks the right file out of the
        /// release by name — components publish different things (a .asi, a .zip, a .7z).
        ///
        /// Tags are written both ways across these repos ("v0.9.3" and "0.9.3"), so both
        /// are tried before giving up. A repo that attaches no assets at all can still be
        /// served by its source archive, which is what <paramref name="fallBackToZipball"/>
        /// allows.
        /// </summary>
        private async Task<string?> ResolveReleaseAssetUrlAsync(
            RepositoryConfig config, string version, string label,
            Func<string, bool> wantsAsset, bool fallBackToZipball = false)
        {
            foreach (var prefix in new[] { "v", "" })
            {
                try
                {
                    var apiUrl = $"https://api.github.com/repos/{config.RepoOwner}/{config.RepoName}/releases/tags/{prefix}{version}";
                    var response = await GetWithRetryAsync(() => _httpClient, apiUrl, maxRetries: 2, timeoutSeconds: 15);
                    if (!response.IsSuccessStatusCode) continue;

                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

                    if (doc.RootElement.TryGetProperty("assets", out var assets))
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            if (!asset.TryGetProperty("browser_download_url", out var urlProp)) continue;
                            if (urlProp.GetString() is not { } url) continue;

                            // Match on the asset's name, falling back to the URL, which ends
                            // with the same file name for anything GitHub hosts.
                            var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                            if (wantsAsset(string.IsNullOrEmpty(name) ? url : name)) return url;
                        }
                    }

                    if (fallBackToZipball &&
                        doc.RootElement.TryGetProperty("zipball_url", out var zipball) &&
                        zipball.GetString() is { Length: > 0 } zipballUrl)
                    {
                        return zipballUrl;
                    }
                }
                catch (Exception ex)
                {
                    Log.Write($"[{label}] API lookup attempt failed: {ex.Message}");
                }
            }
            return null;
        }

        /// <summary>True for the archive formats these components ship in.</summary>
        private static bool IsArchiveAsset(string name) =>
            name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase);

        public async Task<string> DownloadExtrasDllAsync(string version, IProgress<double>? progress = null)
        {
            var extractDir = GetExtrasDllCachePath(version);
            var cached = Components.Fsr4Int8Build.FindIn(extractDir);

            if (cached is not null)
            {
                var dllPath = cached;
                Log.Write($"[ExtrasDownload] DLL for v{version} already cached at {dllPath}");
                return dllPath;
            }

            var downloadUrl = CachedAssetUrl(_extrasCache.Releases, version)
                ?? await ResolveReleaseAssetUrlAsync(_config.OptiScalerExtras, version, "ExtrasDownload", IsArchiveAsset);

            if (string.IsNullOrEmpty(downloadUrl))
                throw new VersionUnavailableException(version, "No downloadable asset found for this Extras version.");

            Directory.CreateDirectory(extractDir);

            var tempZip = Path.Combine(Path.GetTempPath(), $"Extras_{version}_{Guid.NewGuid()}.zip");
            Log.Write($"[ExtrasDownload] Downloading {downloadUrl}");

            try
            {
                // Stream download with retry and per-attempt timeout
                await StreamToFileAsync(() => _httpClient, downloadUrl, tempZip, progress, 20 * 1024 * 1024);

                // Extract only the target DLL with path validation (off the UI thread)
                Log.Write($"[ExtrasDownload] Extracting from {Path.GetFileName(tempZip)}");
                await Task.Run(() =>
                {
                    using var archive = SharpCompress.Archives.ArchiveFactory.Open(tempZip);
                    foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                    {
                        var name = Path.GetFileName(entry.Key ?? "");
                        if (!Components.Fsr4Int8Build.IsKnown(name)) continue;

                        // Keep the name the release ships: it is what OptiScaler loads.
                        var dest = SafeDestinationPath(extractDir, name);
                        using var entryStream = entry.OpenEntryStream();
                        using var outStream = File.Create(dest);
                        entryStream.CopyTo(outStream, 81920);
                        Log.Write($"[ExtrasDownload] Extracted {name} to {dest}");
                        break;
                    }
                });
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }

            var extracted = Components.Fsr4Int8Build.FindIn(extractDir);
            if (extracted is null)
                throw new Exception(
                    "No FSR 4 INT8 DLL found inside the downloaded archive (expected " +
                    string.Join(" or ", Components.Fsr4Int8Build.KnownDllNames) + ").");

            // Same check a DLL you import by hand gets. A download is not more
            // trustworthy than a local file, so do not let it skip validation: a
            // truncated transfer or a wrong-architecture build should fail here rather
            // than next to the game's exe.
            var pe = PeFileInspector.Inspect(extracted);
            if (!pe.IsValidPe || !pe.Is64Bit)
            {
                try { File.Delete(extracted); } catch { }
                throw new Exception(
                    $"The downloaded {Path.GetFileName(extracted)} is not a valid 64-bit DLL " +
                    $"(from {downloadUrl}). Nothing was installed.");
            }

            return extracted;
        }

        // ── AMD FidelityFX SDK (official, open-source) ────────────────────────────

        // NOTE (history): earlier versions could download AMD's signedbin FFX DLLs
        // straight from the FidelityFX-SDK repo as a separate backend. That was
        // removed: OptiScaler hooks the upscaler's model code by byte pattern, so the
        // only compatible AMD files are exactly the ones each OptiScaler release
        // already bundles (e.g. 0.9.3 hooks ≤4.1.0, 0.9.4 hooks 4.1.1) — downloading
        // them again added nothing. Custom DLLs now overlay the installed files.

        // ── OptiPatcher cache ─────────────────────────────────────────────────────

        private void LoadOptiPatcherCache()
        {
            if (_optiPatcherCache.Releases.Count > 0) return;
            if (ReadReleasesCache("optipatcher_cache.json", "OptiPatcherCache", OptimizerContext.Default.OptiPatcherReleasesCache) is not { } loaded) return;
            _optiPatcherCache = loaded;
            RebuildInMemoryOptiPatcherCache();
        }

        private void SaveOptiPatcherCache()
            => WriteReleasesCache("optipatcher_cache.json", "OptiPatcherCache", OptimizerContext.Default.OptiPatcherReleasesCache, _optiPatcherCache);

        private void RebuildInMemoryOptiPatcherCache()
        {
            if (SummariseReleases(_optiPatcherCache.Releases, "OptiPatcherCache") is not { } summary)
            {
                _cachedOptiPatcherVersions = new System.Collections.Generic.List<string>();
                return;
            }
            (_cachedLatestOptiPatcherVersion, _cachedOptiPatcherVersions) = summary;
        }

        // ── Fakenvapi cache ───────────────────────────────────────────────────────

        private void LoadFakenvapiCache()
        {
            if (_fakenvapiCache.Releases.Count > 0) return;
            if (ReadReleasesCache("fakenvapi_cache.json", "FakenvapiCache", OptimizerContext.Default.FakenvapiReleasesCache) is not { } loaded) return;
            _fakenvapiCache = loaded;
            RebuildInMemoryFakenvapiCache();
        }

        private void SaveFakenvapiCache()
            => WriteReleasesCache("fakenvapi_cache.json", "FakenvapiCache", OptimizerContext.Default.FakenvapiReleasesCache, _fakenvapiCache);

        private void RebuildInMemoryFakenvapiCache()
        {
            if (SummariseReleases(_fakenvapiCache.Releases, "FakenvapiCache") is not { } summary)
            {
                _cachedFakenvapiVersions = new System.Collections.Generic.List<string>();
                return;
            }
            (_cachedLatestFakenvapiVersion, _cachedFakenvapiVersions) = summary;
        }

        /// <summary>
        /// Fetches all releases from the Fakenvapi repo. Looks for .zip or .7z assets.
        /// </summary>
        private async Task<System.Collections.Generic.List<FakenvapiReleaseEntry>> FetchFakenvapiReleasesAsync()
        {
            var entries = new System.Collections.Generic.List<FakenvapiReleaseEntry>();
            var config = _config.Fakenvapi;
            var repoLabel = $"{config.RepoOwner}/{config.RepoName}";

            try
            {
                if (string.IsNullOrEmpty(config.RepoOwner) || string.IsNullOrEmpty(config.RepoName))
                {
                    Log.Write($"[FakenvapiVersions] Skipping {repoLabel}: empty config");
                    return entries;
                }

                var url = $"https://api.github.com/repos/{config.RepoOwner}/{config.RepoName}/releases?per_page=30";
                var response = await GetWithRetryAsync(() => _httpClient, url);
                Log.Write($"[FakenvapiVersions] GET {url} → HTTP {(int)response.StatusCode}");
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                bool latestMarked = false;

                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    Log.Write($"[FakenvapiVersions] ERROR: Expected JSON array, got {doc.RootElement.ValueKind}");
                    return entries;
                }

                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (!element.TryGetProperty("tag_name", out var tagName)) continue;
                    var version = tagName.GetString();
                    if (string.IsNullOrEmpty(version)) continue;

                    if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                        version = version.Substring(1);

                    // Look for a .zip or .7z asset
                    string? downloadUrl = null;
                    if (element.TryGetProperty("assets", out var assets))
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            if (asset.TryGetProperty("browser_download_url", out var urlProp) &&
                                asset.TryGetProperty("name", out var nameProp))
                            {
                                var assetName = nameProp.GetString() ?? "";
                                var assetUrl  = urlProp.GetString();
                                if (assetUrl != null &&
                                    (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                                     assetName.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)))
                                {
                                    downloadUrl = assetUrl;
                                    break;
                                }
                            }
                        }
                    }

                    // Fallback to zipball_url
                    if (downloadUrl == null && element.TryGetProperty("zipball_url", out var zipballProp))
                        downloadUrl = zipballProp.GetString();

                    bool isLatest = !latestMarked;

                    entries.Add(new FakenvapiReleaseEntry
                    {
                        Version = version,
                        DownloadUrl = downloadUrl,
                        IsLatest = isLatest,
                    });
                    latestMarked = true;
                }

                Log.Write($"[FakenvapiVersions] {repoLabel} → {entries.Count} release(s)");
            }
            catch (Exception ex)
            {
                Log.Write($"[FakenvapiVersions] {repoLabel} → ERROR: {ex.Message}");
            }

            return entries;
        }

        /// <summary>
        /// Returns the cache directory for a specific Fakenvapi version.
        /// </summary>
        public string GetFakenvapiCachePath(string version)
            => Path.Combine(_cacheDir, "Fakenvapi", version);

        /// <summary>
        /// Returns true if the given Fakenvapi version is already cached locally.
        /// </summary>
        public bool IsFakenvapiCached(string version)
        {
            var dir = GetFakenvapiCachePath(version);
            return Directory.Exists(dir) && Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories).Length > 0;
        }

        /// <summary>
        /// Downloads and extracts Fakenvapi for the given version into the per-version cache folder.
        /// Returns the full cache directory path.
        /// </summary>
        public async Task<string> DownloadFakenvapiAsync(string version, IProgress<double>? progress = null)
        {
            var cacheDir = GetFakenvapiCachePath(version);

            if (IsFakenvapiCached(version))
            {
                Log.Write($"[FakenvapiDownload] v{version} already cached at {cacheDir}");
                return cacheDir;
            }

            var downloadUrl = CachedAssetUrl(_fakenvapiCache.Releases, version)
                ?? await ResolveReleaseAssetUrlAsync(_config.Fakenvapi, version, "FakenvapiDownload",
                       IsArchiveAsset, fallBackToZipball: true);

            if (string.IsNullOrEmpty(downloadUrl))
                throw new VersionUnavailableException(version, "No downloadable asset found for Fakenvapi.");

            var tempFile = Path.Combine(Path.GetTempPath(), $"Fakenvapi_{Guid.NewGuid()}.zip");
            try
            {
                Log.Write($"[FakenvapiDownload] Downloading {downloadUrl}");
                await StreamToFileAsync(() => _httpClient, downloadUrl, tempFile, progress);

                if (Directory.Exists(cacheDir))
                    Directory.Delete(cacheDir, true);
                Directory.CreateDirectory(cacheDir);

                await Task.Run(() =>
                {
                    using var archive = ArchiveFactory.Open(tempFile);
                    foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                    {
                        var destPath = SafeDestinationPath(cacheDir, entry.Key ?? string.Empty);
                        var destDir = Path.GetDirectoryName(destPath);
                        if (destDir != null && !Directory.Exists(destDir))
                            Directory.CreateDirectory(destDir);
                        using var entryStream = entry.OpenEntryStream();
                        using var fileStream = File.Create(destPath);
                        entryStream.CopyTo(fileStream, 81920);
                    }
                });

                Log.Write($"[FakenvapiDownload] Extracted v{version} to {cacheDir}");
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }

            return cacheDir;
        }

        /// <summary>
        /// Returns a list of locally-cached Fakenvapi version names.
        /// Also migrates the legacy flat cache layout to a versioned layout if needed.
        /// </summary>
        public List<string> GetDownloadedFakenvapiVersions()
        {
            var versions = new List<string>();
            var fakenvapiDir = GetFakenvapiCachePath();
            if (!Directory.Exists(fakenvapiDir)) return versions;

            // Legacy migration: if nvapi64.dll exists directly in Fakenvapi/ (flat layout),
            // move everything into a "default" subdirectory.
            var legacyDll = Path.Combine(fakenvapiDir, "nvapi64.dll");
            if (File.Exists(legacyDll))
            {
                var defaultDir = Path.Combine(fakenvapiDir, "default");
                Directory.CreateDirectory(defaultDir);
                foreach (var file in Directory.GetFiles(fakenvapiDir))
                {
                    var destFile = Path.Combine(defaultDir, Path.GetFileName(file));
                    File.Move(file, destFile, true);
                }
                Log.Write("[Fakenvapi] Migrated legacy flat cache to versioned layout (default).");
            }

            foreach (var dir in Directory.GetDirectories(fakenvapiDir))
            {
                var files = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories);
                if (files.Length > 0)
                {
                    versions.Add(Path.GetFileName(dir));
                }
            }
            return VersionOrder.Newest(versions);
        }

        /// <summary>
        /// Fetches all releases from the OptiPatcher repo. Looks for the OptiPatcher.asi asset.
        /// </summary>
        private async Task<System.Collections.Generic.List<OptiPatcherReleaseEntry>> FetchOptiPatcherReleasesAsync()
        {
            var entries = new System.Collections.Generic.List<OptiPatcherReleaseEntry>();
            var config = _config.OptiPatcher;
            var repoLabel = $"{config.RepoOwner}/{config.RepoName}";

            try
            {
                if (string.IsNullOrEmpty(config.RepoOwner) || string.IsNullOrEmpty(config.RepoName))
                {
                    Log.Write($"[OptiPatcherVersions] Skipping {repoLabel}: empty config");
                    return entries;
                }

                var url = $"https://api.github.com/repos/{config.RepoOwner}/{config.RepoName}/releases?per_page=30";
                var response = await GetWithRetryAsync(() => _httpClient, url);
                Log.Write($"[OptiPatcherVersions] GET {url} → HTTP {(int)response.StatusCode}");
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                bool latestMarked = false;

                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    Log.Write($"[OptiPatcherVersions] ERROR: Expected JSON array, got {doc.RootElement.ValueKind}");
                    return entries;
                }

                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (!element.TryGetProperty("tag_name", out var tagName)) continue;
                    var version = tagName.GetString();
                    if (string.IsNullOrEmpty(version)) continue;

                    if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                        version = version.Substring(1);

                    // Look for OptiPatcher.asi asset
                    string? downloadUrl = null;
                    if (element.TryGetProperty("assets", out var assets))
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            if (asset.TryGetProperty("browser_download_url", out var urlProp) &&
                                asset.TryGetProperty("name", out var nameProp))
                            {
                                var assetName = nameProp.GetString() ?? "";
                                var assetUrl  = urlProp.GetString();
                                if (assetUrl != null &&
                                    assetName.EndsWith(".asi", StringComparison.OrdinalIgnoreCase))
                                {
                                    downloadUrl = assetUrl;
                                    break;
                                }
                            }
                        }
                    }

                    // Mark the first entry in the sorted list as latest
                    bool isLatest = !latestMarked;

                    entries.Add(new OptiPatcherReleaseEntry
                    {
                        Version = version,
                        DownloadUrl = downloadUrl,
                        IsLatest = isLatest,
                    });
                    latestMarked = true;
                }

                Log.Write($"[OptiPatcherVersions] {repoLabel} → {entries.Count} release(s)");
            }
            catch (Exception ex)
            {
                Log.Write($"[OptiPatcherVersions] {repoLabel} → ERROR: {ex.Message}");
                // Do NOT rethrow — return empty list so CheckForUpdatesAsync continues
            }

            return entries;
        }

        /// <summary>
        /// Returns the cache directory for a specific OptiPatcher version.
        /// </summary>
        public string GetOptiPatcherCachePath(string version)
            => Path.Combine(_cacheDir, "OptiPatcher", version);

        /// <summary>
        /// Downloads OptiPatcher.asi for the given version into the per-version cache folder.
        /// Returns the full path to the cached OptiPatcher.asi file.
        /// </summary>
        public async Task<string> DownloadOptiPatcherAsync(string version, IProgress<double>? progress = null)
        {
            var cacheDir = GetOptiPatcherCachePath(version);
            var asiPath  = Path.Combine(cacheDir, "OptiPatcher.asi");

            if (File.Exists(asiPath))
            {
                Log.Write($"[OptiPatcherDownload] OptiPatcher v{version} already cached at {asiPath}");
                return asiPath;
            }

            var downloadUrl = CachedAssetUrl(_optiPatcherCache.Releases, version)
                ?? await ResolveReleaseAssetUrlAsync(_config.OptiPatcher, version, "OptiPatcherDownload",
                       name => name.EndsWith(".asi", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrEmpty(downloadUrl))
                throw new VersionUnavailableException(version, "No OptiPatcher.asi asset found for this version.");

            Directory.CreateDirectory(cacheDir);

            Log.Write($"[OptiPatcherDownload] Downloading {downloadUrl}");
            await StreamToFileAsync(() => _httpClient, downloadUrl, asiPath, progress, 5 * 1024 * 1024);

            if (!File.Exists(asiPath))
                throw new Exception("OptiPatcher.asi was not downloaded correctly.");

            return asiPath;
        }

        private bool IsUpdateAvailable(string? localVersion, string? remoteVersion)
        {
            if (string.IsNullOrEmpty(remoteVersion))
                return false;

            if (string.IsNullOrEmpty(localVersion))
                return true;

            return localVersion != remoteVersion;
        }

        public async Task<string> DownloadOptiScalerAsync(string version, IProgress<double>? progress = null)
        {
            if (string.IsNullOrEmpty(version))
                throw new Exception("Version cannot be empty");

            var extractPath = GetOptiScalerCachePath(version);
            if (Directory.Exists(extractPath) && Directory.GetFiles(extractPath).Length > 0)
            {
                Log.Write($"[Download] OptiScaler v{version} already cached at {extractPath}");
                return extractPath; // Already downloaded
            }

            lock (_downloadLock)
            {
                if (_activeOptiDownloads.Contains(version))
                {
                    throw new VersionUnavailableException(version, "Download already in progress for this version.");
                }
                _activeOptiDownloads.Add(version);
            }

            LastError = null;
            Log.Write($"[Download] Starting download of OptiScaler v{version}");
            Log.Write($"[Download] Cache path: {extractPath}");

            try
            {
                // 1. Try to get the download URL from the local releases cache first
                string? cachedDownloadUrl = _releasesCache.Releases
                    .FirstOrDefault(r => string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase))
                    ?.DownloadUrl;

                // 2. Try to retrieve release from GitHub API (stable repo → beta repo, with/without v prefix)
                HttpResponseMessage? response = null;
                string? json = null;
                string repoSource = "";

                bool apiAvailable = true;
                try
                {
                    // Try stable repo with v prefix
                    var url = $"https://api.github.com/repos/{_config.OptiScaler.RepoOwner}/{_config.OptiScaler.RepoName}/releases/tags/v{version}";
                    Log.Write($"[Download] Trying stable repo (with v prefix): {url}");
                    response = await GetWithRetryAsync(() => _httpClient, url, maxRetries: 2, timeoutSeconds: 20);

                    if (!response.IsSuccessStatusCode)
                    {
                        // Try stable repo without v prefix
                        url = $"https://api.github.com/repos/{_config.OptiScaler.RepoOwner}/{_config.OptiScaler.RepoName}/releases/tags/{version}";
                        Log.Write($"[Download] Trying stable repo (without v prefix): {url}");
                        response = await GetWithRetryAsync(() => _httpClient, url, maxRetries: 2, timeoutSeconds: 20);
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        // Try beta repo with v prefix
                        url = $"https://api.github.com/repos/{_config.OptiScalerBetas.RepoOwner}/{_config.OptiScalerBetas.RepoName}/releases/tags/v{version}";
                        Log.Write($"[Download] Trying beta repo (with v prefix): {url}");
                        response = await GetWithRetryAsync(() => _httpClient, url, maxRetries: 2, timeoutSeconds: 20);
                        repoSource = " (beta repo)";
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        // Try beta repo without v prefix
                        url = $"https://api.github.com/repos/{_config.OptiScalerBetas.RepoOwner}/{_config.OptiScalerBetas.RepoName}/releases/tags/{version}";
                        Log.Write($"[Download] Trying beta repo (without v prefix): {url}");
                        response = await GetWithRetryAsync(() => _httpClient, url, maxRetries: 2, timeoutSeconds: 20);
                        repoSource = " (beta repo)";
                    }

                    if (response.IsSuccessStatusCode)
                    {
                        json = await response.Content.ReadAsStringAsync();
                    }
                }
                catch (Exception networkEx)
                {
                    apiAvailable = false;
                    Log.Write($"[Download] GitHub API unreachable: {networkEx.Message}");
                }

                string? downloadUrl = null;

                // 3. Parse download URL from API response if available
                if (json != null)
                {
                    Log.Write($"[Download] Release found{repoSource} for OptiScaler v{version}");
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("assets", out var assets))
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            if (asset.TryGetProperty("browser_download_url", out var urlProp))
                            {
                                var assetUrl = urlProp.GetString();
                                if (assetUrl != null && (assetUrl.EndsWith(".zip") || assetUrl.EndsWith(".7z")))
                                {
                                    downloadUrl = assetUrl;
                                    Log.Write($"[Download] Found download asset: {Path.GetFileName(assetUrl)}");

                                    // Update cached URL if different/missing
                                    var cacheEntry = _releasesCache.Releases.FirstOrDefault(
                                        r => string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase));
                                    if (cacheEntry != null && string.IsNullOrEmpty(cacheEntry.DownloadUrl))
                                    {
                                        cacheEntry.DownloadUrl = downloadUrl;
                                        SaveReleasesCache();
                                    }
                                    break;
                                }
                            }
                        }
                    }
                }

                // 4. Fall back to cached URL if API didn't yield one
                if (downloadUrl == null && !string.IsNullOrEmpty(cachedDownloadUrl))
                {
                    downloadUrl = cachedDownloadUrl;
                    Log.Write($"[Download] Using cached download URL for v{version}: {downloadUrl}");
                }

                // 5. Nothing to download from — surface a friendly error
                if (downloadUrl == null)
                {
                    string reason = apiAvailable
                        ? "No downloadable asset found for the specified OptiScaler version."
                        : "GitHub is unreachable and no cached URL is available for this version.";
                    throw new VersionUnavailableException(version, reason);
                }
                // Create folder
                Directory.CreateDirectory(extractPath);
                Log.Write($"[Download] Created cache directory: {extractPath}");

                var tempZip = Path.Combine(Path.GetTempPath(), $"OptiScaler_{version}_{Guid.NewGuid()}.zip");
                Log.Write($"[Download] Streaming from: {Path.GetFileName(downloadUrl)}");

                try
                {
                    // Stream download with retry and per-attempt timeout
                    await StreamToFileAsync(() => _httpClient, downloadUrl, tempZip, progress);

                    // Extract with path traversal validation (off the UI thread)
                    Log.Write($"[Extract] Starting extraction of {Path.GetFileName(tempZip)} to {extractPath}");
                    var extractStartTime = DateTime.Now;
                    var fileCount = 0;

                    await Task.Run(() =>
                    {
                        using var archive = ArchiveFactory.Open(tempZip);
                        var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
                        foreach (var entry in entries)
                        {
                            var destPath = SafeDestinationPath(extractPath, entry.Key ?? string.Empty);
                            var destDir = Path.GetDirectoryName(destPath);
                            if (destDir != null && !Directory.Exists(destDir))
                                Directory.CreateDirectory(destDir);
                            using var entryStream = entry.OpenEntryStream();
                            using var fileStream = File.Create(destPath);
                            entryStream.CopyTo(fileStream, 81920);
                            fileCount++;
                        }
                    });

                    var extractDuration = DateTime.Now - extractStartTime;
                    Log.Write($"[Extract] Extraction completed: {fileCount} files in {extractDuration.TotalSeconds:F1}s");

                    // Guard against a truncated/partial archive that extracts most files
                    // but silently omits the main DLL — persisting that would fail every
                    // install until the user manually cleared the cache.
                    if (!OptiScalerCacheHasMainDll(extractPath))
                        throw new Exception(
                            "The downloaded OptiScaler package was incomplete (no OptiScaler.dll after extraction) — " +
                            "usually an interrupted download. The partial cache has been discarded; please try again.");
                }
                finally
                {
                    try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                    Log.Write($"[Download] Temp file cleaned up: {Path.GetFileName(tempZip)}");
                }

                _localVersions.OptiScalerVersion = version; // update the locally assumed latest for other components
                SaveLocalVersions();
                Log.Write($"[Download] OptiScaler v{version} download and extraction completed successfully");

                return extractPath;
            }
            catch (Exception ex)
            {
                LastError = ex;
                Log.Write($"[Download] ERROR: {ex.Message}");
                if (Directory.Exists(extractPath))
                {
                    Directory.Delete(extractPath, true);
                    Log.Write($"[Download] Cleaned up cache directory due to error: {extractPath}");
                }
                throw;
            }
            finally
            {
                lock (_downloadLock)
                {
                    _activeOptiDownloads.Remove(version);
                }
            }
        }

        /// <summary>
        /// NukemFG cannot be downloaded automatically from GitHub.
        /// This method shows the manual file picker dialog so the user can
        /// provide the DLL directly. The DLL is stored in the local cache for
        /// future installs, and the provided version tag is saved to versions.json.
        /// </summary>
        /// <param name="isUpdate">True when the user is updating an existing DLL (vs. first install).</param>
        public async Task<bool> ProvideNukemFGManuallyAsync(bool isUpdate = false)
        {
            var targetVersion = _remoteVersions.NukemFGVersion ?? "manual";

            try
            {
                // NukemFG cannot be fetched from GitHub, so we call out to the
                // host through the injected provider. The provider owns the file
                // picker / archive extraction and drops the DLL into the cache.
                bool confirmed = await _manualProvider.ProvideAsync(new ManualComponentRequest
                {
                    ComponentName = "Nukem's DLSSG-to-FSR3 Mod",
                    RequiredFileName = "dlssg_to_fsr3_amd_is_better.dll",
                    TargetCachePath = GetNukemFGCachePath(),
                    IsUpdate = isUpdate
                });

                if (confirmed)
                {
                    _localVersions.NukemFGVersion = targetVersion;
                    IsNukemFGUpdateAvailable = false;
                    SaveLocalVersions();
                    OnStatusChanged?.Invoke();
                }

                return confirmed;
            }
            catch (Exception ex)
            {
                LastError = ex;
                return false;
            }
        }

        public string GetOptiScalerCachePath() => Path.Combine(_cacheDir, "OptiScaler", OptiScalerVersion ?? "latest");
        public string GetOptiScalerCachePath(string version) => Path.Combine(_cacheDir, "OptiScaler", version);

        /// <summary>
        /// A cached OptiScaler version is only usable if the main injectable DLL is
        /// present. An interrupted download can leave a folder full of files but no
        /// OptiScaler.dll (or the legacy nvngx.dll) — callers re-download in that case.
        /// </summary>
        public bool IsOptiScalerCacheComplete(string version) => OptiScalerCacheHasMainDll(GetOptiScalerCachePath(version));

        internal static bool OptiScalerCacheHasMainDll(string cacheDir)
        {
            if (!Directory.Exists(cacheDir)) return false;
            foreach (var f in Directory.EnumerateFiles(cacheDir, "*.dll", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(f);
                if (name.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("nvngx.dll", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        public string GetFakenvapiCachePath() => Path.Combine(_cacheDir, "Fakenvapi");
        /// <summary>Returns the cache directory for NukemFG files (legacy flat path).</summary>
        public string GetNukemFGCachePath() => Path.Combine(_cacheDir, "NukemFG");
        /// <summary>Returns the cache directory for a specific NukemFG version.</summary>
        public string GetNukemFGCachePath(string version) => Path.Combine(_cacheDir, "NukemFG", version);
        public string GetNukemFGDllPath() => Path.Combine(GetNukemFGCachePath(), "dlssg_to_fsr3_amd_is_better.dll");
        public string GetNukemFGDllPath(string version) => Path.Combine(GetNukemFGCachePath(version), "dlssg_to_fsr3_amd_is_better.dll");

        public System.Collections.Generic.List<string> GetDownloadedOptiScalerVersions()
        {
            var versions = new System.Collections.Generic.List<string>();
            var cachePath = Path.Combine(_cacheDir, "OptiScaler");
            if (Directory.Exists(cachePath))
            {
                foreach (var dir in Directory.GetDirectories(cachePath))
                {
                    var dirName = Path.GetFileName(dir);
                    if (dirName.Equals("D3D12_Optiscaler", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Equals("DlssOverrides", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Equals("Licenses", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (System.Linq.Enumerable.Any(dirName, char.IsDigit) || dirName.Equals("latest", StringComparison.OrdinalIgnoreCase) ||
                        _config.CustomOptiScalerVersions.Contains(dirName, StringComparer.OrdinalIgnoreCase))
                    {
                        versions.Add(dirName);
                    }
                }
            }
            return VersionOrder.Newest(versions);
        }

        /// <summary>Returns the set of custom OptiScaler version names imported by the user.</summary>
        public System.Collections.Generic.HashSet<string> CustomVersions
            => new(_config.CustomOptiScalerVersions, StringComparer.OrdinalIgnoreCase);

        public System.Collections.Generic.List<string> GetDownloadedExtrasVersions()
        {
            var versions = new System.Collections.Generic.List<string>();
            var cachePath = Path.Combine(_cacheDir, "Extras");
            if (Directory.Exists(cachePath))
            {
                foreach (var dir in Directory.GetDirectories(cachePath))
                {
                    var dirName = Path.GetFileName(dir);
                    if (File.Exists(Path.Combine(dir, "amd_fidelityfx_upscaler_dx12.dll")))
                        versions.Add(dirName);
                }
            }
            return VersionOrder.Newest(versions);
        }

        public System.Collections.Generic.List<string> GetDownloadedOptiPatcherVersions()
        {
            var versions = new System.Collections.Generic.List<string>();
            var cachePath = Path.Combine(_cacheDir, "OptiPatcher");
            if (Directory.Exists(cachePath))
            {
                foreach (var dir in Directory.GetDirectories(cachePath))
                {
                    var dirName = Path.GetFileName(dir);
                    if (File.Exists(Path.Combine(dir, "OptiPatcher.asi")))
                        versions.Add(dirName);
                }
            }
            return VersionOrder.Newest(versions);
        }

        // ── Custom FSR 4.x amdxcffx64.dll (bring-your-own DLL) ───────────────────
        //
        // This component is strictly local-file based: the app NEVER downloads,
        // bundles, or links to the DLL. The user browses to an amdxcffx64.dll they
        // already possess; it is copied into the local cache like other components.
        //
        // OptiScaler (v0.7.7-pre9 and newer) checks the game folder first for
        // amdxcffx64.dll before falling back to the driver store, so installing
        // means copying the DLL next to the game executable.

        /// <summary>Filename OptiScaler expects for the FSR 4.x driver-side DLL.</summary>
        public const string CustomFsr4DllName = "amdxcffx64.dll";

        /// <summary>Returns the root cache directory for custom FSR4 DLL versions.</summary>
        public string GetCustomFsr4CachePath() => Path.Combine(_cacheDir, "CustomFsr4");

        /// <summary>Returns the cache directory for a specific custom FSR4 DLL version.</summary>
        public string GetCustomFsr4CachePath(string version) => Path.Combine(GetCustomFsr4CachePath(), version);

        /// <summary>Returns the full path of the cached DLL for a specific version.</summary>
        public string GetCustomFsr4DllPath(string version) => Path.Combine(GetCustomFsr4CachePath(version), CustomFsr4DllName);

        /// <summary>
        /// Returns a list of locally-imported custom FSR4 DLL version labels
        /// (subdirectory names under Cache/CustomFsr4/ that contain the DLL).
        /// </summary>
        public List<string> GetDownloadedCustomFsr4Versions()
            => GetUserDllVersions(GetCustomFsr4CachePath(), CustomFsr4DllName);

        // ── Custom FSR SDK amd_fidelityfx_upscaler_dx12.dll (bring-your-own DLL) ─
        //
        // Companion to the custom amdxcffx64.dll component: lets the user swap in a
        // newer FSR SDK upscaler DLL than the one bundled with their OptiScaler
        // release, without waiting for an OptiScaler update. Same "bring your own
        // DLL" rules apply — the app never downloads this file.
        //
        // NOTE: the downloadable "FSR4 INT8 Extras" component installs the SAME
        // file, so the two are mutually exclusive per game (enforced in the UI).

        /// <summary>Filename of the FSR SDK upscaler DLL that OptiScaler loads.</summary>
        public const string CustomFsrSdkDllName = "amd_fidelityfx_upscaler_dx12.dll";

        /// <summary>Returns the root cache directory for custom FSR SDK DLL versions.</summary>
        public string GetCustomFsrSdkCachePath() => Path.Combine(_cacheDir, "CustomFsrSdk");

        /// <summary>Returns the cache directory for a specific custom FSR SDK DLL version.</summary>
        public string GetCustomFsrSdkCachePath(string version) => Path.Combine(GetCustomFsrSdkCachePath(), version);

        /// <summary>
        /// Returns the locally-imported custom FSR SDK DLL version labels
        /// (subdirectory names under Cache/CustomFsrSdk/ that contain the DLL).
        /// </summary>
        public List<string> GetDownloadedCustomFsrSdkVersions()
            => GetUserDllVersions(GetCustomFsrSdkCachePath(), CustomFsrSdkDllName);

        // ── Unified custom-DLL library (bring your own, one or more) ─────────────
        //
        // Flat per-file store: Cache/CustomDlls/<name>.dll + <name>.dll.json.
        // At install time these are overlaid on top of the OptiScaler install:
        // same-name entries overwrite the AMD/OptiScaler file, unknown names (e.g.
        // amdxcffx64.dll) are added alongside. Nothing here is ever downloaded — the
        // user supplies files they already possess.

        /// <summary>Root of the flat custom-DLL library.</summary>
        public string GetCustomDllsPath() => Path.Combine(_cacheDir, "CustomDlls");

        /// <summary>
        /// Lists the custom-DLL library (name → metadata), migrating any legacy
        /// single-file amdxcffx64 / SDK-package imports into it on first use.
        /// </summary>
        public List<CustomDllFileEntry> GetCustomDlls()
        {
            MigrateLegacyCustomImports();
            var dir = GetCustomDllsPath();
            var list = new List<CustomDllFileEntry>();
            if (!Directory.Exists(dir)) return list;

            foreach (var dll in Directory.GetFiles(dir, "*.dll").OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            {
                var meta = ReadCustomDllMeta(dll) ?? BuildCustomDllMeta(dll);
                list.Add(meta);
            }
            return list;
        }

        /// <summary>Deletes one entry (and its metadata) from the custom-DLL library.</summary>
        public void DeleteCustomDll(string name)
        {
            var dll = Path.Combine(GetCustomDllsPath(), Path.GetFileName(name));
            try { if (File.Exists(dll)) File.Delete(dll); } catch { }
            try { if (File.Exists(dll + ".json")) File.Delete(dll + ".json"); } catch { }
        }

        /// <summary>
        /// Imports one or more custom DLLs into the library. Each source may be a
        /// single .dll, a folder (searched recursively), or a .zip/.7z/.rar archive.
        /// Only valid 64-bit PEs are accepted; when the same DLL name appears in
        /// several places the largest copy wins (ML-bearing builds are the big ones);
        /// re-importing a name overwrites the previous entry. Returns the imported names.
        /// </summary>
        public async Task<List<string>> ImportCustomDllsAsync(IEnumerable<string> sources)
        {
            return await Task.Run(() =>
            {
                var destDir = GetCustomDllsPath();
                Directory.CreateDirectory(destDir);
                var imported = new List<string>();

                // clean dll name -> best source path found so far (largest wins)
                var candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var stagingDirs = new List<string>();

                void Consider(string path, string? nameOverride = null)
                {
                    try
                    {
                        var pe = PeFileInspector.Inspect(path);
                        if (!pe.IsValidPe || !pe.Is64Bit) return;
                        var name = nameOverride ?? Path.GetFileName(path);
                        if (!candidates.TryGetValue(name, out var existing)
                            || new FileInfo(path).Length > new FileInfo(existing).Length)
                            candidates[name] = path;
                    }
                    catch { /* unreadable file: skip */ }
                }

                try
                {
                    foreach (var source in sources)
                    {
                        if (File.Exists(source) && source.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        {
                            var pe = PeFileInspector.Inspect(source);
                            if (!pe.IsValidPe)
                                throw new InvalidDataException($"{Path.GetFileName(source)} is not a valid Windows DLL.");
                            if (!pe.Is64Bit)
                                throw new InvalidDataException($"{Path.GetFileName(source)} is not a 64-bit (x64) DLL.");
                            Consider(source);
                        }
                        else if (Directory.Exists(source))
                        {
                            foreach (var f in Directory.GetFiles(source, "*.dll", SearchOption.AllDirectories))
                                Consider(f);
                        }
                        else if (File.Exists(source))
                        {
                            // Archive: stage every .dll entry, then treat like a folder.
                            var staging = Path.Combine(Path.GetTempPath(), "osm_dllimport_" + Guid.NewGuid().ToString("N"));
                            Directory.CreateDirectory(staging);
                            stagingDirs.Add(staging);
                            using var archive = SharpCompress.Archives.ArchiveFactory.Open(source);
                            int i = 0;
                            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory
                                && (e.Key ?? "").EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                            {
                                var name = Path.GetFileName(entry.Key ?? $"entry{i}.dll");
                                // Index-prefixed on disk to avoid staging collisions; the
                                // candidate is keyed by the clean dll name.
                                var stagePath = Path.Combine(staging, $"{i++}_{name}");
                                using (var es = entry.OpenEntryStream())
                                using (var os = File.Create(stagePath))
                                    es.CopyTo(os, 81920);
                                Consider(stagePath, nameOverride: name);
                            }
                        }
                        else
                        {
                            throw new FileNotFoundException($"Source not found: {source}");
                        }
                    }

                    foreach (var (name, path) in candidates)
                    {
                        var dest = SafeDestinationPath(destDir, name);
                        File.Copy(path, dest, overwrite: true);
                        WriteCustomDllMeta(dest);
                        imported.Add(name);
                        Log.Write($"[CustomDlls] Imported {name} ({new FileInfo(dest).Length / 1024 / 1024.0:F1} MB)");
                    }
                }
                finally
                {
                    foreach (var d in stagingDirs)
                        try { Directory.Delete(d, true); } catch { }
                }

                if (imported.Count == 0)
                    throw new InvalidOperationException("No valid 64-bit DLLs were found in the selected source(s).");
                return imported;
            });
        }

        private CustomDllFileEntry BuildCustomDllMeta(string dllPath)
        {
            var pe = PeFileInspector.Inspect(dllPath);
            string sha;
            using (var s = System.Security.Cryptography.SHA256.Create())
            using (var fs = File.OpenRead(dllPath))
                sha = Convert.ToHexString(s.ComputeHash(fs));
            return new CustomDllFileEntry
            {
                Name = Path.GetFileName(dllPath),
                FileVersion = pe.FileVersion,
                Sha256 = sha,
                HasAuthenticodeSignature = pe.HasAuthenticodeSignature,
            };
        }

        private void WriteCustomDllMeta(string dllPath)
        {
            try
            {
                var meta = BuildCustomDllMeta(dllPath);
                File.WriteAllText(dllPath + ".json",
                    JsonSerializer.Serialize(meta, OptimizerContext.Default.CustomDllFileEntry));
            }
            catch (Exception ex) { Log.Write($"[CustomDlls] Failed to write metadata for {Path.GetFileName(dllPath)}: {ex.Message}"); }
        }

        private static CustomDllFileEntry? ReadCustomDllMeta(string dllPath)
        {
            try
            {
                var json = dllPath + ".json";
                if (!File.Exists(json)) return null;
                return JsonSerializer.Deserialize(File.ReadAllText(json), OptimizerContext.Default.CustomDllFileEntry);
            }
            catch { return null; }
        }

        /// <summary>
        /// One-time migration of the legacy per-version stores (single amdxcffx64
        /// imports and SDK packages) into the flat custom-DLL library. Legacy caches
        /// are left untouched; a marker file prevents re-runs.
        /// </summary>
        private void MigrateLegacyCustomImports()
        {
            var dir = GetCustomDllsPath();
            var marker = Path.Combine(dir, ".migrated");
            if (File.Exists(marker)) return;

            try
            {
                Directory.CreateDirectory(dir);

                var legacyFsr4 = GetDownloadedCustomFsr4Versions().FirstOrDefault();
                if (legacyFsr4 != null)
                {
                    var src = GetCustomFsr4DllPath(legacyFsr4);
                    if (File.Exists(src))
                    {
                        var dest = Path.Combine(dir, CustomFsr4DllName);
                        if (!File.Exists(dest)) { File.Copy(src, dest); WriteCustomDllMeta(dest); }
                        Log.Write($"[CustomDlls] Migrated legacy amdxcffx64.dll ({legacyFsr4}).");
                    }
                }

                var legacySdk = GetDownloadedCustomFsrSdkVersions().FirstOrDefault();
                if (legacySdk != null)
                {
                    foreach (var f in Directory.GetFiles(GetCustomFsrSdkCachePath(legacySdk), "*.dll"))
                    {
                        var dest = Path.Combine(dir, Path.GetFileName(f));
                        if (!File.Exists(dest)) { File.Copy(f, dest); WriteCustomDllMeta(dest); }
                    }
                    Log.Write($"[CustomDlls] Migrated legacy SDK package ({legacySdk}).");
                }

                File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
            }
            catch (Exception ex)
            {
                Log.Write($"[CustomDlls] Legacy migration failed (will retry next run): {ex.Message}");
            }
        }

        /// <summary>
        /// The FSR SDK DLLs an SDK package import looks for. The upscaler is the
        /// required anchor (it provides the version label); the rest are optional
        /// companions imported when present so a full FSR release can be swapped in.
        /// Matches the DLL names OptiScaler can load/override via [Libraries].
        /// AMD support libraries (amd_ags_x64.dll / amd_acs_x64.dll) are deliberately
        /// excluded: games often ship their own, and installs must never overwrite a
        /// game-owned file — only OptiScaler's FSR set is swapped in place.
        /// </summary>
        public static readonly string[] FsrSdkDllNames =
        {
            "amd_fidelityfx_upscaler_dx12.dll",
            "amd_fidelityfx_framegeneration_dx12.dll",
            "amd_fidelityfx_dx12.dll",
            "amd_fidelityfx_loader_dx12.dll",
            "amd_fidelityfx_denoiser_dx12.dll",
            "amd_fidelityfx_radiancecache_dx12.dll",
            "amd_fidelityfx_vk.dll",
        };

        /// <summary>
        /// Result of scanning a user-selected FSR SDK source (folder, archive, or
        /// single DLL). FoundFiles maps DLL name → readable path on disk; for
        /// archives the paths point into StagingDir, which the caller must delete
        /// (via Cleanup) once the import is finished or abandoned.
        /// </summary>
        public class FsrSdkScanResult
        {
            public string SourcePath { get; init; } = string.Empty;
            public string? StagingDir { get; set; }
            public Dictionary<string, string> FoundFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
            public PeFileInfo? UpscalerPe { get; set; }
            public bool HasUpscaler => FoundFiles.ContainsKey(CustomFsrSdkDllName);

            public void Cleanup()
            {
                if (string.IsNullOrEmpty(StagingDir)) return;
                try { if (Directory.Exists(StagingDir)) Directory.Delete(StagingDir, true); }
                catch { /* best effort */ }
            }
        }

        /// <summary>
        /// Scans a user-selected source for FSR SDK DLLs. Accepts:
        ///  - an extracted SDK folder (searched recursively),
        ///  - a .zip/.7z/.rar archive (matching entries are staged to a temp dir),
        ///  - a single .dll file (treated as the upscaler, renamed on import if needed).
        /// Only 64-bit PE files are accepted; when the same DLL appears in several
        /// subfolders, paths containing "signed" win, then the shallowest path.
        /// </summary>
        public async Task<FsrSdkScanResult> ScanFsrSdkSourceAsync(string sourcePath)
        {
            return await Task.Run(() =>
            {
                var result = new FsrSdkScanResult { SourcePath = sourcePath };

                if (File.Exists(sourcePath) && sourcePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    var pe = PeFileInspector.Inspect(sourcePath);
                    if (!pe.IsValidPe)
                        throw new InvalidDataException("The selected file is not a valid Windows DLL (missing PE header).");
                    if (!pe.Is64Bit)
                        throw new InvalidDataException("The selected DLL is not a 64-bit (x64) binary.");

                    var name = Path.GetFileName(sourcePath);
                    var known = FsrSdkDllNames.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
                    // Unknown filenames are imported as the upscaler (rename-on-import)
                    result.FoundFiles[known ?? CustomFsrSdkDllName] = sourcePath;
                }
                else if (Directory.Exists(sourcePath))
                {
                    CollectSdkDllsFromDirectory(sourcePath, result);
                }
                else if (File.Exists(sourcePath))
                {
                    // Archive: stage only entries whose filename matches the known set.
                    var staging = Path.Combine(Path.GetTempPath(), "OptiScaler_SdkImport_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(staging);
                    result.StagingDir = staging;

                    using (var archive = ArchiveFactory.Open(sourcePath))
                    {
                        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                        {
                            var entryName = Path.GetFileName(entry.Key ?? "");
                            if (!FsrSdkDllNames.Contains(entryName, StringComparer.OrdinalIgnoreCase)) continue;

                            // Preserve the entry's relative path (flattened safely) so
                            // duplicate names from different subfolders can be ranked.
                            var relative = (entry.Key ?? entryName).Replace('/', '_').Replace('\\', '_');
                            foreach (var c in Path.GetInvalidFileNameChars())
                                relative = relative.Replace(c, '_');
                            var dest = Path.Combine(staging, relative);
                            using var entryStream = entry.OpenEntryStream();
                            using var outStream = File.Create(dest);
                            entryStream.CopyTo(outStream, 81920);
                        }
                    }

                    // Rank staged copies exactly like directory scanning, using the
                    // original entry paths encoded in the flattened filenames.
                    CollectSdkDllsFromDirectory(staging, result);
                    if (result.FoundFiles.Count == 0)
                        Log.Write("[CustomFsrSdk] Archive contained no known FSR SDK DLLs.");
                }
                else
                {
                    throw new FileNotFoundException("Selected path does not exist.", sourcePath);
                }

                if (result.FoundFiles.TryGetValue(CustomFsrSdkDllName, out var upscalerPath))
                    result.UpscalerPe = PeFileInspector.Inspect(upscalerPath);

                Log.Write($"[CustomFsrSdk] Scan of '{sourcePath}' found: {string.Join(", ", result.FoundFiles.Keys)}");
                return result;
            });
        }

        /// <summary>
        /// Finds the best candidate for each known SDK DLL inside a directory tree.
        /// Prefers 64-bit PEs whose path mentions "signed", then the LARGEST file,
        /// then the shallowest path. Size matters: SDK packages can carry several
        /// different builds of the same DLL (e.g. the FidelityFX SDK ships a reduced
        /// upscaler with its denoiser sample and the full ML-capable one with its FSR
        /// sample) — the ML-model-bearing build is dramatically larger, and picking a
        /// reduced copy silently loses the FSR 4 provider.
        /// </summary>
        private static void CollectSdkDllsFromDirectory(string root, FsrSdkScanResult result)
        {
            foreach (var dllName in FsrSdkDllNames)
            {
                var candidates = Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories)
                    .Where(f =>
                    {
                        var fn = Path.GetFileName(f);
                        // Exact name, or a staged archive entry whose flattened name ends with it
                        return fn.Equals(dllName, StringComparison.OrdinalIgnoreCase)
                            || (fn.EndsWith("_" + dllName, StringComparison.OrdinalIgnoreCase));
                    })
                    .Where(f =>
                    {
                        try
                        {
                            var pe = PeFileInspector.Inspect(f);
                            return pe.IsValidPe && pe.Is64Bit;
                        }
                        catch { return false; }
                    })
                    .OrderByDescending(f => f.Contains("signed", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(f => { try { return new FileInfo(f).Length; } catch { return 0L; } })
                    .ThenBy(f => f.Count(c => c == Path.DirectorySeparatorChar || c == '_'))
                    .ToList();

                if (candidates.Count > 0 && !result.FoundFiles.ContainsKey(dllName))
                {
                    result.FoundFiles[dllName] = candidates[0];
                    if (candidates.Count > 1)
                    {
                        Log.Write($"[CustomFsrSdk] Multiple copies of {dllName} found ({candidates.Count}):");
                        foreach (var c in candidates)
                        {
                            long size = 0; string ver = "?";
                            try { size = new FileInfo(c).Length; } catch { }
                            try { ver = PeFileInspector.Inspect(c).FileVersion ?? "?"; } catch { }
                            var mark = c == candidates[0] ? " <= chosen" : "";
                            Log.Write($"[CustomFsrSdk]   {c} (v{ver}, {size / 1024 / 1024.0:F1} MB){mark}");
                        }
                    }
                }
            }
        }

        // ── Shared bring-your-own-DLL helpers ─────────────────────────────────────

        private static List<string> GetUserDllVersions(string cacheRoot, string dllName)
        {
            var versions = new List<string>();
            if (!Directory.Exists(cacheRoot)) return versions;

            foreach (var dir in Directory.GetDirectories(cacheRoot))
            {
                if (File.Exists(Path.Combine(dir, dllName)))
                    versions.Add(Path.GetFileName(dir));
            }

            return VersionOrder.Newest(versions);
        }

    }
}
