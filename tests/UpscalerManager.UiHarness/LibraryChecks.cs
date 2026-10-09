// Upscaler Manager - UI harness
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using Avalonia.Controls;
using Avalonia.VisualTree;

namespace UpscalerManager.UiHarness;

internal sealed partial class Harness
{
    /// <summary>The game list: the GPU line, the anti-cheat tag, and the filter that hides those games.</summary>
    private async Task LibraryAsync()
    {
        Console.WriteLine("--- Library ---");
        Check("every sample game has a card", Cards().Count.ToString(), "5");

        var gpuLine = _w.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Text?.Contains("RX 7800 XT") == true)?.Text ?? "";
        Check("the GPU line names the generation", gpuLine.Contains("RX 7000 (RDNA 3)"), gpuLine);

        bool Tagged(string game) => Card(game).GetVisualDescendants().OfType<Border>()
            .Any(b => b.Name == "AntiCheatTag" && Shown(b));
        Check("the anti-cheat game is tagged", Tagged("Hunt: Showdown 1896"));
        Check("  and only that one", !Tagged("Cyberpunk 2077") && !Tagged("Stellar Blade"));

        Shoot("games.png");

        var filter = Named<CheckBox>("ChkHideAntiCheat");
        filter.IsChecked = true;
        await Settle();
        Check("hiding anti-cheat games removes its card", Cards().Count.ToString(), "4");
        Check("  and leaves the others", Cards().All(c => c.GetVisualDescendants().OfType<TextBlock>()
            .All(t => t.Text != "Hunt: Showdown 1896")));
        filter.IsChecked = false;
        await Settle();
        Check("turning it off brings it straight back", Cards().Count.ToString(), "5");
    }
}
