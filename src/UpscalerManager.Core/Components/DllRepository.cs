// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.Linq;

namespace UpscalerManager.Core.Components;

/// <summary>
/// The DLSS Swapper project's archive of vendor upscaler DLLs, as a download source.
///
/// The one part of DLSS Swapper that cannot be reimplemented, only used: the value is
/// not its code but some 229 archived builds — every DLSS release back to 2018, the
/// FidelityFX runtimes, and Intel's XeSS family — collected out of shipped games and
/// vendor SDK zips over years. No amount of local scanning reconstructs a build the
/// user never owned.
///
/// why: this app links to that archive and never mirrors it. The files are hosted by
/// one volunteer, and Nvidia's licence — which DLSS Swapper itself ships verbatim —
/// says the SDK "may not be distributed or sublicensed as a stand-alone product". So
/// the bytes come from their host on an explicit press, and the whole source has a
/// switch. See the README's download policy.
///
/// There is no FSR 4 in the manifest, so this does not replace the OptiScaler route.
/// </summary>
public static class DllRepository
{
    /// <summary>
    /// The index: a half-megabyte JSON file on GitHub Pages listing every archived
    /// build with its download URL and hashes.
    /// </summary>
    public const string ManifestUrl = "https://beeradmoore.github.io/dlss-swapper/manifest.json";

    /// <summary>Where the archive comes from, for the row that says so.</summary>
    public const string SourceName = "DLSS Swapper";

    /// <summary>The project page, so a user can judge the source themselves.</summary>
    public const string ProjectUrl = "https://github.com/beeradmoore/dlss-swapper";

    /// <param name="ManifestKey">The manifest's own key for this file's build list.</param>
    /// <param name="FileName">The file as it sits in a game folder.</param>
    /// <param name="Vendor">Who wrote the library, not who hosts it.</param>
    public sealed record RepositoryFile(string ManifestKey, string FileName, string Vendor);

    /// <summary>All nine files the manifest carries, keyed by its own naming.</summary>
    public static readonly IReadOnlyList<RepositoryFile> All = new[]
    {
        new RepositoryFile("dlss", "nvngx_dlss.dll", "Nvidia"),
        new RepositoryFile("dlss_g", "nvngx_dlssg.dll", "Nvidia"),
        new RepositoryFile("dlss_d", "nvngx_dlssd.dll", "Nvidia"),
        new RepositoryFile("xess", "libxess.dll", "Intel"),
        new RepositoryFile("xess_dx11", "libxess_dx11.dll", "Intel"),
        new RepositoryFile("xess_fg", "libxess_fg.dll", "Intel"),
        new RepositoryFile("xell", "libxell.dll", "Intel"),
        new RepositoryFile("fsr_31_dx12", "amd_fidelityfx_dx12.dll", "AMD"),
        new RepositoryFile("fsr_31_vk", "amd_fidelityfx_vk.dll", "AMD"),
    };

    private static readonly Dictionary<string, RepositoryFile> ByName =
        All.ToDictionary(f => f.FileName, StringComparer.OrdinalIgnoreCase);

    /// <summary>What the archive holds for this file, or null when it holds nothing.</summary>
    public static RepositoryFile? For(string? fileName) =>
        fileName is not null && ByName.TryGetValue(fileName, out var f) ? f : null;

    /// <summary>True when the archive carries builds of this file.</summary>
    public static bool Covers(string? fileName) => For(fileName) is not null;

    /// <summary>
    /// Every file the archive can supply, so a caller can say which of the swappable
    /// set this route reaches without walking the manifest.
    /// </summary>
    public static IEnumerable<string> FileNames => ByName.Keys;
}
