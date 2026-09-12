// Upscaler Manager - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System;
using System.IO;
using System.Linq;
using UpscalerManager.Core.Components;
using UpscalerManager.Core.Services;
using Xunit;

namespace UpscalerManager.Core.Tests
{
    /// <summary>
    /// The DLSS Swapper archive as a download source.
    ///
    /// The request cannot be exercised offline, so what is tested here is the part that
    /// actually breaks: reading a manifest written by somebody else's project, which can
    /// change shape without warning. The fixtures below are records copied verbatim out
    /// of the live manifest rather than invented, so a shape change shows up as a real
    /// failure instead of a test that agrees with a guess.
    /// </summary>
    [Collection(AppDataCollection.Name)]
    public class DllRepositoryTests : IDisposable
    {
        private readonly ScopedAppData _appData = new();

        public void Dispose() => _appData.Dispose();

        /// <summary>
        /// Two real <c>fsr_31_dx12</c> records and one real <c>dlss</c> record, as the
        /// live manifest serves them. 1.0.1.38338 is a build seen in the wild — it is
        /// what Star Wars Outlaws ships — which is the point: this is the file AMD does
        /// not publish loose, so before this source the only builds available were
        /// whichever ones a user's other games happened to carry.
        /// </summary>
        private const string RealManifestExcerpt = """
        {
          "dlss": [
            {
              "version": "310.9.1.0",
              "version_number": 87304723644776448,
              "internal_name": "CL 39275453",
              "internal_name_extra": "",
              "additional_label": "",
              "md5_hash": "5E1FCE0BFB18F2A2FBEE6AFB7A5A2E6C",
              "zip_md5_hash": "9F86D081884C7D659A2FEAA0C55AD015",
              "download_url": "https://dlss-swapper-downloads.beeradmoore.com/dlss/dlss_v310.9.1.0_5E1FCE0BFB18F2A2FBEE6AFB7A5A2E6C.zip",
              "file_description": "NGX DLSS - DVS PRODUCTION",
              "signed_datetime": "2025-08-01T00:00:00Z",
              "is_signature_valid": true,
              "is_dev_file": false,
              "file_size": 58000000,
              "zip_file_size": 50000000,
              "dll_source": "TechPowerUp"
            }
          ],
          "fsr_31_dx12": [
            {
              "version": "1.0.1.38338",
              "version_number": 281474976747408,
              "internal_name": "3.1.1",
              "internal_name_extra": "2.3.2",
              "additional_label": "3.1.1",
              "md5_hash": "46F7049B30404D8C4EF685A13EF0BC43",
              "zip_md5_hash": "D855E93AA6156C4BB45D2E0924EDE72B",
              "download_url": "https://dlss-swapper-downloads.beeradmoore.com/fsr_31_dx12/fsr_31_dx12_v1.0.1.38338_46F7049B30404D8C4EF685A13EF0BC43.zip",
              "file_description": "DX12 AMD FidelityFX Library",
              "signed_datetime": "0001-01-01T00:00:00",
              "is_signature_valid": true,
              "is_dev_file": false,
              "file_size": 6475992,
              "zip_file_size": 2406675,
              "dll_source": "Horizon Zero Dawn Remastered"
            },
            {
              "version": "1.0.1.41314",
              "version_number": 281474976750384,
              "internal_name": "3.1.4",
              "internal_name_extra": "2.3.2",
              "additional_label": "3.1.4",
              "md5_hash": "CFCD38F47665DDFF195B80C62E3B57E2",
              "zip_md5_hash": "6E0F9EDDE4C3604AB32F257DF59F8F00",
              "download_url": "https://dlss-swapper-downloads.beeradmoore.com/fsr_31_dx12/fsr_31_dx12_v1.0.1.41314_CFCD38F47665DDFF195B80C62E3B57E2.zip",
              "file_description": "DX12 AMD FidelityFX Library",
              "signed_datetime": "0001-01-01T00:00:00",
              "is_signature_valid": true,
              "is_dev_file": false,
              "file_size": 6470360,
              "zip_file_size": 2395294,
              "dll_source": "F1 2024"
            }
          ],
          "directstorage": [],
          "known_dlls": {}
        }
        """;

        private static DllRepository.RepositoryFile FileFor(string fileName) =>
            DllRepository.For(fileName)
            ?? throw new InvalidOperationException($"{fileName} is not covered by the archive.");

        // ── Reading the index ────────────────────────────────────────────────

        [Fact]
        public void BuildsAreReadOutOfTheLiveManifestShape()
        {
            var builds = DllRepositoryService.ParseBuilds(
                RealManifestExcerpt, FileFor("amd_fidelityfx_dx12.dll"));

            Assert.Equal(2, builds.Count);

            var outlaws = builds.Single(b => b.Version == "1.0.1.38338");
            Assert.Equal("amd_fidelityfx_dx12.dll", outlaws.FileName);
            Assert.Equal("3.1.1", outlaws.Label);
            Assert.Equal("Horizon Zero Dawn Remastered", outlaws.Provenance);
            Assert.Equal("D855E93AA6156C4BB45D2E0924EDE72B", outlaws.ZipMd5);
            Assert.Equal("46F7049B30404D8C4EF685A13EF0BC43", outlaws.DllMd5);
            Assert.Equal(2406675, outlaws.ZipFileSize);
            Assert.True(outlaws.SignatureValid);
            Assert.False(outlaws.IsDevFile);
        }

        [Fact]
        public void TheVersionKeyedOnIsTheOneReadOffARealFile()
        {
            // The library, the "in the game now" row and the "installed" marker all key
            // on the version the binary reports. Keying a row on the marketing label
            // instead — 3.1.1 rather than 1.0.1.38338 — would make every row claim to
            // be new.
            var builds = DllRepositoryService.ParseBuilds(
                RealManifestExcerpt, FileFor("amd_fidelityfx_dx12.dll"));

            Assert.All(builds, b => Assert.Matches(@"^\d+(\.\d+){3}$", b.Version));
        }

        [Fact]
        public void ADlssRecordWithNoLabelFallsBackToItsInternalName()
        {
            // DLSS records leave additional_label empty and carry a branch label in
            // internal_name, so a row with no label at all would drop information the
            // manifest does have.
            var build = Assert.Single(DllRepositoryService.ParseBuilds(
                RealManifestExcerpt, FileFor("nvngx_dlss.dll")));

            Assert.Equal("310.9.1.0", build.Version);
            Assert.Equal("CL 39275453", build.Label);
        }

        [Fact]
        public void AnEmptySectionYieldsNothingRatherThanFailing()
        {
            // The live manifest carries directstorage as an empty list, and DLSS
            // Swapper's own model does not even declare it. Extra and empty keys are
            // normal, not an error.
            Assert.Empty(DllRepositoryService.ParseBuilds(
                RealManifestExcerpt, new DllRepository.RepositoryFile("directstorage", "x.dll", "Microsoft")));
        }

        [Fact]
        public void AKeyTheManifestDoesNotHaveYieldsNothing()
        {
            Assert.Empty(DllRepositoryService.ParseBuilds(
                RealManifestExcerpt, new DllRepository.RepositoryFile("not_a_key", "x.dll", "Nobody")));
        }

        [Fact]
        public void ARecordMissingWhatAnInstallNeedsIsSkipped()
        {
            // No URL is nothing to fetch; no zip hash is no way to tell a truncated
            // transfer from a good one. Either way the row cannot be honoured, so it is
            // not offered — the remaining good records still are.
            const string json = """
            {
              "fsr_31_dx12": [
                { "version": "1.0.0.1", "zip_md5_hash": "AA", "download_url": "" },
                { "version": "1.0.0.2", "zip_md5_hash": "", "download_url": "https://example.invalid/a.zip" },
                { "version": "", "zip_md5_hash": "BB", "download_url": "https://example.invalid/b.zip" },
                { "version": "1.0.0.4", "zip_md5_hash": "CC", "download_url": "https://example.invalid/c.zip" }
              ]
            }
            """;

            var build = Assert.Single(DllRepositoryService.ParseBuilds(
                json, FileFor("amd_fidelityfx_dx12.dll")));
            Assert.Equal("1.0.0.4", build.Version);
        }

        [Fact]
        public void AnErrorPageInsteadOfTheIndexYieldsNothing()
        {
            // GitHub Pages serving HTML, or a captive portal, must not throw out of a
            // listing: this is an extra route and every other source on the page has to
            // keep working.
            Assert.Empty(DllRepositoryService.ParseBuilds(
                "<html><body>404</body></html>", FileFor("nvngx_dlss.dll")));
            Assert.Empty(DllRepositoryService.ParseBuilds(
                "[1,2,3]", FileFor("nvngx_dlss.dll")));
            Assert.Empty(DllRepositoryService.ParseBuilds(
                "", FileFor("nvngx_dlss.dll")));
        }

        [Fact]
        public void AWrongTypeWhereAStringBelongsIsIgnoredRatherThanThrowing()
        {
            const string json = """
            { "xess": [ { "version": 42, "zip_md5_hash": "AA", "download_url": "https://example.invalid/a.zip",
                          "dll_source": null, "file_size": "big" } ] }
            """;
            Assert.Empty(DllRepositoryService.ParseBuilds(json, FileFor("libxess.dll")));
        }

        // ── What the archive claims to cover ────────────────────────────────

        [Fact]
        public void EveryFileTheArchiveOffersIsOneWeCanActuallySwap()
        {
            // A download that installed a file the swapper does not recognise would be
            // fetched, imported, and then never offered anywhere.
            foreach (var file in DllRepository.All)
                Assert.True(SwappableDlls.IsSwappable(file.FileName),
                    $"{file.FileName} is offered for download but is not swappable.");
        }

        [Fact]
        public void TheArchiveCoversNineFilesWithDistinctKeys()
        {
            Assert.Equal(9, DllRepository.All.Count);
            Assert.Equal(9, DllRepository.All.Select(f => f.ManifestKey).Distinct().Count());
            Assert.Equal(9, DllRepository.All.Select(f => f.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public void TheFidelityFxRuntimesAreCoveredBecauseAmdPublishesNoneOfThem()
        {
            // This is the gap the archive exists to fill here. Neither runtime is
            // downloadable from AMD, so without it the only builds a user could reach
            // were whatever their other games shipped.
            Assert.True(DllRepository.Covers("amd_fidelityfx_dx12.dll"));
            Assert.True(DllRepository.Covers("amd_fidelityfx_vk.dll"));
            Assert.Null(VendorDllSource.For("amd_fidelityfx_dx12.dll"));
            Assert.Null(VendorDllSource.For("amd_fidelityfx_vk.dll"));
        }

        [Fact]
        public void TheArchiveHasNoFsr4AndMustNotPretendOtherwise()
        {
            // Checked against the live manifest: it carries nine sections and none of
            // them is FSR 4. Adding a key for one would produce a section that lists
            // nothing and a user who concludes no FSR 4 build exists — the community
            // releases are the route to those files.
            Assert.False(DllRepository.Covers("amdxcffx64.dll"));
            Assert.False(DllRepository.Covers("amd_fidelityfx_upscaler_dx12.dll"));
            foreach (var name in Fsr4Int8Build.KnownDllNames)
                Assert.False(DllRepository.Covers(name),
                    $"{name} is an FSR 4 filename and the archive holds no FSR 4 builds.");
        }

        [Fact]
        public void FileNamesAreMatchedCaseInsensitively()
        {
            // A game on ext4 shipping NvNgx_Dlss.dll is the case DLSS Swapper's own
            // exact-case detection misses.
            Assert.True(DllRepository.Covers("NVNGX_DLSS.DLL"));
            Assert.True(DllRepository.Covers("Amd_FidelityFX_Dx12.dll"));
            Assert.False(DllRepository.Covers(null));
        }

        // ── Rows ────────────────────────────────────────────────────────────

        [Fact]
        public void ProvenanceRecordsBothTheMirrorAndWhereTheFileCameFrom()
        {
            // The archive is a mirror of files scraped from elsewhere, so a library row
            // saying only "downloaded" would lose the half that matters for judging it.
            var build = DllRepositoryService.ParseBuilds(
                RealManifestExcerpt, FileFor("amd_fidelityfx_dx12.dll"))
                .Single(b => b.Version == "1.0.1.38338");

            var label = DllRepositoryService.SourceLabelFor(build);
            Assert.Contains(DllRepository.SourceName, label);
            Assert.Contains("Horizon Zero Dawn Remastered", label);
        }

        [Fact]
        public void ProvenanceStillNamesTheMirrorWhenTheIndexRecordsNoOrigin()
        {
            const string json = """
            { "xell": [ { "version": "1.0.0.1", "zip_md5_hash": "AA",
                          "download_url": "https://example.invalid/a.zip" } ] }
            """;
            var build = DllRepositoryService.ParseBuilds(json, FileFor("libxell.dll")).Single();

            Assert.Equal($"from the {DllRepository.SourceName} archive",
                DllRepositoryService.SourceLabelFor(build));
        }

        [Fact]
        public void SizesAreDescribedInUnitsAPlayerReads()
        {
            Assert.Equal(string.Empty, DllRepositoryService.DescribeSize(0));
            Assert.Equal(string.Empty, DllRepositoryService.DescribeSize(-1));
            Assert.Equal("2.3 MB", DllRepositoryService.DescribeSize(2406675));
            Assert.Equal("47.7 MB", DllRepositoryService.DescribeSize(50000000));
            Assert.Equal("12 KB", DllRepositoryService.DescribeSize(12288));
        }

        // ── The index cache ─────────────────────────────────────────────────

        [Fact]
        public void TheCachedIndexLivesUnderTheAppsOwnCacheAndCanBeDeleted()
        {
            // The Storage page offers to delete it, so it has to be somewhere the app
            // owns rather than beside the user's games.
            Assert.StartsWith(AppDataPaths.Cache, DllRepositoryService.Root);

            Directory.CreateDirectory(DllRepositoryService.Root);
            var indexPath = Path.Combine(DllRepositoryService.Root, "manifest.json");
            File.WriteAllText(indexPath, "{}");

            DllRepositoryService.ForgetCachedIndex();
            Assert.False(File.Exists(indexPath));
        }

        [Fact]
        public void ForgettingAnIndexThatIsNotThereIsNotAnError()
        {
            DllRepositoryService.ForgetCachedIndex();
            DllRepositoryService.ForgetCachedIndex();
        }
    }
}
