// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace UpscalerManager.Core.Models;

/// <summary>
/// Orders component version strings newest-first.
///
/// Every component we track — OptiScaler, its betas and extras, OptiPatcher, fakenvapi,
/// Nukem's FG — tags releases in the same rough shape, so they all sort the same way
/// here rather than each growing its own comparer. Real tags from the OptiScaler repo
/// show what this has to cope with:
///
///     v0.9.3   v0.7.9   v0.7.7-pre9   v0.7.0-pre66   v0.6.7   v0.6.7-pre14   v0.7-old_nightly
///
/// which pins down all four rules below:
///
///   1. Compare the numeric part first, so 0.9.3 beats 0.7.9.
///   2. A final release outranks a pre-release of the same version: 0.6.7 beats 0.6.7-pre14.
///   3. Among pre-releases of one version, order by the trailing number: pre66 > pre14 > pre9.
///   4. Fall back to plain text so the order is at least stable and repeatable.
///
/// Sorting these as plain strings — which several call sites used to do — gets rules 1
/// and 3 wrong (pre9 above pre14, and 1.9 above 1.10), and that order is not cosmetic:
/// the offline fallback installs whatever lands first.
///
/// One more rule comes from the community FSR builds, which tag revisions with a bare
/// trailing letter:
///
///     4.0.2   4.0.2d   4.1.1   4.1.1b
///
///   5. A letter attached directly to the number is a <em>later</em> revision, not a
///      pre-release: 4.0.2d outranks 4.0.2, and 4.1.1b outranks 4.1.1. Only a suffix
///      introduced by "-" or "+" means pre-release. Without this the newest build of a
///      version sorted below the oldest, and "latest first" listed the wrong one first.
/// </summary>
public static class VersionOrder
{
    /// <summary>The numeric part: "v0.7.0-pre66" is 0.7.0. Unparseable text is 0.0.</summary>
    public static Version BaseVersion(string? version)
    {
        var text = Trimmed(version);
        var dash = text.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) text = text[..dash];

        // Stop at the first thing that is not part of a number, so "0.7_custom" is 0.7.
        var digits = new string(text.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).TrimEnd('.');
        if (digits.Length == 0) return new Version(0, 0);

        // Version.TryParse needs at least major.minor, so "3" has to become "3.0".
        if (!digits.Contains('.')) digits += ".0";
        return Version.TryParse(digits, out var parsed) ? parsed : new Version(0, 0);
    }

    /// <summary>True when this is a pre-release ("0.6.7-pre14"), which ranks below "0.6.7".</summary>
    public static bool IsPreRelease(string? version) => Trimmed(version).IndexOfAny(new[] { '-', '+' }) >= 0;

    /// <summary>
    /// The number ending a pre-release tag: "pre66" is 66. Tags that end in something
    /// else ("old_nightly") have no ordinal and sort below the numbered ones.
    ///
    /// Only meaningful for a pre-release. On a plain version the regex would match the
    /// version's own last number — which is how "4.0.2" came to outrank "4.0.2d", since
    /// the lettered tag ends in a letter and scored zero.
    /// </summary>
    public static int PreReleaseOrdinal(string? version)
    {
        if (!IsPreRelease(version)) return 0;
        var match = Regex.Match(Trimmed(version), @"\d+$");
        return match.Success && int.TryParse(match.Value, out var value) ? value : 0;
    }

    /// <summary>
    /// The revision letter a community build appends to its version: "4.0.2d" is
    /// revision "d", and the bare "4.0.2" is the revision before any of them.
    ///
    /// Lowercased so "4.0.2D" and "4.0.2d" are the same revision, and empty for
    /// anything that is not a plain version followed by letters — a pre-release suffix
    /// is handled by <see cref="PreReleaseOrdinal"/> instead.
    /// </summary>
    public static string RevisionSuffix(string? version)
    {
        var text = Trimmed(version);
        if (text.Length == 0) return string.Empty;

        var separator = text.IndexOfAny(new[] { '-', '+' });
        if (separator >= 0) return string.Empty;   // a pre-release, not a revision

        var digits = 0;
        while (digits < text.Length && (char.IsDigit(text[digits]) || text[digits] == '.')) digits++;
        if (digits == 0 || digits == text.Length) return string.Empty;

        var suffix = text[digits..];
        return suffix.All(char.IsLetter) ? suffix.ToLowerInvariant() : string.Empty;
    }

    private static string Trimmed(string? version) => (version ?? string.Empty).Trim().TrimStart('v', 'V');

    /// <summary>Newest first.</summary>
    public static IComparer<string> Descending { get; } = new DescendingComparer();

    /// <summary>Sorts a set of version strings newest-first, dropping duplicates.</summary>
    public static List<string> Newest(IEnumerable<string> versions) =>
        versions.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, Descending).ToList();

    private sealed class DescendingComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            var byVersion = BaseVersion(y).CompareTo(BaseVersion(x));
            if (byVersion != 0) return byVersion;

            // A final release outranks any pre-release carrying the same number.
            var byStage = IsPreRelease(x).CompareTo(IsPreRelease(y));
            if (byStage != 0) return byStage;

            var byOrdinal = PreReleaseOrdinal(y).CompareTo(PreReleaseOrdinal(x));
            if (byOrdinal != 0) return byOrdinal;

            // Same number, neither a pre-release: a revision letter is the later build.
            var byRevision = string.Compare(
                RevisionSuffix(y), RevisionSuffix(x), StringComparison.Ordinal);
            if (byRevision != 0) return byRevision;

            return string.Compare(y, x, StringComparison.OrdinalIgnoreCase);
        }
    }
}
