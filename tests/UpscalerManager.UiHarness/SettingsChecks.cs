// Upscaler Manager - UI harness
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using Avalonia.Controls;
using UpscalerManager.App.Views.Pages;

namespace UpscalerManager.UiHarness;

internal sealed partial class Harness
{
    /// <summary>Settings, and the Storage page behind it.</summary>
    private async Task SettingsAsync()
    {
        Console.WriteLine("--- Settings ---");
        Click(Named<Button>("SettingsButton"));
        await Until(() => Page is SettingsPage, "the settings page");
        await Settle(600);
        Shoot("settings.png");

        Click(Named<Button>("StorageButton"));
        await Until(() => Page is not SettingsPage, "the storage page");
        await Settle(600);
        Shoot("storage.png");
        await BackAsync();
        await BackAsync();
    }
}
