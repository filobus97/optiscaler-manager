// Upscaler Manager - UI harness
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace UpscalerManager.UiHarness;

internal sealed partial class Harness
{
    private string FilesPreview() => string.Join(" | ", Named<StackPanel>("FilesList").Children
        .OfType<TextBlock>().Select(t => t.Text));

    private async Task SetSourceAsync(string checkBox, bool on)
    {
        await OpenSettingsAsync();
        Named<CheckBox>(checkBox).IsChecked = on;
        await Settle();
        await BackAsync();
    }

    /// <summary>The faster FSR 4.1.1 builds on an RX 7000: hidden until their source is on, then offered with the right build.</summary>
    private async Task FasterFsr4Async()
    {
        Console.WriteLine("--- Faster FSR 4.1.1 ---");

        await OpenInstallFor("Stellar Blade");
        Check("hidden while its source is off", !Named<RadioButton>("RbFaster").IsVisible);
        await BackAsync();
        await BackAsync();

        await SetSourceAsync("ChkFsr4ShaderOverrides", true);
        await OpenInstallFor("Stellar Blade");
        var faster = Named<RadioButton>("RbFaster");
        Check("offered once the source is on", faster.IsVisible && faster.IsEnabled);

        Named<ToggleButton>("AdvancedToggle").IsChecked = true;
        faster.IsChecked = true;
        await Settle();
        var note = Named<TextBlock>("FasterNoteText");
        Check("  picking it shows the build for this card",
            note.Text?.Contains("RX 7000 (RDNA 3)") == true, note.Text ?? "");
        Check("  and how to see it is the one running",
            note.Text?.Contains("4.1.1-r3-cyboman") == true, note.Text ?? "");
        Check("  the preview says it replaces OptiScaler's upscaler",
            FilesPreview().Contains("faster FSR 4.1.1, exact — replaces OptiScaler's"), FilesPreview());

        Named<ComboBox>("FasterKindCombo").SelectedIndex = 1;
        await Settle();
        Check("  lossy says it changes the image",
            note.Text?.Contains("Changes the image") == true && note.Text.Contains("4.1.1-r3-lossy-cyboman"), note.Text ?? "");
        Check("  and the preview follows", FilesPreview().Contains("lossy"), FilesPreview());

        Named<ComboBox>("FasterKindCombo").SelectedIndex = 0;
        faster.BringIntoView();
        await Settle(400);
        Shoot("install-faster-fsr4.png");

        Named<RadioButton>("RbDefault").IsChecked = true;
        await Settle();
        await BackAsync();
        await BackAsync();
        await SetSourceAsync("ChkFsr4ShaderOverrides", false);
    }

    /// <summary>The same option on an RX 9000: present, and refused in place with the reason.</summary>
    private async Task Rdna4Async()
    {
        Console.WriteLine("--- Faster FSR 4.1.1 on an RX 9000 ---");
        await SetSourceAsync("ChkFsr4ShaderOverrides", true);
        await OpenInstallFor("Stellar Blade");
        var faster = Named<RadioButton>("RbFaster");
        Check("refused on RX 9000", faster.IsVisible && !faster.IsEnabled);
        Check("  saying so where the option is", faster.Content as string ?? "", "Faster FSR 4.1.1 — not for RX 9000");
        await BackAsync();
        await BackAsync();
    }
}
