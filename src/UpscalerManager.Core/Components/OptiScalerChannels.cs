// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.

using System.Globalization;
using System.Text.RegularExpressions;

namespace UpscalerManager.Core.Components;

/// <summary>How OptiScaler's release channels name their builds.</summary>
public static class OptiScalerChannels
{
    private static readonly Regex NightlyTag = new(@"^nightly-(\d{8})$", RegexOptions.IgnoreCase);

    /// <summary>
    /// A version as a player reads it: a nightly tag (nightly-20261024) by its date, with the
    /// tag after it so it can still be matched to OptiScaler's own naming; anything else as is.
    /// </summary>
    public static string Describe(string version)
    {
        var match = NightlyTag.Match(version);
        return match.Success && DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd",
                   CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? $"{date.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}  ({version})"
            : version;
    }
}
