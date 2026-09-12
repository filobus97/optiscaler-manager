// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UpscalerManager.Core.Components;
using UpscalerManager.Core.Logging;
using UpscalerManager.Core.Models;

namespace UpscalerManager.Core.Services;

/// <summary>Where a library entry came from, which is also how much it can be trusted.</summary>
public enum DllOrigin
{
    /// <summary>Taken out of one of the user's own installed games.</summary>
    Harvested,
    /// <summary>A file the user pointed at themselves.</summary>
    Imported,
}

/// <summary>One build of one swappable DLL, held in the library and ready to install.</summary>
/// <param name="FileName">The DLL's name, which is also what it installs as.</param>
/// <param name="Version">Its file version, read from the binary.</param>
/// <param name="Path">Where the copy lives in the library.</param>
/// <param name="Origin">Harvested from a game, or imported by hand.</param>
/// <param name="SourceLabel">Human-readable provenance, e.g. the game it came from.</param>
/// <param name="Sha256">Hash of the library copy, recorded when a swap installs it.</param>
public sealed record LibraryDll(
    string FileName, string Version, string Path, DllOrigin Origin, string SourceLabel, string Sha256)
{
    public SwappableDlls.SwappableDll? Definition => SwappableDlls.For(FileName);
}

/// <summary>
/// One build of a swappable DLL that is sitting in a game but not yet in the library.
/// Offering these is the point of the harvest-first design: the versions a user can
/// install are the ones they already own, so the library costs no downloads, works
/// offline, and cannot be taken away by a third party withdrawing a file.
/// </summary>
/// <param name="InLibrary">
/// True when this exact build is already held, so the row can say so rather than
/// offering a pointless second copy.
/// </param>
public sealed record HarvestableDll(
    string FileName, string Version, string Path, string GameName, bool InLibrary);

/// <summary>
/// The collection of swappable DLL builds this app can install into a game.
///
/// Two ways in, deliberately, and no third:
///
/// <list type="bullet">
/// <item>harvested from the user's own installed games, and</item>
/// <item>imported from a file the user supplies.</item>
/// </list>
///
/// Nothing is downloaded. DLSS Swapper solves the same problem by self-hosting some
/// 229 archives on one volunteer's CDN, and its own metadata shows those were scraped
/// out of shipped games — while the Nvidia licence it redistributes verbatim says the
/// SDK "may not be distributed or sublicensed as a stand-alone product". Harvesting
/// sidesteps both the legal question and the single point of failure: a user's own
/// game files are theirs, are already on the disk, and do not stop existing when
/// somebody's hosting bill goes unpaid.
/// </summary>
public sealed class DllLibraryService
{
    private readonly string _root;

    public DllLibraryService()
    {
        _root = Path.Combine(AppDataPaths.Cache, "SwapLibrary");
        Directory.CreateDirectory(_root);
    }

    /// <summary>The library's directory, for the Storage page.</summary>
    public string Root => _root;

    private string DirFor(string fileName, string version) =>
        Path.Combine(_root, fileName.ToLowerInvariant(), SafeVersion(version));

    /// <summary>
    /// A version turned into a directory name. Versions are dotted numbers in practice,
    /// but a malformed resource can carry anything, and this becomes a path.
    /// </summary>
    internal static string SafeVersion(string version)
    {
        var cleaned = new string(version
            .Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_')
            .ToArray())
            .Trim('.', '_', '-');
        return cleaned.Length == 0 ? "unknown" : cleaned;
    }

    // ── Reading the library ──────────────────────────────────────────────────

    /// <summary>
    /// Every build held, newest first within each DLL. Reads the disk rather than an
    /// index: the library is a directory of files, so there is no index to fall out of
    /// step with what is actually there.
    /// </summary>
    public IReadOnlyList<LibraryDll> All()
    {
        var found = new List<LibraryDll>();
        if (!Directory.Exists(_root)) return found;

        foreach (var dllDir in SafeDirectories(_root))
        {
            var fileName = SwappableDlls.For(Path.GetFileName(dllDir))?.FileName;
            if (fileName is null) continue;   // not something we swap (or a stray folder)

            foreach (var versionDir in SafeDirectories(dllDir))
            {
                var path = Path.Combine(versionDir, fileName);
                if (!File.Exists(path)) continue;

                var meta = ReadMeta(versionDir);
                found.Add(new LibraryDll(
                    fileName,
                    meta?.Version ?? Path.GetFileName(versionDir),
                    path,
                    OriginOf(meta),
                    meta?.SourceLabel ?? "unknown origin",
                    meta?.Sha256 ?? string.Empty));
            }
        }

        return found
            .OrderBy(e => SwappableDlls.DisplayOrder(e.FileName))
            .ThenBy(e => e.Version, VersionOrder.Descending)
            .ToList();
    }

    /// <summary>Every build held of one DLL, newest first.</summary>
    public IReadOnlyList<LibraryDll> For(string fileName) =>
        All().Where(e => e.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase)).ToList();

    public bool Has(string fileName, string version) =>
        File.Exists(Path.Combine(DirFor(fileName, version), fileName));

    // ── Filling it ───────────────────────────────────────────────────────────

    /// <summary>
    /// Every swappable DLL sitting in the given games that the library does not already
    /// hold a copy of, newest first. Built from the components the scan already
    /// detected, so this costs no extra disk reads.
    /// </summary>
    public IReadOnlyList<HarvestableDll> Harvestable(IEnumerable<Game> games, string? onlyFileName = null)
    {
        var found = new List<HarvestableDll>();

        foreach (var game in games)
        {
            foreach (var component in game.DetectedComponents)
            {
                if (!SwappableDlls.IsSwappable(component.FileName)) continue;
                if (onlyFileName is not null &&
                    !component.FileName.Equals(onlyFileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // A version is what makes a build worth offering: "some DLSS" is not a
                // choice a user can reason about.
                if (string.IsNullOrWhiteSpace(component.Version)) continue;
                var path = ResolveComponentPath(game, component);
                if (path is null) continue;

                // A file this app installed is not the game's own build, and offering it
                // back as a harvestable version would launder our own copy into
                // something that looks like provenance from that game.
                if (component.Source == ComponentSource.Manager) continue;

                found.Add(new HarvestableDll(
                    SwappableDlls.For(component.FileName)!.FileName,
                    component.Version!,
                    path,
                    game.Name,
                    Has(component.FileName, component.Version!)));
            }
        }

        return DeduplicateByBuild(found);
    }

    /// <summary>
    /// One row per build: the same DLSS version shipping in six games is one choice, not
    /// six. Keeps whichever source is named first alphabetically, so the list is stable
    /// between scans rather than reordering with directory enumeration.
    /// </summary>
    private static IReadOnlyList<HarvestableDll> DeduplicateByBuild(List<HarvestableDll> found) =>
        found
            .GroupBy(e => (e.FileName.ToLowerInvariant(), e.Version), StringTupleComparer.Instance)
            .Select(g => g.OrderBy(e => e.GameName, StringComparer.OrdinalIgnoreCase).First())
            .OrderBy(e => SwappableDlls.DisplayOrder(e.FileName))
            .ThenBy(e => e.Version, VersionOrder.Descending)
            .ToList();

    /// <summary>
    /// Swappable DLLs inside the OptiScaler releases this app has already downloaded.
    ///
    /// An OptiScaler release bundles the XeSS and FidelityFX libraries it hooks, so a
    /// user who has installed OptiScaler once already has those builds on disk. Reading
    /// them costs nothing, works offline, and is the only source for AMD's FidelityFX
    /// runtimes, which AMD does not publish as loose binaries.
    ///
    /// Scanned recursively: release layouts have changed between versions, and pinning
    /// this to a fixed subdirectory would quietly stop finding anything the next time
    /// upstream moves a file.
    /// </summary>
    public IReadOnlyList<HarvestableDll> FromOptiScalerReleases(string? onlyFileName = null) =>
        FromCachedReleases("OptiScaler", "OptiScaler", onlyFileName);

    /// <summary>
    /// Swappable DLLs in the FSR 4 INT8 community builds this app has already
    /// downloaded for the OptiScaler route.
    ///
    /// These are the only route to FSR 4 on hardware whose driver does not provide it,
    /// and they ship as one of the two FSR upscaler filenames — so once downloaded they
    /// are a swap source like any other, with no second download.
    /// </summary>
    public IReadOnlyList<HarvestableDll> FromCommunityBuilds(string? onlyFileName = null) =>
        FromCachedReleases("Extras", "community build", onlyFileName);

    /// <summary>
    /// Swappable DLLs inside one of the cache's release folders. Shared by the
    /// OptiScaler and community-build sources, which differ only in which folder they
    /// read and what the row calls them.
    /// </summary>
    private IReadOnlyList<HarvestableDll> FromCachedReleases(
        string cacheFolder, string label, string? onlyFileName)
    {
        var root = Path.Combine(AppDataPaths.Cache, cacheFolder);
        if (!Directory.Exists(root)) return Array.Empty<HarvestableDll>();

        var found = new List<HarvestableDll>();
        foreach (var releaseDir in SafeDirectories(root))
        {
            var release = $"{label} {Path.GetFileName(releaseDir)}";
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(releaseDir, "*.dll", SearchOption.AllDirectories); }
            catch { continue; }

            foreach (var path in files)
            {
                var name = Path.GetFileName(path);
                if (SwappableDlls.For(name) is not { } definition) continue;
                if (onlyFileName is not null &&
                    !definition.FileName.Equals(onlyFileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var version = VersionOf(PeFileInspector.Inspect(path));
                if (version is null) continue;

                found.Add(new HarvestableDll(
                    definition.FileName, version, path,
                    release, Has(definition.FileName, version)));
            }
        }

        return DeduplicateByBuild(found);
    }

    /// <summary>
    /// Copies a DLL out of a game into the library. Returns the held entry, which may
    /// be one that was already there.
    /// </summary>
    public LibraryDll Harvest(HarvestableDll source) =>
        Add(source.Path, DllOrigin.Harvested, $"from {source.GameName}", source.FileName);

    /// <summary>
    /// Takes a file the user chose into the library. The name has to be one we swap —
    /// an arbitrary DLL renamed to <c>nvngx_dlss.dll</c> would install happily and then
    /// fail inside the game, where the cause is far less obvious.
    /// </summary>
    public LibraryDll Import(string path) => Add(path, DllOrigin.Imported, "imported", expectedName: null);

    private LibraryDll Add(string sourcePath, DllOrigin origin, string sourceLabel, string? expectedName)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("That file no longer exists.", sourcePath);

        var name = Path.GetFileName(sourcePath);
        if (SwappableDlls.For(name) is not { } definition)
            throw new InvalidOperationException(
                $"'{name}' is not a DLL this app knows how to swap. It has to be one of: " +
                string.Join(", ", SwappableDlls.All.Select(d => d.FileName)) + ".");

        if (expectedName is not null && !definition.FileName.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Expected {expectedName} but the file is named {name}.");

        var pe = PeFileInspector.Inspect(sourcePath);
        if (!pe.IsValidPe)
            throw new InvalidOperationException($"'{name}' is not a Windows DLL.");
        if (!pe.Is64Bit)
            throw new InvalidOperationException($"'{name}' is 32-bit. Games that use these libraries are 64-bit.");

        var version = VersionOf(pe);
        if (version is null)
            throw new InvalidOperationException(
                $"'{name}' carries no version. Without one there is no way to tell it apart from " +
                "the build already in the game, so it cannot be offered as a choice.");

        var dir = DirFor(definition.FileName, version);
        var dest = Path.Combine(dir, definition.FileName);
        var hash = FileHash.Sha256(sourcePath) ?? string.Empty;

        if (File.Exists(dest) && hash.Length > 0 && FileHash.Sha256(dest) == hash)
        {
            // Byte-identical to what is already held. Say so rather than rewriting it:
            // the existing entry keeps whichever provenance it was first recorded with.
            Log.Write($"[Library] {definition.FileName} {version} is already held.");
            return ReadEntry(dir, definition.FileName) ??
                   new LibraryDll(definition.FileName, version, dest, origin, sourceLabel, hash);
        }

        Directory.CreateDirectory(dir);
        File.Copy(sourcePath, dest, overwrite: true);
        var entry = new LibraryDll(definition.FileName, version, dest, origin, sourceLabel, hash);
        WriteMeta(dir, entry);
        Log.Write($"[Library] Added {definition.FileName} {version} ({sourceLabel}).");
        return entry;
    }

    /// <summary>
    /// Removes one held build. Refuses while a game is still using it: the library copy
    /// is what a re-swap or a version comparison reads, and losing it silently would
    /// leave the game's page unable to say what is installed.
    /// </summary>
    public void Delete(LibraryDll entry)
    {
        var dir = Path.GetDirectoryName(entry.Path);
        if (dir is null || !Directory.Exists(dir)) return;
        Directory.Delete(dir, recursive: true);
        Log.Write($"[Library] Removed {entry.FileName} {entry.Version}.");

        // Leave no empty per-DLL folder behind, so the Storage page's counts match what
        // a user would count by eye.
        var parent = Path.GetDirectoryName(dir);
        if (parent is not null && Directory.Exists(parent) && !SafeDirectories(parent).Any())
            try { Directory.Delete(parent); } catch { /* harmless if it races */ }
    }

    // ── Version reading ──────────────────────────────────────────────────────

    /// <summary>
    /// The build's version, preferring the file version and falling back to the product
    /// version. All-zero versions are treated as absent — a DLL with no version resource
    /// reports 0.0.0.0 rather than nothing, and "DLSS 0.0.0.0" is not a choice.
    /// </summary>
    internal static string? VersionOf(PeFileInfo pe)
    {
        foreach (var candidate in new[] { pe.FileVersion, pe.ProductVersion })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var trimmed = candidate.Trim();
            if (trimmed.Split('.', ',').All(part => part.Trim() is "0" or "")) continue;
            return trimmed.Replace(',', '.');
        }
        return null;
    }

    // ── Per-entry metadata ───────────────────────────────────────────────────
    //
    // A tiny file beside each held DLL, recording where it came from. The version is
    // already in the directory name; provenance is not derivable from anything on disk,
    // and "which of my games did this come from" is exactly what a user asks when a
    // swap turns out badly.

    private static string MetaPath(string versionDir) => Path.Combine(versionDir, "origin.json");

    private void WriteMeta(string versionDir, LibraryDll entry)
    {
        try
        {
            var meta = new LibraryEntryMeta
            {
                Version = entry.Version,
                Origin = entry.Origin.ToString(),
                SourceLabel = entry.SourceLabel,
                Sha256 = entry.Sha256,
                AddedUtc = DateTime.UtcNow.ToString("o"),
            };
            File.WriteAllText(MetaPath(versionDir),
                System.Text.Json.JsonSerializer.Serialize(meta, OptimizerContext.Default.LibraryEntryMeta));
        }
        catch (Exception ex)
        {
            // Provenance is a nicety; the DLL is the thing. Never fail an add over it.
            Log.Write($"[Library] Could not record where {entry.FileName} came from: {ex.Message}");
        }
    }

    private static LibraryEntryMeta? ReadMeta(string versionDir)
    {
        var path = MetaPath(versionDir);
        if (!File.Exists(path)) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize(
                File.ReadAllText(path), OptimizerContext.Default.LibraryEntryMeta);
        }
        catch { return null; }
    }

    private LibraryDll? ReadEntry(string versionDir, string fileName)
    {
        var path = Path.Combine(versionDir, fileName);
        if (!File.Exists(path)) return null;
        var meta = ReadMeta(versionDir);
        return new LibraryDll(
            fileName,
            meta?.Version ?? Path.GetFileName(versionDir),
            path,
            OriginOf(meta),
            meta?.SourceLabel ?? "unknown origin",
            meta?.Sha256 ?? string.Empty);
    }

    /// <summary>
    /// Where a detected component actually sits. <see cref="DetectedComponent.RelativePath"/>
    /// is relative to the game's root, but the scan also records absolute paths for
    /// files it found outside it, so both forms have to be handled — resolving one as
    /// the other is what broke component attribution in v0.17.0.
    /// </summary>
    internal static string? ResolveComponentPath(Game game, DetectedComponent component)
    {
        if (string.IsNullOrWhiteSpace(component.RelativePath)) return null;
        try
        {
            var path = Path.IsPathRooted(component.RelativePath)
                ? component.RelativePath
                : Path.Combine(game.InstallPath, component.RelativePath);
            return File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// The recorded origin, defaulting to Imported. Stored as text rather than a number
    /// so a file written by a future version, or edited by hand, degrades to the
    /// conservative answer instead of throwing while listing the library.
    /// </summary>
    private static DllOrigin OriginOf(LibraryEntryMeta? meta) =>
        Enum.TryParse<DllOrigin>(meta?.Origin, ignoreCase: true, out var parsed)
            ? parsed
            : DllOrigin.Imported;

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Groups by (name, version) without allocating a string key per entry.</summary>
    private sealed class StringTupleComparer : IEqualityComparer<(string, string)>
    {
        public static readonly StringTupleComparer Instance = new();

        public bool Equals((string, string) a, (string, string) b) =>
            StringComparer.OrdinalIgnoreCase.Equals(a.Item1, b.Item1)
            && StringComparer.OrdinalIgnoreCase.Equals(a.Item2, b.Item2);

        public int GetHashCode((string, string) v) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(v.Item1),
            StringComparer.OrdinalIgnoreCase.GetHashCode(v.Item2));
    }
}
