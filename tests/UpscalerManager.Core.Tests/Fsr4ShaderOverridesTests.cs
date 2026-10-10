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
    /// The faster FSR 4.1.1 builds replace AMD's DLL with one whose GPU check is lifted,
    /// so which build reaches which card, and what it may replace, is pinned here.
    /// </summary>
    public class Fsr4ShaderOverridesTests : IDisposable
    {
        private readonly string _game = Path.Combine(Path.GetTempPath(), "osm_fsr4o_" + Guid.NewGuid().ToString("N"));

        public Fsr4ShaderOverridesTests() => Directory.CreateDirectory(_game);
        public void Dispose() { try { Directory.Delete(_game, true); } catch { } }

        [Theory]
        [InlineData(RadeonGeneration.Rdna3, "fsr4.1.1-cyboman.zip")]
        [InlineData(RadeonGeneration.Rdna2, "fsr4.1.1-cyboman.zip")]
        [InlineData(RadeonGeneration.SteamDeck, "fsr4.1.1-igpu-cyboman.zip")]
        [InlineData(RadeonGeneration.Rdna3Integrated, "fsr4.1.1-igpu-cyboman.zip")]
        [InlineData(RadeonGeneration.Rdna2Integrated, "fsr4.1.1-igpu-cyboman.zip")]
        public void CardsAndIntegratedGraphicsGetTheirOwnZip(RadeonGeneration generation, string asset)
        {
            // Since dll-2026-10-09 the project ships two zips instead of a file per family.
            Assert.Equal(asset, Fsr4ShaderOverrides.AssetFor(generation));
            Assert.Null(Fsr4ShaderOverrides.RefusalFor(generation));
        }

        [Theory]
        [InlineData(RadeonGeneration.Rdna4)]
        [InlineData(RadeonGeneration.NotRadeon)]
        [InlineData(RadeonGeneration.Unknown)]
        public void EverythingElseIsRefusedWithAReason(RadeonGeneration generation)
        {
            Assert.NotNull(Fsr4ShaderOverrides.RefusalFor(generation));
            Assert.Null(Fsr4ShaderOverrides.AssetFor(generation));
        }

        [Fact]
        public void Rx9000IsRefusedBecauseTheCheckIsLifted()
            => Assert.Contains("RX 9000", Fsr4ShaderOverrides.RefusalFor(RadeonGeneration.Rdna4));

        [Theory]
        [InlineData(RadeonGeneration.Rdna3, FasterFsr4Kind.Exact, "4.1.1-cyboman")]
        [InlineData(RadeonGeneration.Rdna2, FasterFsr4Kind.Lossy, "4.1.1-lossy-cyboman")]
        [InlineData(RadeonGeneration.SteamDeck, FasterFsr4Kind.Exact, "4.1.1-igpu-cyboman")]
        [InlineData(RadeonGeneration.Rdna3Integrated, FasterFsr4Kind.Lossy, "4.1.1-igpu-lossy-cyboman")]
        public void TheOverlayLabelIsTheOneTheAuthorPublishes(RadeonGeneration generation, FasterFsr4Kind kind, string label)
            => Assert.Equal(label, Fsr4ShaderOverrides.OverlayLabel(generation, kind));

        [Theory]
        [InlineData(FasterFsr4Kind.Exact, "MZexact")]
        [InlineData(FasterFsr4Kind.Lossy, "MZlossy")]
        public void TheFolderInTheZipPicksTheBuild(FasterFsr4Kind kind, string content)
        {
            // Both builds share one file name; the exact/ and lossy/ folders tell them apart.
            var zip = Path.Combine(_game, "release.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            {
                foreach (var (entry, text) in new[]
                         {
                             ("exact/amd_fidelityfx_upscaler_dx12.dll", "MZexact"),
                             ("lossy/amd_fidelityfx_upscaler_dx12.dll", "MZlossy"),
                             ("lossy/WARNING.txt", "read me"),
                         })
                {
                    using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
                    writer.Write(text);
                }
            }

            var dest = Path.Combine(_game, "out.dll");
            ComponentManagementService.ExtractFsr4Override(zip, kind, dest);

            Assert.Equal(content, File.ReadAllText(dest));
        }

        [Fact]
        public void OnlyAmdsOwn411BuildMayBeReplaced()
        {
            Assert.Null(Fsr4ShaderOverrides.BaseMismatch("4.1.1.2740"));
            Assert.Contains("4.1.0.0", Fsr4ShaderOverrides.BaseMismatch("4.1.0.0"));
            Assert.NotNull(Fsr4ShaderOverrides.BaseMismatch(null));
        }

        [Fact]
        public void ADownloadedBuildMustBeA64BitDllOfThatVersion()
        {
            string Dll(byte[] bytes) { var p = Path.Combine(_game, Guid.NewGuid().ToString("N") + ".dll"); File.WriteAllBytes(p, bytes); return p; }

            Assert.Null(ComponentManagementService.Fsr4OverrideProblem(Dll(PeTestData.BuildPe(fileVersion: "4.1.1.2740"))));
            Assert.Contains("4.1.0.604", ComponentManagementService.Fsr4OverrideProblem(Dll(PeTestData.BuildPe(fileVersion: "4.1.0.604"))));
            Assert.Contains("64-bit", ComponentManagementService.Fsr4OverrideProblem(
                Dll(PeTestData.BuildPe(PeTestData.MachineI386, fileVersion: "4.1.1.2740"))));
        }

        [Fact]
        public void TheUpscalerReplacedIsTheOneOptiScalerLoads()
        {
            // Flat release: the game folder.
            Assert.Equal(Path.Combine(_game, "amd_fidelityfx_upscaler_dx12.dll"),
                GameInstallationService.OptiScalerUpscalerPath(_game));

            // A release with its own subfolder loads from there.
            Directory.CreateDirectory(Path.Combine(_game, "OptiScaler"));
            Assert.Equal(Path.Combine(_game, "OptiScaler", "amd_fidelityfx_upscaler_dx12.dll"),
                GameInstallationService.OptiScalerUpscalerPath(_game));

            // And [Libraries] FfxDx12SRPath, when it names something that exists, comes first.
            Directory.CreateDirectory(Path.Combine(_game, "ffx"));
            File.WriteAllText(Path.Combine(_game, "OptiScaler.ini"), "[Libraries]\nFfxDx12SRPath=ffx\n");
            Assert.Equal(Path.Combine(_game, "ffx", "amd_fidelityfx_upscaler_dx12.dll"),
                GameInstallationService.OptiScalerUpscalerPath(_game));
        }

        [Fact]
        public void ThePreviewSaysWhichBuildReplacesOptiScalers()
        {
            var exact = ComponentRegistry.BuildInstallPreview(Fsr4Backend.FasterShaders, UpscalerSelection.NewestFsr);
            var lossy = ComponentRegistry.BuildInstallPreview(Fsr4Backend.FasterShaders, UpscalerSelection.NewestFsr,
                fasterKind: FasterFsr4Kind.Lossy);

            Assert.Contains(exact.Files, f => f.Contains("exact") && f.Contains("replaces OptiScaler's"));
            Assert.Contains(lossy.Files, f => f.Contains("lossy"));
            Assert.Single(exact.Files, f => f.StartsWith("amd_fidelityfx_upscaler_dx12.dll"));
            Assert.Empty(exact.Conflicts);
        }

        [Fact]
        public void TheNewestDllReleaseIsReadWithGitHubsChecksums()
        {
            // Shaped like GitHub's /releases response: newest first, the timing kit's own
            // release in between, and a digest on every asset.
            const string json = """
            [
              { "tag_name": "timing-kit-2026-10-08", "draft": false,
                "assets": [ { "name": "timing-kit.tar.gz", "browser_download_url": "https://example/kit", "digest": "sha256:aa" } ] },
              { "tag_name": "dll-2026-10-07", "draft": false,
                "assets": [
                  { "name": "amd_fidelityfx_upscaler_dx12.dll", "browser_download_url": "https://example/dll", "digest": "sha256:ABCDEF" },
                  { "name": "test-lossy.zip", "browser_download_url": "https://example/lossy", "digest": null } ] },
              { "tag_name": "dll-2026-10-06.6", "draft": false,
                "assets": [ { "name": "amd_fidelityfx_upscaler_dx12.dll", "browser_download_url": "https://example/old" } ] }
            ]
            """;

            var releases = ComponentManagementService.ParseFsr4OverridesReleases(json);

            Assert.Equal(new[] { "dll-2026-10-07", "dll-2026-10-06.6" }, releases.Select(r => r.Tag));
            var dll = Assert.Single(releases[0].Assets, a => a.Name == "amd_fidelityfx_upscaler_dx12.dll");
            Assert.Equal("abcdef", dll.Sha256);
            Assert.Null(Assert.Single(releases[0].Assets, a => a.Name == "test-lossy.zip").Sha256);
        }

        [Fact]
        public void TheNewestReleaseThatHasTheFileWins()
        {
            // The project renamed its downloads once already; a release without the file
            // this app expects is passed over rather than failing the install.
            var releases = new[]
            {
                new GitHubRelease("dll-2026-10-20", new[] { new ReleaseAsset("renamed.zip", "https://example/r", null) }),
                new GitHubRelease("dll-2026-10-09", new[] { new ReleaseAsset("fsr4.1.1-cyboman.zip", "https://example/a", "aa") }),
                new GitHubRelease("dll-2026-10-07", new[] { new ReleaseAsset("test-rdna2.zip", "https://example/b", "bb") }),
            };

            var picked = ComponentManagementService.PickFsr4OverridesRelease(releases, "fsr4.1.1-cyboman.zip");

            Assert.NotNull(picked);
            Assert.Equal("dll-2026-10-09", picked!.Value.Release.Tag);
            Assert.Null(ComponentManagementService.PickFsr4OverridesRelease(releases, "nowhere.zip"));
        }

        [Fact]
        public void NoDllReleaseMeansNothingToOffer()
            => Assert.Empty(ComponentManagementService.ParseFsr4OverridesReleases(
                """[ { "tag_name": "timing-kit-1", "assets": [] } ]"""));
    }
}
