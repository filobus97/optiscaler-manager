// OptiScaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.IO;
using OptiscalerManager.Core.Logging;

namespace OptiscalerManager.Core.Services;

/// <summary>
/// Where the app keeps everything it owns: settings, download caches, per-game backups
/// and .ini profiles.
///
/// The folder is named after the product, so the name lives here once rather than being
/// spelled out at each use. That matters because the app is being renamed, and a
/// renamed folder is not a cosmetic change: <c>Backups/</c> holds the only copy of the
/// original files of every modded game. Point the app at a fresh directory and
/// <see cref="BackupStoreService.HasValidBackup"/> simply answers "no" for every game —
/// removing OptiScaler would quietly lose the files it is supposed to restore.
///
/// <see cref="MigrateIfNeeded"/> exists so that can never happen: the old directory is
/// moved into place before anything reads from it.
/// </summary>
public static class AppDataPaths
{
    /// <summary>The current app-data folder name.</summary>
    public const string FolderName = "OptiscalerManager";

    /// <summary>
    /// Folder names this app used previously, newest first. A rename adds the outgoing
    /// name here; the first one that exists is moved to <see cref="FolderName"/>.
    /// </summary>
    private static readonly string[] PreviousFolderNames = Array.Empty<string>();

    /// <summary>
    /// The app-data root, e.g. <c>~/.config/OptiscalerManager</c>. Reading this performs
    /// the one-time migration if an older folder is still the one holding the data.
    /// </summary>
    public static string Root
    {
        get
        {
            var root = Path.Combine(BaseDirectory(), FolderName);
            MigrateIfNeeded(BaseDirectory(), root, PreviousFolderNames);
            return root;
        }
    }

    /// <summary>
    /// The per-user configuration directory.
    ///
    /// <see cref="Environment.SpecialFolder.ApplicationData"/> comes back <em>empty</em>
    /// when XDG_CONFIG_HOME names a directory that does not exist — and an empty base
    /// turns every path below into a relative one, which would scatter settings and
    /// backups into whatever directory the app happened to be launched from. Fall back
    /// to the documented default instead.
    /// </summary>
    private static string BaseDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData)) return appData;

        var configured = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(configured) && Path.IsPathRooted(configured)) return configured;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? Path.Combine(Path.GetTempPath(), ".config") : Path.Combine(home, ".config");
    }

    /// <summary>Downloaded components and imported files.</summary>
    public static string Cache => Path.Combine(Root, "Cache");

    /// <summary>Per-game original files, keyed by a slug of the game directory.</summary>
    public static string Backups => Path.Combine(Root, "Backups");

    /// <summary>Built-in and user-authored OptiScaler.ini profiles.</summary>
    public static string Profiles => Path.Combine(Root, "Profiles");

    /// <summary>
    /// Moves the app-data folder over from a previous product name. Only ever runs when
    /// the current folder does not exist yet, so it can neither merge two sets of state
    /// nor overwrite anything.
    /// </summary>
    /// <summary>True when the directory exists and holds anything at all.</summary>
    private static bool HasContent(string path)
    {
        try { return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).GetEnumerator().MoveNext(); }
        catch { return true; }   // unreadable: leave it alone rather than move onto it
    }

    internal static void MigrateIfNeeded(
        string appData, string root, System.Collections.Generic.IReadOnlyList<string> previousNames)
    {
        // Naturally idempotent, and deliberately not memoised: once the folder holds
        // data the check below is a couple of stats and stops. Caching "already tried"
        // instead made the outcome depend on whether something happened to resolve the
        // path earlier in the process, which is not a thing this should be sensitive to.
        //
        // An *empty* folder does not count as already-migrated. Services create their
        // directories on construction, so the new root can easily exist while holding
        // nothing — and treating that as "done" would strand the real data under the old
        // name permanently. Nothing can be lost by moving into an empty folder.
        if (previousNames.Count == 0 || HasContent(root)) return;

        foreach (var previous in previousNames)
        {
            var old = Path.Combine(appData, previous);
            if (!Directory.Exists(old)) continue;

            try
            {
                // Directory.Move refuses an existing target, so clear the empty shell first.
                if (Directory.Exists(root)) Directory.Delete(root, recursive: false);
                Directory.Move(old, root);
                Log.Write($"[AppData] Moved '{previous}' to '{FolderName}' — settings, caches and backups carried over.");
            }
            catch (Exception ex)
            {
                // Never fatal: a failed move leaves the old folder untouched, and the
                // app starts empty rather than refusing to run. Saying so matters,
                // because the symptom (games no longer revertible) is otherwise silent.
                Log.Write($"[AppData] Could not move '{previous}' to '{FolderName}': {ex.Message}. " +
                          $"The previous data is still at '{old}'.");
            }
            return;
        }
    }
}
