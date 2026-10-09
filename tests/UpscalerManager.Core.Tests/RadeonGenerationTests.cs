// Upscaler Manager - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using UpscalerManager.Core.Services;
using Xunit;

namespace UpscalerManager.Core.Tests
{
    /// <summary>
    /// FSR 4 runs a different model on RX 9000 than on older cards, and community builds
    /// with the GPU check lifted run either way — so telling the generations apart is
    /// what stands between a player and the wrong model.
    /// </summary>
    public class RadeonGenerationTests
    {
        private static GpuInfo Amd(string name, int? id = null) => new() { Vendor = GpuVendor.AMD, Name = name, DeviceId = id };

        [Theory]
        [InlineData(0x7550, RadeonGeneration.Rdna4)]   // RX 9070 XT
        [InlineData(0x7590, RadeonGeneration.Rdna4)]   // RX 9060 XT
        [InlineData(0x744C, RadeonGeneration.Rdna3)]   // RX 7900 XTX
        [InlineData(0x747E, RadeonGeneration.Rdna3)]   // RX 7800 XT
        [InlineData(0x7480, RadeonGeneration.Rdna3)]   // RX 7600
        [InlineData(0x73BF, RadeonGeneration.Rdna2)]   // RX 6800 XT
        [InlineData(0x73DF, RadeonGeneration.Rdna2)]   // RX 6700 XT
        [InlineData(0x743F, RadeonGeneration.Rdna2)]   // RX 6500 XT
        [InlineData(0x163F, RadeonGeneration.SteamDeck)]
        [InlineData(0x1435, RadeonGeneration.SteamDeck)]
        [InlineData(0x15BF, RadeonGeneration.Rdna3Integrated)] // 780M
        [InlineData(0x1681, RadeonGeneration.Rdna2Integrated)] // 680M
        public void TheDeviceIdDecides(int id, RadeonGeneration expected)
            => Assert.Equal(expected, RadeonGenerations.Of(Amd("AMD Radeon Graphics", id)));

        [Theory]
        [InlineData("Advanced Micro Devices, Inc. [AMD/ATI] Navi 48 [Radeon RX 9070/9070 XT]", RadeonGeneration.Rdna4)]
        [InlineData("Advanced Micro Devices, Inc. [AMD/ATI] Navi 31 [Radeon RX 7900 XT/7900 XTX]", RadeonGeneration.Rdna3)]
        [InlineData("Advanced Micro Devices, Inc. [AMD/ATI] Navi 22 [Radeon RX 6700/6700 XT/6750 XT]", RadeonGeneration.Rdna2)]
        [InlineData("AMD Radeon RX 9070 XT", RadeonGeneration.Rdna4)]
        [InlineData("AMD Radeon RX 7800 XT", RadeonGeneration.Rdna3)]
        [InlineData("AMD Radeon RX 6600", RadeonGeneration.Rdna2)]
        [InlineData("Advanced Micro Devices, Inc. [AMD/ATI] VanGogh [AMD Custom GPU 0405]", RadeonGeneration.SteamDeck)]
        [InlineData("Advanced Micro Devices, Inc. [AMD/ATI] Phoenix1", RadeonGeneration.Rdna3Integrated)]
        [InlineData("AMD Radeon 780M Graphics", RadeonGeneration.Rdna3Integrated)]
        [InlineData("AMD Radeon 890M", RadeonGeneration.Rdna3Integrated)]
        [InlineData("AMD Radeon(TM) Graphics", RadeonGeneration.Unknown)]
        public void WithoutAnIdTheNameDecides(string name, RadeonGeneration expected)
            => Assert.Equal(expected, RadeonGenerations.Of(Amd(name)));

        [Fact]
        public void AnIdOutsideTheTableFallsBackToTheName()
            => Assert.Equal(RadeonGeneration.Rdna3, RadeonGenerations.Of(Amd("AMD Radeon RX 7700 XT", 0x0001)));

        [Fact]
        public void OtherVendorsAreNotRadeons()
        {
            Assert.Equal(RadeonGeneration.NotRadeon,
                RadeonGenerations.Of(new GpuInfo { Vendor = GpuVendor.NVIDIA, Name = "NVIDIA GeForce RTX 4070" }));
            Assert.Equal(RadeonGeneration.Unknown, RadeonGenerations.Of(null));
        }
    }
}
