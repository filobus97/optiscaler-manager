// Upscaler Manager - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System;
using System.IO;
using System.Text;
using UpscalerManager.Core.Components;
using Xunit;

namespace UpscalerManager.Core.Tests
{
    /// <summary>
    /// Working out which FSR versions an install will make available.
    ///
    /// The bug this covers: the version picker read the library already in the game, so
    /// a game about to receive an FSR 4 INT8 build was offered 3.1.2 and older and FSR 4
    /// looked unavailable at exactly the moment it was being installed. Reported against
    /// v0.30.0 with Star Wars Outlaws, whose own library provides 3.1.2 / 2.3.2 / 1.1.1.
    /// </summary>
    public class FidelityFxProviderScannerTests : IDisposable
    {
        private readonly string _root;

        public FidelityFxProviderScannerTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ffxscan-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        /// <summary>A NUL-delimited provider table, as these binaries carry one.</summary>
        private static byte[] Table(params string[] versions)
        {
            using var buffer = new MemoryStream();
            buffer.WriteByte(0);
            foreach (var v in versions)
            {
                buffer.Write(Encoding.ASCII.GetBytes(v));
                buffer.WriteByte(0);
                buffer.Write(new byte[] { 0x11, 0x22, 0x33, 0x00 });
            }
            return buffer.ToArray();
        }

        private string WriteLibrary(string relativePath, params string[] versions)
        {
            var path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Table(versions));
            return path;
        }

        // ── Finding the library ─────────────────────────────────────────────

        [Fact]
        public void TheUpscalerModuleIsPreferredOverTheOlderMonolith()
        {
            // An SDK 2 install leaves both in place. The upscaler module is the one that
            // decides the FSR version, so it is the one to read.
            WriteLibrary("amd_fidelityfx_dx12.dll", "3.1.2", "2.3.2");
            WriteLibrary("amd_fidelityfx_upscaler_dx12.dll", "4.1.1", "3.1.5");

            var found = FidelityFxProviderScanner.FromDirectory(_root);
            Assert.NotNull(found);
            Assert.Equal("amd_fidelityfx_upscaler_dx12.dll", found.FromFile);
            Assert.Equal(new[] { "4.1.1", "3.1.5" }, found.Versions);
        }

        [Fact]
        public void TheNameARecentCommunityBuildShipsAsIsRead()
        {
            // Newer INT8 releases ship as amdxcffx64.dll rather than under the upscaler
            // module's name.
            WriteLibrary("amdxcffx64.dll", "4.1.1");

            var found = FidelityFxProviderScanner.FromDirectory(_root);
            Assert.Equal("amdxcffx64.dll", found!.FromFile);
            Assert.Equal(new[] { "4.1.1" }, found.Versions);
        }

        [Fact]
        public void ALibraryIsFoundInASubdirectory()
        {
            // Release layouts move between versions, so the search is recursive.
            WriteLibrary(Path.Combine("bin", "x64", "amd_fidelityfx_upscaler_dx12.dll"), "4.0.2");
            Assert.Equal(new[] { "4.0.2" }, FidelityFxProviderScanner.FromDirectory(_root)!.Versions);
        }

        [Fact]
        public void NothingIsClaimedForADirectoryWithNoLibrary()
        {
            Assert.Null(FidelityFxProviderScanner.FromDirectory(_root));
            Assert.Null(FidelityFxProviderScanner.FromDirectory(Path.Combine(_root, "nope")));
            Assert.Null(FidelityFxProviderScanner.FromDirectory(null));
            Assert.Null(FidelityFxProviderScanner.FromDirectory(""));
        }

        [Fact]
        public void AFileThatIsNotAFidelityFxLibraryIsSkipped()
        {
            var path = Path.Combine(_root, "amd_fidelityfx_upscaler_dx12.dll");
            File.WriteAllText(path, "not a binary");
            Assert.Null(FidelityFxProviderScanner.FromDirectory(_root));
        }

        // ── Merging what the install adds with what the game has ────────────

        [Fact]
        public void TheInstallsVersionsAreMergedWithTheGamesOwn()
        {
            // The reported case, with the real numbers. Outlaws provides 3.1.2 and older;
            // the FSR 4.1.1b build being installed provides 4.1.1. The offered list has
            // to lead with 4.1.1, which is what the user knew should be there.
            var merged = FidelityFxProviderScanner.Merge(
                new[] { "4.1.1", "3.1.5", "2.3.4" },
                new[] { "3.1.2", "2.3.2", "1.1.1" });

            Assert.Equal("4.1.1", merged[0]);
            Assert.Equal(new[] { "4.1.1", "3.1.5", "3.1.2", "2.3.4", "2.3.2", "1.1.1" }, merged);
        }

        [Fact]
        public void MergingIsAUnionBecauseAnInstallNeedNotRemoveWhatWasThere()
        {
            // OptiScaler loads whichever library ends up in the folder, and both can be.
            var merged = FidelityFxProviderScanner.Merge(
                new[] { "4.1.1", "3.1.5" },
                new[] { "3.1.5", "3.1.2" });

            Assert.Equal(new[] { "4.1.1", "3.1.5", "3.1.2" }, merged);
        }

        [Fact]
        public void MergingCopesWithNothingToMerge()
        {
            Assert.Empty(FidelityFxProviderScanner.Merge(null, null));
            Assert.Empty(FidelityFxProviderScanner.Merge(Array.Empty<string>(), null));
            Assert.Equal(new[] { "4.1.1" }, FidelityFxProviderScanner.Merge(null, new[] { "4.1.1" }));
        }

        [Fact]
        public void MergedVersionsAreOrderedNumericallyNotAsText()
        {
            // Sorted as text, 3.1.10 falls below 3.1.9 and the picker would name the
            // wrong build as newest.
            Assert.Equal(
                new[] { "3.1.10", "3.1.9", "3.1.2" },
                FidelityFxProviderScanner.Merge(new[] { "3.1.9", "3.1.2" }, new[] { "3.1.10" }));
        }

        // ── The not-yet-downloaded fallback ─────────────────────────────────

        [Fact]
        public void AReleaseTagNamesTheVersionWhenThereIsNoBinaryYet()
        {
            // A build the user picked but has not downloaded has nothing to read. The
            // Extras releases are tagged by the FSR version they carry.
            Assert.Equal("4.1.1", FidelityFxProviderScanner.VersionFromReleaseTag("FSR_4.1.1b"));
            Assert.Equal("4.0.2", FidelityFxProviderScanner.VersionFromReleaseTag("4.0.2d"));
            Assert.Equal("4.1.0", FidelityFxProviderScanner.VersionFromReleaseTag("v4.1.0"));
        }

        [Fact]
        public void ATagWithNoVersionInItClaimsNothing()
        {
            foreach (var tag in new[] { "latest", "nightly", "4.1", "", null })
                Assert.Null(FidelityFxProviderScanner.VersionFromReleaseTag(tag));
        }

        [Fact]
        public void ReadingACachedBuildBeatsReadingItsTag()
        {
            // The tag is the author's label; the binary is the thing that will run. So
            // the tag is only a fallback, and this proves the binary is available to
            // prefer when the build has been downloaded.
            WriteLibrary("amdxcffx64.dll", "4.1.1", "3.1.5");

            var read = FidelityFxProviderScanner.FromDirectory(_root);
            Assert.Equal(2, read!.Versions.Count);

            // The tag alone would have yielded one version and no provider list.
            Assert.Single(new[] { FidelityFxProviderScanner.VersionFromReleaseTag("FSR_4.1.1b")! });
        }
    }
}
