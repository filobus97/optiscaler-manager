// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;

namespace UpscalerManager.Core.Components;

/// <summary>What a FidelityFX DLL actually is, which its filename alone cannot say.</summary>
public enum FidelityFxRole
{
    /// <summary>Not part of AMD's FidelityFX runtime at all.</summary>
    NotFidelityFx,

    /// <summary>
    /// An SDK 1.x build with every effect inside it, including the upscaler. The FSR
    /// version is in its provider table, not its file version.
    /// </summary>
    Monolith,

    /// <summary>
    /// An SDK 2.x loader. Carries the SDK version and <em>no effect code whatsoever</em>
    /// — so it says nothing about which FSR version a game will run.
    /// </summary>
    Loader,

    /// <summary>The SDK 2.x upscaling module. This is where FSR 4 lives.</summary>
    Upscaler,

    /// <summary>The SDK 2.x frame generation module.</summary>
    FrameGeneration,

    /// <summary>The SDK 2.x denoiser module (Ray Regeneration).</summary>
    Denoiser,

    /// <summary>The SDK 2.x radiance cache module.</summary>
    RadianceCache,
}

/// <summary>
/// Which generation of AMD's FidelityFX runtime a file belongs to, and what it holds.
///
/// why: one filename covers both SDK generations, because AMD documents the SDK 2
/// loader as compatible with the SDK 1 name and games keep it across the migration. So
/// the generation is read out of the file, never taken from the name — and a swap across
/// that line is refused. See docs/fidelityfx.md for the documentation and the binaries
/// this was verified against.
/// </summary>
public static class FidelityFxLayout
{
    /// <summary>
    /// The two names that are ambiguous, because the SDK 2 loader is documented as
    /// compatible with them and games keep them across the migration.
    /// </summary>
    private static readonly HashSet<string> LegacyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "amd_fidelityfx_dx12.dll",
        "amd_fidelityfx_vk.dll",
    };

    private static readonly Dictionary<string, FidelityFxRole> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["amd_fidelityfx_loader_dx12.dll"] = FidelityFxRole.Loader,
        ["amd_fidelityfx_upscaler_dx12.dll"] = FidelityFxRole.Upscaler,
        ["amd_fidelityfx_framegeneration_dx12.dll"] = FidelityFxRole.FrameGeneration,
        ["amd_fidelityfx_denoiser_dx12.dll"] = FidelityFxRole.Denoiser,
        ["amd_fidelityfx_radiancecache_dx12.dll"] = FidelityFxRole.RadianceCache,
    };

    /// <summary>
    /// What this file is: settled by name where the name is unambiguous, and otherwise
    /// by evidence — a provider table topping out at 4.x or above is an effect module,
    /// one in the 3.x range is an SDK 1 monolith, and no table with a major of 2 or more
    /// is a loader.
    ///
    /// why: the fallback is Monolith rather than Loader because that is the answer which
    /// gets a cross-generation swap refused rather than allowed.
    /// </summary>
    /// <param name="fileVersion">The version resource, e.g. "2.3.0.2740".</param>
    /// <param name="effectVersion">
    /// The highest provider version in the binary, from
    /// <see cref="FidelityFxVersion.FromBinary"/>. Null when there is no table, which is
    /// itself the evidence that identifies a loader.
    /// </param>
    public static FidelityFxRole Identify(string? fileName, string? fileVersion, string? effectVersion)
    {
        if (fileName is null) return FidelityFxRole.NotFidelityFx;
        if (ByName.TryGetValue(fileName, out var known)) return known;
        if (!LegacyNames.Contains(fileName)) return FidelityFxRole.NotFidelityFx;

        if (Major(effectVersion) is { } effectMajor)
            return effectMajor >= 4 ? FidelityFxRole.Upscaler : FidelityFxRole.Monolith;

        return Major(fileVersion) >= 2 ? FidelityFxRole.Loader : FidelityFxRole.Monolith;
    }

    private static int? Major(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var dot = version.IndexOf('.');
        var head = dot > 0 ? version[..dot] : version;
        return int.TryParse(head, out var major) ? major : null;
    }

    /// <summary>True when this filename belongs to AMD's FidelityFX runtime.</summary>
    public static bool IsFidelityFx(string? fileName) =>
        fileName is not null && (ByName.ContainsKey(fileName) || LegacyNames.Contains(fileName));

    /// <summary>
    /// True when a build of one role can stand in for a file of the other: same-for-same
    /// only, and anything outside the FidelityFX runtime passes. See docs/fidelityfx.md.
    /// </summary>
    public static bool Interchangeable(FidelityFxRole current, FidelityFxRole candidate) =>
        current == FidelityFxRole.NotFidelityFx
        || candidate == FidelityFxRole.NotFidelityFx
        || current == candidate;

    /// <summary>
    /// Why a build cannot replace what is in the game, in one sentence — or null when
    /// it can.
    /// </summary>
    public static string? ExplainMismatch(FidelityFxRole current, FidelityFxRole candidate) =>
        Interchangeable(current, candidate)
            ? null
            : $"{Noun(candidate)}, and this game uses {Noun(current)} — AMD split the runtime "
              + "in SDK 2.0.0 and the two generations cannot stand in for each other.";

    /// <summary>Which SDK generation a role belongs to, for a row that only has room to tag it.</summary>
    public static string Generation(FidelityFxRole role) => role switch
    {
        FidelityFxRole.Monolith => "SDK 1",
        FidelityFxRole.NotFidelityFx => string.Empty,
        _ => "SDK 2",
    };

    /// <summary>What a file of this role is, for a sentence that has to name both.</summary>
    public static string Noun(FidelityFxRole role) => role switch
    {
        FidelityFxRole.Monolith => "an SDK 1 runtime with the effects inside it",
        FidelityFxRole.Loader => "an SDK 2 loader",
        FidelityFxRole.Upscaler => "an SDK 2 upscaler module",
        FidelityFxRole.FrameGeneration => "an SDK 2 frame generation module",
        FidelityFxRole.Denoiser => "an SDK 2 denoiser module",
        FidelityFxRole.RadianceCache => "an SDK 2 radiance cache module",
        _ => "not a FidelityFX runtime",
    };

    /// <summary>
    /// How a FidelityFX file's version should be written for a player: the label that is
    /// true of its role.
    ///
    /// why: the loader's 2.3.0 is an SDK version, and left bare it reads as an FSR
    /// version two generations behind the 4.1.1 module sitting next to it.
    /// </summary>
    public static string Describe(string? fileName, string? fileVersion, string? effectVersion)
    {
        var role = Identify(fileName, fileVersion, effectVersion);

        return role switch
        {
            // The FSR version is inside the file, and the build number is not it.
            FidelityFxRole.Monolith => FidelityFxVersion.Describe(effectVersion, fileVersion),

            FidelityFxRole.Loader => fileVersion is { Length: > 0 } sdk
                ? $"FidelityFX SDK {Trim(sdk)}  (loader only)"
                : "FidelityFX SDK loader",

            FidelityFxRole.Upscaler => Effect("FSR Upscaling", fileVersion, effectVersion),
            FidelityFxRole.FrameGeneration => Effect("FSR Frame Generation", fileVersion, effectVersion),
            FidelityFxRole.Denoiser => Effect("FidelityFX denoiser", fileVersion, effectVersion),
            FidelityFxRole.RadianceCache => Effect("FidelityFX radiance cache", fileVersion, effectVersion),

            _ => FidelityFxVersion.Describe(null, fileVersion),
        };
    }

    /// <summary>
    /// An effect module's version, which its file version already is — AMD stamps the
    /// upscaler providing FSR 4.1.1 as 4.1.1.2740. The provider table is the fallback
    /// for a module whose resource is missing.
    /// </summary>
    private static string Effect(string label, string? fileVersion, string? effectVersion)
    {
        var version = fileVersion is { Length: > 0 } ? Trim(fileVersion) : effectVersion;
        return version is { Length: > 0 } ? $"{label} {version}" : label;
    }

    /// <summary>
    /// Drops the shared SDK build number from the tail: every module in SDK 2.3.0 ends
    /// in .2740, which makes four versions on one page look alike.
    /// </summary>
    private static string Trim(string version)
    {
        var parts = version.Split('.');
        return parts.Length == 4 ? string.Join('.', parts[0], parts[1], parts[2]) : version;
    }
}
