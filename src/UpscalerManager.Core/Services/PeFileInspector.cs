// OptiScaler Client - A frontend for managing OptiScaler installations
// Copyright (C) 2026 Agustín Montaña (Agustinm28)
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.IO;

namespace UpscalerManager.Core.Services
{
    /// <summary>
    /// Result of inspecting a PE (Portable Executable) file.
    /// </summary>
    public class PeFileInfo
    {
        /// <summary>True when the file has valid MZ + PE signatures.</summary>
        public bool IsValidPe { get; set; }
        /// <summary>True when the PE machine type is x64 (AMD64).</summary>
        public bool Is64Bit { get; set; }
        /// <summary>FileVersion from VS_FIXEDFILEINFO ("a.b.c.d"), or null when no version resource was found.</summary>
        public string? FileVersion { get; set; }
        /// <summary>ProductVersion from VS_FIXEDFILEINFO ("a.b.c.d"), or null when no version resource was found.</summary>
        public string? ProductVersion { get; set; }
        /// <summary>
        /// True when the PE security data directory (Authenticode certificate table) is present
        /// and non-empty. Presence only — the signature is NOT cryptographically validated.
        /// </summary>
        public bool HasAuthenticodeSignature { get; set; }
    }

    /// <summary>The name strings of a version resource.</summary>
    /// <param name="OriginalFilename">The name the DLL was built as, which a rename does not change.</param>
    /// <param name="FileDescription">What the DLL says it is.</param>
    public sealed record PeVersionStrings(string? OriginalFilename, string? FileDescription)
    {
        public static readonly PeVersionStrings None = new(null, null);
    }

    /// <summary>
    /// Minimal, cross-platform PE header / version-resource reader.
    /// Works on Linux and macOS too (unlike FileVersionInfo, which only reads
    /// native PE version resources on Windows), so validation behaves the same
    /// on every OS this app is published for.
    /// </summary>
    public static class PeFileInspector
    {
        private const ushort MzSignature = 0x5A4D;          // "MZ"
        private const uint PeSignature = 0x00004550;        // "PE\0\0"
        private const ushort MachineAmd64 = 0x8664;
        private const ushort MachineArm64 = 0xAA64;
        private const uint VsFixedFileInfoSignature = 0xFEEF04BD;

        public static PeFileInfo Inspect(string filePath)
        {
            var info = new PeFileInfo();

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(fs);

            if (fs.Length < 0x40)
                return info;

            if (reader.ReadUInt16() != MzSignature)
                return info;

            fs.Seek(0x3C, SeekOrigin.Begin);
            uint peHeaderOffset = reader.ReadUInt32();
            if (peHeaderOffset + 24 > fs.Length)
                return info;

            fs.Seek(peHeaderOffset, SeekOrigin.Begin);
            if (reader.ReadUInt32() != PeSignature)
                return info;

            // COFF file header
            ushort machine = reader.ReadUInt16();
            reader.ReadUInt16();               // NumberOfSections
            reader.ReadUInt32();               // TimeDateStamp
            reader.ReadUInt32();               // PointerToSymbolTable
            reader.ReadUInt32();               // NumberOfSymbols
            ushort optionalHeaderSize = reader.ReadUInt16();
            reader.ReadUInt16();               // Characteristics

            info.IsValidPe = true;
            info.Is64Bit = machine == MachineAmd64 || machine == MachineArm64;

            // Optional header: magic 0x20B = PE32+, 0x10B = PE32
            long optionalHeaderStart = fs.Position;
            if (optionalHeaderSize >= 2)
            {
                ushort magic = reader.ReadUInt16();
                bool isPe32Plus = magic == 0x20B;

                // Security directory (index 4) holds the Authenticode certificate table.
                // Data directories start at offset 112 (PE32+) or 96 (PE32) into the optional header.
                int dataDirectoryOffset = isPe32Plus ? 112 : 96;
                long securityDirPos = optionalHeaderStart + dataDirectoryOffset + 4 * 8;
                if (securityDirPos + 8 <= optionalHeaderStart + optionalHeaderSize && securityDirPos + 8 <= fs.Length)
                {
                    fs.Seek(securityDirPos, SeekOrigin.Begin);
                    uint certTableRva = reader.ReadUInt32();
                    uint certTableSize = reader.ReadUInt32();
                    info.HasAuthenticodeSignature = certTableRva != 0 && certTableSize != 0;
                }
            }

            // Version resource: rather than walking the .rsrc tree, scan for the
            // VS_FIXEDFILEINFO signature. The structure is DWORD-aligned inside the
            // resource section, and the signature is unique enough in practice.
            (info.FileVersion, info.ProductVersion) = ScanForFixedFileInfo(fs);

            return info;
        }

        /// <summary>
        /// The version resource's name strings, read from the resource section alone so it
        /// stays cheap enough to run over every DLL in a game folder. Both are null when
        /// the file is not a PE or carries no such string.
        /// </summary>
        public static PeVersionStrings ReadVersionStrings(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new BinaryReader(fs);
                if (fs.Length < 0x40 || reader.ReadUInt16() != MzSignature) return PeVersionStrings.None;

                fs.Seek(0x3C, SeekOrigin.Begin);
                uint pe = reader.ReadUInt32();
                if (pe + 24 > fs.Length) return PeVersionStrings.None;
                fs.Seek(pe, SeekOrigin.Begin);
                if (reader.ReadUInt32() != PeSignature) return PeVersionStrings.None;

                reader.ReadUInt16();                          // Machine
                ushort sectionCount = reader.ReadUInt16();
                fs.Seek(12, SeekOrigin.Current);              // TimeDateStamp, symbol table, symbol count
                ushort optionalSize = reader.ReadUInt16();
                reader.ReadUInt16();                          // Characteristics

                long optional = fs.Position;
                ushort magic = reader.ReadUInt16();
                // Data directory 2 is the resource table.
                long directory = optional + (magic == 0x20B ? 112 : 96) + 2 * 8;
                if (directory + 8 > optional + optionalSize) return PeVersionStrings.None;
                fs.Seek(directory, SeekOrigin.Begin);
                uint resourceRva = reader.ReadUInt32();
                if (resourceRva == 0) return PeVersionStrings.None;

                fs.Seek(optional + optionalSize, SeekOrigin.Begin);
                for (int i = 0; i < sectionCount; i++)
                {
                    fs.Seek(8, SeekOrigin.Current);           // Name
                    uint virtualSize = reader.ReadUInt32();
                    uint va = reader.ReadUInt32();
                    uint rawSize = reader.ReadUInt32();
                    uint raw = reader.ReadUInt32();
                    fs.Seek(16, SeekOrigin.Current);          // relocations, line numbers, characteristics
                    if (resourceRva < va || resourceRva >= va + Math.Max(virtualSize, rawSize)) continue;

                    // Icons make a resource section big, so the version block is found by
                    // walking the tree (type 16, first name, first language) rather than
                    // by reading the section.
                    long root = raw + (resourceRva - va);

                    (uint Offset, bool IsDirectory)? First(uint directoryOffset, uint? id)
                    {
                        fs.Seek(root + directoryOffset + 12, SeekOrigin.Begin);
                        int named = reader.ReadUInt16(), ids = reader.ReadUInt16();
                        for (int n = 0; n < named + ids && n < 64; n++)
                        {
                            fs.Seek(root + directoryOffset + 16 + 8L * n, SeekOrigin.Begin);
                            uint key = reader.ReadUInt32(), target = reader.ReadUInt32();
                            if (id is null ? n == 0 : n >= named && key == id)
                                return (target & 0x7FFFFFFF, (target & 0x80000000) != 0);
                        }
                        return null;
                    }

                    if (First(0, 16) is not { IsDirectory: true } type) return PeVersionStrings.None;
                    if (First(type.Offset, null) is not { IsDirectory: true } name) return PeVersionStrings.None;
                    if (First(name.Offset, null) is not { IsDirectory: false } leaf) return PeVersionStrings.None;

                    fs.Seek(root + leaf.Offset, SeekOrigin.Begin);
                    uint dataRva = reader.ReadUInt32(), size = reader.ReadUInt32();
                    if (dataRva < va || size is 0 or > 1 << 16) return PeVersionStrings.None;

                    var bytes = new byte[size];
                    fs.Seek(raw + (dataRva - va), SeekOrigin.Begin);
                    fs.ReadExactly(bytes, 0, bytes.Length);
                    return new PeVersionStrings(
                        StringValue(bytes, "OriginalFilename"), StringValue(bytes, "FileDescription"));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return PeVersionStrings.None;
        }

        /// <summary>
        /// The value of a version-resource string. Key and value are both UTF-16 with
        /// padding between them, which is skipped rather than computed.
        /// </summary>
        private static string? StringValue(byte[] resource, string key)
        {
            var needle = System.Text.Encoding.Unicode.GetBytes(key);
            var at = resource.AsSpan().IndexOf(needle);
            if (at < 0) return null;

            var i = at + needle.Length;
            while (i + 1 < resource.Length && resource[i] == 0 && resource[i + 1] == 0) i += 2;
            var start = i;
            while (i + 1 < resource.Length && (resource[i] != 0 || resource[i + 1] != 0)) i += 2;
            var value = System.Text.Encoding.Unicode.GetString(resource, start, i - start).Trim();
            return value.Length > 0 ? value : null;
        }

        private static (string? fileVersion, string? productVersion) ScanForFixedFileInfo(FileStream fs)
        {
            const int chunkSize = 1 << 20;
            var buffer = new byte[chunkSize + 64]; // overlap so the struct never straddles a chunk boundary

            fs.Seek(0, SeekOrigin.Begin);
            int carried = 0;

            while (true)
            {
                int read = fs.Read(buffer, carried, chunkSize);
                if (read <= 0)
                    break;

                int available = carried + read;
                // Need signature + 5 more DWORDs (dwStrucVersion .. dwProductVersionLS)
                for (int i = 0; i + 24 <= available; i += 4)
                {
                    if (BitConverter.ToUInt32(buffer, i) != VsFixedFileInfoSignature)
                        continue;

                    uint strucVersion = BitConverter.ToUInt32(buffer, i + 4);
                    // dwStrucVersion is 1.0 (0x00010000) in every known PE
                    if ((strucVersion & 0xFFFF0000) != 0x00010000)
                        continue;

                    uint fileMs = BitConverter.ToUInt32(buffer, i + 8);
                    uint fileLs = BitConverter.ToUInt32(buffer, i + 12);
                    uint prodMs = BitConverter.ToUInt32(buffer, i + 16);
                    uint prodLs = BitConverter.ToUInt32(buffer, i + 20);

                    string fileVersion = $"{fileMs >> 16}.{fileMs & 0xFFFF}.{fileLs >> 16}.{fileLs & 0xFFFF}";
                    string productVersion = $"{prodMs >> 16}.{prodMs & 0xFFFF}.{prodLs >> 16}.{prodLs & 0xFFFF}";
                    return (fileVersion, productVersion);
                }

                if (read < chunkSize)
                    break;

                // Keep the last 64 bytes as overlap for the next chunk
                Array.Copy(buffer, available - 64, buffer, 0, 64);
                carried = 64;
            }

            return (null, null);
        }
    }
}
