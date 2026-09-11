// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.Linq;

namespace UpscalerManager.Core.Components;

/// <summary>
/// Where a swappable DLL can be downloaded from — the vendor's own repository, and
/// nowhere else.
///
/// Nvidia and Intel both publish these binaries themselves, in public GitHub
/// repositories, at a stable path per tag. So the app fetches the file the user asked
/// for straight from the vendor, exactly as it already fetches OptiScaler from the
/// OptiScaler project. Nothing is hosted, mirrored or re-uploaded by this project.
///
/// That distinction is the whole reason this is done this way. DLSS Swapper solves the
/// same problem by self-hosting some 229 archives scraped out of shipped games, while
/// the Nvidia licence it redistributes verbatim states plainly:
///
///   "you may not distribute or sublicense the SDK as a stand-alone product."
///
/// Downloading from Nvidia's own repository is not distribution by us — it is the user
/// obtaining the file from Nvidia, which they could do by hand. Intel's licence is more
/// permissive still (it allows redistribution of the unmodified binary), but the same
/// route is used for both, because it is also the more reliable one: no volunteer's
/// hosting bill stands between the user and the file.
///
/// AMD's FidelityFX runtimes are deliberately absent. They are not published as loose
/// binaries the way these are, and OptiScaler's own releases already carry them — which
/// the library reads as a local source, with no download at all.
/// </summary>
public static class VendorDllSource
{
    /// <param name="FileName">The swappable DLL this source provides.</param>
    /// <param name="Owner">GitHub owner of the vendor's repository.</param>
    /// <param name="Repo">The vendor's repository.</param>
    /// <param name="PathInRepo">Where the binary sits, relative to the repository root.</param>
    /// <param name="Vendor">Who publishes it, for the row that offers the download.</param>
    /// <param name="Licence">
    /// What the vendor's licence permits, in one line. Shown before a download so the
    /// user is not agreeing to something on their behalf that they cannot see.
    /// </param>
    public sealed record Source(
        string FileName, string Owner, string Repo, string PathInRepo, string Vendor, string Licence);

    private const string NvidiaLicence =
        "Nvidia's SDK licence allows you to obtain and use this file, but not to "
        + "redistribute it on its own. This app downloads it from Nvidia directly and "
        + "never hosts a copy.";

    private const string IntelLicence =
        "Intel's Simplified Software License allows use and redistribution of this "
        + "binary unmodified. It is still downloaded from Intel directly.";

    /// <summary>
    /// The x86_64 release builds. The repositories also carry aarch64 and arm64ec
    /// variants and separate development builds; games ship the x86_64 release one, and
    /// installing a development build into a game would be a debugging-only surprise.
    /// </summary>
    public static readonly IReadOnlyList<Source> All = new[]
    {
        new Source("nvngx_dlss.dll", "NVIDIA", "DLSS",
            "lib/Windows_x86_64/rel/nvngx_dlss.dll", "Nvidia", NvidiaLicence),
        new Source("nvngx_dlssd.dll", "NVIDIA", "DLSS",
            "lib/Windows_x86_64/rel/nvngx_dlssd.dll", "Nvidia", NvidiaLicence),
        new Source("nvngx_dlssg.dll", "NVIDIA", "DLSS",
            "lib/Windows_x86_64/rel/nvngx_dlssg.dll", "Nvidia", NvidiaLicence),

        new Source("libxess.dll", "intel", "xess", "bin/libxess.dll", "Intel", IntelLicence),
        new Source("libxess_dx11.dll", "intel", "xess", "bin/libxess_dx11.dll", "Intel", IntelLicence),
        new Source("libxess_fg.dll", "intel", "xess", "bin/libxess_fg.dll", "Intel", IntelLicence),
        new Source("libxell.dll", "intel", "xess", "bin/libxell.dll", "Intel", IntelLicence),
    };

    private static readonly Dictionary<string, Source> ByName =
        All.ToDictionary(s => s.FileName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Where this DLL can be downloaded from, or null when nowhere.</summary>
    public static Source? For(string? fileName) =>
        fileName is not null && ByName.TryGetValue(fileName, out var s) ? s : null;

    /// <summary>True when the vendor publishes this file for download.</summary>
    public static bool CanDownload(string? fileName) => For(fileName) is not null;

    /// <summary>The repositories to ask for tags, each covering several files.</summary>
    public static IEnumerable<(string Owner, string Repo)> Repositories =>
        All.Select(s => (s.Owner, s.Repo)).Distinct();

    /// <summary>The file's URL at one of the vendor's tags.</summary>
    public static string UrlFor(Source source, string tag) =>
        $"https://raw.githubusercontent.com/{source.Owner}/{source.Repo}/{tag}/{source.PathInRepo}";

    /// <summary>
    /// The version a tag names — "v310.9.1" is release 310.9.1. Kept separate from the
    /// version read out of the downloaded binary, which is what the library actually
    /// keys on; this is only what the row says before anything is fetched.
    /// </summary>
    public static string VersionFromTag(string tag) =>
        tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag[1..] : tag;
}
