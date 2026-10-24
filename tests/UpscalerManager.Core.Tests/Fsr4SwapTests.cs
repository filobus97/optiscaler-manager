// Upscaler Manager - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System;
using System.IO;
using System.Linq;
using System.Text;
using UpscalerManager.Core.Components;
using UpscalerManager.Core.Models;
using UpscalerManager.Core.Services;
using Xunit;

namespace UpscalerManager.Core.Tests
{
    /// <summary>
    /// FSR 4 without OptiScaler writes a set of files into a player's game and keeps the
    /// only copy of what was there, so these tests are about what it plans, what it
    /// refuses, and getting every byte back.
    /// </summary>
    [Collection(AppDataCollection.Name)]
    public class Fsr4SwapTests : IDisposable
    {
        private static readonly string[] Ffx =
            { "ffxConfigure", "ffxCreateContext", "ffxDestroyContext", "ffxDispatch", "ffxQuery" };

        private readonly ScopedAppData _appData = new();
        private readonly string _root;
        private readonly string _gameDir;
        private readonly string _exeDir;
        private readonly string _sources;
        private readonly Game _game;

        public Fsr4SwapTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "fsr4test-" + Guid.NewGuid().ToString("N")[..8]);
            _gameDir = Path.Combine(_root, "game");
            _exeDir = Path.Combine(_gameDir, "bin", "x64");
            _sources = Path.Combine(_root, "sources");
            Directory.CreateDirectory(_exeDir);
            Directory.CreateDirectory(_sources);
            _game = new Game { Name = "Test Game", InstallPath = _gameDir };
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
            _appData.Dispose();
        }

        // ── Fixtures ─────────────────────────────────────────────────────────

        private static byte[] Module(string version, string? original = null, string? description = null) =>
            PeTestData.BuildVersionedPeExporting(version, Ffx, original, description);

        private static string Put(string dir, string name, byte[] bytes)
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        /// <summary>An SDK 1 runtime: a version resource and nothing else, as the harness's sample game has.</summary>
        private string Monolith(string name = "amd_fidelityfx_dx12.dll", string version = "1.0.1.41314") =>
            Put(_exeDir, name, PeTestData.BuildPe(PeTestData.MachineAmd64, version));

        private Fsr4Sources Sources(bool loader = true, bool frameGeneration = true)
        {
            var upscaler = Put(_sources, "amd_fidelityfx_upscaler_dx12.dll", Module("4.1.1.2740"));
            var loaderFile = loader
                ? FidelityFxFiles.Identify(Put(_sources, "amd_fidelityfx_loader_dx12.dll", Module("2.3.0.2740")))
                : null;
            var frames = frameGeneration
                ? FidelityFxFiles.Identify(Put(_sources, "amd_fidelityfx_framegeneration_dx12.dll", Module("4.0.1.2740")))
                : null;
            return new Fsr4Sources(upscaler, "Test build", "4.1.1.2740", loaderFile, frames);
        }

        private static string Version(string path) => PeFileInspector.Inspect(path).FileVersion ?? "none";

        // ── Finding the game's files, whatever they are called ───────────────

        [Fact]
        public void TheVersionResourceNamesWhatTheDllWasBuiltAs()
        {
            var path = Put(_exeDir, "renamed.dll",
                Module("4.1.1.2740", "amd_fidelityfx_upscaler_dx12.dll", "AMD FidelityFX Upscaler"));

            var strings = PeFileInspector.ReadVersionStrings(path);

            Assert.Equal("amd_fidelityfx_upscaler_dx12.dll", strings.OriginalFilename);
            Assert.Equal("AMD FidelityFX Upscaler", strings.FileDescription);
        }

        [Fact]
        public void AFileWithoutThoseStringsReadsAsNone()
        {
            Assert.Equal(PeVersionStrings.None,
                PeFileInspector.ReadVersionStrings(Put(_exeDir, "a.dll", PeTestData.BuildPe(fileVersion: "1.0.0.0"))));
            Assert.Equal(PeVersionStrings.None,
                PeFileInspector.ReadVersionStrings(Put(_exeDir, "b.dll", Encoding.ASCII.GetBytes("not a dll at all"))));
            Assert.Equal(PeVersionStrings.None,
                PeFileInspector.ReadVersionStrings(Path.Combine(_exeDir, "missing.dll")));
        }

        [Fact]
        public void ARenamedUpscalerIsFoundByWhatItWasBuiltAs()
        {
            var path = Put(_exeDir, "fsr_upscale.dll", Module("4.0.2.1", "amd_fidelityfx_upscaler_dx12.dll"));

            var file = FidelityFxFiles.Identify(path)!;

            Assert.Equal(FidelityFxRole.Upscaler, file.Role);
            Assert.Equal("fsr_upscale.dll", file.FileName);
        }

        [Fact]
        public void ARenamedSdk1RuntimeIsFoundToo()
        {
            var path = Put(_exeDir, "gfx_amd.dll", Module("1.0.1.41314", "amd_fidelityfx_dx12.dll"));

            Assert.Equal(FidelityFxRole.Monolith, FidelityFxFiles.Identify(path)!.Role);
        }

        [Fact]
        public void ARenamedLoaderIsToldFromAnSdk1RuntimeByItsVersion()
        {
            // The loader is compatible with the old name, so the legacy name settles it by
            // what is inside: an SDK version of 2 or more and no provider table.
            var path = Put(_exeDir, "gfx_amd.dll", Module("2.3.0.2740", "amd_fidelityfx_dx12.dll"));

            Assert.Equal(FidelityFxRole.Loader, FidelityFxFiles.Identify(path)!.Role);
        }

        [Fact]
        public void ADescriptionAloneNamesTheModuleWhenTheDllExportsTheApi()
        {
            var path = Put(_exeDir, "x.dll", Module("4.1.1.2740", null, "AMD FidelityFX Frame Generation"));

            Assert.Equal(FidelityFxRole.FrameGeneration, FidelityFxFiles.Identify(path)!.Role);
        }

        [Fact]
        public void ADescriptionWithoutTheApiIsNotEnough()
        {
            var path = Put(_exeDir, "x.dll",
                PeTestData.BuildVersionedPeExporting("1.0.0.0", new[] { "DllMain" }, null, "AMD FidelityFX Helper"));

            Assert.Null(FidelityFxFiles.Identify(path));
        }

        [Fact]
        public void ADllThatIsNoPartOfFidelityFxIsLeftAlone()
        {
            Assert.Null(FidelityFxFiles.Identify(Put(_exeDir, "nvngx_dlss.dll", PeTestData.BuildPe(fileVersion: "310.1.0.0"))));
            Assert.Null(FidelityFxFiles.Identify(Put(_exeDir, "other.dll", Module("1.0.0.0", "other.dll", "Something else"))));
        }

        [Fact]
        public void TheVulkanBuildIsNotCovered()
        {
            Assert.Null(FidelityFxFiles.Identify(Put(_exeDir, "amd_fidelityfx_vk.dll", PeTestData.BuildPe(fileVersion: "1.0.1.1"))));
        }

        [Fact]
        public void ScanFindsEveryCopyUnderTheGame()
        {
            Monolith();
            Put(Path.Combine(_gameDir, "Engine", "ThirdParty"), "amd_fidelityfx_dx12.dll",
                PeTestData.BuildPe(fileVersion: "1.0.1.41314"));
            Put(_exeDir, "renamed.dll", Module("4.1.1.2740", "amd_fidelityfx_upscaler_dx12.dll"));
            Put(_exeDir, "OptiScaler.dll", PeTestData.BuildPe(fileVersion: "0.9.5.0"));

            var found = FidelityFxFiles.Scan(_gameDir);

            Assert.Equal(3, found.Count);
            Assert.Equal(2, found.Count(f => f.Role == FidelityFxRole.Monolith));
        }

        // ── What gets planned ────────────────────────────────────────────────

        [Fact]
        public void AGameWithNoFidelityFxIsNotOffered()
        {
            Put(_exeDir, "nvngx_dlss.dll", PeTestData.BuildPe(fileVersion: "310.1.0.0"));

            var plan = new Fsr4SwapService().Plan(_game, Sources());

            Assert.Empty(Fsr4SwapPlanner.Targets(FidelityFxFiles.Scan(_gameDir)));
            Assert.NotNull(plan.Refusal);
            Assert.Empty(plan.Steps);
        }

        [Fact]
        public void ALoaderAloneIsNotWhereFsr4GoesIn()
        {
            // A loader with no upscaler module means the game does not ask for upscaling here.
            Put(_exeDir, "amd_fidelityfx_loader_dx12.dll", Module("2.3.0.2740"));

            Assert.NotNull(new Fsr4SwapService().Plan(_game, Sources()).Refusal);
        }

        [Fact]
        public void ASplitSdk2GameGetsTheUpscalerOverItsOwnAndNothingElseChanges()
        {
            Put(_exeDir, "amd_fidelityfx_loader_dx12.dll", Module("2.1.0.604"));
            Put(_exeDir, "amd_fidelityfx_upscaler_dx12.dll", Module("4.0.3.604"));
            Put(_exeDir, "amd_fidelityfx_framegeneration_dx12.dll", Module("3.1.6.604"));

            var plan = new Fsr4SwapService().Plan(_game, Sources());

            var step = Assert.Single(plan.Steps);
            Assert.Equal(Fsr4StepKind.ReplaceUpscaler, step.Kind);
            Assert.Equal("amd_fidelityfx_upscaler_dx12.dll", step.FileName);
            Assert.Equal(_exeDir, step.Directory);
            Assert.Empty(plan.Warnings);
            Assert.False(plan.LosesFrameGeneration);
        }

        [Fact]
        public void ARenamedUpscalerIsReplacedUnderTheNameTheGameUses()
        {
            Put(_exeDir, "fsr_upscale.dll", Module("4.0.3.604", "amd_fidelityfx_upscaler_dx12.dll"));

            var step = Assert.Single(new Fsr4SwapService().Plan(_game, Sources()).Steps);

            Assert.Equal(Fsr4StepKind.ReplaceUpscaler, step.Kind);
            Assert.Equal("fsr_upscale.dll", step.FileName);
        }

        [Fact]
        public void AnSdk1GameGetsTheLoaderUnderTheOldNameWithTheModulesBesideIt()
        {
            Monolith();

            var plan = new Fsr4SwapService().Plan(_game, Sources());

            Assert.Equal(
                new[]
                {
                    (Fsr4StepKind.AddUpscaler, "amd_fidelityfx_upscaler_dx12.dll"),
                    (Fsr4StepKind.AddFrameGeneration, "amd_fidelityfx_framegeneration_dx12.dll"),
                    (Fsr4StepKind.ReplaceRuntimeWithLoader, "amd_fidelityfx_dx12.dll"),
                },
                plan.Steps.Select(s => (s.Kind, s.FileName)));
            Assert.All(plan.Steps, s => Assert.Equal(_exeDir, s.Directory));
            Assert.Empty(plan.Warnings);
            Assert.False(plan.LosesFrameGeneration);
        }

        [Fact]
        public void AnSdk1GameWithALoaderButNoFrameGenerationModuleIsToldSo()
        {
            Monolith();

            var plan = new Fsr4SwapService().Plan(_game, Sources(frameGeneration: false));

            Assert.Equal(2, plan.Steps.Count);
            Assert.True(plan.LosesFrameGeneration);
            Assert.Contains("frame generation", Assert.Single(plan.Warnings));
        }

        [Fact]
        public void WithoutALoaderTheUpscalerStandsInUnderTheOldNameAndTheLossIsSaid()
        {
            Monolith();

            var plan = new Fsr4SwapService().Plan(_game, Sources(loader: false, frameGeneration: false));

            var step = Assert.Single(plan.Steps);
            Assert.Equal(Fsr4StepKind.ReplaceRuntimeWithUpscaler, step.Kind);
            Assert.Equal("amd_fidelityfx_dx12.dll", step.FileName);
            Assert.True(plan.LosesFrameGeneration);
            Assert.Contains("frame generation will not", Assert.Single(plan.Warnings));
        }

        [Fact]
        public void ARenamedSdk1RuntimeKeepsItsNameWhateverRoutePlaced()
        {
            Put(_exeDir, "gfx_amd.dll", Module("1.0.1.41314", "amd_fidelityfx_dx12.dll"));

            var plan = new Fsr4SwapService().Plan(_game, Sources());

            Assert.Contains(plan.Steps, s => s is { Kind: Fsr4StepKind.ReplaceRuntimeWithLoader, FileName: "gfx_amd.dll" });
            Assert.Contains(plan.Steps, s => s is { Kind: Fsr4StepKind.AddUpscaler, FileName: "amd_fidelityfx_upscaler_dx12.dll" });
        }

        [Fact]
        public void EveryCopyOfTheRuntimeGetsItsOwnSet()
        {
            Monolith();
            var other = Path.Combine(_gameDir, "Engine", "ThirdParty");
            Put(other, "amd_fidelityfx_dx12.dll", PeTestData.BuildPe(fileVersion: "1.0.1.41314"));

            var plan = new Fsr4SwapService().Plan(_game, Sources());

            Assert.Equal(6, plan.Steps.Count);
            Assert.Equal(2, plan.Steps.Select(s => s.Directory).Distinct().Count());
        }

        [Fact]
        public void ThePreviewNamesEachFileAndWhatItReplaces()
        {
            Monolith();

            var lines = new Fsr4SwapService().Plan(_game, Sources()).Steps
                .Select(s => Fsr4Plan.Describe(s, _gameDir)).ToList();

            Assert.Equal("bin/x64/amd_fidelityfx_upscaler_dx12.dll  ←  FSR 4 upscaler 4.1.1  (new file)", lines[0]);
            Assert.Equal("bin/x64/amd_fidelityfx_dx12.dll  ←  SDK 2 loader 2.3.0  (replaces 1.0.1.41314)", lines[2]);
        }

        // ── Where the files come from ────────────────────────────────────────

        [Fact]
        public void AnUpscalerUnderTheNameAmdUsesIsAccepted()
        {
            var (upscaler, reason) = Fsr4SwapService.ReadUpscaler(
                Put(_sources, "amd_fidelityfx_upscaler_dx12.dll", Module("4.1.1.2740")));

            Assert.Null(reason);
            Assert.Equal("4.1.1.2740", upscaler!.Version);
        }

        [Fact]
        public void ADriverStyleBuildIsNotUsableWithoutAHost()
        {
            var (upscaler, reason) = Fsr4SwapService.ReadUpscaler(
                Put(_sources, "amdxcffx64.dll", PeTestData.BuildPeExporting("UpdateFfxApiProvider")));

            Assert.Null(upscaler);
            Assert.Contains("driver-style", reason);
        }

        [Fact]
        public void AFileUnderAnotherNameNeedsAnFsrVersionTableToBeCalledAnUpscaler()
        {
            // The loader exports the same five functions, so the exports prove nothing.
            var bare = Put(_sources, "my-build.dll", PeTestData.BuildPeExporting(Ffx));
            Assert.Contains("version table", Fsr4SwapService.ReadUpscaler(bare).Reason);

            var table = PeTestData.BuildPeExporting(Ffx).Concat(Encoding.ASCII.GetBytes("4.1.1\03.1.5\0")).ToArray();
            Assert.NotNull(Fsr4SwapService.ReadUpscaler(Put(_sources, "my-fsr4.dll", table)).Upscaler);
        }

        [Fact]
        public void TheLoaderAndFrameGenerationAreFoundInAReleaseEvenUnderTheOldName()
        {
            // OptiScaler ships the SDK 2 loader as amd_fidelityfx_dx12.dll.
            var release = new[]
            {
                Put(_sources, "amd_fidelityfx_dx12.dll", Module("2.3.0.2740")),
                Put(_sources, "amd_fidelityfx_upscaler_dx12.dll", Module("4.1.1.2740")),
                Put(_sources, "amd_fidelityfx_framegeneration_dx12.dll", Module("4.0.1.2740")),
            };

            var (loader, frames) = Fsr4SwapService.FindCompanions(release);

            Assert.Equal("amd_fidelityfx_dx12.dll", loader!.FileName);
            Assert.Equal("amd_fidelityfx_framegeneration_dx12.dll", frames!.FileName);
        }

        [Fact]
        public void AnSdk1RuntimeIsNotALoader()
        {
            var (loader, frames) = Fsr4SwapService.FindCompanions(new[]
            {
                Put(_sources, "amd_fidelityfx_dx12.dll", PeTestData.BuildPe(fileVersion: "1.0.1.41314")),
                Put(_sources, "amd_fidelityfx_framegeneration_dx12.dll", Module("4.0.1.2740")),
            });

            Assert.Null(loader);
            Assert.Null(frames);   // a module of nobody's set is not paired with a loader
        }

        // ── Doing it, and undoing it ─────────────────────────────────────────

        private (Fsr4SwapService Service, Fsr4Plan Plan, Fsr4Sources Sources) Prepare(
            bool loader = true, bool frameGeneration = true)
        {
            var service = new Fsr4SwapService();
            var sources = Sources(loader, frameGeneration);
            return (service, service.Plan(_game, sources), sources);
        }

        [Fact]
        public void ASetIsWrittenAndEveryByteComesBack()
        {
            var runtime = Monolith();
            var original = File.ReadAllBytes(runtime);
            var (service, plan, sources) = Prepare();

            var manifest = service.Apply(_game, plan, sources);

            Assert.Equal("2.3.0.2740", Version(runtime));
            Assert.Equal("4.1.1.2740", Version(Path.Combine(_exeDir, "amd_fidelityfx_upscaler_dx12.dll")));
            Assert.Equal("4.0.1.2740", Version(Path.Combine(_exeDir, "amd_fidelityfx_framegeneration_dx12.dll")));
            Assert.Equal(3, manifest.Files.Count);
            Assert.True(service.IsActive(_game));
            Assert.Equal(DllSwapService.SwapStanding.Intact, Fsr4SwapService.StandingOf(service.Current(_game)));

            // The original is in the external store, and the two new files are recorded as new.
            var store = new BackupStoreService();
            var replaced = Assert.Single(manifest.Files, f => f.ExistedBefore);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(store.GetFilesDir(_gameDir), replaced.BackupRelative)));
            Assert.Equal(2, manifest.Files.Count(f => !f.ExistedBefore));

            service.Revert(_game);

            Assert.Equal(original, File.ReadAllBytes(runtime));
            Assert.False(File.Exists(Path.Combine(_exeDir, "amd_fidelityfx_upscaler_dx12.dll")));
            Assert.False(File.Exists(Path.Combine(_exeDir, "amd_fidelityfx_framegeneration_dx12.dll")));
            Assert.False(service.IsActive(_game));
            Assert.False(File.Exists(Path.Combine(store.GetFilesDir(_gameDir), replaced.BackupRelative)));
        }

        [Fact]
        public void ASplitGamesUpscalerComesBackToo()
        {
            var upscaler = Put(_exeDir, "amd_fidelityfx_upscaler_dx12.dll", Module("4.0.3.604"));
            var loader = Put(_exeDir, "amd_fidelityfx_loader_dx12.dll", Module("2.1.0.604"));
            var upscalerBefore = File.ReadAllBytes(upscaler);
            var loaderBefore = File.ReadAllBytes(loader);
            var (service, plan, sources) = Prepare();

            service.Apply(_game, plan, sources);

            Assert.Equal("4.1.1.2740", Version(upscaler));
            Assert.Equal(loaderBefore, File.ReadAllBytes(loader));   // not part of the set

            service.Revert(_game);
            Assert.Equal(upscalerBefore, File.ReadAllBytes(upscaler));
        }

        [Fact]
        public void TheFallbackWritesTheUpscalerOverTheOldFileAndRevertsIt()
        {
            var runtime = Monolith();
            var original = File.ReadAllBytes(runtime);
            var (service, plan, sources) = Prepare(loader: false, frameGeneration: false);

            var manifest = service.Apply(_game, plan, sources);

            Assert.Equal("4.1.1.2740", Version(runtime));
            Assert.True(manifest.LosesFrameGeneration);
            Assert.False(File.Exists(Path.Combine(_exeDir, "amd_fidelityfx_upscaler_dx12.dll")));

            service.Revert(_game);
            Assert.Equal(original, File.ReadAllBytes(runtime));
        }

        [Fact]
        public void EveryCopyIsBackedUpSeparately()
        {
            var first = Monolith();
            var second = Put(Path.Combine(_gameDir, "Engine", "ThirdParty"), "amd_fidelityfx_dx12.dll",
                PeTestData.BuildPe(fileVersion: "1.0.2.38022"));
            var (service, plan, sources) = Prepare();

            service.Apply(_game, plan, sources);
            Assert.Equal("2.3.0.2740", Version(first));
            Assert.Equal("2.3.0.2740", Version(second));

            service.Revert(_game);
            Assert.Equal("1.0.1.41314", Version(first));
            Assert.Equal("1.0.2.38022", Version(second));   // not the first copy's original
        }

        [Fact]
        public void ItIsRefusedWhileOptiScalerIsInstalled()
        {
            var runtime = Monolith();
            var before = File.ReadAllBytes(runtime);
            var (service, plan, sources) = Prepare();
            _game.IsOptiscalerInstalled = true;

            var error = Assert.Throws<InvalidOperationException>(() => service.Apply(_game, plan, sources));

            Assert.Contains("OptiScaler", error.Message);
            Assert.Equal(before, File.ReadAllBytes(runtime));
            Assert.False(service.IsActive(_game));
        }

        [Fact]
        public void ItIsRefusedOverATrackedOptiScalerInstall()
        {
            Monolith();
            var (service, plan, sources) = Prepare();
            new BackupStoreService().SaveManifest(_gameDir, new InstallationManifest { OperationStatus = "committed" });

            Assert.Contains("OptiScaler", Assert.Throws<InvalidOperationException>(
                () => service.Apply(_game, plan, sources)).Message);
        }

        [Fact]
        public void ItIsRefusedWhileAFidelityFxFileIsSwappedTheOtherWay()
        {
            // The single-file swap's original would otherwise be taken for the game's own.
            var runtime = Monolith();
            var (service, plan, sources) = Prepare();
            var store = new BackupStoreService();
            Directory.CreateDirectory(store.GetBackupRoot(_gameDir));
            File.WriteAllText(
                Path.Combine(store.GetBackupRoot(_gameDir), "swaps.json"),
                System.Text.Json.JsonSerializer.Serialize(
                    new SwapManifest { Files = { new SwappedFile { FileName = "amd_fidelityfx_dx12.dll" } } },
                    OptimizerContext.Default.SwapManifest));

            var before = File.ReadAllBytes(runtime);
            Assert.Contains("swapped", Assert.Throws<InvalidOperationException>(
                () => service.Apply(_game, plan, sources)).Message);
            Assert.Equal(before, File.ReadAllBytes(runtime));
        }

        [Fact]
        public void ItIsRefusedWhileAnotherSetIsInPlace()
        {
            Monolith();
            var (service, plan, sources) = Prepare();
            service.Apply(_game, plan, sources);

            Assert.Contains("already", Assert.Throws<InvalidOperationException>(
                () => service.Apply(_game, plan, sources)).Message);
        }

        [Fact]
        public void ItIsRefusedWhileTheGameIsRunning()
        {
            // The test host's folder stands in for a game folder with a process in it.
            if (!Directory.Exists("/proc")) return;
            var host = Path.GetDirectoryName(Environment.ProcessPath);
            if (host is null) return;

            var running = new Game { Name = "Busy Game", InstallPath = host };
            var runtime = Put(host, "amd_fidelityfx_dx12.dll", PeTestData.BuildPe(fileVersion: "1.0.1.41314"));
            try
            {
                var service = new Fsr4SwapService();
                var sources = Sources();
                var plan = service.Plan(running, sources, new[] { FidelityFxFiles.Identify(runtime)! });

                var error = Assert.Throws<InvalidOperationException>(() => service.Apply(running, plan, sources));

                Assert.Contains("running", error.Message);
                Assert.Equal("1.0.1.41314", Version(runtime));
            }
            finally
            {
                try { File.Delete(runtime); } catch { }
            }
        }

        [Fact]
        public void APlanForNothingIsRefused()
        {
            var (service, _, sources) = Prepare();

            Assert.Throws<InvalidOperationException>(() => service.Apply(_game, Fsr4Plan.Refused("nothing"), sources));
        }

        [Fact]
        public void AFileThatCannotBeWrittenPutsBackWhatWasWrittenBefore()
        {
            // The frame generation module's name is taken by a folder, so the copy fails
            // after the upscaler has already gone in: nothing may be left half done.
            var runtime = Monolith();
            var original = File.ReadAllBytes(runtime);
            Directory.CreateDirectory(Path.Combine(_exeDir, "amd_fidelityfx_framegeneration_dx12.dll"));
            var (service, plan, sources) = Prepare();

            var error = Assert.ThrowsAny<IOException>(() => service.Apply(_game, plan, sources));

            Assert.Contains("put back", error.Message);
            Assert.Equal(original, File.ReadAllBytes(runtime));
            Assert.False(File.Exists(Path.Combine(_exeDir, "amd_fidelityfx_upscaler_dx12.dll")));
            Assert.False(service.IsActive(_game));
            Assert.False(new DllSwapService().HasSwaps(_game));
        }

        [Fact]
        public void APatchedFileStopsAnOrdinaryUndoAndIsLeftAloneByTheOtherOne()
        {
            var runtime = Monolith();
            var original = File.ReadAllBytes(runtime);
            var (service, plan, sources) = Prepare();
            service.Apply(_game, plan, sources);

            // The game patches its loader.
            File.WriteAllBytes(runtime, PeTestData.BuildPe(fileVersion: "9.9.9.9"));
            var patched = File.ReadAllBytes(runtime);

            Assert.Equal(DllSwapService.SwapStanding.PartlyReplaced, Fsr4SwapService.StandingOf(service.Current(_game)));
            Assert.Contains("no longer", Assert.Throws<InvalidOperationException>(() => service.Revert(_game)).Message);
            Assert.True(File.Exists(Path.Combine(_exeDir, "amd_fidelityfx_upscaler_dx12.dll")));   // nothing was touched

            var left = service.RevertWhatRemains(_game);

            Assert.Equal(new[] { runtime }, left);
            Assert.Equal(patched, File.ReadAllBytes(runtime));
            Assert.False(File.Exists(Path.Combine(_exeDir, "amd_fidelityfx_upscaler_dx12.dll")));
            Assert.False(service.IsActive(_game));
            Assert.NotEqual(original, patched);
        }

        [Fact]
        public void ForceRestoresTheOriginalEvenOverAPatch()
        {
            var runtime = Monolith();
            var original = File.ReadAllBytes(runtime);
            var (service, plan, sources) = Prepare();
            service.Apply(_game, plan, sources);
            File.WriteAllBytes(runtime, PeTestData.BuildPe(fileVersion: "9.9.9.9"));

            service.Revert(_game, force: true);

            Assert.Equal(original, File.ReadAllBytes(runtime));
        }

        [Fact]
        public void AMissingOriginalBackupKeepsTheRecordSoUndoCanBeTriedAgain()
        {
            var runtime = Monolith();
            var (service, plan, sources) = Prepare();
            var manifest = service.Apply(_game, plan, sources);
            File.Delete(Path.Combine(new BackupStoreService().GetFilesDir(_gameDir),
                manifest.Files.Single(f => f.ExistedBefore).BackupRelative));

            Assert.Throws<IOException>(() => service.Revert(_game));

            Assert.True(service.IsActive(_game));
            Assert.True(File.Exists(runtime));
        }

        [Fact]
        public void TheRecordIsForgottenWhenTheGameNoLongerHasTheFiles()
        {
            Monolith();
            var (service, plan, sources) = Prepare();
            service.Apply(_game, plan, sources);

            Assert.False(service.ForgetStale(_game));

            Directory.Delete(_exeDir, recursive: true);
            Assert.True(service.ForgetStale(_game));
            Assert.False(service.IsActive(_game));
        }

        // ── How the rest of the app sees it ──────────────────────────────────

        [Fact]
        public void TheSingleFileSwapListLeavesTheSetAlone()
        {
            var runtime = Monolith();
            var (service, plan, sources) = Prepare();
            service.Apply(_game, plan, sources);
            _game.DetectedComponents = new()
            {
                new DetectedComponent
                {
                    FileName = "amd_fidelityfx_dx12.dll", Technology = "FidelityFX runtime (DX12)",
                    Role = TechRole.Runtime, Vendor = "AMD", Version = "2.3.0.2740",
                    RelativePath = Path.GetRelativePath(_gameDir, runtime),
                },
            };

            var slot = Assert.Single(new DllSwapService().Slots(_game));

            Assert.False(slot.Verdict.Allowed);
            Assert.Contains("FSR 4", slot.Verdict.Reason);
        }

        [Fact]
        public void TheStoragePageKnowsTheBackupsAreNeeded()
        {
            Monolith();
            var swaps = new DllSwapService();
            Assert.False(swaps.HasSwapsForDirectory(_gameDir));

            var (service, plan, sources) = Prepare();
            service.Apply(_game, plan, sources);

            Assert.True(swaps.HasSwapsForDirectory(_gameDir));
            Assert.True(swaps.HasSwaps(_game));

            service.Revert(_game);
            Assert.False(swaps.HasSwapsForDirectory(_gameDir));
        }

        [Fact]
        public void ACorruptRecordIsNotTakenForNothingSetUp()
        {
            Monolith();
            var (service, plan, sources) = Prepare();
            var store = new BackupStoreService();
            Directory.CreateDirectory(store.GetBackupRoot(_gameDir));
            File.WriteAllText(Path.Combine(store.GetBackupRoot(_gameDir), Fsr4SwapService.ManifestFileName), "{ not json");

            Assert.Throws<InvalidOperationException>(() => service.Apply(_game, plan, sources));
            Assert.True(Fsr4SwapService.HasRecordForDirectory(_gameDir));
        }
    }
}
