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

    /// <summary>Latest FSR 4 Extras version known from source, if a check has run.</summary>
    public string? LatestExtrasVersion => _components.LatestExtrasVersion;

    /// <summary>Whether a given backend can currently be installed (imports present / etc.).</summary>
    public bool IsBackendAvailable(Fsr4Backend backend) => backend switch
    {
        Fsr4Backend.CustomSdk => HasCustomFsrSdk,
        Fsr4Backend.CustomDll => HasCustomFsr4Dll,
        _ => true, // None and LatestSdkFromSource need no prior import
    };

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
    public InstallPreview BuildInstallPreview(Game game, Fsr4Backend backend, OptiScalerProfile? iniProfile)
        => ComponentRegistry.BuildInstallPreview(backend, ComponentRegistry.DefaultInjectionDll, ProfileToIniKeys(iniProfile));

    // ── Install OptiScaler ──────────────────────────────────────────────────
    public async Task InstallAsync(Game game, Fsr4Backend backend, OptiScalerProfile? iniProfile, IProgress<string>? status = null)
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
                status?.Report($"Installing your custom FSR SDK ({v})…");
                _install.InstallCustomFsrSdk(game, _components.GetCustomFsrSdkCachePath(v), v);
                break;
            }
            case Fsr4Backend.CustomDll:
            {
                var v = _components.GetDownloadedCustomFsr4Versions().First();
                status?.Report($"Installing your custom FSR 4 DLL ({v})…");
                _install.InstallCustomFsr4Dll(game, _components.GetCustomFsr4DllPath(v), v);
                break;
            }
            case Fsr4Backend.LatestSdkFromSource:
            {
                status?.Report("Downloading the latest FSR 4 SDK from source…");
                await InstallExtrasFromSourceAsync(game, gameDir, status);
                break;
            }
            case Fsr4Backend.None:
            default:
                break; // OptiScaler core only.
        }

        status?.Report("Done.");
    }

    /// <summary>
    /// Downloads OptiScaler's Extras FSR 4.x INT8 package and installs its upscaler
    /// DLL next to the game exe, then engages the FSR path in OptiScaler.ini. Mirrors
    /// the source project's Extras injection; Revert removes it via the known-artifact
    /// list in <see cref="GameInstallationService"/>.
    /// </summary>
    private async Task InstallExtrasFromSourceAsync(Game game, string gameDir, IProgress<string>? status)
    {
        var extrasVersion = _components.LatestExtrasVersion
            ?? throw new InvalidOperationException("Could not determine the latest FSR 4 SDK version from source.");

        var extrasDllPath = await _components.DownloadExtrasDllAsync(extrasVersion);
        if (!File.Exists(extrasDllPath))
            throw new FileNotFoundException("The downloaded FSR 4 SDK package is incomplete.", extrasDllPath);

        status?.Report("Installing the FSR 4 SDK…");
        var dest = Path.Combine(gameDir, "amd_fidelityfx_upscaler_dx12.dll");
        File.Copy(extrasDllPath, dest, overwrite: true);
        game.Fsr4ExtraVersion = extrasVersion;

        foreach (var k in ComponentRegistry.Fsr4EnableKeys)
            GameInstallationService.ModifyOptiScalerIniKey(gameDir, k.Section, k.Key, k.Value);
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
        return $"{info.VersionLabel} ({names})";
    }
}
