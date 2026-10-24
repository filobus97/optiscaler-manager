// Upscaler Manager - UI harness
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using Avalonia.Controls;
using UpscalerManager.App.Views.Pages;
using UpscalerManager.Core.Components;
using UpscalerManager.Core.Models;
using UpscalerManager.Core.Services;
using UpscalerManager.Core.Tests;

namespace UpscalerManager.UiHarness;

internal sealed partial class Harness
{
    private static string Version(string path) =>
        File.Exists(path) ? PeFileInspector.Inspect(path).FileVersion ?? "none" : "missing";

    /// <summary>
    /// FSR 4 without OptiScaler, end to end on the sample game that ships the SDK 1
    /// runtime: offered, the preview of every file, the route without a loader and its
    /// warning, the write, the refusal of an OptiScaler install over it, and Undo.
    /// </summary>
    private async Task Fsr4SwapAsync()
    {
        Console.WriteLine("--- FSR 4 without OptiScaler ---");

        // The stand-in OptiScaler release ships the SDK 2 set the way the real one does:
        // the loader under the old name, with the upscaler and frame generation beside it.
        var release = Path.Combine(AppDataPaths.Cache, "OptiScaler", HarnessRelease);
        Directory.CreateDirectory(release);
        File.WriteAllBytes(Path.Combine(release, "OptiScaler.dll"), PeTestData.BuildPe(fileVersion: "0.9.5.0"));
        File.WriteAllBytes(Path.Combine(release, "amd_fidelityfx_upscaler_dx12.dll"),
            PeTestData.BuildVersionedPeExporting("4.1.1.2740", Ffx));
        var loader = Path.Combine(release, "amd_fidelityfx_dx12.dll");
        File.WriteAllBytes(loader, PeTestData.BuildVersionedPeExporting("2.3.0.2740", Ffx));
        File.WriteAllBytes(Path.Combine(release, "amd_fidelityfx_framegeneration_dx12.dll"),
            PeTestData.BuildVersionedPeExporting("4.0.1.2740", Ffx));

        var game = _manager.ScannedGames.First(g => g.Name == "Cyberpunk 2077");
        var exeDir = Path.Combine(game.InstallPath, "bin", "x64");
        string Dll(string name) => Path.Combine(exeDir, name);

        // A game with no FidelityFX files is not offered it.
        await OpenDetailsFor("Stellar Blade");
        await Settle(800);
        Check("not offered for a game with no FidelityFX files", !Shown(Named<Border>("Fsr4Card")));
        await BackAsync();

        await OpenDetailsFor("Cyberpunk 2077");
        await Until(() => MaybeNamed<Border>("Fsr4Card") is { IsEffectivelyVisible: true }, "the FSR 4 card");
        Check("offered for a game that ships the SDK 1 runtime", Shown(Named<Border>("Fsr4Card")));
        Check("  with the action", Named<Button>("Fsr4Button").Content as string ?? "", "FSR 4 without OptiScaler…");

        Click(Named<Button>("Fsr4Button"));
        await Until(() => Page is Fsr4SwapPage, "the FSR 4 page");
        await Until(() => MaybeNamed<ComboBox>("BuildCombo")?.ItemCount > 0, "the builds on offer");
        await Until(() => MaybeNamed<StackPanel>("FilesList")?.Children.Count > 0, "the preview");

        var labels = string.Join(" | ", Named<ComboBox>("BuildCombo").Items.OfType<ComboBoxItem>().Select(i => i.Content as string));
        Check("  the builds are the omniloader's, less the driver-style one",
            labels.Contains("OptiScaler's own FSR") && labels.Contains("Your amd_fidelityfx_upscaler_dx12.dll")
                && !labels.Contains("amdxcffx64"), labels);

        Check("  the preview adds the upscaler and the frame generation module",
            FilesPreview().Contains("bin/x64/amd_fidelityfx_upscaler_dx12.dll  ←  FSR 4 upscaler 4.1.1  (new file)")
                && FilesPreview().Contains("bin/x64/amd_fidelityfx_framegeneration_dx12.dll  ←  SDK 2 frame generation 4.0.1  (new file)"),
            FilesPreview());
        Check("  and puts the loader over the old file, saying what it replaces",
            FilesPreview().Contains("bin/x64/amd_fidelityfx_dx12.dll  ←  SDK 2 loader 2.3.0  (replaces 1.0.1.41314)"),
            FilesPreview());
        Check("  with nothing to warn about", !Shown(Named<Border>("NoticeBox")));
        Check("  and the action to confirm it", Named<Button>("ConfirmButton").IsEnabled);
        Shoot("fsr4-swap.png");

        // Without a loader anywhere, the upscaler stands in alone, and the page says what that costs.
        await BackAsync();
        File.Move(loader, loader + ".away");
        try
        {
            Click(Named<Button>("Fsr4Button"));
            await Until(() => Page is Fsr4SwapPage, "the FSR 4 page again");
            await Until(() => MaybeNamed<StackPanel>("FilesList")?.Children.Count > 0, "the preview without a loader");
            await Settle();

            Check("  without a loader the upscaler takes the old file's place",
                FilesPreview() == "bin/x64/amd_fidelityfx_dx12.dll  ←  FSR 4 upscaler 4.1.1  (replaces 1.0.1.41314)",
                FilesPreview());
            var notice = string.Join(" ", Named<StackPanel>("NoticeList").Children.OfType<TextBlock>().Select(t => t.Text));
            Check("  and says the game's frame generation will not work",
                Shown(Named<Border>("NoticeBox")) && notice.Contains("frame generation will not"), notice);
            Shoot("fsr4-swap-no-loader.png");
        }
        finally
        {
            File.Move(loader + ".away", loader);
        }

        // Confirm, with the loader back.
        await BackAsync();
        Click(Named<Button>("Fsr4Button"));
        await Until(() => Page is Fsr4SwapPage && MaybeNamed<Button>("ConfirmButton")?.IsEnabled == true, "the page to be ready");
        Click(Named<Button>("ConfirmButton"));
        await Until(() => Page is GameDetailsPage && (MaybeNamed<Button>("Fsr4Button")?.Content as string) == "Undo",
            "the details page to show the set in place");

        Check("the loader now sits under the old name",
            Version(Dll("amd_fidelityfx_dx12.dll")) == "2.3.0.2740", Version(Dll("amd_fidelityfx_dx12.dll")));
        Check("  with the upscaler and frame generation beside it",
            Version(Dll("amd_fidelityfx_upscaler_dx12.dll")) == "4.1.1.2740"
                && Version(Dll("amd_fidelityfx_framegeneration_dx12.dll")) == "4.0.1.2740");
        Check("  the page shows it in place, with an Undo",
            Named<TextBlock>("Fsr4StateText").Text?.Contains("OptiScaler's own FSR") == true
                && Named<TextBlock>("Fsr4StateText").Text!.Contains("3 files written"),
            Named<TextBlock>("Fsr4StateText").Text ?? "");
        Check("  and nothing to warn about", !Shown(Named<TextBlock>("Fsr4WarningText")));
        Shoot("details-fsr4.png");

        // OptiScaler would write its own FidelityFX files over the set.
        Click(Named<Button>("InstallButton"));
        await Until(() => Page is InstallPage, "the install page");
        await Settle(600);
        Check("OptiScaler cannot be installed over it",
            !Named<Button>("ConfirmButton").IsEnabled && Shown(Named<Border>("ConflictBox")));
        string? refused = null;
        try
        {
            await _manager.InstallAsync(game, Fsr4Backend.Default, null,
                UpscalerSelection.NewestFsr, null, optiscalerVersion: HarnessRelease);
        }
        catch (Exception ex) { refused = ex.Message; }
        Check("  and the install itself refuses", refused?.Contains("FSR 4 without OptiScaler") == true, refused ?? "no error");
        await BackAsync();
        await BackAsync();

        // Undo puts all of it back.
        await OpenDetailsFor("Cyberpunk 2077");
        await Until(() => (MaybeNamed<Button>("Fsr4Button")?.Content as string) == "Undo", "the Undo");
        Click(Named<Button>("Fsr4Button"));
        await Until(() => (MaybeNamed<Button>("Fsr4Button")?.Content as string) == "FSR 4 without OptiScaler…", "the page to offer it again");

        Check("Undo puts the game's own file back",
            Version(Dll("amd_fidelityfx_dx12.dll")) == "1.0.1.41314", Version(Dll("amd_fidelityfx_dx12.dll")));
        Check("  and removes the two files it added",
            !File.Exists(Dll("amd_fidelityfx_upscaler_dx12.dll")) && !File.Exists(Dll("amd_fidelityfx_framegeneration_dx12.dll")));
        await BackAsync();
    }
}
