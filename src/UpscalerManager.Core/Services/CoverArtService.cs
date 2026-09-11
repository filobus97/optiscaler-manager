// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UpscalerManager.Core.Logging;
using UpscalerManager.Core.Models;

namespace UpscalerManager.Core.Services;

/// <summary>
/// Finds the cover image for a game, and keeps it on disk so it is fetched once.
///
/// Steam publishes library art on its CDN keyed by the app id, which the scanner
/// already records, so Steam games cost nothing to illustrate. Nothing equivalent
/// exists for the other launchers without an API key, so those keep their placeholder
/// — a card without art still shows everything that matters about the game.
/// </summary>
public sealed class CoverArtService
{
    /// <summary>Portrait art, then the wide header as a fallback — some games only have one.</summary>
    private static readonly string[] SteamCandidates =
    {
        "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/library_600x900.jpg",
        "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/header.jpg",
    };

    /// <summary>How long before art is looked for again. Covers do change, but rarely.</summary>
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(30);

    private static readonly SemaphoreSlim Throttle = new(4, 4);

    public static string CacheDirectory => Path.Combine(AppDataPaths.Cache, "Covers");

    /// <summary>Where a game's cover would live, whether or not it has been fetched.</summary>
    public static string? CachePathFor(Game game)
    {
        var key = KeyFor(game);
        return key is null ? null : Path.Combine(CacheDirectory, key + ".jpg");
    }

    /// <summary>
    /// The cover for a game, or null when there is none to be had. Returns immediately
    /// from disk when already cached.
    /// </summary>
    public async Task<string?> GetCoverAsync(Game game, CancellationToken cancel = default)
    {
        if (CachePathFor(game) is not { } path) return null;

        // A zero-length file marks "looked, found nothing" so a game without art is not
        // re-requested on every scan.
        if (File.Exists(path))
        {
            var info = new FileInfo(path);
            if (DateTime.UtcNow - info.LastWriteTimeUtc < RefreshAfter)
                return info.Length > 0 ? path : null;
        }

        if (game.Platform != GamePlatform.Steam || string.IsNullOrEmpty(game.AppId))
            return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;

        await Throttle.WaitAsync(cancel);
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            foreach (var template in SteamCandidates)
            {
                var url = string.Format(template, game.AppId);
                try
                {
                    using var response = await NetworkService.GetHttpClient().GetAsync(url, cancel);
                    if (!response.IsSuccessStatusCode) continue;

                    var bytes = await response.Content.ReadAsByteArrayAsync(cancel);
                    if (bytes.Length == 0) continue;

                    await File.WriteAllBytesAsync(path, bytes, cancel);
                    return path;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Write($"[Covers] {game.Name}: {url} failed ({ex.Message}).");
                }
            }

            // Nothing available: leave the marker so we stop asking for a month.
            try { await File.WriteAllBytesAsync(path, Array.Empty<byte>(), cancel); } catch { }
            return null;
        }
        finally { Throttle.Release(); }
    }

    /// <summary>
    /// A stable file name for a game. Steam art is shared by app id; everything else is
    /// keyed by its install path, so two copies of a game do not collide.
    /// </summary>
    private static string? KeyFor(Game game)
    {
        if (game.Platform == GamePlatform.Steam && !string.IsNullOrEmpty(game.AppId))
            return "steam_" + Sanitise(game.AppId);

        if (string.IsNullOrEmpty(game.InstallPath)) return null;

        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(game.InstallPath.ToLowerInvariant()));
        return "path_" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static string Sanitise(string value) =>
        new(value.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
}
