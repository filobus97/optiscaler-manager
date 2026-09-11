// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using UpscalerManager.Core.Components;
using UpscalerManager.Core.Logging;
using UpscalerManager.Core.Models;

namespace UpscalerManager.Core.Services;

/// <summary>Why a swap cannot be offered, in terms a player can act on.</summary>
/// <param name="Allowed">True when the swap can go ahead.</param>
/// <param name="Reason">Empty when allowed; otherwise what stands in the way.</param>
public sealed record SwapVerdict(bool Allowed, string Reason)
{
    public static readonly SwapVerdict Ok = new(true, string.Empty);
    public static SwapVerdict No(string reason) => new(false, reason);
}

/// <summary>One swappable DLL as it currently stands in a game.</summary>
/// <param name="FileName">The DLL.</param>
/// <param name="Directory">The directory it lives in.</param>
/// <param name="Version">The version on disk, or null when it carries none.</param>
/// <param name="Swapped">The record of this app's swap, when there is one.</param>
/// <param name="Verdict">Whether a swap can be offered for it.</param>
public sealed record SwapSlot(
    string FileName, string Directory, string? Version, SwappedFile? Swapped, SwapVerdict Verdict)
{
    public SwappableDlls.SwappableDll Definition => SwappableDlls.For(FileName)!;

    /// <summary>True when this app put the current build there and can put it back.</summary>
    public bool IsOurs => Swapped is not null;
}

/// <summary>
/// Replaces a game's own upscaler DLL with a different build of the same DLL, and puts
/// the original back on request.
///
/// The swap route exists because OptiScaler is overkill for a great many games: if a
/// game already ships DLSS, dropping a newer <c>nvngx_dlss.dll</c> beside its
/// executable upgrades it with nothing hooked, nothing injected, and nothing to
/// configure.
///
/// Three things this does that DLSS Swapper does not:
///
/// <list type="bullet">
/// <item>
/// The original goes into the app's external backup store, not a sibling
/// <c>.dlsss</c> file in the game folder. A game verifying its files removes a stray
/// sibling; the store survives that, and survives the game being reinstalled.
/// </item>
/// <item>
/// Filenames match case-insensitively. DLSS Swapper compares exact case and relies
/// on NTFS to paper over it — on ext4 a game shipping <c>NvNgx_Dlss.dll</c> would
/// simply never be offered.
/// </item>
/// <item>
/// A swap is refused outright where OptiScaler owns the same file, rather than
/// letting the two silently overwrite each other.
/// </item>
/// </list>
/// </summary>
public sealed class DllSwapService
{
    private const string SwapManifestFileName = "swaps.json";

    /// <summary>
    /// Subdirectory inside the per-game backup store that holds pre-swap originals,
    /// keeping them clear of OptiScaler's own backups of the same filenames.
    /// </summary>
    private const string BackupPrefix = "swaps";

    private readonly BackupStoreService _backupStore = new();

    // ── Where the record lives ───────────────────────────────────────────────

    private string ManifestPath(Game game) =>
        Path.Combine(_backupStore.GetBackupRoot(game.InstallPath), SwapManifestFileName);

    public SwapManifest LoadManifest(Game game)
    {
        var path = ManifestPath(game);
        if (!File.Exists(path)) return new SwapManifest { GameName = game.Name };

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), OptimizerContext.Default.SwapManifest)
                   ?? new SwapManifest { GameName = game.Name };
        }
        catch (Exception ex)
        {
            // A corrupt record must not be treated as "nothing was swapped": that would
            // let the app overwrite a game's original DLL a second time and lose the
            // pre-swap backup's claim on it. Refuse instead and say why.
            throw new InvalidOperationException(
                $"The record of swapped files for {game.Name} could not be read ({ex.Message}). " +
                $"It is at {path}; the original DLLs are in the same folder under '{BackupPrefix}'.", ex);
        }
    }

    private void SaveManifest(Game game, SwapManifest manifest)
    {
        var root = _backupStore.GetBackupRoot(game.InstallPath);
        Directory.CreateDirectory(root);

        var path = ManifestPath(game);
        var tmp = path + ".tmp";
        manifest.GameName = game.Name;

        // Written aside and moved into place, as the install manifest is: a half-written
        // record of which files have backups is worse than no record.
        File.WriteAllText(tmp, JsonSerializer.Serialize(manifest, OptimizerContext.Default.SwapManifest));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>True when this game has anything swapped, so its backup is still needed.</summary>
    public bool HasSwaps(Game game)
    {
        try { return LoadManifest(game).Files.Count > 0; }
        catch { return true; }   // unreadable record: assume the backup matters
    }

    /// <summary>
    /// Whether the given game directory has swaps recorded, without needing a
    /// <see cref="Game"/>. The Storage page works from backup folders on disk and has
    /// no scanned game to hand, but must not offer to delete a backup a swap depends on.
    /// </summary>
    public bool HasSwapsForDirectory(string gameDir)
    {
        try
        {
            var path = Path.Combine(_backupStore.GetBackupRoot(gameDir), SwapManifestFileName);
            if (!File.Exists(path)) return false;
            var manifest = JsonSerializer.Deserialize(File.ReadAllText(path), OptimizerContext.Default.SwapManifest);
            return manifest is { Files.Count: > 0 };
        }
        catch
        {
            // Unreadable means "there may be swaps", which is the safe answer here: it
            // keeps the backup out of the deletable pile.
            return File.Exists(Path.Combine(_backupStore.GetBackupRoot(gameDir), SwapManifestFileName));
        }
    }

    // ── What can be swapped in this game ─────────────────────────────────────

    /// <summary>
    /// One row per swappable DLL actually present in the game, in display order.
    ///
    /// Only files that are there: this route replaces a library a game already ships
    /// rather than adding one. A game with no <c>nvngx_dlss.dll</c> does not call DLSS,
    /// and dropping the file in would achieve nothing.
    /// </summary>
    public IReadOnlyList<SwapSlot> Slots(Game game)
    {
        var manifest = SafeManifest(game);
        var optiScaler = _backupStore.LoadManifest(game.InstallPath);

        var slots = new List<SwapSlot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var component in game.DetectedComponents)
        {
            if (SwappableDlls.For(component.FileName) is not { } definition) continue;
            if (!seen.Add(definition.FileName)) continue;

            var path = DllLibraryService.ResolveComponentPath(game, component);
            if (path is null) continue;

            var dir = Path.GetDirectoryName(path)!;
            var swapped = manifest.Files.FirstOrDefault(
                f => f.FileName.Equals(definition.FileName, StringComparison.OrdinalIgnoreCase));

            slots.Add(new SwapSlot(
                definition.FileName, dir, component.Version, swapped,
                Judge(definition.FileName, swapped, optiScaler)));
        }

        return slots.OrderBy(s => SwappableDlls.DisplayOrder(s.FileName)).ToList();
    }

    /// <summary>
    /// Whether a swap of this file can be offered.
    ///
    /// The one hard refusal is a file OptiScaler is already responsible for. Both
    /// routes write into the same game folder, and <c>libxess.dll</c> in particular is
    /// on both lists — so without this, installing OptiScaler and then swapping XeSS
    /// would leave two components each believing they own the file, and whichever
    /// reverted second would restore the other's build over the game's original.
    /// </summary>
    private static SwapVerdict Judge(string fileName, SwappedFile? swapped, InstallationManifest? optiScaler)
    {
        // Already ours: reverting is always allowed, so this is not a refusal.
        if (swapped is not null) return SwapVerdict.Ok;

        if (optiScaler is null) return SwapVerdict.Ok;
        if (!string.Equals(optiScaler.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase))
            return SwapVerdict.Ok;

        var ownedByOptiScaler =
            optiScaler.InstalledFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase)
            || optiScaler.ExpectedFinalMarkers.Contains(fileName, StringComparer.OrdinalIgnoreCase);

        if (ownedByOptiScaler)
            return SwapVerdict.No(
                $"OptiScaler installed this copy of {fileName}, so swapping it would fight the " +
                "OptiScaler install. Remove OptiScaler first, or change the upscaler from its overlay instead.");

        // Not currently OptiScaler's, but on the list of files an install may replace.
        // Allowed — with the consequence stated, since a later OptiScaler install would
        // overwrite the swap.
        if (GameInstallationService.SensitiveArtifacts.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            return SwapVerdict.Ok;

        return SwapVerdict.Ok;
    }

    /// <summary>
    /// True when installing OptiScaler into this game would tread on a swapped file, and
    /// which files those are. The install screen warns rather than refuses: OptiScaler
    /// is the more capable route, so the user may well want it to win.
    /// </summary>
    public IReadOnlyList<string> SwapsOptiScalerWouldOverwrite(Game game)
    {
        var swapped = SafeManifest(game).Files.Select(f => f.FileName);
        return swapped
            .Where(name => GameInstallationService.SensitiveArtifacts.Contains(name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(name => SwappableDlls.DisplayOrder(name))
            .ToList();
    }

    // ── Doing it ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Puts a library build into the game, backing up whatever is there first.
    ///
    /// The order matters and is the whole safety story: back up, verify the backup,
    /// then overwrite. A copy that fails after the backup leaves the game with its own
    /// file and a spare copy of it; a copy that fails before leaves the game untouched.
    /// Neither loses anything.
    /// </summary>
    public SwappedFile Swap(Game game, SwapSlot slot, LibraryDll build)
    {
        if (!slot.Verdict.Allowed)
            throw new InvalidOperationException(slot.Verdict.Reason);

        if (!build.FileName.Equals(slot.FileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Cannot install {build.FileName} as {slot.FileName}: a DLL has to keep its own name, " +
                "because the name is what the game loads.");

        if (!File.Exists(build.Path))
            throw new FileNotFoundException(
                $"The library's copy of {build.FileName} {build.Version} is gone. " +
                "Re-add it from a game that has it, or import it again.", build.Path);

        var target = Path.Combine(slot.Directory, slot.FileName);
        var manifest = LoadManifest(game);
        var existing = manifest.Files.FirstOrDefault(
            f => f.FileName.Equals(slot.FileName, StringComparison.OrdinalIgnoreCase));

        var existedBefore = File.Exists(target);
        var backupRelative = Path.Combine(BackupPrefix, slot.FileName);

        // Back up only the game's own file, and only once. A second swap must not
        // overwrite the backup with the build from the first — that is how the original
        // gets lost for good, and it is a one-way mistake.
        if (existing is null && existedBefore)
        {
            var originalHash = FileHash.Sha256(target);
            if (!_backupStore.BackupFile(game.InstallPath, slot.Directory, slot.FileName, backupRelative))
                throw new IOException($"Could not back up the game's own {slot.FileName}; nothing was changed.");

            var stored = Path.Combine(_backupStore.GetFilesDir(game.InstallPath), backupRelative);
            if (originalHash is not null && FileHash.Sha256(stored) != originalHash)
                throw new IOException(
                    $"The backup of {slot.FileName} does not match the file it was taken from, " +
                    "so it is not safe to overwrite the original. Nothing was changed.");

            existing = new SwappedFile
            {
                FileName = slot.FileName,
                OriginalVersion = slot.Version,
                OriginalSha256 = originalHash,
                ExistedBefore = true,
            };
            Log.Write($"[Swap] Backed up the game's own {slot.FileName} for {game.Name}.");
        }

        existing ??= new SwappedFile { FileName = slot.FileName, ExistedBefore = false };

        File.Copy(build.Path, target, overwrite: true);

        existing.InstalledInDirectory = slot.Directory;
        existing.InstalledVersion = build.Version;
        existing.SourceLabel = build.SourceLabel;
        existing.InstalledSha256 = FileHash.Sha256(target);
        existing.SwappedAtUtc = DateTime.UtcNow.ToString("o");

        if (!manifest.Files.Contains(existing)) manifest.Files.Add(existing);
        SaveManifest(game, manifest);

        // The analyzer caches on the game folder's write stamp, and overwriting a file
        // in place does not change it — so without this the game's page would keep
        // reporting the version that was there before the swap.
        GameAnalyzerService.InvalidateCacheForPath(game.InstallPath);

        Log.Write($"[Swap] {game.Name}: {slot.FileName} -> {build.Version} ({build.SourceLabel}).");
        return existing;
    }

    /// <summary>
    /// Puts the game's own DLL back.
    ///
    /// Refuses when what is on disk is not the build this app installed: a game patch
    /// or another tool has been through, and restoring a backup from before that would
    /// quietly undo their work. <paramref name="force"/> is for a user who has been
    /// told that and wants the original back regardless.
    /// </summary>
    public void Revert(Game game, SwappedFile swapped, bool force = false)
    {
        var target = Path.Combine(swapped.InstalledInDirectory, swapped.FileName);

        if (!force && File.Exists(target) && swapped.InstalledSha256 is { Length: > 0 } expected)
        {
            var actual = FileHash.Sha256(target);
            if (actual is not null && actual != expected)
                throw new InvalidOperationException(
                    $"{swapped.FileName} is no longer the build this app installed — the game has " +
                    "probably been patched, or another tool has replaced it. Restoring the older " +
                    "original over it would undo that change.");
        }

        var manifest = LoadManifest(game);
        var backupRelative = Path.Combine(BackupPrefix, swapped.FileName);

        if (swapped.ExistedBefore)
        {
            if (!_backupStore.RestoreFile(game.InstallPath, swapped.InstalledInDirectory, swapped.FileName, backupRelative))
                throw new FileNotFoundException(
                    $"The backup of the game's own {swapped.FileName} is missing, so it cannot be " +
                    "restored. Verify the game's files in its launcher to get the original back.",
                    Path.Combine(_backupStore.GetFilesDir(game.InstallPath), backupRelative));

            if (swapped.OriginalSha256 is { Length: > 0 } originalHash
                && FileHash.Sha256(target) is { } restoredHash
                && restoredHash != originalHash)
                Log.Write($"[Swap] Restored {swapped.FileName} but its hash does not match what was recorded.");
        }
        else if (File.Exists(target))
        {
            // There was no file here before the swap, so putting it back means removing
            // ours. A game that ships XeSS without the DX11 build is exactly this case.
            File.Delete(target);
        }

        manifest.Files.RemoveAll(
            f => f.FileName.Equals(swapped.FileName, StringComparison.OrdinalIgnoreCase));
        SaveManifest(game, manifest);

        // Nothing is left to point at, so drop the stored original too rather than
        // leaving disk the Storage page would have to explain.
        TryDeleteStoredBackup(game, backupRelative);

        GameAnalyzerService.InvalidateCacheForPath(game.InstallPath);
        Log.Write($"[Swap] {game.Name}: reverted {swapped.FileName} to the game's own build.");
    }

    /// <summary>
    /// Drops records whose file is no longer where it was installed — the game was
    /// moved, verified or reinstalled. Without this, a stale record makes the page claim
    /// a swap that is not there, and blocks re-swapping because the app believes it
    /// already holds the original.
    ///
    /// The stored original is kept: it may still be the only copy of that build, and
    /// deleting a user's game files on a hunch is not a trade worth making.
    /// </summary>
    public int ForgetStaleSwaps(Game game)
    {
        var manifest = SafeManifest(game);
        var stale = manifest.Files
            .Where(f => !File.Exists(Path.Combine(f.InstalledInDirectory, f.FileName)))
            .ToList();

        if (stale.Count == 0) return 0;

        foreach (var f in stale)
        {
            manifest.Files.Remove(f);
            Log.Write($"[Swap] {game.Name}: {f.FileName} is no longer where it was installed; " +
                      "forgetting the swap but keeping the backed-up original.");
        }

        SaveManifest(game, manifest);
        return stale.Count;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The manifest, or an empty one when it cannot be read. Used where the caller is
    /// only describing state: a game's page must still render if the record is corrupt.
    /// The paths that <em>write</em> go through <see cref="LoadManifest"/>, which throws
    /// rather than risk overwriting a backup it cannot see.
    /// </summary>
    private SwapManifest SafeManifest(Game game)
    {
        try { return LoadManifest(game); }
        catch (Exception ex)
        {
            Log.Write($"[Swap] Could not read swap records for {game.Name}: {ex.Message}");
            return new SwapManifest { GameName = game.Name };
        }
    }

    private void TryDeleteStoredBackup(Game game, string backupRelative)
    {
        try
        {
            var stored = Path.Combine(_backupStore.GetFilesDir(game.InstallPath), backupRelative);
            if (File.Exists(stored)) File.Delete(stored);

            var dir = Path.GetDirectoryName(stored);
            if (dir is not null && Directory.Exists(dir)
                && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch (Exception ex)
        {
            Log.Write($"[Swap] Left the stored original in place: {ex.Message}");
        }
    }
}
