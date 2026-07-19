// OptiScaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OptiscalerManager.Core.Components;
using OptiscalerManager.Core.Logging;
using OptiscalerManager.Core.Models;
using OptiscalerManager.Core.Prompts;
using OptiscalerManager.Core.Services;

namespace OptiscalerManager.App.Services;

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
    }

    // ── GPU banner ──────────────────────────────────────────────────────────
    public GpuInfo? DetectPrimaryGpu()
    {
        try { return _gpu?.GetDiscreteGPU() ?? _gpu?.GetPrimaryGPU(); }
        catch { return null; }
    }

    // ── Game scan ───────────────────────────────────────────────────────────
    public Task<List<Game>> ScanGamesAsync() => _scanner.ScanAllGamesAsync();

    // ── Bring-your-own inventory ────────────────────────────────────────────
    /// <summary>The user's custom-DLL library (migrated from any legacy imports on first use).</summary>
    public List<CustomDllFileEntry> GetCustomDlls() => _components.GetCustomDlls();

    public bool HasCustomDlls => _components.GetCustomDlls().Count > 0;

    /// <summary>Imports one or more custom DLLs (files, a folder, or an archive).</summary>
    public Task<List<string>> ImportCustomDllsAsync(IEnumerable<string> sources) => _components.ImportCustomDllsAsync(sources);

    public void DeleteCustomDll(string name) => _components.DeleteCustomDll(name);

    /// <summary>Latest FSR 4 INT8 community version known from source, if a check has run.</summary>
    public string? LatestExtrasVersion => _components.LatestExtrasVersion;

    /// <summary>Cached INT8 community build versions (newest first); may be empty until fetched.</summary>
    public IReadOnlyList<string> Int8CommunityVersions => _components.ExtrasAvailableVersions;

    /// <summary>
    /// Ensures the INT8 community version list is populated (best-effort network fetch),
    /// then returns it, falling back to any already-downloaded versions when offline.
    /// </summary>
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
        _ => true, // Default, LatestAmdSdk and Int8Community need no prior import
    };

    // ── Menu / overlay shortcut key (global, persisted) ─────────────────────
    /// <summary>The configured overlay key (VK hex string, e.g. "0x78"), or null for OptiScaler's default.</summary>
    public string? MenuShortcutKey
    {
        get => _components.Config.MenuShortcutKey;
        set { _components.Config.MenuShortcutKey = value; _components.SaveConfiguration(); }
    }

    // ── Add-ons: fakenvapi + Nukem DLSSG-to-FSR3 ────────────────────────────
    /// <summary>True when Nukem's DLL has been imported into the cache (it cannot be auto-downloaded).</summary>
    public bool IsNukemFgCached => _components.IsNukemFGInstalled;

    /// <summary>The cached Nukem DLL's version tag, if known.</summary>
    public string? NukemFgVersion => _components.NukemFGVersion;

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
        bool spoofNvidia = false, bool forceInt8 = false, bool fsr4Watermark = false)
        => ComponentRegistry.BuildInstallPreview(backend, selectFsr4, ComponentRegistry.DefaultInjectionDll, MenuShortcutKey,
            backend == Fsr4Backend.CustomMerged ? _components.GetCustomDlls().Select(d => d.Name).ToList() : null,
            addFakenvapi, addNukemFg, spoofNvidia, forceInt8, fsr4Watermark);

    // ── Install OptiScaler ──────────────────────────────────────────────────
    public async Task InstallAsync(Game game, Fsr4Backend backend, string? int8Version, bool selectFsr4,
        OptiScalerProfile? iniProfile, IProgress<string>? status = null,
        bool addFakenvapi = false, bool addNukemFg = false,
        bool spoofNvidia = false, bool forceInt8 = false, bool fsr4Watermark = false)
    {
        if (!IsBackendAvailable(backend))
            throw new InvalidOperationException($"The selected FSR 4 backend ({backend}) is not available. Import it in Settings first.");
        if (addNukemFg && !IsNukemFgCached)
            throw new InvalidOperationException("Nukem's DLSSG-to-FSR3 DLL has not been imported. Add it in Settings first.");

        status?.Report("Checking for the latest OptiScaler release…");
        try { await _components.CheckForUpdatesAsync(); } catch { /* offline: fall back to cache */ }

        var version = _components.LatestStableVersion
                      ?? _components.GetDownloadedOptiScalerVersions().FirstOrDefault()
                      ?? throw new InvalidOperationException(
                          "No OptiScaler release is available and none is cached. Connect to the internet and try again.");

        var cachePath = _components.GetOptiScalerCachePath(version);
        if (!Directory.Exists(cachePath) || Directory.GetFiles(cachePath, "*", SearchOption.AllDirectories).Length == 0)
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
                await InstallCustomMergedAsync(game, status);
                break;
            }
            case Fsr4Backend.LatestAmdSdk:
            {
                await InstallAmdSdkAsync(game, status);
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

        // Force ONLY the keys the Manager owns; everything else in the chosen ini
        // (default or custom) is left untouched. FSR 4 is always made *available*;
        // whether it is *selected* depends on selectFsr4.
        ApplyForcedIniKeys(gameDir, selectFsr4, spoofNvidia, forceInt8, fsr4Watermark);

        status?.Report("Done.");
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
    /// [FSR] Fsr4Update=true always, [FSR] UpscalerIndex = 0 (Manager selects FSR 4) or
    /// auto (user selects in-game), the opt-in [FSR] Fsr4ForceEnableInt8 /
    /// Fsr4EnableWatermark and [Spoofing] Dxgi toggles, and [Menu] ShortcutKey when a
    /// menu key is configured. Applied last, so it overrides anything the backend
    /// installers set. Off toggles leave the keys untouched (OptiScaler's auto behaviour).
    /// </summary>
    private void ApplyForcedIniKeys(string gameDir, bool selectFsr4, bool spoofNvidia = false,
        bool forceInt8 = false, bool fsr4Watermark = false)
    {
        GameInstallationService.ModifyOptiScalerIniKey(gameDir, "FSR", "Fsr4Update", "true");
        GameInstallationService.ModifyOptiScalerIniKey(gameDir, "FSR", "UpscalerIndex", selectFsr4 ? "0" : "auto");

        if (forceInt8)
            GameInstallationService.ModifyOptiScalerIniKey(gameDir, "FSR", "Fsr4ForceEnableInt8", "true");
        if (fsr4Watermark)
            GameInstallationService.ModifyOptiScalerIniKey(gameDir, "FSR", "Fsr4EnableWatermark", "true");
        if (spoofNvidia)
            GameInstallationService.ModifyOptiScalerIniKey(gameDir, "Spoofing", "Dxgi", "true");

        var menuKey = _components.Config.MenuShortcutKey;
        if (!string.IsNullOrWhiteSpace(menuKey))
            GameInstallationService.ModifyOptiScalerIniKey(gameDir, "Menu", "ShortcutKey", menuKey!);
    }

    /// <summary>
    /// Downloads AMD's signed prebuilt FFX DLL set (signedbin) from the official
    /// FidelityFX-SDK repository — the same artifacts OptiScaler bundles, at AMD's
    /// newest revision — then imports it as an SDK package and swaps it in place.
    /// </summary>
    private async Task InstallAmdSdkAsync(Game game, IProgress<string>? status)
    {
        status?.Report("Downloading AMD's signed FSR DLLs (FidelityFX-SDK signedbin)…");
        var (version, dirPath) = await _components.DownloadFidelityFxSignedBinAsync();

        status?.Report($"Preparing FSR SDK {version}…");
        var scan = await _components.ScanFsrSdkSourceAsync(dirPath);
        try
        {
            if (!scan.HasUpscaler)
                throw new InvalidOperationException(
                    "The downloaded FidelityFX SDK did not contain amd_fidelityfx_upscaler_dx12.dll.");

            var info = await _components.ImportCustomFsrSdkPackageAsync(scan);
            status?.Report($"Installing FSR SDK {version} ({info.Files.Count} DLL(s))…");
            _install.InstallCustomFsrSdk(game, _components.GetCustomFsrSdkCachePath(info.VersionLabel), info.VersionLabel);
        }
        finally
        {
            scan.Cleanup();
        }
    }

    /// <summary>
    /// The unified custom overlay: latest AMD signedbin as the base, with the user's
    /// imported custom DLLs merged on top — same-name entries overwrite the AMD file,
    /// unknown names (e.g. amdxcffx64.dll) are added alongside (manifest-tracked, so
    /// Revert removes them). Falls back to a cached signedbin when offline; installs
    /// the custom overlay alone if nothing is cached.
    /// </summary>
    private async Task InstallCustomMergedAsync(Game game, IProgress<string>? status)
    {
        var customs = _components.GetCustomDlls();
        if (customs.Count == 0)
            throw new InvalidOperationException("No custom DLLs imported. Add them in Settings first.");

        // 1) Base: latest AMD signedbin (best effort — cached or custom-only fallback).
        string? baseDir = null;
        string baseVersion = "custom";
        try
        {
            status?.Report("Downloading AMD's signed FSR DLLs (base set)…");
            (baseVersion, baseDir) = await _components.DownloadFidelityFxSignedBinAsync();
        }
        catch (Exception ex)
        {
            Log.Write($"[CustomMerged] signedbin download failed, checking cache: {ex.Message}");
            var cacheRoot = _components.GetFidelityFxSdkCachePath();
            if (Directory.Exists(cacheRoot))
                baseDir = Directory.GetDirectories(cacheRoot)
                    .Where(d => File.Exists(Path.Combine(d, ComponentManagementService.CustomFsrSdkDllName)))
                    .OrderByDescending(Directory.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            status?.Report(baseDir is null
                ? "AMD base set unavailable (offline?) — installing your custom DLLs only."
                : "Using the cached AMD base set (offline).");
        }

        // 2) Merge dir: base files first, then the custom overlay wins on name clashes.
        var mergeDir = Path.Combine(Path.GetTempPath(), "osm_merge_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mergeDir);
        try
        {
            if (baseDir is not null)
                foreach (var f in Directory.GetFiles(baseDir, "*.dll"))
                    File.Copy(f, Path.Combine(mergeDir, Path.GetFileName(f)), overwrite: true);

            var customNames = new List<string>();
            foreach (var c in customs)
            {
                var src = Path.Combine(_components.GetCustomDllsPath(), c.Name);
                if (!File.Exists(src)) continue;
                File.Copy(src, Path.Combine(mergeDir, c.Name), overwrite: true);
                customNames.Add(c.Name);
            }

            var label = baseDir is not null ? $"{baseVersion}+custom" : "custom";
            status?.Report($"Installing merged FSR set ({label}, {Directory.GetFiles(mergeDir, "*.dll").Length} DLL(s))…");
            _install.InstallCustomFsrSdk(game, mergeDir, label, injectNames: customNames);
        }
        finally
        {
            try { Directory.Delete(mergeDir, true); } catch { }
        }
    }

    /// <summary>
    /// Downloads a community FSR 4 INT8 build from the OptiScaler-Extras repo (at the
    /// chosen version, or latest) and installs its upscaler DLL next to the game exe.
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
        var dest = Path.Combine(gameDir, "amd_fidelityfx_upscaler_dx12.dll");
        File.Copy(extrasDllPath, dest, overwrite: true);
        game.Fsr4ExtraVersion = version;
    }

    // ── Revert support ──────────────────────────────────────────────────────
    /// <summary>True when OptiScaler has a tracked install (backup/manifest) for this game.</summary>
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
