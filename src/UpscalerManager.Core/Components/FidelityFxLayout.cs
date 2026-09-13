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
/// <para><b>The thing that makes this necessary.</b> AMD restructured the runtime in
/// FidelityFX SDK 2.0.0. This is what lets the app both label these files and swap them
/// safely: "which FSR version is this game running" is the same question whether
/// OptiScaler or the game itself put the files there, and it is also the question that
/// decides whether a build can replace another.
/// Before SDK 2.0.0, <c>amd_fidelityfx_dx12.dll</c> was a monolith
/// containing every effect — upscaling included. From 2.0.0 the effects were split into
/// separate modules behind a loader, and AMD's documentation is explicit:</para>
///
/// <para><i>"Starting with AMD FidelityFX™ SDK 2.0.0 the effects, previously combined in
/// amd_fidelityfx_dx12.dll, are split into multiple DLLs based on effect type… A small
/// loader DLL, not containing any effect code… It is interface- and behavior-compatible
/// with amd_fidelityfx_dx12.dll. Applications previously loading amd_fidelityfx_dx12.dll
/// must now load amd_fidelityfx_loader_dx12.dll instead."</i></para>
///
/// <para>Because the loader is deliberately compatible with the old name, a game
/// migrating to SDK 2 can keep shipping <c>amd_fidelityfx_dx12.dll</c> — and in the
/// wild, they do. So that one filename means two completely different things, and
/// telling them apart is not optional: SDK 1 effects are deprecated to SDK 1, so
/// dropping a monolith over a loader leaves SDK 1 code trying to drive SDK 2 modules.
/// Both build sources this app offers for that filename — the DLSS Swapper archive and
/// a game's own files — can hold either generation.</para>
///
/// <para><b>What the real binaries say.</b> Read out of AMD's own signed builds in
/// <c>FidelityFX-SDK</c> at tag v2.3.0, whose release notes state it contains FSR
/// Upscaling 4.1.1:</para>
///
/// <list type="table">
/// <item><term>amd_fidelityfx_loader_dx12.dll</term><description>file version 2.3.0.2740 — the SDK version — and an empty provider table. 26 KB.</description></item>
/// <item><term>amd_fidelityfx_upscaler_dx12.dll</term><description>file version 4.1.1.2740, providers 2.3.4 / 3.1.5 / 4.1.1. So FSR 4.1.1, and the file version already says so.</description></item>
/// <item><term>amd_fidelityfx_framegeneration_dx12.dll</term><description>file version 4.0.1.2740, providers 3.1.6 / 3.1.7 / 4.0.1.</description></item>
/// <item><term>amd_fidelityfx_denoiser_dx12.dll</term><description>file version 1.2.0.2740.</description></item>
/// <item><term>amd_fidelityfx_radiancecache_dx12.dll</term><description>file version 0.9.0.2740.</description></item>
/// </list>
///
/// <para>Two conclusions that shape everything below. For an SDK 2 module the file
/// version <em>is</em> the effect version, so nothing needs translating. For the loader
/// the file version is the SDK version and the FSR version is not in that file at all —
/// showing it as though it were an FSR version is exactly the confusion this exists to
/// prevent.</para>
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
    /// What this file is.
    ///
    /// Unambiguous names are settled by name. The two legacy names are settled by
    /// evidence, in this order:
    ///
    /// <list type="number">
    /// <item>A provider table topping out in the 3.x range is an SDK 1 monolith — that
    /// is FSR 3.1.x upscaling code sitting inside the file.</item>
    /// <item>A provider table topping out at 4.x or above is an effect module, whatever
    /// it has been named. Community and OptiScaler drops do rename these.</item>
    /// <item>No provider table and a major version of 2 or more is a loader: no effect
    /// code, and a version number in the SDK's own series.</item>
    /// <item>Otherwise, assume a monolith — that is the older layout, and the
    /// conservative answer, since it is the one that gets a cross-generation swap
    /// refused rather than allowed.</item>
    /// </list>
    /// </summary>
    /// <param name="fileVersion">The version resource, e.g. "2.3.0.2740".</param>
    /// <param name="effectVersion">
    /// The highest provider version found in the binary, from
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
    /// True when a build of one role can stand in for a file of the other.
    ///
    /// Only same-for-same. SDK 1 effects are deprecated to SDK 1, so dropping a
    /// monolith over a loader leaves SDK 1 code trying to drive SDK 2 modules, and the
    /// reverse leaves a game calling into a loader with no modules beside it. Files
    /// outside the FidelityFX runtime are nobody's business here, so they pass.
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
    /// How a FidelityFX file's version should be written for a player.
    ///
    /// Each role gets the label that is actually true of it. The loader is the one that
    /// matters most: its 2.3.0 is an SDK version, and left bare it reads as an FSR
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
    /// An effect module's version. Its file version already <em>is</em> the effect
    /// version — AMD stamps the upscaler that provides FSR 4.1.1 as 4.1.1.2740 — so the
    /// provider table is only a fallback for a module whose resource is missing.
    /// </summary>
    private static string Effect(string label, string? fileVersion, string? effectVersion)
    {
        var version = fileVersion is { Length: > 0 } ? Trim(fileVersion) : effectVersion;
        return version is { Length: > 0 } ? $"{label} {version}" : label;
    }

    /// <summary>
    /// Drops the shared SDK build number from the tail: every module in SDK 2.3.0 ends
    /// in .2740, which carries no information a player can use and makes four versions
    /// on one page look alike.
    /// </summary>
    private static string Trim(string version)
    {
        var parts = version.Split('.');
        return parts.Length == 4 ? string.Join('.', parts[0], parts[1], parts[2]) : version;
    }
}
