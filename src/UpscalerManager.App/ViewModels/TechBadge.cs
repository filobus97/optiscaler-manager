// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using UpscalerManager.Core.Models;

namespace UpscalerManager.App.ViewModels;

/// <summary>
/// A <see cref="TechChip"/> ready to draw: the same label, plus the vendor's colour
/// from the app palette. Nvidia green, AMD red, Intel blue, wherever a technology is
/// named.
/// </summary>
public sealed record TechBadge(string Label, TechVendor Vendor)
{
    public static IReadOnlyList<TechBadge> For(Game game) =>
        TechChip.For(game).Select(c => new TechBadge(c.Label, c.Vendor)).ToList();

    /// <summary>Resolved here rather than in markup, so no value converter is needed.</summary>
    public IBrush? Accent => Application.Current?.FindResource(BrushKey) as IBrush;

    /// <summary>The palette key this badge draws with. Public so the UI harness can
    /// assert the colour coding without reaching into rendered pixels.</summary>
    public string BrushKey => Vendor switch
    {
        TechVendor.Nvidia => "BrTechNvidia",
        TechVendor.Amd => "BrTechAmd",
        TechVendor.Intel => "BrTechIntel",
        _ => "BrTextSecondary",
    };
}
