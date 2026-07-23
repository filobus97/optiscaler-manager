// OptiScaler Manager - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System.IO;
using OptiscalerManager.Core.Services;
using Xunit;

namespace OptiscalerManager.Core.Tests
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
    }
}
