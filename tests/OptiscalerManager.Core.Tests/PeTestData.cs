// OptiScaler Client - test fixtures
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System;
using System.IO;

namespace OptiscalerManager.Core.Tests
{
    /// <summary>
    /// Builds minimal, in-memory PE files for testing PeFileInspector without
    /// shipping binary fixtures. Only the fields the inspector reads are populated:
    /// the MZ/PE signatures, the COFF machine type, the optional-header magic and
    /// security data directory, and (optionally) a VS_FIXEDFILEINFO block that the
    /// version scanner locates by signature.
    /// </summary>
    internal static class PeTestData
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
            w.Write(machine);              // Machine
            w.Write((ushort)0);            // NumberOfSections
            w.Write((uint)0);              // TimeDateStamp
            w.Write((uint)0);              // PointerToSymbolTable
            w.Write((uint)0);              // NumberOfSymbols
            ushort optHeaderSize = 240;    // enough to cover data directories
            w.Write(optHeaderSize);        // SizeOfOptionalHeader
            w.Write((ushort)0x22);         // Characteristics (EXECUTABLE|LARGE_ADDRESS_AWARE)

            long optStart = ms.Position;
            // Optional header magic: 0x20B = PE32+, 0x10B = PE32
            w.Write((ushort)(pe32Plus ? 0x20B : 0x10B));
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

            // Optional VS_FIXEDFILEINFO block (located by signature scan)
            if (!string.IsNullOrEmpty(fileVersion))
            {
                var parts = fileVersion.Split('.');
                ushort a = ushort.Parse(parts[0]);
                ushort b = parts.Length > 1 ? ushort.Parse(parts[1]) : (ushort)0;
                ushort c = parts.Length > 2 ? ushort.Parse(parts[2]) : (ushort)0;
                ushort d = parts.Length > 3 ? ushort.Parse(parts[3]) : (ushort)0;

                // Align to 4 bytes
                while (ms.Position % 4 != 0) w.Write((byte)0);
                w.Write((uint)0xFEEF04BD);              // dwSignature
                w.Write((uint)0x00010000);              // dwStrucVersion (1.0)
                w.Write((uint)(((uint)a << 16) | b));   // dwFileVersionMS
                w.Write((uint)(((uint)c << 16) | d));   // dwFileVersionLS
                w.Write((uint)(((uint)a << 16) | b));   // dwProductVersionMS (mirror)
                w.Write((uint)(((uint)c << 16) | d));   // dwProductVersionLS
                // Trailing padding so the 24-byte read window is fully in range
                for (int i = 0; i < 16; i++) w.Write((byte)0);
            }

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
