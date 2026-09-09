// OptiScaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.IO;
using System.Linq;

namespace OptiscalerManager.Core.Components;

/// <summary>
/// The file a community FSR 4 INT8 build ships as.
///
/// It was renamed between releases: older builds ship a patched
/// <c>amd_fidelityfx_upscaler_dx12.dll</c>, newer ones ship <c>amdxcffx64.dll</c>.
/// Looking for only the old name makes every recent release fail to install, so both
/// are accepted and whichever one the archive contains is installed under its own
/// name — the name is what OptiScaler loads, so it must not be rewritten.
///
/// Note that the newer name is the same filename as AMD's proprietary FSR 4 runtime.
/// It is not that file: it is a community build that happens to occupy the same slot.
/// This app still never downloads AMD's own binary.
/// </summary>
public static class Fsr4Int8Build
{
    /// <summary>What older INT8 releases ship.</summary>
    public const string LegacyDllName = "amd_fidelityfx_upscaler_dx12.dll";

    /// <summary>What newer INT8 releases ship.</summary>
    public const string CurrentDllName = "amdxcffx64.dll";

    /// <summary>Both names, newest first.</summary>
    public static readonly string[] KnownDllNames = { CurrentDllName, LegacyDllName };

    /// <summary>True when this filename is one an INT8 build ships as.</summary>
    public static bool IsKnown(string? fileName) =>
        fileName is not null && KnownDllNames.Contains(fileName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The INT8 DLL inside a folder, or null when there is none. Prefers the current
    /// name if a folder somehow holds both.
    /// </summary>
    public static string? FindIn(string directory)
    {
        foreach (var name in KnownDllNames)
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
