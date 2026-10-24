// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.

using System.Text;
using UpscalerManager.Core.Services;

namespace UpscalerManager.Core.Components;

/// <summary>What a DLL offered to the omniloader turns out to be.</summary>
public enum OmniBuildKind
{
    /// <summary>An FSR upscaler with the five FidelityFX functions: it goes in the omniloader's list.</summary>
    Module,

    /// <summary>
    /// AMD's driver file for DirectX 12 (amdxcffx64.dll), or a build made like it: it hands
    /// FSR 4 out through <c>UpdateFfxApiProvider</c> instead of the FidelityFX API. The
    /// omniloader hosts it like any build, under a name of its own (<see cref="Omniloader.DriverPrefix"/>).
    /// The Vulkan file (amdvkffx64.dll) cannot be: the omniloader stands in for the DX12 upscaler.
    /// </summary>
    DriverStyle,

    /// <summary>Anything else: frame generation, a loader, or not a FidelityFX DLL at all.</summary>
    NotUpscaler,
}

/// <summary>One build to put behind the omniloader: where it comes from, and its label.</summary>
public sealed record OmniBuild(string SourcePath, string Label);

/// <summary>
/// The omniloader: a stand-in for OptiScaler's FSR upscaler DLL that loads several
/// builds and offers all their versions in OptiScaler's FFX Upscaler list, so they can
/// be switched in-game. Its source is native/omniloader; the app carries the built DLL.
/// </summary>
public static class Omniloader
{
    /// <summary>The DLL it stands in for, and the name it is installed under.</summary>
    public const string UpscalerDllName = "amd_fidelityfx_upscaler_dx12.dll";

    /// <summary>The list it reads, beside it: its own name with .omni.ini.</summary>
    public const string IniName = "amd_fidelityfx_upscaler_dx12.omni.ini";

    /// <summary>Where the builds go, relative to the omniloader's folder.</summary>
    public const string BuildsFolder = "omni/upscaler";

    private const string ResourceName = "omniloader.dll";

    private static readonly string[] FfxExports =
        { "ffxCreateContext", "ffxDestroyContext", "ffxConfigure", "ffxQuery", "ffxDispatch" };

    /// <summary>
    /// Whether this copy of the app carries the omniloader. Builds made without
    /// MinGW-w64 do not, and then the option is not offered.
    /// </summary>
    public static bool IsAvailable =>
        typeof(Omniloader).Assembly.GetManifestResourceInfo(ResourceName) is not null;

    /// <summary>Writes the omniloader DLL to <paramref name="path"/>.</summary>
    public static void ExtractTo(string path)
    {
        using var source = typeof(Omniloader).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("This build of the app does not include the omniloader.");
        using var target = File.Create(path);
        source.CopyTo(target);
    }

    /// <summary>
    /// What <paramref name="path"/> is, read from what it exports. The file name only
    /// tells an upscaler from frame generation, which export the same five functions.
    /// </summary>
    public static OmniBuildKind Classify(string path)
    {
        var exports = PeExports.Read(path);
        if (FfxExports.All(e => exports.Contains(e)))
        {
            var name = Path.GetFileName(path).ToLowerInvariant();
            return name.Contains("framegeneration") || name.Contains("denoiser") || name.Contains("fidelityfx_loader")
                ? OmniBuildKind.NotUpscaler
                : OmniBuildKind.Module;
        }
        return exports.Contains("UpdateFfxApiProvider") ? OmniBuildKind.DriverStyle : OmniBuildKind.NotUpscaler;
    }

    /// <summary>
    /// What every build's file name starts with. OptiScaler treats DLLs by name: on Linux
    /// it answers FSR 4's GPU check for the card only when the asking DLL's name starts
    /// with "amd", and with forced INT8 it spoofs the vendor only for names starting with
    /// this. A build renamed to anything else decides the GPU cannot run FSR 4 and drops
    /// its FSR 4 versions. The name must not end like AMD's either, or OptiScaler's load
    /// hook takes it for the upscaler itself.
    /// </summary>
    public const string BuildPrefix = "amd_fidelityfx_upscaler_dx12_";

    /// <summary>
    /// What a hosted driver file's name starts with. It keeps "amdxcffx64" at the front,
    /// which is what OptiScaler checks the callers of its GPU answers by, and never is
    /// amdxcffx64.dll itself: OptiScaler loads its own copy by that name, and a second module
    /// under it would be taken for that one.
    /// </summary>
    public const string DriverPrefix = "amdxcffx64_";

    /// <summary>
    /// The file name each build gets in <see cref="BuildsFolder"/>, from its label:
    /// the builds mostly share one name, so they cannot keep theirs.
    /// </summary>
    public static IReadOnlyList<string> FileNamesFor(IReadOnlyList<string> labels)
        => FileNamesFor(labels.Select(l => (l, OmniBuildKind.Module)).ToList());

    /// <summary>
    /// As <see cref="FileNamesFor(IReadOnlyList{string})"/>, with a driver file named by
    /// <see cref="DriverPrefix"/> instead of <see cref="BuildPrefix"/>.
    /// </summary>
    public static IReadOnlyList<string> FileNamesFor(IReadOnlyList<(string Label, OmniBuildKind Kind)> builds)
    {
        var names = new List<string>();
        foreach (var (label, kind) in builds)
        {
            var prefix = kind == OmniBuildKind.DriverStyle ? DriverPrefix : BuildPrefix;
            var slug = new StringBuilder();
            foreach (var c in label.ToLowerInvariant())
                slug.Append(char.IsAsciiLetterOrDigit(c) || c == '.' ? c : '-');
            var stem = string.Join('-', slug.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
            if (stem.EndsWith(".dll", StringComparison.Ordinal)) stem = stem[..^4];
            if (stem.Length == 0) stem = "build";

            var name = prefix + stem + ".dll";
            for (var n = 2; names.Contains(name, StringComparer.OrdinalIgnoreCase); n++)
                name = $"{prefix}{stem}-{n}.dll";
            names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// The .omni.ini listing <paramref name="builds"/> in order — the first is the one
    /// that runs until another is picked in the overlay.
    /// </summary>
    public static string IniFor(IReadOnlyList<(string FileName, string Label)> builds)
    {
        var ini = new StringBuilder();
        ini.Append("; Written by Upscaler Manager. The first build runs until another is picked\r\n");
        ini.Append("; in OptiScaler's overlay, under FFX Upscaler.\r\n");
        ini.Append("[Modules]\r\n");
        foreach (var (file, label) in builds)
            ini.Append(BuildsFolder.Replace('/', '\\')).Append('\\').Append(file)
               .Append(" = ").Append(label.Replace('\r', ' ').Replace('\n', ' ')).Append("\r\n");
        return ini.ToString();
    }
}
