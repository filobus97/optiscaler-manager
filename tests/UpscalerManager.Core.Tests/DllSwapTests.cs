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
            Assert.False(SwappableDlls.IsSwappable("nvapi64.dll"));
            Assert.False(SwappableDlls.IsSwappable("dxgi.dll"));
            Assert.False(SwappableDlls.IsSwappable(null));
        }

        [Fact]
        public void TheFsrUpscalerFilesAreSwappable()
        {
            // The FSR 4 community builds ship as one of these two names, so without
            // them the swap route cannot reach FSR 4 at all. DLSS Swapper reserves an
            // enum slot for the first and never detects or swaps it; this goes past
            // what it does rather than catching up.
            Assert.True(SwappableDlls.IsSwappable("amd_fidelityfx_upscaler_dx12.dll"));
            Assert.True(SwappableDlls.IsSwappable("amdxcffx64.dll"));
        }

        [Fact]
        public void EveryFidelityFxLibraryOptiScalerLoadsByNameIsSwappable()
        {
            // Read out of OptiScaler's own DllNames.h rather than guessed: these are
            // the six FidelityFX libraries it loads by name (DEFINE_NAME_VECTORS for
            // ffxDx12, ffxDx12Upscaler, ffxDx12FG, ffxDx12Denoiser, ffxDx12Radiance and
            // ffxVk). A game ships whichever ones it needs, so any that are missing
            // here are files a user can see in their game folder and not swap.
            foreach (var name in new[]
            {
                "amd_fidelityfx_dx12.dll",
                "amd_fidelityfx_loader_dx12.dll",
                "amd_fidelityfx_upscaler_dx12.dll",
                "amd_fidelityfx_framegeneration_dx12.dll",
                "amd_fidelityfx_denoiser_dx12.dll",
                "amd_fidelityfx_radiancecache_dx12.dll",
                "amd_fidelityfx_vk.dll",
            })
                Assert.True(SwappableDlls.IsSwappable(name),
                    $"OptiScaler loads {name} by name, so a game can be carrying it.");
        }

        [Fact]
        public void EverySwappableFileIsAlsoOneTheScanDescribes()
        {
            // A file that can be swapped but is not in the catalogue would be installed
            // into a game and then never appear in its technology list, so the page
            // would show a swap the game page cannot account for.
            foreach (var dll in SwappableDlls.All)
                Assert.NotNull(UpscalerCatalog.For(dll.FileName));
        }

        [Fact]
        public void EverySwappableFileHasItsOwnPlaceInTheOrdering()
        {
            // Two files sharing a display order sort unpredictably against each other,
            // so the rows on a game page would move between renders.
            var orders = SwappableDlls.All.Select(d => SwappableDlls.DisplayOrder(d.FileName)).ToList();
            Assert.Equal(orders.Count, orders.Distinct().Count());
        }

        [Fact]
        public void ACommunityReleaseSuppliesWhicheverSwappableFilesItCarries()
        {
            // The reason this matters: these releases are drops of a matched FidelityFX
            // set, and the cache used to keep only the INT8 upscaler out of each one. A
            // user looking at the FSR runtime row saw nothing on offer no matter how
            // many releases they had downloaded, because the runtime had been thrown
            // away at extraction time.
            var release = Path.Combine(AppDataPaths.Cache, "Extras", "4.1.1b");
            Directory.CreateDirectory(release);
            File.WriteAllBytes(Path.Combine(release, "amdxcffx64.dll"),
                PeTestData.BuildPe(PeTestData.MachineAmd64, "4.1.1.0"));
            File.WriteAllBytes(Path.Combine(release, "amd_fidelityfx_dx12.dll"),
                PeTestData.BuildPe(PeTestData.MachineAmd64, "1.0.1.41314"));

            var library = new DllLibraryService();

            var runtime = Assert.Single(library.FromCommunityBuilds("amd_fidelityfx_dx12.dll"));
            Assert.Equal("1.0.1.41314", runtime.Version);

            var upscaler = Assert.Single(library.FromCommunityBuilds("amdxcffx64.dll"));
            Assert.Equal("4.1.1.0", upscaler.Version);
        }

        // ── The same DLL in several places ───────────────────────────────────

        /// <summary>
        /// Writes the same DLL into a subdirectory as well, the way an Unreal title
        /// carries one beside its executable and another under Engine/Binaries.
        /// </summary>
        private DetectedComponent NestedDll(string name, string version, string subdirectory)
        {
            var dir = Path.Combine(_gameDir, subdirectory);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, name), PeTestData.BuildPe(PeTestData.MachineAmd64, version));

            var component = Component(name, version);
            component.RelativePath = Path.Combine(subdirectory, name);
            return component;
        }

        [Fact]
        public void EveryCopyOfADllInAGameIsOneSlot()
        {
            GameDll("nvngx_dlss.dll", "310.1.0.0");
            var nested = NestedDll("nvngx_dlss.dll", "310.1.0.0", Path.Combine("Engine", "Binaries"));
            _game.DetectedComponents = new() { Component("nvngx_dlss.dll", "310.1.0.0"), nested };

            var slot = SlotFor("nvngx_dlss.dll");
            Assert.Equal(2, slot.CopyCount);
            // Shallowest first, so the row names the copy a player would call "the game's".
            Assert.Equal(_gameDir, slot.Copies[0].Directory);
        }

        [Fact]
        public void SwappingWritesToEveryCopy()
        {
            // The bug this fixes: a game with two copies got one of them replaced, and
            // which one it loaded was the game's choice — so half the time the swap
            // appeared to do nothing whatsoever.
            GameDll("nvngx_dlss.dll", "310.1.0.0");
            var nested = NestedDll("nvngx_dlss.dll", "310.1.0.0", Path.Combine("Engine", "Binaries"));
            _game.DetectedComponents = new() { Component("nvngx_dlss.dll", "310.1.0.0"), nested };

            var build = AddToLibrary("nvngx_dlss.dll", "310.3.0.0");
            var swapped = new DllSwapService().Swap(_game, SlotFor("nvngx_dlss.dll"), build);

            Assert.Equal(2, swapped.Copies.Count);
            foreach (var copy in swapped.Copies)
            {
                var installed = Path.Combine(copy.Directory, "nvngx_dlss.dll");
                Assert.Equal(File.ReadAllBytes(build.Path), File.ReadAllBytes(installed));
            }
        }

        [Fact]
        public void EachCopysOriginalIsStoredSeparately()
        {
            // Two directories holding the same filename backing up to one path would
            // destroy one of the two originals, with nothing left to say which.
            GameDll("nvngx_dlss.dll", "310.1.0.0");
            var nested = NestedDll("nvngx_dlss.dll", "300.0.0.0", Path.Combine("Engine", "Binaries"));
            _game.DetectedComponents = new() { Component("nvngx_dlss.dll", "310.1.0.0"), nested };

            var swapped = new DllSwapService().Swap(
                _game, SlotFor("nvngx_dlss.dll"), AddToLibrary("nvngx_dlss.dll", "310.3.0.0"));

            var paths = swapped.Copies.Select(c => c.BackupRelative).ToList();
            Assert.Equal(2, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());

            var files = new BackupStoreService().GetFilesDir(_gameDir);
            foreach (var copy in swapped.Copies)
                Assert.True(File.Exists(Path.Combine(files, copy.BackupRelative)),
                    $"the original from {copy.Directory} was not stored at {copy.BackupRelative}");

            // And each stored original is the build that was actually in that directory.
            Assert.Contains(swapped.Copies, c => c.OriginalVersion == "310.1.0.0");
            Assert.Contains(swapped.Copies, c => c.OriginalVersion == "300.0.0.0");
        }

        [Fact]
        public void RevertingPutsEveryCopyBack()
        {
            GameDll("nvngx_dlss.dll", "310.1.0.0");
            var nested = NestedDll("nvngx_dlss.dll", "300.0.0.0", Path.Combine("Engine", "Binaries"));
            _game.DetectedComponents = new() { Component("nvngx_dlss.dll", "310.1.0.0"), nested };

            var root = Path.Combine(_gameDir, "nvngx_dlss.dll");
            var deep = Path.Combine(_gameDir, "Engine", "Binaries", "nvngx_dlss.dll");
            var originalRoot = File.ReadAllBytes(root);
            var originalDeep = File.ReadAllBytes(deep);

            var service = new DllSwapService();
            var swapped = service.Swap(
                _game, SlotFor("nvngx_dlss.dll"), AddToLibrary("nvngx_dlss.dll", "310.3.0.0"));
            service.Revert(_game, swapped);

            Assert.Equal(originalRoot, File.ReadAllBytes(root));
            Assert.Equal(originalDeep, File.ReadAllBytes(deep));
            Assert.Empty(service.LoadManifest(_game).Files);
        }

        [Fact]
        public void ACopyThatIsNewToTheGameIsDeletedOnRevertWhileTheOtherIsRestored()
        {
            // One directory has the file, the other does not. Reverting has to restore
            // the first and remove the second, not treat both the same way.
            GameDll("libxess.dll", "2.0.0.0");
            var dir = Path.Combine(_gameDir, "Binaries");
            Directory.CreateDirectory(dir);

            var nested = Component("libxess.dll", "2.0.0.0");
            nested.RelativePath = Path.Combine("Binaries", "libxess.dll");
            // Deliberately not written to disk, so the slot's second copy points at a
            // directory with no file in it.
            File.WriteAllBytes(Path.Combine(dir, "libxess.dll"), PeTestData.BuildPe(PeTestData.MachineAmd64, "2.0.0.0"));
            _game.DetectedComponents = new() { Component("libxess.dll", "2.0.0.0"), nested };

            var service = new DllSwapService();
            var swapped = service.Swap(
                _game, SlotFor("libxess.dll"), AddToLibrary("libxess.dll", "2.0.2.0"));
            Assert.Equal(2, swapped.Copies.Count);
            Assert.All(swapped.Copies, c => Assert.True(c.ExistedBefore));

            service.Revert(_game, swapped);
            Assert.True(File.Exists(Path.Combine(_gameDir, "libxess.dll")));
            Assert.True(File.Exists(Path.Combine(dir, "libxess.dll")));
        }

        [Fact]
        public void ASecondSwapDoesNotReBackUpAnyCopy()
        {
            GameDll("nvngx_dlss.dll", "310.1.0.0");
            var nested = NestedDll("nvngx_dlss.dll", "310.1.0.0", "Binaries");
            _game.DetectedComponents = new() { Component("nvngx_dlss.dll", "310.1.0.0"), nested };

            var service = new DllSwapService();
            service.Swap(_game, SlotFor("nvngx_dlss.dll"), AddToLibrary("nvngx_dlss.dll", "310.2.0.0"));

            // Re-read the slot: it now carries the swap record, as the UI's would.
            var second = service.Swap(
                _game, SlotFor("nvngx_dlss.dll"), AddToLibrary("nvngx_dlss.dll", "310.3.0.0"));

            Assert.Equal(2, second.Copies.Count);
            // Both originals still say 310.1.0.0 — the game's build, not the first swap's.
            Assert.All(second.Copies, c => Assert.Equal("310.1.0.0", c.OriginalVersion));

            var files = new BackupStoreService().GetFilesDir(_gameDir);
            foreach (var copy in second.Copies)
            {
                var stored = Path.Combine(files, copy.BackupRelative);
                Assert.Equal("310.1.0.0", DllLibraryService.VersionOf(PeFileInspector.Inspect(stored)));
            }
        }

        [Fact]
        public void OneCopyDisappearingIsNotAStaleSwap()
        {
            // A partially verified game is not a swap that has gone away: the record
            // still describes real files and still holds their originals.
            GameDll("nvngx_dlss.dll", "310.1.0.0");
            var nested = NestedDll("nvngx_dlss.dll", "310.1.0.0", "Binaries");
            _game.DetectedComponents = new() { Component("nvngx_dlss.dll", "310.1.0.0"), nested };

            var service = new DllSwapService();
            service.Swap(_game, SlotFor("nvngx_dlss.dll"), AddToLibrary("nvngx_dlss.dll", "310.3.0.0"));

            File.Delete(Path.Combine(_gameDir, "Binaries", "nvngx_dlss.dll"));
            Assert.Equal(0, service.ForgetStaleSwaps(_game));

            // Both gone: now there is nothing left to point at.
            File.Delete(Path.Combine(_gameDir, "nvngx_dlss.dll"));
            Assert.Equal(1, service.ForgetStaleSwaps(_game));
        }

        [Fact]
        public void ARecordWrittenBeforeMultiCopySupportStillReverts()
        {
            // The migration that matters most: an existing user's swaps.json has the
            // single-directory fields and no Copies list, and its original sits at the
            // old backup path. Getting this wrong loses their game's original DLL.
            GameDll("nvngx_dlss.dll", "310.1.0.0");
            _game.DetectedComponents = new() { Component("nvngx_dlss.dll", "310.1.0.0") };

            var target = Path.Combine(_gameDir, "nvngx_dlss.dll");
            var original = File.ReadAllBytes(target);

            // Stage the file and backup exactly as the old code would have left them.
            var store = new BackupStoreService();
            var legacyRelative = Path.Combine("swaps", "nvngx_dlss.dll");
            store.BackupFile(_gameDir, _gameDir, "nvngx_dlss.dll", legacyRelative);
            File.WriteAllBytes(target, PeTestData.BuildPe(PeTestData.MachineAmd64, "310.3.0.0"));

            var legacy = new SwappedFile
            {
                FileName = "nvngx_dlss.dll",
                InstalledInDirectory = _gameDir,
                InstalledVersion = "310.3.0.0",
                SourceLabel = "imported",
                InstalledSha256 = FileHash.Sha256(target),
                OriginalVersion = "310.1.0.0",
                OriginalSha256 = FileHash.Sha256(Path.Combine(store.GetFilesDir(_gameDir), legacyRelative)),
                ExistedBefore = true,
            };
            Assert.Empty(legacy.Copies);   // exactly what an old manifest deserialises to

            Directory.CreateDirectory(store.GetBackupRoot(_gameDir));
            File.WriteAllText(
                Path.Combine(store.GetBackupRoot(_gameDir), "swaps.json"),
                System.Text.Json.JsonSerializer.Serialize(
                    new SwapManifest { GameName = _game.Name, Files = { legacy } },
                    OptimizerContext.Default.SwapManifest));

            var service = new DllSwapService();
            var loaded = Assert.Single(service.LoadManifest(_game).Files);
            var copy = Assert.Single(loaded.Copies);
            Assert.Equal(_gameDir, copy.Directory);
            Assert.Equal(legacyRelative, copy.BackupRelative);

            service.Revert(_game, loaded);
            Assert.Equal(original, File.ReadAllBytes(target));
        }

        [Fact]
        public void AFirstSwapKeepsTheOldBackupPathSoAnOlderBuildCouldStillRevertIt()
        {
            // Downgrading the app should not strand a swap: the first copy's original
            // stays where a version without Copies would look for it, and the flat
            // fields keep mirroring it.
            GameDll("nvngx_dlss.dll", "310.1.0.0");
            _game.DetectedComponents = new() { Component("nvngx_dlss.dll", "310.1.0.0") };

            var swapped = new DllSwapService().Swap(
                _game, SlotFor("nvngx_dlss.dll"), AddToLibrary("nvngx_dlss.dll", "310.3.0.0"));

            var copy = Assert.Single(swapped.Copies);
            Assert.Equal(Path.Combine("swaps", "nvngx_dlss.dll"), copy.BackupRelative);
            Assert.Equal(copy.Directory, swapped.InstalledInDirectory);
            Assert.Equal(copy.OriginalSha256, swapped.OriginalSha256);
            Assert.Equal(copy.InstalledSha256, swapped.InstalledSha256);
        }

        [Fact]
        public void RevertingIsRefusedWhenAnyCopyIsNoLongerOurs()
        {
            // Checking every copy before touching any of them: a partial revert would
            // leave the game mixing its original in one place with our build in another.
            GameDll("nvngx_dlss.dll", "310.1.0.0");
            var nested = NestedDll("nvngx_dlss.dll", "310.1.0.0", "Binaries");
            _game.DetectedComponents = new() { Component("nvngx_dlss.dll", "310.1.0.0") , nested };

            var service = new DllSwapService();
            var swapped = service.Swap(
                _game, SlotFor("nvngx_dlss.dll"), AddToLibrary("nvngx_dlss.dll", "310.3.0.0"));

            // A game patch replaces only the nested copy.
            var patched = Path.Combine(_gameDir, "Binaries", "nvngx_dlss.dll");
            File.WriteAllBytes(patched, PeTestData.BuildPe(PeTestData.MachineAmd64, "320.0.0.0"));

            var error = Assert.Throws<InvalidOperationException>(() => service.Revert(_game, swapped));
            Assert.Contains("Binaries", error.Message);

            // Nothing was touched, so the root copy is still ours.
            Assert.Equal("310.3.0.0", DllLibraryService.VersionOf(
                PeFileInspector.Inspect(Path.Combine(_gameDir, "nvngx_dlss.dll"))));

            // Forcing it through restores everything.
            service.Revert(_game, swapped, force: true);
            Assert.Equal("310.1.0.0", DllLibraryService.VersionOf(
                PeFileInspector.Inspect(Path.Combine(_gameDir, "nvngx_dlss.dll"))));
        }

        [Fact]
        public void CopiesAtDifferentVersionsAreFlagged()
        {
            GameDll("nvngx_dlss.dll", "310.1.0.0");
            var nested = NestedDll("nvngx_dlss.dll", "300.0.0.0", "Binaries");
            _game.DetectedComponents = new() { Component("nvngx_dlss.dll", "310.1.0.0"), nested };

            Assert.True(SlotFor("nvngx_dlss.dll").VersionsDiffer);
        }

        [Fact]
        public void TheSameDirectoryReachedTwiceIsOneCopy()
        {
            // The scan can reach a directory by two routes; swapping it twice would
            // back the file up over itself.
            GameDll("nvngx_dlss.dll", "310.1.0.0");
            _game.DetectedComponents = new()
            {
                Component("nvngx_dlss.dll", "310.1.0.0"),
                Component("nvngx_dlss.dll", "310.1.0.0"),
            };

            Assert.Equal(1, SlotFor("nvngx_dlss.dll").CopyCount);
        }

        [Fact]
        public void NothingIsReportedAsRunningOutOfADirectoryThatDoesNotExist()
        {
            // The pre-flight check only ever improves an error message, so it must
            // never be the thing that breaks a swap.
            Assert.Null(DllSwapService.RunningProcessIn("/nonexistent/game"));
            Assert.Null(DllSwapService.RunningProcessIn(""));
        }

        [Fact]
        public void AProcessRunningAnywhereUnderTheGameIsFound()
        {
            // The contract is "is anything running out of this tree", subdirectories
            // included — a game's executable usually lives in one. It names a process,
            // not necessarily this one: the test host shares its tree with the Roslyn
            // compiler server, and either answer is correct for the question asked.
            var self = Environment.ProcessPath;
            if (self is null) return;   // single-file publish quirk; nothing to assert

            var found = DllSwapService.RunningProcessIn(Path.GetDirectoryName(self)!);

            Assert.NotNull(found);
            Assert.DoesNotContain(Path.DirectorySeparatorChar, found);
        }

        [Fact]
        public void AnEmptyDirectoryHasNothingRunningInIt()
        {
            var empty = Path.Combine(Path.GetTempPath(), "idle-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(empty);
            try { Assert.Null(DllSwapService.RunningProcessIn(empty)); }
            finally { try { Directory.Delete(empty, true); } catch { } }
        }

        [Fact]
        public void ASwapIsRefusedWhileSomethingIsRunningInTheGame()
        {
            // Writing over a DLL a running game has mapped either fails or is ignored
            // until it restarts — either way the player is told they swapped something
            // that did not change. Refusing up front and saying why is kinder.
            var host = Path.GetDirectoryName(Environment.ProcessPath);
            if (host is null) return;

            var running = new Game { Name = "Busy Game", InstallPath = host };
            running.DetectedComponents = new() { Component("nvngx_dlss.dll", "310.1.0.0") };
            File.WriteAllBytes(
                Path.Combine(host, "nvngx_dlss.dll"), PeTestData.BuildPe(PeTestData.MachineAmd64, "310.1.0.0"));

            try
            {
                var service = new DllSwapService();
                var slot = Assert.Single(service.Slots(running));
                var error = Assert.Throws<InvalidOperationException>(
                    () => service.Swap(running, slot, AddToLibrary("nvngx_dlss.dll", "310.3.0.0")));

                Assert.Contains("running", error.Message);
                // And it refused before touching anything.
                Assert.Equal("310.1.0.0", DllLibraryService.VersionOf(
                    PeFileInspector.Inspect(Path.Combine(host, "nvngx_dlss.dll"))));
            }
            finally
            {
                try { File.Delete(Path.Combine(host, "nvngx_dlss.dll")); } catch { }
            }
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

        // ── Other sources for a build ────────────────────────────────────────

        [Fact]
        public void OptiScalerReleasesAlreadyDownloadedAreASource()
        {
            // An OptiScaler release bundles the libraries it hooks, so anyone who has
            // installed it once already has those builds — no network, and the only
            // source for AMD's runtimes.
            var release = Path.Combine(AppDataPaths.Cache, "OptiScaler", "v0.9.4");
            Directory.CreateDirectory(release);
            File.WriteAllBytes(Path.Combine(release, "libxess.dll"),
                PeTestData.BuildPe(PeTestData.MachineAmd64, "2.1.0.0"));

            var found = Assert.Single(new DllLibraryService().FromOptiScalerReleases("libxess.dll"));
            Assert.Equal("2.1.0.0", found.Version);
            Assert.Equal("OptiScaler v0.9.4", found.GameName);
            Assert.False(found.InLibrary);
        }

        [Fact]
        public void ReleaseLayoutsAreScannedRecursively()
        {
            // Upstream has moved these between subdirectories across versions; pinning
            // the scan to one path would quietly stop finding anything.
            var nested = Path.Combine(AppDataPaths.Cache, "OptiScaler", "v0.9.4", "D3D12_Optiscaler");
            Directory.CreateDirectory(nested);
            File.WriteAllBytes(Path.Combine(nested, "amd_fidelityfx_dx12.dll"),
                PeTestData.BuildPe(PeTestData.MachineAmd64, "1.1.2.0"));

            var found = Assert.Single(new DllLibraryService().FromOptiScalerReleases());
            Assert.Equal("amd_fidelityfx_dx12.dll", found.FileName);
            Assert.Equal("1.1.2.0", found.Version);
        }

        [Fact]
        public void ThingsInAReleaseThatAreNotSwappableAreIgnored()
        {
            var release = Path.Combine(AppDataPaths.Cache, "OptiScaler", "v0.9.4");
            Directory.CreateDirectory(release);
            foreach (var name in new[] { "OptiScaler.dll", "nvapi64.dll", "dxgi.dll" })
                File.WriteAllBytes(Path.Combine(release, name),
                    PeTestData.BuildPe(PeTestData.MachineAmd64, "1.0.0.0"));

            Assert.Empty(new DllLibraryService().FromOptiScalerReleases());
        }

        [Fact]
        public void ReleasesAreASourceForTheFsrUpscalerToo()
        {
            // An OptiScaler release bundles the FSR upscaler it hooks, so this is a
            // no-network source for the file FSR 4 lives in.
            var release = Path.Combine(AppDataPaths.Cache, "OptiScaler", "v0.9.4");
            Directory.CreateDirectory(release);
            File.WriteAllBytes(Path.Combine(release, "amd_fidelityfx_upscaler_dx12.dll"),
                PeTestData.BuildPe(PeTestData.MachineAmd64, "4.1.1.0"));

            var found = Assert.Single(
                new DllLibraryService().FromOptiScalerReleases("amd_fidelityfx_upscaler_dx12.dll"));
            Assert.Equal("4.1.1.0", found.Version);
        }

        [Fact]
        public void ABuildAlreadyHeldIsMarkedRatherThanOfferedTwice()
        {
            AddToLibrary("libxess.dll", "2.1.0.0");
            var release = Path.Combine(AppDataPaths.Cache, "OptiScaler", "v0.9.4");
            Directory.CreateDirectory(release);
            File.WriteAllBytes(Path.Combine(release, "libxess.dll"),
                PeTestData.BuildPe(PeTestData.MachineAmd64, "2.1.0.0"));

            Assert.True(Assert.Single(new DllLibraryService().FromOptiScalerReleases()).InLibrary);
        }

        [Fact]
        public void CommunityBuildsAlreadyDownloadedAreASource()
        {
            // Downloaded for the OptiScaler route, and immediately usable by the swap
            // route — the cache is shared, so a version fetched once serves both.
            var build = Path.Combine(AppDataPaths.Cache, "Extras", "4.0.2d");
            Directory.CreateDirectory(build);
            File.WriteAllBytes(Path.Combine(build, "amdxcffx64.dll"),
                PeTestData.BuildPe(PeTestData.MachineAmd64, "4.0.2.0"));

            var found = Assert.Single(new DllLibraryService().FromCommunityBuilds("amdxcffx64.dll"));
            Assert.Equal("4.0.2.0", found.Version);
            Assert.Equal("community build 4.0.2d", found.GameName);
        }

        [Fact]
        public void BothNamesACommunityBuildShipsAsAreRecognised()
        {
            // Older releases ship amd_fidelityfx_upscaler_dx12.dll, newer ones ship
            // amdxcffx64.dll. Handling only one is how every recent release stops
            // being found.
            Assert.True(Fsr4Int8Build.IsKnown("amd_fidelityfx_upscaler_dx12.dll"));
            Assert.True(Fsr4Int8Build.IsKnown("amdxcffx64.dll"));
            foreach (var name in Fsr4Int8Build.KnownDllNames)
                Assert.True(SwappableDlls.IsSwappable(name),
                    $"{name} is a community build filename but is not swappable, so those " +
                    "builds could be downloaded and never offered.");
        }

        [Fact]
        public void EveryVendorSourceNamesADllWeActuallySwap()
        {
            // A download that installed a file the swapper does not recognise would be
            // fetched, imported, and then never offered anywhere.
            foreach (var source in VendorDllSource.All)
                Assert.True(SwappableDlls.IsSwappable(source.FileName),
                    $"{source.FileName} is offered for download but is not swappable.");
        }

        [Fact]
        public void VendorUrlsPointAtTheVendorsOwnRepository()
        {
            // The point of this route: the file comes from Nvidia or Intel, not from a
            // mirror this project runs. If that ever changes, the licence position
            // changes with it.
            foreach (var source in VendorDllSource.All)
            {
                var url = VendorDllSource.UrlFor(source, "v1.2.3");
                Assert.StartsWith("https://raw.githubusercontent.com/", url);
                Assert.Contains($"/{source.Owner}/{source.Repo}/v1.2.3/", url);
                Assert.EndsWith(source.PathInRepo, url);
            }
        }

        [Fact]
        public void OnlyReleaseBuildsForTheArchitectureGamesShipAreOffered()
        {
            // The repositories also carry aarch64, arm64ec and development builds.
            // Installing a development build into a game would be a debugging-only
            // surprise, and the wrong architecture simply would not load.
            foreach (var source in VendorDllSource.All.Where(s => s.Owner == "NVIDIA"))
            {
                Assert.Contains("Windows_x86_64", source.PathInRepo);
                Assert.Contains("/rel/", source.PathInRepo);
            }
        }

        [Fact]
        public void AmdRuntimesAreNotOfferedForDownload()
        {
            // AMD does not publish them as loose binaries; OptiScaler's releases carry
            // them, which is why that local source exists.
            Assert.False(VendorDllSource.CanDownload("amd_fidelityfx_dx12.dll"));
            Assert.False(VendorDllSource.CanDownload("amd_fidelityfx_vk.dll"));
            Assert.True(VendorDllSource.CanDownload("nvngx_dlss.dll"));
            Assert.True(VendorDllSource.CanDownload("libxess.dll"));
        }

        [Fact]
        public void TagsAreReadOutOfGitHubsResponse()
        {
            // The request cannot be exercised offline, so the parsing is. Shape taken
            // from the real /tags response.
            const string json = """
                [
                  {"name":"v310.9.1","commit":{"sha":"abc","url":"https://api.github.com/x"}},
                  {"name":"v310.7.0","commit":{"sha":"def","url":"https://api.github.com/y"}}
                ]
                """;
            Assert.Equal(new[] { "v310.9.1", "v310.7.0" }, VendorDllService.ParseTags(json));
        }

        [Theory]
        [InlineData("not json at all")]
        [InlineData("{\"message\":\"Not Found\"}")]
        [InlineData("[]")]
        [InlineData("[{\"commit\":{}}]")]
        public void AResponseThatIsNotAListOfTagsYieldsNothing(string json)
        {
            // An error page or a rate-limit body must read as "no downloads offered",
            // not as an exception on a page that is only showing an extra route.
            Assert.Empty(VendorDllService.ParseTags(json));
        }

        [Theory]
        [InlineData("v310.9.1", "310.9.1")]
        [InlineData("v3.0.2", "3.0.2")]
        [InlineData("3.0.2", "3.0.2")]
        public void ATagNamesAVersion(string tag, string expected)
            => Assert.Equal(expected, VendorDllSource.VersionFromTag(tag));

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
