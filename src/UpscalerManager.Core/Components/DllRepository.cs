// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.Linq;

namespace UpscalerManager.Core.Components;

/// <summary>
/// The DLSS Swapper project's archive of vendor upscaler DLLs, used as a download
/// source for the swap route.
///
/// This is the one part of DLSS Swapper that cannot be reimplemented, only used: the
/// value is not in its code but in some 229 archived builds — every DLSS release back
/// to 1.0.0.0 in 2018, the FidelityFX DX12 and Vulkan runtimes, and Intel's XeSS
/// family — scraped out of shipped games and vendor SDK zips over years. Nothing else
/// holds that history, and no amount of local scanning reconstructs a build the user
/// never owned.
///
/// <para><b>Provenance, stated plainly because it matters.</b> These files are hosted
/// by one volunteer (<c>dlss-swapper-downloads.beeradmoore.com</c>), indexed by a
/// manifest on GitHub Pages, and the manifest's own <c>dll_source</c> field records
/// where each came from — game installs, often via TechPowerUp, and vendor SDK
/// archives. Nvidia's licence, which DLSS Swapper itself ships verbatim, says the SDK
/// "may not be distributed or sublicensed as a stand-alone product". This app
/// therefore <em>links</em> to that archive and never mirrors it: the bytes come from
/// their host on an explicit press, exactly as the OptiScaler-Extras route works, and
/// the whole source can be switched off in Settings.</para>
///
/// <para>The manifest covers nine files, and only nine. There is no FSR 4 in it — no
/// <c>amd_fidelityfx_upscaler_dx12.dll</c> and no <c>amdxcffx64.dll</c> — so this
/// source sits alongside the community-build and OptiScaler-release routes rather
/// than replacing them. Its <c>fsr_31_dx12</c> and <c>fsr_31_vk</c> sections are the
/// FidelityFX <em>runtime</em> libraries, which is precisely the gap the harvest-only
/// library left: those are not published loose by AMD, so before this the only
/// versions available were whatever the user's other games happened to ship.</para>
/// </summary>
public static class DllRepository
{
    /// <summary>
    /// The index. A small JSON file on GitHub Pages — around half a megabyte — listing
    /// every archived build with its download URL and hashes.
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

    /// <summary>
    /// The nine files the manifest carries, keyed by its own naming. The keys are the
    /// manifest's, not ours: <c>fsr_31_dx12</c> is DLSS Swapper's name for
    /// <c>amd_fidelityfx_dx12.dll</c> and has held builds past FSR 3.1 for a while, so
    /// it is a historical label rather than a version constraint.
    /// </summary>
    public static readonly IReadOnlyList<RepositoryFile> All = new[]
    {
        new RepositoryFile("dlss", "nvngx_dlss.dll", "Nvidia"),
        new RepositoryFile("dlss_g", "nvngx_dlssg.dll", "Nvidia"),
        new RepositoryFile("dlss_d", "nvngx_dlssd.dll", "Nvidia"),
        new RepositoryFile("fsr_31_dx12", "amd_fidelityfx_dx12.dll", "AMD"),
        new RepositoryFile("fsr_31_vk", "amd_fidelityfx_vk.dll", "AMD"),
        new RepositoryFile("xess", "libxess.dll", "Intel"),
        new RepositoryFile("xess_dx11", "libxess_dx11.dll", "Intel"),
        new RepositoryFile("xess_fg", "libxess_fg.dll", "Intel"),
        new RepositoryFile("xell", "libxell.dll", "Intel"),
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
