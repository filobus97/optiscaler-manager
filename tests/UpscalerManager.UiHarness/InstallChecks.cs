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

        // Stable, beta and nightly: each channel lists its own releases under its own latest.
        var versions = Named<ComboBox>("OptiScalerVersionCombo");
        string Items() => string.Join(" | ", versions.Items.OfType<ComboBoxItem>().Select(i => i.Content as string));
        await Until(() => Named<RadioButton>("RbChannelNightly").IsVisible, "the release channels");
        Check("the install screen offers the stable, beta and nightly channels",
            Named<RadioButton>("RbChannelStable").IsChecked == true && Named<RadioButton>("RbChannelBeta").IsVisible
                && Named<RadioButton>("RbChannelNightly").IsVisible);
        Check("  stable lists releases only", Items().StartsWith("Latest stable") && !Items().Contains("nightly"), Items());
        Named<RadioButton>("RbChannelNightly").IsChecked = true;
        await Settle();
        Check("  nightly lists the daily builds by date, the newest first",
            Items().StartsWith("Latest nightly — ") && Items().Contains("24 Oct 2026  (nightly-20261024)")
                && !Items().Contains("0.9.5"), Items());
        Check("  and says what a nightly is",
            Shown(Named<TextBlock>("ChannelNoteText")) && Named<TextBlock>("ChannelNoteText").Text?.Contains("untested") == true);
        Check("  picking one installs that nightly",
            (versions.SelectedItem as ComboBoxItem)?.Tag as string is { } tag && tag.StartsWith("nightly-"),
            (versions.SelectedItem as ComboBoxItem)?.Tag as string ?? "null");
        Named<RadioButton>("RbChannelBeta").IsChecked = true;
        await Settle();
        Check("  beta lists the pre-releases", Items().StartsWith("Latest beta — ") && Items().Contains("0.9.6-pre1"), Items());
        Named<RadioButton>("RbChannelStable").IsChecked = true;
        await Settle();

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
