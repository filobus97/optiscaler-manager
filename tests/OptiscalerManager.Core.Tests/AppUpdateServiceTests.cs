// OptiScaler Manager - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System;
using System.IO;
using OptiscalerManager.Core.Services;
using Xunit;

namespace OptiscalerManager.Core.Tests
{
    /// <summary>Pins the pure version-comparison logic of the app self-update check.</summary>
    public class AppUpdateServiceTests
    {
        // ── Which executable did the payload bring? ─────────────────────────────
        //
        // This decides what gets restarted after an update. Guess wrong when the app is
        // renamed and the updater restarts the version it just replaced, leaving the
        // user on the old build with the update banner reappearing every launch.

        private static string PayloadDir(params string[] fileNames)
        {
            var dir = Path.Combine(Path.GetTempPath(), "osm_payload_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            foreach (var name in fileNames)
                File.WriteAllText(Path.Combine(dir, name), "x");
            return dir;
        }

        [Fact]
        public void APayloadKeepingTheSameNameIsRecognised()
        {
            var dir = PayloadDir("OptiscalerManager", "update.sh", "config.json", "VERSION");
            try
            {
                Assert.Equal("OptiscalerManager",
                    AppUpdateService.FindPayloadExecutableName(dir, "OptiscalerManager"));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ARenamedPayloadIsFoundByElimination()
        {
            var dir = PayloadDir("UpscalerManager", "update.sh", "update.ps1", "config.json", "VERSION");
            try
            {
                Assert.Equal("UpscalerManager",
                    AppUpdateService.FindPayloadExecutableName(dir, "OptiscalerManager"));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void LooseNativeLibrariesAreNotMistakenForTheApp()
        {
            var dir = PayloadDir("UpscalerManager", "libSkiaSharp.so", "libHarfBuzzSharp.so",
                                 "update.sh", "config.json", "VERSION", "README.md");
            try
            {
                Assert.Equal("UpscalerManager",
                    AppUpdateService.FindPayloadExecutableName(dir, "OptiscalerManager"));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void TheWindowsExecutableIsFound()
        {
            var dir = PayloadDir("UpscalerManager.exe", "update.ps1", "config.json", "VERSION");
            try
            {
                Assert.Equal("UpscalerManager.exe",
                    AppUpdateService.FindPayloadExecutableName(dir, "OptiscalerManager.exe"));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void AnAmbiguousPayloadIsNotGuessedAt()
        {
            // Two plausible executables: better to keep the current name than to pick
            // one at random and restart something that is not the app.
            var dir = PayloadDir("UpscalerManager", "SomethingElse", "update.sh");
            try
            {
                Assert.Null(AppUpdateService.FindPayloadExecutableName(dir, "OptiscalerManager"));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Theory]
        [InlineData("v0.6.0", "0.6.0")]
        [InlineData("0.6.0", "0.6.0")]
        [InlineData("0.6.0+abc123", "0.6.0")]
        [InlineData("v0.6.0+build.5", "0.6.0")]
        [InlineData("  v1.2.3 ", "1.2.3")]
        public void NormalizeVersion_StripsPrefixAndBuildMetadata(string raw, string expected)
            => Assert.Equal(expected, AppUpdateService.NormalizeVersion(raw));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void NormalizeVersion_BlankIsNull(string? raw)
            => Assert.Null(AppUpdateService.NormalizeVersion(raw));

        [Theory]
        [InlineData("0.5.0", "0.6.0", true)]
        [InlineData("0.6.0", "0.6.0", false)]
        [InlineData("0.6.1", "0.6.0", false)]   // never suggest a downgrade
        [InlineData("0.6.0", "v0.7.0", true)]   // v-prefix on the remote tag
        [InlineData("0.9.9", "1.0.0", true)]
        [InlineData("0.6.0+abc", "0.6.0", false)] // build metadata ignored
        public void IsNewer_ComparesSemanticVersions(string current, string latest, bool expected)
            => Assert.Equal(expected, AppUpdateService.IsNewer(current, latest));

        [Theory]
        [InlineData("0.6.0", null)]
        [InlineData(null, "0.6.0")]
        public void IsNewer_MissingSide_IsFalse(string? current, string? latest)
            => Assert.False(AppUpdateService.IsNewer(current, latest));

        [Fact]
        public void IsNewer_UnparseableVersions_FallBackToInequality()
        {
            Assert.True(AppUpdateService.IsNewer("nightly-a", "nightly-b"));
            Assert.False(AppUpdateService.IsNewer("nightly-a", "nightly-a"));
        }

        [Theory]
        [InlineData("OptiscalerManager-0.9.0-linux-x64.zip", "linux-x64", true)]
        [InlineData("OptiscalerManager-0.9.0-win-x64.zip", "win-x64", true)]
        [InlineData("OptiscalerManager-0.9.0-osx-arm64.zip", "osx-arm64", true)]
        [InlineData("OptiscalerManager-0.9.0-linux-x64.zip", "win-x64", false)]  // wrong platform
        [InlineData("OptiscalerManager-0.9.0-osx-x64.zip", "osx-arm64", false)]  // arch mismatch
        public void SelectAssetUrl_MatchesRidBySuffix(string assetName, string rid, bool shouldMatch)
        {
            var url = AppUpdateService.SelectAssetUrl(
                new[] { (assetName, "https://example/dl") }, rid);
            Assert.Equal(shouldMatch ? "https://example/dl" : null, url);
        }

        [Fact]
        public void SelectAssetUrl_PicksTheMatchingRid_AmongMany()
        {
            var assets = new[]
            {
                ("OptiscalerManager-0.9.0-win-x64.zip", "u-win"),
                ("OptiscalerManager-0.9.0-linux-x64.zip", "u-linux"),
                ("OptiscalerManager-0.9.0-osx-arm64.zip", "u-mac"),
            };
            Assert.Equal("u-linux", AppUpdateService.SelectAssetUrl(assets, "linux-x64"));
        }

        [Fact]
        public void CurrentRid_HasOsAndArch()
        {
            var rid = AppUpdateService.CurrentRid();
            Assert.Contains("-", rid);
            Assert.Matches("^(win|linux|osx)-", rid);
        }

        [Fact]
        public void CanSelfUpdate_False_UnderTestHost_NotSingleFile()
        {
            // The test runner is a normal framework-dependent host (assembly has an
            // on-disk Location), so the single-file gate must report false — proving
            // the in-app updater won't offer itself outside a shipped bundle.
            Assert.False(AppUpdateService.IsSingleFilePublish);
            Assert.False(AppUpdateService.CanSelfUpdate);
        }

        [Fact]
        public void SelfUpdateCommand_Linux_RunsShellScriptWithWaitRelaunchForce()
        {
            var (file, args) = AppUpdateService.BuildSelfUpdateCommand("/opt/osm", 1234, isWindows: false);
            Assert.Equal("/bin/sh", file);
            Assert.Contains("update.sh", args);
            Assert.Contains("--wait-pid 1234", args);
            Assert.Contains("--relaunch", args);
            // --force: the app already decided to update; the script must not no-op
            // on a stale on-disk VERSION and loop.
            Assert.Contains("--force", args);
        }

        [Fact]
        public void SelfUpdateCommand_Windows_RunsPowerShellWithWaitRelaunchForce()
        {
            var (file, args) = AppUpdateService.BuildSelfUpdateCommand(@"C:\osm", 4321, isWindows: true);
            Assert.Equal("powershell", file);
            Assert.Contains("update.ps1", args);
            Assert.Contains("-WaitPid 4321", args);
            Assert.Contains("-Relaunch", args);
            Assert.Contains("-Force", args);
            Assert.Contains("-ExecutionPolicy Bypass", args);
        }
    }
}
