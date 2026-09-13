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
    // ── Community revision letters ──────────────────────────────────────────

    [Fact]
    public void ALetteredRevisionIsNewerThanTheBareVersion()
    {
        // The community FSR builds tag revisions with a bare trailing letter, so 4.0.2d
        // is the fourth build of 4.0.2 — not a pre-release of it. This used to invert:
        // the ordinal regex matched the version's own last digit for "4.0.2" and nothing
        // for "4.0.2d", so the newest build sorted below the oldest and "latest first"
        // offered the wrong one.
        Assert.Equal(
            new[] { "4.1.1b", "4.1.1", "4.0.2d", "4.0.2" },
            VersionOrder.Newest(new[] { "4.0.2", "4.1.1", "4.0.2d", "4.1.1b" }));
    }

    [Fact]
    public void RevisionLettersOrderAlphabetically()
    {
        Assert.Equal(
            new[] { "4.0.2d", "4.0.2c", "4.0.2b", "4.0.2" },
            VersionOrder.Newest(new[] { "4.0.2b", "4.0.2", "4.0.2d", "4.0.2c" }));
    }

    [Fact]
    public void APreReleaseStillRanksBelowItsRelease()
    {
        // The new rule must not disturb the old one: a "-" or "+" suffix is a
        // pre-release and ranks below, a bare letter is a revision and ranks above.
        Assert.Equal(
            new[] { "0.6.7d", "0.6.7", "0.6.7-pre14", "0.6.7-pre9" },
            VersionOrder.Newest(new[] { "0.6.7-pre9", "0.6.7", "0.6.7-pre14", "0.6.7d" }));
    }

    [Fact]
    public void TheRevisionSuffixIsOnlyLettersAttachedToAVersion()
    {
        Assert.Equal("d", VersionOrder.RevisionSuffix("4.0.2d"));
        Assert.Equal("b", VersionOrder.RevisionSuffix("v4.1.1B"));      // case-insensitive
        Assert.Equal("", VersionOrder.RevisionSuffix("4.0.2"));
        Assert.Equal("", VersionOrder.RevisionSuffix("0.6.7-pre14"));   // a pre-release
        Assert.Equal("", VersionOrder.RevisionSuffix("1.0.1.38338"));   // a file version
        Assert.Equal("", VersionOrder.RevisionSuffix("4.0.2_final"));   // not only letters
        Assert.Equal("", VersionOrder.RevisionSuffix("nightly"));       // no version at all
        Assert.Equal("", VersionOrder.RevisionSuffix(null));
    }

    [Fact]
    public void TheOrdinalOnlyAppliesToPreReleases()
    {
        // On a plain version the regex would match the version's own last number, which
        // is what made the comparison wrong.
        Assert.Equal(0, VersionOrder.PreReleaseOrdinal("4.0.2"));
        Assert.Equal(0, VersionOrder.PreReleaseOrdinal("1.0.1.38338"));
        Assert.Equal(14, VersionOrder.PreReleaseOrdinal("0.6.7-pre14"));
    }

    // ── The version shapes the swap route actually meets ────────────────────

    [Fact]
    public void TheRealVersionShapesAllSortNewestFirst()
    {
        // Each of these is a real list the app shows, and each has a trap in it.

        // The DLSS archive spans a numbering change: 310.x came after 3.7.x.
        Assert.Equal("310.9.1.0", VersionOrder.Newest(
            new[] { "2.5.1.0", "310.9.1.0", "3.7.20.0", "310.2.1.0", "1.0.0.0" })[0]);

        // FidelityFX runtime builds differ only in the fourth part.
        Assert.Equal("1.0.2.38022", VersionOrder.Newest(
            new[] { "1.0.0.36208", "1.0.1.38338", "1.0.2.38022", "1.0.1.41314" })[0]);

        // FSR versions, where a text sort puts 3.1.10 below 3.1.4.
        Assert.Equal(
            new[] { "4.1.1", "4.0.1", "3.1.10", "3.1.4" },
            VersionOrder.Newest(new[] { "3.1.4", "3.1.10", "4.1.1", "4.0.1" }));
    }

    [Fact]
    public void ABuildIsOnlyNewerWhenItReallyIsALaterBuild()
    {
        // The game-page row asks this question about every library it shows, so an
        // equal build must not read as an update.
        Assert.True(VersionOrder.IsNewer("310.9.1.0", "310.1.0.0"));
        Assert.False(VersionOrder.IsNewer("310.1.0.0", "310.9.1.0"));
        Assert.False(VersionOrder.IsNewer("310.9.1.0", "310.9.1.0"));

        // The numbering change: 310.x came after 3.7.x.
        Assert.True(VersionOrder.IsNewer("310.2.1.0", "3.7.20.0"));

        // A text sort puts 3.1.10 below 3.1.4.
        Assert.True(VersionOrder.IsNewer("3.1.10", "3.1.4"));

        // Community revision letters, and pre-releases, rank as they sort.
        Assert.True(VersionOrder.IsNewer("4.1.1b", "4.1.1"));
        Assert.False(VersionOrder.IsNewer("4.1.1", "4.1.1b"));
        Assert.True(VersionOrder.IsNewer("0.7.0", "0.7.0-pre66"));
        Assert.True(VersionOrder.IsNewer("0.7.0-pre66", "0.7.0-pre14"));

        // An unreadable version is not an excuse to claim an update either way round,
        // but anything real beats nothing.
        Assert.True(VersionOrder.IsNewer("310.1.0.0", null));
        Assert.False(VersionOrder.IsNewer(null, "310.1.0.0"));
        Assert.False(VersionOrder.IsNewer(null, null));
    }
}
