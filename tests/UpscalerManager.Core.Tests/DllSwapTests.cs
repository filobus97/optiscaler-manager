// Upscaler Manager - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UpscalerManager.Core.Components;
using UpscalerManager.Core.Models;
using UpscalerManager.Core.Services;
using Xunit;

namespace UpscalerManager.Core.Tests
{
    /// <summary>
    /// The swap route writes into the player's game folder and its backup is the only
    /// copy of what was there before, so these tests are mostly about the ways that
    /// could go wrong rather than the happy path.
    /// </summary>
    [Collection(AppDataCollection.Name)]
    public class DllSwapTests : IDisposable
    {
        private readonly ScopedAppData _appData = new();
        private readonly string _gameDir;
        private readonly Game _game;

        public DllSwapTests()
        {
            _gameDir = Path.Combine(Path.GetTempPath(), "swaptest-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_gameDir);
            _game = new Game { Name = "Test Game", InstallPath = _gameDir };
        }

        public void Dispose()
        {
            try { Directory.Delete(_gameDir, recursive: true); } catch { }
            _appData.Dispose();
        }

        // ── Fixtures ─────────────────────────────────────────────────────────

        /// <summary>Writes a DLL into the game folder and returns its path.</summary>
        private string GameDll(string name, string version)
        {
            var path = Path.Combine(_gameDir, name);
            File.WriteAllBytes(path, PeTestData.BuildPe(PeTestData.MachineAmd64, version));
            return path;
        }

        /// <summary>A detected component for a file in the game folder.</summary>
        private DetectedComponent Component(string name, string? version)
        {
            var def = UpscalerCatalog.For(name)!;
            return new DetectedComponent
            {
                FileName = def.FileName, Technology = def.Technology, Role = def.Role,
                Vendor = def.Vendor, Version = version, RelativePath = name,
                Source = ComponentSource.Unattributed,
            };
        }

        private LibraryDll AddToLibrary(string name, string version)
        {
            var staging = Path.Combine(Path.GetTempPath(), "swapsrc-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(staging);
            var src = Path.Combine(staging, name);
            File.WriteAllBytes(src, PeTestData.BuildPe(PeTestData.MachineAmd64, version));
            try { return new DllLibraryService().Import(src); }
            finally { try { Directory.Delete(staging, true); } catch { } }
        }

        private SwapSlot SlotFor(string name) =>
            Assert.Single(new DllSwapService().Slots(_game),
                s => s.FileName.Equals(name, StringComparison.OrdinalIgnoreCase));

        // ── The catalogue ────────────────────────────────────────────────────

        [Fact]
        public void EverySwappableDllIsAlsoOneTheScannerLooksFor()
        {
            // If a name is swappable but not in the detection catalogue, the scan never
            // finds it and the swap row never appears — the feature would be silently
            // missing for that DLL rather than visibly broken.
            foreach (var dll in SwappableDlls.All)
                Assert.NotNull(UpscalerCatalog.For(dll.FileName));
        }

        [Fact]
        public void SwappableDllsUseTheSameTechnologyNamesAsTheCatalogue()
        {
            // Two names for one technology would split a game's page into rows that
            // disagree with each other.
            foreach (var dll in SwappableDlls.All)
                Assert.Equal(UpscalerCatalog.For(dll.FileName)!.Technology, dll.Technology);
        }

        [Fact]
        public void NamesMatchWhateverCaseTheGameShipsThemIn()
        {
            // DLSS Swapper compares exact case and gets away with it on NTFS. On ext4
            // this is the difference between working and never seeing the file.
            Assert.True(SwappableDlls.IsSwappable("NvNgx_Dlss.dll"));
            Assert.True(SwappableDlls.IsSwappable("LIBXESS.DLL"));
            Assert.Equal("nvngx_dlss.dll", SwappableDlls.For("NVNGX_DLSS.DLL")!.FileName);
        }

        [Fact]
        public void ThingsThatAreNotSwappableAreRejected()
        {
            Assert.False(SwappableDlls.IsSwappable("OptiScaler.dll"));
            Assert.False(SwappableDlls.IsSwappable("amd_fidelityfx_upscaler_dx12.dll"));
            Assert.False(SwappableDlls.IsSwappable(null));
        }

        // ── The library ──────────────────────────────────────────────────────

        [Fact]
        public void ImportingRecordsTheVersionReadFromTheBinary()
        {
            var entry = AddToLibrary("nvngx_dlss.dll", "310.3.0.0");
            Assert.Equal("310.3.0.0", entry.Version);
            Assert.Equal(DllOrigin.Imported, entry.Origin);
            Assert.True(File.Exists(entry.Path));
            Assert.True(new DllLibraryService().Has("nvngx_dlss.dll", "310.3.0.0"));
        }

        [Fact]
        public void ImportingSomethingWeCannotSwapIsRefused()
        {
            var staging = Path.Combine(Path.GetTempPath(), "swapsrc-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(staging);
            var src = Path.Combine(staging, "some_other.dll");
            File.WriteAllBytes(src, PeTestData.BuildPe(PeTestData.MachineAmd64, "1.0.0.0"));

            var ex = Assert.Throws<InvalidOperationException>(() => new DllLibraryService().Import(src));
            Assert.Contains("not a DLL this app knows how to swap", ex.Message);
            try { Directory.Delete(staging, true); } catch { }
        }

        [Fact]
        public void Importing32BitIsRefused()
        {
            var staging = Path.Combine(Path.GetTempPath(), "swapsrc-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(staging);
            var src = Path.Combine(staging, "nvngx_dlss.dll");
            File.WriteAllBytes(src, PeTestData.BuildPe(PeTestData.MachineI386, "310.3.0.0"));

            var ex = Assert.Throws<InvalidOperationException>(() => new DllLibraryService().Import(src));
            Assert.Contains("32-bit", ex.Message);
            try { Directory.Delete(staging, true); } catch { }
        }

        [Fact]
        public void ImportingSomethingWithNoVersionIsRefused()
        {
            // Without a version there is no way to tell it apart from the build already
            // in the game, so it cannot be offered as a choice.
            var staging = Path.Combine(Path.GetTempPath(), "swapsrc-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(staging);
            var src = Path.Combine(staging, "nvngx_dlss.dll");
            File.WriteAllBytes(src, PeTestData.BuildPe(PeTestData.MachineAmd64, fileVersion: null));

            var ex = Assert.Throws<InvalidOperationException>(() => new DllLibraryService().Import(src));
            Assert.Contains("no version", ex.Message);
            try { Directory.Delete(staging, true); } catch { }
        }

        [Fact]
        public void HarvestableBuildsComeFromTheGamesAndAreListedOncePerBuild()
        {
            // The same DLSS version shipping in several games is one choice, not several.
            var games = new List<Game>();
            foreach (var name in new[] { "Game A", "Game B" })
            {
                var dir = Path.Combine(_gameDir, name);
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, "nvngx_dlss.dll"),
                    PeTestData.BuildPe(PeTestData.MachineAmd64, "310.1.0.0"));
                var g = new Game { Name = name, InstallPath = dir };
                g.DetectedComponents.Add(Component("nvngx_dlss.dll", "310.1.0.0"));
                games.Add(g);
            }

            var harvestable = Assert.Single(new DllLibraryService().Harvestable(games));
            Assert.Equal("nvngx_dlss.dll", harvestable.FileName);
            Assert.Equal("310.1.0.0", harvestable.Version);
            Assert.False(harvestable.InLibrary);
            Assert.Equal("Game A", harvestable.GameName);   // stable between scans
        }

        [Fact]
        public void OurOwnInstalledFilesAreNotOfferedBackAsTheGamesBuild()
        {
            // Otherwise this app's copy gets laundered into something that looks like
            // provenance from that game.
            GameDll("nvngx_dlss.dll", "310.1.0.0");
            var component = Component("nvngx_dlss.dll", "310.1.0.0");
            component.Source = ComponentSource.Manager;
            _game.DetectedComponents.Add(component);

            Assert.Empty(new DllLibraryService().Harvestable(new[] { _game }));
        }

        [Fact]
        public void HarvestingSaysWhichGameItCameFrom()
        {
            GameDll("libxess.dll", "2.0.1.0");
            _game.DetectedComponents.Add(Component("libxess.dll", "2.0.1.0"));

            var library = new DllLibraryService();
            var entry = library.Harvest(Assert.Single(library.Harvestable(new[] { _game })));

            Assert.Equal(DllOrigin.Harvested, entry.Origin);
            Assert.Equal("from Test Game", entry.SourceLabel);
            // Provenance has to survive a reload, since it cannot be re-derived.
            Assert.Equal("from Test Game", Assert.Single(library.For("libxess.dll")).SourceLabel);
        }

        [Theory]
        [InlineData("4.1.1", "4.1.1")]
        [InlineData("310.3.0.0", "310.3.0.0")]
        [InlineData("2.0.1-beta", "2.0.1-beta")]
        [InlineData("...", "unknown")]
        [InlineData("", "unknown")]
        public void AVersionBecomesASafeFolderName(string version, string expected)
            => Assert.Equal(expected, DllLibraryService.SafeVersion(version));

        [Theory]
        [InlineData("/../../etc")]
        [InlineData("..")]
        [InlineData("../4.1.1")]
        [InlineData("C:\\Windows\\System32")]
        public void AVersionCannotEscapeTheLibraryFolder(string hostile)
        {
            // A version read out of a malformed resource can carry anything, and it
            // becomes a path segment.
            var safe = DllLibraryService.SafeVersion(hostile);
            Assert.DoesNotContain("/", safe);
            Assert.DoesNotContain("\\", safe);
            Assert.NotEqual("..", safe);
            Assert.False(safe.StartsWith('.'), $"'{safe}' would be a hidden or relative path");
        }

        // ── Swapping ─────────────────────────────────────────────────────────

        [Fact]
        public void ASwapReplacesTheFileAndKeepsTheOriginal()
        {
            var original = GameDll("nvngx_dlss.dll", "310.1.0.0");
            var originalBytes = File.ReadAllBytes(original);
            _game.DetectedComponents.Add(Component("nvngx_dlss.dll", "310.1.0.0"));

            var swaps = new DllSwapService();
            var record = swaps.Swap(_game, SlotFor("nvngx_dlss.dll"), AddToLibrary("nvngx_dlss.dll", "310.3.0.0"));

            Assert.Equal("310.3.0.0", record.InstalledVersion);
            Assert.Equal("310.1.0.0", record.OriginalVersion);
            Assert.True(record.ExistedBefore);
            Assert.NotEqual(originalBytes, File.ReadAllBytes(original));

            // The original is in the store, byte-for-byte.
            var stored = Path.Combine(new BackupStoreService().GetFilesDir(_gameDir), "swaps", "nvngx_dlss.dll");
            Assert.True(File.Exists(stored));
            Assert.Equal(originalBytes, File.ReadAllBytes(stored));
            Assert.True(swaps.HasSwaps(_game));
        }

        [Fact]
        public void ASecondSwapDoesNotOverwriteTheBackupOfTheGamesOwnBuild()
        {
            // This is the one-way mistake: if the second swap backs up the first swap's
            // build, the game's own DLL is gone for good and no revert can get it back.
            var target = GameDll("nvngx_dlss.dll", "310.1.0.0");
            var gameOwnBytes = File.ReadAllBytes(target);
            _game.DetectedComponents.Add(Component("nvngx_dlss.dll", "310.1.0.0"));

            var swaps = new DllSwapService();
            swaps.Swap(_game, SlotFor("nvngx_dlss.dll"), AddToLibrary("nvngx_dlss.dll", "310.2.0.0"));

            // Re-read the slot: it now reports the swapped build, as the real UI would.
            _game.DetectedComponents.Clear();
            _game.DetectedComponents.Add(Component("nvngx_dlss.dll", "310.2.0.0"));
            swaps.Swap(_game, SlotFor("nvngx_dlss.dll"), AddToLibrary("nvngx_dlss.dll", "310.3.0.0"));

            var stored = Path.Combine(new BackupStoreService().GetFilesDir(_gameDir), "swaps", "nvngx_dlss.dll");
            Assert.Equal(gameOwnBytes, File.ReadAllBytes(stored));

            // And the record still describes the game's own build, not the first swap's.
            var record = Assert.Single(swaps.LoadManifest(_game).Files);
            Assert.Equal("310.1.0.0", record.OriginalVersion);
            Assert.Equal("310.3.0.0", record.InstalledVersion);
        }

        [Fact]
        public void RevertingPutsTheGamesOwnBuildBackExactly()
        {
            var target = GameDll("nvngx_dlss.dll", "310.1.0.0");
            var gameOwnBytes = File.ReadAllBytes(target);
            _game.DetectedComponents.Add(Component("nvngx_dlss.dll", "310.1.0.0"));

            var swaps = new DllSwapService();
            var record = swaps.Swap(_game, SlotFor("nvngx_dlss.dll"), AddToLibrary("nvngx_dlss.dll", "310.3.0.0"));
            swaps.Revert(_game, record);

            Assert.Equal(gameOwnBytes, File.ReadAllBytes(target));
            Assert.Empty(swaps.LoadManifest(_game).Files);
            Assert.False(swaps.HasSwaps(_game));
        }

        [Fact]
        public void RevertingRefusesWhenTheFileIsNoLongerOurs()
        {
            // A game patch or another tool replaced it. Restoring a backup from before
            // that would quietly undo their change.
            var target = GameDll("nvngx_dlss.dll", "310.1.0.0");
            _game.DetectedComponents.Add(Component("nvngx_dlss.dll", "310.1.0.0"));

            var swaps = new DllSwapService();
            var record = swaps.Swap(_game, SlotFor("nvngx_dlss.dll"), AddToLibrary("nvngx_dlss.dll", "310.3.0.0"));

            File.WriteAllBytes(target, PeTestData.BuildPe(PeTestData.MachineAmd64, "320.0.0.0"));

            var ex = Assert.Throws<InvalidOperationException>(() => swaps.Revert(_game, record));
            Assert.Contains("no longer the build this app installed", ex.Message);

            // Still recorded, so the page keeps offering the choice.
            Assert.Single(swaps.LoadManifest(_game).Files);
        }

        [Fact]
        public void RevertingAnywayIsAllowedWhenTheUserInsists()
        {
            var target = GameDll("nvngx_dlss.dll", "310.1.0.0");
            var gameOwnBytes = File.ReadAllBytes(target);
            _game.DetectedComponents.Add(Component("nvngx_dlss.dll", "310.1.0.0"));

            var swaps = new DllSwapService();
            var record = swaps.Swap(_game, SlotFor("nvngx_dlss.dll"), AddToLibrary("nvngx_dlss.dll", "310.3.0.0"));
            File.WriteAllBytes(target, PeTestData.BuildPe(PeTestData.MachineAmd64, "320.0.0.0"));

            swaps.Revert(_game, record, force: true);
            Assert.Equal(gameOwnBytes, File.ReadAllBytes(target));
        }

        [Fact]
        public void ADllThatWasNotThereBeforeIsDeletedOnRevertRatherThanRestored()
        {
            // A game shipping XeSS without the DX11 build is exactly this case: there is
            // no original to put back, so reverting means removing ours.
            GameDll("libxess.dll", "2.0.1.0");
            _game.DetectedComponents.Add(Component("libxess.dll", "2.0.1.0"));

            var swaps = new DllSwapService();
            var slot = SlotFor("libxess.dll");

            // Point the slot at a filename that is not on disk, which is what an
            // "add this one too" swap would do.
            var addSlot = slot with { FileName = "libxess_dx11.dll" };
            var record = swaps.Swap(_game, addSlot, AddToLibrary("libxess_dx11.dll", "2.0.1.0"));

            var added = Path.Combine(_gameDir, "libxess_dx11.dll");
            Assert.False(record.ExistedBefore);
            Assert.True(File.Exists(added));

            swaps.Revert(_game, record);
            Assert.False(File.Exists(added));
        }

        // ── Living alongside OptiScaler ──────────────────────────────────────

        [Fact]
        public void SwappingAFileOptiScalerInstalledIsRefused()
        {
            // Both routes write into the same folder and libxess.dll is on both lists.
            // Without this, each would believe it owns the file, and whichever reverted
            // second would restore the other's build over the game's original.
            GameDll("libxess.dll", "2.0.1.0");
            _game.DetectedComponents.Add(Component("libxess.dll", "2.0.1.0"));

            new BackupStoreService().SaveManifest(_gameDir, new InstallationManifest
            {
                OperationStatus = "committed",
                OptiscalerVersion = "0.9.4",
                InstalledGameDirectory = _gameDir,
                InstalledFiles = new List<string> { "OptiScaler.dll", "libxess.dll" },
            });

            var slot = SlotFor("libxess.dll");
            Assert.False(slot.Verdict.Allowed);
            Assert.Contains("OptiScaler installed this copy", slot.Verdict.Reason);

            var ex = Assert.Throws<InvalidOperationException>(
                () => new DllSwapService().Swap(_game, slot, AddToLibrary("libxess.dll", "2.1.0.0")));
            Assert.Contains("OptiScaler", ex.Message);
        }

        [Fact]
        public void DlssIsSwappableEvenWithOptiScalerInstalled()
        {
            // nvngx_dlss.dll is not on OptiScaler's artifact list, so the two do not
            // overlap there and the swap stays available.
            GameDll("nvngx_dlss.dll", "310.1.0.0");
            _game.DetectedComponents.Add(Component("nvngx_dlss.dll", "310.1.0.0"));

            new BackupStoreService().SaveManifest(_gameDir, new InstallationManifest
            {
                OperationStatus = "committed",
                InstalledGameDirectory = _gameDir,
                InstalledFiles = new List<string> { "OptiScaler.dll", "OptiScaler.ini" },
            });

            Assert.True(SlotFor("nvngx_dlss.dll").Verdict.Allowed);
        }

        [Fact]
        public void InstallingOptiScalerReportsWhichSwapsItWouldOverwrite()
        {
            GameDll("libxess.dll", "2.0.1.0");
            GameDll("nvngx_dlss.dll", "310.1.0.0");
            _game.DetectedComponents.Add(Component("libxess.dll", "2.0.1.0"));
            _game.DetectedComponents.Add(Component("nvngx_dlss.dll", "310.1.0.0"));

            var swaps = new DllSwapService();
            swaps.Swap(_game, SlotFor("libxess.dll"), AddToLibrary("libxess.dll", "2.1.0.0"));
            swaps.Swap(_game, SlotFor("nvngx_dlss.dll"), AddToLibrary("nvngx_dlss.dll", "310.3.0.0"));

            // Only the file OptiScaler may replace is a conflict; DLSS is not.
            Assert.Equal(new[] { "libxess.dll" }, swaps.SwapsOptiScalerWouldOverwrite(_game));
        }

        // ── Recovering from the world changing underneath ────────────────────

        [Fact]
        public void ASwapWhoseFileHasVanishedIsForgottenButItsBackupIsKept()
        {
            // The game was verified, moved or reinstalled. A stale record would make the
            // page claim a swap that is not there, and block re-swapping.
            var target = GameDll("nvngx_dlss.dll", "310.1.0.0");
            _game.DetectedComponents.Add(Component("nvngx_dlss.dll", "310.1.0.0"));

            var swaps = new DllSwapService();
            swaps.Swap(_game, SlotFor("nvngx_dlss.dll"), AddToLibrary("nvngx_dlss.dll", "310.3.0.0"));
            File.Delete(target);

            Assert.Equal(1, swaps.ForgetStaleSwaps(_game));
            Assert.Empty(swaps.LoadManifest(_game).Files);

            // The original may be the only copy of that build left, so it stays.
            var stored = Path.Combine(new BackupStoreService().GetFilesDir(_gameDir), "swaps", "nvngx_dlss.dll");
            Assert.True(File.Exists(stored));
        }

        [Fact]
        public void ACorruptSwapRecordIsRefusedRatherThanTreatedAsNoSwaps()
        {
            // Treating it as "nothing was swapped" would let the app overwrite the
            // game's DLL a second time and lose the backup's claim on it.
            var store = new BackupStoreService();
            Directory.CreateDirectory(store.GetBackupRoot(_gameDir));
            File.WriteAllText(Path.Combine(store.GetBackupRoot(_gameDir), "swaps.json"), "{ not json");

            var swaps = new DllSwapService();
            Assert.Throws<InvalidOperationException>(() => swaps.LoadManifest(_game));

            // But describing the game still works, so its page renders.
            Assert.Empty(swaps.Slots(_game));
            // And the backup is treated as needed, not deletable.
            Assert.True(swaps.HasSwaps(_game));
        }

        [Fact]
        public void OnlyFilesActuallyPresentGetASwapRow()
        {
            // This route replaces a library the game already ships. A game with no
            // nvngx_dlss.dll does not call DLSS, and adding the file achieves nothing.
            _game.DetectedComponents.Add(Component("nvngx_dlss.dll", "310.1.0.0"));  // recorded but not on disk
            Assert.Empty(new DllSwapService().Slots(_game));
        }

        // ── Storage must not offer to delete the only copy ───────────────────

        [Fact]
        public void ASwapOnlyBackupIsLiveAndCannotBeDeleted()
        {
            // There is no OptiScaler here, so the old live/spent test called this
            // "already reverted" and offered a Remove button — which would have deleted
            // the only copy of the game's own DLL.
            GameDll("nvngx_dlss.dll", "310.1.0.0");
            _game.DetectedComponents.Add(Component("nvngx_dlss.dll", "310.1.0.0"));

            new BackupStoreService().SaveManifest(_gameDir, new InstallationManifest
            {
                OperationStatus = "committed",
                InstalledGameDirectory = _gameDir,
                InstalledFiles = new List<string>(),
            });
            new DllSwapService().Swap(_game, SlotFor("nvngx_dlss.dll"), AddToLibrary("nvngx_dlss.dll", "310.3.0.0"));

            var backup = Assert.Single(
                new StorageInventoryService(new AppConfiguration()).Scan(),
                i => i.Group == "Backups");

            Assert.Equal(StorageTier.LiveBackup, backup.Tier);
            Assert.False(backup.CanDelete);
            Assert.True(backup.SwapsOnly);
        }

        [Fact]
        public void OnceRevertedTheBackupBecomesRemovable()
        {
            GameDll("nvngx_dlss.dll", "310.1.0.0");
            _game.DetectedComponents.Add(Component("nvngx_dlss.dll", "310.1.0.0"));

            new BackupStoreService().SaveManifest(_gameDir, new InstallationManifest
            {
                OperationStatus = "committed",
                InstalledGameDirectory = _gameDir,
                InstalledFiles = new List<string>(),
            });

            var swaps = new DllSwapService();
            var record = swaps.Swap(_game, SlotFor("nvngx_dlss.dll"), AddToLibrary("nvngx_dlss.dll", "310.3.0.0"));
            swaps.Revert(_game, record);

            var backup = Assert.Single(
                new StorageInventoryService(new AppConfiguration()).Scan(),
                i => i.Group == "Backups");

            Assert.Equal(StorageTier.SpentBackup, backup.Tier);
            Assert.True(backup.CanDelete);
        }

        [Fact]
        public void TheLibraryShowsUpAsReclaimableStorageThatNothingCanRefetch()
        {
            AddToLibrary("nvngx_dlss.dll", "310.3.0.0");

            var item = Assert.Single(
                new StorageInventoryService(new AppConfiguration()).Scan(),
                i => i.Group == "Swappable DLL builds");

            Assert.Equal(StorageTier.UserImport, item.Tier);
            Assert.True(item.CanDelete);
            Assert.Contains("nvngx_dlss.dll", item.Label);
            Assert.Contains("310.3.0.0", item.Label);
        }
    }
}
