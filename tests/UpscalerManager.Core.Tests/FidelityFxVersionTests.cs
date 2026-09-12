// Upscaler Manager - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System;
using System.IO;
using System.Text;
using UpscalerManager.Core.Components;
using Xunit;

namespace UpscalerManager.Core.Tests
{
    /// <summary>
    /// Reading the FSR version out of a FidelityFX runtime.
    ///
    /// The parse was validated against every build the DLSS Swapper archive holds —
    /// nine DX12 and seven Vulkan, all sixteen resolved to exactly the FSR version the
    /// archive's index records. That cannot live in a test (each binary is 6.5 MB), so
    /// what is here is the real 216-byte provider table lifted out of one of them, plus
    /// the edge cases that decide whether the parse stays honest when it is wrong.
    /// </summary>
    public class FidelityFxVersionTests
    {
        /// <summary>
        /// The provider version table from the real <c>amd_fidelityfx_dx12.dll</c> of
        /// file version 1.0.1.41314 — pointer blocks interleaved with the NUL-terminated
        /// strings 1.1.3, 2.3.3 and 3.1.4. The archive's index says that build is
        /// FSR 3.1.4, so 3.1.4 is the right answer.
        /// </summary>
        private const string RealProviderTableBase64 =
            "ADAqAIABAAAAMS4xLjMAAAAlAHMAJQBkAAAAAAAAAIA/eFVkgAEAAADAOgCAAQAAAKBBAIABAAAAwEEAgAEAAAD" +
            "QQQCAAQAAAOBBAIABAAAAUEQAgAEAAAAARQCAAQAAAIBFAIABAAAAEEcAgAEAAAAyLjMuMwAAAPhVZIABAAAAwD" +
            "oAgAEAAACgQQCAAQAAAOBSAIABAAAA8FIAgAEAAAAAUwCAAQAAAGBXAIABAAAAMFgAgAEAAADwWACAAQAAAKBaAI" +
            "ABAAAAMy4xLjQAAAB4VmSAAQAA";

        private static byte[] Real() => Convert.FromBase64String(RealProviderTableBase64);

        /// <summary>A NUL-delimited run of C strings, as the table is laid out.</summary>
        private static byte[] Table(params string[] strings)
        {
            using var buffer = new MemoryStream();
            buffer.WriteByte(0);
            foreach (var s in strings)
            {
                buffer.Write(Encoding.ASCII.GetBytes(s));
                buffer.WriteByte(0);
                buffer.Write(new byte[] { 0x11, 0x22, 0x33, 0x00 });   // a pointer block
            }
            return buffer.ToArray();
        }

        // ── The real thing ──────────────────────────────────────────────────

        [Fact]
        public void TheFsrVersionIsReadOutOfARealProviderTable()
        {
            Assert.Equal("3.1.4", FidelityFxVersion.FromProviderTable(Real()));
        }

        [Fact]
        public void TheHighestProviderWinsRatherThanTheFirstOneFound()
        {
            // The table is laid out ascending, so taking the first match would report
            // the FSR 2 API version — 1.1.3 — as though it were the FSR version.
            Assert.Equal("3.1.4", FidelityFxVersion.FromProviderTable(Table("1.1.3", "2.3.3", "3.1.4")));
            Assert.Equal("3.1.4", FidelityFxVersion.FromProviderTable(Table("3.1.4", "2.3.3", "1.1.3")));
        }

        [Fact]
        public void VersionsAreComparedNumericallyNotAsText()
        {
            // Sorted as text, "3.1.10" is lower than "3.1.9".
            Assert.Equal("3.1.10", FidelityFxVersion.FromProviderTable(Table("3.1.9", "3.1.10")));
            Assert.Equal("3.10.0", FidelityFxVersion.FromProviderTable(Table("3.9.9", "3.10.0")));
            Assert.Equal("4.0.1", FidelityFxVersion.FromProviderTable(Table("3.1.4", "4.0.1")));
        }

        // ── Declining to answer ─────────────────────────────────────────────

        [Fact]
        public void ALibraryOlderThanFsr31ClaimsNothing()
        {
            // Its highest provider is an SDK version in the 2.x range. Reporting
            // "FSR 2.3.2" would invent a version that does not exist, so the row falls
            // back to the build number instead.
            Assert.Null(FidelityFxVersion.FromProviderTable(Table("1.1.0", "2.3.2")));
        }

        [Fact]
        public void NothingIsFoundInAFileWithNoSuchTable()
        {
            Assert.Null(FidelityFxVersion.FromProviderTable(Array.Empty<byte>()));
            Assert.Null(FidelityFxVersion.FromProviderTable(new byte[] { 0, 0, 0, 0 }));
            Assert.Null(FidelityFxVersion.FromProviderTable(Encoding.ASCII.GetBytes("no versions here")));
        }

        [Fact]
        public void AFourPartFileVersionIsNotMistakenForAProviderVersion()
        {
            // 1.0.1.41314 is the file version, and sits in the same binary. Reading it
            // as a provider version would report the very number this exists to avoid.
            Assert.Null(FidelityFxVersion.FromProviderTable(Table("1.0.1.41314")));
            Assert.Equal("3.1.4", FidelityFxVersion.FromProviderTable(Table("1.0.1.41314", "3.1.4")));
        }

        [Fact]
        public void MalformedNumbersAreIgnored()
        {
            foreach (var junk in new[] { "3..1", "3.1.", ".3.1", "3.1.4a", "-3.1.4", "3.1.4444", "31.4" })
                Assert.Null(FidelityFxVersion.FromProviderTable(Table(junk)));
        }

        [Fact]
        public void AStringThatIsNotItsOwnCStringDoesNotCount()
        {
            // Requiring a NUL on both sides is what keeps this from matching digits
            // embedded in a longer string, and is why a 6.5 MB binary yields only the
            // three provider versions.
            var embedded = Encoding.ASCII.GetBytes("\0build-3.1.4-internal\0");
            Assert.Null(FidelityFxVersion.FromProviderTable(embedded));
        }

        [Fact]
        public void AnUnterminatedStringAtTheEndOfTheDataIsNotRead()
        {
            var truncated = Encoding.ASCII.GetBytes("\03.1.4");
            Assert.Null(FidelityFxVersion.FromProviderTable(truncated));
        }

        // ── Which files this applies to ─────────────────────────────────────

        [Fact]
        public void OnlyTheTwoRuntimesVerifiedAgainstRealBuildsAreTranslated()
        {
            Assert.True(FidelityFxVersion.Translates("amd_fidelityfx_dx12.dll"));
            Assert.True(FidelityFxVersion.Translates("amd_fidelityfx_vk.dll"));
            Assert.True(FidelityFxVersion.Translates("AMD_FidelityFX_DX12.DLL"));

            // DLSS and XeSS stamp the version a player recognises, so translating them
            // would replace a good number with a guess.
            Assert.False(FidelityFxVersion.Translates("nvngx_dlss.dll"));
            Assert.False(FidelityFxVersion.Translates("libxess.dll"));

            // FSR 4 lives in the upscaler, whose file version already *is* the FSR
            // version (4.0.2 and so on) — nothing to translate.
            Assert.False(FidelityFxVersion.Translates("amd_fidelityfx_upscaler_dx12.dll"));
            Assert.False(FidelityFxVersion.Translates("amdxcffx64.dll"));

            Assert.False(FidelityFxVersion.Translates(null));
        }

        [Fact]
        public void EveryTranslatedFileIsOneWeCanSwap()
        {
            foreach (var name in new[] { "amd_fidelityfx_dx12.dll", "amd_fidelityfx_vk.dll" })
                Assert.True(SwappableDlls.IsSwappable(name));
        }

        // ── How it reads ────────────────────────────────────────────────────

        [Fact]
        public void BothNumbersAreShownBecauseBothAreNeeded()
        {
            // The FSR version means something to a player; the build number is what the
            // app reads off the file, and is the only thing separating two builds of the
            // same FSR version.
            Assert.Equal("FSR 3.1.2  (1.0.1.38338)", FidelityFxVersion.Describe("3.1.2", "1.0.1.38338"));
            Assert.Equal("FSR 3.1.2  (1.0.2.38022)", FidelityFxVersion.Describe("3.1.2", "1.0.2.38022"));
        }

        [Fact]
        public void WhateverIsKnownIsShownAndNothingIsInvented()
        {
            Assert.Equal("FSR 3.1.4", FidelityFxVersion.Describe("3.1.4", null));
            Assert.Equal("310.9.1.0", FidelityFxVersion.Describe(null, "310.9.1.0"));
            Assert.Equal("unknown version", FidelityFxVersion.Describe(null, null));
            Assert.Equal("unknown version", FidelityFxVersion.Describe("", ""));
        }

        [Fact]
        public void ReadingAFileThatIsNotThereIsNotAnError()
        {
            // A DLL can be removed between the scan and the render; a missing file must
            // degrade to "no FSR version" rather than take down the page.
            Assert.Null(FidelityFxVersion.FromBinary("/nonexistent/amd_fidelityfx_dx12.dll"));
            Assert.Null(FidelityFxVersion.FromBinary(Path.GetTempPath()));
        }

        [Fact]
        public void TheRealTableSurvivesARoundTripThroughAFile()
        {
            var path = Path.Combine(Path.GetTempPath(), "ffx-" + Guid.NewGuid().ToString("N")[..8] + ".dll");
            try
            {
                File.WriteAllBytes(path, Real());
                Assert.Equal("3.1.4", FidelityFxVersion.FromBinary(path));
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }
    }
}
