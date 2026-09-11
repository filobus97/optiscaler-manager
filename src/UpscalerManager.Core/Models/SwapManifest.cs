// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System.Collections.Generic;

namespace UpscalerManager.Core.Models;

/// <summary>
/// Where one held library build came from. Written beside the DLL, because provenance
/// cannot be derived from the file: "which of my games did this come from" is exactly
/// the question a user asks when a swap turns out badly.
/// </summary>
public class LibraryEntryMeta
{
    public string Version { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public string SourceLabel { get; set; } = string.Empty;
    public string? Sha256 { get; set; }
    public string AddedUtc { get; set; } = string.Empty;
}

/// <summary>One DLL this app replaced in a game, and what it takes to put it back.</summary>
public class SwappedFile
{
    /// <summary>The DLL's name, which is its path relative to the directory it lives in.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>The directory it was installed into — not always the game's root.</summary>
    public string InstalledInDirectory { get; set; } = string.Empty;

    /// <summary>The version this app put there.</summary>
    public string InstalledVersion { get; set; } = string.Empty;

    /// <summary>Where the installed build came from, e.g. "from Cyberpunk 2077".</summary>
    public string SourceLabel { get; set; } = string.Empty;

    /// <summary>
    /// Hash of the file as installed. Checked before a revert: if what is on disk no
    /// longer matches, the game was patched or another tool has been through, and
    /// restoring a backup of the pre-swap build over it would undo their work.
    /// </summary>
    public string? InstalledSha256 { get; set; }

    /// <summary>
    /// The version that was there before, or null when the game shipped no version
    /// resource. Shown on the revert row so the user knows what they are going back to.
    /// </summary>
    public string? OriginalVersion { get; set; }

    /// <summary>Hash of the original, so a restored file can be confirmed.</summary>
    public string? OriginalSha256 { get; set; }

    /// <summary>
    /// False when there was no file at this path before the swap — then reverting means
    /// deleting rather than restoring. Rare, but a game that ships XeSS without the DX11
    /// build is exactly this case.
    /// </summary>
    public bool ExistedBefore { get; set; } = true;

    public string SwappedAtUtc { get; set; } = string.Empty;
}

/// <summary>
/// What this app has swapped in one game.
///
/// Deliberately separate from <see cref="InstallationManifest"/> rather than folded
/// into it. The OptiScaler manifest drives an uninstall path with several years of
/// accumulated special cases — legacy in-folder backups, residue scanning, Phoenix
/// subdirectories — and it is the code that restores a player's game files. Adding a
/// second, unrelated concern to it would put the highest-stakes path in the app at
/// risk for no benefit, since swaps revert one file at a time and need none of that.
///
/// The two are kept from fighting over the same file by a collision check instead:
/// OptiScaler and a swap can never own the same DLL.
/// </summary>
public class SwapManifest
{
    public int ManifestVersion { get; set; } = 1;

    /// <summary>The game this belongs to, for diagnosing a stray file by hand.</summary>
    public string GameName { get; set; } = string.Empty;

    public List<SwappedFile> Files { get; set; } = new();
}
