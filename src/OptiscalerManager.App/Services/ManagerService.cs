// OptiScaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OptiscalerManager.Core.Components;
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
    public bool HasCustomFsrSdk => _components.GetDownloadedCustomFsrSdkVersions().Count > 0;
    public bool HasCustomFsr4Dll => _components.GetDownloadedCustomFsr4Versions().Count > 0;
    public IReadOnlyList<string> CustomFsrSdkVersions => _components.GetDownloadedCustomFsrSdkVersions();
    public IReadOnlyList<string> CustomFsr4DllVersions => _components.GetDownloadedCustomFsr4Versions();
    public string? LatestCustomFsrSdk => _components.GetDownloadedCustomFsrSdkVersions().FirstOrDefault();
    public string? LatestCustomFsr4Dll => _components.GetDownloadedCustomFsr4Versions().FirstOrDefault();

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
        Fsr4Backend.CustomSdk => HasCustomFsrSdk,
        Fsr4Backend.CustomDllPlusAmdSdk => HasCustomFsr4Dll,
        _ => true, // Default, LatestAmdSdk and Int8Community need no prior import
    };

    // ── Menu / overlay shortcut key (global, persisted) ─────────────────────
    /// <summary>The configured overlay key (VK hex string, e.g. "0x78"), or null for OptiScaler's default.</summary>
    public string? MenuShortcutKey
    {
        get => _components.Config.MenuShortcutKey;
        set { _components.Config.MenuShortcutKey = value; _components.SaveConfiguration(); }
    }

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
    public InstallPreview BuildInstallPreview(Game game, Fsr4Backend backend, bool selectFsr4)
        => ComponentRegistry.BuildInstallPreview(backend, selectFsr4, ComponentRegistry.DefaultInjectionDll, MenuShortcutKey);

    // ── Install OptiScaler ──────────────────────────────────────────────────
    public async Task InstallAsync(Game game, Fsr4Backend backend, string? int8Version, bool selectFsr4,
        OptiScalerProfile? iniProfile, IProgress<string>? status = null)
    {
        if (!IsBackendAvailable(backend))
            throw new InvalidOperationException($"The selected FSR 4 backend ({backend}) is not available. Import it in Settings first.");

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

        status?.Report($"Installing OptiScaler {version}…");
        // Pass the chosen ini profile (null → OptiScaler's own default config).
        var profileToApply = (iniProfile is null || iniProfile.IsBuiltIn || iniProfile.IniSettings.Count == 0) ? null : iniProfile;
        _install.InstallOptiScaler(game, cachePath, ComponentRegistry.DefaultInjectionDll,
            optiscalerVersion: version, profile: profileToApply);

        var gameDir = _install.DetermineInstallDirectory(game);
        if (string.IsNullOrEmpty(gameDir))
            throw new InvalidOperationException("OptiScaler installed but the game directory could not be resolved.");

        switch (backend)
        {
            case Fsr4Backend.CustomSdk:
            {
                var v = _components.GetDownloadedCustomFsrSdkVersions().First();
                var warning = UnsignedSdkWarning(_components.GetCustomFsrSdkDllInfo(v));
                if (warning is not null) status?.Report(warning);
                status?.Report($"Installing your custom FSR SDK ({v})…");
                _install.InstallCustomFsrSdk(game, _components.GetCustomFsrSdkCachePath(v), v);
                break;
            }
            case Fsr4Backend.CustomDllPlusAmdSdk:
            {
                // amdxcffx64.dll cannot run alone — install it together with the AMD SDK.
                var v = _components.GetDownloadedCustomFsr4Versions().First();
                status?.Report($"Installing your amdxcffx64.dll ({v})…");
                _install.InstallCustomFsr4Dll(game, _components.GetCustomFsr4DllPath(v), v);
                await InstallAmdSdkAsync(game, status);
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
        ApplyForcedIniKeys(gameDir, selectFsr4);

        status?.Report("Done.");
    }

    /// <summary>
    /// Writes the (and only the) OptiScaler.ini keys the Manager is responsible for:
    /// [FSR] Fsr4Update=true always, [FSR] UpscalerIndex = 0 (Manager selects FSR 4) or
    /// auto (user selects in-game), and [Menu] ShortcutKey when a menu key is configured.
    /// Applied last, so it overrides anything the backend installers set.
    /// </summary>
    private void ApplyForcedIniKeys(string gameDir, bool selectFsr4)
    {
        GameInstallationService.ModifyOptiScalerIniKey(gameDir, "FSR", "Fsr4Update", "true");
        GameInstallationService.ModifyOptiScalerIniKey(gameDir, "FSR", "UpscalerIndex", selectFsr4 ? "0" : "auto");

        var menuKey = _components.Config.MenuShortcutKey;
        if (!string.IsNullOrWhiteSpace(menuKey))
            GameInstallationService.ModifyOptiScalerIniKey(gameDir, "Menu", "ShortcutKey", menuKey!);
    }

    /// <summary>
    /// OptiScaler bundles AMD's *signed* FFX DLLs and the FFX loader verifies
    /// signatures (OptiScaler's WinVerifyTrust hook only whitelists
    /// amd_fidelityfx_dx12/vk) — an unsigned swapped-in upscaler is rejected and the
    /// FSR 4 entry silently disappears. Warn whenever an SDK package's upscaler has
    /// no Authenticode signature.
    /// </summary>
    private static string? UnsignedSdkWarning(CustomFsr4DllInfo? info)
    {
        if (info is null) return null;
        var upscaler = info.Files.FirstOrDefault(f =>
            f.Name.Equals(ComponentManagementService.CustomFsrSdkDllName, StringComparison.OrdinalIgnoreCase));
        var signed = upscaler?.HasAuthenticodeSignature ?? info.HasAuthenticodeSignature;
        if (signed) return null;
        return "Warning: this package's upscaler DLL is NOT signed. OptiScaler's FFX loader verifies signatures, " +
               "so FSR 4 may not appear with it. Use signed DLLs (e.g. from a driver package) — " +
               "OptiScaler's own Default files are already signed and include FSR 4.1.";
    }

    /// <summary>
    /// Downloads AMD's official open-source FidelityFX SDK, extracts its full prebuilt
    /// DLL set (loader + upscaler + frame-gen + denoiser + companions) via the existing
    /// SDK scanner, imports it as an SDK package, and installs it into the game.
    /// </summary>
    private async Task InstallAmdSdkAsync(Game game, IProgress<string>? status)
    {
        status?.Report("Downloading AMD's FidelityFX SDK…");
        var (version, archivePath) = await _components.DownloadFidelityFxSdkArchiveAsync();

        status?.Report("Extracting the FSR SDK DLLs…");
        var scan = await _components.ScanFsrSdkSourceAsync(archivePath);
        try
        {
            if (!scan.HasUpscaler)
                throw new InvalidOperationException(
                    "The downloaded FidelityFX SDK did not contain amd_fidelityfx_upscaler_dx12.dll.");

            var info = await _components.ImportCustomFsrSdkPackageAsync(scan);
            var warning = UnsignedSdkWarning(info);
            if (warning is not null) status?.Report(warning);
            status?.Report($"Installing FSR SDK {version} ({info.Files.Count} DLL(s))…");
            _install.InstallCustomFsrSdk(game, _components.GetCustomFsrSdkCachePath(info.VersionLabel), info.VersionLabel);
        }
        finally
        {
            scan.Cleanup();
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

    // ── Bring-your-own DLL imports ──────────────────────────────────────────
    public async Task<string> ImportCustomFsr4DllAsync(string sourcePath)
    {
        var info = await _components.ImportCustomFsr4DllAsync(sourcePath);
        return info.VersionLabel;
    }

    public async Task<string> ImportCustomFsrSdkAsync(string sourcePath)
    {
        var scan = await _components.ScanFsrSdkSourceAsync(sourcePath);
        if (!scan.HasUpscaler)
            throw new InvalidOperationException(
                "The selected source does not contain a 64-bit amd_fidelityfx_upscaler_dx12.dll (the required upscaler).");

        var info = await _components.ImportCustomFsrSdkPackageAsync(scan);
        var names = string.Join(", ", info.Files.Select(f => f.Name));
        var warning = UnsignedSdkWarning(info);
        return warning is null
            ? $"{info.VersionLabel} ({names})"
            : $"{info.VersionLabel} ({names}). {warning}";
    }
}
