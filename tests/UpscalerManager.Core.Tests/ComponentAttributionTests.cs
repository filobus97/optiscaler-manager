// Upscaler Manager - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System;
using System.IO;
using System.Linq;
using UpscalerManager.Core.Models;
using UpscalerManager.Core.Services;
using Xunit;

namespace UpscalerManager.Core.Tests
{
    /// <summary>
    /// The details page credits a file to this app only when the install manifest says so.
    ///
    /// These exist because the previous test only checked the negative case — that a
    /// game's own file is not credited to the app — which passed happily while the
    /// positive case was broken for every install, and every row read "came with the
    /// game". Both directions are pinned here, including the nested install directory
    /// that made the paths fail to line up.
    /// </summary>
    [Collection(AppDataCollection.Name)]
    public class ComponentAttributionTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "osm_attrib_" + Guid.NewGuid().ToString("N"));
        private readonly string? _previousRoot = AppDataPaths.RootOverride;
        private readonly string _gameDir;

        public ComponentAttributionTests()
        {
            _gameDir = Path.Combine(_root, "game");
            Directory.CreateDirectory(_gameDir);
            // Keep the backup store inside the scratch directory. An explicit override,
            // not XDG_CONFIG_HOME, which Windows ignores.
            AppDataPaths.RootOverride = Path.Combine(_root, "config", "UpscalerManager");
        }

        public void Dispose()
        {
            AppDataPaths.RootOverride = _previousRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private void Place(string relative)
        {
            var path = Path.Combine(_gameDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, PeTestData.BuildPe(PeTestData.MachineAmd64, "1.2.3.4"));
        }

        /// <summary>
        /// Writes a committed manifest the way an install does: paths relative to the
        /// directory the files were written to, which is not always the game root.
        /// </summary>
        private void WriteManifest(string installedDirectory, params string[] installedFiles)
        {
            var store = new BackupStoreService();
            var manifest = new InstallationManifest
            {
                OperationStatus = "committed",
                OptiscalerVersion = "0.9.4",
                InstalledGameDirectory = installedDirectory,
                InstalledFiles = installedFiles.ToList(),
            };
            store.SaveManifest(_gameDir, manifest);
        }

        private Game Analyze()
        {
            var game = new Game { Name = "Test", InstallPath = _gameDir };
            new GameAnalyzerService().AnalyzeGame(game, forceRefresh: true);
            return game;
        }

        private static ComponentSource SourceOf(Game game, string fileName) =>
            game.DetectedComponents.Single(c =>
                c.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase)).Source;

        [Fact]
        public void AFileTheManifestRecordsIsCreditedToTheApp()
        {
            Place("dlssg_to_fsr3_amd_is_better.dll");
            WriteManifest(_gameDir, "dlssg_to_fsr3_amd_is_better.dll");

            Assert.Equal(ComponentSource.Manager, SourceOf(Analyze(), "dlssg_to_fsr3_amd_is_better.dll"));
        }

        [Fact]
        public void AFileTheManifestDoesNotRecordIsNotCreditedToTheApp()
        {
            Place("nvngx_dlss.dll");
            Place("dlssg_to_fsr3_amd_is_better.dll");
            WriteManifest(_gameDir, "dlssg_to_fsr3_amd_is_better.dll");

            var game = Analyze();
            Assert.Equal(ComponentSource.Manager, SourceOf(game, "dlssg_to_fsr3_amd_is_better.dll"));
            Assert.Equal(ComponentSource.Unattributed, SourceOf(game, "nvngx_dlss.dll"));
        }

        [Fact]
        public void AttributionWorksWhenTheInstallDirectoryIsNested()
        {
            // The case that was broken in the wild: OptiScaler installed into
            // <Game>/SB/Binaries/Win64, so manifest paths are relative to *that*, while
            // the scan walks the game root. Resolving them against the wrong anchor —
            // or not resolving them at all — credited nothing to the app.
            var nested = Path.Combine(_gameDir, "SB", "Binaries", "Win64");
            Directory.CreateDirectory(nested);

            Place(Path.Combine("SB", "Binaries", "Win64", "dlssg_to_fsr3_amd_is_better.dll"));
            Place(Path.Combine("SB", "Binaries", "Win64", "libxess.dll"));
            WriteManifest(nested, "dlssg_to_fsr3_amd_is_better.dll");

            var game = Analyze();
            Assert.Equal(ComponentSource.Manager, SourceOf(game, "dlssg_to_fsr3_amd_is_better.dll"));
            Assert.Equal(ComponentSource.Unattributed, SourceOf(game, "libxess.dll"));
        }

        [Fact]
        public void AFileTheAppOverwroteIsStillCreditedToTheApp()
        {
            // The bytes on disk are ours even though the game had its own copy first,
            // so InstalledFiles — not FilesCreated — is the right question to ask.
            Place("libxess.dll");
            WriteManifest(_gameDir, "libxess.dll");

            Assert.Equal(ComponentSource.Manager, SourceOf(Analyze(), "libxess.dll"));
        }

        [Fact]
        public void AnAddOnInstalledAfterTheMainInstallIsRecordedAndCredited()
        {
            // Add-ons (the FSR 4 INT8 build, for one) run after the main install has
            // already committed its manifest, so they have to reopen it and save again.
            // The INT8 path used to skip that entirely with a bare File.Copy, leaving its
            // DLL unattributed and revert relying on the known-artifact fallback.
            WriteManifest(_gameDir);

            var source = Path.Combine(_root, "amdxcffx64.dll");
            File.WriteAllBytes(source, PeTestData.BuildPe(PeTestData.MachineAmd64, "4.1.1.0"));

            var game = new Game { Name = "Test", InstallPath = _gameDir };
            new GameInstallationService().InstallTrackedFile(
                game, source, "amdxcffx64.dll", _gameDir,
                manifest => { manifest.IncludesExtras = true; manifest.ExtrasVersion = "1.1.0"; });

            var saved = new BackupStoreService().LoadManifest(_gameDir)!;
            Assert.Contains("amdxcffx64.dll", saved.InstalledFiles);
            Assert.True(saved.IncludesExtras);
            Assert.Equal("1.1.0", saved.ExtrasVersion);

            // And the details page now credits it.
            Assert.Equal(ComponentSource.Manager, SourceOf(Analyze(), "amdxcffx64.dll"));
        }

        [Fact]
        public void WithNoInstallNothingIsCreditedToTheApp()
        {
            Place("nvngx_dlss.dll");
            Place("libxess.dll");

            Assert.All(Analyze().DetectedComponents,
                c => Assert.Equal(ComponentSource.Unattributed, c.Source));
        }
    }
}
