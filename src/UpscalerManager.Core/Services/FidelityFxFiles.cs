// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.

using UpscalerManager.Core.Components;
using UpscalerManager.Core.Logging;

namespace UpscalerManager.Core.Services;

/// <summary>
/// Finds AMD's FidelityFX DLLs in a folder and says what each one is, whatever it is called.
///
/// why: a game can rename these files, and the name is all the rest of the app goes by.
/// The DLL still says what it was built as in its version resource, so a file with an
/// unfamiliar name is read for that before it is passed over.
/// </summary>
public static class FidelityFxFiles
{
    private static readonly string[] FfxExports =
        { "ffxCreateContext", "ffxDestroyContext", "ffxConfigure", "ffxQuery", "ffxDispatch" };

    private const long LargestWorthReading = 512L << 20;
    private const int MostDllsToLookAt = 6000;

    /// <summary>
    /// What <paramref name="path"/> is in AMD's DX12 runtime, or null when it is none of it.
    /// Vulkan builds are not covered: the FSR 4 modules are DX12.
    /// </summary>
    public static FidelityFxFile? Identify(string path)
    {
        try
        {
            var name = Path.GetFileName(path);
            var known = FidelityFxLayout.IsFidelityFx(name);

            // The name the DLL goes by for the purpose of telling what it is: its own when
            // that is one of AMD's, otherwise the one its version resource says it was built as.
            string basis = name;
            if (!known)
            {
                var strings = PeFileInspector.ReadVersionStrings(path);
                if (strings.OriginalFilename is { } original && FidelityFxLayout.IsFidelityFx(original))
                    basis = original;
                else if (FromDescription(strings.FileDescription) is { } described && ExportsFfx(path))
                    basis = described;
                else
                    return null;
            }

            if (basis.Contains("_vk", StringComparison.OrdinalIgnoreCase)) return null;

            var pe = PeFileInspector.Inspect(path);
            if (!pe.IsValidPe || !pe.Is64Bit) return null;

            var version = DllLibraryService.VersionOf(pe);
            // The ambiguous legacy name is settled by what is inside, as everywhere else.
            var effect = FidelityFxLayout.Identify(basis, null, null) == FidelityFxRole.Monolith
                ? FidelityFxVersion.FromBinary(path)
                : null;

            var role = FidelityFxLayout.Identify(basis, version, effect);
            return role == FidelityFxRole.NotFidelityFx ? null : new FidelityFxFile(path, role, version, effect);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool ExportsFfx(string path)
    {
        var exports = PeExports.Read(path);
        return FfxExports.All(e => exports.Contains(e));
    }

    /// <summary>
    /// A name for a DLL that has none of AMD's, from what its description says — the
    /// ambiguous legacy name when the description does not say which module it is.
    /// </summary>
    private static string? FromDescription(string? description)
    {
        if (description is null || !description.Contains("fidelityfx", StringComparison.OrdinalIgnoreCase))
            return null;

        var d = description.ToLowerInvariant();
        return d.Contains("loader") ? "amd_fidelityfx_loader_dx12.dll"
            : d.Contains("upscal") ? "amd_fidelityfx_upscaler_dx12.dll"
            : d.Contains("frame") ? "amd_fidelityfx_framegeneration_dx12.dll"
            : d.Contains("denois") ? "amd_fidelityfx_denoiser_dx12.dll"
            : d.Contains("radiance") ? "amd_fidelityfx_radiancecache_dx12.dll"
            : "amd_fidelityfx_dx12.dll";
    }

    /// <summary>
    /// Every FidelityFX DX12 DLL under a folder. Files that are not DLLs, or are far too
    /// large to be one of these, are never opened.
    /// </summary>
    public static IReadOnlyList<FidelityFxFile> Scan(string folder)
    {
        var found = new List<FidelityFxFile>();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return found;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        var looked = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.dll", options))
            {
                if (++looked > MostDllsToLookAt)
                {
                    Log.Write($"[Fsr4] Stopped looking for FidelityFX files in {folder} after {MostDllsToLookAt} DLLs.");
                    break;
                }

                long length;
                try { length = new FileInfo(path).Length; }
                catch (IOException) { continue; }
                if (length > LargestWorthReading) continue;

                if (Identify(path) is { } file) found.Add(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"[Fsr4] Could not read {folder}: {ex.Message}");
        }

        return found;
    }
}
