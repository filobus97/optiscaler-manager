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
/// to this class, which keeps the one-click "Enable FSR 4" flow, the transparent
/// preview, and the bring-your-own imports in one place. All heavy lifting is
/// done by the reused Core services — this adds no install logic of its own.
/// </summary>
public sealed class ManagerService
{
    private readonly ComponentManagementService _components;
    private readonly GameInstallationService _install = new();
    private readonly GameScannerService _scanner = new();
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

    /// <summary>Latest imported custom SDK version label, if any.</summary>
    public string? LatestCustomFsrSdk => _components.GetDownloadedCustomFsrSdkVersions().FirstOrDefault();
    /// <summary>Latest imported custom amdxcffx64.dll version label, if any.</summary>
    public string? LatestCustomFsr4Dll => _components.GetDownloadedCustomFsr4Versions().FirstOrDefault();

    // ── The transparent "what will happen" preview ──────────────────────────
    /// <summary>
    /// Builds the exact preview for one-click Enable FSR 4 on <paramref name="game"/>:
    /// it picks the imported SDK, else the imported amdxcffx64.dll, else OptiScaler's
    /// built-in FSR path. What the preview shows is precisely what gets installed.
    /// </summary>
    public InstallPreview BuildEnableFsr4Preview(Game game)
        => ComponentRegistry.BuildFsr4Preview(HasCustomFsrSdk, HasCustomFsr4Dll && !HasCustomFsrSdk);

    // ── One-click Enable FSR 4 ──────────────────────────────────────────────
    public async Task EnableFsr4Async(Game game, IProgress<string>? status = null)
    {
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
        _install.InstallOptiScaler(game, cachePath, ComponentRegistry.DefaultInjectionDll, optiscalerVersion: version);

        var gameDir = _install.DetermineInstallDirectory(game);
        if (string.IsNullOrEmpty(gameDir))
            throw new InvalidOperationException("OptiScaler installed but the game directory could not be resolved.");

        // Bring-your-own FSR 4 backend takes precedence; it also sets the FSR ini keys.
        if (HasCustomFsrSdk)
        {
            var v = _components.GetDownloadedCustomFsrSdkVersions().First();
            status?.Report($"Installing your custom FSR SDK ({v})…");
            _install.InstallCustomFsrSdk(game, _components.GetCustomFsrSdkCachePath(v), v);
        }
        else if (HasCustomFsr4Dll)
        {
            var v = _components.GetDownloadedCustomFsr4Versions().First();
            status?.Report($"Installing your custom FSR 4 DLL ({v})…");
            _install.InstallCustomFsr4Dll(game, _components.GetCustomFsr4DllPath(v), v);
        }
        else
        {
            // No bring-your-own component: engage OptiScaler's built-in FSR path.
            status?.Report("Enabling FSR 4 in OptiScaler.ini…");
            foreach (var k in ComponentRegistry.Fsr4EnableKeys)
                GameInstallationService.ModifyOptiScalerIniKey(gameDir, k.Section, k.Key, k.Value);
        }

        status?.Report("Done.");
    }

    // ── Uninstall / revert ──────────────────────────────────────────────────
    public Task UninstallAsync(Game game) => Task.Run(() => _install.UninstallOptiScaler(game));

    // ── Bring-your-own imports ──────────────────────────────────────────────
    /// <summary>Imports a user-supplied amdxcffx64.dll from a local file.</summary>
    public async Task<string> ImportCustomFsr4DllAsync(string sourcePath)
    {
        var info = await _components.ImportCustomFsr4DllAsync(sourcePath);
        return info.VersionLabel;
    }

    /// <summary>
    /// Imports a user-supplied FSR SDK from a local archive (.zip/.7z) or an
    /// extracted folder, recursively collecting the DLL set. Returns a short
    /// human-readable summary of what was imported.
    /// </summary>
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
