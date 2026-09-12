// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UpscalerManager.Core.Components;
using UpscalerManager.Core.Logging;
using UpscalerManager.Core.Models;

namespace UpscalerManager.Core.Services;

/// <summary>One build the vendor publishes, before anything has been downloaded.</summary>
/// <param name="Tag">The vendor's tag, which is what the URL is built from.</param>
/// <param name="Version">The version that tag names, for the row.</param>
/// <param name="InLibrary">
/// True when a build of this version is already held, so the row can say so rather than
/// offering a 60 MB download of something already on disk.
/// </param>
public sealed record VendorBuild(
    string FileName, string Tag, string Version, string Vendor, string Licence, bool InLibrary);

/// <summary>
/// Downloads swappable DLLs from the vendor that publishes them.
///
/// Only ever on an explicit press: nothing here runs on a scan, on startup, or in the
/// background. These files are large — Nvidia's DLSS library is around 60 MB and
/// Intel's XeSS around 80 MB — and a user on a metered connection should not discover
/// that by watching their allowance disappear.
/// </summary>
public sealed class VendorDllService
{
    private readonly DllLibraryService _library;

    public VendorDllService(DllLibraryService library) => _library = library;

    /// <summary>
    /// Tags are listed once per repository per session. Two of the seven files come from
    /// Intel and three from Nvidia, so without this, opening a few pickers would ask
    /// GitHub the same question repeatedly.
    /// </summary>
    private static readonly Dictionary<string, IReadOnlyList<string>> TagCache = new();
    private static readonly SemaphoreSlim TagLock = new(1, 1);

    /// <summary>
    /// What the vendor offers for this DLL, newest first. Empty when the vendor does not
    /// publish it, or when the network is unavailable — this is an extra route, never a
    /// requirement, so a failure here is reported and shrugged off rather than thrown.
    /// </summary>
    public async Task<IReadOnlyList<VendorBuild>> AvailableAsync(string fileName, CancellationToken cancel = default)
    {
        if (VendorDllSource.For(fileName) is not { } source) return Array.Empty<VendorBuild>();

        IReadOnlyList<string> tags;
        try
        {
            tags = await TagsAsync(source.Owner, source.Repo, cancel);
        }
        catch (Exception ex)
        {
            Log.Write($"[Vendor] Could not list {source.Owner}/{source.Repo} releases: {ex.Message}");
            return Array.Empty<VendorBuild>();
        }

        return tags
            .Select(tag => VendorDllSource.VersionFromTag(tag))
            .Zip(tags, (version, tag) => new VendorBuild(
                source.FileName, tag, version, source.Vendor, source.Licence,
                _library.Has(source.FileName, version)))
            .OrderBy(b => b.Version, VersionOrder.Descending)
            .ToList();
    }

    private static async Task<IReadOnlyList<string>> TagsAsync(string owner, string repo, CancellationToken cancel)
    {
        var key = $"{owner}/{repo}";
        await TagLock.WaitAsync(cancel);
        try
        {
            if (TagCache.TryGetValue(key, out var cached)) return cached;

            var url = $"https://api.github.com/repos/{owner}/{repo}/tags?per_page=30";
            var json = await NetworkService.GetHttpClient().GetStringAsync(url, cancel);

            var tags = ParseTags(json);
            TagCache[key] = tags;
            return tags;
        }
        finally
        {
            TagLock.Release();
        }
    }

    /// <summary>
    /// Tag names out of a GitHub tags response. Separated from the request so it can be
    /// tested without a network: this is the part that breaks if the response shape is
    /// not what was assumed, and the request itself cannot be exercised offline.
    /// </summary>
    internal static IReadOnlyList<string> ParseTags(string json)
    {
        var tags = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return tags;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;
                if (element.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String
                    && name.GetString() is { Length: > 0 } tag)
                    tags.Add(tag);
            }
        }
        catch (JsonException)
        {
            // An error page or a truncated body. No tags is the right answer; the
            // download route is an extra, never a requirement.
        }
        return tags;
    }

    /// <summary>
    /// Fetches one build into the library and returns it.
    ///
    /// The download lands in a temporary file first and only becomes a library entry
    /// once <see cref="DllLibraryService.Import"/> has confirmed it is a 64-bit PE
    /// carrying a version — a truncated transfer or an HTML error page served in place
    /// of the binary would otherwise be filed as a DLL and installed into a game.
    /// </summary>
    public async Task<LibraryDll> DownloadAsync(
        VendorBuild build, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        if (VendorDllSource.For(build.FileName) is not { } source)
            throw new InvalidOperationException($"No vendor publishes {build.FileName}.");

        var url = VendorDllSource.UrlFor(source, build.Tag);
        var staging = Path.Combine(Path.GetTempPath(), "um-vendor-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        var destination = Path.Combine(staging, source.FileName);

        try
        {
            Log.Write($"[Vendor] Downloading {source.FileName} {build.Version} from {source.Vendor}.");
            await LargeFileDownload.ToFileAsync(url, destination, progress, cancel);

            var entry = _library.Import(destination);
            Log.Write($"[Vendor] Added {entry.FileName} {entry.Version} from {source.Vendor}.");
            return entry with { SourceLabel = $"from {source.Vendor}" };
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }

}
