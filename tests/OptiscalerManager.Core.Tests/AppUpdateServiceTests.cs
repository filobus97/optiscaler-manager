// OptiScaler Manager - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using OptiscalerManager.Core.Services;
using Xunit;

namespace OptiscalerManager.Core.Tests
{
    /// <summary>Pins the pure version-comparison logic of the app self-update check.</summary>
    public class AppUpdateServiceTests
    {
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
    }
}
