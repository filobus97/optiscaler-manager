// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace UpscalerManager.Core.Components;

/// <summary>What FSR versions a FidelityFX library can provide, and which file said so.</summary>
public sealed record FfxProviders(IReadOnlyList<string> Versions, string FromFile);

/// <summary>
/// Finds the FidelityFX library in a directory and reads the FSR versions it provides.
///
/// <para>The point of doing this over a <em>cache</em> directory rather than only over a
/// game folder: the install screen has to answer "which FSR versions will I be able to
/// choose from" <b>before</b> the install happens. Reading the game's current library
/// answers the wrong question — a game about to receive an FSR 4 INT8 build has no FSR 4
/// in it yet, so the version picker offered 3.1.2 and older and FSR 4 looked
/// unavailable exactly when it was being installed.</para>
///
/// <para>The libraries this app is about to copy in are already on disk, in the
/// component caches, so the honest answer is to read those.</para>
/// </summary>
public static class FidelityFxProviderScanner
{
    /// <summary>
    /// Files worth reading, in priority order. The SDK 2 upscaler module decides the FSR
    /// version where one is present; the SDK 1 monolith carries the providers itself;
    /// <c>amdxcffx64.dll</c> is what a community INT8 build ships as on recent releases.
    /// </summary>
    private static readonly string[] Candidates =
    {
        "amd_fidelityfx_upscaler_dx12.dll",
        "amdxcffx64.dll",
        "amd_fidelityfx_dx12.dll",
    };

    /// <summary>
    /// The providers of the first FidelityFX library found under <paramref name="directory"/>,
    /// or null when there is none. Searches recursively, because release layouts move
    /// between versions, and never throws.
    /// </summary>
    public static FfxProviders? FromDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;

        foreach (var candidate in Candidates)
        {
            IEnumerable<string> matches;
            try
            {
                matches = Directory.EnumerateFiles(directory, candidate, SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var path in matches)
            {
                var versions = FidelityFxVersion.AllFromBinary(path);
                if (versions.Count > 0) return new FfxProviders(versions, Path.GetFileName(path));
            }
        }

        return null;
    }

    /// <summary>
    /// The FSR version a community build's release tag names — "FSR_4.1.1b" is 4.1.1.
    ///
    /// A fallback for a version that has not been downloaded yet, where there is no
    /// binary to read. The Extras releases are tagged by the FSR version they carry,
    /// which makes the tag a reasonable statement of what the build provides — but it is
    /// the author's label rather than something read out of the file, so it is only used
    /// when the file is not there.
    /// </summary>
    public static string? VersionFromReleaseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var match = Regex.Match(tag, @"(\d+)\.(\d+)\.(\d+)");
        return match.Success ? $"{match.Groups[1].Value}.{match.Groups[2].Value}.{match.Groups[3].Value}" : null;
    }

    /// <summary>
    /// Merges several provider lists into one, newest first and without duplicates.
    ///
    /// Union rather than replacement, because an install does not necessarily remove
    /// what the game already had: OptiScaler loads whichever library ends up in the
    /// folder, and both the game's own and the one being installed can be there.
    /// </summary>
    public static IReadOnlyList<string> Merge(params IEnumerable<string>?[] lists) =>
        lists
            .Where(l => l is not null)
            .SelectMany(l => l!)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, Models.VersionOrder.Descending)
            .ToList();
}
