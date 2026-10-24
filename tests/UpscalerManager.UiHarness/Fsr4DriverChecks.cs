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
    private const string Harness10Release = "10.0.0-harness";

    /// <summary>
    /// AMD's driver files and the FSR 4 keys of OptiScaler 10, end to end: Settings lists what
    /// was imported, the install screen offers it, and an install against a stand-in
    /// OptiScaler 10 release writes the model and force keys under their new names and puts
    /// the driver files next to OptiScaler, where Remove finds them again.
    /// </summary>
    private async Task Fsr4DriverFilesAsync()
    {
        Console.WriteLine("--- AMD driver files and FSR 4 models ---");

        await OpenSettingsAsync();
        Named<ToggleButton>("OwnFilesToggle").IsChecked = true;
        await Settle();
        string DriverList() => string.Join(" | ", Named<StackPanel>("AmdDriverList").Children.OfType<Panel>()
            .SelectMany(p => p.Children.OfType<TextBlock>()).Select(t => t.Text)
            .Concat(Named<StackPanel>("AmdDriverList").Children.OfType<TextBlock>().Select(t => t.Text)));
        Check("Settings says no driver files are imported yet", DriverList() == "None imported.", DriverList());
        await BackAsync();

        // Stand-ins for the two files of an AMD driver: they hand FSR 4 out through
        // UpdateFfxApiProvider and carry a version. The DirectX 12 one also carries a
        // provider table, read for the FSR version it names, after the image where the
        // loader ignores it.
        var unpacked = Path.Combine(Path.GetTempPath(), "um-harness-driver-" + Guid.NewGuid().ToString("N"), "Packages", "Drivers");
        Directory.CreateDirectory(unpacked);
        File.WriteAllBytes(Path.Combine(unpacked, AmdDriverFiles.Dx12Name),
            PeTestData.BuildVersionedPeExporting("2.4.0.1234", AmdDriverFiles.ProviderExport)
                .Concat(System.Text.Encoding.ASCII.GetBytes("\04.1.2\0" + "3.1.5\0")).ToArray());
        File.WriteAllBytes(Path.Combine(unpacked, AmdDriverFiles.VulkanName),
            PeTestData.BuildVersionedPeExporting("2.4.0.1234", AmdDriverFiles.ProviderExport));
        var import = await _manager.ImportAmdDriverFilesAsync(new[] { Path.GetDirectoryName(Path.GetDirectoryName(unpacked))! });
        Check("an unpacked driver folder gives both files", import.Added.Count == 2,
            string.Join(" | ", import.Notes));

        await OpenSettingsAsync();
        Named<ToggleButton>("OwnFilesToggle").IsChecked = true;
        await Settle();
        Check("  and Settings lists them with their version",
            DriverList().Contains("amdxcffx64.dll   2.4.0.1234   DirectX 12") && DriverList().Contains("amdvkffx64.dll   2.4.0.1234   Vulkan"),
            DriverList());
        Named<Button>("ImportAmdDriverButton").BringIntoView();
        await Settle(400);
        Shoot("settings-amd-driver.png");
        await BackAsync();

        await OpenInstallFor("Stellar Blade");
        var driver = Named<RadioButton>("RbAmdDriver");
        Check("the install screen offers them by their FSR version, picked on Linux",
            driver.IsVisible && driver.IsChecked == true, $"{driver.IsVisible} {driver.IsChecked}");
        Check("  named so", driver.Content as string ?? "", "AMD's FSR 4.1.2, from your driver files (recommended)");
        Check("  with no import button once they are in", !Named<StackPanel>("ImportDriverHint").IsVisible);
        Check("  the preview says where they go",
            FilesPreview().Contains("amdxcffx64.dll  (AMD driver 2.4.0.1234, FSR 4 for DirectX 12 — next to OptiScaler)")
                && FilesPreview().Contains("amdvkffx64.dll  (AMD driver 2.4.0.1234, FSR 4 for Vulkan — next to OptiScaler)"),
            FilesPreview());
        Check("  the FSR version list starts at the driver's",
            (Named<ComboBox>("FfxVersionCombo").Items.OfType<ComboBoxItem>().FirstOrDefault()?.Content as string ?? "")
                .Contains("FSR 4.1.2"),
            Named<ComboBox>("FfxVersionCombo").Items.OfType<ComboBoxItem>().FirstOrDefault()?.Content as string ?? "");
        Check("  the model note says which model this GPU runs",
            Named<TextBlock>("Fsr4ModelNoteText").Text?.Contains("INT8 model") == true,
            Named<TextBlock>("Fsr4ModelNoteText").Text ?? "");
        Named<RadioButton>("RbDefault").IsChecked = true;
        await Settle();
        Check("  and leaves them out when OptiScaler's own is picked", !FilesPreview().Contains("AMD driver"), FilesPreview());
        driver.IsChecked = true;
        await Settle();
        driver.BringIntoView();
        await Settle(400);
        Shoot("install-fsr4-driver.png");
        await BackAsync();
        await BackAsync();

        // A stand-in OptiScaler 10 release: its ini documents the renamed keys.
        var release = Path.Combine(AppDataPaths.Cache, "OptiScaler", Harness10Release);
        Directory.CreateDirectory(release);
        File.WriteAllBytes(Path.Combine(release, "OptiScaler.dll"), PeTestData.BuildPe(fileVersion: "10.0.0.0"));
        File.WriteAllText(Path.Combine(release, "OptiScaler.ini"),
            "[Upscalers]\nDx12Upscaler=auto\n[FSR]\nUpscalerIndex=auto\n"
            + "; 0 = No override | 1 = FP8 | 2 = INT8 | 3 = INT8-2 | 4 = INT8 Lite\nFsr4GpuSupport=auto\n"
            + "; 0 = FP8 | 1 = INT8 | 2 = INT8 RDNA2 | 3 = INT8 Lite\nFsr4Model=auto\n");

        var game = _manager.ScannedGames.First(g => g.Name == "Stellar Blade");
        var files = _manager.AmdDriverFilesImported;
        try
        {
            await _manager.InstallAsync(game, Fsr4Backend.Default, null, UpscalerSelection.NewestFsr, null,
                optiscalerVersion: Harness10Release,
                fsr4: new Fsr4Options(Fsr4ModelChoice.Int8Rdna2, Force: false, files));
        }
        catch (Exception ex)
        {
            Fail("installing with driver files and a model", ex.Message);
            return;
        }

        var dir = _manager.GetInstalledDirectory(game);
        var lib = GameInstallationService.OptiScalerLibraryDirectory(dir);
        // Read from the file itself: ReadIniValue reports "auto" as unset.
        var written = File.ReadAllLines(Path.Combine(dir, "OptiScaler.ini")).Select(l => l.Trim()).ToList();
        Check("OptiScaler 10 is asked for the RX 6000 model, and an old force undone",
            written.Contains("Fsr4Model=2") && written.Contains("Fsr4GpuSupport=auto")
                && !written.Any(l => l.StartsWith("Fsr4ForceEnableInt8")),
            string.Join(" ", written.Where(l => l.StartsWith("Fsr4"))));
        Check("  the driver files sit next to OptiScaler",
            File.Exists(Path.Combine(lib, AmdDriverFiles.Dx12Name)) && File.Exists(Path.Combine(lib, AmdDriverFiles.VulkanName)),
            string.Join(", ", Directory.GetFiles(lib).Select(Path.GetFileName)));

        await _manager.UninstallAsync(game);
        Check("Remove takes the driver files away",
            !File.Exists(Path.Combine(lib, AmdDriverFiles.Dx12Name)) && !File.Exists(Path.Combine(lib, AmdDriverFiles.VulkanName)),
            Directory.Exists(lib) ? string.Join(", ", Directory.GetFiles(lib).Select(Path.GetFileName)) : "(folder gone)");

        // The stand-in release is only for this pass: left in the cache, it would be the
        // newest release every later pass picks up.
        Directory.Delete(release, recursive: true);
    }
}
