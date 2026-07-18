// OptiScaler Client - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OptiscalerManager.Core.Services;
using Xunit;

namespace OptiscalerManager.Core.Tests
{
    /// <summary>
    /// The SDK-package scan collects known FSR SDK DLLs from a folder tree,
    /// requires a 64-bit upscaler, skips 32-bit copies and unrelated DLLs, and
    /// prefers 'signed' paths. These pin that selection logic against a synthetic
    /// SDK tree built from minimal PE files (no proprietary binaries involved).
    /// </summary>
    public class FsrSdkScanTests : IDisposable
    {
        private readonly string _root;

        public FsrSdkScanTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "sdkscan_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }

        private void Place(string relativePath, ushort machine, string? version = "4.1.1.0")
        {
            var full = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, PeTestData.BuildPe(machine, fileVersion: version));
        }

        [Fact]
        public async Task Folder_CollectsKnownDllsAndSkipsDecoys()
        {
            Place(Path.Combine("bin", "amd_fidelityfx_upscaler_dx12.dll"), PeTestData.MachineAmd64);
            Place(Path.Combine("bin", "amd_fidelityfx_framegeneration_dx12.dll"), PeTestData.MachineAmd64);
            Place(Path.Combine("x86", "amd_fidelityfx_upscaler_dx12.dll"), PeTestData.MachineI386); // 32-bit decoy
            Place(Path.Combine("docs", "random_other.dll"), PeTestData.MachineAmd64);               // unrelated

            var svc = new ComponentManagementService();
            var scan = await svc.ScanFsrSdkSourceAsync(_root);

            Assert.True(scan.HasUpscaler);
            Assert.Contains("amd_fidelityfx_upscaler_dx12.dll", scan.FoundFiles.Keys);
            Assert.Contains("amd_fidelityfx_framegeneration_dx12.dll", scan.FoundFiles.Keys);
            // The 64-bit copy is chosen, never the 32-bit x86 one
            Assert.DoesNotContain("x86", scan.FoundFiles["amd_fidelityfx_upscaler_dx12.dll"]);
            // Unrelated DLLs are ignored
            Assert.DoesNotContain(scan.FoundFiles.Values, v => v.Contains("random_other"));

            scan.Cleanup();
        }

        [Fact]
        public async Task Folder_PrefersSignedPathForDuplicates()
        {
            Place(Path.Combine("unsigned", "amd_fidelityfx_upscaler_dx12.dll"), PeTestData.MachineAmd64);
            Place(Path.Combine("signed", "amd_fidelityfx_upscaler_dx12.dll"), PeTestData.MachineAmd64);

            var svc = new ComponentManagementService();
            var scan = await svc.ScanFsrSdkSourceAsync(_root);

            Assert.Contains("signed", scan.FoundFiles["amd_fidelityfx_upscaler_dx12.dll"]);
            scan.Cleanup();
        }

        [Fact]
        public async Task Folder_PrefersLargestCopyForEqualDuplicates()
        {
            // The FidelityFX SDK ships several different builds of the same DLL at
            // equal depth (e.g. a reduced upscaler with the denoiser sample and the
            // full ML-capable one with the FSR sample). The ML build is much larger —
            // the scanner must pick the largest, not the first enumerated.
            Place(Path.Combine("Samples", "Denoisers", "amd_fidelityfx_upscaler_dx12.dll"), PeTestData.MachineAmd64);
            Place(Path.Combine("Samples", "Upscalers", "amd_fidelityfx_upscaler_dx12.dll"), PeTestData.MachineAmd64);

            // Pad the Upscalers-sample copy so it is clearly the bigger build.
            var big = Path.Combine(_root, "Samples", "Upscalers", "amd_fidelityfx_upscaler_dx12.dll");
            using (var fs = new FileStream(big, FileMode.Append))
                fs.Write(new byte[64 * 1024]);

            var svc = new ComponentManagementService();
            var scan = await svc.ScanFsrSdkSourceAsync(_root);

            Assert.Contains("Upscalers", scan.FoundFiles["amd_fidelityfx_upscaler_dx12.dll"]);
            scan.Cleanup();
        }

        [Fact]
        public async Task Folder_WithoutUpscaler_HasNoUpscaler()
        {
            Place(Path.Combine("bin", "amd_fidelityfx_framegeneration_dx12.dll"), PeTestData.MachineAmd64);

            var svc = new ComponentManagementService();
            var scan = await svc.ScanFsrSdkSourceAsync(_root);

            Assert.False(scan.HasUpscaler);
            scan.Cleanup();
        }
    }
}
