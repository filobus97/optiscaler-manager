// OptiScaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OptiscalerManager.Core.Logging;
using OptiscalerManager.Core.Models;

namespace OptiscalerManager.Core.Services;

/// <summary>
/// How safe an item is to delete. The tiers are not cosmetic: they decide whether the
/// Storage screen offers a delete button at all.
/// </summary>
public enum StorageTier
{
    /// <summary>Downloaded for you and downloadable again — usually.</summary>
    Downloaded,
    /// <summary>A file the user supplied. We hold the only copy.</summary>
    UserImport,
    /// <summary>A backup no game is relying on any more. Free to reclaim.</summary>
    SpentBackup,
    /// <summary>
    /// The original files of a game that still has OptiScaler installed. Deleting this
    /// does not just lose data, it strands the game in its modified state — Revert
    /// restores from here and has nowhere else to look.
    /// </summary>
    LiveBackup,
}

/// <summary>One deletable thing in the app's storage.</summary>
public sealed record StorageItem(
    StorageTier Tier,
    string Group,
    string Label,
    string Path,
    long Bytes,
    string? Note = null,
    string? GameDirectory = null)
{
    /// <summary>Everything except a live backup, which has to be reverted first.</summary>
    public bool CanDelete => Tier != StorageTier.LiveBackup;
}

/// <summary>
/// Takes stock of everything the app has written to disk: component versions it
/// downloaded, DLLs the user imported, and per-game backups.
///
/// Nothing here prunes itself — every version ever fetched is kept — so this is what
/// the Storage screen reads to let the user reclaim the space deliberately.
/// </summary>
public sealed class StorageInventoryService
{
    private readonly string _baseDir;
    private readonly string _cacheDir;
    private readonly AppConfiguration _config;

    /// <summary>Component folders that hold one subdirectory per downloaded version.</summary>
    private static readonly (string Folder, string Group)[] VersionedComponents =
    {
        ("OptiScaler", "OptiScaler"),
        ("Extras", "FSR 4 INT8 (Extras)"),
        ("OptiPatcher", "OptiPatcher"),
        ("Fakenvapi", "fakenvapi"),
        ("NukemFG", "Nukem frame generation"),
    };

    /// <summary>Subdirectories of Cache/OptiScaler that are not versions.</summary>
    private static readonly HashSet<string> NotVersionFolders =
        new(StringComparer.OrdinalIgnoreCase) { "D3D12_Optiscaler", "DlssOverrides", "Licenses" };

    public StorageInventoryService(AppConfiguration config)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _baseDir = Path.Combine(appData, "OptiscalerManager");
        _cacheDir = Path.Combine(_baseDir, "Cache");
        _config = config;
    }

    public IReadOnlyList<StorageItem> Scan()
    {
        var items = new List<StorageItem>();
        items.AddRange(ScanDownloadedComponents());
        items.AddRange(ScanUserImports());
        items.AddRange(ScanBackups());
        return items;
    }

    private IEnumerable<StorageItem> ScanDownloadedComponents()
    {
        foreach (var (folder, group) in VersionedComponents)
        {
            var root = Path.Combine(_cacheDir, folder);
            if (!Directory.Exists(root)) continue;

            foreach (var dir in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (folder == "OptiScaler" && NotVersionFolders.Contains(name)) continue;

                // A version the user imported by hand lives in the same folder but is
                // not re-downloadable, so it belongs with their other imports.
                var isImported = folder == "OptiScaler" &&
                                 _config.CustomOptiScalerVersions.Contains(name, StringComparer.OrdinalIgnoreCase);

                yield return isImported
                    ? new StorageItem(StorageTier.UserImport, "Imported OptiScaler builds", name, dir, DirectorySize(dir))
                    : new StorageItem(StorageTier.Downloaded, group, name, dir, DirectorySize(dir));
            }
        }
    }

    private IEnumerable<StorageItem> ScanUserImports()
    {
        var dllDir = Path.Combine(_cacheDir, "CustomDlls");
        if (Directory.Exists(dllDir))
        {
            foreach (var file in Directory.GetFiles(dllDir, "*.dll"))
                yield return new StorageItem(StorageTier.UserImport, "Custom DLLs",
                    Path.GetFileName(file), file, FileSize(file));
        }

        foreach (var (folder, group) in new[]
                 {
                     ("CustomFsr4", "Imported FSR 4 DLLs"),
                     ("CustomFsrSdk", "Imported FSR SDK packages"),
                 })
        {
            var root = Path.Combine(_cacheDir, folder);
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root))
                yield return new StorageItem(StorageTier.UserImport, group,
                    Path.GetFileName(dir), dir, DirectorySize(dir));
        }
    }

    /// <summary>
    /// Backups, split by whether anything still depends on them. A backup is live while
    /// its game still has OptiScaler in place; once the game has been reverted — or has
    /// gone from disk entirely — the backup is just occupying space.
    /// </summary>
    private IEnumerable<StorageItem> ScanBackups()
    {
        var root = Path.Combine(_baseDir, "Backups");
        if (!Directory.Exists(root)) yield break;

        foreach (var dir in Directory.GetDirectories(root))
        {
            var manifest = LoadManifest(dir);
            var gameDir = manifest?.InstalledGameDirectory;
            var label = gameDir is not null ? DescribeGame(gameDir) : Path.GetFileName(dir);
            var size = DirectorySize(dir);

            if (gameDir is null || !Directory.Exists(gameDir))
            {
                yield return new StorageItem(StorageTier.SpentBackup, "Backups", label, dir, size,
                    gameDir is null ? "The game folder is unknown." : "The game folder is gone.");
                continue;
            }

            // Still installed? Then Revert needs these files. The committed manifest we
            // just read is the authority — re-deriving the backup's location from the
            // game path would only risk disagreeing with the folder we are looking at.
            var committed = string.Equals(manifest?.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase);
            if (committed && GameStillHasOptiScaler(gameDir))
            {
                yield return new StorageItem(StorageTier.LiveBackup, "Backups", label, dir, size,
                    "This game still has OptiScaler installed.", gameDir);
            }
            else
            {
                yield return new StorageItem(StorageTier.SpentBackup, "Backups", label, dir, size,
                    "This game has already been reverted.", gameDir);
            }
        }
    }

    /// <summary>
    /// OptiScaler's own DLL being present is what makes a backup live. Checking the file
    /// rather than trusting the manifest matters: a player can remove a mod by hand, and
    /// then the backup really is spent.
    /// </summary>
    private static bool GameStillHasOptiScaler(string gameDir)
    {
        try
        {
            return File.Exists(Path.Combine(gameDir, "OptiScaler.dll"))
                || File.Exists(Path.Combine(gameDir, "OptiScaler.ini"));
        }
        catch { return false; }
    }

    private static InstallationManifest? LoadManifest(string backupDir)
    {
        var path = Path.Combine(backupDir, "manifest.json");
        if (!File.Exists(path)) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize(
                File.ReadAllText(path), OptimizerContext.Default.InstallationManifest);
        }
        catch { return null; }
    }

    /// <summary>
    /// Folder names that identify a build layout rather than a game. OptiScaler is often
    /// installed into a nested engine folder, so the innermost segment of a recorded
    /// directory is frequently "Win64" — naming a backup "bin/x64" tells the user
    /// nothing about which game it belongs to.
    /// </summary>
    private static readonly HashSet<string> BuildFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "bin64", "binaries", "win64", "wingdk", "x64", "x86", "retail", "shipping",
        "game", "engine", "build", "release", "debug",
    };

    /// <summary>The innermost folder that names the game rather than the build layout.</summary>
    private static string DescribeGame(string gameDir)
    {
        var parts = gameDir.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return gameDir;

        for (var i = parts.Length - 1; i >= 0; i--)
            if (!BuildFolderNames.Contains(parts[i]))
                return parts[i];

        return parts[^1];
    }

    /// <summary>Deletes one item. Live backups are refused — the game must be reverted first.</summary>
    public bool Delete(StorageItem item)
    {
        if (!item.CanDelete)
        {
            Log.Write($"[Storage] Refused to delete a backup still in use: {item.Label}");
            return false;
        }

        try
        {
            if (Directory.Exists(item.Path)) Directory.Delete(item.Path, recursive: true);
            else if (File.Exists(item.Path)) File.Delete(item.Path);
            else return false;

            Log.Write($"[Storage] Deleted {item.Group} / {item.Label} ({FormatSize(item.Bytes)}).");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"[Storage] Could not delete '{item.Path}': {ex.Message}");
            return false;
        }
    }

    private static long FileSize(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static long DirectorySize(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => { try { return f.Length; } catch { return 0L; } });
        }
        catch { return 0; }
    }

    /// <summary>Sizes for humans: "412 MB", "1.3 GB".</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        string[] units = { "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = -1;
        do { value /= 1024; unit++; } while (value >= 1024 && unit < units.Length - 1);
        return value >= 100 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}
