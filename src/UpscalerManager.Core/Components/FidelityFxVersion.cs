// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UpscalerManager.Core.Components;

/// <summary>
/// The FSR version a FidelityFX runtime library actually provides.
///
/// why: AMD stamps these with an SDK build number, so the library providing FSR 3.1.2
/// reports itself as 1.0.1.38338 — which reads as a downgrade next to a game's FSR 3.
/// The real version is read out of the binary's provider table rather than by calling
/// into it, because these are Windows PEs and this app runs on Linux. See
/// docs/fidelityfx.md.
/// </summary>
public static class FidelityFxVersion
{
    /// <summary>
    /// The files whose version resource needs translating: these two, verified against
    /// all sixteen builds the archive holds. The SDK 2 loader is deliberately absent —
    /// it may carry the same table, and "may" is not a basis for a version number.
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
    public static string? FromBinary(string path) =>
        AllFromBinary(path).FirstOrDefault(v => MajorOf(v) >= LowestRealFsrMajor);

    /// <summary>
    /// Every FSR version a module on disk can provide, newest first, or empty when it
    /// cannot be read. Never throws.
    /// </summary>
    public static IReadOnlyList<string> AllFromBinary(string path)
    {
        try
        {
            // These libraries run to 6.5 MB for an SDK 1 monolith and 29 MB for an
            // SDK 2 upscaler; reading one to find three short strings is still cheap
            // next to the directory scan that found it.
            return AllFromProviderTable(File.ReadAllBytes(path));
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// The FSR version out of a FidelityFX binary's provider table: every standalone
    /// ASCII major.minor.patch C string, highest first, at or above
    /// <see cref="LowestRealFsrMajor"/>.
    ///
    /// why: requiring a NUL on both sides matches only whole short strings, and across
    /// all sixteen archived builds the complete set of matches was exactly the three
    /// provider versions — nothing else in a 6.5 MB binary qualified.
    /// </summary>
    internal static string? FromProviderTable(ReadOnlySpan<byte> bytes) =>
        AllFromProviderTable(bytes).FirstOrDefault(v => MajorOf(v) >= LowestRealFsrMajor);

    private static int MajorOf(string version) =>
        int.TryParse(version.Split('.')[0], out var major) ? major : 0;

    /// <summary>
    /// Every provider version in the binary's table, newest first — the order
    /// [FSR] UpscalerIndex indexes into. See docs/fidelityfx.md.
    ///
    /// why: no floor here, unlike <see cref="FromProviderTable"/>. Every entry is a real
    /// choice, and a game that misbehaves on FSR 4 can be pinned to the FSR 3.1 provider
    /// in the same module. The floor is only for labelling a file, where an SDK-era 2.3.x
    /// shown as an FSR version would invent something that does not exist.
    /// </summary>
    internal static IReadOnlyList<string> AllFromProviderTable(ReadOnlySpan<byte> bytes)
    {
        var found = new List<(int Major, int Minor, int Patch)>();

        for (var i = 1; i < bytes.Length - 1; i++)
        {
            if (bytes[i - 1] != 0) continue;          // must start a C string

            var end = i;
            while (end < bytes.Length && bytes[end] != 0) end++;
            if (end >= bytes.Length) break;           // unterminated: end of data

            var length = end - i;
            if (length is < 5 or > 8) { i = end; continue; }   // "1.2.3" … "12.34.567"

            if (Parse(bytes.Slice(i, length)) is { } parsed && !found.Contains(parsed))
                found.Add(parsed);

            i = end;
        }

        found.Sort((a, b) => Compare(b, a));   // newest first, as AMD reports them
        return found.Select(v => $"{v.Major}.{v.Minor}.{v.Patch}").ToList();
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
