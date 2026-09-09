// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
namespace UpscalerManager.Core.Models;

/// <summary>What a detected file does, in the terms a player thinks in.</summary>
public enum TechRole
{
    /// <summary>Renders at a lower resolution and scales it up.</summary>
    Upscaler,
    /// <summary>Invents extra frames between rendered ones.</summary>
    FrameGeneration,
    /// <summary>Reduces input latency.</summary>
    LatencyReduction,
    /// <summary>A support library the above need in order to work.</summary>
    Runtime,
}

/// <summary>
/// Whether this app is responsible for a file being where it is.
///
/// Only <see cref="Manager"/> is something the app can actually prove, from its install
/// manifest. Everything else is <see cref="Unattributed"/> — the file may have shipped
/// with the game, or been put there by a mod, another tool, or the player. The app has
/// no way to tell those apart, so it does not guess.
/// </summary>
public enum ComponentSource
{
    /// <summary>This app did not put the file here. Who did is unknown.</summary>
    Unattributed,
    /// <summary>Placed there by this app, according to the install manifest.</summary>
    Manager,
}

/// <summary>
/// One upscaling/frame-gen component found next to a game, with the version actually
/// on disk. A game can carry several at once — including more than one FSR file at
/// different versions — so these are reported individually rather than collapsed into
/// a single "FSR version".
/// </summary>
public class DetectedComponent
{
    /// <summary>Display name, e.g. "DLSS" or "FSR (FidelityFX upscaler)".</summary>
    public string Technology { get; set; } = string.Empty;

    /// <summary>The file this was read from, e.g. "nvngx_dlss.dll".</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Version read from the file, or null when it does not declare one.</summary>
    public string? Version { get; set; }

    /// <summary>Where it sits, relative to the game folder.</summary>
    public string RelativePath { get; set; } = string.Empty;

    public TechRole Role { get; set; }

    public ComponentSource Source { get; set; }

    /// <summary>Who makes it — "Nvidia", "AMD", "Intel", or a mod author.</summary>
    public string Vendor { get; set; } = string.Empty;

    /// <summary>One plain sentence explaining what it is.</summary>
    public string Explanation { get; set; } = string.Empty;
}
