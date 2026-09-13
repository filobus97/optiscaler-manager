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
/// AMD publishes its FidelityFX modules too, and under the most permissive licence of
/// the three: the FidelityFX SDK is MIT, and the signed binaries are committed to
/// <c>FidelityFX-SDK</c> under <c>Kits/FidelityFX/signedbin/</c> at every SDK 2 tag.
/// This file used to claim otherwise — that AMD did not publish them loose and
/// OptiScaler's releases were the only route — and that was simply wrong. It mattered,
/// because it is what made FSR 4 look like something only a third-party community build
/// could supply, when AMD ships it themselves.
///
/// Only the SDK 2 module names appear below, because those are the files AMD actually
/// publishes. <c>amd_fidelityfx_dx12.dll</c> does not exist in SDK 2 at all — see
/// <see cref="FidelityFxLayout"/> — so a game carrying that name is either running an
/// SDK 1 monolith, which AMD has deprecated and no longer publishes, or an SDK 2 loader
/// under the old name, which AMD publishes as
/// <c>amd_fidelityfx_loader_dx12.dll</c> instead. Neither can be served by a download
/// without renaming the file, and a DLL's name is what the game loads.
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
    /// <param name="TagIsSdkVersion">
    /// True when the repository's tags name an SDK release rather than this file's own
    /// version. AMD's SDK 2.3.0 ships an upscaler stamped 4.1.1 and a loader stamped
    /// 2.3.0, so a tag cannot be presented as the file's version, and cannot be used to
    /// tell whether that build is already held.
    /// </param>
    public sealed record Source(
        string FileName, string Owner, string Repo, string PathInRepo, string Vendor, string Licence,
        bool TagIsSdkVersion = false);

    private const string NvidiaLicence =
        "Nvidia's SDK licence allows you to obtain and use this file, but not to "
        + "redistribute it on its own. This app downloads it from Nvidia directly and "
        + "never hosts a copy.";

    private const string AmdLicence =
        "The FidelityFX SDK is MIT-licensed, so AMD permits use and redistribution of "
        + "these binaries outright — the most permissive of the three. They are still "
        + "downloaded from AMD directly.";

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

        // AMD's FidelityFX SDK 2 modules, committed as signed binaries. The upscaler is
        // the one that matters: it is where FSR 4 lives, so this is the route to FSR 4
        // from AMD rather than from a community rebuild.
        new Source("amd_fidelityfx_upscaler_dx12.dll", "GPUOpen-LibrariesAndSDKs", "FidelityFX-SDK",
            "Kits/FidelityFX/signedbin/amd_fidelityfx_upscaler_dx12.dll", "AMD", AmdLicence, true),
        new Source("amd_fidelityfx_framegeneration_dx12.dll", "GPUOpen-LibrariesAndSDKs", "FidelityFX-SDK",
            "Kits/FidelityFX/signedbin/amd_fidelityfx_framegeneration_dx12.dll", "AMD", AmdLicence, true),
        new Source("amd_fidelityfx_loader_dx12.dll", "GPUOpen-LibrariesAndSDKs", "FidelityFX-SDK",
            "Kits/FidelityFX/signedbin/amd_fidelityfx_loader_dx12.dll", "AMD", AmdLicence, true),
        new Source("amd_fidelityfx_denoiser_dx12.dll", "GPUOpen-LibrariesAndSDKs", "FidelityFX-SDK",
            "Kits/FidelityFX/signedbin/amd_fidelityfx_denoiser_dx12.dll", "AMD", AmdLicence, true),
        new Source("amd_fidelityfx_radiancecache_dx12.dll", "GPUOpen-LibrariesAndSDKs", "FidelityFX-SDK",
            "Kits/FidelityFX/signedbin/amd_fidelityfx_radiancecache_dx12.dll", "AMD", AmdLicence, true),
    };

    /// <summary>
    /// Tags that cannot supply a file, so they are not offered.
    ///
    /// AMD's SDK 1 tags predate the module split — <c>signedbin</c> holds none of these
    /// files there, and asking for one returns a 404 page rather than a binary. Checked:
    /// the modules appear from v2.0.0 and are absent at v1.1.4.
    /// </summary>
    public static bool TagCanSupply(Source source, string tag)
    {
        if (!source.TagIsSdkVersion) return true;

        var version = VersionFromTag(tag);
        var dot = version.IndexOf('.');
        var head = dot > 0 ? version[..dot] : version;
        return int.TryParse(head, out var major) && major >= 2;
    }

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
