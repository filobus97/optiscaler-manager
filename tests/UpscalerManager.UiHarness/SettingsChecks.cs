// Upscaler Manager - UI harness
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using Avalonia.Controls;
using UpscalerManager.App.Views.Pages;

namespace UpscalerManager.UiHarness;

internal sealed partial class Harness
{
    /// <summary>Settings: the Sources list, what switching one off does, and the Storage page.</summary>
    private async Task SettingsAsync()
    {
        Console.WriteLine("--- Settings ---");
        await OpenSettingsAsync();

        string Name(string control) => Named<CheckBox>(control).Content as string ?? "";
        Check("the sources are listed by name",
            string.Join(" | ", Name("ChkRepositoryDownloads"), Name("ChkOptiScalerExtras"), Name("ChkFsr4ShaderOverrides")),
            "DLSS Swapper archive | OptiScaler Client extras | FSR 4 shader overrides");
        Check("  the two that always were on stay on",
            Named<CheckBox>("ChkRepositoryDownloads").IsChecked == true && Named<CheckBox>("ChkOptiScalerExtras").IsChecked == true);
        Check("  the modified FSR 4 builds start off", Named<CheckBox>("ChkFsr4ShaderOverrides").IsChecked == false);
        Shoot("settings.png");

        // A source switched off is not offered at all.
        Named<CheckBox>("ChkOptiScalerExtras").IsChecked = false;
        await Settle();
        await BackAsync();
        await OpenInstallFor("Stellar Blade");
        Check("switching OptiScaler Client extras off removes its INT8 builds",
            !Named<RadioButton>("RbInt8").IsVisible);
        await BackAsync();
        await BackAsync();

        await OpenSettingsAsync();
        Named<CheckBox>("ChkOptiScalerExtras").IsChecked = true;
        await Settle();
        await BackAsync();
        await OpenInstallFor("Stellar Blade");
        Check("  and switching it back brings them back", Named<RadioButton>("RbInt8").IsVisible);
        await BackAsync();
        await BackAsync();

        await OpenSettingsAsync();
        Click(Named<Button>("StorageButton"));
        await Until(() => Page is not SettingsPage, "the storage page");
        await Settle(600);
        Shoot("storage.png");
        await BackAsync();
        await BackAsync();
    }

    private async Task OpenSettingsAsync()
    {
        Click(Named<Button>("SettingsButton"));
        await Until(() => Page is SettingsPage, "the settings page");
        await Settle(600);
    }
}
