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
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _baseDir = Path.Combine(appData, "OptiscalerManager");
            _cacheDir = Path.Combine(_baseDir, "Cache");
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

                    // Migration: configs inherited from OptiScaler Client (or written by
                    // older Manager versions) either leave the App repo empty or point it
                    // at the Client repos. This app's own releases live in
                    // filobus97/optiscaler-manager — retarget once so the in-app update
                    // check queries the right repository.
                    if (string.IsNullOrWhiteSpace(_config.App.RepoOwner) ||
                        string.IsNullOrWhiteSpace(_config.App.RepoName) ||
                        _config.App.RepoName.Equals("Optiscaler-Client", StringComparison.OrdinalIgnoreCase))
                    {
                        _config.App = new RepositoryConfig { RepoOwner = "filobus97", RepoName = "optiscaler-manager" };
                        try
                        {
                            var json = JsonSerializer.Serialize(_config, OptimizerContext.Default.AppConfiguration);
                            File.WriteAllText(_configFile, json);
                            Log.Write("[Config] Retargeted App update repo to filobus97/optiscaler-manager.");
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

        private void LoadReleasesCache()
        {
            // Only load once per process (static field)
            if (_releasesCache.Releases.Count > 0) return;
            if (!File.Exists(_releasesCacheFile)) return;
            try
            {
                var json = File.ReadAllText(_releasesCacheFile);
                var loaded = JsonSerializer.Deserialize(json, OptimizerContext.Default.OptiScalerReleasesCache);
                if (loaded != null)
                {
                    _releasesCache = loaded;
                    RebuildInMemoryCacheFromReleases();
                    Log.Write($"[ReleasesCache] Loaded {_releasesCache.Releases.Count} entries from local cache (last updated: {_releasesCache.LastUpdated})");
                }
            }
            catch (Exception ex)
            {
                Log.Write($"[ReleasesCache] Failed to load: {ex.Message}");
            }
        }

        private void SaveReleasesCache()
        {
            try
            {
                var json = JsonSerializer.Serialize(_releasesCache, OptimizerContext.Default.OptiScalerReleasesCache);
                File.WriteAllText(_releasesCacheFile, json);
            }
            catch (Exception ex)
            {
                Log.Write($"[ReleasesCache] Failed to save: {ex.Message}");
            }
        }

        // ── Extras (FSR4 INT8) cache ──────────────────────────────────────────────

        private void LoadExtrasCache()
        {
            if (_extrasCache.Releases.Count > 0) return;
            var file = Path.Combine(_baseDir, "extras_cache.json");
            if (!File.Exists(file)) return;
            try
            {
                var json = File.ReadAllText(file);
                var loaded = JsonSerializer.Deserialize(json, OptimizerContext.Default.ExtrasReleasesCache);
                if (loaded != null)
                {
                    _extrasCache = loaded;
                    RebuildInMemoryExtrasCache();
                    Log.Write($"[ExtrasCache] Loaded {_extrasCache.Releases.Count} entries from local cache.");
                }
            }
            catch (Exception ex)
            {
                Log.Write($"[ExtrasCache] Failed to load: {ex.Message}");
            }
        }

        private void SaveExtrasCache()
        {
            try
            {
                var file = Path.Combine(_baseDir, "extras_cache.json");
                var json = JsonSerializer.Serialize(_extrasCache, OptimizerContext.Default.ExtrasReleasesCache);
                File.WriteAllText(file, json);
                Log.Write($"[ExtrasCache] Saved {_extrasCache.Releases.Count} entries to {file}.");
            }
            catch (Exception ex)
            {
                Log.Write($"[ExtrasCache] Failed to save: {ex.Message}");
            }
        }

        private void RebuildInMemoryExtrasCache()
        {
            if (_extrasCache.Releases == null || _extrasCache.Releases.Count == 0)
            {
                _cachedExtrasVersions = new System.Collections.Generic.List<string>();
                return;
            }

            _cachedLatestExtrasVersion = _extrasCache.Releases.FirstOrDefault(r => r.IsLatest)?.Version
                ?? _extrasCache.Releases.FirstOrDefault()?.Version;

            _cachedExtrasVersions = _extrasCache.Releases.Select(r => r.Version).Distinct().ToList();
            Log.Write($"[ExtrasCache] Rebuilt in-memory: {_cachedExtrasVersions.Count} version(s), latest={_cachedLatestExtrasVersion}");
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

            Version parse(string v)
            {
                if (string.IsNullOrEmpty(v)) return new Version(0, 0);
                var clean = new string(v.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).TrimEnd('.');
                if (!string.IsNullOrEmpty(clean) && Version.TryParse(clean, out var parsed)) return parsed;
                return new Version(0, 0);
            }

            int parseSuffixValue(string v)
            {
                var match = System.Text.RegularExpressions.Regex.Match(v, @"\d+$");
                if (match.Success && int.TryParse(match.Value, out int val)) return val;
                return 0;
            }

            var stablesList = all.Where(r => !r.IsBeta)
                                 .OrderByDescending(r => parse(r.Version))
                                 .ThenByDescending(r => parseSuffixValue(r.Version))
                                 .ThenByDescending(r => r.Version, StringComparer.OrdinalIgnoreCase)
                                 .ToList();

            var betasList = all.Where(r => r.IsBeta)
                               .OrderByDescending(r => parse(r.Version))
                               .ThenByDescending(r => parseSuffixValue(r.Version))
                               .ThenByDescending(r => r.Version, StringComparer.OrdinalIgnoreCase)
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

                        await Task.WhenAll(optiVersionsTask, optiBetasTask, fakeTask, extrasTask, optiPatcherTask);

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
                        IsBeta = isBeta,
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

        // Legacy helper kept for CheckComponentUpdateAsync compatibility
        private async Task<(System.Collections.Generic.List<string> versions, string? latestVersion)> FetchAllComponentVersionsAsync(RepositoryConfig config)
        {
            var versions = new System.Collections.Generic.List<string>();
            string? latestVersion = null;
            var repoLabel = $"{config.RepoOwner}/{config.RepoName}";
            try
            {
                if (string.IsNullOrEmpty(config.RepoOwner) || string.IsNullOrEmpty(config.RepoName))
                {
                    Log.Write($"[FetchVersions] Skipping {repoLabel}: empty config");
                    return (versions, latestVersion);
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
                    if (element.TryGetProperty("tag_name", out var tagName))
                    {
                        var version = tagName.GetString();
                        if (version != null)
                        {
                            if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                                version = version.Substring(1);
                            versions.Add(version);

                            // Check if this is marked as latest release
                            if (latestVersion == null && element.TryGetProperty("prerelease", out var prerelease) && !prerelease.GetBoolean())
                            {
                                latestVersion = version;
                                Log.Write($"[FetchVersions] {repoLabel} → Latest stable: {latestVersion}");
                            }
                        }
                    }
                }
                Log.Write($"[FetchVersions] {repoLabel} → {versions.Count} version(s): [{string.Join(", ", versions)}]");
            }
            catch (Exception ex)
            {
                Log.Write($"[FetchVersions] {repoLabel} → ERROR: {ex.Message}");
            }

            return (versions, latestVersion);
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
        /// Returns true if the DLL for the given Extras version is already cached.
        /// </summary>
        public bool IsExtrasDllCached(string version)
            => File.Exists(Path.Combine(GetExtrasDllCachePath(version), "amd_fidelityfx_upscaler_dx12.dll"));

        /// <summary>
        /// Downloads the Extras zip for the given version and extracts amd_fidelityfx_upscaler_dx12.dll
        /// into the per-version cache folder. Returns the path to the DLL file.
        /// </summary>
        public async Task<string> DownloadExtrasDllAsync(string version, IProgress<double>? progress = null)
        {
            var extractDir = GetExtrasDllCachePath(version);
            var dllPath = Path.Combine(extractDir, "amd_fidelityfx_upscaler_dx12.dll");

            if (File.Exists(dllPath))
            {
                Log.Write($"[ExtrasDownload] DLL for v{version} already cached at {dllPath}");
                return dllPath;
            }

            // Resolve download URL (cache first, then API)
            string? downloadUrl = _extrasCache.Releases
                .FirstOrDefault(r => string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase))
                ?.DownloadUrl;

            if (string.IsNullOrEmpty(downloadUrl))
            {
                // Try to fetch from API
                Log.Write($"[ExtrasDownload] No cached URL for v{version}, trying API...");
                var config = _config.OptiScalerExtras;
                foreach (var prefix in new[] { "v", "" })
                {
                    try
                    {
                        var apiUrl = $"https://api.github.com/repos/{config.RepoOwner}/{config.RepoName}/releases/tags/{prefix}{version}";
                        var response = await GetWithRetryAsync(() => _httpClient, apiUrl, maxRetries: 2, timeoutSeconds: 15);
                        if (!response.IsSuccessStatusCode) continue;

                        var json = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("assets", out var assets))
                        {
                            foreach (var asset in assets.EnumerateArray())
                            {
                                if (asset.TryGetProperty("browser_download_url", out var urlProp))
                                {
                                    var u = urlProp.GetString();
                                    if (u != null && (u.EndsWith(".zip") || u.EndsWith(".7z")))
                                    {
                                        downloadUrl = u;
                                        break;
                                    }
                                }
                            }
                        }
                        if (!string.IsNullOrEmpty(downloadUrl)) break;
                    }
                    catch (Exception ex) { Log.Write($"[ExtrasDownload] API lookup attempt failed: {ex.Message}"); }
                }
            }

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
                        if (Path.GetFileName(entry.Key ?? "").Equals("amd_fidelityfx_upscaler_dx12.dll",
                            StringComparison.OrdinalIgnoreCase))
                        {
                            var dest = SafeDestinationPath(extractDir, "amd_fidelityfx_upscaler_dx12.dll");
                            using var entryStream = entry.OpenEntryStream();
                            using var outStream = File.Create(dest);
                            entryStream.CopyTo(outStream, 81920);
                            Log.Write($"[ExtrasDownload] Extracted DLL to {dest}");
                            break;
                        }
                    }
                });
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }

            if (!File.Exists(dllPath))
                throw new Exception("amd_fidelityfx_upscaler_dx12.dll not found inside the downloaded archive.");

            return dllPath;
        }

        // ── AMD FidelityFX SDK (official, open-source) ────────────────────────────

        /// <summary>Owner of AMD's open-source FidelityFX SDK on GitHub.</summary>
        public const string FidelityFxSdkRepoOwner = "GPUOpen-LibrariesAndSDKs";
        /// <summary>Name of AMD's open-source FidelityFX SDK repository.</summary>
        public const string FidelityFxSdkRepoName = "FidelityFX-SDK";

        /// <summary>Cache root for downloaded FidelityFX SDK files.</summary>
        public string GetFidelityFxSdkCachePath() => Path.Combine(_cacheDir, "FidelityFxSdk");

        /// <summary>
        /// Repo path of AMD's signed prebuilt FFX DLLs. This is the exact directory
        /// OptiScaler's own release packaging copies from (via its FidelityFX-SDK-v2
        /// submodule) — the ML-capable, signed builds, unlike the sample binaries
        /// attached to the SDK's release zips.
        /// </summary>
        public const string FidelityFxSignedBinPath = "Kits/FidelityFX/signedbin";

        /// <summary>
        /// The FidelityFX-SDK commit OptiScaler ≥0.9.4 pins (signedbin of the
        /// FFX 2.3 / FSR 4.1.1 era — 0.9.4 has hook patterns for these binaries).
        /// </summary>
        public const string FidelityFxSdkFallbackRef = "60f4ea81909200d8542eca14dccb2628b763a9a3";

        /// <summary>
        /// The FidelityFX-SDK commit OptiScaler 0.9.3 pins (FSR 4.1.0). OptiScaler
        /// hooks the upscaler's model-selection code by byte-pattern, and 0.9.3 only
        /// carries patterns for the 4.0.3/4.1.0 binaries — a 4.1.1 upscaler cannot be
        /// hooked by it and FSR 4 silently disappears from the menu.
        /// </summary>
        public const string FidelityFxSdk410Ref = "e236f2304dcda35f282fdddd085f41e2ff48c86a";

        /// <summary>
        /// The signedbin git refs to try, best first, for a given OptiScaler release.
        /// Each OptiScaler build can only hook the upscaler binaries it has byte
        /// patterns for (0.9.4 changelog: "Updated model/preset hooking for FSR
        /// 4.1.1"), so the AMD files must be taken at the submodule commit that
        /// release pins — newer is NOT better here. Resolution order: the pin
        /// resolved live from the OptiScaler repo (when available), then the static
        /// era pin for the version, then the repo head as a last resort for unknown
        /// newer versions.
        /// </summary>
        public static string[] SignedBinRefsFor(string? optiscalerVersion, string? resolvedPin = null)
        {
            var refs = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(resolvedPin)) refs.Add(resolvedPin!);

            // Parse the numeric part of the version ("0.9.4-final" → 0.9.4).
            Version? v = null;
            if (!string.IsNullOrWhiteSpace(optiscalerVersion))
            {
                var numeric = new string(optiscalerVersion!.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).TrimEnd('.');
                Version.TryParse(numeric.Contains('.') ? numeric : numeric + ".0", out v);
            }

            if (v is not null && v < new Version(0, 9, 4))
            {
                // 0.9.3 and older: only the 4.1.0-era signedbin is hookable.
                if (!refs.Contains(FidelityFxSdk410Ref)) refs.Add(FidelityFxSdk410Ref);
            }
            else
            {
                // 0.9.4+ (or unknown): the 4.1.1-era pin, then the live head for
                // releases newer than this app's knowledge.
                if (!refs.Contains(FidelityFxSdkFallbackRef)) refs.Add(FidelityFxSdkFallbackRef);
                refs.Add("main");
                refs.Add("master");
            }
            return refs.ToArray();
        }

        /// <summary>
        /// Resolves the FidelityFX-SDK-v2 submodule commit pinned by a given
        /// OptiScaler release tag, via the GitHub contents API. Returns null on any
        /// failure (offline, unknown tag) — callers fall back to the static era pins.
        /// </summary>
        public async Task<string?> ResolveSignedBinPinAsync(string optiscalerVersion)
        {
            foreach (var tag in new[] { $"v{optiscalerVersion}", optiscalerVersion })
            {
                try
                {
                    var url = $"https://api.github.com/repos/{_config.OptiScaler.RepoOwner}/{_config.OptiScaler.RepoName}" +
                              $"/contents/external/FidelityFX-SDK-v2?ref={Uri.EscapeDataString(tag)}";
                    var resp = await GetWithRetryAsync(() => _httpClient, url, maxRetries: 1, timeoutSeconds: 15);
                    if (!resp.IsSuccessStatusCode) continue;

                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("sha", out var sha))
                    {
                        var pin = sha.GetString();
                        if (!string.IsNullOrWhiteSpace(pin))
                        {
                            Log.Write($"[FidelityFxSdk] OptiScaler {tag} pins signedbin commit {pin[..7]}");
                            return pin;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Write($"[FidelityFxSdk] Pin resolution for '{tag}' failed: {ex.Message}");
                }
            }
            return null;
        }

        /// <summary>
        /// Downloads AMD's signed prebuilt FFX DLL set from the official
        /// FidelityFX-SDK repository tree (signedbin) into the cache and returns
        /// (versionLabel, dirPath). The revision is MATCHED to the OptiScaler release
        /// being installed (see <see cref="SignedBinRefsFor"/>) — each OptiScaler can
        /// only hook the upscaler binaries it knows. Every file is validated as a
        /// 64-bit PE (a Git-LFS pointer or an error page would fail validation).
        /// This is AMD's official open-source SDK repo — not the proprietary
        /// amdxcffx64.dll, which remains strictly bring-your-own.
        /// </summary>
        public async Task<(string version, string dirPath)> DownloadFidelityFxSignedBinAsync(
            IProgress<double>? progress = null, string? optiscalerVersion = null)
        {
            string? resolvedPin = null;
            if (!string.IsNullOrWhiteSpace(optiscalerVersion))
                resolvedPin = await ResolveSignedBinPinAsync(optiscalerVersion!);

            var refsToTry = SignedBinRefsFor(optiscalerVersion, resolvedPin);
            Exception? lastError = null;

            foreach (var gitRef in refsToTry)
            {
                var stageDir = Path.Combine(GetFidelityFxSdkCachePath(), "staging_" + gitRef[..Math.Min(12, gitRef.Length)]);
                try
                {
                    Directory.CreateDirectory(stageDir);

                    async Task<bool> FetchAsync(string dllName, bool required)
                    {
                        var url = $"https://raw.githubusercontent.com/{FidelityFxSdkRepoOwner}/{FidelityFxSdkRepoName}/{gitRef}/{FidelityFxSignedBinPath}/{dllName}";
                        var dest = Path.Combine(stageDir, dllName);
                        try
                        {
                            Log.Write($"[FidelityFxSdk] Fetching {url}");
                            await StreamToFileAsync(() => _httpClient, url, dest, required ? progress : null, 40 * 1024 * 1024);
                            var pe = PeFileInspector.Inspect(dest);
                            if (!pe.IsValidPe || !pe.Is64Bit || new FileInfo(dest).Length < 10 * 1024)
                                throw new InvalidDataException($"{dllName} from ref '{gitRef}' is not a valid 64-bit DLL (LFS pointer or error page?).");
                            return true;
                        }
                        catch (Exception ex) when (!required)
                        {
                            Log.Write($"[FidelityFxSdk] Optional {dllName} not available at '{gitRef}': {ex.Message}");
                            try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                            return false;
                        }
                    }

                    // The upscaler is mandatory; companions best-effort.
                    await FetchAsync(CustomFsrSdkDllName, required: true);
                    foreach (var name in FsrSdkDllNames)
                        if (!name.Equals(CustomFsrSdkDllName, StringComparison.OrdinalIgnoreCase))
                            await FetchAsync(name, required: false);

                    var upscalerPe = PeFileInspector.Inspect(Path.Combine(stageDir, CustomFsrSdkDllName));
                    var version = upscalerPe.FileVersion ?? $"signedbin-{gitRef[..Math.Min(7, gitRef.Length)]}";
                    Log.Write($"[FidelityFxSdk] signedbin fetched from '{gitRef}': upscaler v{version}");
                    return (version, stageDir);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Log.Write($"[FidelityFxSdk] Ref '{gitRef}' failed: {ex.Message}");
                    try { if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true); } catch { }
                }
            }

            throw new Exception(
                "Could not download AMD's signed FFX DLLs from the FidelityFX-SDK repository. " +
                "Check your connection and try again.", lastError);
        }

        // ── OptiPatcher cache ─────────────────────────────────────────────────────

        private void LoadOptiPatcherCache()
        {
            if (_optiPatcherCache.Releases.Count > 0) return;
            var file = Path.Combine(_baseDir, "optipatcher_cache.json");
            if (!File.Exists(file)) return;
            try
            {
                var json = File.ReadAllText(file);
                var loaded = JsonSerializer.Deserialize(json, OptimizerContext.Default.OptiPatcherReleasesCache);
                if (loaded != null)
                {
                    _optiPatcherCache = loaded;
                    RebuildInMemoryOptiPatcherCache();
                    Log.Write($"[OptiPatcherCache] Loaded {_optiPatcherCache.Releases.Count} entries from local cache.");
                }
            }
            catch (Exception ex)
            {
                Log.Write($"[OptiPatcherCache] Failed to load: {ex.Message}");
            }
        }

        private void SaveOptiPatcherCache()
        {
            try
            {
                var file = Path.Combine(_baseDir, "optipatcher_cache.json");
                var json = JsonSerializer.Serialize(_optiPatcherCache, OptimizerContext.Default.OptiPatcherReleasesCache);
                File.WriteAllText(file, json);
                Log.Write($"[OptiPatcherCache] Saved {_optiPatcherCache.Releases.Count} entries.");
            }
            catch (Exception ex)
            {
                Log.Write($"[OptiPatcherCache] Failed to save: {ex.Message}");
            }
        }

        private void RebuildInMemoryOptiPatcherCache()
        {
            if (_optiPatcherCache.Releases == null || _optiPatcherCache.Releases.Count == 0)
            {
                _cachedOptiPatcherVersions = new System.Collections.Generic.List<string>();
                return;
            }
            _cachedLatestOptiPatcherVersion = _optiPatcherCache.Releases.FirstOrDefault(r => r.IsLatest)?.Version
                ?? _optiPatcherCache.Releases.FirstOrDefault()?.Version;
            _cachedOptiPatcherVersions = _optiPatcherCache.Releases.Select(r => r.Version).Distinct().ToList();
            Log.Write($"[OptiPatcherCache] Rebuilt in-memory: {_cachedOptiPatcherVersions.Count} version(s), latest={_cachedLatestOptiPatcherVersion}");
        }

        // ── Fakenvapi cache ───────────────────────────────────────────────────────

        private void LoadFakenvapiCache()
        {
            if (_fakenvapiCache.Releases.Count > 0) return;
            var file = Path.Combine(_baseDir, "fakenvapi_cache.json");
            if (!File.Exists(file)) return;
            try
            {
                var json = File.ReadAllText(file);
                var loaded = JsonSerializer.Deserialize(json, OptimizerContext.Default.FakenvapiReleasesCache);
                if (loaded != null)
                {
                    _fakenvapiCache = loaded;
                    RebuildInMemoryFakenvapiCache();
                    Log.Write($"[FakenvapiCache] Loaded {_fakenvapiCache.Releases.Count} entries from local cache.");
                }
            }
            catch (Exception ex)
            {
                Log.Write($"[FakenvapiCache] Failed to load: {ex.Message}");
            }
        }

        private void SaveFakenvapiCache()
        {
            try
            {
                var file = Path.Combine(_baseDir, "fakenvapi_cache.json");
                var json = JsonSerializer.Serialize(_fakenvapiCache, OptimizerContext.Default.FakenvapiReleasesCache);
                File.WriteAllText(file, json);
                Log.Write($"[FakenvapiCache] Saved {_fakenvapiCache.Releases.Count} entries.");
            }
            catch (Exception ex)
            {
                Log.Write($"[FakenvapiCache] Failed to save: {ex.Message}");
            }
        }

        private void RebuildInMemoryFakenvapiCache()
        {
            if (_fakenvapiCache.Releases == null || _fakenvapiCache.Releases.Count == 0)
            {
                _cachedFakenvapiVersions = new System.Collections.Generic.List<string>();
                return;
            }
            _cachedLatestFakenvapiVersion = _fakenvapiCache.Releases.FirstOrDefault(r => r.IsLatest)?.Version
                ?? _fakenvapiCache.Releases.FirstOrDefault()?.Version;

            static Version parseFakenvapiVer(string v)
            {
                var clean = v.TrimStart('v');
                var dash = clean.IndexOf('-');
                if (dash >= 0) clean = clean[..dash];
                return Version.TryParse(clean, out var p) ? p : new Version(0, 0);
            }

            _cachedFakenvapiVersions = _fakenvapiCache.Releases
                .Select(r => r.Version)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(v => parseFakenvapiVer(v))
                .ThenByDescending(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
            Log.Write($"[FakenvapiCache] Rebuilt in-memory: {_cachedFakenvapiVersions.Count} version(s), latest={_cachedLatestFakenvapiVersion}");
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

            // Resolve download URL (cache first, then API)
            string? downloadUrl = _fakenvapiCache.Releases
                .FirstOrDefault(r => string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase))
                ?.DownloadUrl;

            if (string.IsNullOrEmpty(downloadUrl))
            {
                var config = _config.Fakenvapi;
                foreach (var prefix in new[] { "v", "" })
                {
                    try
                    {
                        var apiUrl = $"https://api.github.com/repos/{config.RepoOwner}/{config.RepoName}/releases/tags/{prefix}{version}";
                        var resp = await GetWithRetryAsync(() => _httpClient, apiUrl, maxRetries: 2, timeoutSeconds: 15);
                        if (!resp.IsSuccessStatusCode) continue;

                        var json = await resp.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("assets", out var assets))
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
                        // Fallback to zipball
                        if (string.IsNullOrEmpty(downloadUrl) && doc.RootElement.TryGetProperty("zipball_url", out var zipball))
                            downloadUrl = zipball.GetString();

                        if (!string.IsNullOrEmpty(downloadUrl)) break;
                    }
                    catch (Exception ex) { Log.Write($"[FakenvapiDownload] API lookup attempt failed: {ex.Message}"); }
                }
            }

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
            return versions.OrderByDescending(v => v).ToList();
        }

        public void DeleteFakenvapiCache(string version)
        {
            var cachePath = GetFakenvapiCachePath(version);
            if (Directory.Exists(cachePath))
            {
                Directory.Delete(cachePath, true);
            }
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
        /// Returns true if OptiPatcher.asi for the given version is already cached.
        /// </summary>
        public bool IsOptiPatcherCached(string version)
            => File.Exists(Path.Combine(GetOptiPatcherCachePath(version), "OptiPatcher.asi"));

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

            // Resolve download URL (cache first, then API)
            string? downloadUrl = _optiPatcherCache.Releases
                .FirstOrDefault(r => string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase))
                ?.DownloadUrl;

            if (string.IsNullOrEmpty(downloadUrl))
            {
                var config = _config.OptiPatcher;
                foreach (var prefix in new[] { "v", "" })
                {
                    try
                    {
                        var apiUrl = $"https://api.github.com/repos/{config.RepoOwner}/{config.RepoName}/releases/tags/{prefix}{version}";
                        var response = await GetWithRetryAsync(() => _httpClient, apiUrl, maxRetries: 2, timeoutSeconds: 15);
                        if (!response.IsSuccessStatusCode) continue;

                        var json = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("assets", out var assets))
                        {
                            foreach (var asset in assets.EnumerateArray())
                            {
                                if (asset.TryGetProperty("browser_download_url", out var urlProp) &&
                                    asset.TryGetProperty("name", out var nameProp))
                                {
                                    var assetName = nameProp.GetString() ?? "";
                                    var assetUrl  = urlProp.GetString();
                                    if (assetUrl != null && assetName.EndsWith(".asi", StringComparison.OrdinalIgnoreCase))
                                    {
                                        downloadUrl = assetUrl;
                                        break;
                                    }
                                }
                            }
                        }
                        if (!string.IsNullOrEmpty(downloadUrl)) break;
                    }
                    catch (Exception ex) { Log.Write($"[OptiPatcherDownload] API lookup attempt failed: {ex.Message}"); }
                }
            }

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

        public async Task DownloadAndExtractAllAsync()
        {
            var errors = new System.Collections.Generic.List<string>();

            // Try to download each component independently
            try
            {
                // We no longer auto-download OptiScaler here. It's fetched per-version on demand.
            }
            catch (Exception ex)
            {
                errors.Add($"OptiScaler: {ex.Message}");
            }

            try
            {
                await DownloadAndExtractFakenvapiAsync();
            }
            catch (Exception ex)
            {
                errors.Add($"Fakenvapi: {ex.Message}");
            }

            // NukemFG is never downloaded automatically — it is always provided manually.
            // If the DLL is not present yet, we prompt the user here.
            if (!IsNukemFGInstalled)
            {
                bool provided = await ProvideNukemFGManuallyAsync(isUpdate: false);
                if (!provided)
                    errors.Add("NukemFG: Manual download was skipped.");
            }

            // If all failed, throw
            if (errors.Count == 3)
            {
                throw new Exception($"All downloads failed:\n{string.Join("\n", errors)}");
            }

            // If some failed, store in LastError but don't throw
            if (errors.Count > 0)
            {
                LastError = new Exception($"Some downloads failed:\n{string.Join("\n", errors)}");
            }
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

        public static bool IsOptiScalerDownloadActive(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return false;
            lock (_downloadLock)
            {
                return _activeOptiDownloads.Contains(version);
            }
        }

        public async Task DownloadAndExtractFakenvapiAsync()
        {
            var version = _cachedLatestFakenvapiVersion ?? _remoteVersions.FakenvapiVersion;
            if (version == null)
                throw new Exception("No remote version available for Fakenvapi");

            await DownloadFakenvapiAsync(version);

            _localVersions.FakenvapiVersion = version;
            SaveLocalVersions();
            OnStatusChanged?.Invoke();
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

        private async Task DownloadAndExtractComponentAsync(
            string componentName,
            RepositoryConfig config,
            string version,
            string cacheSubDir)
        {
            LastError = null;
            try
            {
                // Get release info (with retry)
                var url = $"https://api.github.com/repos/{config.RepoOwner}/{config.RepoName}/releases/latest";
                var response = await GetWithRetryAsync(() => _httpClient, url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);

                // Find download URL
                string? downloadUrl = null;
                if (doc.RootElement.TryGetProperty("assets", out var assets))
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

                // Fallback to zipball_url if no assets found
                if (downloadUrl == null && doc.RootElement.TryGetProperty("zipball_url", out var zipballProp))
                    downloadUrl = zipballProp.GetString();

                if (downloadUrl == null)
                    throw new Exception($"No downloadable asset found for {componentName}. Check if the repository has releases with downloadable files.");

                var tempZip = Path.Combine(Path.GetTempPath(), $"{componentName}_{Guid.NewGuid()}.zip");
                var extractPath = Path.Combine(_cacheDir, cacheSubDir);
                try
                {
                    // Stream download with retry and per-attempt timeout
                    Log.Write($"[Download] Streaming {componentName} from {Path.GetFileName(downloadUrl)}");
                    await StreamToFileAsync(() => _httpClient, downloadUrl, tempZip);

                    // Extract with path traversal validation
                    if (Directory.Exists(extractPath))
                        Directory.Delete(extractPath, true);
                    Directory.CreateDirectory(extractPath);

                    await Task.Run(() =>
                    {
                        using var archive = ArchiveFactory.Open(tempZip);
                        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                        {
                            var destPath = SafeDestinationPath(extractPath, entry.Key ?? string.Empty);
                            var destDir = Path.GetDirectoryName(destPath);
                            if (destDir != null && !Directory.Exists(destDir))
                                Directory.CreateDirectory(destDir);
                            using var entryStream = entry.OpenEntryStream();
                            using var fileStream = File.Create(destPath);
                            entryStream.CopyTo(fileStream, 81920);
                        }
                    });
                }
                catch (HttpRequestException httpEx)
                {
                    throw new Exception($"Failed to download {componentName}: {httpEx.Message}", httpEx);
                }
                catch (IOException ioEx)
                {
                    throw new Exception($"Failed to extract {componentName}: {ioEx.Message}", ioEx);
                }
                finally
                {
                    try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                }
            }
            catch (Exception ex)
            {
                LastError = ex;
                throw;
            }
        }

        public string GetOptiScalerCachePath() => Path.Combine(_cacheDir, "OptiScaler", OptiScalerVersion ?? "latest");
        public string GetOptiScalerCachePath(string version) => Path.Combine(_cacheDir, "OptiScaler", version);
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
            // Better to sort by length and alpha descending:
            versions.Sort((a, b) =>
            {
                var comparison = b.Length.CompareTo(a.Length);
                if (comparison == 0) return string.Compare(b, a, StringComparison.OrdinalIgnoreCase);
                return comparison;
            });
            return versions;
        }

        public void DeleteOptiScalerCache(string version)
        {
            var cachePath = Path.Combine(_cacheDir, "OptiScaler", version);
            if (Directory.Exists(cachePath))
            {
                Directory.Delete(cachePath, true);
            }
            // Also remove from custom versions list if present
            if (_config.CustomOptiScalerVersions.Remove(version))
                SaveConfiguration();
            // Keep static cache in sync
            _cachedOptiScalerVersions?.Remove(version);
            if (_localVersions.OptiScalerVersion == version)
            {
                _localVersions.OptiScalerVersion = GetDownloadedOptiScalerVersions().FirstOrDefault();
                SaveLocalVersions();
            }
        }

        /// <summary>Returns the set of custom OptiScaler version names imported by the user.</summary>
        public System.Collections.Generic.HashSet<string> CustomVersions
            => new(_config.CustomOptiScalerVersions, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Imports a custom OptiScaler version from a 7z archive.
        /// Extracts to Cache/OptiScaler/{versionName}/ and registers it.
        /// </summary>
        public async Task<string> ImportCustomOptiScalerVersionAsync(string archivePath)
        {
            var fileName = Path.GetFileNameWithoutExtension(archivePath);
            // Sanitize to produce a safe directory name
            var versionName = "custom-" + SanitizeVersionName(fileName);
            var targetDir = Path.Combine(_cacheDir, "OptiScaler", versionName);

            if (Directory.Exists(targetDir))
                Directory.Delete(targetDir, true);
            Directory.CreateDirectory(targetDir);

            try
            {
                await Task.Run(() =>
                {
                    using var stream = File.OpenRead(archivePath);
                    using var archive = SharpCompress.Archives.ArchiveFactory.Open(stream);
                    var fileEntries = archive.Entries.Where(e => !e.IsDirectory).ToList();
                    var commonPrefix = FindCommonArchivePrefix(fileEntries.Select(e => e.Key).ToList());

                    // Use ExtractAllEntries (sequential reader) which works for all formats
                    // including solid RAR/7z where random OpenEntryStream() can fail.
                    using var reader = archive.ExtractAllEntries();
                    while (reader.MoveToNextEntry())
                    {
                        if (reader.Entry.IsDirectory) continue;
                        ExtractEntry(reader.Entry.Key, targetDir, commonPrefix,
                            dest => reader.WriteEntryTo(dest));
                    }
                });
            }
            catch
            {
                // Clean up partial extraction so the empty dir doesn't appear as a ghost version
                try { if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true); }
                catch { /* best effort */ }
                throw;
            }

            // Register as custom version
            if (!_config.CustomOptiScalerVersions.Contains(versionName, StringComparer.OrdinalIgnoreCase))
            {
                _config.CustomOptiScalerVersions.Add(versionName);
                SaveConfiguration();
            }
            // Update static cache so other windows see the new version immediately
            if (_cachedOptiScalerVersions != null && !_cachedOptiScalerVersions.Contains(versionName, StringComparer.OrdinalIgnoreCase))
                _cachedOptiScalerVersions.Add(versionName);
            return versionName;
        }

        private static void ExtractEntry(string? key, string targetDir, string commonPrefix, Action<Stream> writeAction)
        {
            var entryKey = (key ?? "").Replace('/', Path.DirectorySeparatorChar);
            if (!string.IsNullOrEmpty(commonPrefix) && entryKey.StartsWith(commonPrefix, StringComparison.OrdinalIgnoreCase))
                entryKey = entryKey.Substring(commonPrefix.Length);
            if (string.IsNullOrEmpty(entryKey)) return;

            // Guard against path traversal
            var destPath = Path.GetFullPath(Path.Combine(targetDir, entryKey));
            if (!destPath.StartsWith(Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return;

            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            using var fileStream = File.Create(destPath);
            writeAction(fileStream);
        }

        private static string SanitizeVersionName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (var c in name)
                sb.Append(invalid.Contains(c) ? '_' : c);
            return sb.ToString();
        }

        private static string FindCommonArchivePrefix(System.Collections.Generic.List<string?> keys)
        {
            if (keys.Count == 0) return "";
            var normalizedKeys = keys.Select(k => (k ?? "").Replace('/', Path.DirectorySeparatorChar)).ToList();
            var firstSep = normalizedKeys[0].IndexOf(Path.DirectorySeparatorChar);
            if (firstSep < 0) return "";
            var candidate = normalizedKeys[0].Substring(0, firstSep + 1);
            if (normalizedKeys.All(k => k.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
            return "";
        }

        public string GetVersionString()
        {
            var parts = new System.Collections.Generic.List<string>();

            if (!string.IsNullOrEmpty(OptiScalerVersion))
                parts.Add($"OptiScaler {OptiScalerVersion}");

            if (!string.IsNullOrEmpty(FakenvapiVersion))
                parts.Add($"Fakenvapi {FakenvapiVersion}");

            if (!string.IsNullOrEmpty(NukemFGVersion))
                parts.Add($"NukemFG {NukemFGVersion}");

            return parts.Count > 0 ? string.Join(" | ", parts) : "Not installed";
        }

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
            return versions.OrderByDescending(v => v).ToList();
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
            return versions.OrderByDescending(v => v).ToList();
        }

        public void DeleteOptiPatcherCache(string version)
        {
            var cachePath = Path.Combine(_cacheDir, "OptiPatcher", version);
            if (Directory.Exists(cachePath))
            {
                Directory.Delete(cachePath, true);
            }
        }

        public void DeleteExtrasCache(string version)
        {
            var cachePath = Path.Combine(_cacheDir, "Extras", version);
            if (Directory.Exists(cachePath))
            {
                Directory.Delete(cachePath, true);
            }
        }

        /// <summary>
        /// Returns a list of locally-cached NukemFG version names (subdirectory names under Cache/NukemFG/).
        /// Also migrates the legacy flat cache layout to the new versioned layout if needed.
        /// </summary>
        public List<string> GetDownloadedNukemFGVersions()
        {
            var versions = new List<string>();
            var nukemDir = GetNukemFGCachePath();
            if (!Directory.Exists(nukemDir)) return versions;

            // Legacy migration: if dlssg_to_fsr3_amd_is_better.dll exists directly in NukemFG/,
            // move it into a "default" subdirectory.
            var legacyDll = Path.Combine(nukemDir, "dlssg_to_fsr3_amd_is_better.dll");
            if (File.Exists(legacyDll))
            {
                var defaultDir = Path.Combine(nukemDir, "default");
                Directory.CreateDirectory(defaultDir);
                File.Move(legacyDll, Path.Combine(defaultDir, "dlssg_to_fsr3_amd_is_better.dll"), true);
                Log.Write("[NukemFG] Migrated legacy flat cache to versioned layout (default).");
            }

            foreach (var dir in Directory.GetDirectories(nukemDir))
            {
                var dll = Path.Combine(dir, "dlssg_to_fsr3_amd_is_better.dll");
                if (File.Exists(dll))
                {
                    versions.Add(Path.GetFileName(dir));
                }
            }
            static Version parseNukemVer(string v)
            {
                var clean = v.TrimStart('v');
                var dash = clean.IndexOf('-');
                if (dash >= 0) clean = clean[..dash];
                return Version.TryParse(clean, out var p) ? p : new Version(0, 0);
            }

            return versions
                .OrderByDescending(v => parseNukemVer(v))
                .ThenByDescending(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public void DeleteNukemFGCache(string version)
        {
            var cachePath = GetNukemFGCachePath(version);
            if (Directory.Exists(cachePath))
            {
                Directory.Delete(cachePath, true);
            }
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

        /// <summary>Loads the stored metadata for an imported custom FSR4 DLL version, or null.</summary>
        public CustomFsr4DllInfo? GetCustomFsr4DllInfo(string version)
            => ReadUserDllInfo(GetCustomFsr4CachePath(version), "CustomFsr4");

        /// <summary>
        /// Imports a user-supplied amdxcffx64.dll into the local component cache.
        /// Validates the file is a 64-bit PE, reads its version resource, computes
        /// the SHA-256, and stores everything under Cache/CustomFsr4/{version}/.
        /// Re-importing the same version overwrites the previous copy.
        /// Returns the metadata of the imported DLL.
        /// </summary>
        public Task<CustomFsr4DllInfo> ImportCustomFsr4DllAsync(string sourcePath)
            => ImportUserDllAsync(sourcePath, CustomFsr4DllName, GetCustomFsr4CachePath, "CustomFsr4");

        /// <summary>Deletes an imported custom FSR4 DLL version from the cache.</summary>
        public void DeleteCustomFsr4Cache(string version)
        {
            var cachePath = GetCustomFsr4CachePath(version);
            if (Directory.Exists(cachePath))
                Directory.Delete(cachePath, true);
        }

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

        /// <summary>Returns the full path of the cached SDK DLL for a specific version.</summary>
        public string GetCustomFsrSdkDllPath(string version) => Path.Combine(GetCustomFsrSdkCachePath(version), CustomFsrSdkDllName);

        /// <summary>
        /// Returns the locally-imported custom FSR SDK DLL version labels
        /// (subdirectory names under Cache/CustomFsrSdk/ that contain the DLL).
        /// </summary>
        public List<string> GetDownloadedCustomFsrSdkVersions()
            => GetUserDllVersions(GetCustomFsrSdkCachePath(), CustomFsrSdkDllName);

        /// <summary>Loads the stored metadata for an imported custom FSR SDK DLL version, or null.</summary>
        public CustomFsr4DllInfo? GetCustomFsrSdkDllInfo(string version)
            => ReadUserDllInfo(GetCustomFsrSdkCachePath(version), "CustomFsrSdk");

        /// <summary>Deletes an imported custom FSR SDK DLL version from the cache.</summary>
        public void DeleteCustomFsrSdkCache(string version)
        {
            var cachePath = GetCustomFsrSdkCachePath(version);
            if (Directory.Exists(cachePath))
                Directory.Delete(cachePath, true);
        }

        // ── Unified custom-DLL library (bring your own, one or more) ─────────────
        //
        // Flat per-file store: Cache/CustomDlls/<name>.dll + <name>.dll.json.
        // At install time these are overlaid on top of the latest AMD signedbin set:
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

        /// <summary>
        /// Imports a scanned FSR SDK package into Cache/CustomFsrSdk/{version}/.
        /// The upscaler DLL is required; all other found DLLs are imported alongside
        /// it. The version label comes from the upscaler's FileVersion. The caller
        /// owns the scan's staging cleanup.
        /// </summary>
        public async Task<CustomFsr4DllInfo> ImportCustomFsrSdkPackageAsync(FsrSdkScanResult scan)
        {
            if (!scan.HasUpscaler)
                throw new InvalidDataException(
                    $"The selected source does not contain {CustomFsrSdkDllName} (64-bit). The upscaler DLL is required.");

            return await Task.Run(() =>
            {
                var upscalerPath = scan.FoundFiles[CustomFsrSdkDllName];
                var upscalerPe = scan.UpscalerPe ?? PeFileInspector.Inspect(upscalerPath);

                string upscalerSha;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var stream = File.OpenRead(upscalerPath))
                    upscalerSha = Convert.ToHexString(sha.ComputeHash(stream));

                var versionLabel = !string.IsNullOrEmpty(upscalerPe.FileVersion) && upscalerPe.FileVersion != "0.0.0.0"
                    ? upscalerPe.FileVersion
                    : $"unknown-{upscalerSha[..8].ToLowerInvariant()}";
                versionLabel = SanitizeVersionName(versionLabel);

                var targetDir = GetCustomFsrSdkCachePath(versionLabel);
                if (Directory.Exists(targetDir))
                    Directory.Delete(targetDir, true); // re-import replaces the whole package
                Directory.CreateDirectory(targetDir);

                var info = new CustomFsr4DllInfo
                {
                    VersionLabel = versionLabel,
                    FileVersion = upscalerPe.FileVersion,
                    ProductVersion = upscalerPe.ProductVersion,
                    Sha256 = upscalerSha,
                    HasAuthenticodeSignature = upscalerPe.HasAuthenticodeSignature,
                    OriginalFileName = Path.GetFileName(scan.SourcePath),
                    ImportedAtUtc = DateTime.UtcNow.ToString("O")
                };

                foreach (var (dllName, sourceFile) in scan.FoundFiles)
                {
                    File.Copy(sourceFile, Path.Combine(targetDir, dllName), overwrite: true);

                    var pe = dllName.Equals(CustomFsrSdkDllName, StringComparison.OrdinalIgnoreCase)
                        ? upscalerPe
                        : PeFileInspector.Inspect(sourceFile);
                    string fileSha;
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                    using (var stream = File.OpenRead(sourceFile))
                        fileSha = Convert.ToHexString(sha.ComputeHash(stream));

                    info.Files.Add(new CustomDllFileEntry
                    {
                        Name = dllName,
                        FileVersion = pe.FileVersion,
                        Sha256 = fileSha,
                        HasAuthenticodeSignature = pe.HasAuthenticodeSignature
                    });
                }

                var json = JsonSerializer.Serialize(info, OptimizerContext.Default.CustomFsr4DllInfo);
                File.WriteAllText(Path.Combine(targetDir, "dll_info.json"), json);

                Log.Write($"[CustomFsrSdk] Imported package '{versionLabel}' with {info.Files.Count} DLL(s): " +
                                string.Join(", ", info.Files.Select(f => f.Name)));
                return info;
            });
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

            static Version parseVer(string v)
            {
                var clean = new string(v.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).TrimEnd('.');
                return Version.TryParse(clean, out var p) ? p : new Version(0, 0);
            }

            return versions
                .OrderByDescending(v => parseVer(v))
                .ThenByDescending(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static CustomFsr4DllInfo? ReadUserDllInfo(string versionDir, string logTag)
        {
            var infoPath = Path.Combine(versionDir, "dll_info.json");
            if (!File.Exists(infoPath)) return null;
            try
            {
                var json = File.ReadAllText(infoPath);
                return JsonSerializer.Deserialize(json, OptimizerContext.Default.CustomFsr4DllInfo);
            }
            catch (Exception ex)
            {
                Log.Write($"[{logTag}] Failed to read dll_info.json: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Shared import core for user-supplied DLL components: validates the file is
        /// a 64-bit PE, reads its version resource, computes the SHA-256, and stores
        /// DLL + metadata under {cache}/{versionLabel}/. Never downloads anything.
        /// </summary>
        private static async Task<CustomFsr4DllInfo> ImportUserDllAsync(
            string sourcePath, string dllName, Func<string, string> versionDirFor, string logTag)
        {
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("Selected file does not exist.", sourcePath);

            return await Task.Run(() =>
            {
                PeFileInfo pe;
                try
                {
                    pe = PeFileInspector.Inspect(sourcePath);
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException($"Could not read the selected file: {ex.Message}", ex);
                }

                if (!pe.IsValidPe)
                    throw new InvalidDataException("The selected file is not a valid Windows DLL (missing PE header).");
                if (!pe.Is64Bit)
                    throw new InvalidDataException($"The selected DLL is not a 64-bit (x64) binary. OptiScaler requires the 64-bit {dllName}.");

                string sha256;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var stream = File.OpenRead(sourcePath))
                    sha256 = Convert.ToHexString(sha.ComputeHash(stream));

                // Version label = detected FileVersion, falling back to a hash prefix
                // so two unknown builds never collide in the cache.
                var versionLabel = !string.IsNullOrEmpty(pe.FileVersion) && pe.FileVersion != "0.0.0.0"
                    ? pe.FileVersion
                    : $"unknown-{sha256[..8].ToLowerInvariant()}";
                versionLabel = SanitizeVersionName(versionLabel);

                var targetDir = versionDirFor(versionLabel);
                Directory.CreateDirectory(targetDir);
                File.Copy(sourcePath, Path.Combine(targetDir, dllName), overwrite: true);

                var info = new CustomFsr4DllInfo
                {
                    VersionLabel = versionLabel,
                    FileVersion = pe.FileVersion,
                    ProductVersion = pe.ProductVersion,
                    Sha256 = sha256,
                    HasAuthenticodeSignature = pe.HasAuthenticodeSignature,
                    OriginalFileName = Path.GetFileName(sourcePath),
                    ImportedAtUtc = DateTime.UtcNow.ToString("O")
                };

                var json = JsonSerializer.Serialize(info, OptimizerContext.Default.CustomFsr4DllInfo);
                File.WriteAllText(Path.Combine(targetDir, "dll_info.json"), json);

                Log.Write($"[{logTag}] Imported '{info.OriginalFileName}' as version '{versionLabel}' " +
                                $"(FileVersion={pe.FileVersion ?? "?"}, signed={pe.HasAuthenticodeSignature}, sha256={sha256[..16]}…)");
                return info;
            });
        }

        /// <summary>
        /// Imports a NukemFG version from a .zip archive.
        /// Extracts the archive, locates dlssg_to_fsr3_amd_is_better.dll, and caches it
        /// under Cache/NukemFG/{archiveName}/.
        /// </summary>
        public async Task<string> ImportNukemFGArchiveAsync(string archivePath)
        {
            var archiveName = Path.GetFileNameWithoutExtension(archivePath);
            // Sanitize folder name
            foreach (var c in Path.GetInvalidFileNameChars())
                archiveName = archiveName.Replace(c, '_');

            var versionDir = GetNukemFGCachePath(archiveName);
            Directory.CreateDirectory(versionDir);

            var tempExtractDir = Path.Combine(Path.GetTempPath(), "OptiScaler_NukemFG_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempExtractDir);

            try
            {
                await Task.Run(() =>
                {
                    using var archive = SharpCompress.Archives.ArchiveFactory.Open(archivePath);
                    foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                    {
                        entry.WriteToDirectory(tempExtractDir, new SharpCompress.Common.ExtractionOptions
                        {
                            ExtractFullPath = true,
                            Overwrite = true
                        });
                    }
                });

                // Find the target DLL anywhere in the extracted tree
                var dllFiles = Directory.GetFiles(tempExtractDir, "dlssg_to_fsr3_amd_is_better.dll", SearchOption.AllDirectories);
                if (dllFiles.Length == 0)
                {
                    // Cleanup on failure
                    if (Directory.Exists(versionDir)) Directory.Delete(versionDir, true);
                    throw new FileNotFoundException("The archive does not contain 'dlssg_to_fsr3_amd_is_better.dll'.");
                }

                File.Copy(dllFiles[0], Path.Combine(versionDir, "dlssg_to_fsr3_amd_is_better.dll"), true);
                Log.Write($"[NukemFG] Imported version '{archiveName}' from archive.");
                return archiveName;
            }
            finally
            {
                // Cleanup temp extraction directory
                if (Directory.Exists(tempExtractDir))
                {
                    try { Directory.Delete(tempExtractDir, true); } catch { /* best-effort */ }
                }
            }
        }
    }
}
