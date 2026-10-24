// OptiScaler Client - test fixtures
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System;
using System.IO;
using System.Linq;
using System.Text;

namespace UpscalerManager.Core.Tests
{
    /// <summary>
    /// Builds minimal, in-memory PE files for testing PeFileInspector without
    /// shipping binary fixtures. Only the fields the inspector reads are populated:
    /// the MZ/PE signatures, the COFF machine type, the optional-header magic and
    /// security data directory, and (optionally) a VS_FIXEDFILEINFO block that the
    /// version scanner locates by signature.
    ///
    /// Public rather than internal so the UI harness can build the same fixtures: the
    /// swap flow needs real 64-bit PEs carrying version resources, and a second
    /// implementation of that would be free to disagree with this one.
    /// </summary>
    public static class PeTestData
    {
        public const ushort MachineAmd64 = 0x8664;
        public const ushort MachineI386 = 0x014c;

        /// <summary>
        /// Produces a minimal PE64 (or PE32) image.
        /// </summary>
        /// <param name="machine">COFF machine type (0x8664 = x64, 0x014c = x86).</param>
        /// <param name="fileVersion">If set (a.b.c.d), appends a VS_FIXEDFILEINFO block.</param>
        /// <param name="withSecurityDir">When true, sets a non-empty security data directory (signature present).</param>
        public static byte[] BuildPe(ushort machine = MachineAmd64, string? fileVersion = null, bool withSecurityDir = false)
        {
            bool pe32Plus = true; // we only need PE32+ for the x64 case; magic set accordingly
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            // DOS header: "MZ" then padding to 0x3C, then e_lfanew
            w.Write((byte)'M');
            w.Write((byte)'Z');
            while (ms.Position < 0x3C) w.Write((byte)0);
            int peHeaderOffset = 0x80;
            w.Write(peHeaderOffset); // e_lfanew at 0x3C
            while (ms.Position < peHeaderOffset) w.Write((byte)0);

            // PE signature "PE\0\0"
            w.Write((byte)'P'); w.Write((byte)'E'); w.Write((byte)0); w.Write((byte)0);

            // COFF file header (20 bytes)
            // A version resource needs a real .rsrc section for the reader to find it.
            ushort numSections = string.IsNullOrEmpty(fileVersion) ? (ushort)0 : (ushort)1;
            const uint RsrcRva = 0x1000;
            const uint RsrcRawOffset = 0x400;
            const uint RsrcRawSize = 0x200;

            w.Write(machine);              // Machine
            w.Write(numSections);          // NumberOfSections
            w.Write((uint)0);              // TimeDateStamp
            w.Write((uint)0);              // PointerToSymbolTable
            w.Write((uint)0);              // NumberOfSymbols
            ushort optHeaderSize = 240;    // enough to cover data directories
            w.Write(optHeaderSize);        // SizeOfOptionalHeader
            w.Write((ushort)0x22);         // Characteristics (EXECUTABLE|LARGE_ADDRESS_AWARE)

            long optStart = ms.Position;
            // Optional header magic: 0x20B = PE32+, 0x10B = PE32
            w.Write((ushort)(pe32Plus ? 0x20B : 0x10B));

            // Resource data directory (index 2) — how the version reader locates .rsrc.
            if (numSections > 0)
            {
                long resourceDirPos = optStart + 112 + 2 * 8;
                while (ms.Position < resourceDirPos) w.Write((byte)0);
                w.Write(RsrcRva);
                w.Write(RsrcRawSize);
            }

            // Pad the optional header up to the security data directory.
            // Data directories start at offset 112 (PE32+) into the optional header;
            // the security dir is index 4 → +32 bytes.
            long securityDirPos = optStart + 112 + 4 * 8;
            while (ms.Position < securityDirPos) w.Write((byte)0);
            if (withSecurityDir)
            {
                w.Write((uint)0x1000); // cert table RVA (non-zero)
                w.Write((uint)0x200);  // cert table size (non-zero)
            }
            else
            {
                w.Write((uint)0);
                w.Write((uint)0);
            }
            // Pad to end of optional header
            while (ms.Position < optStart + optHeaderSize) w.Write((byte)0);

            // Section table, then the .rsrc payload holding VS_FIXEDFILEINFO.
            if (numSections > 0)
            {
                var parts = fileVersion!.Split('.');
                ushort a = ushort.Parse(parts[0]);
                ushort b = parts.Length > 1 ? ushort.Parse(parts[1]) : (ushort)0;
                ushort c = parts.Length > 2 ? ushort.Parse(parts[2]) : (ushort)0;
                ushort d = parts.Length > 3 ? ushort.Parse(parts[3]) : (ushort)0;

                // IMAGE_SECTION_HEADER (40 bytes)
                foreach (var ch in ".rsrc\0\0\0") w.Write((byte)ch);
                w.Write(RsrcRawSize);   // VirtualSize
                w.Write(RsrcRva);       // VirtualAddress
                w.Write(RsrcRawSize);   // SizeOfRawData
                w.Write(RsrcRawOffset); // PointerToRawData
                w.Write((uint)0);       // PointerToRelocations
                w.Write((uint)0);       // PointerToLinenumbers
                w.Write((ushort)0);     // NumberOfRelocations
                w.Write((ushort)0);     // NumberOfLinenumbers
                w.Write((uint)0x40000040); // Characteristics (initialised data, read)

                while (ms.Position < RsrcRawOffset) w.Write((byte)0);

                w.Write((uint)0xFEEF04BD);              // dwSignature
                w.Write((uint)0x00010000);              // dwStrucVersion (1.0)
                w.Write((uint)(((uint)a << 16) | b));   // dwFileVersionMS
                w.Write((uint)(((uint)c << 16) | d));   // dwFileVersionLS
                w.Write((uint)(((uint)a << 16) | b));   // dwProductVersionMS (mirror)
                w.Write((uint)(((uint)c << 16) | d));   // dwProductVersionLS

                while (ms.Position < RsrcRawOffset + RsrcRawSize) w.Write((byte)0);
            }

            return ms.ToArray();
        }

        /// <summary>A minimal 64-bit DLL whose export directory names <paramref name="exports"/>.</summary>
        public static byte[] BuildPeExporting(params string[] exports)
        {
            const uint Rva = 0x1000, Raw = 0x400, Size = 0x400;
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            w.Write((ushort)0x5A4D);
            while (ms.Position < 0x3C) w.Write((byte)0);
            w.Write(0x80);
            while (ms.Position < 0x80) w.Write((byte)0);
            w.Write(0x00004550u);

            w.Write((ushort)0x8664);   // Machine
            w.Write((ushort)1);        // NumberOfSections
            w.Write(0u); w.Write(0u); w.Write(0u);
            w.Write((ushort)240);      // SizeOfOptionalHeader
            w.Write((ushort)0x2022);   // Characteristics (DLL)

            var optional = ms.Position;
            w.Write((ushort)0x20B);
            while (ms.Position < optional + 112) w.Write((byte)0);
            w.Write(Rva);              // export directory
            w.Write(Size);
            while (ms.Position < optional + 240) w.Write((byte)0);

            foreach (var c in ".edata\0\0") w.Write((byte)c);
            w.Write(Size); w.Write(Rva); w.Write(Size); w.Write(Raw);
            w.Write(0u); w.Write(0u); w.Write((ushort)0); w.Write((ushort)0);
            w.Write(0x40000040u);
            while (ms.Position < Raw) w.Write((byte)0);

            // IMAGE_EXPORT_DIRECTORY, then the name pointers, then the names.
            var names = Rva + 40;
            var strings = names + 4u * (uint)exports.Length;
            w.Write(0u); w.Write(0u); w.Write((ushort)0); w.Write((ushort)0);
            w.Write(0u);                       // Name
            w.Write(1u);                       // Base
            w.Write((uint)exports.Length);     // NumberOfFunctions
            w.Write((uint)exports.Length);     // NumberOfNames
            w.Write(0u);                       // AddressOfFunctions
            w.Write(names);                    // AddressOfNames
            w.Write(0u);                       // AddressOfNameOrdinals
            foreach (var name in exports)
            {
                w.Write(strings);
                strings += (uint)name.Length + 1;
            }
            foreach (var name in exports)
            {
                w.Write(Encoding.ASCII.GetBytes(name));
                w.Write((byte)0);
            }
            while (ms.Position < Raw + Size) w.Write((byte)0);
            return ms.ToArray();
        }

        /// <summary>
        /// A minimal 64-bit DLL with both an export directory and a version resource: the
        /// file version, and optionally the OriginalFilename and FileDescription strings a
        /// renamed AMD DLL is recognised by.
        /// </summary>
        public static byte[] BuildVersionedPeExporting(
            string fileVersion, string[] exports, string? originalFilename = null, string? fileDescription = null)
        {
            const uint EdataRva = 0x1000, EdataRaw = 0x400, EdataSize = 0x400;
            const uint RsrcRva = 0x2000, RsrcRaw = 0x800, RsrcSize = 0x400;
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            w.Write((ushort)0x5A4D);
            while (ms.Position < 0x3C) w.Write((byte)0);
            w.Write(0x80);
            while (ms.Position < 0x80) w.Write((byte)0);
            w.Write(0x00004550u);

            w.Write((ushort)0x8664);   // Machine
            w.Write((ushort)2);        // NumberOfSections
            w.Write(0u); w.Write(0u); w.Write(0u);
            w.Write((ushort)240);      // SizeOfOptionalHeader
            w.Write((ushort)0x2022);   // Characteristics (DLL)

            var optional = ms.Position;
            w.Write((ushort)0x20B);
            while (ms.Position < optional + 112) w.Write((byte)0);
            w.Write(EdataRva); w.Write(EdataSize);   // export directory
            w.Write(0u); w.Write(0u);                // import directory
            w.Write(RsrcRva); w.Write(RsrcSize);     // resource directory
            while (ms.Position < optional + 240) w.Write((byte)0);

            foreach (var c in ".edata\0\0") w.Write((byte)c);
            w.Write(EdataSize); w.Write(EdataRva); w.Write(EdataSize); w.Write(EdataRaw);
            w.Write(0u); w.Write(0u); w.Write((ushort)0); w.Write((ushort)0);
            w.Write(0x40000040u);
            foreach (var c in ".rsrc\0\0\0") w.Write((byte)c);
            w.Write(RsrcSize); w.Write(RsrcRva); w.Write(RsrcSize); w.Write(RsrcRaw);
            w.Write(0u); w.Write(0u); w.Write((ushort)0); w.Write((ushort)0);
            w.Write(0x40000040u);

            while (ms.Position < EdataRaw) w.Write((byte)0);
            var names = EdataRva + 40;
            var strings = names + 4u * (uint)exports.Length;
            w.Write(0u); w.Write(0u); w.Write((ushort)0); w.Write((ushort)0);
            w.Write(0u); w.Write(1u);
            w.Write((uint)exports.Length); w.Write((uint)exports.Length);
            w.Write(0u); w.Write(names); w.Write(0u);
            foreach (var name in exports)
            {
                w.Write(strings);
                strings += (uint)name.Length + 1;
            }
            foreach (var name in exports)
            {
                w.Write(Encoding.ASCII.GetBytes(name));
                w.Write((byte)0);
            }
            while (ms.Position < EdataRaw + EdataSize) w.Write((byte)0);

            var parts = fileVersion.Split('.').Select(ushort.Parse).ToArray();
            ushort a = parts[0], b = parts.Length > 1 ? parts[1] : (ushort)0;
            ushort c3 = parts.Length > 2 ? parts[2] : (ushort)0, d = parts.Length > 3 ? parts[3] : (ushort)0;

            // The version block, as the resource tree below points at it.
            using var block = new MemoryStream();
            using var bw = new BinaryWriter(block);
            bw.Write(0xFEEF04BDu); bw.Write(0x00010000u);
            bw.Write(((uint)a << 16) | b); bw.Write(((uint)c3 << 16) | d);
            bw.Write(((uint)a << 16) | b); bw.Write(((uint)c3 << 16) | d);
            void Str(string key, string? value)
            {
                if (value is null) return;
                foreach (var text in new[] { key, value })
                {
                    bw.Write(Encoding.Unicode.GetBytes(text));
                    bw.Write((ushort)0);
                    while (block.Position % 4 != 0) bw.Write((byte)0);
                }
            }
            Str("OriginalFilename", originalFilename);
            Str("FileDescription", fileDescription);

            // Resource tree: type 16 (version) -> name 1 -> language 0x409 -> the block.
            const uint DataAt = 0x58;
            void Level(uint id, uint target)
            {
                w.Write(0u); w.Write(0u); w.Write(0u);       // characteristics, time, version
                w.Write((ushort)0); w.Write((ushort)1);      // named entries, id entries
                w.Write(id); w.Write(target);
            }
            Level(16, 0x80000000u | 0x18);
            Level(1, 0x80000000u | 0x30);
            Level(0x409, 0x48);
            w.Write(RsrcRva + DataAt); w.Write((uint)block.Length); w.Write(0u); w.Write(0u);
            w.Write(block.ToArray());
            if (ms.Position > RsrcRaw + RsrcSize) throw new InvalidOperationException("The fixture's resource section is too small.");
            while (ms.Position < RsrcRaw + RsrcSize) w.Write((byte)0);
            return ms.ToArray();
        }

        /// <summary>Writes bytes to a temp .dll and returns the path (caller deletes).</summary>
        public static string WriteTempDll(byte[] bytes)
        {
            var path = Path.Combine(Path.GetTempPath(), "petest_" + Guid.NewGuid().ToString("N") + ".dll");
            File.WriteAllBytes(path, bytes);
            return path;
        }
    }
}
