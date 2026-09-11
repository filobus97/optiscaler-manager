// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.IO;
using System.Security.Cryptography;

namespace UpscalerManager.Core.Services;

/// <summary>
/// The app's one file hash. Installs record it so a revert can tell an untouched file
/// from one a game patch has replaced, the DLL library uses it to recognise a build it
/// already holds, and residue detection matches files against the cache with it.
///
/// Pulled out of <see cref="GameInstallationService"/> when the swap route needed the
/// same thing, rather than growing a second implementation that could disagree.
/// </summary>
public static class FileHash
{
    /// <summary>
    /// The file's SHA-256 as uppercase hex, or null when it cannot be read.
    ///
    /// Null rather than throwing, because every caller is recording a fact about a file
    /// for later comparison: a hash that could not be taken means "cannot compare",
    /// which is a legitimate state, not a failure worth aborting an install over.
    /// </summary>
    public static string? Sha256(string filePath)
    {
        try
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True when both files exist and have the same contents. A missing file is not
    /// equal to anything, including another missing file.
    /// </summary>
    public static bool SameContents(string a, string b)
    {
        var left = Sha256(a);
        return left is not null && left == Sha256(b);
    }
}
