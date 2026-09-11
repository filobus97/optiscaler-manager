// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.Linq;

namespace UpscalerManager.Core.Models;

/// <summary>Who makes a technology, which is what decides the colour it is shown in.</summary>
public enum TechVendor
{
    Other,
    Nvidia,
    Amd,
    Intel,
}

/// <summary>
/// A "DLSS" / "FSR" / "XeSS" tag naming one technology found in a game, for the game
/// card.
///
/// Presence only, deliberately no version. A card cannot say which version is the one
/// that will actually load: a game can carry several files under the same technology —
/// its own shipped FSR next to the one an OptiScaler install brought — and picking the
/// newest to display named a version the game does not necessarily use. That was
/// reported as a card claiming FSR 4.1.1 on a game where the 4.0.2 community build had
/// just been installed. The game's own page lists every file with its own version, which
/// is the honest place for that detail.
/// </summary>
public sealed record TechChip(string Label, TechVendor Vendor)
{
    /// <summary>
    /// One chip per technology present in the game.
    ///
    /// Built from the per-file detection, deliberately: the game's summary
    /// <c>FsrVersion</c>/<c>DlssVersion</c> fields skip files this app installed, so a
    /// game whose FSR library arrived with an OptiScaler install reported no FSR at
    /// all. What matters on a card is what is in the game now, not who put it there.
    /// </summary>
    public static IReadOnlyList<TechChip> For(Game game)
    {
        if (game.DetectedComponents.Count == 0) return Array.Empty<TechChip>();

        return game.DetectedComponents
            .Where(c => c.Role is TechRole.Upscaler or TechRole.FrameGeneration)
            .GroupBy(c => ShortName(c.Technology))
            .OrderBy(g => Order(g.Key))
            .Select(g => new TechChip(g.Key, VendorOf(g.First().Vendor)))
            .ToList();
    }

    /// <summary>
    /// Chip-sized names. The catalogue's full names ("FSR (FidelityFX upscaler)") are
    /// written for the details page, where there is room to read them.
    /// </summary>
    internal static string ShortName(string technology)
    {
        bool Starts(string prefix) => technology.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        if (Starts("DLSS Frame Generation")) return "DLSS FG";
        if (Starts("DLSS Ray Reconstruction")) return "DLSS RR";
        if (Starts("DLSS")) return "DLSS";
        if (Starts("XeSS Frame Generation")) return "XeSS FG";
        if (Starts("XeSS")) return "XeSS";
        if (Starts("FSR Frame Generation")) return "FSR FG";
        if (Starts("FSR")) return "FSR";
        if (Starts("Nukem")) return "Nukem FG";
        return technology;
    }

    /// <summary>Upscalers first, then frame generation; vendors in a stable order.</summary>
    private static int Order(string shortName) => shortName switch
    {
        "DLSS" => 0,
        "FSR" => 1,
        "XeSS" => 2,
        "DLSS RR" => 3,
        "DLSS FG" => 4,
        "FSR FG" => 5,
        "XeSS FG" => 6,
        "Nukem FG" => 7,
        _ => 8,
    };

    internal static TechVendor VendorOf(string vendor)
    {
        if (vendor.Contains("Nvidia", StringComparison.OrdinalIgnoreCase)) return TechVendor.Nvidia;
        if (vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase)) return TechVendor.Amd;
        if (vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return TechVendor.Intel;
        return TechVendor.Other;
    }
}
