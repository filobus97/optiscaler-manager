// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UpscalerManager.Core.Components;
using UpscalerManager.Core.Logging;
using UpscalerManager.Core.Models;
using UpscalerManager.Core.Prompts;
using UpscalerManager.Core.Services;

namespace UpscalerManager.App.Services;

/// <summary>
/// Thin application facade over the ported Core service layer. The UI talks only
/// to this class, which keeps the "Install OptiScaler" flow, the transparent
/// preview, the bring-your-own imports, and the OptiScaler.ini profile library in
/// one place. All heavy lifting is done by the reused Core services.
/// </summary>
public sealed class ManagerService
{
    private readonly ComponentManagementService _components;
    private readonly GameInstallationService _install = new();
    private readonly GameScannerService _scanner = new();
    private readonly ProfileManagementService _profiles = new();
    private readonly BackupStoreService _backupStore = new();
    private readonly IGpuDetectionService? _gpu = PlatformServiceFactory.CreateGpuDetectionService();

    public ManagerService(IManualComponentProvider manualProvider)
    {
        _components = new ComponentManagementService(manualProvider);
        _vendor = new VendorDllService(_library);
        _repository = new DllRepositoryService(_library);
    }

    // ── GPU banner ──────────────────────────────────────────────────────────
    public GpuInfo? DetectPrimaryGpu()
    {
        try { return _gpu?.GetDiscreteGPU() ?? _gpu?.GetPrimaryGPU(); }
        catch { return null; }
    }

    // ── Game scan ───────────────────────────────────────────────────────────
    public async Task<List<Game>> ScanGamesAsync()
    {
        var games = await _scanner.ScanAllGamesAsync();
        // Kept because the swap library harvests from the user's whole collection, not
        // just the game being looked at: the point of that route is that the versions on
        // offer are ones they already own.
        _scanned = games;
        return games;
    }

    private List<Game> _scanned = new();

    /// <summary>The games from the last scan.</summary>
    public IReadOnlyList<Game> ScannedGames => _scanned;

    // ── DLL swapping ────────────────────────────────────────────────────────
    private readonly DllSwapService _swaps = new();
    private readonly DllLibraryService _library = new();
    private readonly VendorDllService _vendor;
    private readonly DllRepositoryService _repository;

    /// <summary>
    /// One row per swappable DLL present in this game. Stale records are dropped first,
    /// so a game that was verified or reinstalled since the last swap does not claim one
    /// that is no longer there.
    /// </summary>
    public IReadOnlyList<SwapSlot> SwapSlots(Game game)
    {
        _swaps.ForgetStaleSwaps(game);
        return _swaps.Slots(game);
    }

    /// <summary>Builds of one DLL the library already holds, newest first.</summary>
    public IReadOnlyList<LibraryDll> LibraryBuilds(string fileName) => _library.For(fileName);

    /// <summary>
    /// Builds of one DLL sitting in the user's other games, not yet held. Excludes the
    /// game being looked at: swapping a game's own file for itself achieves nothing.
    /// </summary>
    public IReadOnlyList<HarvestableDll> HarvestableBuilds(string fileName, Game exclude) =>
        _library.Harvestable(
            _scanned.Where(g => !string.Equals(g.InstallPath, exclude.InstallPath, StringComparison.OrdinalIgnoreCase)),
            fileName);

    /// <summary>
    /// Builds of one DLL inside the OptiScaler releases already downloaded. No network,
    /// and the only source for AMD's FidelityFX runtimes.
    /// </summary>
    public IReadOnlyList<HarvestableDll> OptiScalerBuilds(string fileName) =>
        _library.FromOptiScalerReleases(fileName);

    /// <summary>Copies a build out of a game, or out of an OptiScaler release, into the library.</summary>
    public LibraryDll HarvestBuild(HarvestableDll source) => _library.Harvest(source);

    /// <summary>
    /// Builds already unpacked from OptiScaler-Extras releases. A no-network source,
    /// and the only route the swap path has to FSR 4.
    /// </summary>
    public IReadOnlyList<HarvestableDll> CommunityBuilds(string fileName) =>
        _library.FromCommunityBuilds(fileName);

    /// <summary>
    /// True when the community releases are worth offering for this DLL.
    ///
    /// The whole FidelityFX family, not just the two names an INT8 upscaler ships as.
    /// These releases are drops of a matched set — a patched upscaler alongside the
    /// runtime it was built against — so gating on the upscaler's filename alone hid
    /// the source on exactly the pages where nothing else could supply the file.
    /// Whether a given release actually carries the file being asked for is settled by
    /// looking inside it, not guessed from its name.
    /// </summary>
    public static bool TakesCommunityBuilds(string fileName) =>
        fileName.StartsWith("amd_fidelityfx", StringComparison.OrdinalIgnoreCase)
        || Fsr4Int8Build.IsKnown(fileName);

    /// <summary>
    /// Community build versions available to download, newest first, paired with
    /// whether their author marked the release a pre-release. Reuses the same listing
    /// the OptiScaler install screen uses rather than asking a second time.
    /// </summary>
    public Task<IReadOnlyList<(string Version, bool IsPreRelease)>> CommunityBuildReleasesAsync() =>
        GetInt8ReleasesAsync();

    /// <summary>
    /// Downloads one community release and files the requested DLL from it in the swap
    /// library.
    ///
    /// Goes through the existing Extras download, so the release lands in the same
    /// cache the OptiScaler route uses and a version fetched for one route is
    /// immediately available to the other.
    /// </summary>
    public async Task<LibraryDll> DownloadCommunityBuildAsync(
        string version, string fileName, IProgress<double>? progress = null)
    {
        var extracted = await _components.DownloadExtrasReleaseAsync(version, progress);

        var built = _library.FromCommunityBuilds(fileName)
            .FirstOrDefault(b => b.Path.Contains(version, StringComparison.OrdinalIgnoreCase));

        if (built is null)
        {
            // Say what the release did carry. "It isn't in there" on its own leaves a
            // user re-pressing rows to find out which one is.
            var alternatives = SafeFileNames(extracted)
                .Where(SwappableDlls.IsSwappable)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            throw new InvalidOperationException(
                $"Community build {version} downloaded, but it carries no {fileName}. " +
                (alternatives.Count > 0
                    ? $"It holds {string.Join(", ", alternatives)} — those rows can use it."
                    : "It holds none of the swappable DLLs."));
        }

        return _library.Harvest(built);
    }

    private static IEnumerable<string> SafeFileNames(string directory)
    {
        try { return Directory.EnumerateFiles(directory).Select(Path.GetFileName).OfType<string>().ToList(); }
        catch { return Array.Empty<string>(); }
    }

    // ── The DLSS Swapper archive ────────────────────────────────────────────

    /// <summary>
    /// True when the DLSS Swapper archive carries builds of this DLL, so the picker
    /// knows whether to show that section at all.
    /// </summary>
    public static bool TakesRepositoryBuilds(string fileName) => DllRepository.Covers(fileName);

    /// <summary>
    /// What the archive holds for this DLL, newest first. Reads a cached index, so
    /// opening a picker costs nothing after the first time in a day.
    /// </summary>
    public Task<IReadOnlyList<RepositoryBuild>> RepositoryBuildsAsync(
        string fileName, CancellationToken cancel = default) =>
        SwapRepositoryDownloadsEnabled
            ? _repository.AvailableAsync(fileName, cancel)
            : Task.FromResult<IReadOnlyList<RepositoryBuild>>(Array.Empty<RepositoryBuild>());

    /// <summary>Downloads one archived build into the library, hashes checked.</summary>
    public Task<LibraryDll> DownloadRepositoryBuildAsync(
        RepositoryBuild build, IProgress<double>? progress = null, CancellationToken cancel = default) =>
        _repository.DownloadAsync(build, progress, cancel);

    /// <summary>What the vendor publishes for this DLL, newest first. Never automatic.</summary>
    public Task<IReadOnlyList<VendorBuild>> VendorBuildsAsync(string fileName, CancellationToken cancel = default) =>
        SwapVendorDownloadsEnabled
            ? _vendor.AvailableAsync(fileName, cancel)
            : Task.FromResult<IReadOnlyList<VendorBuild>>(Array.Empty<VendorBuild>());

    /// <summary>Downloads one vendor build into the library.</summary>
    public Task<LibraryDll> DownloadVendorBuildAsync(
        VendorBuild build, IProgress<double>? progress = null, CancellationToken cancel = default) =>
        _vendor.DownloadAsync(build, progress, cancel);

    /// <summary>Takes a file the user chose into the library.</summary>
    public LibraryDll ImportSwappableDll(string path) => _library.Import(path);

    /// <summary>Replaces the game's DLL with a held build, backing the original up.</summary>
    public void SwapDll(Game game, SwapSlot slot, LibraryDll build)
    {
        _swaps.Swap(game, slot, build);
        RefreshGameAnalysis(game);
    }

    /// <summary>Puts the game's own DLL back.</summary>
    public void RevertSwap(Game game, SwappedFile swapped, bool force = false)
    {
        _swaps.Revert(game, swapped, force);
        RefreshGameAnalysis(game);
    }

    /// <summary>Swapped files an OptiScaler install would overwrite, for the warning.</summary>
    public IReadOnlyList<string> SwapsOptiScalerWouldOverwrite(Game game) =>
        _swaps.SwapsOptiScalerWouldOverwrite(game);

    // ── Bring-your-own inventory ────────────────────────────────────────────
    /// <summary>The user's custom-DLL library (migrated from any legacy imports on first use).</summary>
    public List<CustomDllFileEntry> GetCustomDlls() => _components.GetCustomDlls();

    public bool HasCustomDlls => _components.GetCustomDlls().Count > 0;

    /// <summary>Imports one or more custom DLLs (files, a folder, or an archive).</summary>
    public Task<List<string>> ImportCustomDllsAsync(IEnumerable<string> sources) => _components.ImportCustomDllsAsync(sources);

    public void DeleteCustomDll(string name) => _components.DeleteCustomDll(name);

    // ── Storage ─────────────────────────────────────────────────────────────
    private StorageInventoryService? _storage;
    private StorageInventoryService Storage => _storage ??= new StorageInventoryService(_components.Config);

    /// <summary>Everything the app is keeping on disk, grouped by how safe it is to remove.</summary>
    public IReadOnlyList<StorageItem> ScanStorage() => Storage.Scan();

    /// <summary>Removes one stored item. Backups a game still relies on are refused.</summary>
    public bool DeleteStorageItem(StorageItem item) => Storage.Delete(item);

    /// <summary>Latest FSR 4 INT8 community version known from source, if a check has run.</summary>
    public string? LatestExtrasVersion => _components.LatestExtrasVersion;

    /// <summary>Cached INT8 community build versions (newest first); may be empty until fetched.</summary>
    public IReadOnlyList<string> Int8CommunityVersions => _components.ExtrasAvailableVersions;

    /// <summary>
    /// Ensures the INT8 community version list is populated (best-effort network fetch),
    /// then returns it, falling back to any already-downloaded versions when offline.
    /// </summary>
    /// <summary>
    /// INT8 community builds with their upstream pre-release flag, so the picker can
    /// say which ones their author has not marked stable.
    /// </summary>
    public async Task<IReadOnlyList<(string Version, bool IsPreRelease)>> GetInt8ReleasesAsync()
    {
        var versions = await GetInt8VersionsAsync();
        var flags = _components.ExtrasAvailableReleases
            .ToDictionary(r => r.Version, r => r.IsPreRelease, StringComparer.OrdinalIgnoreCase);
        return versions
            .Select(v => (Version: v, IsPreRelease: flags.TryGetValue(v, out var pre) && pre))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> GetInt8VersionsAsync()
    {
        if (_components.ExtrasAvailableVersions.Count == 0)
        {
            try { await _components.CheckForUpdatesAsync(); } catch { /* offline / rate-limited */ }
        }
        var list = _components.ExtrasAvailableVersions;
        if (list.Count > 0) return list;
        return _components.ExtrasDownloadedVersions; // fallback to what's on disk
    }

    /// <summary>Whether a given backend can currently be installed (imports present / etc.).</summary>
    public bool IsBackendAvailable(Fsr4Backend backend) => backend switch
    {
        Fsr4Backend.CustomMerged => HasCustomDlls,
        _ => true, // Default and Int8Community need no prior import
    };

    // ── Menu / overlay shortcut key (global, persisted) ─────────────────────
    /// <summary>The configured overlay key (VK hex string, e.g. "0x78"), or null for OptiScaler's default.</summary>
    public string? MenuShortcutKey
    {
        get => _components.Config.MenuShortcutKey;
        set { _components.Config.MenuShortcutKey = value; _components.SaveConfiguration(); }
    }

    // ── Controller navigation (global, persisted) ───────────────────────────
    /// <summary>Whether a game controller may drive the UI. Applied at startup.</summary>
    public bool GamepadNavigationEnabled
    {
        get => _components.Config.GamepadNavigation;
        set { _components.Config.GamepadNavigation = value; _components.SaveConfiguration(); }
    }

    /// <summary>
    /// Whether the vendors' own releases are offered as a swap download source.
    /// </summary>
    public bool SwapVendorDownloadsEnabled
    {
        get => _components.Config.SwapVendorDownloads;
        set { _components.Config.SwapVendorDownloads = value; _components.SaveConfiguration(); }
    }

    /// <summary>
    /// Whether the DLSS Swapper archive is offered as a swap download source. See
    /// <see cref="AppConfiguration.SwapRepositoryDownloads"/> for why it defaults on
    /// and why it has a switch at all.
    /// </summary>
    public bool SwapRepositoryDownloadsEnabled
    {
        get => _components.Config.SwapRepositoryDownloads;
        set { _components.Config.SwapRepositoryDownloads = value; _components.SaveConfiguration(); }
    }

    /// <summary>
    /// Whether to hide games with no upscaler at all. Those cannot be helped by either
    /// route — OptiScaler hooks an upscaler a game already has, and there is nothing to
    /// swap — so some libraries are mostly noise without this.
    ///
    /// Hides rather than skips: the games are still scanned, so turning this off shows
    /// them again without another scan, and a mis-detected game is never invisible for
    /// good.
    /// </summary>
    public bool HideGamesWithoutUpscaler
    {
        get => _components.Config.ScanSources.UpscalerFilter == UpscalerFilterMode.HideWithoutUpscaler;
        set
        {
            _components.Config.ScanSources.UpscalerFilter =
                value ? UpscalerFilterMode.HideWithoutUpscaler : UpscalerFilterMode.ShowAll;
            _components.SaveConfiguration();
        }
    }

    // ── Add-ons: fakenvapi + Nukem DLSSG-to-FSR3 ────────────────────────────
    /// <summary>True when Nukem's DLL has been imported into the cache (it cannot be auto-downloaded).</summary>
    public bool IsNukemFgCached => _components.IsNukemFGInstalled;

    /// <summary>The cached Nukem DLL's version tag, if known.</summary>
    public string? NukemFgVersion => _components.NukemFGVersion;

    /// <summary>The latest Nukem FG version known from its repo (null until a check runs).</summary>
    public string? LatestNukemFgVersion => _components.LatestNukemFGVersion;

    /// <summary>True when the imported Nukem DLL is behind the latest published release.</summary>
    public bool IsNukemFgUpdateAvailable => _components.IsNukemFGUpdateAvailable;

    /// <summary>
    /// Best-effort refresh of add-on version info (so the Settings add-on card can
    /// flag a newer Nukem release). Never throws.
    /// </summary>
    public async Task RefreshAddonStatusAsync()
    {
        try { await _components.CheckForUpdatesAsync(); } catch { /* offline / rate-limited */ }
    }

    /// <summary>
    /// Opens the manual-import flow (file picker / archive) for Nukem's
    /// dlssg_to_fsr3_amd_is_better.dll and stores it in the cache.
    /// </summary>
    public Task<bool> ImportNukemFgAsync() => _components.ProvideNukemFGManuallyAsync(isUpdate: _components.IsNukemFGInstalled);

    // ── App self-update check ───────────────────────────────────────────────
    private AppUpdateService? _appUpdate;
    private AppUpdateService AppUpdate => _appUpdate ??= new AppUpdateService(_components.Config.App);

    /// <summary>The running app version (from the assembly).</summary>
    public string AppVersion => AppUpdateService.GetCurrentVersion();

    /// <summary>URL of this app's GitHub releases page.</summary>
    public string ReleasesPageUrl => AppUpdate.ReleasesPageUrl;

    /// <summary>Best-effort check of this app's own repo for a newer release; never throws.</summary>
    public Task<AppUpdateCheck> CheckForAppUpdateAsync() => AppUpdate.CheckAsync();

    /// <summary>True when the bundled updater script sits next to the executable.</summary>
    public bool CanSelfUpdate => AppUpdateService.CanSelfUpdate;

    /// <summary>
    /// True on platforms that update seamlessly in-process (Unix): the app replaces
    /// its own process image at the same PID (survives Steam Gaming Mode). Windows
    /// uses the detached-script flow instead.
    /// </summary>
    public bool UsesInProcessUpdate => AppUpdateService.UsesInProcessUpdate;

    /// <summary>
    /// Windows flow: launches the detached updater (which waits for this process,
    /// updates in place, and relaunches it). Returns null on success — the caller
    /// should then shut down — or a human-readable error to show while staying open.
    /// </summary>
    public string? StartSelfUpdate() => AppUpdateService.StartSelfUpdate();

    /// <summary>
    /// Unix flow: downloads the latest release, swaps files in place, and re-execs the
    /// app at the same PID. Returns an error on failure (app stays running); on success
    /// it does not return (the process is replaced).
    /// </summary>
    public Task<string?> RunInProcessUpdateAsync(Action<string>? report = null) => AppUpdate.RunInProcessUpdateAsync(report);

    // ── OptiScaler.ini profile library ──────────────────────────────────────
    /// <summary>All saved OptiScaler.ini profiles (built-in default + user-imported).</summary>
    public List<OptiScalerProfile> GetIniProfiles() => _profiles.GetAllProfiles(forceRefresh: true);

    /// <summary>The built-in "use OptiScaler's own default config" profile.</summary>
    public OptiScalerProfile DefaultIniProfile => _profiles.GetDefaultProfile();

    /// <summary>Imports an OptiScaler.ini file from disk and tags it with a name.</summary>
    public Task ImportIniProfileAsync(string iniPath, string name, string description = "") => Task.Run(() =>
    {
        var profile = _profiles.CreateProfileFromIni(iniPath, name, description);
        _profiles.SaveProfile(profile);
    });

    public void DeleteIniProfile(OptiScalerProfile profile) => _profiles.DeleteProfile(profile);

    /// <summary>Flattens a profile's section/key map into the preview's ini-key list.</summary>
    public static IReadOnlyList<IniKeyChange> ProfileToIniKeys(OptiScalerProfile? profile)
    {
        if (profile is null || profile.IniSettings.Count == 0)
            return Array.Empty<IniKeyChange>();
        return profile.IniSettings
            .SelectMany(section => section.Value.Select(kv => new IniKeyChange(section.Key, kv.Key, kv.Value)))
            .ToList();
    }

    // ── The transparent "what will happen" preview ──────────────────────────
    /// <summary>
    /// Builds the exact preview for installing OptiScaler on <paramref name="game"/>
    /// with the chosen FSR 4 backend and ini profile. What the preview shows is
    /// precisely what gets installed.
    /// </summary>
    public InstallPreview BuildInstallPreview(Game game, Fsr4Backend backend, bool selectFsr4,
        bool addFakenvapi = false, bool addNukemFg = false,
        SpoofMethod? spoofMethod = null, bool forceInt8 = false, bool fsr4Watermark = false)
        => ComponentRegistry.BuildInstallPreview(backend, selectFsr4, ComponentRegistry.DefaultInjectionDll, MenuShortcutKey,
            backend == Fsr4Backend.CustomMerged ? _components.GetCustomDlls().Select(d => d.Name).ToList() : null,
            addFakenvapi, addNukemFg, spoofMethod, forceInt8, fsr4Watermark);

    // ── OptiScaler version list ─────────────────────────────────────────────
    /// <summary>
    /// OptiScaler release versions selectable at install (newest first): the remote
    /// list when a check has run, merged with anything already downloaded; falls back
    /// to downloaded-only when offline.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetOptiScalerVersionsAsync()
    {
        if (_components.OptiScalerAvailableVersions.Count == 0)
        {
            try { await _components.CheckForUpdatesAsync(); } catch { /* offline / rate-limited */ }
        }
        var list = _components.OptiScalerAvailableVersions;
        if (list.Count > 0) return list;
        return _components.GetDownloadedOptiScalerVersions();
    }

    /// <summary>Version strings that are pre-releases/betas (for grouping in the UI).</summary>
    public bool IsBetaOptiScalerVersion(string version) => _components.BetaVersions.Contains(version);

    // ── Install OptiScaler ──────────────────────────────────────────────────
    public async Task InstallAsync(Game game, Fsr4Backend backend, string? int8Version, bool selectFsr4,
        OptiScalerProfile? iniProfile, IProgress<string>? status = null,
        bool addFakenvapi = false, bool addNukemFg = false,
        SpoofMethod? spoofMethod = null, bool forceInt8 = false, bool fsr4Watermark = false,
        string? optiscalerVersion = null)
    {
        if (!IsBackendAvailable(backend))
            throw new InvalidOperationException($"The selected FSR 4 backend ({backend}) is not available. Import it in Settings first.");
        if (addNukemFg && !IsNukemFgCached)
            throw new InvalidOperationException("Nukem's DLSSG-to-FSR3 DLL has not been imported. Add it in Settings first.");

        status?.Report("Checking for the latest OptiScaler release…");
        try { await _components.CheckForUpdatesAsync(); } catch { /* offline: fall back to cache */ }

        // A specific version wins; otherwise latest stable, then anything cached.
        var version = optiscalerVersion
                      ?? _components.LatestStableVersion
                      ?? _components.GetDownloadedOptiScalerVersions().FirstOrDefault()
                      ?? throw new InvalidOperationException(
                          "No OptiScaler release is available and none is cached. Connect to the internet and try again.");

        var cachePath = _components.GetOptiScalerCachePath(version);
        // Re-download when the cache is missing OR incomplete — an interrupted prior
        // download can leave a partial extraction (files present but no OptiScaler.dll),
        // which we self-heal instead of failing the install and asking the user to
        // manually clear the cache.
        if (!_components.IsOptiScalerCacheComplete(version))
        {
            status?.Report($"Downloading OptiScaler {version}…");
            await _components.DownloadOptiScalerAsync(version);
        }

        // Resolve add-on caches up front so the single InstallOptiScaler call places
        // everything under one manifest (Revert then removes add-ons too).
        var fakenvapiCache = addFakenvapi ? await EnsureFakenvapiCacheAsync(status) : "";
        var nukemCache = addNukemFg ? _components.GetNukemFGCachePath() : "";

        status?.Report($"Installing OptiScaler {version}…");
        // Pass the chosen ini profile (null → OptiScaler's own default config).
        var profileToApply = (iniProfile is null || iniProfile.IsBuiltIn || iniProfile.IniSettings.Count == 0) ? null : iniProfile;
        _install.InstallOptiScaler(game, cachePath, ComponentRegistry.DefaultInjectionDll,
            installFakenvapi: addFakenvapi, fakenvapiCachePath: fakenvapiCache,
            installNukemFG: addNukemFg, nukemFGCachePath: nukemCache,
            optiscalerVersion: version, profile: profileToApply);

        var gameDir = _install.DetermineInstallDirectory(game);
        if (string.IsNullOrEmpty(gameDir))
            throw new InvalidOperationException("OptiScaler installed but the game directory could not be resolved.");

        switch (backend)
        {
            case Fsr4Backend.CustomMerged:
            {
                InstallCustomOverlay(game, status);
                break;
            }
            case Fsr4Backend.Int8Community:
            {
                await InstallInt8CommunityAsync(game, gameDir, int8Version, status);
                break;
            }
            case Fsr4Backend.Default:
            default:
                break; // OptiScaler-provided files only.
        }

        // Nvidia override via OptiPatcher: the ASI plugin patches vendor checks in
        // memory instead of spoofing the adapter. plugins/OptiPatcher.asi is a known
        // OptiScaler artifact, so Revert removes it with the rest.
        if (spoofMethod == SpoofMethod.OptiPatcher)
            await InstallOptiPatcherAsync(gameDir, status);

        // Force ONLY the keys the Manager owns; everything else in the chosen ini
        // (default or custom) is left untouched. FSR 4 is always made *available*;
        // whether it is *selected* depends on selectFsr4.
        ApplyForcedIniKeys(gameDir, selectFsr4, spoofMethod, forceInt8, fsr4Watermark);

        status?.Report("Done.");
    }

    /// <summary>
    /// Downloads the latest OptiPatcher.asi (cached fallback offline) and places it in
    /// the game's plugins folder. LoadAsiPlugins=true is forced by ApplyForcedIniKeys.
    /// </summary>
    private async Task InstallOptiPatcherAsync(string gameDir, IProgress<string>? status)
    {
        string? asiPath = null;
        var version = _components.LatestOptiPatcherVersion;
        if (version is not null)
        {
            try
            {
                status?.Report($"Downloading OptiPatcher {version}…");
                asiPath = await _components.DownloadOptiPatcherAsync(version);
            }
            catch (Exception ex)
            {
                Log.Write($"[OptiPatcher] Download of {version} failed, checking cache: {ex.Message}");
            }
        }
        if (asiPath is null)
        {
            var cached = _components.GetDownloadedOptiPatcherVersions().FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "OptiPatcher could not be downloaded and no version is cached. Connect to the internet and try again.");
            asiPath = Path.Combine(_components.GetOptiPatcherCachePath(cached), "OptiPatcher.asi");
            status?.Report($"Using cached OptiPatcher {cached} (offline).");
        }

        var pluginsDir = Path.Combine(gameDir, "plugins");
        Directory.CreateDirectory(pluginsDir);
        File.Copy(asiPath, Path.Combine(pluginsDir, "OptiPatcher.asi"), overwrite: true);
        status?.Report("Installed OptiPatcher plugin.");
    }

    /// <summary>
    /// Downloads the latest fakenvapi release into the versioned cache (reusing an
    /// existing download), falling back to the newest cached version when offline.
    /// Returns the cache directory to install from.
    /// </summary>
    private async Task<string> EnsureFakenvapiCacheAsync(IProgress<string>? status)
    {
        var version = _components.LatestFakenvapiVersion;
        if (version is not null)
        {
            try
            {
                if (!_components.IsFakenvapiCached(version))
                {
                    status?.Report($"Downloading fakenvapi {version}…");
                    await _components.DownloadFakenvapiAsync(version);
                }
                return _components.GetFakenvapiCachePath(version);
            }
            catch (Exception ex)
            {
                Log.Write($"[Fakenvapi] Download of {version} failed, checking cache: {ex.Message}");
            }
        }

        var cached = _components.GetDownloadedFakenvapiVersions().FirstOrDefault()
            ?? throw new InvalidOperationException(
                "fakenvapi could not be downloaded and no version is cached. Connect to the internet and try again.");
        status?.Report($"Using cached fakenvapi {cached} (offline).");
        return _components.GetFakenvapiCachePath(cached);
    }

    /// <summary>
    /// Writes the (and only the) OptiScaler.ini keys the Manager is responsible for:
    /// [FSR] Fsr4Update=true always, [Upscalers] Dx12/Dx11/VulkanUpscaler (the FSR
    /// upscaler when the Manager selects FSR 4, auto when the user selects it in-game),
    /// the opt-in [FSR] Fsr4ForceEnableInt8 /
    /// Fsr4EnableWatermark and [Spoofing] Dxgi toggles, and [Menu] ShortcutKey when a
    /// menu key is configured. Applied last, so it overrides anything the backend
    /// installers set. Off toggles leave the keys untouched (OptiScaler's auto behaviour).
    /// </summary>
    private void ApplyForcedIniKeys(string gameDir, bool selectFsr4, SpoofMethod? spoofMethod = null,
        bool forceInt8 = false, bool fsr4Watermark = false)
    {
        GameInstallationService.ModifyOptiScalerIniKey(gameDir, "FSR", "Fsr4Update", "true");

        // Which upscaler runs at all. This is the setting that makes "select FSR 4"
        // mean anything; the DX12 default is XeSS, and FSR keys are not read while it
        // is running. Written after Fsr4Update so it wins over the backend installers.
        GameInstallationService.SelectFsr4Upscaler(gameDir, selectFsr4);

        // UpscalerIndex picks *which* FSR version the FSR upscaler uses, by position in
        // a list the FidelityFX runtime reports. Its default is already 0, so the 0 that
        // older Manager versions wrote here was a no-op; write auto to clear it.
        GameInstallationService.ModifyOptiScalerIniKey(gameDir, "FSR", "UpscalerIndex", "auto");

        if (forceInt8)
            GameInstallationService.ModifyOptiScalerIniKey(gameDir, "FSR", "Fsr4ForceEnableInt8", "true");
        if (fsr4Watermark)
            GameInstallationService.ModifyOptiScalerIniKey(gameDir, "FSR", "Fsr4EnableWatermark", "true");
        if (spoofMethod == SpoofMethod.Dxgi)
            GameInstallationService.ModifyOptiScalerIniKey(gameDir, "Spoofing", "Dxgi", "true");
        if (spoofMethod == SpoofMethod.OptiPatcher)
            GameInstallationService.ModifyOptiScalerIniKey(gameDir, "Plugins", "LoadAsiPlugins", "true");

        var menuKey = _components.Config.MenuShortcutKey;
        if (!string.IsNullOrWhiteSpace(menuKey))
            GameInstallationService.ModifyOptiScalerIniKey(gameDir, "Menu", "ShortcutKey", menuKey!);
    }

    /// <summary>
    /// The custom overlay: the user's imported DLLs applied over the files OptiScaler
    /// just installed — names OptiScaler ships are swapped in place (with loader-name
    /// equivalence), unknown names (e.g. amdxcffx64.dll) are added alongside.
    /// Manifest-tracked, so Revert removes/restores everything. Fully offline: the
    /// base is the install itself (a separate AMD download was removed as redundant —
    /// each OptiScaler release can only hook the FSR binaries it already bundles).
    /// </summary>
    private void InstallCustomOverlay(Game game, IProgress<string>? status)
    {
        var customs = _components.GetCustomDlls();
        if (customs.Count == 0)
            throw new InvalidOperationException("No custom DLLs imported. Add them in Settings first.");

        var overlayDir = Path.Combine(Path.GetTempPath(), "osm_overlay_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(overlayDir);
        try
        {
            var customNames = new List<string>();
            foreach (var c in customs)
            {
                var src = Path.Combine(_components.GetCustomDllsPath(), c.Name);
                if (!File.Exists(src)) continue;
                File.Copy(src, Path.Combine(overlayDir, c.Name), overwrite: true);
                customNames.Add(c.Name);
            }
            if (customNames.Count == 0)
                throw new InvalidOperationException("The custom-DLL library is empty on disk. Re-import your DLLs in Settings.");

            status?.Report($"Applying {customNames.Count} custom DLL(s) over the install…");
            _install.InstallCustomFsrSdk(game, overlayDir, "custom", injectNames: customNames);
        }
        finally
        {
            try { Directory.Delete(overlayDir, true); } catch { }
        }
    }

    /// <summary>
    /// Downloads a community FSR 4 INT8 build from the OptiScaler-Extras repo (at the
    /// chosen version, or latest) and installs its DLL next to the game exe, under
    /// whichever name that release ships.
    /// The FSR ini keys are forced centrally by <see cref="ApplyForcedIniKeys"/>.
    /// Revert removes the DLL via the known-artifact list in <see cref="GameInstallationService"/>.
    /// </summary>
    private async Task InstallInt8CommunityAsync(Game game, string gameDir, string? int8Version, IProgress<string>? status)
    {
        var version = int8Version
            ?? _components.LatestExtrasVersion
            ?? (await GetInt8VersionsAsync()).FirstOrDefault()
            ?? throw new InvalidOperationException("Could not determine an INT8 community build version from source.");

        status?.Report($"Downloading FSR 4 INT8 community build {version}…");
        var extrasDllPath = await _components.DownloadExtrasDllAsync(version);
        if (!File.Exists(extrasDllPath))
            throw new FileNotFoundException("The downloaded INT8 build is incomplete.", extrasDllPath);

        status?.Report($"Installing FSR 4 INT8 {version}…");
        // Keep the name the release ships as — newer INT8 builds are amdxcffx64.dll,
        // older ones amd_fidelityfx_upscaler_dx12.dll, and OptiScaler loads them by name.
        //
        // Recorded in the manifest like every other installed file: without it revert had
        // to fall back to the known-artifact list, and the details page could not tell
        // this app had put the DLL there.
        _install.InstallTrackedFile(game, extrasDllPath, Path.GetFileName(extrasDllPath), gameDir,
            manifest =>
            {
                manifest.IncludesExtras = true;
                manifest.ExtrasVersion = version;
            },
            logTag: "Fsr4Int8");
        game.Fsr4ExtraVersion = version;
    }

    // ── Revert support ──────────────────────────────────────────────────────
    /// <summary>True when OptiScaler has a tracked install (backup/manifest) for this game.</summary>
    /// <summary>
    /// Where OptiScaler actually sits for this game. Usually the install folder, but
    /// some games (UE5) put the executable — and therefore the mod and its ini — in a
    /// nested Binaries folder, which the manifest recorded at install time.
    /// </summary>
    public string GetInstalledDirectory(Game game)
    {
        try
        {
            foreach (var dir in CandidateDirectories(game))
            {
                var manifest = _backupStore.LoadManifest(dir);
                if (manifest?.InstalledGameDirectory is { Length: > 0 } recorded && Directory.Exists(recorded))
                    return recorded;
                if (manifest is not null) return dir;
            }
        }
        catch { /* fall through to the install path */ }
        return game.InstallPath;
    }

    private static IEnumerable<string> CandidateDirectories(Game game)
    {
        if (!string.IsNullOrEmpty(game.ExecutablePath))
        {
            var dir = Path.GetDirectoryName(game.ExecutablePath);
            if (!string.IsNullOrEmpty(dir)) yield return dir;
        }
        if (!string.IsNullOrEmpty(game.InstallPath)) yield return game.InstallPath;
    }

    /// <summary>
    /// Re-reads a game's folder, ignoring the cached result. The analyzer caches on the
    /// folder's write stamp, which does not change when a file is edited in place, so
    /// the details page offers this for anyone who has changed things by hand.
    /// </summary>
    public void RefreshGameAnalysis(Game game)
    {
        GameAnalyzerService.InvalidateCacheForPath(game.InstallPath);
        new GameAnalyzerService().AnalyzeGame(game, forceRefresh: true);
    }

    public bool HasInstall(Game game)
    {
        try
        {
            if (!string.IsNullOrEmpty(game.ExecutablePath) && File.Exists(game.ExecutablePath)
                && _backupStore.HasValidBackup(Path.GetDirectoryName(game.ExecutablePath)!))
                return true;
            return !string.IsNullOrEmpty(game.InstallPath) && _backupStore.HasValidBackup(game.InstallPath);
        }
        catch { return false; }
    }

    // ── Uninstall / revert ──────────────────────────────────────────────────
    public Task UninstallAsync(Game game) => Task.Run(() => _install.UninstallOptiScaler(game));

}
