// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.Linq;
using UpscalerManager.Core.Models;

namespace UpscalerManager.Core.Components;

/// <summary>
/// What each upscaling-related file next to a game actually is.
///
/// Kept as data, like <see cref="ComponentRegistry"/>: the analyzer scans for these
/// names and the details view renders whatever comes back, so adding a technology is
/// one row here rather than changes in three places.
///
/// The wording is aimed at a player deciding what to turn on, not at a developer —
/// the point of the details view is to explain what is in the game, not to dump files.
/// </summary>
public static class UpscalerCatalog
{
    /// <param name="FileName">File to look for, matched case-insensitively.</param>
    /// <param name="Technology">Display name.</param>
    /// <param name="Role">What it does.</param>
    /// <param name="Vendor">Who makes it.</param>
    /// <param name="Explanation">One sentence a player can act on.</param>
    public sealed record TechDefinition(
        string FileName, string Technology, TechRole Role, string Vendor, string Explanation);

    public static readonly IReadOnlyList<TechDefinition> All = new[]
    {
        // ── Upscalers ────────────────────────────────────────────────────────────
        new TechDefinition("nvngx_dlss.dll", "DLSS", TechRole.Upscaler, "Nvidia",
            "Nvidia's upscaler. Normally GeForce RTX only — OptiScaler is what lets a game's DLSS option drive FSR or XeSS instead."),
        new TechDefinition("nvngx_dlssd.dll", "DLSS Ray Reconstruction", TechRole.Upscaler, "Nvidia",
            "Nvidia's ray-tracing denoiser, used in place of the game's own."),
        new TechDefinition("libxess.dll", "XeSS", TechRole.Upscaler, "Intel",
            "Intel's upscaler. Runs on any modern GPU, and best on Intel Arc."),
        new TechDefinition("amd_fidelityfx_upscaler_dx12.dll", "FSR (FidelityFX upscaler)", TechRole.Upscaler, "AMD",
            "AMD's upscaler. This is the file FSR 4 lives in, so its version is the FSR version you can actually get."),
        new TechDefinition("ffx_fsr2_api_x64.dll", "FSR 2 (older API)", TechRole.Upscaler, "AMD",
            "An older FSR 2 build linked directly into the game."),
        new TechDefinition("ffx_fsr2_api_dx12_x64.dll", "FSR 2 (older API, DX12)", TechRole.Upscaler, "AMD",
            "An older FSR 2 build linked directly into the game."),
        new TechDefinition("ffx_fsr2_api_vk_x64.dll", "FSR 2 (older API, Vulkan)", TechRole.Upscaler, "AMD",
            "An older FSR 2 build linked directly into the game."),
        new TechDefinition("ffx_fsr3_api_x64.dll", "FSR 3 (older API)", TechRole.Upscaler, "AMD",
            "An older FSR 3 build linked directly into the game."),
        new TechDefinition("ffx_fsr3_api_dx12_x64.dll", "FSR 3 (older API, DX12)", TechRole.Upscaler, "AMD",
            "An older FSR 3 build linked directly into the game."),

        // ── Frame generation ─────────────────────────────────────────────────────
        new TechDefinition("nvngx_dlssg.dll", "DLSS Frame Generation", TechRole.FrameGeneration, "Nvidia",
            "Nvidia's frame generation. On AMD it only works through a mod such as Nukem's."),
        new TechDefinition("amd_fidelityfx_framegeneration_dx12.dll", "FSR Frame Generation", TechRole.FrameGeneration, "AMD",
            "AMD's frame generation."),
        new TechDefinition("libxess_fg.dll", "XeSS Frame Generation", TechRole.FrameGeneration, "Intel",
            "Intel's frame generation."),
        new TechDefinition("dlssg_to_fsr3_amd_is_better.dll", "Nukem DLSSG-to-FSR3", TechRole.FrameGeneration, "Nukem (mod)",
            "Turns a game's DLSS frame generation into FSR 3 frame generation, so it works on AMD."),

        // ── Latency ──────────────────────────────────────────────────────────────
        new TechDefinition("nvapi64.dll", "fakenvapi", TechRole.LatencyReduction, "OptiScaler project",
            "Stands in for Nvidia's driver library so a game's Reflex setting drives AMD Anti-Lag 2 or LatencyFlex."),
        new TechDefinition("libxell.dll", "XeLL", TechRole.LatencyReduction, "Intel",
            "Intel's latency reduction."),

        // ── Runtimes / support ───────────────────────────────────────────────────
        new TechDefinition("amdxcffx64.dll", "AMD FSR 4 runtime (or a community INT8 build)", TechRole.Runtime, "AMD",
            "The slot AMD's FSR 4 library occupies. AMD's own binary is proprietary and never downloaded for you — you supply it. Newer community INT8 builds also ship under this exact name, so this file may be either; the two cannot be told apart by name alone. When it sits next to an OptiScaler upscaler library, which of the two actually runs is OptiScaler's choice, not something that can be read off the files."),
        new TechDefinition("amd_fidelityfx_dx12.dll", "FidelityFX runtime (DX12)", TechRole.Runtime, "AMD",
            "The library that loads AMD's upscaler and frame generation."),
        new TechDefinition("amd_fidelityfx_loader_dx12.dll", "FidelityFX loader (DX12)", TechRole.Runtime, "AMD",
            "Picks which FidelityFX version to load."),
        new TechDefinition("amd_fidelityfx_vk.dll", "FidelityFX runtime (Vulkan)", TechRole.Runtime, "AMD",
            "The Vulkan build of AMD's FidelityFX library."),
        new TechDefinition("OptiPatcher.asi", "OptiPatcher", TechRole.Runtime, "OptiScaler project",
            "Patches a game's \"is this an Nvidia card?\" checks in memory, so DLSS options stay visible."),
    };

    private static readonly Dictionary<string, TechDefinition> ByName =
        All.ToDictionary(d => d.FileName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every filename worth scanning for.</summary>
    public static IEnumerable<string> FileNames => ByName.Keys;

    /// <summary>What a file is, or null when it is not one we describe.</summary>
    public static TechDefinition? For(string fileName) =>
        ByName.TryGetValue(fileName, out var d) ? d : null;

    /// <summary>Section heading for a role, in the order the details view shows them.</summary>
    public static string RoleHeading(TechRole role) => role switch
    {
        TechRole.Upscaler => "Upscalers",
        TechRole.FrameGeneration => "Frame generation",
        TechRole.LatencyReduction => "Latency reduction",
        _ => "Supporting libraries",
    };

    /// <summary>Display order of the sections.</summary>
    public static int RoleOrder(TechRole role) => role switch
    {
        TechRole.Upscaler => 0,
        TechRole.FrameGeneration => 1,
        TechRole.LatencyReduction => 2,
        _ => 3,
    };
}
