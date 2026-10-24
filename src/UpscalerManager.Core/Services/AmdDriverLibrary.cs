// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.

using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using UpscalerManager.Core.Components;
using UpscalerManager.Core.Logging;

namespace UpscalerManager.Core.Services;

/// <summary>One imported AMD driver file.</summary>
/// <param name="Api">Which API it serves.</param>
/// <param name="Version">Its file version, which is also its folder in the library.</param>
/// <param name="Path">Where the library keeps it.</param>
/// <param name="Signed">Whether it carries a signature. AMD signs its own; a copy without one has been altered.</param>
public sealed record AmdDriverFile(AmdDriverApi Api, string Version, string Path, bool Signed)
{
    public string FileName => AmdDriverFiles.NameFor(Api);
}

/// <summary>What an import found: the files it kept, and why it passed over others.</summary>
public sealed record AmdDriverImport(IReadOnlyList<AmdDriverFile> Added, IReadOnlyList<string> Notes);

/// <summary>
/// The player's AMD driver files (<see cref="AmdDriverFiles"/>), one folder per version.
/// They come from a driver the player already has: the files themselves, a folder they sit
/// in (an unpacked driver), an archive, or AMD's installer.
/// </summary>
public sealed class AmdDriverLibrary
{
    private readonly string _root;

    public AmdDriverLibrary(string? root = null) =>
        _root = root ?? System.IO.Path.Combine(AppDataPaths.Cache, "AmdDriverFiles");

    public string Root => _root;

    /// <summary>Everything imported, newest version first, DirectX 12 before Vulkan.</summary>
    public IReadOnlyList<AmdDriverFile> List()
    {
        if (!Directory.Exists(_root)) return Array.Empty<AmdDriverFile>();

        var files = new List<AmdDriverFile>();
        foreach (var dir in Directory.EnumerateDirectories(_root))
            foreach (var name in AmdDriverFiles.Names)
            {
                var path = System.IO.Path.Combine(dir, name);
                if (!File.Exists(path)) continue;
                var info = PeFileInspector.Inspect(path);
                files.Add(new AmdDriverFile(AmdDriverFiles.ApiOf(name)!.Value,
                    System.IO.Path.GetFileName(dir), path, info.HasAuthenticodeSignature));
            }

        return files
            .OrderByDescending(f => ParseVersion(f.Version))
            .ThenBy(f => f.Api)
            .ToList();
    }

    /// <summary>The newest imported file for <paramref name="api"/>, or null.</summary>
    public AmdDriverFile? Newest(AmdDriverApi api) => List().FirstOrDefault(f => f.Api == api);

    /// <summary>Removes one imported file, and its version folder once that is empty.</summary>
    public void Delete(AmdDriverFile file)
    {
        if (File.Exists(file.Path)) File.Delete(file.Path);
        var dir = System.IO.Path.GetDirectoryName(file.Path)!;
        if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            Directory.Delete(dir);
    }

    /// <summary>
    /// Imports every driver file found in <paramref name="sources"/>: files, folders searched
    /// all the way down, archives (.zip, .7z, .rar) and AMD's installer, which is a 7-Zip
    /// archive behind a small program.
    /// </summary>
    public Task<AmdDriverImport> ImportAsync(IEnumerable<string> sources, IProgress<string>? status = null) =>
        Task.Run(() =>
        {
            var added = new List<AmdDriverFile>();
            var notes = new List<string>();
            var staging = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "um_amd_" + Guid.NewGuid().ToString("N"));
            try
            {
                foreach (var source in sources)
                {
                    if (Directory.Exists(source))
                    {
                        var found = Directory.EnumerateFiles(source, "*.dll", SearchOption.AllDirectories)
                            .Where(f => AmdDriverFiles.ApiOf(System.IO.Path.GetFileName(f)) is not null)
                            .ToList();
                        if (found.Count == 0)
                            notes.Add($"{System.IO.Path.GetFileName(source.TrimEnd('/', '\\'))} holds no {string.Join(" or ", AmdDriverFiles.Names)}.");
                        foreach (var file in found) Consider(file, added, notes);
                    }
                    else if (AmdDriverFiles.ApiOf(System.IO.Path.GetFileName(source)) is not null)
                    {
                        Consider(source, added, notes);
                    }
                    else if (File.Exists(source))
                    {
                        status?.Report($"Looking through {System.IO.Path.GetFileName(source)}…");
                        var dir = System.IO.Path.Combine(staging, added.Count + "_" + notes.Count + "_" + System.IO.Path.GetFileNameWithoutExtension(source));
                        var extracted = ExtractDriverFiles(source, dir);
                        if (extracted.Count == 0)
                            notes.Add($"{System.IO.Path.GetFileName(source)} holds no {string.Join(" or ", AmdDriverFiles.Names)}.");
                        foreach (var file in extracted) Consider(file, added, notes);
                    }
                    else
                    {
                        notes.Add($"{source} does not exist.");
                    }
                }
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            }
            return new AmdDriverImport(added, notes);
        });

    /// <summary>
    /// Keeps a file when it is what it is named: a 64-bit DLL that hands out FSR 4 through
    /// <see cref="AmdDriverFiles.ProviderExport"/>. A driver package can hold the same file
    /// several times (one copy per hardware family); one copy per version is kept.
    /// </summary>
    private void Consider(string path, List<AmdDriverFile> added, List<string> notes)
    {
        var name = System.IO.Path.GetFileName(path);
        var api = AmdDriverFiles.ApiOf(name)!.Value;
        var info = PeFileInspector.Inspect(path);
        if (!info.IsValidPe || !info.Is64Bit)
        {
            notes.Add($"{name}: not a 64-bit Windows DLL.");
            return;
        }
        if (!PeExports.Read(path).Contains(AmdDriverFiles.ProviderExport))
        {
            notes.Add($"{name}: not AMD's driver file (it has no {AmdDriverFiles.ProviderExport}). "
                      + "A community build under this name goes in Custom DLLs instead.");
            return;
        }

        var version = info.FileVersion ?? "unversioned";
        var dest = System.IO.Path.Combine(_root, version, name);
        if (added.Any(a => a.Api == api && a.Version == version)) return;
        if (File.Exists(dest))
        {
            notes.Add($"{name} {version} was already imported.");
            return;
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
        File.Copy(path, dest, overwrite: false);
        added.Add(new AmdDriverFile(api, version, dest, info.HasAuthenticodeSignature));
        Log.Write($"[AmdDriver] Imported {name} {version}{(info.HasAuthenticodeSignature ? "" : " (unsigned)")} from {path}");
    }

    /// <summary>
    /// Writes the driver files inside an archive or AMD's installer under <paramref name="dir"/>,
    /// and returns their paths.
    /// </summary>
    internal static IReadOnlyList<string> ExtractDriverFiles(string archivePath, string dir)
    {
        using var file = File.OpenRead(archivePath);

        // A plain archive opens as one; AMD's installer is a program with a 7-Zip archive
        // after it, so the archive is found by its signature and read from there.
        IArchive? archive = null;
        try { archive = ArchiveFactory.Open(file); }
        catch (Exception) { archive = null; }

        if (archive is null)
        {
            file.Position = 0;
            var offset = FindSevenZip(file);
            if (offset < 0) return Array.Empty<string>();
            archive = SevenZipArchive.Open(new OffsetStream(file, offset));
        }

        using (archive)
        {
            var written = new List<string>();
            var counter = 0;

            void Write(string? key, Action<Stream> copy)
            {
                var name = ComponentManagementService.ArchiveEntryName(key);
                if (AmdDriverFiles.ApiOf(name) is null) return;

                // Every copy gets its own folder: a package holds the file once per hardware
                // family, and the copies can differ.
                var dest = System.IO.Path.Combine(dir, (counter++).ToString(), name);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
                using (var target = File.Create(dest))
                    copy(target);
                written.Add(dest);
            }

            // A 7-Zip installer is usually one solid block: reading its entries in order costs
            // one pass, where opening each on its own would unpack everything before it again.
            if (archive.Type == SharpCompress.Common.ArchiveType.SevenZip || archive.IsSolid)
            {
                using var reader = archive.ExtractAllEntries();
                while (reader.MoveToNextEntry())
                    if (!reader.Entry.IsDirectory)
                        Write(reader.Entry.Key, reader.WriteEntryTo);
            }
            else
            {
                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                    Write(entry.Key, target =>
                    {
                        using var source = entry.OpenEntryStream();
                        source.CopyTo(target);
                    });
            }
            return written;
        }
    }

    private static readonly byte[] SevenZipSignature = { (byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C };

    /// <summary>
    /// Where a 7-Zip archive starts inside <paramref name="stream"/>, or -1. The signature
    /// is followed by format version 0.x, which rules out most chance matches in the program
    /// in front of it.
    /// </summary>
    internal static long FindSevenZip(Stream stream)
    {
        const int chunk = 1 << 20;
        var buffer = new byte[chunk + SevenZipSignature.Length + 2];
        long basePosition = 0;
        var carried = 0;
        while (true)
        {
            var read = stream.Read(buffer, carried, chunk);
            if (read <= 0) return -1;
            var available = carried + read;
            for (var i = 0; i + SevenZipSignature.Length + 2 <= available; i++)
            {
                if (buffer[i] != '7' || !buffer.AsSpan(i, SevenZipSignature.Length).SequenceEqual(SevenZipSignature))
                    continue;
                if (buffer[i + SevenZipSignature.Length] == 0)
                    return basePosition + i;
            }
            var keep = Math.Min(SevenZipSignature.Length + 1, available);
            Array.Copy(buffer, available - keep, buffer, 0, keep);
            basePosition += available - keep;
            carried = keep;
        }
    }

    private static Version ParseVersion(string text) =>
        Version.TryParse(text, out var v) ? v : new Version(0, 0);

    /// <summary>A read-only view of a stream from an offset on, so an archive inside a file reads as a file.</summary>
    private sealed class OffsetStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _offset;

        public OffsetStream(Stream inner, long offset)
        {
            _inner = inner;
            _offset = offset;
            _inner.Position = offset;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length - _offset;

        public override long Position
        {
            get => _inner.Position - _offset;
            set => _inner.Position = value + _offset;
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => origin switch
        {
            SeekOrigin.Begin => _inner.Seek(offset + _offset, SeekOrigin.Begin) - _offset,
            SeekOrigin.Current => _inner.Seek(offset, SeekOrigin.Current) - _offset,
            _ => _inner.Seek(offset, SeekOrigin.End) - _offset,
        };

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
