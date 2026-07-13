// OptiScaler Client - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System.IO;
using OptiscalerManager.Core.Services;
using Xunit;

namespace OptiscalerManager.Core.Tests
{
    public class PeFileInspectorTests
    {
        [Fact]
        public void RandomBytes_AreNotValidPe()
        {
            var path = PeTestData.WriteTempDll(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            try
            {
                var info = PeFileInspector.Inspect(path);
                Assert.False(info.IsValidPe);
                Assert.False(info.Is64Bit);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Amd64Pe_IsValidAnd64Bit()
        {
            var path = PeTestData.WriteTempDll(PeTestData.BuildPe(PeTestData.MachineAmd64));
            try
            {
                var info = PeFileInspector.Inspect(path);
                Assert.True(info.IsValidPe);
                Assert.True(info.Is64Bit);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void I386Pe_IsValidButNot64Bit()
        {
            var path = PeTestData.WriteTempDll(PeTestData.BuildPe(PeTestData.MachineI386));
            try
            {
                var info = PeFileInspector.Inspect(path);
                Assert.True(info.IsValidPe);
                Assert.False(info.Is64Bit);
            }
            finally { File.Delete(path); }
        }

        [Theory]
        [InlineData("2.3.1.0")]
        [InlineData("4.1.1.0")]
        [InlineData("2.2.0.0")]
        public void VersionResource_IsReadFromFixedFileInfo(string version)
        {
            var path = PeTestData.WriteTempDll(PeTestData.BuildPe(PeTestData.MachineAmd64, fileVersion: version));
            try
            {
                var info = PeFileInspector.Inspect(path);
                Assert.Equal(version, info.FileVersion);
                Assert.Equal(version, info.ProductVersion);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void NoVersionResource_LeavesVersionsNull()
        {
            var path = PeTestData.WriteTempDll(PeTestData.BuildPe(PeTestData.MachineAmd64, fileVersion: null));
            try
            {
                var info = PeFileInspector.Inspect(path);
                Assert.Null(info.FileVersion);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void SecurityDirectory_TogglesSignaturePresence()
        {
            var signed = PeTestData.WriteTempDll(PeTestData.BuildPe(PeTestData.MachineAmd64, withSecurityDir: true));
            var unsigned = PeTestData.WriteTempDll(PeTestData.BuildPe(PeTestData.MachineAmd64, withSecurityDir: false));
            try
            {
                Assert.True(PeFileInspector.Inspect(signed).HasAuthenticodeSignature);
                Assert.False(PeFileInspector.Inspect(unsigned).HasAuthenticodeSignature);
            }
            finally { File.Delete(signed); File.Delete(unsigned); }
        }
    }
}
