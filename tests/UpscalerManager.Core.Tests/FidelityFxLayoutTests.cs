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

        // ── Refusing to mix generations ─────────────────────────────────────

        [Fact]
        public void AMonolithAndALoaderAreNotInterchangeable()
        {
            // The archive holds nine SDK 1 builds of amd_fidelityfx_dx12.dll. Installing
            // one into a game running an SDK 2 loader leaves SDK 1 code — which AMD has
            // deprecated — with the game's separate SDK 2 modules it cannot drive.
            Assert.False(FidelityFxLayout.Interchangeable(FidelityFxRole.Loader, FidelityFxRole.Monolith));
            Assert.False(FidelityFxLayout.Interchangeable(FidelityFxRole.Monolith, FidelityFxRole.Loader));
        }

        [Fact]
        public void AnEffectModuleCannotStandInForARuntime()
        {
            Assert.False(FidelityFxLayout.Interchangeable(FidelityFxRole.Loader, FidelityFxRole.Upscaler));
            Assert.False(FidelityFxLayout.Interchangeable(FidelityFxRole.Upscaler, FidelityFxRole.FrameGeneration));
        }

        [Fact]
        public void TheSameRoleAlwaysIs()
        {
            foreach (var role in System.Enum.GetValues<FidelityFxRole>())
                Assert.True(FidelityFxLayout.Interchangeable(role, role));
        }

        [Fact]
        public void NonFidelityFxFilesAreNeverBlocked()
        {
            // Every other swappable DLL has exactly one meaning, so this check must not
            // become a way for them to be refused.
            Assert.True(FidelityFxLayout.Interchangeable(FidelityFxRole.NotFidelityFx, FidelityFxRole.Monolith));
            Assert.True(FidelityFxLayout.Interchangeable(FidelityFxRole.Upscaler, FidelityFxRole.NotFidelityFx));
        }

        [Fact]
        public void TheRefusalSaysWhatToDoInstead()
        {
            var message = FidelityFxLayout.ExplainMismatch(
                "amd_fidelityfx_dx12.dll", FidelityFxRole.Loader, FidelityFxRole.Monolith);

            // The actionable part: the FSR version lives in the upscaler module.
            Assert.Contains("amd_fidelityfx_upscaler_dx12.dll", message);
            Assert.Contains("SDK 2", message);
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
        public void EveryFidelityFxRoleMapsToAFileWeCanSwapAndDescribe()
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
                Assert.True(SwappableDlls.IsSwappable(name), $"{name} is not swappable");
                Assert.NotNull(UpscalerCatalog.For(name));
                Assert.NotEqual(FidelityFxRole.NotFidelityFx,
                    FidelityFxLayout.Identify(name, "2.3.0.2740", null));
            }
        }

        // ── AMD as a vendor source ──────────────────────────────────────────

        [Fact]
        public void AmdPublishesTheSdk2ModulesItself()
        {
            // This project previously asserted the opposite — that AMD did not publish
            // these loose and only OptiScaler's releases carried them. The signed
            // binaries are committed to FidelityFX-SDK under Kits/FidelityFX/signedbin,
            // which is what makes FSR 4 reachable from AMD rather than only from a
            // community rebuild.
            var upscaler = VendorDllSource.For("amd_fidelityfx_upscaler_dx12.dll");
            Assert.NotNull(upscaler);
            Assert.Equal("AMD", upscaler.Vendor);
            Assert.Contains("signedbin", upscaler.PathInRepo);
            Assert.True(upscaler.TagIsSdkVersion);
            Assert.Contains("MIT", upscaler.Licence);
        }

        [Fact]
        public void AmdIsNotOfferedForTheAmbiguousLegacyNames()
        {
            // amd_fidelityfx_dx12.dll does not exist in SDK 2 at all, and AMD no longer
            // publishes the SDK 1 monolith. Offering a download here could only mean
            // renaming a file, and a DLL's name is what the game loads.
            Assert.Null(VendorDllSource.For("amd_fidelityfx_dx12.dll"));
            Assert.Null(VendorDllSource.For("amd_fidelityfx_vk.dll"));
        }

        [Fact]
        public void OnlySdk2TagsAreOfferedForTheModules()
        {
            // Checked against the real repository: the modules appear from v2.0.0 and
            // are absent at v1.1.4, where the path 404s.
            var source = VendorDllSource.For("amd_fidelityfx_upscaler_dx12.dll")!;

            Assert.True(VendorDllSource.TagCanSupply(source, "v2.0.0"));
            Assert.True(VendorDllSource.TagCanSupply(source, "v2.3.0"));
            Assert.False(VendorDllSource.TagCanSupply(source, "v1.1.4"));
            Assert.False(VendorDllSource.TagCanSupply(source, "fsr3-v3.0.4"));

            // Nvidia's and Intel's tags name the file's own version, so none are filtered.
            var dlss = VendorDllSource.For("nvngx_dlss.dll")!;
            Assert.True(VendorDllSource.TagCanSupply(dlss, "v310.9.1"));
            Assert.True(VendorDllSource.TagCanSupply(dlss, "v1.0.0"));
        }

        [Fact]
        public void EveryAmdSourcePointsAtAFileWeCanSwap()
        {
            foreach (var source in VendorDllSource.All.Where(s => s.Vendor == "AMD"))
            {
                Assert.True(SwappableDlls.IsSwappable(source.FileName));
                Assert.EndsWith(source.FileName, source.PathInRepo);
            }
        }

        [Fact]
        public void TheModuleUrlIsBuiltAgainstTheSdkTag()
        {
            var source = VendorDllSource.For("amd_fidelityfx_upscaler_dx12.dll")!;
            Assert.Equal(
                "https://raw.githubusercontent.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/v2.3.0/"
                + "Kits/FidelityFX/signedbin/amd_fidelityfx_upscaler_dx12.dll",
                VendorDllSource.UrlFor(source, "v2.3.0"));
        }
    }
}
