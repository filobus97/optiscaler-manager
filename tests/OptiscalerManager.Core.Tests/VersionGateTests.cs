// OptiScaler Client - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using OptiscalerManager.Core.Services;
using Xunit;

namespace OptiscalerManager.Core.Tests
{
    /// <summary>
    /// The custom amdxcffx64.dll needs OptiScaler to load it from the game folder,
    /// which first shipped in v0.7.7-pre9 / stable v0.7.8. These pin that gate.
    /// </summary>
    public class VersionGateTests
    {
        [Theory]
        [InlineData("0.7.8")]
        [InlineData("0.8.0")]
        [InlineData("1.0.0")]
        [InlineData("v0.7.8")]
        public void NewEnoughStable_IsSupported(string version)
            => Assert.True(GameInstallationService.SupportsCustomFsr4Dll(version));

        [Theory]
        [InlineData("0.7.7")]
        [InlineData("0.7.6")]
        [InlineData("0.6.0")]
        public void OlderStable_IsNotSupported(string version)
            => Assert.False(GameInstallationService.SupportsCustomFsr4Dll(version));

        [Theory]
        [InlineData("0.7.7-pre9", true)]
        [InlineData("0.7.7-pre12", true)]
        [InlineData("0.7.7-pre8", false)]
        [InlineData("0.7.7-pre1", false)]
        public void PreReleases_GateAtPre9(string version, bool expected)
            => Assert.Equal(expected, GameInstallationService.SupportsCustomFsr4Dll(version));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("custom-my-build")]
        [InlineData("nightly-abc123")]
        public void UnknownOrCustom_IsNotBlocked(string? version)
            => Assert.True(GameInstallationService.SupportsCustomFsr4Dll(version));
    }
}
