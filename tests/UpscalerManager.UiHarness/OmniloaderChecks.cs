// Upscaler Manager - UI harness
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using UpscalerManager.App.Services;
using UpscalerManager.Core.Components;
using UpscalerManager.Core.Models;
using UpscalerManager.Core.Services;
using UpscalerManager.Core.Tests;

namespace UpscalerManager.UiHarness;

internal sealed partial class Harness
{
    private const string HarnessRelease = "0.9.5-harness";

    private static readonly string[] Ffx =
        { "ffxConfigure", "ffxCreateContext", "ffxDestroyContext", "ffxDispatch", "ffxQuery" };

    /// <summary>
    /// Several FSR builds behind the omniloader, end to end: the option, the builds on
    /// offer, the preview, a real install into a sample game from a stand-in OptiScaler
    /// release, and Remove taking it all away again.
    /// </summary>
    private async Task OmniloaderAsync()
    {
        Console.WriteLine("--- Several FSR builds (omniloader) ---");
        if (!Omniloader.IsAvailable)
        {
            Fail("the omniloader is in this build", "build native/omniloader first (./build.sh)");
            return;
        }

        // A stand-in OptiScaler release, so the install needs no download, and two imported
        // DLLs: an ordinary upscaler and a driver-style build.
        var release = Path.Combine(AppDataPaths.Cache, "OptiScaler", HarnessRelease);
        Directory.CreateDirectory(release);
        File.WriteAllBytes(Path.Combine(release, "OptiScaler.dll"), PeTestData.BuildPe(fileVersion: "0.9.5.0"));
        File.WriteAllText(Path.Combine(release, "OptiScaler.ini"), "[Upscalers]\nDx12Upscaler=auto\n[FSR]\nUpscalerIndex=auto\n");
        File.WriteAllBytes(Path.Combine(release, Omniloader.UpscalerDllName), PeTestData.BuildPeExporting(Ffx));

        var imports = Path.Combine(Path.GetTempPath(), "um-harness-imports-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(imports);
        File.WriteAllBytes(Path.Combine(imports, Omniloader.UpscalerDllName), PeTestData.BuildPeExporting(Ffx));
        File.WriteAllBytes(Path.Combine(imports, "amdxcffx64.dll"), PeTestData.BuildPeExporting("UpdateFfxApiProvider"));
        await _manager.ImportCustomDllsAsync(new[]
        {
            Path.Combine(imports, Omniloader.UpscalerDllName),
            Path.Combine(imports, "amdxcffx64.dll"),
        });

        await OpenInstallFor("Hades II");
        var omni = Named<RadioButton>("RbOmni");
        Check("offered", omni.IsVisible && omni.IsEnabled);
        Named<ToggleButton>("AdvancedToggle").IsChecked = true;
        omni.IsChecked = true;
        await Until(() => Named<StackPanel>("OmniList").Children.OfType<CheckBox>().Any(), "the builds on offer");

        var boxes = Named<StackPanel>("OmniList").Children.OfType<CheckBox>().ToList();
        string Labels() => string.Join(" | ", boxes.Select(b => b.Content as string));
        Check("  every build on offer is listed, the release's own ticked",
            boxes.Count == 3 && boxes[0].IsChecked == true && boxes.Skip(1).All(b => b.IsChecked != true),
            Labels());
        Check("  imported upscalers and driver-style builds are both offered",
            Labels().Contains("Your amd_fidelityfx_upscaler_dx12.dll") && Labels().Contains("Your amdxcffx64.dll"), Labels());

        foreach (var box in boxes) box.IsChecked = true;
        await Settle();
        Check("  the preview names the omniloader and its list",
            FilesPreview().Contains("(the omniloader") && FilesPreview().Contains(Omniloader.IniName), FilesPreview());
        Check("  and where each build goes, the first running first",
            FilesPreview().Contains("omni/upscaler/amd_fidelityfx_upscaler_dx12_optiscaler-s-own-fsr.dll  (OptiScaler's own FSR — runs first)")
                && FilesPreview().Contains("omni/upscaler/amd_fidelityfx_upscaler_dx12_your-amd-fidelityfx-upscaler-dx12.dll"),
            FilesPreview());
        Check("  a driver-style build goes in by its own name",
            FilesPreview().Contains("amdxcffx64.dll  (Your amdxcffx64.dll — driver-style"), FilesPreview());
        Check("  the FSR version is left to the overlay",
            Named<TextBlock>("FfxVersionNoteText").Text?.Contains("picked in OptiScaler's overlay") == true,
            Named<TextBlock>("FfxVersionNoteText").Text ?? "");
        omni.BringIntoView();
        await Settle(400);
        Shoot("install-omniloader.png");

        // Plus one that cannot be had, as an upstream release that changed would be.
        var picked = boxes.Select(b => (ManagerService.OmniBuildOption)b.Tag!)
            .Append(new ManagerService.OmniBuildOption("custom:gone.dll", "Your gone.dll"))
            .ToList();
        await BackAsync();
        await BackAsync();

        // The install itself, as Confirm runs it, against the stand-in release.
        var game = _manager.ScannedGames.First(g => g.Name == "Hades II");
        try
        {
            await _manager.InstallAsync(game, Fsr4Backend.Omniloader, null, UpscalerSelection.NewestFsr, null,
                optiscalerVersion: HarnessRelease, omniBuilds: picked);
        }
        catch (Exception ex)
        {
            Fail("installing with the omniloader", ex.Message);
            return;
        }

        Check("a build that cannot be had is skipped, and said so, while the rest install",
            _manager.LastInstallNotes.Count == 1 && _manager.LastInstallNotes[0].StartsWith("Skipped Your gone.dll"),
            string.Join(" | ", _manager.LastInstallNotes));

        var dir = _manager.GetInstalledDirectory(game);
        var lib = GameInstallationService.OptiScalerLibraryDirectory(dir);
        var upscaler = Path.Combine(lib, Omniloader.UpscalerDllName);
        var builds = Path.Combine(lib, "omni", "upscaler");
        Check("the omniloader sits where OptiScaler loads its upscaler",
            PeFileInspector.Inspect(upscaler).FileVersion == "0.1.0.0", PeFileInspector.Inspect(upscaler).FileVersion ?? "none");
        Check("  the builds sit beside it, under their own names",
            File.Exists(Path.Combine(builds, "amd_fidelityfx_upscaler_dx12_optiscaler-s-own-fsr.dll"))
                && File.Exists(Path.Combine(builds, "amd_fidelityfx_upscaler_dx12_your-amd-fidelityfx-upscaler-dx12.dll")),
            string.Join(", ", Directory.Exists(builds) ? Directory.GetFiles(builds).Select(Path.GetFileName) : Array.Empty<string>()));
        var ini = Path.Combine(lib, Omniloader.IniName);
        var text = File.Exists(ini) ? File.ReadAllText(ini) : "";
        Check("  its list has both, the release's own first",
            text.Contains("[Modules]\r\nomni\\upscaler\\amd_fidelityfx_upscaler_dx12_optiscaler-s-own-fsr.dll = OptiScaler's own FSR\r\n"
                + "omni\\upscaler\\amd_fidelityfx_upscaler_dx12_your-amd-fidelityfx-upscaler-dx12.dll = Your amd_fidelityfx_upscaler_dx12.dll\r\n"),
            text);
        Check("  and the driver-style build is installed by its name",
            File.Exists(Path.Combine(dir, "amdxcffx64.dll")) && !text.Contains("amdxcffx64"));

        await _manager.UninstallAsync(game);
        Check("Remove takes the omniloader, its list and every build away",
            !File.Exists(upscaler) && !File.Exists(ini) && !Directory.Exists(Path.Combine(lib, "omni"))
                && !File.Exists(Path.Combine(dir, "amdxcffx64.dll")),
            string.Join(", ", Directory.GetFileSystemEntries(dir).Select(Path.GetFileName)));
    }
}
