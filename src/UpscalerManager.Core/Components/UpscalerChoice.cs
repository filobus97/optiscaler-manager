// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.Linq;

namespace UpscalerManager.Core.Components;

/// <summary>Which graphics API a game renders with, since the ini has a key per API.</summary>
public enum UpscalerApi
{
    Dx12,
    Dx11,
    Vulkan,
}

/// <summary>The <c>[Upscalers]</c> codes for one choice, one per API.</summary>
/// <param name="Dx12">Value for <c>Dx12Upscaler</c>, or null when unavailable there.</param>
/// <param name="Dx11">Value for <c>Dx11Upscaler</c>.</param>
/// <param name="Vulkan">Value for <c>VulkanUpscaler</c>.</param>
public sealed record UpscalerCodes(string? Dx12, string? Dx11, string? Vulkan)
{
    public string? For(UpscalerApi api) => api switch
    {
        UpscalerApi.Dx12 => Dx12,
        UpscalerApi.Dx11 => Dx11,
        _ => Vulkan,
    };
}

/// <summary>
/// Which upscaler OptiScaler should actually run, for the install screen to offer. The
/// choice lives in [Upscalers] Dx12Upscaler, Dx11Upscaler and VulkanUpscaler, one key
/// per API.
///
/// why: the lists below are OptiScaler's own, read from MenuCommon::AddDx12Backends,
/// AddDx11Backends and AddVulkanBackends — the same source that populates its overlay
/// picker — so this app cannot offer a combination OptiScaler would reject. That is
/// also why dlssd is absent: it is a denoiser path, and appears in none of the three.
///
/// A code selects a family rather than a release: ffx covers FSR 2.3, 3.1 and 4.x
/// alike, and which one runs is decided by <see cref="FfxProviderChoice"/>.
/// </summary>
public static class UpscalerChoice
{
    /// <param name="Id">Stable identifier this app persists and writes to its manifest.</param>
    /// <param name="Label">What the install screen calls it.</param>
    /// <param name="Vendor">Whose upscaler it is, for the row's colour and wording.</param>
    /// <param name="Note">One plain sentence on what choosing it means.</param>
    /// <param name="Codes">The modern <c>[Upscalers]</c> spellings.</param>
    /// <param name="LegacyCodes">
    /// The pre-rename spellings, where a release used different ones. Only the
    /// FidelityFX family was renamed — <c>fsr31</c>/<c>fsr31_12</c> became
    /// <c>ffx</c>/<c>ffx_12</c> — and writing a code a given release does not
    /// understand silently falls back to its default, which on DX12 is XeSS.
    /// </param>
    /// <param name="RequiresGpuVendor">
    /// The GPU vendor this needs, or null for any. OptiScaler applies the same gate in
    /// its own picker: it hides DLSS unless the primary GPU is DLSS-capable.
    /// </param>
    public sealed record Choice(
        string Id,
        string Label,
        string Vendor,
        string Note,
        UpscalerCodes Codes,
        UpscalerCodes? LegacyCodes = null,
        string? RequiresGpuVendor = null)
    {
        /// <summary>True when this choice can be written for the given API at all.</summary>
        public bool Supports(UpscalerApi api) => Codes.For(api) is { Length: > 0 };
    }

    /// <summary>Leave the choice to OptiScaler, which is its own default behaviour.</summary>
    public const string AutoId = "auto";

    /// <summary>The FidelityFX family — the one whose version needs choosing separately.</summary>
    public const string FidelityFxId = "ffx";

    public static readonly IReadOnlyList<Choice> All = new[]
    {
        new Choice(AutoId, "Whatever OptiScaler picks", string.Empty,
            "OptiScaler decides per game and per API. Its own default, and the right answer when you have no particular preference.",
            new UpscalerCodes("auto", "auto", "auto")),

        new Choice(FidelityFxId, "FSR (FidelityFX)", "AMD",
            "AMD's modern upscaler: FSR 2.3, 3.1 and 4.x all live behind this one choice. Which version runs depends on the upscaler module present — pick that below.",
            new UpscalerCodes("ffx", "ffx_12", "ffx"),
            new UpscalerCodes("fsr31", "fsr31_12", "fsr31_12")),

        new Choice("dlss", "DLSS", "Nvidia",
            "Nvidia's own upscaler, run as itself rather than translated. Needs a GeForce RTX card — OptiScaler hides this option on anything else, and so does this screen.",
            new UpscalerCodes("dlss", "dlss", "dlss"),
            RequiresGpuVendor: "Nvidia"),

        new Choice("xess", "XeSS", "Intel",
            "Intel's upscaler. Runs on any modern GPU, not just Intel Arc, and recent builds are competitive. On DX11 it goes through OptiScaler's DX12 compatibility layer, which avoids Intel-only native XeSS.",
            new UpscalerCodes("xess", "xess_12", "xess")),

        new Choice("fsr22", "FSR 2.2", "AMD",
            "The older FSR 2.2 backend. Worth trying when a game misbehaves with the modern FidelityFX path.",
            new UpscalerCodes("fsr22", "fsr22_12", "fsr22")),

        new Choice("fsr21", "FSR 2.1", "AMD",
            "The oldest FSR backend OptiScaler carries. A fallback for stubborn games rather than a quality choice.",
            new UpscalerCodes("fsr21", "fsr21_12", "fsr21")),
    };

    private static readonly Dictionary<string, Choice> ById =
        All.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>The choice with this id, or null when the id is unknown.</summary>
    public static Choice? For(string? id) =>
        id is not null && ById.TryGetValue(id, out var c) ? c : null;

    /// <summary>The default: hand the decision to OptiScaler.</summary>
    public static Choice Auto => ById[AutoId];

    /// <summary>
    /// The choices worth offering for a given GPU, in display order.
    ///
    /// <paramref name="gpuVendor"/> is matched loosely because it comes from a detected
    /// device string. When it is unknown, everything is offered rather than nothing:
    /// guessing a user out of the option they came for is worse than letting OptiScaler
    /// refuse it in the overlay.
    /// </summary>
    public static IReadOnlyList<Choice> AvailableFor(string? gpuVendor) =>
        All.Where(c => c.RequiresGpuVendor is null
                       || gpuVendor is null or ""
                       || gpuVendor.Contains(c.RequiresGpuVendor, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// True when this choice is barred on the given GPU — so a row can be shown with the
    /// reason rather than quietly vanishing.
    /// </summary>
    public static bool IsBlockedOn(Choice choice, string? gpuVendor) =>
        choice.RequiresGpuVendor is { } needed
        && gpuVendor is { Length: > 0 }
        && !gpuVendor.Contains(needed, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What the user asked OptiScaler to upscale with, as the install screen captured it.
/// </summary>
/// <param name="ChoiceId">
/// An id from <see cref="UpscalerChoice"/>, or "auto" to leave it to OptiScaler.
/// </param>
/// <param name="FfxProviderIndex">
/// For the FidelityFX family only: which provider to ask for, as an index into the
/// newest-first list AMD reports. 0 is the newest and is exact; null leaves
/// <c>[FSR] UpscalerIndex</c> alone.
/// </param>
public sealed record UpscalerSelection(string ChoiceId, int? FfxProviderIndex)
{
    /// <summary>Leave it to OptiScaler.</summary>
    public static UpscalerSelection Auto { get; } = new(UpscalerChoice.AutoId, null);

    /// <summary>
    /// What "pre-enable FSR 4" used to mean: the FidelityFX family, newest provider.
    /// </summary>
    public static UpscalerSelection NewestFsr { get; } = new(UpscalerChoice.FidelityFxId, 0);

    /// <summary>The catalogue entry, falling back to Auto for an unknown id.</summary>
    public UpscalerChoice.Choice Choice => UpscalerChoice.For(ChoiceId) ?? UpscalerChoice.Auto;

    /// <summary>True when this selects the FidelityFX family, where the version matters.</summary>
    public bool IsFidelityFx =>
        ChoiceId.Equals(UpscalerChoice.FidelityFxId, System.StringComparison.OrdinalIgnoreCase);

    /// <summary>True when OptiScaler is left to decide.</summary>
    public bool IsAuto =>
        ChoiceId.Equals(UpscalerChoice.AutoId, System.StringComparison.OrdinalIgnoreCase);
}
