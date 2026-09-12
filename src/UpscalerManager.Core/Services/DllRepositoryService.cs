// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UpscalerManager.Core.Components;
using UpscalerManager.Core.Logging;
using UpscalerManager.Core.Models;

namespace UpscalerManager.Core.Services;

/// <summary>One archived build, before anything has been downloaded.</summary>
/// <param name="Version">
/// The file version, as the binary itself reports it. Keyed on this so
/// <paramref name="InLibrary"/> and the "in the game now" row line up with what the
/// app reads off a real file — the manifest's other version fields are labels.
/// </param>
/// <param name="Label">
/// The vendor's own name for the build where there is one — the FSR marketing version
/// for the FidelityFX runtimes, Nvidia's branch label for DLSS. Empty when the
/// manifest carries none.
/// </param>
/// <param name="Provenance">Where the archive says the file came from, e.g. a game.</param>
/// <param name="IsDevFile">A development build, which the manifest flags separately.</param>
public sealed record RepositoryBuild(
    string FileName,
    string Version,
    string Label,
    string Provenance,
    string DownloadUrl,
    string ZipMd5,
    string DllMd5,
    long FileSize,
    long ZipFileSize,
    bool IsDevFile,
    bool SignatureValid,
    bool InLibrary);

/// <summary>
/// Downloads swappable DLLs from the DLSS Swapper project's archive.
///
/// See <see cref="DllRepository"/> for what the archive is and why this app links to
/// it rather than mirroring it. The mechanics here are DLSS Swapper's own, because its
/// hygiene around this is good and worth copying rather than re-deciding:
///
/// <list type="bullet">
/// <item>the manifest is cached on disk and re-fetched only when stale, so opening a
/// picker costs nothing;</item>
/// <item>a stale cache is preferred to no list at all when the network is down;</item>
/// <item>the downloaded archive is checked against the manifest's
/// <c>zip_md5_hash</c> <em>before</em> being opened, and the extracted DLL against
/// <c>md5_hash</c> before being filed.</item>
/// </list>
///
/// The one thing deliberately not copied is its Authenticode check: there is no
/// <c>wintrust</c> on Linux, so the manifest's recorded signature state is surfaced as
/// information on the row instead of enforced.
/// </summary>
public sealed class DllRepositoryService
{
    private readonly DllLibraryService _library;

    public DllRepositoryService(DllLibraryService library) => _library = library;

    /// <summary>
    /// How long a cached manifest is trusted before a fresh one is fetched. A day:
    /// builds appear every few weeks at most, and a user who opens the app twice in an
    /// afternoon should not pay for the index twice.
    /// </summary>
    public static readonly TimeSpan ManifestMaxAge = TimeSpan.FromHours(24);

    private static readonly SemaphoreSlim ManifestLock = new(1, 1);
    private static string? _manifestJson;

    private static string CacheDirectory => Path.Combine(AppDataPaths.Cache, "DllRepository");
    private static string CachePath => Path.Combine(CacheDirectory, "manifest.json");

    /// <summary>Where the cached index lives, for the Storage page.</summary>
    public static string Root => CacheDirectory;

    // ── The index ────────────────────────────────────────────────────────────

    /// <summary>
    /// The manifest text, from memory, then disk, then the network. Returns null when
    /// all three fail, which is not an error: this is an extra route, and every other
    /// source on the page still works without it.
    /// </summary>
    private static async Task<string?> ManifestAsync(CancellationToken cancel)
    {
        await ManifestLock.WaitAsync(cancel);
        try
        {
            if (_manifestJson is not null) return _manifestJson;

            var cached = ReadCache();
            if (cached is not null && !IsStale())
            {
                _manifestJson = cached;
                return _manifestJson;
            }

            try
            {
                Log.Write($"[Repository] Fetching the {DllRepository.SourceName} index.");
                var fetched = await NetworkService.GetHttpClient()
                    .GetStringAsync(DllRepository.ManifestUrl, cancel);

                // Parse before storing: an error page served with a 200 would
                // otherwise replace a good cache with something unusable.
                using (JsonDocument.Parse(fetched)) { }

                WriteCache(fetched);
                _manifestJson = fetched;
                return _manifestJson;
            }
            catch (Exception ex)
            {
                // A stale index is far better than none — it lists builds that were
                // real yesterday, and every download is hash-checked anyway.
                Log.Write($"[Repository] Could not fetch the index: {ex.Message}"
                    + (cached is not null ? " Using the cached copy." : string.Empty));
                _manifestJson = cached;
                return _manifestJson;
            }
        }
        finally
        {
            ManifestLock.Release();
        }
    }

    private static bool IsStale()
    {
        try { return DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) > ManifestMaxAge; }
        catch { return true; }
    }

    private static string? ReadCache()
    {
        try { return File.Exists(CachePath) ? File.ReadAllText(CachePath) : null; }
        catch { return null; }
    }

    private static void WriteCache(string json)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            File.WriteAllText(CachePath, json);
        }
        catch (Exception ex)
        {
            // Not fatal: the list is already in memory for this session.
            Log.Write($"[Repository] Could not cache the index: {ex.Message}");
        }
    }

    // ── Listing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// What the archive holds for this file, newest first. Empty when the archive does
    /// not cover it, or when there is no index to read.
    /// </summary>
    public async Task<IReadOnlyList<RepositoryBuild>> AvailableAsync(
        string fileName, CancellationToken cancel = default)
    {
        if (DllRepository.For(fileName) is not { } file) return Array.Empty<RepositoryBuild>();
        if (await ManifestAsync(cancel) is not { } json) return Array.Empty<RepositoryBuild>();

        return ParseBuilds(json, file)
            .Select(b => b with { InLibrary = _library.Has(b.FileName, b.Version) })
            .OrderBy(b => b.Version, VersionOrder.Descending)
            .ToList();
    }

    /// <summary>
    /// Builds out of a manifest, for one file. Separated from the fetch so the part
    /// that breaks when the manifest's shape changes can be tested without a network.
    /// </summary>
    internal static IReadOnlyList<RepositoryBuild> ParseBuilds(
        string json, DllRepository.RepositoryFile file)
    {
        var builds = new List<RepositoryBuild>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return builds;
            if (!document.RootElement.TryGetProperty(file.ManifestKey, out var list)) return builds;
            if (list.ValueKind != JsonValueKind.Array) return builds;

            foreach (var element in list.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;

                var version = Text(element, "version");
                var url = Text(element, "download_url");
                var zipMd5 = Text(element, "zip_md5_hash");

                // A row without these three cannot be offered: there would be nothing
                // to label it with, nowhere to fetch it from, or no way to tell a
                // truncated transfer from a good one.
                if (version.Length == 0 || url.Length == 0 || zipMd5.Length == 0) continue;

                builds.Add(new RepositoryBuild(
                    FileName: file.FileName,
                    Version: version,
                    Label: Label(element),
                    Provenance: Text(element, "dll_source"),
                    DownloadUrl: url,
                    ZipMd5: zipMd5,
                    DllMd5: Text(element, "md5_hash"),
                    FileSize: Number(element, "file_size"),
                    ZipFileSize: Number(element, "zip_file_size"),
                    IsDevFile: Flag(element, "is_dev_file"),
                    SignatureValid: Flag(element, "is_signature_valid"),
                    InLibrary: false));
            }
        }
        catch (JsonException)
        {
            // An error page or a truncated body. No builds is the right answer.
        }
        return builds;
    }

    /// <summary>
    /// The vendor's own name for a build. <c>additional_label</c> carries the FSR
    /// marketing version for the FidelityFX runtimes, which is the number a player
    /// recognises — the file version there is an SDK build number nobody quotes.
    /// <c>internal_name</c> is the fallback, and holds DLSS's branch labels.
    /// </summary>
    private static string Label(JsonElement element)
    {
        var additional = Text(element, "additional_label");
        return additional.Length > 0 ? additional : Text(element, "internal_name");
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : 0;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    // ── Downloading ──────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches one archived build into the library and returns it.
    ///
    /// Three gates, in this order: the archive's bytes against the manifest's zip
    /// hash, the extracted DLL against the manifest's file hash, and then the same
    /// 64-bit-PE-with-a-version check a hand-imported file gets. A download is not
    /// more trustworthy than a local file, so it does not get to skip the last one.
    /// </summary>
    public async Task<LibraryDll> DownloadAsync(
        RepositoryBuild build, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var staging = Path.Combine(Path.GetTempPath(), "um-repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        var archivePath = Path.Combine(staging, "build.zip");
        var dllPath = Path.Combine(staging, build.FileName);

        try
        {
            Log.Write($"[Repository] Downloading {build.FileName} {build.Version} " +
                      $"from {DllRepository.SourceName}.");
            await LargeFileDownload.ToFileAsync(build.DownloadUrl, archivePath, progress, cancel);

            var archiveHash = Md5Of(archivePath);
            if (!HashesMatch(archiveHash, build.ZipMd5))
                throw new InvalidDataException(
                    $"The downloaded archive does not match what the index says it should be " +
                    $"({archiveHash} rather than {build.ZipMd5.ToUpperInvariant()}). Nothing was added.");

            ExtractDll(archivePath, build.FileName, dllPath);

            var dllHash = Md5Of(dllPath);
            if (build.DllMd5.Length > 0 && !HashesMatch(dllHash, build.DllMd5))
                throw new InvalidDataException(
                    $"{build.FileName} inside the archive does not match what the index says " +
                    $"it should be ({dllHash} rather than {build.DllMd5.ToUpperInvariant()}). " +
                    "Nothing was added.");

            var entry = _library.Import(dllPath);
            Log.Write($"[Repository] Added {entry.FileName} {entry.Version} " +
                      $"from {DllRepository.SourceName}.");
            return entry with { SourceLabel = SourceLabelFor(build) };
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Provenance for the library row. The archive is a mirror, so both halves are
    /// worth recording: who hosts it, and where they say the file originally came from.
    /// </summary>
    internal static string SourceLabelFor(RepositoryBuild build) =>
        build.Provenance.Length > 0
            ? $"from the {DllRepository.SourceName} archive ({build.Provenance})"
            : $"from the {DllRepository.SourceName} archive";

    /// <summary>
    /// Pulls one named file out of the archive.
    ///
    /// Matched on the entry's <em>base name</em>, case-insensitively, because the
    /// layout inside these archives is not guaranteed — and the destination is a path
    /// this method chooses, never one built from the entry's own name, so an entry
    /// called <c>../../x</c> cannot write outside the staging directory.
    /// </summary>
    private static void ExtractDll(string archivePath, string fileName, string destination)
    {
        using var archive = ZipFile.OpenRead(archivePath);

        var entry = archive.Entries.FirstOrDefault(e =>
            string.Equals(Path.GetFileName(e.FullName), fileName, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            var contents = archive.Entries
                .Select(e => Path.GetFileName(e.FullName))
                .Where(n => n.Length > 0)
                .Take(6)
                .ToList();
            throw new InvalidDataException(
                $"The archive does not contain {fileName}" +
                (contents.Count > 0 ? $" — it holds {string.Join(", ", contents)}." : ".") +
                " Nothing was added.");
        }

        entry.ExtractToFile(destination, overwrite: true);
    }

    /// <summary>
    /// MD5, which is what the manifest records. Used only to confirm a transfer
    /// arrived intact and matches the index — not as a security boundary, for which it
    /// would be the wrong choice.
    /// </summary>
    private static string Md5Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(MD5.HashData(stream));
    }

    private static bool HashesMatch(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>A size for the row, so a 7 MB row reads differently from an 80 MB one.</summary>
    public static string DescribeSize(long bytes) => bytes switch
    {
        <= 0 => string.Empty,
        < 1024L * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };

    /// <summary>
    /// Drops the cached index, so the next listing fetches a fresh one. For the
    /// Storage page's delete, and for a user who wants to see a build that landed in
    /// the archive an hour ago.
    /// </summary>
    public static void ForgetCachedIndex()
    {
        ManifestLock.Wait();
        try
        {
            _manifestJson = null;
            try { if (File.Exists(CachePath)) File.Delete(CachePath); } catch { }
        }
        finally
        {
            ManifestLock.Release();
        }
    }
}
