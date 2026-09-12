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

/// <summary>
/// One place a swapped DLL was written, and what it takes to put that one back.
///
/// A game can ship the same upscaler library in several directories — an Unreal title
/// commonly carries <c>nvngx_dlss.dll</c> both beside its executable and under
/// <c>Engine/Binaries/ThirdParty/…</c>. Which one the game loads is the game's
/// business, so a swap that replaced only the first was a coin toss: half the time it
/// appeared to do nothing at all. Every copy is therefore recorded separately, because
/// each has its own original to restore and its own hash to check.
/// </summary>
public class SwappedCopy
{
    /// <summary>The directory this copy lives in.</summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>Hash of the file as installed here, checked before reverting.</summary>
    public string? InstalledSha256 { get; set; }

    /// <summary>The version that was here before, or null when it carried none.</summary>
    public string? OriginalVersion { get; set; }

    /// <summary>Hash of the original, so a restored file can be confirmed.</summary>
    public string? OriginalSha256 { get; set; }

    /// <summary>
    /// False when there was no file at this path before the swap — then reverting means
    /// deleting rather than restoring.
    /// </summary>
    public bool ExistedBefore { get; set; } = true;

    /// <summary>
    /// Where this copy's original sits inside the per-game backup store, relative to
    /// its files directory. Distinct per copy: two directories holding the same
    /// filename would otherwise back up over each other and lose one original for good.
    /// </summary>
    public string BackupRelative { get; set; } = string.Empty;
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

    /// <summary>
    /// Every place this DLL was written, one entry per directory.
    ///
    /// The single-directory fields above predate this and are still read, so a record
    /// written by an older version keeps reverting — see <see cref="Normalize"/>. They
    /// are also still written, mirroring the first copy, so a manifest this version
    /// writes stays revertible by an older build rather than looking like a swap with
    /// no backup.
    /// </summary>
    public List<SwappedCopy> Copies { get; set; } = new();

    /// <summary>
    /// Fills <see cref="Copies"/> from the single-directory fields when a record
    /// predates them, so one code path handles both.
    ///
    /// The legacy backup layout put the original at <c>swaps/&lt;filename&gt;</c> with
    /// no directory component, and that path has to be preserved exactly: it is where
    /// the only copy of a user's original DLL actually is.
    /// </summary>
    public void Normalize()
    {
        if (Copies.Count > 0 || string.IsNullOrEmpty(InstalledInDirectory)) return;

        Copies.Add(new SwappedCopy
        {
            Directory = InstalledInDirectory,
            InstalledSha256 = InstalledSha256,
            OriginalVersion = OriginalVersion,
            OriginalSha256 = OriginalSha256,
            ExistedBefore = ExistedBefore,
            BackupRelative = LegacyBackupRelative,
        });
    }

    /// <summary>
    /// The pre-multi-copy backup path: <c>swaps/&lt;filename&gt;</c>. Kept as a
    /// constant expression rather than rebuilt at each call site so the one thing that
    /// must not drift — where an existing user's original DLL is stored — is stated once.
    /// </summary>
    public string LegacyBackupRelative => System.IO.Path.Combine("swaps", FileName);

    /// <summary>
    /// Mirrors the first copy back onto the single-directory fields, so the manifest
    /// stays readable by a version that predates <see cref="Copies"/>.
    /// </summary>
    public void MirrorFirstCopy()
    {
        if (Copies.Count == 0) return;

        var first = Copies[0];
        InstalledInDirectory = first.Directory;
        InstalledSha256 = first.InstalledSha256;
        OriginalVersion = first.OriginalVersion;
        OriginalSha256 = first.OriginalSha256;
        ExistedBefore = first.ExistedBefore;
    }
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
