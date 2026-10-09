// Upscaler Manager - UI harness
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using Avalonia.Controls;
using Avalonia.VisualTree;
using UpscalerManager.App.Views.Pages;

namespace UpscalerManager.UiHarness;

internal sealed partial class Harness
{
    /// <summary>The details page and the install screen, for an ordinary game and an anti-cheat one.</summary>
    private async Task InstallPageAsync()
    {
        Console.WriteLine("--- Details and install ---");

        await OpenInstallFor("Hunt: Showdown 1896");
        Check("an anti-cheat game is warned about before anything else",
            Shown(Named<Border>("AntiCheatBox")));
        Check("  naming which anti-cheat",
            Named<TextBlock>("AntiCheatText").Text?.Contains("Easy Anti-Cheat") == true,
            Named<TextBlock>("AntiCheatText").Text ?? "");
        await BackAsync();
        await BackAsync();

        await OpenDetailsFor("Stellar Blade");
        Shoot("details.png");
        Click(Named<Button>("InstallButton"));
        await Until(() => Page is InstallPage, "the install page");
        await Settle(600);
        Check("an ordinary game shows no anti-cheat warning", !Shown(Named<Border>("AntiCheatBox")));
        Shoot("install.png");

        await BackAsync();
        await BackAsync();
    }

    private async Task OpenDetailsFor(string game)
    {
        Click(Card(game));
        await Until(() => Page is GameDetailsPage, $"the details page for {game}");
        await Settle();
    }

    private async Task OpenInstallFor(string game)
    {
        await OpenDetailsFor(game);
        Click(Named<Button>("InstallButton"));
        await Until(() => Page is InstallPage, $"the install page for {game}");
        await Settle(600);
    }
}
