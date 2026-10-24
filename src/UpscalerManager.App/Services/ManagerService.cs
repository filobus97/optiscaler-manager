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
    private readonly IGpuDetectionService? _gpu;

    /// <param name="gpu">Where the GPU is read from; the platform's own detection unless given.</param>
    public ManagerService(IManualComponentProvider manualProvider, IGpuDetectionService? gpu = null)
    {
        _components = new ComponentManagementService(manualProvider);
        _repository = new DllRepositoryService(_library);
        _gpu = gpu ?? PlatformServiceFactory.CreateGpuDetectionService();
    }

    // ── GPU banner ──────────────────────────────────────────────────────────
    /// <summary>
    /// The GPU the app reports, and which OptiScaler's spoofing default turns on. Cached:
    /// detection shells out to lspci on Linux, and the install screen asks again.
    /// </summary>
    public GpuInfo? DetectPrimaryGpu()
    {
        if (_gpuDetected) return _primaryGpu;
        _gpuDetected = true;
        try { _primaryGpu = _gpu?.GetDiscreteGPU() ?? _gpu?.GetPrimaryGPU(); }
        catch { _primaryGpu = null; }
        return _primaryGpu;
    }

    private GpuInfo? _primaryGpu;
    private bool _gpuDetected;

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
    private readonly AmdDriverLibrary _amdDrivers = new();

    // ── AMD driver files ─────────────────────────────────────────────────────
    /// <summary>The imported AMD driver files, newest first. See <see cref="AmdDriverFiles"/>.</summary>
    public IReadOnlyList<AmdDriverFile> AmdDriverFilesImported => _amdDrivers.List();

    /// <summary>Imports AMD driver files from files, folders, archives or AMD's installer.</summary>
    public Task<AmdDriverImport> ImportAmdDriverFilesAsync(IEnumerable<string> sources, IProgress<string>? status = null)
        => _amdDrivers.ImportAsync(sources, status);

    public void DeleteAmdDriverFile(AmdDriverFile file) => _amdDrivers.Delete(file);

    private readonly Dictionary<string, IReadOnlyList<string>> _driverFsrVersions = new();

    /// <summary>
    /// The FSR 4 versions an imported driver file provides, newest first, read from its
    /// provider table. Only FSR 4 numbers are believed: a driver DLL is large, and full of
    /// other strings that look like versions.
    /// </summary>
    public IReadOnlyList<string> AmdDriverFsrVersions(AmdDriverFile file)
    {
        lock (_driverFsrVersions)
        {
            if (_driverFsrVersions.TryGetValue(file.Path, out var known)) return known;
            var read = FidelityFxVersion.AllFromBinary(file.Path).Where(v => v.StartsWith("4.")).ToList();
            _driverFsrVersions[file.Path] = read;
            return read;
        }
    }

    /// <summary>"AMD driver FSR 4.1.2 (2.4.0.4370)": the FSR version where the file says, and always the file's own.</summary>
    public string AmdDriverLabel(AmdDriverFile file) =>
        AmdDriverFsrVersions(file) is { Count: > 0 } fsr
            ? $"AMD driver FSR {fsr[0]} ({file.Version})"
            : $"AMD driver FSR 4 ({file.Version})";

    /// <summary>
    /// Whether an install should place driver files unless told otherwise: on Linux, where
    /// there is no AMD driver store for OptiScaler to find them in. On Windows the installed
    /// driver provides them, and a copy next to OptiScaler would pin an older version.
    /// </summary>
    public static bool DriverFilesNeeded => OperatingSystem.IsLinux();

    /// <summary>What the release being installed understands about FSR 4 models and forcing.</summary>
    public Fsr4IniSchema Fsr4Schema(string? optiscalerVersion) => Fsr4IniSchema.Of(ReleaseCache(optiscalerVersion));

    private IReadOnlyList<IniKeyChange> Fsr4Keys(Fsr4Options fsr4, string? optiscalerVersion)
        => Fsr4Models.IniKeys(Fsr4Schema(optiscalerVersion), fsr4.Model, fsr4.Force, GpuGeneration);

    /// <summary>The preview's lines for driver files: where each goes and what it is.</summary>
    private static IReadOnlyList<string> DriverPreviewLines(Fsr4Options fsr4) =>
        fsr4.DriverFiles
            .Select(f => $"{f.FileName}  (AMD driver {f.Version}, FSR 4 for {AmdDriverFiles.Describe(f.Api)} — next to OptiScaler)")
            .ToList();

    private readonly DllSwapService _swaps = new();
    private readonly Fsr4SwapService _fsr4 = new();
    private readonly DllLibraryService _library = new();
    private readonly DllRepositoryService _repository;

    /// <summary>
    /// One row per swappable DLL present in this game. Stale records are dropped first,
    /// so a game that was verified or reinstalled since the last swap does not claim one
    /// that is no longer there.
    /// </summary>
    public IReadOnlyList<SwapSlot> SwapSlots(Game game)
    {
        _swaps.ForgetStaleSwaps(game);
        _fsr4.ForgetStale(game);
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

    /// <summary>A build of a swappable DLL that could go into a game, and where it is.</summary>
    /// <param name="Display">How the version reads, which for FSR is not the same thing.</param>
    public readonly record struct AvailableBuild(string Version, string Source, string Display);

    /// <summary>
    /// The newest build of one DLL that can be installed without the network: what the
    /// library holds, what the user's other games carry, and what the OptiScaler
    /// releases already downloaded contain.
    /// </summary>
    public AvailableBuild? BestLocalBuild(string fileName, Game exclude)
    {
        var found = LibraryBuilds(fileName)
            .Select(b => new AvailableBuild(
                b.Version, "in your library", DllSwapService.DescribeBuild(fileName, b.Version, b.Path)))
            .Concat(HarvestableBuilds(fileName, exclude).Concat(OptiScalerBuilds(fileName))
                .Select(h => new AvailableBuild(
                    h.Version, $"in {h.GameName}", DllSwapService.DescribeBuild(fileName, h.Version, h.Path))))
            .OrderBy(b => b.Version, VersionOrder.Descending)
            .ToList();

        return found.Count > 0 ? found[0] : null;
    }

    /// <summary>
    /// The newest build of one DLL the archive index lists. The index is cached for a
    /// day, so a page can ask for every row it shows; null when archive downloads are
    /// off, the file is not covered, or the index cannot be read.
    /// </summary>
    public async Task<AvailableBuild?> BestArchiveBuildAsync(
        string fileName, CancellationToken cancel = default)
    {
        var builds = await RepositoryBuildsAsync(fileName, cancel).ConfigureAwait(false);
        // why: development builds are in the index and can carry the highest version.
        // Offering one as "the update" would push a debug DLL at someone who only
        // pressed Update; the version list still lists them as a deliberate choice.
        var newest = builds.FirstOrDefault(b => !b.IsDevFile);
        return newest is not null
            ? new AvailableBuild(
                newest.Version,
                $"in the {DllRepository.SourceName} archive",
                DllRepositoryService.DescribeBuild(newest))
            : null;
    }

    /// <summary>Copies a build out of a game, or out of an OptiScaler release, into the library.</summary>
    public LibraryDll HarvestBuild(HarvestableDll source) => _library.Harvest(source);

    // NOTE: the community FSR 4 builds are not a swap source. They ship
    // amd_fidelityfx_upscaler_dx12.dll, which OptiScaler installs and manages — so the
    // Extras download belongs to the install route, not to swapping.

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

    /// <summary>Which FSR versions an install will be able to choose from, and why.</summary>
    /// <param name="Versions">Newest first. Empty when nothing could be read.</param>
    /// <param name="Explanation">Where the list came from, for the line under the picker.</param>
    public sealed record FfxVersionOptions(IReadOnlyList<string> Versions, string Explanation);

    /// <summary>
    /// The FSR versions that will be available <b>after</b> this install, newest first.
    ///
    /// Composed from the libraries the install is about to copy in — which are already
    /// on disk in the component caches — merged with whatever the game already has.
    /// Reading only the game answers the wrong question: a game about to receive an
    /// FSR 4 INT8 build has no FSR 4 in it yet, so the picker offered 3.1.2 and older
    /// and FSR 4 looked unavailable at exactly the moment it was being installed.
    /// </summary>
    public FfxVersionOptions FidelityFxVersionOptions(
        Game game, Fsr4Backend backend, string? int8Version, string? optiscalerVersion = null,
        IReadOnlyList<AmdDriverFile>? driverFiles = null)
    {
        // Behind the omniloader the list is every picked build's, merged at run time.
        if (backend == Fsr4Backend.Omniloader)
            return new FfxVersionOptions(Array.Empty<string>(),
                "With several builds, the version is picked in OptiScaler's overlay, under FFX Upscaler. "
                + "The first build you ticked runs until then.");

        var fromGame = FidelityFxProviderScanner.FromDirectory(game.InstallPath);
        FfxProviders? fromInstall = null;
        string? label = null;

        switch (backend)
        {
            case Fsr4Backend.Int8Community when !string.IsNullOrWhiteSpace(int8Version):
                fromInstall = FidelityFxProviderScanner.FromDirectory(
                    _components.GetExtrasDllCachePath(int8Version!));
                label = $"the {int8Version} community build";

                // Not downloaded yet, so there is no binary to read. The Extras releases
                // are tagged by the FSR version they carry, so the tag is the next best
                // statement — and it is flagged as the author's label, not a file read.
                if (fromInstall is null
                    && FidelityFxProviderScanner.VersionFromReleaseTag(int8Version) is { } tagged)
                {
                    fromInstall = new FfxProviders(new[] { tagged }, $"the {int8Version} release tag");
                    label = $"the {int8Version} community build (from its release name — it is not downloaded yet)";
                }
                break;

            case Fsr4Backend.CustomMerged:
                fromInstall = FidelityFxProviderScanner.FromDirectory(_components.GetCustomDllsPath());
                label = "the DLLs you imported";
                break;

            default:
                fromInstall = FidelityFxProviderScanner.FromDirectory(ReleaseCache(optiscalerVersion));
                label = "the OptiScaler release being installed";
                break;
        }

        // OptiScaler loads AMD's driver file beside its own upscaler, so both lists are on offer.
        if (driverFiles?.FirstOrDefault(f => f.Api == AmdDriverApi.Dx12) is { } driver
            && AmdDriverFsrVersions(driver) is { Count: > 0 } fromDriver)
        {
            fromInstall = new FfxProviders(
                FidelityFxProviderScanner.Merge(fromInstall?.Versions, fromDriver),
                fromInstall is null ? driver.FileName : $"{fromInstall.FromFile} and {driver.FileName}");
            label = fromInstall.FromFile == driver.FileName ? "your AMD driver files" : $"{label} and your AMD driver files";
        }

        var versions = FidelityFxProviderScanner.Merge(fromInstall?.Versions, fromGame?.Versions);

        var explanation = (fromInstall, fromGame) switch
        {
            (not null, not null) =>
                $"From {label} ({fromInstall.FromFile}) and what the game already has "
                + $"({fromGame.FromFile}).",
            (not null, null) => $"From {label} ({fromInstall.FromFile}).",
            (null, not null) => $"From the FidelityFX library already in this game ({fromGame.FromFile}).",
            _ => "No FidelityFX library could be read yet, so only \u201cnewest\u201d can be offered. "
                 + "OptiScaler's overlay lists every version the library it loads provides.",
        };

        return new FfxVersionOptions(versions, explanation);
    }

    /// <summary>Takes a file the user chose into the library.</summary>
    public LibraryDll ImportSwappableDll(string path) => _library.Import(path);

    /// <summary>What one picked file turned into: the builds it added, or why it did not.</summary>
    public sealed record ImportResult(string Source, IReadOnlyList<LibraryDll> Added, string? Error);

    /// <summary>
    /// Imports every file the user picked — DLLs and zips — reporting each separately
    /// so one bad file does not lose the rest.
    /// </summary>
    public IReadOnlyList<ImportResult> ImportSwappable(IEnumerable<string> paths)
    {
        var results = new List<ImportResult>();
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            try { results.Add(new ImportResult(name, _library.ImportAny(path), null)); }
            catch (Exception ex) { results.Add(new ImportResult(name, Array.Empty<LibraryDll>(), ex.Message)); }
        }
        return results;
    }

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

    /// <summary>Whether the build this app swapped in is still the one in the game.</summary>
    public static DllSwapService.SwapStanding SwapStandingOf(SwappedFile? swapped) =>
        DllSwapService.StandingOf(swapped);

    /// <summary>
    /// Clears a swap the game has since replaced, restoring whatever copies are still
    /// ours and leaving the rest alone. Returns the directories left alone.
    /// </summary>
    public IReadOnlyList<string> ForgetSwap(Game game, SwappedFile swapped)
    {
        var left = _swaps.RevertWhatRemains(game, swapped);
        RefreshGameAnalysis(game);
        return left;
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
        await AwaitRunningRefreshAsync();
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
        Fsr4Backend.Int8Community => OptiScalerExtrasEnabled,
        Fsr4Backend.FasterShaders => Fsr4ShaderOverridesEnabled && FasterFsr4Refusal() is null,
        Fsr4Backend.Omniloader => Omniloader.IsAvailable,
        _ => true, // Default needs nothing
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
    /// Whether the DLSS Swapper archive is offered as a swap download source. See
    /// <see cref="AppConfiguration.SwapRepositoryDownloads"/> for why it defaults on
    /// and why it has a switch at all.
    /// </summary>
    public bool SwapRepositoryDownloadsEnabled
    {
        get => _components.Config.SwapRepositoryDownloads;
        set { _components.Config.SwapRepositoryDownloads = value; _components.SaveConfiguration(); }
    }

    /// <summary>Whether OptiScaler Client's FSR 4 INT8 builds are offered. See <see cref="AppConfiguration.SourceOptiScalerExtras"/>.</summary>
    public bool OptiScalerExtrasEnabled
    {
        get => _components.Config.SourceOptiScalerExtras;
        set { _components.Config.SourceOptiScalerExtras = value; _components.SaveConfiguration(); }
    }

    /// <summary>Which Radeon generation this machine's GPU is, for anything cut per generation.</summary>
    public RadeonGeneration GpuGeneration => RadeonGenerations.Of(DetectPrimaryGpu());

    /// <summary>Why this GPU gets no faster FSR 4 build, or null when it gets both.</summary>
    public string? FasterFsr4Refusal() => Fsr4ShaderOverrides.RefusalFor(GpuGeneration);

    /// <summary>Whether the faster FSR 4.1.1 builds are offered. See <see cref="AppConfiguration.SourceFsr4ShaderOverrides"/>.</summary>
    public bool Fsr4ShaderOverridesEnabled
    {
        get => _components.Config.SourceFsr4ShaderOverrides;
        set { _components.Config.SourceFsr4ShaderOverrides = value; _components.SaveConfiguration(); }
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

    /// <summary>The toolbar's anti-cheat filter, saved as it is toggled.</summary>
    public bool HideGamesWithAntiCheat
    {
        get => _components.Config.ScanSources.HideAntiCheatGames;
        set
        {
            _components.Config.ScanSources.HideAntiCheatGames = value;
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

    private Task? _sourcesRefresh;

    /// <summary>When the release lists were last refreshed in this session, or null.</summary>
    public DateTime? SourcesRefreshedAt { get; private set; }

    /// <summary>
    /// Refreshes what every source lists: OptiScaler's stable, beta and nightly releases,
    /// OptiScaler Client extras, fakenvapi, OptiPatcher, Nukem's releases, and the DLSS
    /// Swapper index. Run at startup and by Settings' update check, so a list is current
    /// before anyone opens it rather than after the first download. Never throws.
    /// </summary>
    /// <remarks>
    /// why: the lists used to be fetched only when empty, so once anything was cached the
    /// install screen kept offering it — a release days old stayed "latest" until some
    /// download happened to refresh the cache.
    /// </remarks>
    public Task RefreshSourcesAsync(bool force = false)
    {
        if (_sourcesRefresh is { IsCompleted: false } running) return running;
        return _sourcesRefresh = RunAsync();

        async Task RunAsync()
        {
            try { await _components.CheckForUpdatesAsync(force); } catch { /* offline / rate-limited */ }
            if (SwapRepositoryDownloadsEnabled)
                try { await DllRepositoryService.WarmAsync(); } catch { /* the cached index still serves */ }
            SourcesRefreshedAt = DateTime.Now;
        }
    }

    /// <summary>Waits for a refresh already under way, so a list opened meanwhile shows its result.</summary>
    private async Task AwaitRunningRefreshAsync()
    {
        if (_sourcesRefresh is { IsCompleted: false } running)
            await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(20)));
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
    public InstallPreview BuildInstallPreview(Game game, Fsr4Backend backend, UpscalerSelection selection,
        bool addFakenvapi = false, bool addNukemFg = false,
        SpoofMethod spoofMethod = SpoofMethod.Off, Fsr4Options? fsr4 = null, bool fsr4Watermark = false,
        string? optiscalerVersion = null, FasterFsr4Kind fasterKind = FasterFsr4Kind.Exact,
        IReadOnlyList<OmniBuildOption>? omniBuilds = null)
        => ComponentRegistry.BuildInstallPreview(backend, selection, ComponentRegistry.DefaultInjectionDll, MenuShortcutKey,
            backend == Fsr4Backend.CustomMerged ? _components.GetCustomDlls().Select(d => d.Name).ToList() : null,
            addFakenvapi, addNukemFg, spoofMethod, Fsr4Keys(fsr4 ?? Fsr4Options.None, optiscalerVersion), fsr4Watermark,
            legacyFsr4UpdateKey: GameInstallationService.IniDocumentsKey(
                ReleaseCache(optiscalerVersion), "FSR", "Fsr4Update"),
            nukemKeys: GameInstallationService.NukemFrameGenerationKeys(ReleaseCache(optiscalerVersion)),
            fasterKind: fasterKind,
            omniFiles: backend == Fsr4Backend.Omniloader ? OmniPreviewLines(omniBuilds ?? Array.Empty<OmniBuildOption>()) : null,
            extraFiles: DriverPreviewLines(fsr4 ?? Fsr4Options.None));

    /// <summary>
    /// The downloaded release a preview should read: the version the user picked, or
    /// whatever would be installed if they picked nothing.
    /// </summary>
    private string ReleaseCache(string? optiscalerVersion) =>
        _components.GetOptiScalerCachePath(
            optiscalerVersion
            ?? _components.LatestStableVersion
            ?? _components.OptiScalerVersion
            ?? "latest");

    // ── OptiScaler version list ─────────────────────────────────────────────
    /// <summary>
    /// OptiScaler release versions selectable at install (newest first): the remote
    /// list when a check has run, merged with anything already downloaded; falls back
    /// to downloaded-only when offline.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetOptiScalerVersionsAsync()
    {
        await AwaitRunningRefreshAsync();
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

    /// <summary>Version strings from OptiScaler's nightly repository.</summary>
    public bool IsNightlyOptiScalerVersion(string version) => _components.NightlyVersions.Contains(version);

    // ── Install OptiScaler ──────────────────────────────────────────────────
    public async Task InstallAsync(Game game, Fsr4Backend backend, string? int8Version, UpscalerSelection selection,
        OptiScalerProfile? iniProfile, IProgress<string>? status = null,
        bool addFakenvapi = false, bool addNukemFg = false,
        SpoofMethod spoofMethod = SpoofMethod.Off, Fsr4Options? fsr4 = null, bool fsr4Watermark = false,
        string? optiscalerVersion = null, FasterFsr4Kind fasterKind = FasterFsr4Kind.Exact,
        IReadOnlyList<OmniBuildOption>? omniBuilds = null)
    {
        LastInstallNotes = Array.Empty<string>();
        fsr4 ??= Fsr4Options.None;
        foreach (var file in fsr4.DriverFiles)
            if (!File.Exists(file.Path))
                throw new InvalidOperationException($"The imported {file.FileName} {file.Version} is gone. Import it again in Settings.");
        // OptiScaler brings its own FidelityFX files, and the FSR 4 set would be written over.
        if (_fsr4.IsActive(game))
            throw new InvalidOperationException(
                $"FSR 4 without OptiScaler is set up in {game.Name}. Undo it on the game's page first.");
        if (backend == Fsr4Backend.FasterShaders && FasterFsr4Refusal() is { } refused)
            throw new InvalidOperationException(refused);
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
        // everything under one manifest (Revert then removes add-ons too). The faster FSR 4
        // build too: anything that can refuse or fail to download does it before a single
        // file is written, not after OptiScaler is half in place.
        var fasterDll = backend == Fsr4Backend.FasterShaders
            ? await PrepareFasterFsr4Async(cachePath, fasterKind, status)
            : null;
        var omni = backend == Fsr4Backend.Omniloader
            ? await PrepareOmniBuildsAsync(cachePath, omniBuilds ?? Array.Empty<OmniBuildOption>(), status)
            : null;
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

        string? extrasPlaced = null;
        switch (backend)
        {
            case Fsr4Backend.CustomMerged:
            {
                InstallCustomOverlay(game, status);
                break;
            }
            case Fsr4Backend.Int8Community:
            {
                extrasPlaced = await InstallInt8CommunityAsync(game, gameDir, int8Version, status);
                break;
            }
            case Fsr4Backend.FasterShaders:
            {
                PlaceFasterFsr4(game, gameDir, fasterDll!, status);
                break;
            }
            case Fsr4Backend.Omniloader:
            {
                PlaceOmniloader(game, gameDir, omni!, status);
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

        PlaceDriverFiles(game, gameDir, fsr4, status, extrasPlaced);

        // Force ONLY the keys the Manager owns; everything else in the chosen ini
        // (default or custom) is left untouched. FSR 4 is always made *available*;
        // whether it is *selected* depends on selectFsr4.
        ApplyForcedIniKeys(gameDir, selection, spoofMethod, Fsr4Keys(fsr4, version), fsr4Watermark);

        status?.Report("Done.");
    }

    /// <summary>Two directory paths naming the same place, trailing separator aside.</summary>
    private static bool PathsEqual(string? a, string? b)
        => a is not null && b is not null
           && string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'),
                            Path.GetFullPath(b).TrimEnd('/', '\\'),
                            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Downloads the latest OptiPatcher.asi (cached fallback offline) and places it in
    /// the folder this OptiScaler install reads plugins from.
    /// <c>[Plugins] LoadAsiPlugins=true</c> is forced by ApplyForcedIniKeys.
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

        // Where this OptiScaler install actually looks, which is not always the game
        // folder any more. A plugin in the wrong folder is not reported by anything.
        var pluginsDir = GameInstallationService.OptiScalerPluginsDirectory(gameDir);
        Directory.CreateDirectory(pluginsDir);
        File.Copy(asiPath, Path.Combine(pluginsDir, "OptiPatcher.asi"), overwrite: true);
        Log.Write($"[OptiPatcher] Installed into {pluginsDir}");

        // An earlier version of this app always wrote <game>/plugins. Left behind it is
        // a second, older copy in a folder the current release may still read.
        var stale = Path.Combine(gameDir, "plugins", "OptiPatcher.asi");
        if (!PathsEqual(Path.GetDirectoryName(stale), pluginsDir) && File.Exists(stale))
        {
            try
            {
                File.Delete(stale);
                Log.Write("[OptiPatcher] Removed the copy an older install left in the game folder.");
            }
            catch (Exception ex) { Log.Write($"[OptiPatcher] Could not remove the old copy: {ex.Message}"); }
        }

        status?.Report("Installed OptiPatcher. OptiScaler's overlay shows (OP) when the patch lands.");
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
    /// [Upscalers] Dx12/Dx11/VulkanUpscaler and [FSR] UpscalerIndex from the chosen
    /// upscaler, the legacy [FSR] Fsr4Update where a release still has it,
    /// the FSR 4 model and force keys (named per release, see <see cref="Fsr4Models"/>),
    /// the opt-in [FSR] Fsr4EnableWatermark toggle,
    /// [Spoofing] Dxgi, and [Menu] ShortcutKey when a menu key is configured. Applied
    /// last, so it overrides anything the backend installers set. Off toggles leave the
    /// keys untouched (OptiScaler's auto behaviour).
    /// </summary>
    private void ApplyForcedIniKeys(string gameDir, UpscalerSelection selection,
        SpoofMethod spoofMethod = SpoofMethod.Off,
        IReadOnlyList<IniKeyChange>? fsr4Keys = null, bool fsr4Watermark = false)
    {
        // Only where the release still has the key; it is gone from current OptiScaler.
        GameInstallationService.SelectLegacyFsr4Update(gameDir);

        // Which upscaler runs at all, and for the FidelityFX family which provider
        // version. This is the setting that makes the choice mean anything: the DX12
        // default is XeSS, and the FSR keys are not read while it is running. Written
        // after the backend installers so it wins over them.
        if (selection.IsAuto)
        {
            GameInstallationService.SelectUpscaler(gameDir, UpscalerChoice.Auto, null);
            // Clear any provider index an earlier install left behind, so "let
            // OptiScaler decide" really does.
            GameInstallationService.ModifyOptiScalerIniKey(gameDir, "FSR", "UpscalerIndex", "auto");
        }
        else
        {
            GameInstallationService.SelectUpscaler(gameDir, selection.Choice, selection.FfxProviderIndex);
            if (!selection.IsFidelityFx)
                GameInstallationService.ModifyOptiScalerIniKey(gameDir, "FSR", "UpscalerIndex", "auto");
        }

        foreach (var key in fsr4Keys ?? Array.Empty<IniKeyChange>())
            GameInstallationService.ModifyOptiScalerIniKey(gameDir, key.Section, key.Key, key.Value);
        if (fsr4Watermark)
            GameInstallationService.ModifyOptiScalerIniKey(gameDir, "FSR", "Fsr4EnableWatermark", "true");
        // Written on every install, including "auto": the ini is preserved across a
        // reinstall, so leaving the key alone would keep a force the user has since
        // turned off. Auto is also the only value OptiScaler's own per-game spoofing
        // fixes are read under.
        GameInstallationService.ModifyOptiScalerIniKey(gameDir, "Spoofing", "Dxgi", spoofMethod switch
        {
            SpoofMethod.On => "true",
            SpoofMethod.Off => "false",
            _ => "auto",
        });
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
    /// <returns>The file name it installed under.</returns>
    private async Task<string> InstallInt8CommunityAsync(Game game, string gameDir, string? int8Version, IProgress<string>? status)
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
        // Under the name the release ships, where OptiScaler loads that name: the upscaler
        // where it loads its upscaler, a build named like AMD's driver file next to
        // OptiScaler. The game folder is neither once OptiScaler sits in a subfolder (0.9.5
        // and later): an upscaler left there lost to the one OptiScaler ships, and
        // OptiScaler 10 no longer looks there for the driver file at all.
        //
        // Recorded in the manifest like every other installed file: without it revert had
        // to fall back to the known-artifact list, and the details page could not tell
        // this app had put the DLL there.
        var name = Path.GetFileName(extrasDllPath);
        var target = AmdDriverFiles.ApiOf(name) is not null
            ? Path.Combine(GameInstallationService.OptiScalerLibraryDirectory(gameDir), name)
            : GameInstallationService.OptiScalerUpscalerPath(gameDir);
        _install.InstallTrackedFile(game, extrasDllPath, Path.GetRelativePath(gameDir, target), gameDir,
            manifest =>
            {
                manifest.IncludesExtras = true;
                manifest.ExtrasVersion = version;
            },
            logTag: "Fsr4Int8");
        game.Fsr4ExtraVersion = version;
        return name;
    }

    /// <summary>
    /// Everything about the faster FSR 4.1.1 build that can say no, before the install
    /// writes anything: the GPU, the FSR version the release being installed ships, the
    /// listing, the download and its checks. Returns the checked DLL.
    /// </summary>
    /// <param name="replacesBundled">
    /// True when the build is to replace the release's own upscaler, which only works if
    /// that is the 4.1.1 the build is made from. Behind the omniloader it sits beside the
    /// others instead, so the release's version does not matter.
    /// </param>
    private async Task<string> PrepareFasterFsr4Async(string releaseCache, FasterFsr4Kind kind, IProgress<string>? status,
        bool replacesBundled = true)
    {
        var generation = GpuGeneration;
        if (Fsr4ShaderOverrides.RefusalFor(generation) is { } refused)
            throw new InvalidOperationException(refused);

        if (replacesBundled)
        {
            var bundled = BundledUpscaler(releaseCache);
            if (Fsr4ShaderOverrides.BaseMismatch(bundled is null ? null : PeFileInspector.Inspect(bundled).FileVersion) is { } mismatch)
                throw new InvalidOperationException(mismatch);
        }

        status?.Report("Looking up the faster FSR 4 builds…");
        var releases = await _components.Fsr4OverridesReleasesAsync();
        if (releases.Count == 0)
            throw new InvalidOperationException(
                "The faster FSR 4 builds could not be listed. Check the connection and try again.");
        var assetName = Fsr4ShaderOverrides.AssetFor(generation)!;
        var (release, asset) = ComponentManagementService.PickFsr4OverridesRelease(releases, assetName)
            ?? throw new InvalidOperationException(
                $"No release of the shader overrides has {assetName} any more — the project has renamed its "
                + "downloads, and this app needs an update to follow.");
        if (release != releases[0])
            Log.Write($"[FasterFsr4] Newest release {releases[0].Tag} has no {assetName}; using {release.Tag}");

        status?.Report($"Downloading faster FSR 4.1.1 ({kind.ToString().ToLowerInvariant()}, {release.Tag})…");
        var dll = await _components.DownloadFsr4OverrideAsync(release.Tag, asset, kind);
        Log.Write($"[FasterFsr4] {assetName} ({kind}) from {release.Tag} ready; OptiScaler's overlay should name it "
                  + Fsr4ShaderOverrides.OverlayLabel(generation, kind));
        return dll;
    }

    /// <summary>
    /// Puts the prepared build over the FSR 4.1.1 upscaler OptiScaler just installed —
    /// the copy OptiScaler loads, wherever this release keeps it — tracked, so Remove
    /// takes it away with the rest.
    /// </summary>
    private void PlaceFasterFsr4(Game game, string gameDir, string dll, IProgress<string>? status)
    {
        var target = GameInstallationService.OptiScalerUpscalerPath(gameDir);

        // Checked again on the file actually being replaced: a profile can point
        // OptiScaler at a different copy than the one the release shipped.
        var installed = File.Exists(target) ? PeFileInspector.Inspect(target).FileVersion : null;
        if (Fsr4ShaderOverrides.BaseMismatch(installed) is { } mismatch)
            throw new InvalidOperationException(mismatch);

        status?.Report("Installing faster FSR 4.1.1…");
        _install.InstallTrackedFile(game, dll, Path.GetRelativePath(gameDir, target), gameDir, logTag: "FasterFsr4");
        Log.Write($"[FasterFsr4] Installed at {target}");
    }

    // ── Several builds behind the omniloader ────────────────────────────────
    /// <summary>One build the omniloader can load, as the install screen lists it.</summary>
    /// <param name="Id">"optiscaler", "faster:exact", "faster:lossy", "extras:&lt;version&gt;" or "custom:&lt;file&gt;".</param>
    public sealed record OmniBuildOption(string Id, string Label, string? Note = null);

    /// <summary>The FSR upscaler a release ships, or null when it has none.</summary>
    private static string? BundledUpscaler(string releaseCache) =>
        Directory.Exists(releaseCache)
            ? Directory.EnumerateFiles(releaseCache, Omniloader.UpscalerDllName, SearchOption.AllDirectories).FirstOrDefault()
            : null;

    /// <summary>
    /// Every build the omniloader could load on this machine, from every source that is
    /// switched on: the release's own, the faster 4.1.1 builds this GPU gets, each
    /// OptiScaler Client extras release, and the user's imported upscalers.
    /// </summary>
    public async Task<IReadOnlyList<OmniBuildOption>> OmniBuildOptionsAsync()
    {
        var options = new List<OmniBuildOption>
        {
            new("optiscaler", "OptiScaler's own FSR", "The upscaler the OptiScaler release ships."),
        };

        if (Fsr4ShaderOverridesEnabled && FasterFsr4Refusal() is null)
        {
            var generation = GpuGeneration;
            options.Add(new("faster:exact", "Faster FSR 4.1.1, exact",
                $"Shader overrides, the build for {RadeonGenerations.Describe(generation)}. "
                + $"Listed as {Fsr4ShaderOverrides.OverlayLabel(generation, FasterFsr4Kind.Exact)}."));
            options.Add(new("faster:lossy", "Faster FSR 4.1.1, lossy",
                $"About twice as fast, softer. Listed as {Fsr4ShaderOverrides.OverlayLabel(generation, FasterFsr4Kind.Lossy)}."));
        }

        if (OptiScalerExtrasEnabled)
            foreach (var (version, pre) in await GetInt8ReleasesAsync())
                options.Add(new($"extras:{version}", $"OptiScaler Client extras {version}",
                    pre ? "Pre-release." : null));

        foreach (var custom in _components.GetCustomDlls())
        {
            var path = Path.Combine(_components.GetCustomDllsPath(), custom.Name);
            if (File.Exists(path) && AmdDriverFiles.ApiOf(custom.Name) != AmdDriverApi.Vulkan
                && Omniloader.Classify(path) != OmniBuildKind.NotUpscaler)
                options.Add(new($"custom:{custom.Name}", $"Your {custom.Name}", "Imported in Settings."));
        }

        // Each imported DirectX 12 driver file is a build of its own: AMD's FSR 4.1.2 and
        // later carry every model and pick the one for the GPU.
        foreach (var driver in _amdDrivers.List().Where(f => f.Api == AmdDriverApi.Dx12))
            options.Add(new($"driver:{driver.Version}", AmdDriverLabel(driver),
                "Your imported amdxcffx64.dll. It picks the FSR 4 model for this GPU itself."));
        return options;
    }

    /// <summary>The preview's lines for the builds picked, where each one will go.</summary>
    private IReadOnlyList<string> OmniPreviewLines(IReadOnlyList<OmniBuildOption> builds)
    {
        var lines = new List<string>();

        // Only a build already on disk can be read; the rest are assumed to be ordinary
        // upscalers until the install downloads and checks them.
        var kinds = builds
            .Select(b => OmniSourceIfCached(b) is { } known && File.Exists(known)
                ? Omniloader.Classify(known) : OmniBuildKind.Module)
            .ToList();
        var names = Omniloader.FileNamesFor(builds.Select((b, i) => (b.Label, kinds[i])).ToList());
        for (var i = 0; i < builds.Count; i++)
            lines.Add($"{Omniloader.BuildsFolder}/{names[i]}  ({builds[i].Label}"
                      + (kinds[i] == OmniBuildKind.DriverStyle ? ", AMD driver file" : "")
                      + (i == 0 ? " — runs first" : "") + ")");
        if (builds.Count == 0)
            lines.Add("(no builds picked — tick at least one)");
        return lines;
    }

    /// <summary>Where a build already sits in the app's caches, or null when it has to be downloaded.</summary>
    private string? OmniSourceIfCached(OmniBuildOption build)
    {
        var (source, value) = SplitOmniId(build.Id);
        return source switch
        {
            "extras" => Fsr4Int8Build.FindIn(_components.GetExtrasDllCachePath(value)),
            "custom" => Path.Combine(_components.GetCustomDllsPath(), value),
            "driver" => ImportedDx12Driver(value)?.Path,
            _ => null,
        };
    }

    private AmdDriverFile? ImportedDx12Driver(string version) =>
        _amdDrivers.List().FirstOrDefault(f => f.Api == AmdDriverApi.Dx12 && f.Version == version);

    private static (string Source, string Value) SplitOmniId(string id)
    {
        var colon = id.IndexOf(':');
        return colon < 0 ? (id, "") : (id[..colon], id[(colon + 1)..]);
    }

    /// <summary>One build ready to place: its file, its label, and what it turned out to be.</summary>
    private sealed record PreparedOmniBuild(string Path, string Label, OmniBuildKind Kind);

    /// <summary>
    /// Fetches and checks every picked build before anything is written, so a build
    /// that cannot be had, or turns out not to be an upscaler, stops the install while
    /// the game is still untouched.
    /// </summary>
    private async Task<IReadOnlyList<PreparedOmniBuild>> PrepareOmniBuildsAsync(
        string releaseCache, IReadOnlyList<OmniBuildOption> builds, IProgress<string>? status)
    {
        if (!Omniloader.IsAvailable)
            throw new InvalidOperationException("This build of the app does not include the omniloader.");
        if (builds.Count == 0)
            throw new InvalidOperationException("Pick at least one FSR build for the omniloader to load.");

        var prepared = new List<PreparedOmniBuild>();
        var skipped = new List<string>();
        foreach (var build in builds)
        {
            // One build that cannot be had leaves the rest to install: a picker of several
            // builds should not fail as a whole because one release changed upstream.
            try
            {
                prepared.Add(await PrepareOmniBuildAsync(releaseCache, build, status));
            }
            catch (Exception ex)
            {
                Log.Write($"[Omniloader] Skipping {build.Label}: {ex.Message}");
                status?.Report($"Skipping {build.Label}: {ex.Message}");
                skipped.Add($"{build.Label} ({ex.Message})");
            }
        }
        LastInstallNotes = skipped.Count == 0
            ? Array.Empty<string>()
            : skipped.Select(s => "Skipped " + s).ToList();

        if (prepared.Count == 0)
            throw new InvalidOperationException(
                $"No build was left to install. {string.Join(" ", LastInstallNotes)}.");
        return prepared;
    }

    /// <summary>
    /// What the last install left out, and why — builds the omniloader could not get or
    /// use. Empty when everything picked went in.
    /// </summary>
    public IReadOnlyList<string> LastInstallNotes { get; private set; } = Array.Empty<string>();

    private async Task<PreparedOmniBuild> PrepareOmniBuildAsync(
        string releaseCache, OmniBuildOption build, IProgress<string>? status)
    {
        var (source, value) = SplitOmniId(build.Id);
        string path = source switch
        {
            "optiscaler" => BundledUpscaler(releaseCache)
                ?? throw new InvalidOperationException("This OptiScaler release ships no FSR upscaler of its own."),
            "faster" => await PrepareFasterFsr4Async(releaseCache,
                value == "lossy" ? FasterFsr4Kind.Lossy : FasterFsr4Kind.Exact, status, replacesBundled: false),
            "extras" => await DownloadExtrasForOmniAsync(value, status),
            "custom" => Path.Combine(_components.GetCustomDllsPath(), value),
            "driver" => ImportedDx12Driver(value)?.Path
                ?? throw new InvalidOperationException("it is no longer imported"),
            _ => throw new InvalidOperationException($"Unknown build: {build.Label}"),
        };
        if (!File.Exists(path))
            throw new FileNotFoundException("its file is missing", path);

        var kind = Omniloader.Classify(path);
        if (kind == OmniBuildKind.NotUpscaler)
            throw new InvalidOperationException("it is not an FSR upscaler, so the omniloader cannot load it");
        if (kind == OmniBuildKind.DriverStyle && AmdDriverFiles.ApiOf(Path.GetFileName(path)) == AmdDriverApi.Vulkan)
            throw new InvalidOperationException(
                "it is the Vulkan driver file, and the omniloader stands in for the DirectX 12 upscaler only");
        return new PreparedOmniBuild(path, build.Label, kind);
    }

    private async Task<string> DownloadExtrasForOmniAsync(string version, IProgress<string>? status)
    {
        status?.Report($"Downloading OptiScaler Client extras {version}…");
        return await _components.DownloadExtrasDllAsync(version);
    }

    /// <summary>
    /// Puts the omniloader where OptiScaler loads its FSR upscaler, the builds in a
    /// folder beside it, and the list it reads — all tracked, so Remove takes them away.
    /// AMD driver files go in the same folder, named apart from the copy OptiScaler loads.
    /// </summary>
    private void PlaceOmniloader(Game game, string gameDir, IReadOnlyList<PreparedOmniBuild> builds, IProgress<string>? status)
    {
        var target = GameInstallationService.OptiScalerUpscalerPath(gameDir);
        var libDir = Path.GetDirectoryName(target)!;
        string Rel(string path) => Path.GetRelativePath(gameDir, Path.Combine(libDir, path));

        var modules = builds;
        var names = Omniloader.FileNamesFor(modules.Select(m => (m.Label, m.Kind)).ToList());
        var buildsDir = Omniloader.BuildsFolder.Replace('/', Path.DirectorySeparatorChar);
        Directory.CreateDirectory(Path.Combine(libDir, buildsDir));

        void MarkFolders(InstallationManifest manifest)
        {
            foreach (var dir in new[] { Rel(buildsDir), Rel(Path.GetDirectoryName(buildsDir)!) })
                if (!manifest.InstalledDirectories.Contains(dir, StringComparer.OrdinalIgnoreCase))
                    manifest.InstalledDirectories.Add(dir);
        }

        status?.Report($"Installing {modules.Count} FSR build(s) behind the omniloader…");
        for (var i = 0; i < modules.Count; i++)
            _install.InstallTrackedFile(game, modules[i].Path, Rel(Path.Combine(buildsDir, names[i])), gameDir,
                MarkFolders, logTag: "Omniloader");


        var temp = Path.Combine(Path.GetTempPath(), "osm_omni_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            // It reads the list named after its own file, and OptiScaler can be pointed
            // at an upscaler under another name.
            var ini = Path.Combine(temp, "omni.ini");
            File.WriteAllText(ini, Omniloader.IniFor(modules.Select((m, i) => (names[i], m.Label)).ToList()));
            _install.InstallTrackedFile(game, ini,
                Rel(Path.GetFileNameWithoutExtension(target) + ".omni.ini"), gameDir, logTag: "Omniloader");

            var loader = Path.Combine(temp, "omniloader.dll");
            Omniloader.ExtractTo(loader);
            _install.InstallTrackedFile(game, loader, Rel(Path.GetFileName(target)), gameDir, logTag: "Omniloader");
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
        Log.Write($"[Omniloader] Installed at {target} with {string.Join(", ", modules.Select(m => m.Label))}");
    }

    /// <summary>
    /// Places the chosen AMD driver files next to OptiScaler, where it loads them before the
    /// driver's own (and, on Linux, where it finds them at all). Tracked, so Remove takes them.
    /// </summary>
    /// <param name="placedByBackend">The file name the chosen FSR build put next to OptiScaler, if any.</param>
    private void PlaceDriverFiles(Game game, string gameDir, Fsr4Options fsr4, IProgress<string>? status,
        string? placedByBackend = null)
    {
        var lib = GameInstallationService.OptiScalerLibraryDirectory(gameDir);
        foreach (var file in fsr4.DriverFiles)
        {
            var target = Path.Combine(lib, file.FileName);
            // The driver file goes last and wins: it is the one picked for this purpose.
            if (string.Equals(placedByBackend, file.FileName, StringComparison.OrdinalIgnoreCase))
                LastInstallNotes = LastInstallNotes
                    .Append($"Your AMD driver {file.FileName} {file.Version} replaced the INT8 build of the same name.")
                    .ToList();

            status?.Report($"Installing AMD driver {file.FileName} {file.Version}…");
            _install.InstallTrackedFile(game, file.Path, Path.GetRelativePath(gameDir, target), gameDir, logTag: "AmdDriver");
            Log.Write($"[AmdDriver] Installed {file.FileName} {file.Version} at {target}");
        }
    }

    // ── FSR 4 without OptiScaler ────────────────────────────────────────────
    /// <summary>What a game's page needs to know to offer FSR 4 without OptiScaler.</summary>
    /// <param name="GameFiles">The FidelityFX files in the game; empty while a set is in place.</param>
    /// <param name="Active">The set this app wrote, or null.</param>
    /// <param name="Standing">Whether that set is still what is in the game.</param>
    public sealed record Fsr4SwapOverview(
        IReadOnlyList<FidelityFxFile> GameFiles, Fsr4SwapManifest? Active, DllSwapService.SwapStanding Standing,
        bool OptiScalerInstalled)
    {
        /// <summary>True when the game ships something FSR 4 can replace and nothing stands in the way of showing it.</summary>
        public bool Offered => Active is null && !OptiScalerInstalled && Fsr4SwapPlanner.Targets(GameFiles).Count > 0;
    }

    /// <summary>
    /// Reads the game for the page: drops a record of files that are gone, then looks at
    /// what the game ships. Off the UI thread, because it opens every DLL in the folder.
    /// </summary>
    public Task<Fsr4SwapOverview> Fsr4SwapOverviewAsync(Game game) => Task.Run(() =>
    {
        _fsr4.ForgetStale(game);
        var active = _fsr4.Current(game);
        var optiScaler = _fsr4.OptiScalerInstalled(game);
        IReadOnlyList<FidelityFxFile> files = active is null && !optiScaler
            ? Fsr4SwapService.Scan(game)
            : Array.Empty<FidelityFxFile>();
        return new Fsr4SwapOverview(files, active, Fsr4SwapService.StandingOf(active), optiScaler);
    });

    /// <summary>True when this app has FSR 4 files in the game, so OptiScaler should not be installed over them.</summary>
    public bool HasFsr4Swap(Game game) => _fsr4.IsActive(game);

    /// <summary>What stands in the way of setting up FSR 4 now, or null.</summary>
    public string? Fsr4SwapRefusal(Game game) => _fsr4.Refusal(game);

    /// <summary>
    /// The FSR 4 builds that can go straight into a game: every source the omniloader
    /// lists, less the driver-style builds, which need OptiScaler to load them.
    /// </summary>
    public async Task<IReadOnlyList<OmniBuildOption>> Fsr4SwapBuildOptionsAsync()
    {
        var options = await OmniBuildOptionsAsync();
        return options
            .Where(o => OmniSourceIfCached(o) is not { } cached || Omniloader.Classify(cached) == OmniBuildKind.Module)
            .ToList();
    }

    /// <summary>The files a build would write, or why it cannot be used.</summary>
    public sealed record Fsr4Preview(Fsr4Plan? Plan, Fsr4Sources? Sources, string? Problem);

    /// <summary>
    /// What setting up FSR 4 with this build would do to the game. Nothing is written.
    /// With <paramref name="fetchBuilds"/> off, a build that is not downloaded yet is
    /// planned by its name alone. The OptiScaler release is fetched either way when its
    /// files are needed, because which route a game gets depends on whether it has a loader.
    /// </summary>
    public async Task<Fsr4Preview> PreviewFsr4SwapAsync(
        Game game, OmniBuildOption build, IReadOnlyList<FidelityFxFile> gameFiles, bool fetchBuilds,
        IProgress<string>? status = null)
    {
        var (sources, problem) = await ResolveFsr4SourcesAsync(gameFiles, build, fetchBuilds, status);
        if (sources is null) return new Fsr4Preview(null, null, problem);
        return new Fsr4Preview(_fsr4.Plan(game, sources, gameFiles), sources, null);
    }

    /// <summary>Downloads what the build needs, then writes the set into the game, backing up what it replaces.</summary>
    public async Task ApplyFsr4SwapAsync(Game game, OmniBuildOption build, IProgress<string>? status = null)
    {
        if (_fsr4.Refusal(game) is { } refused) throw new InvalidOperationException(refused);

        var files = await Task.Run(() => Fsr4SwapService.Scan(game));
        var preview = await PreviewFsr4SwapAsync(game, build, files, fetchBuilds: true, status);
        if (preview.Problem is { } problem) throw new InvalidOperationException(problem);

        status?.Report("Writing the files…");
        await Task.Run(() => _fsr4.Apply(game, preview.Plan!, preview.Sources!));
        RefreshGameAnalysis(game);
    }

    /// <summary>Puts the game's own files back and removes the ones the set added.</summary>
    public void UndoFsr4Swap(Game game, bool force = false)
    {
        _fsr4.Revert(game, force);
        RefreshGameAnalysis(game);
    }

    /// <summary>
    /// Undoes what is still ours when the game has replaced part of the set since, and
    /// clears the record. Returns the files left alone.
    /// </summary>
    public IReadOnlyList<string> ForgetFsr4Swap(Game game)
    {
        var left = _fsr4.RevertWhatRemains(game);
        RefreshGameAnalysis(game);
        return left;
    }

    /// <summary>
    /// The newest OptiScaler release already on disk and complete: the latest stable one
    /// if it is there, otherwise the newest stable, otherwise anything.
    /// </summary>
    private string? CachedOptiScalerRelease()
    {
        var held = _components.GetDownloadedOptiScalerVersions()
            .Where(_components.IsOptiScalerCacheComplete)
            .ToList();
        var latest = _components.LatestStableVersion;
        return held.FirstOrDefault(v => string.Equals(v, latest, StringComparison.OrdinalIgnoreCase))
               ?? held.FirstOrDefault(v => !IsBetaOptiScalerVersion(v))
               ?? held.FirstOrDefault();
    }

    private async Task<string> FetchOptiScalerReleaseAsync(IProgress<string>? status)
    {
        if (_components.LatestStableVersion is null)
        {
            try { await _components.CheckForUpdatesAsync(); } catch { /* offline: say so below */ }
        }
        var version = _components.LatestStableVersion
                      ?? throw new InvalidOperationException(
                          "No OptiScaler release is available and none is downloaded. Connect to the internet and try again.");
        status?.Report($"Downloading OptiScaler {version}…");
        await _components.DownloadOptiScalerAsync(version);
        return version;
    }

    /// <summary>
    /// Finds the upscaler a build is, and the loader and frame generation module to go
    /// with it for a game on the SDK 1 runtime. A problem is returned, not thrown: it is
    /// the page's answer to "can I use this".
    /// </summary>
    private async Task<(Fsr4Sources? Sources, string? Problem)> ResolveFsr4SourcesAsync(
        IReadOnlyList<FidelityFxFile> gameFiles, OmniBuildOption build, bool fetchBuilds, IProgress<string>? status)
    {
        var (source, value) = SplitOmniId(build.Id);
        var needsLoader = gameFiles.Any(f => f.Role == FidelityFxRole.Monolith);

        // The release is read for its upscaler, and for the loader and frame generation
        // module an SDK 1 game needs. It is the same download an install makes.
        string? fetchProblem = null;
        var release = CachedOptiScalerRelease();
        if (release is null && (source == "optiscaler" || needsLoader))
        {
            try { release = await FetchOptiScalerReleaseAsync(status); }
            catch (Exception ex) { fetchProblem = ex.Message; }
        }
        var releaseFiles = release is null
            ? Array.Empty<FidelityFxFile>()
            : await Task.Run(() => FidelityFxFiles.Scan(_components.GetOptiScalerCachePath(release))).ConfigureAwait(false);

        string? upscalerPath;
        switch (source)
        {
            case "optiscaler":
                upscalerPath = releaseFiles.FirstOrDefault(f => f.Role == FidelityFxRole.Upscaler)?.FullPath;
                if (upscalerPath is null)
                    return (null, fetchProblem
                                  ?? (release is null
                                      ? "No OptiScaler release is downloaded yet. Pick another build, or install OptiScaler once."
                                      : $"OptiScaler {release} ships no FSR upscaler."));
                break;

            case "faster":
                try
                {
                    upscalerPath = fetchBuilds
                        ? await PrepareFasterFsr4Async(release is null ? "" : _components.GetOptiScalerCachePath(release),
                            value == "lossy" ? FasterFsr4Kind.Lossy : FasterFsr4Kind.Exact, status, replacesBundled: false)
                        : "";
                }
                catch (Exception ex) { return (null, ex.Message); }
                break;

            case "extras":
                upscalerPath = OmniSourceIfCached(build);
                if (upscalerPath is null || !File.Exists(upscalerPath))
                {
                    if (!fetchBuilds) { upscalerPath = ""; break; }
                    try { upscalerPath = await DownloadExtrasForOmniAsync(value, status); }
                    catch (Exception ex) { return (null, ex.Message); }
                }
                break;

            case "custom":
                upscalerPath = OmniSourceIfCached(build);
                break;

            default:
                return (null, $"Unknown build: {build.Label}");
        }

        string? version = null;
        if (!string.IsNullOrEmpty(upscalerPath))
        {
            if (!File.Exists(upscalerPath)) return (null, "That build's file is missing.");
            var (upscaler, reason) = Fsr4SwapService.ReadUpscaler(upscalerPath);
            if (upscaler is null) return (null, $"{build.Label} cannot be used here: {reason}.");
            version = upscaler.Version;
        }

        FidelityFxFile? loader = null, frames = null;
        if (needsLoader)
        {
            var custom = await Task.Run(() => FidelityFxFiles.Scan(_components.GetCustomDllsPath())).ConfigureAwait(false);
            // The set the upscaler came from first: a loader and modules of one release belong together.
            var origins = source == "custom"
                ? new[] { custom, releaseFiles }
                : new[] { releaseFiles, custom };
            foreach (var origin in origins)
            {
                (loader, frames) = Fsr4SwapService.FindCompanions(origin);
                if (loader is not null) break;
            }
        }

        return (new Fsr4Sources(upscalerPath ?? "", build.Label, version, loader, frames), null);
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
