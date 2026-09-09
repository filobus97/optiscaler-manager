// OptiScaler Client - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System;
using System.IO;
using System.Linq;
using OptiscalerManager.Core.Services;
using Xunit;

namespace OptiscalerManager.Core.Tests
{
    /// <summary>
    /// Section-aware OptiScaler.ini editing used by the custom FSR components.
    /// Editing ini text by hand is fiddly, so these pin the create/update/insert paths.
    /// </summary>
    public class IniEditingTests : IDisposable
    {
        private readonly string _dir;
        private string IniPath => Path.Combine(_dir, "OptiScaler.ini");

        public IniEditingTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "initest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        private string? ValueOf(string section, string key)
        {
            bool inSection = false;
            foreach (var raw in File.ReadAllLines(IniPath))
            {
                var line = raw.Trim();
                if (line.StartsWith("["))
                {
                    inSection = line.Equals($"[{section}]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inSection) continue;
                var eq = line.IndexOf('=');
                if (eq > 0 && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return line[(eq + 1)..].Trim();
            }
            return null;
        }

        [Fact]
        public void CreatesFileAndSectionWhenMissing()
        {
            GameInstallationService.ModifyOptiScalerIniKey(_dir, "FSR", "UpscalerIndex", "0");
            Assert.True(File.Exists(IniPath));
            Assert.Equal("0", ValueOf("FSR", "UpscalerIndex"));
        }

        [Fact]
        public void UpdatesExistingKeyInPlace()
        {
            File.WriteAllText(IniPath, "[FSR]\nUpscalerIndex=auto\nFsr4Update=auto\n");
            GameInstallationService.ModifyOptiScalerIniKey(_dir, "FSR", "UpscalerIndex", "0");
            Assert.Equal("0", ValueOf("FSR", "UpscalerIndex"));
            // The sibling key is untouched
            Assert.Equal("auto", ValueOf("FSR", "Fsr4Update"));
            // No duplicate key was appended
            var count = File.ReadAllLines(IniPath)
                .Count(l => l.Trim().StartsWith("UpscalerIndex=", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(1, count);
        }

        [Fact]
        public void InsertsKeyIntoExistingSection()
        {
            File.WriteAllText(IniPath, "[General]\nFoo=bar\n\n[FSR]\nFsr4Update=auto\n");
            GameInstallationService.ModifyOptiScalerIniKey(_dir, "FSR", "UpscalerIndex", "0");
            Assert.Equal("0", ValueOf("FSR", "UpscalerIndex"));
            Assert.Equal("bar", ValueOf("General", "Foo"));
        }

        [Fact]
        public void AppendsSectionWhenAbsentFromExistingFile()
        {
            File.WriteAllText(IniPath, "[General]\nFoo=bar\n");
            GameInstallationService.ModifyOptiScalerIniKey(_dir, "FSR", "Fsr4Update", "true");
            Assert.Equal("true", ValueOf("FSR", "Fsr4Update"));
            Assert.Equal("bar", ValueOf("General", "Foo"));
        }

        [Fact]
        public void DoesNotMatchKeyInDifferentSection()
        {
            // A key named UpscalerIndex under [Other] must not be changed when we target [FSR]
            File.WriteAllText(IniPath, "[Other]\nUpscalerIndex=99\n\n[FSR]\n");
            GameInstallationService.ModifyOptiScalerIniKey(_dir, "FSR", "UpscalerIndex", "0");
            Assert.Equal("99", ValueOf("Other", "UpscalerIndex"));
            Assert.Equal("0", ValueOf("FSR", "UpscalerIndex"));
        }
        // ── [Upscalers] selection: the key that actually decides which upscaler runs ──

        // Real comment blocks from the two OptiScaler naming eras.
        private const string LegacyUpscalersSection = @"[Upscalers]
; Select Upscaler for Dx11 games
; fsr22 (native DX11), fsr31 (native DX11), xess (native DX11, Arc only), xess_12 (dx11on12), fsr21_12 (dx11on12), fsr22_12 (dx11on12), fsr31_12 (dx11on12, FSR4), dlss - Default (auto) is fsr22
Dx11Upscaler=auto

; Select Upscaler for Dx12 games
; xess, fsr21, fsr22, fsr31 (also for FSR4), dlss - Default (auto) is xess
Dx12Upscaler=auto

; Select Upscaler for Vulkan games
; fsr21 (native VK), fsr22 (native VK), fsr31 (native VK), xess (native VK), fsr21_12 (VKon12), fsr31_12 (VKon12, FSR4), dlss - Default (auto) is fsr22
VulkanUpscaler=auto

[FSR]
Fsr4Update=auto
";

        private const string ModernUpscalersSection = @"[Upscalers]
; Select Upscaler for Dx11 games
; fsr22 (native DX11), fsr31 (native DX11), xess (native DX11, Arc only), xess_12 (dx11on12), fsr21_12 (dx11on12), fsr22_12 (dx11on12), ffx_12 (FSR 2.3; 3.1; 4.x), dlss - Default (auto) is fsr22
Dx11Upscaler=auto

; Select Upscaler for Dx12 games
; xess, fsr21, fsr22, ffx (FSR 2.3; 3.1; 4.x), dlss
; Default (auto) is DLSS when capable gpu, FSR4 when capable gpu, XeSS otherwise
Dx12Upscaler=auto

; Select Upscaler for Vulkan games
; fsr21 (native VK), fsr22 (native VK), ffx (native FSR 2.3; 3.1), xess (native VK), fsr21_12 (VKon12), ffx_12 (FSR 2.3; 3.1; 4.x), dlss - Default (auto) is fsr22
VulkanUpscaler=auto

[FSR]
Fsr4Update=auto
";

        [Fact]
        public void DetectsLegacyUpscalerCodes()
        {
            File.WriteAllText(IniPath, LegacyUpscalersSection);
            var codes = GameInstallationService.DetectFsr4UpscalerCodes(_dir);
            Assert.NotNull(codes);
            Assert.Equal("fsr31", codes!.Dx12);
            Assert.Equal("fsr31_12", codes.Dx11);
            Assert.Equal("fsr31_12", codes.Vulkan);
        }

        [Fact]
        public void DetectsModernUpscalerCodes()
        {
            File.WriteAllText(IniPath, ModernUpscalersSection);
            var codes = GameInstallationService.DetectFsr4UpscalerCodes(_dir);
            Assert.NotNull(codes);
            Assert.Equal("ffx", codes!.Dx12);
            Assert.Equal("ffx_12", codes.Dx11);
            Assert.Equal("ffx_12", codes.Vulkan);
        }

        [Fact]
        public void SelectingFsr4_WritesTheUpscalerThatActuallyRuns()
        {
            // The whole bug: without this key the DX12 default is XeSS, so every FSR
            // setting configures an upscaler that never runs.
            File.WriteAllText(IniPath, LegacyUpscalersSection);
            GameInstallationService.SelectFsr4Upscaler(_dir, true);
            Assert.Equal("fsr31", ValueOf("Upscalers", "Dx12Upscaler"));
        }

        [Fact]
        public void SelectingFsr4_NeverWritesALegacyCodeToAModernRelease()
        {
            // "fsr31" is not a DX12 option on modern builds — it falls through to
            // FSR 2.1.2, so writing it would silently downgrade the game.
            File.WriteAllText(IniPath, ModernUpscalersSection);
            GameInstallationService.SelectFsr4Upscaler(_dir, true);
            Assert.Equal("ffx", ValueOf("Upscalers", "Dx12Upscaler"));
        }

        [Fact]
        public void UndocumentedIni_LeavesUpscalerAlone_RatherThanGuessing()
        {
            // Writing a code this build does not accept is worse than doing nothing.
            File.WriteAllText(IniPath, "[Upscalers]\nDx12Upscaler=auto\n");
            GameInstallationService.SelectFsr4Upscaler(_dir, true);
            Assert.Equal("auto", ValueOf("Upscalers", "Dx12Upscaler"));
        }

        [Fact]
        public void NotSelectingFsr4_HandsTheChoiceBack()
        {
            File.WriteAllText(IniPath, LegacyUpscalersSection);
            GameInstallationService.SelectFsr4Upscaler(_dir, true);
            Assert.Equal("fsr31", ValueOf("Upscalers", "Dx12Upscaler"));

            // Reinstalling with the box unticked must not leave the forced value behind.
            GameInstallationService.SelectFsr4Upscaler(_dir, false);
            Assert.Equal("auto", ValueOf("Upscalers", "Dx12Upscaler"));
            Assert.Equal("auto", ValueOf("Upscalers", "Dx11Upscaler"));
            Assert.Equal("auto", ValueOf("Upscalers", "VulkanUpscaler"));
        }

        [Fact]
        public void OnlyTheUpscalersSectionDecidesTheCodes()
        {
            // "ffx" appears elsewhere in a real ini (EnableFfxInputs, FfxDx12Path...);
            // reading those would misdetect an old release as a new one.
            File.WriteAllText(IniPath, LegacyUpscalersSection + @"
[Inputs]
; OptiScaler will hook FidelityFX (amd_fidelityfx_dx12.dll) API Inputs
EnableFfxInputs=auto
; ffx ffx_12
UseFfxInputs=auto
");
            var codes = GameInstallationService.DetectFsr4UpscalerCodes(_dir);
            Assert.Equal("fsr31", codes!.Dx12);
        }

        // ── Unreal shipping-layout detection (Linux path separators) ─────────────

        [Theory]
        // The layout Unreal always ships, with the separators each OS actually produces.
        [InlineData("/games/MyGame/MyGame/Binaries/Win64/MyGame-Win64-Shipping.exe", true)]
        [InlineData(@"C:\games\MyGame\MyGame\Binaries\Win64\MyGame-Win64-Shipping.exe", true)]
        [InlineData("/games/MyGame/Phoenix/Binaries/Win64/Phoenix-Win64-Shipping.exe", true)]
        // Not the shipping layout.
        [InlineData("/games/MyGame/MyGame.exe", false)]
        [InlineData("/games/MyGame/Binaries/Win32/Game.exe", false)]
        [InlineData("/games/MyGame/Engine/Binaries/Win64/deeper/Tool.exe", false)]
        public void RecognisesUnrealShippingPaths_OnEitherSeparator(string exePath, bool expected)
        {
            // Guards a Linux-only failure: the old check matched the literal string
            // "Binaries\\Win64", which enumeration never produces on Linux, so every
            // Unreal heuristic was silently dead on this app's primary platform.
            var method = typeof(GameInstallationService).GetMethod(
                "IsUnrealShippingPath",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);
            Assert.Equal(expected, (bool)method!.Invoke(null, new object[] { exePath })!);
        }

    }
}
