// OptiScaler Manager - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System.Linq;
using OptiscalerManager.Core.Components;
using Xunit;

namespace OptiscalerManager.Core.Tests
{
    /// <summary>
    /// The registry is the single source of truth for mutual exclusion and the
    /// "what will happen" preview, so these pin the derived behaviour.
    /// </summary>
    public class ComponentRegistryTests
    {
        [Fact]
        public void CustomSdkAndExtras_ShareUpscalerFile_AreMutuallyExclusive()
        {
            // Both write amd_fidelityfx_upscaler_dx12.dll — the conflict must be
            // derived from the shared target file, not hard-coded per screen.
            Assert.True(ComponentRegistry.AreMutuallyExclusive(
                ComponentIds.CustomFsrSdk, ComponentIds.Fsr4Extras));
        }

        [Fact]
        public void CustomFsr4Dll_AndExtras_DoNotShareFiles_NotExclusive()
        {
            // amdxcffx64.dll vs amd_fidelityfx_upscaler_dx12.dll — different files.
            Assert.False(ComponentRegistry.AreMutuallyExclusive(
                ComponentIds.CustomFsr4Dll, ComponentIds.Fsr4Extras));
        }

        [Fact]
        public void ConflictsFor_CustomSdk_IncludesExtras()
        {
            Assert.Contains(ComponentIds.Fsr4Extras,
                ComponentRegistry.ConflictsFor(ComponentIds.CustomFsrSdk));
        }

        [Fact]
        public void Preview_ResolvesInjectionDll_AndListsIniKeys()
        {
            var preview = ComponentRegistry.BuildPreview(
                new[] { ComponentIds.OptiScaler, ComponentIds.Fsr4Extras }, injectionDll: "winmm.dll");

            Assert.Contains("winmm.dll", preview.Files);
            Assert.Contains("OptiScaler.ini", preview.Files);
            Assert.Contains("amd_fidelityfx_upscaler_dx12.dll", preview.Files);
            Assert.DoesNotContain("{injection}", preview.Files);

            Assert.Contains(preview.IniKeys, k => k.Section == "FSR" && k.Key == "UpscalerIndex" && k.Value == "0");
            Assert.Contains(preview.IniKeys, k => k.Section == "FSR" && k.Key == "Fsr4Update" && k.Value == "true");
            Assert.Empty(preview.Conflicts);
        }

        [Fact]
        public void Preview_DefaultInjectionDll_IsDxgi()
        {
            var preview = ComponentRegistry.BuildPreview(new[] { ComponentIds.OptiScaler });
            Assert.Contains("dxgi.dll", preview.Files);
        }

        [Fact]
        public void Preview_WithConflictingPair_ReportsConflict()
        {
            var preview = ComponentRegistry.BuildPreview(
                new[] { ComponentIds.CustomFsrSdk, ComponentIds.Fsr4Extras });
            Assert.NotEmpty(preview.Conflicts);
        }

        [Fact]
        public void Preview_DeduplicatesSharedIniKeys()
        {
            // Extras and the custom FSR4 DLL both set [FSR] UpscalerIndex — the
            // preview should list it once.
            var preview = ComponentRegistry.BuildPreview(
                new[] { ComponentIds.CustomFsr4Dll, ComponentIds.OptiScaler });
            Assert.Single(preview.IniKeys, k => k.Section == "FSR" && k.Key == "UpscalerIndex");
        }

        [Fact]
        public void AllComponents_ExceptCore_RequireOptiScaler()
        {
            foreach (var def in ComponentRegistry.All.Where(d => d.Id != ComponentIds.OptiScaler))
                Assert.Contains(ComponentIds.OptiScaler, def.Requires);
        }

        [Theory]
        [InlineData(Fsr4Backend.None, null)]
        [InlineData(Fsr4Backend.LatestAmdSdk, ComponentIds.Fsr4AmdSdk)]
        [InlineData(Fsr4Backend.Int8Community, ComponentIds.Fsr4Extras)]
        [InlineData(Fsr4Backend.CustomSdk, ComponentIds.CustomFsrSdk)]
        [InlineData(Fsr4Backend.CustomDll, ComponentIds.CustomFsr4Dll)]
        public void ComponentIdFor_MapsBackend(Fsr4Backend backend, string? expectedId)
            => Assert.Equal(expectedId, ComponentRegistry.ComponentIdFor(backend));

        [Fact]
        public void AmdSdk_And_Int8_ShareUpscaler_AreMutuallyExclusive()
            => Assert.True(ComponentRegistry.AreMutuallyExclusive(
                ComponentIds.Fsr4AmdSdk, ComponentIds.Fsr4Extras));

        [Fact]
        public void InstallPreview_AmdSdk_HasFullDllSet()
        {
            var preview = ComponentRegistry.BuildInstallPreview(Fsr4Backend.LatestAmdSdk);
            Assert.Contains("amd_fidelityfx_upscaler_dx12.dll", preview.Files);
            Assert.Contains("amd_fidelityfx_framegeneration_dx12.dll", preview.Files);
            Assert.Contains("amd_fidelityfx_dx12.dll", preview.Files);      // loader
            Assert.Contains("amd_fidelityfx_denoiser_dx12.dll", preview.Files);
            Assert.Contains(preview.IniKeys, k => k.Section == "FSR" && k.Key == "UpscalerIndex" && k.Value == "0");
        }

        [Fact]
        public void InstallPreview_None_IsCoreOnly_NoFsrKeys()
        {
            var preview = ComponentRegistry.BuildInstallPreview(Fsr4Backend.None);
            Assert.Contains("dxgi.dll", preview.Files);
            Assert.DoesNotContain("amd_fidelityfx_upscaler_dx12.dll", preview.Files);
            Assert.DoesNotContain("amdxcffx64.dll", preview.Files);
            Assert.DoesNotContain(preview.IniKeys, k => k.Section == "FSR" && k.Key == "UpscalerIndex");
        }

        [Fact]
        public void InstallPreview_Int8Community_HasUpscalerAndFsrKeys()
        {
            var preview = ComponentRegistry.BuildInstallPreview(Fsr4Backend.Int8Community);
            Assert.Contains("amd_fidelityfx_upscaler_dx12.dll", preview.Files);
            Assert.Contains(preview.IniKeys, k => k.Section == "FSR" && k.Key == "UpscalerIndex" && k.Value == "0");
        }

        [Fact]
        public void InstallPreview_CustomDll_HasAmdxcffx64()
        {
            var preview = ComponentRegistry.BuildInstallPreview(Fsr4Backend.CustomDll);
            Assert.Contains("amdxcffx64.dll", preview.Files);
        }

        [Fact]
        public void InstallPreview_ProfileKeysMerged_ButFsrEnableKeysWin()
        {
            // A profile that would set UpscalerIndex to something else plus an unrelated key.
            var profileKeys = new[]
            {
                new IniKeyChange("FSR", "UpscalerIndex", "7"),
                new IniKeyChange("Spoofing", "Dxgi", "true"),
            };
            var preview = ComponentRegistry.BuildInstallPreview(
                Fsr4Backend.Int8Community, injectionDll: null, profileKeys: profileKeys);

            // The unrelated profile key survives.
            Assert.Contains(preview.IniKeys, k => k.Section == "Spoofing" && k.Key == "Dxgi" && k.Value == "true");
            // The FSR enable key wins over the profile's value (applied last, as on disk).
            Assert.Single(preview.IniKeys, k => k.Section == "FSR" && k.Key == "UpscalerIndex");
            Assert.Contains(preview.IniKeys, k => k.Section == "FSR" && k.Key == "UpscalerIndex" && k.Value == "0");
        }

        [Fact]
        public void InstallPreview_None_KeepsProfileUpscalerIndex()
        {
            // With no backend, the profile's own [FSR] value is not overridden.
            var profileKeys = new[] { new IniKeyChange("FSR", "UpscalerIndex", "7") };
            var preview = ComponentRegistry.BuildInstallPreview(
                Fsr4Backend.None, injectionDll: null, profileKeys: profileKeys);
            Assert.Contains(preview.IniKeys, k => k.Section == "FSR" && k.Key == "UpscalerIndex" && k.Value == "7");
        }
    }
}
