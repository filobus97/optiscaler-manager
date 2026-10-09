// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.

using System.Text.RegularExpressions;

namespace UpscalerManager.Core.Services;

/// <summary>
/// Which Radeon generation a GPU belongs to, as far as FSR 4 cares: it runs a different
/// model on RDNA 4 than on everything older, and community builds are cut per generation.
/// </summary>
public enum RadeonGeneration
{
    /// <summary>An AMD GPU this could not place. Nothing generation-specific is offered.</summary>
    Unknown,
    /// <summary>Not an AMD GPU at all.</summary>
    NotRadeon,
    /// <summary>RX 6000 desktop and laptop cards.</summary>
    Rdna2,
    /// <summary>RDNA 2 integrated graphics other than the Steam Deck (680M, Ryzen 7000 desktop).</summary>
    Rdna2Integrated,
    /// <summary>The Steam Deck, LCD and OLED.</summary>
    SteamDeck,
    /// <summary>RX 7000 desktop and laptop cards.</summary>
    Rdna3,
    /// <summary>RDNA 3 and 3.5 integrated graphics (780M, 890M, 8060S, the Z1 Extreme and Z2).</summary>
    Rdna3Integrated,
    /// <summary>RX 9000.</summary>
    Rdna4,
}

/// <summary>Places a GPU in a <see cref="RadeonGeneration"/>, by PCI device ID first and its name second.</summary>
public static class RadeonGenerations
{
    public static RadeonGeneration Of(GpuInfo? gpu)
    {
        if (gpu is null) return RadeonGeneration.Unknown;
        if (gpu.Vendor != GpuVendor.AMD) return RadeonGeneration.NotRadeon;

        if (gpu.DeviceId is { } id && FromDeviceId(id) is var byId && byId != RadeonGeneration.Unknown)
            return byId;
        return FromName(gpu.Name);
    }

    /// <summary>
    /// The generation a PCI device ID belongs to. Discrete chips take whole ranges, which
    /// AMD allocates per family; integrated parts are listed one by one, and only where
    /// the ID is certain — anything else falls through to the name.
    /// </summary>
    internal static RadeonGeneration FromDeviceId(int id) => id switch
    {
        0x163F or 0x1435 => RadeonGeneration.SteamDeck,           // Van Gogh, Sephiroth
        >= 0x7540 and <= 0x75FF => RadeonGeneration.Rdna4,         // Navi 48, Navi 44
        >= 0x7440 and <= 0x749F => RadeonGeneration.Rdna3,         // Navi 31, 32, 33
        (>= 0x73A0 and <= 0x73FF) or (>= 0x7420 and <= 0x743F)
            => RadeonGeneration.Rdna2,                             // Navi 21, 22, 23, 24
        0x15BF or 0x15C8 or 0x150E or 0x1586
            => RadeonGeneration.Rdna3Integrated,                   // Phoenix, Strix, Strix Halo
        0x1681 or 0x164E or 0x1506
            => RadeonGeneration.Rdna2Integrated,                   // Rembrandt, Raphael, Mendocino
        _ => RadeonGeneration.Unknown,
    };

    /// <summary>
    /// The generation a GPU name points to. lspci names carry the chip's codename
    /// ("Navi 31 [Radeon RX 7900 XT/7900 XTX]"), Windows names the product ("AMD Radeon RX
    /// 7800 XT"); both are read.
    /// </summary>
    internal static RadeonGeneration FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return RadeonGeneration.Unknown;

        if (Regex.IsMatch(name, @"VanGogh|Van Gogh|Sephiroth|Custom GPU 0405|Custom GPU 0932", RegexOptions.IgnoreCase))
            return RadeonGeneration.SteamDeck;
        if (Regex.IsMatch(name, @"Phoenix|Hawk ?Point|Strix|Krackan|\b(7[2-8]0M|8[4-9]0M|80[5-6]0S)\b", RegexOptions.IgnoreCase))
            return RadeonGeneration.Rdna3Integrated;
        if (Regex.IsMatch(name, @"Rembrandt|Raphael|Mendocino|\b(6[6-8]0M)\b", RegexOptions.IgnoreCase))
            return RadeonGeneration.Rdna2Integrated;

        var navi = Regex.Match(name, @"Navi\s*(\d)\d", RegexOptions.IgnoreCase);
        if (navi.Success)
            return navi.Groups[1].Value switch
            {
                "2" => RadeonGeneration.Rdna2,
                "3" => RadeonGeneration.Rdna3,
                "4" => RadeonGeneration.Rdna4,
                _ => RadeonGeneration.Unknown,
            };

        var rx = Regex.Match(name, @"\bRX\s*([679])\d{3}", RegexOptions.IgnoreCase);
        if (rx.Success)
            return rx.Groups[1].Value switch
            {
                "6" => RadeonGeneration.Rdna2,
                "7" => RadeonGeneration.Rdna3,
                _ => RadeonGeneration.Rdna4,
            };

        return RadeonGeneration.Unknown;
    }

    /// <summary>The generation in the words a player knows it by.</summary>
    public static string Describe(RadeonGeneration generation) => generation switch
    {
        RadeonGeneration.Rdna2 => "RX 6000 (RDNA 2)",
        RadeonGeneration.Rdna2Integrated => "RDNA 2 integrated graphics",
        RadeonGeneration.SteamDeck => "Steam Deck",
        RadeonGeneration.Rdna3 => "RX 7000 (RDNA 3)",
        RadeonGeneration.Rdna3Integrated => "RDNA 3 integrated graphics",
        RadeonGeneration.Rdna4 => "RX 9000 (RDNA 4)",
        RadeonGeneration.NotRadeon => "a non-AMD GPU",
        _ => "an AMD GPU whose generation could not be read",
    };
}
