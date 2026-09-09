// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System.Collections.Generic;
using UpscalerManager.Core.Models;
using Xunit;

namespace UpscalerManager.Core.Tests;

public class VersionOrderTests
{
    /// <summary>
    /// The real tag list from the OptiScaler repo, shuffled. Ordering these correctly is
    /// the whole job, so the expectation is spelled out rather than computed.
    /// </summary>
    [Fact]
    public void OrdersRealOptiScalerTagsNewestFirst()
    {
        var shuffled = new[]
        {
            "0.6.7-pre14", "0.9.3", "0.7.0-pre9", "0.6.7", "0.9.0",
            "0.7.0-pre66", "0.7.9", "0.7.7-pre9", "0.7-old_nightly", "0.7.0-pre23",
        };

        Assert.Equal(new[]
        {
            "0.9.3",
            "0.9.0",
            "0.7.9",
            "0.7.7-pre9",
            "0.7.0-pre66",
            "0.7.0-pre23",
            "0.7.0-pre9",
            "0.7-old_nightly",
            "0.6.7",
            "0.6.7-pre14",
        }, VersionOrder.Newest(shuffled));
    }

    [Fact]
    public void PreReleaseNumbersAreComparedAsNumbersNotText()
    {
        // The bug that motivated this: as text, "pre9" sorts above "pre66".
        Assert.Equal(new[] { "0.7.0-pre66", "0.7.0-pre14", "0.7.0-pre9" },
            VersionOrder.Newest(new[] { "0.7.0-pre9", "0.7.0-pre66", "0.7.0-pre14" }));
    }

    [Fact]
    public void DoubleDigitVersionPartsBeatSingleDigitOnes()
    {
        // As text, "1.9" sorts above "1.10" — which picked the wrong offline fallback.
        Assert.Equal(new[] { "1.10.0", "1.9.0", "1.2.0" },
            VersionOrder.Newest(new[] { "1.9.0", "1.2.0", "1.10.0" }));
    }

    [Fact]
    public void FinalReleaseOutranksItsOwnPreReleases()
    {
        Assert.Equal(new[] { "0.6.7", "0.6.7-pre14", "0.6.7-pre7" },
            VersionOrder.Newest(new[] { "0.6.7-pre7", "0.6.7", "0.6.7-pre14" }));
    }

    [Theory]
    [InlineData("v0.9.3", "0.9.3")]      // the leading v, as GitHub tags it
    [InlineData("0.7.0-pre66", "0.7.0")]
    [InlineData("0.7-old_nightly", "0.7")]
    [InlineData("3", "3.0")]             // single component still parses
    [InlineData("0.7_custom", "0.7")]
    public void ParsesTheNumericPart(string tag, string expected)
        => Assert.Equal(System.Version.Parse(expected), VersionOrder.BaseVersion(tag));

    [Theory]
    [InlineData("latest")]
    [InlineData("")]
    [InlineData(null)]
    public void UnparseableNamesDoNotThrowAndSortLast(string? name)
    {
        Assert.Equal(new System.Version(0, 0), VersionOrder.BaseVersion(name));
        Assert.Equal(new[] { "0.1.0", name ?? "" },
            VersionOrder.Newest(new[] { name ?? "", "0.1.0" }));
    }

    [Fact]
    public void DuplicatesAreDropped()
        => Assert.Equal(new[] { "1.0.0" }, VersionOrder.Newest(new List<string> { "1.0.0", "1.0.0" }));
}
