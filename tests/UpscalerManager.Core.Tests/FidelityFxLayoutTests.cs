// Upscaler Manager - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System.Linq;
using UpscalerManager.Core.Components;
using Xunit;

namespace UpscalerManager.Core.Tests
{
    /// <summary>
    /// Telling AMD's two FidelityFX generations apart.
    ///
    /// The numbers below are not invented. They were read out of AMD's own signed
    /// binaries in FidelityFX-SDK at tag v2.3.0 — whose release notes say it contains
    /// FSR Upscaling 4.1.1 — and out of the nine amd_fidelityfx_dx12.dll builds the
    /// DLSS Swapper archive holds, which are all SDK 1.
    /// </summary>
    public class FidelityFxLayoutTests
    {
        // ── The real SDK 2.3.0 module set ───────────────────────────────────

        [Fact]
        public void TheSdk2ModulesAreIdentifiedByName()
        {
            Assert.Equal(FidelityFxRole.Loader,
                FidelityFxLayout.Identify("amd_fidelityfx_loader_dx12.dll", "2.3.0.2740", null));
            Assert.Equal(FidelityFxRole.Upscaler,
                FidelityFxLayout.Identify("amd_fidelityfx_upscaler_dx12.dll", "4.1.1.2740", "4.1.1"));
            Assert.Equal(FidelityFxRole.FrameGeneration,
                FidelityFxLayout.Identify("amd_fidelityfx_framegeneration_dx12.dll", "4.0.1.2740", "4.0.1"));
            Assert.Equal(FidelityFxRole.Denoiser,
                FidelityFxLayout.Identify("amd_fidelityfx_denoiser_dx12.dll", "1.2.0.2740", null));
            Assert.Equal(FidelityFxRole.RadianceCache,
                FidelityFxLayout.Identify("amd_fidelityfx_radiancecache_dx12.dll", "0.9.0.2740", null));
        }

        [Fact]
        public void AnSdk1MonolithIsToldApartFromAnSdk2LoaderUnderTheSameName()
        {
            // This is the whole point. AMD made the SDK 2 loader "interface- and
            // behavior-compatible with amd_fidelityfx_dx12.dll", so a game migrating
            // keeps the old filename — and one name now covers two incompatible things.

            // SDK 1: version 1.0.x, and the upscaler is inside it, so it has a provider
            // table topping out in FSR 3's range.
            Assert.Equal(FidelityFxRole.Monolith,
                FidelityFxLayout.Identify("amd_fidelityfx_dx12.dll", "1.0.1.38338", "3.1.2"));

            // SDK 2 loader under the legacy name: the SDK's version, and no effect code
            // at all — which is exactly what makes the provider table empty.
            Assert.Equal(FidelityFxRole.Loader,
                FidelityFxLayout.Identify("amd_fidelityfx_dx12.dll", "2.3.0.2740", null));
        }

        [Fact]
        public void AnEffectModuleUnderTheLegacyNameIsStillAnEffectModule()
        {
            // Community and OptiScaler drops do rename these. A 4.x provider table is
            // an upscaler whatever the file is called, and must not be taken for a
            // runtime just because of its name.
            Assert.Equal(FidelityFxRole.Upscaler,
                FidelityFxLayout.Identify("amd_fidelityfx_dx12.dll", "4.0.2.45326", "4.0.2"));
        }

        [Fact]
        public void AnUnknownVersionFallsBackToTheOlderLayout()
        {
            // The conservative answer, because it is the one that gets a
            // cross-generation swap refused rather than allowed.
            Assert.Equal(FidelityFxRole.Monolith,
                FidelityFxLayout.Identify("amd_fidelityfx_dx12.dll", null, null));
            Assert.Equal(FidelityFxRole.Monolith,
                FidelityFxLayout.Identify("amd_fidelityfx_dx12.dll", "not a version", null));
        }

        [Fact]
        public void FilesOutsideTheFidelityFxFamilyAreLeftAlone()
        {
            foreach (var name in new[] { "nvngx_dlss.dll", "libxess.dll", "amdxcffx64.dll", null })
                Assert.Equal(FidelityFxRole.NotFidelityFx,
                    FidelityFxLayout.Identify(name, "1.0.0.0", null));
        }

        [Fact]
        public void NamesAreMatchedCaseInsensitively()
        {
            Assert.Equal(FidelityFxRole.Upscaler,
                FidelityFxLayout.Identify("AMD_FidelityFX_Upscaler_DX12.DLL", "4.1.1.2740", null));
        }

        // ── How each role is written ────────────────────────────────────────

        [Fact]
        public void TheLoaderIsNotPresentedAsAnFsrVersion()
        {
            // 2.3.0 is an SDK version. Shown bare it reads as an FSR version two
            // generations behind the 4.1.1 module sitting next to it in the same folder,
            // which is precisely the confusion this exists to prevent.
            var text = FidelityFxLayout.Describe("amd_fidelityfx_loader_dx12.dll", "2.3.0.2740", null);

            Assert.Equal("FidelityFX SDK 2.3.0  (loader only)", text);
            Assert.DoesNotContain("FSR", text);
        }

        [Fact]
        public void AnSdk2LoaderUnderTheLegacyNameReadsTheSameWay()
        {
            Assert.Equal("FidelityFX SDK 2.3.0  (loader only)",
                FidelityFxLayout.Describe("amd_fidelityfx_dx12.dll", "2.3.0.2740", null));
        }

        [Fact]
        public void AnSdk1MonolithShowsItsFsrVersionAndItsBuildNumber()
        {
            Assert.Equal("FSR 3.1.2  (1.0.1.38338)",
                FidelityFxLayout.Describe("amd_fidelityfx_dx12.dll", "1.0.1.38338", "3.1.2"));
        }

        [Fact]
        public void AnEffectModulesFileVersionIsItsEffectVersion()
        {
            // AMD stamps the upscaler that provides FSR 4.1.1 as 4.1.1.2740, so there is
            // nothing to translate — only the shared SDK build number to drop.
            Assert.Equal("FSR Upscaling 4.1.1",
                FidelityFxLayout.Describe("amd_fidelityfx_upscaler_dx12.dll", "4.1.1.2740", "4.1.1"));
            Assert.Equal("FSR Frame Generation 4.0.1",
                FidelityFxLayout.Describe("amd_fidelityfx_framegeneration_dx12.dll", "4.0.1.2740", "4.0.1"));
            Assert.Equal("FidelityFX denoiser 1.2.0",
                FidelityFxLayout.Describe("amd_fidelityfx_denoiser_dx12.dll", "1.2.0.2740", null));
            Assert.Equal("FidelityFX radiance cache 0.9.0",
                FidelityFxLayout.Describe("amd_fidelityfx_radiancecache_dx12.dll", "0.9.0.2740", null));
        }

        [Fact]
        public void TheSharedSdkBuildNumberIsDroppedBecauseItSaysNothing()
        {
            // Every module in SDK 2.3.0 ends in .2740. Keeping it makes four different
            // versions on one page look like the same number.
            Assert.DoesNotContain("2740",
                FidelityFxLayout.Describe("amd_fidelityfx_upscaler_dx12.dll", "4.1.1.2740", null));
        }

        [Fact]
        public void OtherFilesAreDescribedByTheirPlainVersion()
        {
            Assert.Equal("310.9.1.0", FidelityFxLayout.Describe("nvngx_dlss.dll", "310.9.1.0", null));
            Assert.Equal("unknown version", FidelityFxLayout.Describe("libxess.dll", null, null));
        }

        // ── Every module is swappable and catalogued ────────────────────────

        [Fact]
        public void EveryFidelityFxRoleMapsToAFileWeStillDescribe()
        {
            foreach (var name in new[]
            {
                "amd_fidelityfx_dx12.dll",
                "amd_fidelityfx_vk.dll",
                "amd_fidelityfx_loader_dx12.dll",
                "amd_fidelityfx_upscaler_dx12.dll",
                "amd_fidelityfx_framegeneration_dx12.dll",
                "amd_fidelityfx_denoiser_dx12.dll",
                "amd_fidelityfx_radiancecache_dx12.dll",
            })
            {
                Assert.NotNull(UpscalerCatalog.For(name));
                Assert.NotNull(SwappableDlls.For(name));   // described, for revert and labelling
                Assert.NotEqual(FidelityFxRole.NotFidelityFx,
                    FidelityFxLayout.Identify(name, "2.3.0.2740", null));
            }
        }

        // ── No longer swapped, still revertible ─────────────────────────────

        [Fact]
        public void NoFidelityFxFileIsOfferedForSwappingAnyMore()
        {
            // The decision this release implements. Swapping is DLSS and XeSS; AMD's
            // files changed shape between SDK generations, and FSR 4 is not in any file
            // most games ship, so OptiScaler is the route for FSR.
            foreach (var name in new[]
            {
                "amd_fidelityfx_dx12.dll",
                "amd_fidelityfx_vk.dll",
                "amd_fidelityfx_loader_dx12.dll",
                "amd_fidelityfx_upscaler_dx12.dll",
                "amd_fidelityfx_framegeneration_dx12.dll",
                "amd_fidelityfx_denoiser_dx12.dll",
                "amd_fidelityfx_radiancecache_dx12.dll",
                "amdxcffx64.dll",
            })
            {
                Assert.False(SwappableDlls.IsSwappable(name), $"{name} is still offered for swapping");
                Assert.True(SwappableDlls.IsRetired(name), $"{name} is not retired, so an existing swap could not be undone");

                // Still described, so a row can explain itself and a revert can name it.
                Assert.NotNull(SwappableDlls.For(name));
                Assert.NotNull(UpscalerCatalog.For(name));
            }
        }

        [Fact]
        public void WhatIsLeftIsExactlyDlssAndXeSS()
        {
            Assert.Equal(
                new[]
                {
                    "libxell.dll", "libxess.dll", "libxess_dx11.dll", "libxess_fg.dll",
                    "nvngx_dlss.dll", "nvngx_dlssd.dll", "nvngx_dlssg.dll",
                },
                SwappableDlls.All.Select(d => d.FileName).OrderBy(n => n, System.StringComparer.Ordinal));
        }

        [Fact]
        public void AnOfferedFileIsNeverAlsoRetired()
        {
            foreach (var offered in SwappableDlls.All)
                Assert.False(SwappableDlls.IsRetired(offered.FileName));

            foreach (var retired in SwappableDlls.Retired)
                Assert.False(SwappableDlls.IsSwappable(retired.FileName));
        }

        [Fact]
        public void EveryOfferedFileStillHasItsOwnPlaceInTheOrdering()
        {
            var orders = SwappableDlls.All.Select(d => SwappableDlls.DisplayOrder(d.FileName)).ToList();
            Assert.Equal(orders.Count, orders.Distinct().Count());
        }

        [Fact]
        public void NoDownloadSourceOffersAFileWeNoLongerSwap()
        {
            // A source pointing at a retired file would fetch something, import it, and
            // then have nowhere to install it.
            foreach (var source in VendorDllSource.All)
                Assert.True(SwappableDlls.IsSwappable(source.FileName),
                    $"{source.FileName} is a vendor download but is no longer swappable");

            foreach (var file in DllRepository.All)
                Assert.True(SwappableDlls.IsSwappable(file.FileName),
                    $"{file.FileName} is an archive download but is no longer swappable");
        }

        [Fact]
        public void NeitherTheArchiveNorAnyVendorCoversFidelityFxNow()
        {
            foreach (var name in new[] { "amd_fidelityfx_dx12.dll", "amd_fidelityfx_vk.dll",
                                         "amd_fidelityfx_upscaler_dx12.dll", "amdxcffx64.dll" })
            {
                Assert.False(DllRepository.Covers(name));
                Assert.Null(VendorDllSource.For(name));
            }
        }

    }
}
