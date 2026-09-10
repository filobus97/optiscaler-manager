// Upscaler Manager - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System;
using System.IO;
using System.Linq;
using UpscalerManager.Core.Components;
using UpscalerManager.Core.Models;
using UpscalerManager.Core.Services;
using Xunit;

namespace UpscalerManager.Core.Tests
{
    /// <summary>
    /// The details view exists to answer "what is in this game and will FSR 4 be used",
    /// so these pin the two things it reads: the components on disk and what the ini
    /// actually says.
    /// </summary>
    [Collection(AppDataCollection.Name)]
    public class GameDetailsTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "osm_details_" + Guid.NewGuid().ToString("N"));

        private readonly ScopedAppData _appData = new();

        public GameDetailsTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            _appData.Dispose();
            try { Directory.Delete(_dir, true); } catch { }
        }

        private void Place(string relative, string? fileVersion = null)
        {
            var path = Path.Combine(_dir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, PeTestData.BuildPe(PeTestData.MachineAmd64, fileVersion));
        }

        private Game Analyze()
        {
            var game = new Game { Name = "Test", InstallPath = _dir };
            new GameAnalyzerService().AnalyzeGame(game, forceRefresh: true);
            return game;
        }

        // ── Catalogue ────────────────────────────────────────────────────────────

        [Fact]
        public void EveryCataloguedFileIsDescribed()
        {
            // A row with no explanation would render as a blank line in the details view.
            foreach (var def in UpscalerCatalog.All)
            {
                Assert.False(string.IsNullOrWhiteSpace(def.Technology), def.FileName);
                Assert.False(string.IsNullOrWhiteSpace(def.Vendor), def.FileName);
                Assert.False(string.IsNullOrWhiteSpace(def.Explanation), def.FileName);
            }
        }

        [Fact]
        public void CatalogueHasNoDuplicateFileNames()
        {
            var dupes = UpscalerCatalog.All.GroupBy(d => d.FileName, StringComparer.OrdinalIgnoreCase)
                                           .Where(g => g.Count() > 1).Select(g => g.Key);
            Assert.Empty(dupes);
        }

        [Fact]
        public void CatalogueCoversTheTechnologiesTheAppInstalls()
        {
            // Everything the Manager can place must be explainable afterwards, or the
            // details view would show files it put there as unknown.
            foreach (var f in new[]
            {
                "amd_fidelityfx_upscaler_dx12.dll", "amdxcffx64.dll",
                "nvapi64.dll", "dlssg_to_fsr3_amd_is_better.dll", "OptiPatcher.asi",
            })
                Assert.NotNull(UpscalerCatalog.For(f));
        }

        // ── Detection ────────────────────────────────────────────────────────────

        [Fact]
        public void ReportsEveryComponent_NotOnePerTechnology()
        {
            // The whole reason for the details view: one game, several FSR libraries.
            Place("amd_fidelityfx_upscaler_dx12.dll");
            Place("amd_fidelityfx_dx12.dll");
            Place(Path.Combine("Engine", "Binaries", "nvngx_dlss.dll"));
            Place("libxess.dll");

            var found = Analyze().DetectedComponents;

            Assert.Equal(4, found.Count);
            Assert.Contains(found, c => c.FileName == "amd_fidelityfx_upscaler_dx12.dll");
            Assert.Contains(found, c => c.FileName == "amd_fidelityfx_dx12.dll");
            Assert.Contains(found, c => c.FileName == "libxess.dll");
        }

        [Fact]
        public void ReportsWhereEachComponentLives_RelativeToTheGame()
        {
            Place(Path.Combine("Engine", "Binaries", "nvngx_dlss.dll"));
            var dlss = Assert.Single(Analyze().DetectedComponents, c => c.FileName == "nvngx_dlss.dll");

            Assert.DoesNotContain(_dir, dlss.RelativePath);           // not an absolute path
            Assert.Contains("Binaries", dlss.RelativePath);
        }

        [Fact]
        public void UnknownFilesAreIgnored()
        {
            Place("some_random_game.dll");
            Assert.Empty(Analyze().DetectedComponents);
        }

        [Fact]
        public void ComponentsAreGroupedByWhatTheyDo()
        {
            Place("nvngx_dlss.dll");
            Place("nvngx_dlssg.dll");
            Place("libxell.dll");

            var found = Analyze().DetectedComponents;
            Assert.Equal(TechRole.Upscaler, found.Single(c => c.FileName == "nvngx_dlss.dll").Role);
            Assert.Equal(TechRole.FrameGeneration, found.Single(c => c.FileName == "nvngx_dlssg.dll").Role);
            Assert.Equal(TechRole.LatencyReduction, found.Single(c => c.FileName == "libxell.dll").Role);

            // Upscalers first — that is the section a player looks at.
            Assert.Equal(TechRole.Upscaler, found.First().Role);
        }

        [Fact]
        public void ReportsTheVersionActuallyOnDisk()
        {
            // The version is the whole point: "FSR is present" is not useful, "FSR
            // 4.1.1 is present" is.
            Place("amd_fidelityfx_upscaler_dx12.dll", "4.1.1.0");
            var fsr = Assert.Single(Analyze().DetectedComponents);
            Assert.StartsWith("4.1.1", fsr.Version);
        }

        [Fact]
        public void SameTechnologyAtDifferentVersionsIsReportedSeparately()
        {
            // A game can ship one FSR library and have another installed beside it;
            // collapsing them to a single "FSR version" hides the one that matters.
            Place("amd_fidelityfx_upscaler_dx12.dll", "4.1.1.0");
            Place(Path.Combine("bin", "ffx_fsr3_api_x64.dll"), "3.1.5.0");

            var found = Analyze().DetectedComponents;
            Assert.Equal(2, found.Count);
            Assert.StartsWith("4.1.1", found.Single(c => c.FileName == "amd_fidelityfx_upscaler_dx12.dll").Version);
            Assert.StartsWith("3.1.5", found.Single(c => c.FileName == "ffx_fsr3_api_x64.dll").Version);
        }

        [Fact]
        public void AFileWithNoVersionResourceReportsNoVersion()
        {
            // Better a blank than a made-up "0.0.0.0" in the UI.
            Place("libxess.dll");
            Assert.Null(Assert.Single(Analyze().DetectedComponents).Version);
        }

        [Fact]
        public void FilesTheGameShippedAreNotCreditedToTheManager()
        {
            Place("nvngx_dlss.dll");
            Assert.Equal(ComponentSource.Unattributed,
                Assert.Single(Analyze().DetectedComponents).Source);
        }

        // ── Version labels ───────────────────────────────────────────────────────

        [Theory]
        [InlineData("3.7.0.0", "3.7")]
        [InlineData("4.1.1.0", "4.1.1")]
        [InlineData("1.3.0.0", "1.3")]
        [InlineData("1.0.0.0", "1.0")]
        [InlineData("3.1.5", "3.1.5")]
        // The one that matters: trimming zero characters rather than whole groups
        // turned 3.7.10 into 3.7.1 — a real version, and the wrong one.
        [InlineData("3.7.10.0", "3.7.10")]
        [InlineData("10.0.0.0", "10.0")]
        public void ShortensVersionsWithoutChangingThem(string raw, string expected)
            => Assert.Equal(expected, VersionLabel.Short(raw));

        // ── Effective configuration ──────────────────────────────────────────────

        private void WriteIni(string body) => File.WriteAllText(Path.Combine(_dir, "OptiScaler.ini"), body);

        private string ValueOf(string label) =>
            OptiScalerConfigReader.Read(_dir).Single(f => f.Label == label).Value;

        [Fact]
        public void NoIni_ReadsAsNothing()
        {
            Assert.False(OptiScalerConfigReader.Exists(_dir));
            Assert.Empty(OptiScalerConfigReader.Read(_dir));
        }

        [Fact]
        public void SaysWhichUpscalerWillActuallyRun()
        {
            // Both spellings are the FSR path; the name changed between releases.
            WriteIni("[Upscalers]\nDx12Upscaler=ffx\n");
            Assert.Contains("FSR", ValueOf("Upscaler in use"));

            WriteIni("[Upscalers]\nDx12Upscaler=fsr31\n");
            Assert.Contains("FSR", ValueOf("Upscaler in use"));
        }

        [Fact]
        public void CallsOutThatAutoIsNotFsr()
        {
            // "auto" reading as FSR is exactly the misunderstanding that hid the bug
            // where FSR 4 was never selected, so it must not look like a win.
            WriteIni("[Upscalers]\nDx12Upscaler=auto\n");
            var value = ValueOf("Upscaler in use");
            Assert.Contains("OptiScaler", value);
            Assert.Contains("XeSS", value);
        }

        [Fact]
        public void ReportsFsr4AndFrameGenerationInWords()
        {
            WriteIni("[FSR]\nFsr4Update=true\nFsr4ForceEnableInt8=true\n\n[FrameGen]\nFGInput=nukems\n");
            Assert.Equal("Yes", ValueOf("Upgrade FSR 3 to FSR 4"));
            Assert.Contains("INT8", ValueOf("Force FSR 4 on unsupported GPUs"));
            Assert.Contains("Nukem", ValueOf("Frame generation"));
        }

        [Fact]
        public void ReportsNvidiaOverrideAndOverlayKey()
        {
            WriteIni("[Spoofing]\nDxgi=true\n\n[Menu]\nShortcutKey=0x78\n");
            Assert.Contains("RTX 4090", ValueOf("Nvidia override"));
            Assert.Equal("F9", ValueOf("Overlay key"));

            WriteIni("[Plugins]\nLoadAsiPlugins=true\n");
            Assert.Contains("OptiPatcher", ValueOf("Nvidia override"));
        }

        [Fact]
        public void CommentsAndBlankLinesDoNotConfuseTheReader()
        {
            WriteIni("; a comment\n\n[FSR]\n; another\nFsr4Update = true   \n");
            Assert.Equal("Yes", ValueOf("Upgrade FSR 3 to FSR 4"));
        }

        [Fact]
        public void EveryFactCitesTheKeyBehindIt()
        {
            WriteIni("[FSR]\nFsr4Update=true\n");
            Assert.All(OptiScalerConfigReader.Read(_dir),
                f => Assert.False(string.IsNullOrWhiteSpace(f.Source)));
        }
    }
}
