// OptiScaler Manager - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System;
using System.IO;
using OptiscalerManager.Core.Models;
using OptiscalerManager.Core.Services;
using Xunit;

namespace OptiscalerManager.Core.Tests
{
    /// <summary>
    /// Renaming the app renames the folder it keeps everything in, and that folder holds
    /// the only copy of every modded game's original files. If the move does not happen,
    /// nothing errors — the app just starts empty and every game silently becomes
    /// unrevertible. These pin the move, and the reason it exists.
    /// </summary>
    [Collection(AppDataCollection.Name)]
    public class AppDataMigrationTests : IDisposable
    {
        private readonly string _appData = Path.Combine(Path.GetTempPath(), "osm_mig_" + Guid.NewGuid().ToString("N"));

        public AppDataMigrationTests() => Directory.CreateDirectory(_appData);
        public void Dispose() { try { Directory.Delete(_appData, true); } catch { } }

        private string Old(string name) => Path.Combine(_appData, name);
        private string New => Path.Combine(_appData, "NewName");

        private void SeedOldInstall(string folder, string marker = "original bytes")
        {
            var backup = Path.Combine(Old(folder), "Backups", "some_game_1a2b3c4d", "files");
            Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(backup, "nvngx_dlss.dll"), marker);
            Directory.CreateDirectory(Path.Combine(Old(folder), "Cache"));
            File.WriteAllText(Path.Combine(Old(folder), "config.json"), "{}");
        }

        [Fact]
        public void TheOldFolderIsMovedAcrossWithItsBackups()
        {
            SeedOldInstall("OldName");

            AppDataPaths.MigrateIfNeeded(_appData, New, new[] { "OldName" });

            Assert.False(Directory.Exists(Old("OldName")));
            Assert.Equal("original bytes",
                File.ReadAllText(Path.Combine(New, "Backups", "some_game_1a2b3c4d", "files", "nvngx_dlss.dll")));
            Assert.True(File.Exists(Path.Combine(New, "config.json")));
        }

        [Fact]
        public void AnExistingNewFolderIsNeverOverwritten()
        {
            // Two sets of state must never be merged: whatever is already at the new
            // name wins, and the old folder is left alone for the user to inspect.
            SeedOldInstall("OldName", "the old one");
            Directory.CreateDirectory(New);
            File.WriteAllText(Path.Combine(New, "config.json"), "the new one");

            AppDataPaths.MigrateIfNeeded(_appData, New, new[] { "OldName" });

            Assert.Equal("the new one", File.ReadAllText(Path.Combine(New, "config.json")));
            Assert.True(Directory.Exists(Old("OldName")));
        }

        [Fact]
        public void TheNewestPreviousNameWins()
        {
            SeedOldInstall("Newer", "newer");
            SeedOldInstall("Older", "older");

            AppDataPaths.MigrateIfNeeded(_appData, New, new[] { "Newer", "Older" });

            Assert.Equal("newer",
                File.ReadAllText(Path.Combine(New, "Backups", "some_game_1a2b3c4d", "files", "nvngx_dlss.dll")));
            Assert.True(Directory.Exists(Old("Older")));   // untouched, not deleted
        }

        [Fact]
        public void NothingHappensWhenThereIsNoPreviousInstall()
        {
            AppDataPaths.MigrateIfNeeded(_appData, New, new[] { "OldName" });
            Assert.False(Directory.Exists(New));
        }

        [Fact]
        public void WithNoPreviousNamesConfiguredItIsANoOp()
        {
            SeedOldInstall("OldName");
            AppDataPaths.MigrateIfNeeded(_appData, New, Array.Empty<string>());
            Assert.True(Directory.Exists(Old("OldName")));
            Assert.False(Directory.Exists(New));
        }

        /// <summary>
        /// The point of the whole exercise: a backup taken under the old product name
        /// must still restore the game after the folder is renamed.
        /// </summary>
        [Fact]
        public void AGameBackedUpBeforeTheRenameIsStillRevertibleAfterIt()
        {
            var previousConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var home = Path.Combine(_appData, "home");
            var gameDir = Path.Combine(_appData, "game");
            // The directory has to exist before XDG_CONFIG_HOME points at it, or the
            // runtime reports no application-data folder at all.
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(gameDir);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", home);

            try
            {
                // The app backs up the game's own DLL, then overwrites it.
                File.WriteAllText(Path.Combine(gameDir, "nvngx_dlss.dll"), "the game's own dll");
                var store = new BackupStoreService();
                store.BackupFile(gameDir, gameDir, "nvngx_dlss.dll");
                store.SaveManifest(gameDir, new InstallationManifest
                {
                    OperationStatus = "committed",
                    InstalledGameDirectory = gameDir,
                    InstalledFiles = { "nvngx_dlss.dll" },
                });
                Assert.True(store.HasValidBackup(gameDir));
                File.WriteAllText(Path.Combine(gameDir, "nvngx_dlss.dll"), "swapped in");

                // Rewind: pretend all of that had happened under a previous product
                // name, which is the state a renamed app wakes up to.
                var current = Path.Combine(home, AppDataPaths.FolderName);
                var underOldName = Path.Combine(home, "PreviousProductName");
                Directory.Move(current, underOldName);

                // The data is now only under the old name — the app would start blank.
                // Checked on disk rather than by constructing a store, because a store
                // creates its directories, which is not what a real startup does before
                // the migration has had its turn.
                Assert.False(Directory.Exists(current));
                Assert.True(Directory.Exists(Path.Combine(underOldName, "Backups")));

                AppDataPaths.MigrateIfNeeded(home, current, new[] { "PreviousProductName" });

                // And with it, the game reverts exactly as before.
                var afterStore = new BackupStoreService();
                Assert.True(afterStore.HasValidBackup(gameDir));
                Assert.True(afterStore.RestoreFile(gameDir, gameDir, "nvngx_dlss.dll"));
                Assert.Equal("the game's own dll", File.ReadAllText(Path.Combine(gameDir, "nvngx_dlss.dll")));
            }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previousConfigHome);
            }
        }
    }
}
