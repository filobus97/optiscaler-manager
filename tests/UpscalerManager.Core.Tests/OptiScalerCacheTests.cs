// Upscaler Manager - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System.IO;
using UpscalerManager.Core.Services;
using Xunit;

namespace UpscalerManager.Core.Tests
{
    /// <summary>
    /// A cache left by an interrupted download can contain many files but not the
    /// main injectable DLL. These pin the completeness check that makes the app
    /// self-heal (re-download) instead of failing the install.
    /// </summary>
    public class OptiScalerCacheTests
    {
        private static string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "osm_cache_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void PartialCache_WithoutMainDll_IsIncomplete()
        {
            var dir = NewTempDir();
            try
            {
                // Mirror the reported failure: FFX/XeSS DLLs + ini present, no OptiScaler.dll.
                foreach (var f in new[] { "amd_fidelityfx_dx12.dll", "libxess.dll", "OptiScaler.ini", "setup_linux.sh" })
                    File.WriteAllText(Path.Combine(dir, f), "x");

                Assert.False(ComponentManagementService.OptiScalerCacheHasMainDll(dir));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Theory]
        [InlineData("OptiScaler.dll")]
        [InlineData("nvngx.dll")]      // legacy injectable name
        [InlineData("optiscaler.dll")] // case-insensitive
        public void Cache_WithMainDll_IsComplete(string mainName)
        {
            var dir = NewTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "amd_fidelityfx_dx12.dll"), "x");
                File.WriteAllText(Path.Combine(dir, mainName), "x");
                Assert.True(ComponentManagementService.OptiScalerCacheHasMainDll(dir));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void MissingDirectory_IsIncomplete()
            => Assert.False(ComponentManagementService.OptiScalerCacheHasMainDll(
                Path.Combine(Path.GetTempPath(), "osm_does_not_exist_" + System.Guid.NewGuid().ToString("N"))));
        [Fact]
        public void AWindowsArchivePathBecomesARealSubfolder()
        {
            // The release that first shipped an OptiScaler subfolder would not install:
            // the archive stores the entry as OptiScaler\OptiScaler.dll, and on Linux a backslash
            // is part of the file NAME, so every nested entry landed as one top-level
            // file and the main DLL was never found.
            var dir = NewTempDir();
            try
            {
                var dest = ComponentManagementService.SafeDestinationPath(dir, @"OptiScaler\OptiScaler.dll");

                Assert.Equal(Path.Combine(dir, "OptiScaler", "OptiScaler.dll"), dest);
                Assert.Equal("OptiScaler.dll", Path.GetFileName(dest));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void AndTheDeepOnesToo()
        {
            var dir = NewTempDir();
            try
            {
                var dest = ComponentManagementService.SafeDestinationPath(
                    dir, @"OptiScaler\D3D12_OptiScaler\D3D12Core.dll");

                Assert.Equal(Path.Combine(dir, "OptiScaler", "D3D12_OptiScaler", "D3D12Core.dll"), dest);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Theory]
        [InlineData(@"..\..\evil.dll")]
        [InlineData("../../evil.dll")]
        [InlineData("/etc/passwd")]
        [InlineData(@"C:\Windows\System32\evil.dll")]
        public void NothingEscapesTheCacheFolder(string entry)
        {
            var dir = NewTempDir();
            try
            {
                var dest = TryResolve(dir, entry);

                // Either refused outright, or normalised to somewhere inside the folder.
                if (dest is not null)
                    Assert.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, dest);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string? TryResolve(string dir, string entry)
        {
            try { return ComponentManagementService.SafeDestinationPath(dir, entry); }
            catch (System.InvalidOperationException) { return null; }
        }

        [Fact]
        public void AReleaseLaidOutLikeAWindowsArchiveUnpacksAndIsUsable()
        {
            // End to end over the reported failure: a release whose files sit in an
            // OptiScaler subfolder must unpack as folders and pass the same
            // completeness check the installer uses.
            var dir = NewTempDir();
            var zip = Path.Combine(dir, "release.zip");
            var into = Path.Combine(dir, "cache");
            try
            {
                using (var archive = System.IO.Compression.ZipFile.Open(
                           zip, System.IO.Compression.ZipArchiveMode.Create))
                {
                    foreach (var entry in new[]
                             {
                                 "OptiScaler.ini",
                                 @"OptiScaler\OptiScaler.dll",
                                 @"OptiScaler\amd_fidelityfx_upscaler_dx12.dll",
                                 @"OptiScaler\D3D12_OptiScaler\D3D12Core.dll",
                             })
                    {
                        using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
                        writer.Write("MZ");
                    }
                }

                var written = ComponentManagementService.ExtractArchive(zip, into);

                Assert.Equal(4, written);
                Assert.True(File.Exists(Path.Combine(into, "OptiScaler", "OptiScaler.dll")));
                Assert.True(File.Exists(Path.Combine(into, "OptiScaler", "D3D12_OptiScaler", "D3D12Core.dll")));
                Assert.True(ComponentManagementService.OptiScalerCacheHasMainDll(into));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Theory]
        [InlineData(@"Kits\FidelityFX\amd_fidelityfx_dx12.dll", "amd_fidelityfx_dx12.dll")]
        [InlineData("Kits/FidelityFX/amd_fidelityfx_dx12.dll", "amd_fidelityfx_dx12.dll")]
        [InlineData("nvngx_dlss.dll", "nvngx_dlss.dll")]
        public void AnEntrysOwnNameIsReadWhicheverSeparatorTheArchiveUsed(string key, string expected)
        {
            // Every importer tests this name against a known set, so a whole-path answer
            // reads as "the archive had nothing in it".
            Assert.Equal(expected, ComponentManagementService.ArchiveEntryName(key));
        }

    }
}
