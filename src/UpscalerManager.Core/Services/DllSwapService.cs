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

/// <summary>One place a swappable DLL sits in a game, and the version there.</summary>
/// <param name="FsrVersion">
/// The FSR version, for the FidelityFX runtimes whose file version is an SDK build
/// number. Null for every other file.
/// </param>
public sealed record SwapCopy(string Directory, string? Version, string? FsrVersion = null)
{
    /// <summary>The version as it should be shown to a player.</summary>
    public string VersionText => Components.FidelityFxVersion.Describe(FsrVersion, Version);
}

/// <summary>One swappable DLL as it currently stands in a game.</summary>
/// <param name="FileName">The DLL.</param>
/// <param name="Copies">
/// Every directory it was found in. Usually one; more when a game ships the same
/// library in several places, which is common in Unreal titles and is why a swap has
/// to write to all of them rather than the first.
/// </param>
/// <param name="Swapped">The record of this app's swap, when there is one.</param>
/// <param name="Verdict">Whether a swap can be offered for it.</param>
public sealed record SwapSlot(
    string FileName, IReadOnlyList<SwapCopy> Copies, SwappedFile? Swapped, SwapVerdict Verdict)
{
    public SwappableDlls.SwappableDll Definition => SwappableDlls.For(FileName)!;

    /// <summary>True when this app put the current build there and can put it back.</summary>
    public bool IsOurs => Swapped is not null;

    /// <summary>The first directory, for the one-line summary a row shows.</summary>
    public string Directory => Copies.Count > 0 ? Copies[0].Directory : string.Empty;

    /// <summary>The first copy's version, which is what a single-copy game reports.</summary>
    public string? Version => Copies.Count > 0 ? Copies[0].Version : null;

    /// <summary>The first copy's version, written the way a player should read it.</summary>
    public string VersionText => Copies.Count > 0 ? Copies[0].VersionText : "unknown version";

    /// <summary>How many places this DLL sits in.</summary>
    public int CopyCount => Copies.Count;

    /// <summary>
    /// True when the copies are not all the same build. Worth saying out loud: it means
    /// the game has been swapped by something else, or patched unevenly, and whichever
    /// copy it loads decides what the player actually gets.
    /// </summary>
    public bool VersionsDiffer =>
        Copies.Select(c => c.Version ?? string.Empty)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
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
/// Three things this does differently from DLSS Swapper:
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
///
/// And one thing taken from it, because it was simply right: a swap writes to
/// <em>every</em> copy of the DLL in the game, not the first one found. Its
/// <c>UpdateDllAsync</c> loops over all assets of a type; this originally did not, and
/// on a game carrying two copies the swap was a coin toss.
///
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
            var manifest = JsonSerializer.Deserialize(File.ReadAllText(path), OptimizerContext.Default.SwapManifest)
                           ?? new SwapManifest { GameName = game.Name };

            // Records written before swaps could span directories carry their single
            // location in the flat fields. Migrating on read means one code path below,
            // and means an existing user's revert keeps working across this upgrade.
            foreach (var file in manifest.Files) file.Normalize();
            return manifest;
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

        // Group by filename rather than taking the first match. Every directory the
        // file was found in becomes a copy of the same slot: they are one swap, because
        // the game decides which of them it loads and the player cannot.
        var byName = new Dictionary<string, List<SwapCopy>>(StringComparer.OrdinalIgnoreCase);

        foreach (var component in game.DetectedComponents)
        {
            if (SwappableDlls.For(component.FileName) is not { } definition) continue;

            var path = DllLibraryService.ResolveComponentPath(game, component);
            if (path is null) continue;

            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) continue;

            if (!byName.TryGetValue(definition.FileName, out var copies))
                byName[definition.FileName] = copies = new List<SwapCopy>();

            // The same directory can appear twice if the scan reached it by two routes.
            if (copies.Any(c => c.Directory.Equals(dir, StringComparison.OrdinalIgnoreCase))) continue;

            copies.Add(new SwapCopy(dir, component.Version, component.FsrVersion));
        }

        var slots = new List<SwapSlot>();
        foreach (var (fileName, copies) in byName)
        {
            var swapped = manifest.Files.FirstOrDefault(
                f => f.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));

            slots.Add(new SwapSlot(
                SwappableDlls.For(fileName)!.FileName,
                // Shallowest first, so the row shows the copy a player would recognise
                // as "the game's" rather than one buried in an engine subfolder.
                copies.OrderBy(c => c.Directory.Length).ThenBy(c => c.Directory, StringComparer.OrdinalIgnoreCase).ToList(),
                swapped,
                Judge(fileName, swapped, optiScaler)));
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
    /// Puts a library build into the game, backing up whatever is there first — in
    /// every directory the DLL was found in.
    ///
    /// The order matters and is the whole safety story: back up every copy, verify
    /// every backup, and only then overwrite anything. A failure during the backup
    /// phase leaves the game completely untouched; a failure during the write phase
    /// leaves a game whose every copy already has a stored original, so the revert
    /// puts all of it back.
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

        if (slot.Copies.Count == 0)
            throw new InvalidOperationException(
                $"{slot.FileName} is no longer in {game.Name}. Re-scan the game and try again.");

        // A game that is running has its DLLs mapped, and writing over one either fails
        // or is ignored until the game restarts — either way the player is told they
        // swapped something that did not change. Cheaper to refuse and say why.
        if (RunningProcessIn(game.InstallPath) is { } running)
            throw new InvalidOperationException(
                $"{game.Name} looks like it is running ({running}). Close it first — a DLL " +
                "the game already has open cannot be replaced.");

        var manifest = LoadManifest(game);
        var existing = manifest.Files.FirstOrDefault(
            f => f.FileName.Equals(slot.FileName, StringComparison.OrdinalIgnoreCase));
        existing?.Normalize();

        var isFirstSwap = existing is null;
        existing ??= new SwappedFile { FileName = slot.FileName };

        // ── Phase 1: back up every copy we have not already backed up ───────
        foreach (var copy in slot.Copies)
        {
            if (existing.Copies.Any(c => c.Directory.Equals(copy.Directory, StringComparison.OrdinalIgnoreCase)))
                continue;   // already ours; its original is already stored

            var target = Path.Combine(copy.Directory, slot.FileName);
            var record = new SwappedCopy
            {
                Directory = copy.Directory,
                OriginalVersion = copy.Version,
                ExistedBefore = File.Exists(target),
                // The very first copy of a first swap keeps the pre-multi-copy path, so
                // a manifest this version writes is still revertible by an older build.
                BackupRelative = isFirstSwap && existing.Copies.Count == 0
                    ? existing.LegacyBackupRelative
                    : BackupRelativeFor(game, copy.Directory, slot.FileName),
            };

            if (record.ExistedBefore)
            {
                record.OriginalSha256 = FileHash.Sha256(target);

                if (!_backupStore.BackupFile(game.InstallPath, copy.Directory, slot.FileName, record.BackupRelative))
                    throw new IOException(
                        $"Could not back up the game's own {slot.FileName} in {copy.Directory}; " +
                        "nothing was changed.");

                var stored = Path.Combine(_backupStore.GetFilesDir(game.InstallPath), record.BackupRelative);
                if (record.OriginalSha256 is not null && FileHash.Sha256(stored) != record.OriginalSha256)
                    throw new IOException(
                        $"The backup of {slot.FileName} in {copy.Directory} does not match the file it " +
                        "was taken from, so it is not safe to overwrite the original. Nothing was changed.");

                Log.Write($"[Swap] Backed up the game's own {slot.FileName} from {copy.Directory}.");
            }

            existing.Copies.Add(record);
        }

        // ── Phase 2: write every copy ───────────────────────────────────────
        var failures = new List<string>();
        foreach (var record in existing.Copies)
        {
            var target = Path.Combine(record.Directory, slot.FileName);
            try
            {
                File.Copy(build.Path, target, overwrite: true);
                record.InstalledSha256 = FileHash.Sha256(target);
            }
            catch (Exception ex)
            {
                failures.Add($"{record.Directory} ({DescribeWriteFailure(ex, game)})");
                Log.Write($"[Swap] Could not write {slot.FileName} to {record.Directory}: {ex.Message}");
            }
        }

        existing.InstalledVersion = build.Version;
        existing.SourceLabel = build.SourceLabel;
        existing.SwappedAtUtc = DateTime.UtcNow.ToString("o");
        existing.MirrorFirstCopy();

        if (!manifest.Files.Contains(existing)) manifest.Files.Add(existing);
        SaveManifest(game, manifest);

        // The analyzer caches on the game folder's write stamp, and overwriting a file
        // in place does not change it — so without this the game's page would keep
        // reporting the version that was there before the swap.
        GameAnalyzerService.InvalidateCacheForPath(game.InstallPath);

        if (failures.Count == existing.Copies.Count)
            throw new IOException(
                $"{slot.FileName} could not be replaced: {string.Join("; ", failures)}. " +
                "The game's own files are untouched.");

        if (failures.Count > 0)
            throw new IOException(
                $"{slot.FileName} was replaced in {existing.Copies.Count - failures.Count} of " +
                $"{existing.Copies.Count} places, but not in {string.Join("; ", failures)}. " +
                "The game may still load the old build. Reverting puts back what was changed.");

        Log.Write($"[Swap] {game.Name}: {slot.FileName} -> {build.Version} ({build.SourceLabel}) " +
                  $"in {existing.Copies.Count} place(s).");
        return existing;
    }

    /// <summary>
    /// Where one copy's original is stored, relative to the backup store's files
    /// directory. Derived from the copy's location inside the game so two directories
    /// holding the same filename cannot back up over each other — which would destroy
    /// one of the two originals with no way to tell.
    /// </summary>
    private static string BackupRelativeFor(Game game, string directory, string fileName)
    {
        string relative;
        try { relative = Path.GetRelativePath(game.InstallPath, directory); }
        catch { relative = directory; }

        if (relative is "." or "") relative = "_root";

        var slug = new string(relative
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')
            .ToArray())
            .Trim('_', '.');

        if (slug.Length == 0) slug = "_root";
        if (slug.Length > 80) slug = slug[..80];

        return Path.Combine(BackupPrefix, slug, fileName);
    }

    /// <summary>
    /// Why a write into a game folder failed, in terms a player can act on.
    ///
    /// The two that actually happen are a running game holding the file open and a
    /// directory the user cannot write to — a game installed under another account, or
    /// a read-only mount. Both look like an opaque IO error otherwise.
    /// </summary>
    private static string DescribeWriteFailure(Exception ex, Game game) => ex switch
    {
        UnauthorizedAccessException =>
            "no permission to write there — the game folder may belong to another user, "
            + "or be on a read-only filesystem",
        IOException io when RunningProcessIn(game.InstallPath) is { } running =>
            $"the file is in use; {game.Name} appears to be running ({running})",
        IOException io => io.Message,
        _ => ex.Message,
    };

    /// <summary>
    /// The name of a process running out of this game's directory, or null.
    ///
    /// Reads <c>/proc</c> directly rather than shelling out, and treats every failure
    /// as "nothing found": this only ever improves an error message or prevents a
    /// pointless write, so it must never be the thing that breaks a swap.
    ///
    /// Linux only, and deliberately not reimplemented for Windows. There, overwriting a
    /// DLL a process has loaded fails with a sharing violation whose own message
    /// already says the file is in use — so the swap is refused either way, and walking
    /// the process table would add a slow, permission-prone lookup to say the same
    /// thing. On Windows this answers null and the write goes ahead to fail honestly.
    /// </summary>
    internal static string? RunningProcessIn(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists("/proc")) return null;

        string root;
        try { root = Path.GetFullPath(installPath).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return null; }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                var pid = Path.GetFileName(dir);
                if (pid.Length == 0 || !char.IsDigit(pid[0])) continue;

                string? exe;
                // Unreadable /proc/<pid>/exe is normal — another user's process, or one
                // that exited between the listing and the read.
                try { exe = File.ResolveLinkTarget(Path.Combine(dir, "exe"), returnFinalTarget: true)?.FullName; }
                catch { continue; }

                if (exe is null) continue;
                if (!exe.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;

                return Path.GetFileName(exe);
            }
        }
        catch
        {
            // Nothing here is worth failing a swap over.
        }

        return null;
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
        swapped.Normalize();

        if (swapped.Copies.Count == 0)
            throw new InvalidOperationException(
                $"The record of the swap of {swapped.FileName} names no location, so there is " +
                "nothing to put back. Re-scan the game.");

        if (RunningProcessIn(game.InstallPath) is { } running)
            throw new InvalidOperationException(
                $"{game.Name} looks like it is running ({running}). Close it first — a DLL " +
                "the game already has open cannot be replaced.");

        // Check every copy before touching any of them. A partial revert is the worst
        // outcome available here: the game would be left mixing the original in one
        // place with our build in another.
        if (!force)
        {
            foreach (var copy in swapped.Copies)
            {
                var path = Path.Combine(copy.Directory, swapped.FileName);
                if (!File.Exists(path)) continue;
                if (copy.InstalledSha256 is not { Length: > 0 } expected) continue;

                var actual = FileHash.Sha256(path);
                if (actual is not null && actual != expected)
                    throw new InvalidOperationException(
                        $"{swapped.FileName} in {copy.Directory} is no longer the build this app " +
                        "installed — the game has probably been patched, or another tool has " +
                        "replaced it. Restoring the older original over it would undo that change.");
            }
        }

        var manifest = LoadManifest(game);
        var failures = new List<string>();

        foreach (var copy in swapped.Copies)
        {
            var target = Path.Combine(copy.Directory, swapped.FileName);
            try
            {
                if (copy.ExistedBefore)
                {
                    if (!_backupStore.RestoreFile(
                            game.InstallPath, copy.Directory, swapped.FileName, copy.BackupRelative))
                    {
                        failures.Add($"{copy.Directory} (its stored original is missing)");
                        continue;
                    }

                    if (copy.OriginalSha256 is { Length: > 0 } originalHash
                        && FileHash.Sha256(target) is { } restoredHash
                        && restoredHash != originalHash)
                        Log.Write($"[Swap] Restored {swapped.FileName} in {copy.Directory} but its " +
                                  "hash does not match what was recorded.");
                }
                else if (File.Exists(target))
                {
                    // There was no file here before the swap, so putting it back means
                    // removing ours. A game that ships XeSS without the DX11 build is
                    // exactly this case.
                    File.Delete(target);
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{copy.Directory} ({DescribeWriteFailure(ex, game)})");
                Log.Write($"[Swap] Could not revert {swapped.FileName} in {copy.Directory}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            // The record stays, so the copies that did revert are still accounted for
            // and the ones that did not can be tried again.
            GameAnalyzerService.InvalidateCacheForPath(game.InstallPath);
            throw new IOException(
                $"{swapped.FileName} could not be put back in {string.Join("; ", failures)}. " +
                (failures.Count < swapped.Copies.Count
                    ? "The other places were restored."
                    : "Nothing was restored.") +
                (swapped.Copies.Any(c => c.ExistedBefore)
                    ? " The stored originals are kept."
                    : string.Empty));
        }

        var storedPaths = swapped.Copies.Select(c => c.BackupRelative).ToList();

        manifest.Files.RemoveAll(
            f => f.FileName.Equals(swapped.FileName, StringComparison.OrdinalIgnoreCase));
        SaveManifest(game, manifest);

        // Nothing is left to point at, so drop the stored originals too rather than
        // leaving disk the Storage page would have to explain.
        foreach (var stored in storedPaths) TryDeleteStoredBackup(game, stored);

        GameAnalyzerService.InvalidateCacheForPath(game.InstallPath);
        Log.Write($"[Swap] {game.Name}: reverted {swapped.FileName} to the game's own build " +
                  $"in {swapped.Copies.Count} place(s).");
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
        foreach (var file in manifest.Files) file.Normalize();

        // Stale only when no copy survives. One copy of a multi-copy swap disappearing
        // is a game that was partially verified, not a swap that has gone away — the
        // record still describes real files and still holds their originals.
        var stale = manifest.Files
            .Where(f => f.Copies.Count == 0
                        || f.Copies.All(c => !File.Exists(Path.Combine(c.Directory, f.FileName))))
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
