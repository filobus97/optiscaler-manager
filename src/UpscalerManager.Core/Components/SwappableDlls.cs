// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.Linq;

namespace UpscalerManager.Core.Components;

/// <summary>
/// The DLLs that can simply be replaced with a newer build of themselves.
///
/// A game that ships its own DLSS, XeSS or FidelityFX library loads whatever build is
/// sitting in that file, so dropping a newer one in upgrades the game with nothing
/// hooked, nothing injected and nothing to configure. It works because the vendors keep
/// these ABIs stable across builds — which is why the list is short and specific rather
/// than "any DLL".
///
/// <para><b>The FidelityFX runtimes need a guard the others do not.</b> FidelityFX SDK
/// 2.0.0 split <c>amd_fidelityfx_dx12.dll</c> into a loader plus one module per effect,
/// and made the loader compatible with the old filename — so one name covers two
/// incompatible generations, and a build of the wrong one leaves a game with no
/// upscaler. <see cref="FidelityFxLayout"/> tells them apart from the file's own
/// evidence, and a cross-generation swap is refused. Within a generation the swap is
/// ordinary and useful: 1.0.1.38338 to 1.0.1.41314 is FSR 3.1.2 to 3.1.4.</para>
///
/// <para><b>FSR 4 is not reachable this way, and that is OptiScaler's job.</b> It lives
/// in <c>amd_fidelityfx_upscaler_dx12.dll</c>, which games older than SDK 2 do not
/// ship, and a swap can only replace a file a game already has. OptiScaler brings its
/// own upscaler instead, so that route does not depend on what the game shipped.</para>
///
/// Names are matched <em>case-insensitively</em>, which is a deliberate difference from
/// DLSS Swapper. Its detection compares exact case — with the comment "the case of these
/// files should never change, right?" above it — and gets away with it on NTFS; on the
/// ext4 filesystems this app's primary platform uses, a game shipping
/// <c>NvNgx_Dlss.dll</c> would simply never be seen.
/// </summary>
public static class SwappableDlls
{
    /// <param name="FileName">The file as it sits in the game folder.</param>
    /// <param name="Technology">
    /// Which technology it implements, using the same names as
    /// <see cref="UpscalerCatalog"/> so one game's detected components line up with the
    /// swap rows without a second mapping table.
    /// </param>
    /// <param name="Label">Short name for a row heading, e.g. "DLSS".</param>
    /// <param name="Note">
    /// What a player gets from replacing it — or the caveat that comes with doing so.
    /// </param>
    public sealed record SwappableDll(string FileName, string Technology, string Label, string Note);

    public static readonly IReadOnlyList<SwappableDll> All = new[]
    {
        // ── Nvidia ───────────────────────────────────────────────────────────────
        // The safest and most useful swaps: the DLSS presets and the transformer model
        // live in this file, so a newer build changes image quality in a game whose
        // developer has not shipped an update.
        new SwappableDll("nvngx_dlss.dll", "DLSS", "DLSS",
            "The upscaler itself. A newer build brings newer presets and, from 310.x, the transformer model — usually the single most worthwhile swap."),
        new SwappableDll("nvngx_dlssd.dll", "DLSS Ray Reconstruction", "DLSS Ray Reconstruction",
            "The ray-tracing denoiser. Swapping it changes how denoised reflections and lighting look."),
        new SwappableDll("nvngx_dlssg.dll", "DLSS Frame Generation", "DLSS Frame Generation",
            "Nvidia's frame generation. Newer builds are generally better behaved, but this one is the most likely to misbehave if the game expects an older build."),

        // ── Intel ────────────────────────────────────────────────────────────────
        new SwappableDll("libxess.dll", "XeSS", "XeSS",
            "Intel's upscaler. Runs on any modern GPU, and newer builds have improved markedly, so this is worth swapping even on non-Intel hardware."),
        new SwappableDll("libxess_dx11.dll", "XeSS (DX11)", "XeSS (DX11)",
            "The DX11 build of Intel's upscaler."),
        new SwappableDll("libxess_fg.dll", "XeSS Frame Generation", "XeSS Frame Generation",
            "Intel's frame generation."),
        new SwappableDll("libxell.dll", "XeLL", "XeLL",
            "Intel's latency reduction. Not an upscaler, but it ships and swaps the same way."),

        // ── AMD ──────────────────────────────────────────────────────────────────
        // Swappable within one SDK generation, and refused across the two. The archive
        // holds sixteen of these and AMD publishes none of them loose, so for a game on
        // SDK 1 it is the only way to a newer FSR 3.1.x.
        new SwappableDll("amd_fidelityfx_dx12.dll", "FidelityFX runtime (DX12)", "FSR (DX12)",
            "AMD's runtime. A newer build of the same SDK generation raises the FSR version the game can run; a build of the other generation is refused."),
        new SwappableDll("amd_fidelityfx_vk.dll", "FidelityFX runtime (Vulkan)", "FSR (Vulkan)",
            "The Vulkan build of AMD's runtime, with the same generation rule as the DX12 one."),
    };

    /// <summary>
    /// Files this app used to swap and no longer offers.
    ///
    /// Recognised for one reason: somebody may already have swapped one, and their
    /// game's original is in this app's backup store with nothing else pointing at it.
    /// Dropping these names outright would leave that game carrying a file the app
    /// installed, with no row to undo it and a backup it would offer to delete. So a
    /// retired file still gets a row wherever a swap record exists for it — to revert,
    /// and only to revert.
    /// </summary>
    public static readonly IReadOnlyList<SwappableDll> Retired = new[]
    {
        new SwappableDll("amd_fidelityfx_loader_dx12.dll", "FidelityFX loader (DX12)", "FidelityFX loader",
            "No longer swapped by this app. Reverting puts back the build your game shipped."),
        new SwappableDll("amd_fidelityfx_upscaler_dx12.dll", "FSR (FidelityFX upscaler)", "FSR upscaler",
            "No longer swapped by this app — OptiScaler installs and manages this file instead. Reverting puts back the build your game shipped."),
        new SwappableDll("amd_fidelityfx_framegeneration_dx12.dll", "FSR Frame Generation", "FSR Frame Generation",
            "No longer swapped by this app. Reverting puts back the build your game shipped."),
        new SwappableDll("amd_fidelityfx_denoiser_dx12.dll", "FidelityFX denoiser (DX12)", "FidelityFX denoiser",
            "No longer swapped by this app. Reverting puts back the build your game shipped."),
        new SwappableDll("amd_fidelityfx_radiancecache_dx12.dll", "FidelityFX radiance cache (DX12)", "FidelityFX radiance cache",
            "No longer swapped by this app. Reverting puts back the build your game shipped."),
        new SwappableDll("amdxcffx64.dll", "AMD FSR 4 runtime (or a community INT8 build)", "FSR 4 runtime",
            "No longer swapped by this app — OptiScaler installs and manages this file instead. Reverting puts back the build your game shipped."),
    };

    private static readonly Dictionary<string, SwappableDll> ByName =
        All.ToDictionary(d => d.FileName, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, SwappableDll> ByNameIncludingRetired =
        All.Concat(Retired).ToDictionary(d => d.FileName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every swappable filename, for scanning.</summary>
    public static IEnumerable<string> FileNames => ByName.Keys;

    /// <summary>
    /// What this file is, retired files included — so an existing swap can still be
    /// described and undone. Use <see cref="IsSwappable"/> to decide whether to
    /// <em>offer</em> a swap.
    /// </summary>
    public static SwappableDll? For(string? fileName) =>
        fileName is not null && ByNameIncludingRetired.TryGetValue(fileName, out var d) ? d : null;

    /// <summary>True when a new swap of this file can be offered. Case-insensitive, deliberately.</summary>
    public static bool IsSwappable(string? fileName) =>
        fileName is not null && ByName.ContainsKey(fileName);

    /// <summary>
    /// True when this app once swapped this file but no longer does. Such a file is
    /// shown only where a swap record exists, and only to revert it.
    /// </summary>
    public static bool IsRetired(string? fileName) =>
        fileName is not null && !ByName.ContainsKey(fileName) && ByNameIncludingRetired.ContainsKey(fileName);

    /// <summary>
    /// The order swap rows are shown in: upscalers first, then frame generation, then
    /// latency — the same priority <see cref="UpscalerCatalog"/> uses, so the two
    /// halves of a game's page read consistently.
    /// </summary>
    public static int DisplayOrder(string fileName) => fileName.ToLowerInvariant() switch
    {
        // Upscalers first — the swaps that change how a game looks.
        "nvngx_dlss.dll" => 0,
        "amd_fidelityfx_dx12.dll" => 1,
        "amd_fidelityfx_vk.dll" => 2,
        "libxess.dll" => 3,
        "libxess_dx11.dll" => 4,
        "nvngx_dlssd.dll" => 5,
        // Then frame generation.
        "nvngx_dlssg.dll" => 6,
        "libxess_fg.dll" => 7,
        // Then everything that ships the same way without being an upscaler.
        "libxell.dll" => 8,
        // Retired files sort last: they appear only to be reverted.
        _ => 9,
    };
}
