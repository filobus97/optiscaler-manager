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

namespace OptiscalerManager.Core.Services
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
