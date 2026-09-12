// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UpscalerManager.Core.Components;

/// <summary>
/// The FSR version a FidelityFX runtime library actually provides.
///
/// <para><b>Why this is needed.</b> For every other swappable DLL the version resource
/// is the version a player recognises: <c>nvngx_dlss.dll</c> reporting 310.9.1.0 means
/// DLSS 310.9.1. The FidelityFX runtimes do not work that way. AMD stamps them with an
/// SDK build number, so the library providing <b>FSR 3.1.2</b> reports itself as
/// <c>1.0.1.38338</c> — a number that appears nowhere in AMD's documentation, cannot be
/// compared against anything a player has read, and looks alarmingly like a
/// <em>downgrade</em> next to a game's FSR 3 library.</para>
///
/// <para>DLSS Swapper solves it by loading the DLL and calling its <c>ffxQuery</c>
/// entry point for the provider version table. That is not available here: these are
/// Windows PEs and this app's primary platform is Linux, so there is nothing to load
/// them into.</para>
///
/// <para>Two routes work without executing anything:</para>
///
/// <list type="number">
/// <item>
/// The DLSS Swapper index records the FSR version for every build it archives, keyed
/// by MD5. That is authoritative — it is AMD's own label — and it is what
/// <see cref="Services.DllRepositoryService"/> supplies.
/// </item>
/// <item>
/// Failing that, the provider version table is a run of plain NUL-terminated ASCII
/// strings in the binary's data, and can simply be read out. See
/// <see cref="FromProviderTable"/> for why that is safer than it sounds.
/// </item>
/// </list>
/// </summary>
public static class FidelityFxVersion
{
    /// <summary>
    /// The files whose version resource needs translating.
    ///
    /// Only these two, and only because both were checked against every build the
    /// archive holds — nine DX12 and seven Vulkan, all sixteen resolved exactly. The
    /// DX12 loader is deliberately absent: it plausibly carries the same table, but
    /// "plausibly" is not a basis for showing a player a version number.
    /// </summary>
    private static readonly HashSet<string> Translated = new(StringComparer.OrdinalIgnoreCase)
    {
        "amd_fidelityfx_dx12.dll",
        "amd_fidelityfx_vk.dll",
    };

    /// <summary>
    /// True when this file's version resource is an SDK build number rather than the
    /// FSR version, so a bare version is misleading on its own.
    /// </summary>
    public static bool Translates(string? fileName) =>
        fileName is not null && Translated.Contains(fileName);

    /// <summary>
    /// Below this, the number found is not an FSR marketing version.
    ///
    /// The table holds one entry per provider the library carries — the FSR 2 API at
    /// 1.1.x, FSR 3 at 2.3.x, FSR 3.1 at 3.1.x — and the FSR version is the highest.
    /// A library predating FSR 3.1 tops out in the 2.x range, and reporting "FSR 2.3.2"
    /// would be inventing a version that does not exist, so nothing is claimed at all.
    /// </summary>
    private const int LowestRealFsrMajor = 3;

    /// <summary>
    /// Reads the FSR version out of a FidelityFX runtime on disk, or null when it
    /// cannot be established. Never throws: an unreadable file just means the row falls
    /// back to showing the build number, which is what it did before.
    /// </summary>
    public static string? FromBinary(string path)
    {
        try
        {
            // These libraries run to about 6.5 MB; reading one to find three short
            // strings is cheap next to the directory scan that found it.
            return FromProviderTable(File.ReadAllBytes(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The FSR version out of a FidelityFX binary's provider version table.
    ///
    /// The table is a sequence of pointer blocks each followed by a NUL-terminated
    /// version string, so the parse is: find every standalone ASCII
    /// <c>major.minor.patch</c> C string, and take the highest at or above
    /// <see cref="LowestRealFsrMajor"/>.
    ///
    /// <para>That sounds fragile and is not, for a specific reason: requiring a NUL on
    /// <em>both</em> sides makes the pattern match only whole short strings, and across
    /// all sixteen archived builds the complete set of matches was exactly the three
    /// provider versions — no other string in a 6.5 MB binary qualified. It is still a
    /// fallback rather than the primary route, and it declines to answer rather than
    /// guess when nothing is in FSR's range.</para>
    /// </summary>
    internal static string? FromProviderTable(ReadOnlySpan<byte> bytes)
    {
        (int Major, int Minor, int Patch)? best = null;

        for (var i = 1; i < bytes.Length - 1; i++)
        {
            if (bytes[i - 1] != 0) continue;          // must start a C string

            var end = i;
            while (end < bytes.Length && bytes[end] != 0) end++;
            if (end >= bytes.Length) break;           // unterminated: end of data

            var length = end - i;
            if (length is < 5 or > 8) { i = end; continue; }   // "1.2.3" … "12.34.567"

            if (Parse(bytes.Slice(i, length)) is { } parsed
                && parsed.Major >= LowestRealFsrMajor
                && (best is null || Compare(parsed, best.Value) > 0))
                best = parsed;

            i = end;
        }

        return best is { } v ? $"{v.Major}.{v.Minor}.{v.Patch}" : null;
    }

    /// <summary>
    /// A <c>major.minor.patch</c> of plain ASCII digits, or null for anything else.
    /// Deliberately strict: three parts, digits only, no leading plus or minus, no
    /// fourth part — a four-part number is a file version, not one of these.
    /// </summary>
    private static (int Major, int Minor, int Patch)? Parse(ReadOnlySpan<byte> ascii)
    {
        Span<int> parts = stackalloc int[3];
        var part = 0;
        var digits = 0;

        foreach (var b in ascii)
        {
            if (b == (byte)'.')
            {
                if (digits == 0 || part == 2) return null;   // ".." or a fourth part
                part++;
                digits = 0;
                continue;
            }

            if (b is < (byte)'0' or > (byte)'9') return null;
            if (++digits > 3) return null;
            parts[part] = parts[part] * 10 + (b - (byte)'0');
        }

        return part == 2 && digits > 0 ? (parts[0], parts[1], parts[2]) : null;
    }

    private static int Compare((int Major, int Minor, int Patch) a, (int Major, int Minor, int Patch) b) =>
        a.Major != b.Major ? a.Major - b.Major
        : a.Minor != b.Minor ? a.Minor - b.Minor
        : a.Patch - b.Patch;

    /// <summary>
    /// How a FidelityFX runtime's version is written for a player: the FSR version
    /// leads, with the build number kept in brackets.
    ///
    /// Both halves are shown because both are needed. The FSR version is the one that
    /// means something; the build number is the one the app reads off the file, so it
    /// is what appears in a log and what distinguishes two builds of the same FSR
    /// version — 1.0.1.38338 and 1.0.2.38022 are both FSR 3.1.2.
    /// </summary>
    public static string Describe(string? fsrVersion, string? fileVersion) =>
        (fsrVersion, fileVersion) switch
        {
            ({ Length: > 0 } fsr, { Length: > 0 } file) => $"FSR {fsr}  ({file})",
            ({ Length: > 0 } fsr, _) => $"FSR {fsr}",
            (_, { Length: > 0 } file) => file,
            _ => "unknown version",
        };
}
