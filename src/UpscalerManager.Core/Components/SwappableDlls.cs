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
/// The list is drawn from two references, checked against their source rather than
/// assumed. DLSS Swapper swaps nine vendor files, and its archive — see
/// <see cref="DllRepository"/> — carries builds of exactly those nine. Its
/// GameAssetType enum additionally declares FidelityFX SDK2, Streamline, DirectStorage
/// and DeepDVC entries, but those are reserved slots: nothing detects or swaps them,
/// DllNameForGameAssetType returns an empty string for every one, and the live manifest
/// carries their sections empty. It has no FSR 4 awareness at all — no amdxcffx64, no
/// mention of FSR 4 anywhere in it.
///
/// OptiScaler's own DllNames.h is the second reference, and the more complete one for
/// AMD: it loads six FidelityFX libraries by name (runtime, loader, upscaler, frame
/// generation, denoiser and radiance cache on DX12, plus the Vulkan runtime). Those are
/// all here, which is where this list goes past DLSS Swapper rather than catching up —
/// as are the two names the FSR 4 community builds ship under, the only route the swap
/// path has to FSR 4 at all.
///
/// Names are matched <em>case-insensitively</em>, which is the other deliberate
/// difference. DLSS Swapper's detection compares exact case — with the comment "the case
/// of these files should never change, right?" above it — and gets away with it on NTFS;
/// on the ext4 filesystems this app's primary platform uses, a game shipping
/// <c>NvNgx_Dlss.dll</c> would simply never be seen. (Its zip extraction does use an
/// ignore-case comparison, so the inconsistency is theirs rather than a blanket rule.)
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
        new SwappableDll("amd_fidelityfx_loader_dx12.dll", "FidelityFX loader (DX12)", "FidelityFX loader",
            "Picks which FidelityFX version to load. A game ships either this or the runtime above — OptiScaler tries the loader first — so whichever one is present is the one to swap."),
        new SwappableDll("amd_fidelityfx_framegeneration_dx12.dll", "FSR Frame Generation", "FSR Frame Generation",
            "AMD's frame generation. Swapping it changes how generated frames look, and like Nvidia's it is the most likely of the set to misbehave against a game built for an older build."),
        new SwappableDll("amd_fidelityfx_denoiser_dx12.dll", "FidelityFX denoiser (DX12)", "FidelityFX denoiser",
            "AMD's ray-tracing denoiser, loaded through the FidelityFX runtime."),
        new SwappableDll("amd_fidelityfx_radiancecache_dx12.dll", "FidelityFX radiance cache (DX12)", "FidelityFX radiance cache",
            "AMD's radiance cache. Not an upscaler, but it ships and swaps the same way."),

        // The FSR upscaler model itself — the file FSR 4 actually lives in, and what
        // the community INT8 builds ship. Unlike the rest of this list, it is not
        // loaded by the game directly: something has to load it, either the
        // FidelityFX runtime the game ships or an OptiScaler install. Swapping it in a
        // game that has neither achieves nothing, which the row says outright.
        new SwappableDll("amd_fidelityfx_upscaler_dx12.dll", "FSR (FidelityFX upscaler)", "FSR upscaler",
            "The file FSR 4 lives in. A newer build here is how a game that already loads AMD's FidelityFX runtime gets a newer FSR — including the community INT8 builds, which are the route to FSR 4 without a driver that provides it. It needs the FidelityFX runtime or OptiScaler present to be loaded at all."),
        new SwappableDll("amdxcffx64.dll", "AMD FSR 4 runtime (or a community INT8 build)", "FSR 4 runtime",
            "The slot AMD's FSR 4 library occupies, and the name newer community INT8 builds ship under. Same caveat: something has to load it — the driver, the FidelityFX runtime, or OptiScaler."),

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
        // Upscalers first — the swaps that change how a game looks.
        "nvngx_dlss.dll" => 0,
        "amd_fidelityfx_upscaler_dx12.dll" => 1,
        "amdxcffx64.dll" => 2,
        "libxess.dll" => 3,
        "libxess_dx11.dll" => 4,
        "nvngx_dlssd.dll" => 5,
        // Then frame generation.
        "nvngx_dlssg.dll" => 6,
        "amd_fidelityfx_framegeneration_dx12.dll" => 7,
        "libxess_fg.dll" => 8,
        // Then the runtimes an upscaler is loaded through.
        "amd_fidelityfx_dx12.dll" => 9,
        "amd_fidelityfx_loader_dx12.dll" => 10,
        "amd_fidelityfx_vk.dll" => 11,
        // Then everything that ships the same way without being an upscaler.
        "amd_fidelityfx_denoiser_dx12.dll" => 12,
        "amd_fidelityfx_radiancecache_dx12.dll" => 13,
        "libxell.dll" => 14,
        _ => 15,
    };
}
