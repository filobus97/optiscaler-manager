// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.

using System.Text.Json;
using UpscalerManager.Core.Components;
using UpscalerManager.Core.Logging;
using UpscalerManager.Core.Models;

namespace UpscalerManager.Core.Services;

/// <summary>
/// Puts FSR 4 into a game that uses AMD's FidelityFX runtime by replacing the game's own
/// files, and takes it out again — as one unit.
///
/// A set of files that only work together is not a swap of one DLL, so this keeps its
/// own record beside the swap record: every file it overwrote has its original in the
/// per-game backup store, every file it created is listed, and Undo restores the lot.
/// The generation guard on single-file swaps is untouched; this always writes a
/// consistent set. See <see cref="Fsr4SwapPlanner"/> and docs/fidelityfx.md.
/// </summary>
public sealed class Fsr4SwapService
{
    public const string ManifestFileName = "fsr4swap.json";

    /// <summary>Where the originals go inside the per-game backup store, clear of the single-file swaps'.</summary>
    private const string BackupPrefix = "fsr4swap";

    private readonly BackupStoreService _backupStore = new();
    private readonly DllSwapService _swaps = new();

    // ── Where the record lives ───────────────────────────────────────────────

    private string ManifestPath(Game game) =>
        Path.Combine(_backupStore.GetBackupRoot(game.InstallPath), ManifestFileName);

    /// <summary>The record, or null when there is none. Throws when it exists and cannot be read.</summary>
    public Fsr4SwapManifest? Load(Game game)
    {
        var path = ManifestPath(game);
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), OptimizerContext.Default.Fsr4SwapManifest);
        }
        catch (Exception ex)
        {
            // Not "nothing was set up": writing over a game whose originals this record
            // names would lose them.
            throw new InvalidOperationException(
                $"The record of the FSR 4 files in {game.Name} could not be read ({ex.Message}). "
                + $"It is at {path}; the original files are in the same folder under '{BackupPrefix}'.", ex);
        }
    }

    /// <summary>The record for describing state: null when there is none or it cannot be read.</summary>
    public Fsr4SwapManifest? Current(Game game)
    {
        try { return Load(game) is { Files.Count: > 0 } m ? m : null; }
        catch (Exception ex)
        {
            Log.Write($"[Fsr4] {ex.Message}");
            return null;
        }
    }

    /// <summary>True when this app has FSR 4 files in the game.</summary>
    public bool IsActive(Game game) => Current(game) is not null;

    /// <summary>
    /// Whether the given game directory has an FSR 4 record, for the Storage page, which
    /// works from backup folders and must not offer to delete originals this depends on.
    /// An unreadable record counts: it may still point at the only copy of a file.
    /// </summary>
    public static bool HasRecordForDirectory(string gameDir)
    {
        var path = Path.Combine(new BackupStoreService().GetBackupRoot(gameDir), ManifestFileName);
        if (!File.Exists(path)) return false;
        try
        {
            var manifest = JsonSerializer.Deserialize(File.ReadAllText(path), OptimizerContext.Default.Fsr4SwapManifest);
            return manifest is { Files.Count: > 0 };
        }
        catch { return true; }
    }

    /// <summary>The names of the files this route wrote in a game, for the single-file swap list to leave alone.</summary>
    internal static IReadOnlySet<string> WrittenNames(string gameDir)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(new BackupStoreService().GetBackupRoot(gameDir), ManifestFileName);
            if (!File.Exists(path)) return names;
            var manifest = JsonSerializer.Deserialize(File.ReadAllText(path), OptimizerContext.Default.Fsr4SwapManifest);
            foreach (var file in manifest?.Files ?? new()) names.Add(file.FileName);
        }
        catch { /* describing state only */ }
        return names;
    }

    private void Save(Game game, Fsr4SwapManifest manifest)
    {
        Directory.CreateDirectory(_backupStore.GetBackupRoot(game.InstallPath));
        var path = ManifestPath(game);
        var tmp = path + ".tmp";
        manifest.GameName = game.Name;

        // Written aside and moved into place: a half-written record of which files have
        // backups is worse than none.
        File.WriteAllText(tmp, JsonSerializer.Serialize(manifest, OptimizerContext.Default.Fsr4SwapManifest));
        File.Move(tmp, path, overwrite: true);
    }

    private void Forget(Game game)
    {
        var path = ManifestPath(game);
        if (File.Exists(path)) File.Delete(path);
    }

    // ── What can be done here ────────────────────────────────────────────────

    /// <summary>Every FidelityFX DX12 file in the game, found by contents as well as by name.</summary>
    public static IReadOnlyList<FidelityFxFile> Scan(Game game) => FidelityFxFiles.Scan(game.InstallPath);

    /// <summary>True when OptiScaler is installed in the game, which brings FidelityFX files of its own.</summary>
    public bool OptiScalerInstalled(Game game)
    {
        if (game.IsOptiscalerInstalled) return true;
        try
        {
            if (!string.IsNullOrEmpty(game.ExecutablePath)
                && Path.GetDirectoryName(game.ExecutablePath) is { Length: > 0 } exeDir
                && _backupStore.HasValidBackup(exeDir))
                return true;
            return !string.IsNullOrEmpty(game.InstallPath) && _backupStore.HasValidBackup(game.InstallPath);
        }
        catch { return false; }
    }

    /// <summary>
    /// What stands in the way of setting this up now, in terms a player can act on — or
    /// null when nothing does. Every one of these is checked again when the files are written.
    /// </summary>
    public string? Refusal(Game game)
    {
        try
        {
            if (Load(game) is { Files.Count: > 0 })
                return $"FSR 4 is already set up in {game.Name}. Undo it first to pick another build.";
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }

        if (OptiScalerInstalled(game))
            return $"OptiScaler is installed in {game.Name}, and it brings FidelityFX files of its own. "
                   + "Remove OptiScaler first, or pick FSR 4 in its install options instead.";

        try
        {
            var swapped = _swaps.LoadManifest(game).Files
                .Where(f => FidelityFxLayout.IsFidelityFx(f.FileName))
                .Select(f => f.FileName)
                .ToList();
            if (swapped.Count > 0)
                return $"{string.Join(", ", swapped)} was swapped from the upscaler libraries list. "
                       + "Put it back there first, so the game's own file is not lost.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        if (DllSwapService.RunningProcessIn(game.InstallPath) is { } running)
            return $"{game.Name} looks like it is running ({running}). Close it first — a DLL "
                   + "the game already has open cannot be replaced.";

        return null;
    }

    // ── Where the files come from ────────────────────────────────────────────

    /// <summary>
    /// The FSR 4 upscaler a DLL is, read from its exports and its version — or the reason
    /// it cannot be one. A driver-style build (amdxcffx64.dll) needs a host to load it, and
    /// an SDK 2 loader exports the same five functions as an upscaler, so the name alone
    /// is not enough: a file under an unfamiliar name needs an FSR version table.
    /// </summary>
    public static (FidelityFxFile? Upscaler, string? Reason) ReadUpscaler(string path)
    {
        switch (Omniloader.Classify(path))
        {
            case OmniBuildKind.DriverStyle:
                return (null, "it is a driver-style build (amdxcffx64.dll), which needs OptiScaler to load it");
            case OmniBuildKind.NotUpscaler:
                return (null, "it is not an FSR upscaler");
        }

        var pe = PeFileInspector.Inspect(path);
        if (!pe.IsValidPe || !pe.Is64Bit) return (null, "it is not a 64-bit DLL");

        var effect = FidelityFxVersion.FromBinary(path);
        var byName = FidelityFxFiles.Identify(path)?.Role == FidelityFxRole.Upscaler;
        if (!byName && effect is null)
            return (null, "it carries no FSR version table, so it cannot be told apart from an SDK 2 loader");

        return (new FidelityFxFile(path, FidelityFxRole.Upscaler, DllLibraryService.VersionOf(pe), effect), null);
    }

    /// <summary>
    /// The SDK 2 loader and frame generation module among some DLLs that come from one
    /// place, or null for what is not there. The frame generation module is taken from the
    /// loader's own set only: pairing it with another set's loader is a guess.
    /// </summary>
    public static (FidelityFxFile? Loader, FidelityFxFile? FrameGeneration) FindCompanions(IEnumerable<string> dlls) =>
        FindCompanions(dlls.Select(FidelityFxFiles.Identify).OfType<FidelityFxFile>());

    /// <summary>The same, for files that have already been identified.</summary>
    public static (FidelityFxFile? Loader, FidelityFxFile? FrameGeneration) FindCompanions(IEnumerable<FidelityFxFile> identified)
    {
        var files = identified.ToList();
        var loader = files.FirstOrDefault(f => f.Role == FidelityFxRole.Loader);
        return loader is null
            ? (null, null)
            : (loader, files.FirstOrDefault(f => f.Role == FidelityFxRole.FrameGeneration));
    }

    // ── The plan ─────────────────────────────────────────────────────────────

    /// <summary>What setting up FSR 4 would do to this game, file by file.</summary>
    public Fsr4Plan Plan(Game game, Fsr4Sources sources, IReadOnlyList<FidelityFxFile>? gameFiles = null) =>
        Fsr4SwapPlanner.Plan(game.InstallPath, gameFiles ?? Scan(game), sources);

    // ── Doing it ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the files in <paramref name="plan"/>, backing up whatever they replace.
    ///
    /// The order is the whole safety story: back up every file and verify each backup,
    /// write the record, and only then overwrite anything. If a write fails, everything
    /// written so far is put back at once — half a set (a loader with no module behind
    /// it) is worse than none.
    /// </summary>
    public Fsr4SwapManifest Apply(Game game, Fsr4Plan plan, Fsr4Sources sources)
    {
        if (plan.Refusal is { } refused) throw new InvalidOperationException(refused);
        if (Refusal(game) is { } blocked) throw new InvalidOperationException(blocked);

        foreach (var step in plan.Steps)
            if (step.SourcePath.Length == 0 || !File.Exists(step.SourcePath))
                throw new FileNotFoundException(
                    $"The file for {step.FileName} is not available yet. Nothing was changed.", step.SourcePath);

        var duplicate = plan.Steps
            .GroupBy(s => Path.Combine(s.Directory, s.FileName), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"{duplicate.Key} would be written twice. Nothing was changed.");

        var manifest = new Fsr4SwapManifest
        {
            GameName = game.Name,
            BuildLabel = sources.Label,
            BuildVersion = sources.UpscalerVersion ?? string.Empty,
            LosesFrameGeneration = plan.LosesFrameGeneration,
            AppliedAtUtc = DateTime.UtcNow.ToString("o"),
        };

        // ── Phase 1: back up everything that is there, and check each backup ────
        try
        {
            foreach (var step in plan.Steps)
            {
                var target = Path.Combine(step.Directory, step.FileName);
                var record = new Fsr4SwapFile
                {
                    Directory = step.Directory,
                    FileName = step.FileName,
                    Kind = KindOf(step.Kind),
                    ExistedBefore = File.Exists(target),
                    InstalledSha256 = FileHash.Sha256(step.SourcePath),
                    BackupRelative = DllSwapService.BackupRelativeFor(game, step.Directory, step.FileName, BackupPrefix),
                };

                if (record.ExistedBefore)
                {
                    record.OriginalSha256 = FileHash.Sha256(target);
                    record.OriginalVersion = DllLibraryService.VersionOf(PeFileInspector.Inspect(target));

                    if (!_backupStore.BackupFile(game.InstallPath, step.Directory, step.FileName, record.BackupRelative))
                        throw new IOException(
                            $"Could not back up the game's own {step.FileName} in {step.Directory}; nothing was changed.");

                    var stored = Path.Combine(_backupStore.GetFilesDir(game.InstallPath), record.BackupRelative);
                    if (record.OriginalSha256 is not null && FileHash.Sha256(stored) != record.OriginalSha256)
                        throw new IOException(
                            $"The backup of {step.FileName} in {step.Directory} does not match the file it was "
                            + "taken from, so it is not safe to overwrite the original. Nothing was changed.");
                }

                manifest.Files.Add(record);
            }

            // Written before the first overwrite, so a crash halfway leaves a record
            // that Undo can finish from.
            Save(game, manifest);
        }
        catch
        {
            foreach (var done in manifest.Files.Where(f => f.ExistedBefore))
                _swaps.TryDeleteStoredBackup(game, done.BackupRelative);
            Forget(game);
            throw;
        }

        // ── Phase 2: write ──────────────────────────────────────────────────────
        try
        {
            foreach (var (step, record) in plan.Steps.Zip(manifest.Files))
            {
                var target = Path.Combine(step.Directory, step.FileName);
                File.Copy(step.SourcePath, target, overwrite: true);
                if (FileHash.Sha256(target) != record.InstalledSha256)
                    throw new IOException($"{step.FileName} in {step.Directory} did not copy correctly.");
            }
        }
        catch (Exception ex)
        {
            Log.Write($"[Fsr4] {game.Name}: writing failed ({ex.Message}); putting everything back.");
            string put;
            try
            {
                var failures = RestoreFiles(game, manifest.Files);
                if (failures.Count == 0)
                {
                    DropBackups(game, manifest.Files);
                    Forget(game);
                    put = "Everything that was written has been put back.";
                }
                else
                {
                    put = $"Some files could not be put back ({string.Join("; ", failures)}); "
                          + "Undo on the game's page tries again.";
                }
            }
            catch (Exception rollback)
            {
                put = $"Putting the files back failed too ({rollback.Message}); Undo on the game's page tries again.";
            }

            GameAnalyzerService.InvalidateCacheForPath(game.InstallPath);
            throw new IOException($"FSR 4 could not be written: {DescribeWriteFailure(ex, game)}. {put}", ex);
        }

        GameAnalyzerService.InvalidateCacheForPath(game.InstallPath);
        Log.Write($"[Fsr4] {game.Name}: {sources.Label} {sources.UpscalerVersion} written to "
                  + $"{manifest.Files.Count} file(s), {(plan.LosesFrameGeneration ? "without" : "with")} frame generation.");
        return manifest;
    }

    private static string KindOf(Fsr4StepKind kind) => kind switch
    {
        Fsr4StepKind.ReplaceRuntimeWithLoader => "loader",
        Fsr4StepKind.AddFrameGeneration => "frame generation",
        _ => "upscaler",
    };

    private static string DescribeWriteFailure(Exception ex, Game game) => ex switch
    {
        UnauthorizedAccessException =>
            "no permission to write there — the game folder may belong to another user, or be on a read-only filesystem",
        IOException when DllSwapService.RunningProcessIn(game.InstallPath) is { } running =>
            $"the file is in use; {game.Name} appears to be running ({running})",
        _ => ex.Message,
    };

    // ── Undoing it ───────────────────────────────────────────────────────────

    /// <summary>
    /// True when a file is still what this app wrote — or still what was there before,
    /// which is how a write that stopped halfway looks. A missing file counts too: there
    /// is nothing of anybody else's to overwrite.
    /// </summary>
    private static bool StillOurs(Fsr4SwapFile file)
    {
        var path = Path.Combine(file.Directory, file.FileName);
        if (!File.Exists(path)) return true;
        if (file.InstalledSha256 is not { Length: > 0 } expected) return true;

        var actual = FileHash.Sha256(path);
        return actual is null || actual == expected || actual == file.OriginalSha256;
    }

    /// <summary>Whether the files this app wrote are still the ones in the game.</summary>
    public static DllSwapService.SwapStanding StandingOf(Fsr4SwapManifest? manifest)
    {
        if (manifest is null || manifest.Files.Count == 0) return DllSwapService.SwapStanding.NotOurs;

        var ours = manifest.Files.Count(StillOurs);
        return ours == manifest.Files.Count ? DllSwapService.SwapStanding.Intact
            : ours == 0 ? DllSwapService.SwapStanding.Replaced
            : DllSwapService.SwapStanding.PartlyReplaced;
    }

    /// <summary>
    /// Puts the game's own files back and removes the ones this route created.
    ///
    /// Refuses when a file is no longer what this app wrote: a game patch or another tool
    /// has been through, and restoring an older original over it would undo their work.
    /// <paramref name="force"/> is for a user who has been told that.
    /// </summary>
    public void Revert(Game game, bool force = false)
    {
        var manifest = Load(game);
        if (manifest is null || manifest.Files.Count == 0)
            throw new InvalidOperationException($"There is no FSR 4 setup in {game.Name} to undo.");

        RefuseWhileRunning(game);

        // Every file is checked before any is touched: a partial undo would leave a loader
        // from one set beside a module from another.
        if (!force)
            foreach (var file in manifest.Files.Where(f => !StillOurs(f)))
                throw new InvalidOperationException(
                    $"{file.FileName} in {file.Directory} is no longer the file this app wrote — the game "
                    + "has probably been patched, or another tool has replaced it. Restoring the older "
                    + "original over it would undo that change.");

        var failures = RestoreFiles(game, manifest.Files);
        if (failures.Count > 0)
        {
            GameAnalyzerService.InvalidateCacheForPath(game.InstallPath);
            throw new IOException(
                $"FSR 4 could not be fully undone: {string.Join("; ", failures)}. The record is kept, so "
                + "Undo can be tried again, and the stored originals are kept.");
        }

        Forget(game);
        DropBackups(game, manifest.Files);
        GameAnalyzerService.InvalidateCacheForPath(game.InstallPath);
        Log.Write($"[Fsr4] {game.Name}: put the game's own files back ({manifest.Files.Count} file(s)).");
    }

    /// <summary>
    /// Restores every file this app still owns, leaves any that something else has
    /// replaced, and clears the record either way. The way out once a game has patched a
    /// file: an ordinary undo refuses then, correctly, and would otherwise be a dead end.
    /// </summary>
    /// <returns>The paths left alone, for the caller to report.</returns>
    public IReadOnlyList<string> RevertWhatRemains(Game game)
    {
        var manifest = Load(game);
        if (manifest is null || manifest.Files.Count == 0)
            throw new InvalidOperationException($"There is no FSR 4 setup in {game.Name} to undo.");

        RefuseWhileRunning(game);

        var ours = manifest.Files.Where(StillOurs).ToList();
        var replaced = manifest.Files.Where(f => !StillOurs(f)).ToList();

        var failures = RestoreFiles(game, ours);
        if (failures.Count > 0)
        {
            GameAnalyzerService.InvalidateCacheForPath(game.InstallPath);
            throw new IOException(
                $"FSR 4 could not be fully undone: {string.Join("; ", failures)}. The record is kept.");
        }

        Forget(game);
        // The originals of the files left alone are kept: they may be the only copy of
        // that build, and the Storage page can remove them as spent backups.
        DropBackups(game, ours);
        GameAnalyzerService.InvalidateCacheForPath(game.InstallPath);
        Log.Write($"[Fsr4] {game.Name}: forgot the FSR 4 setup; {replaced.Count} file(s) had been replaced since and were left alone.");
        return replaced.Select(f => Path.Combine(f.Directory, f.FileName)).ToList();
    }

    /// <summary>
    /// Drops the record when none of its files is in the game any more — it was moved,
    /// verified or reinstalled. The stored originals stay.
    /// </summary>
    public bool ForgetStale(Game game)
    {
        var manifest = Current(game);
        if (manifest is null) return false;
        if (manifest.Files.Any(f => File.Exists(Path.Combine(f.Directory, f.FileName)))) return false;

        Forget(game);
        Log.Write($"[Fsr4] {game.Name}: the FSR 4 files are gone from the game; forgetting the record.");
        return true;
    }

    private static void RefuseWhileRunning(Game game)
    {
        if (DllSwapService.RunningProcessIn(game.InstallPath) is { } running)
            throw new InvalidOperationException(
                $"{game.Name} looks like it is running ({running}). Close it first — a DLL "
                + "the game already has open cannot be replaced.");
    }

    /// <summary>Puts each file back, or removes it when nothing was there before. Returns what failed.</summary>
    private List<string> RestoreFiles(Game game, IEnumerable<Fsr4SwapFile> files)
    {
        var failures = new List<string>();
        foreach (var file in files)
        {
            var target = Path.Combine(file.Directory, file.FileName);
            try
            {
                if (file.ExistedBefore)
                {
                    if (!_backupStore.RestoreFile(game.InstallPath, file.Directory, file.FileName, file.BackupRelative))
                    {
                        failures.Add($"{file.FileName} in {file.Directory} (its stored original is missing)");
                        continue;
                    }

                    if (file.OriginalSha256 is { Length: > 0 } hash && FileHash.Sha256(target) is { } restored && restored != hash)
                        Log.Write($"[Fsr4] Restored {file.FileName} in {file.Directory} but its hash does not match what was recorded.");
                }
                else if (File.Exists(target))
                {
                    File.Delete(target);
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{file.FileName} in {file.Directory} ({DescribeWriteFailure(ex, game)})");
                Log.Write($"[Fsr4] Could not put back {file.FileName} in {file.Directory}: {ex.Message}");
            }
        }
        return failures;
    }

    private void DropBackups(Game game, IEnumerable<Fsr4SwapFile> files)
    {
        foreach (var file in files.Where(f => f.ExistedBefore))
            _swaps.TryDeleteStoredBackup(game, file.BackupRelative);
    }
}
