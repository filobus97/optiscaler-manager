// Upscaler Manager - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System;
using System.IO;
using System.Linq;
using UpscalerManager.Core.Components;
using UpscalerManager.Core.Services;
using Xunit;

namespace UpscalerManager.Core.Tests
{
    /// <summary>
    /// Choosing which upscaler OptiScaler runs.
    ///
    /// The codes and the per-API lists are OptiScaler's, taken from
    /// <c>UpscalerToCode</c> and <c>MenuCommon::AddDx12Backends</c> / <c>AddDx11Backends</c>
    /// / <c>AddVulkanBackends</c>. Writing a code a release does not understand does not
    /// fail loudly — it falls back to that release's default, which on DX12 is XeSS — so
    /// the interesting tests here are the ones about refusing to write.
    /// </summary>
    [Collection(AppDataCollection.Name)]
    public class UpscalerChoiceTests : IDisposable
    {
        private readonly ScopedAppData _appData = new();
        private readonly string _gameDir;

        public UpscalerChoiceTests()
        {
            _gameDir = Path.Combine(Path.GetTempPath(), "upsel-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_gameDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_gameDir, recursive: true); } catch { }
            _appData.Dispose();
        }

        /// <summary>An ini whose [Upscalers] comments document the modern code names.</summary>
        private void WriteModernIni() => File.WriteAllText(
            Path.Combine(_gameDir, "OptiScaler.ini"),
            """
            [Upscalers]
            ; Select upscaler for DirectX 11
            ; fsr22 | xess | fsr31 | fsr21_12 | fsr22_12 | xess_12 | ffx_12 | dlss
            Dx11Upscaler=auto
            ; Select upscaler for DirectX 12
            ; xess | fsr21 | fsr22 | ffx | dlss
            Dx12Upscaler=auto
            ; Select upscaler for Vulkan
            ; xess | fsr21 | fsr22 | ffx | dlss
            VulkanUpscaler=auto

            [FSR]
            ; Index of the upscaler reported by the FFX SDK
            UpscalerIndex=auto
            """);

        /// <summary>An older ini, before the FidelityFX codes were renamed.</summary>
        private void WriteLegacyIni() => File.WriteAllText(
            Path.Combine(_gameDir, "OptiScaler.ini"),
            """
            [Upscalers]
            ; xess | fsr21 | fsr22 | fsr31 | dlss
            Dx12Upscaler=auto
            ; xess_12 | fsr21_12 | fsr22_12 | fsr31_12 | dlss
            Dx11Upscaler=auto
            ; xess | fsr21 | fsr22 | fsr31 | dlss
            VulkanUpscaler=auto

            [FSR]
            ; Upgrade FSR 3.x to FSR 4
            Fsr4Update=auto
            """);

        private string? Read(string section, string key)
        {
            var path = Path.Combine(_gameDir, "OptiScaler.ini");
            if (!File.Exists(path)) return null;

            var inSection = false;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("["))
                {
                    inSection = line.Equals($"[{section}]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inSection || line.StartsWith(";")) continue;

                var parts = line.Split('=', 2);
                if (parts.Length == 2 && parts[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return parts[1].Trim();
            }
            return null;
        }

        // ── The catalogue matches OptiScaler's own lists ─────────────────────

        [Fact]
        public void EveryChoiceUsesACodeOptiScalerDefines()
        {
            // From UpscalerToCode: these are the only strings it maps back to a backend.
            var known = new[]
            {
                "auto", "xess", "xess_12", "fsr21", "fsr21_12", "fsr22", "fsr22_12",
                "fsr31", "fsr31_12", "ffx", "ffx_12", "dlss", "dlssd",
            };

            foreach (var choice in UpscalerChoice.All)
            foreach (var codes in new[] { choice.Codes, choice.LegacyCodes })
            foreach (var code in new[] { codes?.Dx12, codes?.Dx11, codes?.Vulkan })
                if (code is { Length: > 0 })
                    Assert.Contains(code, known);
        }

        [Fact]
        public void TheDx12CodesAreOnesTheOverlayAlsoOffers()
        {
            // AddDx12Backends: xess, fsr21, fsr22, ffx, dlss. Offering anything else on
            // DX12 would be a combination OptiScaler's own picker will not show.
            var dx12 = new[] { "auto", "xess", "fsr21", "fsr22", "ffx", "dlss" };
            foreach (var choice in UpscalerChoice.All)
                Assert.Contains(choice.Codes.Dx12!, dx12);
        }

        [Fact]
        public void Dx11UsesTheCompatibilityLayerRatherThanNativeXeSS()
        {
            // OptiScaler hides native DX11 XeSS unless the GPU is Intel. Going through
            // its DX12 compatibility layer avoids an option that would vanish on most
            // hardware.
            Assert.Equal("xess_12", UpscalerChoice.For("xess")!.Codes.Dx11);
        }

        [Fact]
        public void OnlyTheFidelityFxFamilyHasARenamedSpelling()
        {
            // fsr31/fsr31_12 became ffx/ffx_12; nothing else was renamed, so nothing
            // else needs a legacy variant.
            foreach (var choice in UpscalerChoice.All)
                if (choice.Id != UpscalerChoice.FidelityFxId)
                    Assert.Null(choice.LegacyCodes);

            Assert.Equal("fsr31", UpscalerChoice.For(UpscalerChoice.FidelityFxId)!.LegacyCodes!.Dx12);
        }

        [Fact]
        public void DlssIsOfferedOnNvidiaAndWithheldElsewhere()
        {
            // The same gate OptiScaler applies in its own picker.
            Assert.Contains(UpscalerChoice.AvailableFor("Nvidia"), c => c.Id == "dlss");
            Assert.DoesNotContain(UpscalerChoice.AvailableFor("AMD"), c => c.Id == "dlss");
            Assert.DoesNotContain(UpscalerChoice.AvailableFor("Intel"), c => c.Id == "dlss");

            // Unknown GPU offers everything: guessing a user out of the option they came
            // for is worse than letting the overlay refuse it.
            Assert.Contains(UpscalerChoice.AvailableFor(null), c => c.Id == "dlss");
            Assert.Contains(UpscalerChoice.AvailableFor(""), c => c.Id == "dlss");
        }

        [Fact]
        public void EveryOtherChoiceIsOfferedOnEveryGpu()
        {
            foreach (var vendor in new[] { "Nvidia", "AMD", "Intel" })
            foreach (var choice in UpscalerChoice.All.Where(c => c.RequiresGpuVendor is null))
                Assert.Contains(UpscalerChoice.AvailableFor(vendor), c => c.Id == choice.Id);
        }

        // ── Writing it ──────────────────────────────────────────────────────

        [Fact]
        public void ChoosingDlssWritesDlssForEveryApi()
        {
            WriteModernIni();
            GameInstallationService.SelectUpscaler(_gameDir, UpscalerChoice.For("dlss")!, null);

            Assert.Equal("dlss", Read("Upscalers", "Dx12Upscaler"));
            Assert.Equal("dlss", Read("Upscalers", "Dx11Upscaler"));
            Assert.Equal("dlss", Read("Upscalers", "VulkanUpscaler"));
        }

        [Fact]
        public void ChoosingXeSSUsesThePerApiCodes()
        {
            WriteModernIni();
            GameInstallationService.SelectUpscaler(_gameDir, UpscalerChoice.For("xess")!, null);

            Assert.Equal("xess", Read("Upscalers", "Dx12Upscaler"));
            Assert.Equal("xess_12", Read("Upscalers", "Dx11Upscaler"));
            Assert.Equal("xess", Read("Upscalers", "VulkanUpscaler"));
        }

        [Fact]
        public void ARenamedReleaseGetsTheSpellingItUnderstands()
        {
            WriteLegacyIni();
            GameInstallationService.SelectUpscaler(
                _gameDir, UpscalerChoice.For(UpscalerChoice.FidelityFxId)!, 0);

            // This release documents fsr31, not ffx. Writing "ffx" here would fall back
            // to its default — XeSS on DX12 — and a user who asked for FSR would get it.
            Assert.Equal("fsr31", Read("Upscalers", "Dx12Upscaler"));
            Assert.Equal("fsr31_12", Read("Upscalers", "Dx11Upscaler"));
        }

        [Fact]
        public void AModernReleaseGetsTheModernSpelling()
        {
            WriteModernIni();
            GameInstallationService.SelectUpscaler(
                _gameDir, UpscalerChoice.For(UpscalerChoice.FidelityFxId)!, 0);

            Assert.Equal("ffx", Read("Upscalers", "Dx12Upscaler"));
            Assert.Equal("ffx_12", Read("Upscalers", "Dx11Upscaler"));
        }

        [Fact]
        public void NothingIsWrittenWhenTheReleaseDocumentsNeitherSpelling()
        {
            // An ini with no [Upscalers] guidance at all. Leaving OptiScaler's own
            // default alone beats installing a value that silently downgrades.
            File.WriteAllText(Path.Combine(_gameDir, "OptiScaler.ini"), "[General]\nSomething=1\n");

            GameInstallationService.SelectUpscaler(_gameDir, UpscalerChoice.For("dlss")!, null);
            Assert.Null(Read("Upscalers", "Dx12Upscaler"));
        }

        [Fact]
        public void AutoHandsTheChoiceBack()
        {
            WriteModernIni();
            GameInstallationService.SelectUpscaler(_gameDir, UpscalerChoice.For("dlss")!, null);
            GameInstallationService.SelectUpscaler(_gameDir, UpscalerChoice.Auto, null);

            // Explicitly "auto" rather than left at the previous install's value.
            Assert.Equal("auto", Read("Upscalers", "Dx12Upscaler"));
            Assert.Equal("auto", Read("Upscalers", "Dx11Upscaler"));
            Assert.Equal("auto", Read("Upscalers", "VulkanUpscaler"));
        }

        // ── The FidelityFX provider index ───────────────────────────────────

        [Fact]
        public void TheProviderIndexIsOnlyWrittenForTheFidelityFxFamily()
        {
            WriteModernIni();

            // A number passed with any other choice is meaningless: UpscalerIndex only
            // applies to the FFX backend.
            GameInstallationService.SelectUpscaler(_gameDir, UpscalerChoice.For("dlss")!, 2);
            Assert.Equal("auto", Read("FSR", "UpscalerIndex"));

            GameInstallationService.SelectUpscaler(
                _gameDir, UpscalerChoice.For(UpscalerChoice.FidelityFxId)!, 2);
            Assert.Equal("2", Read("FSR", "UpscalerIndex"));
        }

        [Fact]
        public void NoIndexLeavesTheKeyAlone()
        {
            WriteModernIni();
            GameInstallationService.SelectUpscaler(
                _gameDir, UpscalerChoice.For(UpscalerChoice.FidelityFxId)!, null);

            Assert.Equal("auto", Read("FSR", "UpscalerIndex"));
        }

        [Fact]
        public void SelectingFsr4MeansTheNewestProvider()
        {
            // What the old "pre-enable FSR 4" switch did, now expressed once: the
            // FidelityFX family at index 0, which AMD guarantees is the newest provider
            // because it sorts the list it reports newest-first.
            WriteModernIni();
            GameInstallationService.SelectFsr4Upscaler(_gameDir, true);

            Assert.Equal("ffx", Read("Upscalers", "Dx12Upscaler"));
            Assert.Equal("0", Read("FSR", "UpscalerIndex"));
            Assert.Equal(0, UpscalerSelection.NewestFsr.FfxProviderIndex);
        }

        // ── The key that no longer exists ───────────────────────────────────

        [Fact]
        public void TheLegacyFsr4UpdateKeyIsOnlyWrittenWhereTheReleaseHasIt()
        {
            // [FSR] Fsr4Update is gone from current OptiScaler entirely — no reference
            // anywhere in its source, replaced by [FSR] UpscalerIndex. Writing it
            // unconditionally left an inert key in the config and a log line claiming
            // something had been engaged.
            WriteLegacyIni();
            GameInstallationService.SelectLegacyFsr4Update(_gameDir);
            Assert.Equal("true", Read("FSR", "Fsr4Update"));

            Directory.Delete(_gameDir, true);
            Directory.CreateDirectory(_gameDir);
            WriteModernIni();
            GameInstallationService.SelectLegacyFsr4Update(_gameDir);
            Assert.Null(Read("FSR", "Fsr4Update"));
        }

        [Fact]
        public void AKeyIsDetectedFromTheReleasesOwnConfig()
        {
            WriteModernIni();
            Assert.True(GameInstallationService.IniDocumentsKey(_gameDir, "FSR", "UpscalerIndex"));
            Assert.False(GameInstallationService.IniDocumentsKey(_gameDir, "FSR", "Fsr4Update"));

            // A key named in another section does not count.
            Assert.False(GameInstallationService.IniDocumentsKey(_gameDir, "Upscalers", "UpscalerIndex"));
        }

        [Fact]
        public void NoIniMeansNothingIsClaimedEitherWay()
        {
            Assert.Empty(GameInstallationService.DocumentedUpscalerCodes(_gameDir));
            Assert.False(GameInstallationService.IniDocumentsKey(_gameDir, "FSR", "UpscalerIndex"));
        }

        // ── The selection record ────────────────────────────────────────────

        [Fact]
        public void AnUnknownChoiceIdFallsBackToAuto()
        {
            // Config is persisted text and can be hand-edited or written by a future
            // version; an unrecognised id must not throw while rendering the page.
            var selection = new UpscalerSelection("no-such-upscaler", 3);
            Assert.Equal(UpscalerChoice.AutoId, selection.Choice.Id);
        }

        [Fact]
        public void TheSelectionKnowsWhenTheVersionMatters()
        {
            Assert.True(UpscalerSelection.NewestFsr.IsFidelityFx);
            Assert.False(UpscalerSelection.NewestFsr.IsAuto);

            Assert.True(UpscalerSelection.Auto.IsAuto);
            Assert.False(UpscalerSelection.Auto.IsFidelityFx);

            Assert.False(new UpscalerSelection("dlss", null).IsFidelityFx);
        }
    }
}
