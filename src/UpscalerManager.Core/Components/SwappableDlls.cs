// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.Linq;

namespace UpscalerManager.Core.Components;

/// <summary>
/// The DLLs that can simply be replaced with a newer build of themselves.
///
/// This is the whole of the swap route: a game that ships its own DLSS, FSR or XeSS
/// library loads whatever build is sitting in that file, so dropping a newer one in
/// upgrades the game without OptiScaler in the picture at all. It works because the
/// vendors keep these ABIs stable across builds — which is also why the list is short
/// and specific rather than "any DLL".
///
/// Taken from DLSS Swapper's own swappable set, with one deliberate difference: names
/// are matched <em>case-insensitively</em>. DLSS Swapper compares exact case and gets
/// away with it on NTFS; on the ext4 filesystems this app's primary platform uses, a
/// game shipping <c>NvNgx_Dlss.dll</c> would simply never be seen.
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
        new SwappableDll("nvngx_dlssg.dll", "DLSS Frame Generation", "DLSS Frame Generation",
            "Nvidia's frame generation. Newer builds are generally better behaved, but this one is the most likely to misbehave if the game expects an older build."),
        new SwappableDll("nvngx_dlssd.dll", "DLSS Ray Reconstruction", "DLSS Ray Reconstruction",
            "The ray-tracing denoiser. Swapping it changes how denoised reflections and lighting look."),

        // ── AMD ──────────────────────────────────────────────────────────────────
        // Note these are the FidelityFX *runtime* libraries, not the upscaler model
        // file OptiScaler bundles. A game linking the FidelityFX runtime loads its
        // upscaler through these.
        new SwappableDll("amd_fidelityfx_dx12.dll", "FidelityFX runtime (DX12)", "FSR (DX12)",
            "The library that loads AMD's upscaler on DX12. Swapping it is how a game gets a newer FSR without OptiScaler — when the game uses the FidelityFX runtime rather than linking FSR directly."),
        new SwappableDll("amd_fidelityfx_vk.dll", "FidelityFX runtime (Vulkan)", "FSR (Vulkan)",
            "The Vulkan build of the same library."),

        // ── Intel ────────────────────────────────────────────────────────────────
        new SwappableDll("libxess.dll", "XeSS", "XeSS",
            "Intel's upscaler. Runs on any modern GPU, and newer builds have improved markedly, so this is worth swapping even on non-Intel hardware."),
        new SwappableDll("libxess_dx11.dll", "XeSS (DX11)", "XeSS (DX11)",
            "The DX11 build of Intel's upscaler."),
        new SwappableDll("libxess_fg.dll", "XeSS Frame Generation", "XeSS Frame Generation",
            "Intel's frame generation."),
        new SwappableDll("libxell.dll", "XeLL", "XeLL",
            "Intel's latency reduction. Not an upscaler, but it ships and swaps the same way."),
    };

    private static readonly Dictionary<string, SwappableDll> ByName =
        All.ToDictionary(d => d.FileName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every swappable filename, for scanning.</summary>
    public static IEnumerable<string> FileNames => ByName.Keys;

    /// <summary>What this file is, or null when it is not one we can swap.</summary>
    public static SwappableDll? For(string? fileName) =>
        fileName is not null && ByName.TryGetValue(fileName, out var d) ? d : null;

    /// <summary>True when this file can be swapped. Case-insensitive, deliberately.</summary>
    public static bool IsSwappable(string? fileName) => For(fileName) is not null;

    /// <summary>
    /// The order swap rows are shown in: upscalers first, then frame generation, then
    /// latency — the same priority <see cref="UpscalerCatalog"/> uses, so the two
    /// halves of a game's page read consistently.
    /// </summary>
    public static int DisplayOrder(string fileName) => fileName.ToLowerInvariant() switch
    {
        "nvngx_dlss.dll" => 0,
        "amd_fidelityfx_dx12.dll" => 1,
        "amd_fidelityfx_vk.dll" => 2,
        "libxess.dll" => 3,
        "libxess_dx11.dll" => 4,
        "nvngx_dlssd.dll" => 5,
        "nvngx_dlssg.dll" => 6,
        "libxess_fg.dll" => 7,
        "libxell.dll" => 8,
        _ => 9,
    };
}
