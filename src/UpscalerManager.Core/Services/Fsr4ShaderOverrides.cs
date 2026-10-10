// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.

namespace UpscalerManager.Core.Services;

/// <summary>Which of the faster FSR 4.1.1 builds: the one with AMD's image, or the faster one.</summary>
public enum FasterFsr4Kind
{
    /// <summary>AMD's image byte for byte, about 25–30% cheaper at 4K on RX 7000.</summary>
    Exact,
    /// <summary>About twice as fast as AMD's; softer, with frame times that alternate.</summary>
    Lossy,
}

/// <summary>
/// The FSR 4.1.1 builds from zgauthier2000/fsr4-shader-performance-overrides: AMD's own
/// <c>amd_fidelityfx_upscaler_dx12.dll</c> with its slowest shaders rewritten. Which build
/// fits which GPU, and why one is refused, lives here; the download is in
/// <see cref="ComponentManagementService"/>.
/// </summary>
/// <remarks>
/// why the generation matters: every build lifts AMD's GPU check, and RX 9000 runs a
/// different FSR 4 model — so on an RX 9000 a build would run the RDNA 3 model in place
/// of the one the card is meant to run.
/// </remarks>
public static class Fsr4ShaderOverrides
{
    public const string RepoOwner = "zgauthier2000";
    public const string RepoName = "fsr4-shader-performance-overrides";
    public const string DllName = "amd_fidelityfx_upscaler_dx12.dll";

    /// <summary>
    /// The only AMD build the rewritten shaders are matched to. Any other version has
    /// different shaders, and the replacement would not be the same upscaler.
    /// </summary>
    public const string RequiredBaseVersion = "4.1.1.2740";

    /// <summary>
    /// The release asset for a GPU, or null when none is built for it. Since release
    /// dll-2026-10-09 there are two: one for RX 7000 and RX 6000 cards, one for graphics
    /// built into the processor, the Steam Deck's included. Each holds an exact and a
    /// lossy build (see <see cref="EntryFor"/>).
    /// </summary>
    public static string? AssetFor(RadeonGeneration generation) => generation switch
    {
        RadeonGeneration.Rdna3 or RadeonGeneration.Rdna2 => "fsr4.1.1-cyboman.zip",
        RadeonGeneration.Rdna3Integrated or RadeonGeneration.Rdna2Integrated or RadeonGeneration.SteamDeck
            => "fsr4.1.1-igpu-cyboman.zip",
        _ => null,
    };

    /// <summary>The path of the chosen build inside either zip.</summary>
    public static string EntryFor(FasterFsr4Kind kind) =>
        (kind == FasterFsr4Kind.Lossy ? "lossy/" : "exact/") + DllName;

    /// <summary>Why a GPU is offered nothing, or null when it is offered both builds.</summary>
    public static string? RefusalFor(RadeonGeneration generation) => generation switch
    {
        RadeonGeneration.Rdna4 =>
            "Not for RX 9000: these builds lift AMD's GPU check, and an RX 9000 would run the RDNA 3 model instead of its own.",
        RadeonGeneration.NotRadeon => "Built for Radeon cards only.",
        RadeonGeneration.Unknown => "Could not tell which Radeon generation this GPU is, so no build can be chosen safely.",
        _ => null,
    };

    /// <summary>What OptiScaler's overlay names the upscaler when the build is the one loaded.</summary>
    public static string OverlayLabel(RadeonGeneration generation, FasterFsr4Kind kind)
    {
        var integrated = AssetFor(generation) == "fsr4.1.1-igpu-cyboman.zip";
        return (integrated, kind) switch
        {
            (true, FasterFsr4Kind.Lossy) => "4.1.1-igpu-lossy-cyboman",
            (true, _) => "4.1.1-igpu-cyboman",
            (false, FasterFsr4Kind.Lossy) => "4.1.1-lossy-cyboman",
            _ => "4.1.1-cyboman",
        };
    }

    /// <summary>
    /// Why the upscaler OptiScaler installed cannot be replaced, or null when it can:
    /// the rewritten shaders are matched to <see cref="RequiredBaseVersion"/> only.
    /// </summary>
    public static string? BaseMismatch(string? installedFileVersion) =>
        installedFileVersion == RequiredBaseVersion
            ? null
            : installedFileVersion is null
                ? $"This OptiScaler release ships no {DllName}, and the faster builds replace that file."
                : $"This OptiScaler release ships FSR {installedFileVersion}; the faster builds are made from {RequiredBaseVersion} only.";
}
