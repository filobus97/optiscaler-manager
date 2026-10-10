// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.

using System.Text;

namespace UpscalerManager.Core.Services;

/// <summary>The names a Windows DLL exports, read from its export directory.</summary>
public static class PeExports
{
    /// <summary>
    /// Every exported name, or an empty list when the file is not a PE, has no export
    /// directory, or cannot be read.
    /// </summary>
    public static IReadOnlyList<string> Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var r = new BinaryReader(fs);
            if (fs.Length < 0x40 || r.ReadUInt16() != 0x5A4D) return Array.Empty<string>();

            fs.Seek(0x3C, SeekOrigin.Begin);
            var pe = r.ReadUInt32();
            if (pe + 24 > fs.Length) return Array.Empty<string>();
            fs.Seek(pe, SeekOrigin.Begin);
            if (r.ReadUInt32() != 0x00004550) return Array.Empty<string>();

            r.ReadUInt16();                    // Machine
            var sectionCount = r.ReadUInt16();
            fs.Seek(12, SeekOrigin.Current);   // TimeDateStamp, symbol table, symbol count
            var optionalSize = r.ReadUInt16();
            r.ReadUInt16();                    // Characteristics

            var optional = fs.Position;
            var magic = r.ReadUInt16();
            // Data directory 0 is the export table; the directories start at 112 (PE32+) or 96 (PE32).
            var directories = optional + (magic == 0x20B ? 112 : 96);
            if (directories + 8 > optional + optionalSize) return Array.Empty<string>();
            fs.Seek(directories, SeekOrigin.Begin);
            var exportRva = r.ReadUInt32();
            if (exportRva == 0) return Array.Empty<string>();

            var sections = new List<(uint Va, uint Size, uint Raw)>();
            fs.Seek(optional + optionalSize, SeekOrigin.Begin);
            for (var i = 0; i < sectionCount; i++)
            {
                fs.Seek(8, SeekOrigin.Current);     // Name
                var virtualSize = r.ReadUInt32();
                var va = r.ReadUInt32();
                var rawSize = r.ReadUInt32();
                var raw = r.ReadUInt32();
                fs.Seek(16, SeekOrigin.Current);    // relocations, line numbers, characteristics
                sections.Add((va, Math.Max(virtualSize, rawSize), raw));
            }

            long Offset(uint rva)
            {
                foreach (var s in sections)
                    if (rva >= s.Va && rva < s.Va + s.Size) return s.Raw + (rva - s.Va);
                return -1;
            }

            var dir = Offset(exportRva);
            if (dir < 0 || dir + 40 > fs.Length) return Array.Empty<string>();
            fs.Seek(dir + 24, SeekOrigin.Begin);
            var nameCount = r.ReadUInt32();
            r.ReadUInt32();                    // AddressOfFunctions
            var namesRva = r.ReadUInt32();
            var names = Offset(namesRva);
            if (names < 0 || nameCount > 65536) return Array.Empty<string>();

            var result = new List<string>((int)nameCount);
            for (var i = 0; i < nameCount; i++)
            {
                fs.Seek(names + 4L * i, SeekOrigin.Begin);
                var at = Offset(r.ReadUInt32());
                if (at < 0) continue;
                fs.Seek(at, SeekOrigin.Begin);
                var bytes = new List<byte>();
                for (int b; bytes.Count < 512 && (b = fs.ReadByte()) > 0;) bytes.Add((byte)b);
                result.Add(Encoding.ASCII.GetString(bytes.ToArray()));
            }
            return result;
        }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }
}
