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

    /// <summary>The release asset for a GPU and a kind, or null when none is built for it.</summary>
    public static string? AssetFor(RadeonGeneration generation, FasterFsr4Kind kind) => (generation, kind) switch
    {
        (RadeonGeneration.Rdna3, FasterFsr4Kind.Exact) => DllName,
        (RadeonGeneration.Rdna3, FasterFsr4Kind.Lossy) => "test-lossy.zip",
        (RadeonGeneration.Rdna2, FasterFsr4Kind.Exact) => "test-rdna2.zip",
        (RadeonGeneration.Rdna2, FasterFsr4Kind.Lossy) => "test-lossy-rdna2.zip",
        (RadeonGeneration.SteamDeck, FasterFsr4Kind.Exact) => "test-rdna2-compact.zip",
        (RadeonGeneration.SteamDeck, FasterFsr4Kind.Lossy) => "test-lossy-rdna2-compact.zip",
        (RadeonGeneration.Rdna3Integrated, FasterFsr4Kind.Exact) => "test-igpu.zip",
        (RadeonGeneration.Rdna3Integrated, FasterFsr4Kind.Lossy) => "test-lossy-igpu.zip",
        _ => null,
    };

    /// <summary>Why a GPU is offered nothing, or null when it is offered both builds.</summary>
    public static string? RefusalFor(RadeonGeneration generation) => generation switch
    {
        RadeonGeneration.Rdna4 =>
            "Not for RX 9000: these builds lift AMD's GPU check, and an RX 9000 would run the RDNA 3 model instead of its own.",
        RadeonGeneration.NotRadeon => "Built for Radeon cards only.",
        RadeonGeneration.Rdna2Integrated => "No build exists for RDNA 2 integrated graphics other than the Steam Deck.",
        RadeonGeneration.Unknown => "Could not tell which Radeon generation this GPU is, so no build can be chosen safely.",
        _ => null,
    };

    /// <summary>What OptiScaler's overlay names the upscaler when the build is the one loaded.</summary>
    public static string OverlayLabel(RadeonGeneration generation, FasterFsr4Kind kind)
    {
        var target = generation switch
        {
            RadeonGeneration.Rdna2 => "r2",
            RadeonGeneration.SteamDeck => "r2c",
            RadeonGeneration.Rdna3Integrated => "ig",
            _ => "r3",
        };
        return kind == FasterFsr4Kind.Lossy ? $"4.1.1-{target}-lossy-cyboman" : $"4.1.1-{target}-cyboman";
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
