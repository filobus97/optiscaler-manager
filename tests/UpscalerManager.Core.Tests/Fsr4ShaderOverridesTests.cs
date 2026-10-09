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
        [InlineData(RadeonGeneration.Rdna3, FasterFsr4Kind.Exact, "amd_fidelityfx_upscaler_dx12.dll")]
        [InlineData(RadeonGeneration.Rdna3, FasterFsr4Kind.Lossy, "test-lossy.zip")]
        [InlineData(RadeonGeneration.Rdna2, FasterFsr4Kind.Exact, "test-rdna2.zip")]
        [InlineData(RadeonGeneration.Rdna2, FasterFsr4Kind.Lossy, "test-lossy-rdna2.zip")]
        [InlineData(RadeonGeneration.SteamDeck, FasterFsr4Kind.Exact, "test-rdna2-compact.zip")]
        [InlineData(RadeonGeneration.SteamDeck, FasterFsr4Kind.Lossy, "test-lossy-rdna2-compact.zip")]
        [InlineData(RadeonGeneration.Rdna3Integrated, FasterFsr4Kind.Exact, "test-igpu.zip")]
        [InlineData(RadeonGeneration.Rdna3Integrated, FasterFsr4Kind.Lossy, "test-lossy-igpu.zip")]
        public void EachGenerationGetsTheBuildMadeForIt(RadeonGeneration generation, FasterFsr4Kind kind, string asset)
        {
            Assert.Equal(asset, Fsr4ShaderOverrides.AssetFor(generation, kind));
            Assert.Null(Fsr4ShaderOverrides.RefusalFor(generation));
        }

        [Theory]
        [InlineData(RadeonGeneration.Rdna4)]
        [InlineData(RadeonGeneration.NotRadeon)]
        [InlineData(RadeonGeneration.Rdna2Integrated)]
        [InlineData(RadeonGeneration.Unknown)]
        public void EverythingElseIsRefusedWithAReason(RadeonGeneration generation)
        {
            Assert.NotNull(Fsr4ShaderOverrides.RefusalFor(generation));
            Assert.Null(Fsr4ShaderOverrides.AssetFor(generation, FasterFsr4Kind.Exact));
            Assert.Null(Fsr4ShaderOverrides.AssetFor(generation, FasterFsr4Kind.Lossy));
        }

        [Fact]
        public void Rx9000IsRefusedBecauseTheCheckIsLifted()
            => Assert.Contains("RX 9000", Fsr4ShaderOverrides.RefusalFor(RadeonGeneration.Rdna4));

        [Theory]
        [InlineData(RadeonGeneration.Rdna3, FasterFsr4Kind.Exact, "4.1.1-r3-cyboman")]
        [InlineData(RadeonGeneration.Rdna3, FasterFsr4Kind.Lossy, "4.1.1-r3-lossy-cyboman")]
        [InlineData(RadeonGeneration.SteamDeck, FasterFsr4Kind.Exact, "4.1.1-r2c-cyboman")]
        [InlineData(RadeonGeneration.Rdna3Integrated, FasterFsr4Kind.Lossy, "4.1.1-ig-lossy-cyboman")]
        public void TheOverlayLabelIsTheOneTheAuthorPublishes(RadeonGeneration generation, FasterFsr4Kind kind, string label)
            => Assert.Equal(label, Fsr4ShaderOverrides.OverlayLabel(generation, kind));

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

            var release = ComponentManagementService.ParseFsr4OverridesReleases(json);

            Assert.NotNull(release);
            Assert.Equal("dll-2026-10-07", release!.Value.Tag);
            var dll = Assert.Single(release.Value.Assets, a => a.Name == "amd_fidelityfx_upscaler_dx12.dll");
            Assert.Equal("abcdef", dll.Sha256);
            Assert.Null(Assert.Single(release.Value.Assets, a => a.Name == "test-lossy.zip").Sha256);
        }

        [Fact]
        public void NoDllReleaseMeansNothingToOffer()
            => Assert.Null(ComponentManagementService.ParseFsr4OverridesReleases(
                """[ { "tag_name": "timing-kit-1", "assets": [] } ]"""));
    }
}
