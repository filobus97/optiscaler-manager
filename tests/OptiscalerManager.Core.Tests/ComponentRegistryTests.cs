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
        public void CustomMergedAndExtras_ShareUpscalerFile_AreMutuallyExclusive()
        {
            // Both write amd_fidelityfx_upscaler_dx12.dll — the conflict must be
            // derived from the shared target file, not hard-coded per screen.
            Assert.True(ComponentRegistry.AreMutuallyExclusive(
                ComponentIds.CustomMerged, ComponentIds.Fsr4Extras));
        }

        [Fact]
        public void ConflictsFor_CustomMerged_IncludesExtras()
        {
            Assert.Contains(ComponentIds.Fsr4Extras,
                ComponentRegistry.ConflictsFor(ComponentIds.CustomMerged));
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
                new[] { ComponentIds.CustomMerged, ComponentIds.Fsr4Extras });
            Assert.NotEmpty(preview.Conflicts);
        }

        [Fact]
        public void Preview_DeduplicatesSharedIniKeys()
        {
            var preview = ComponentRegistry.BuildPreview(
                new[] { ComponentIds.CustomMerged, ComponentIds.OptiScaler });
            Assert.Single(preview.IniKeys, k => k.Section == "FSR" && k.Key == "UpscalerIndex");
        }

        [Fact]
        public void AllComponents_ExceptCore_RequireOptiScaler()
        {
            foreach (var def in ComponentRegistry.All.Where(d => d.Id != ComponentIds.OptiScaler))
                Assert.Contains(ComponentIds.OptiScaler, def.Requires);
        }

        [Theory]
        [InlineData(Fsr4Backend.Default)]
        public void ComponentIdsFor_Default_IsEmpty(Fsr4Backend backend)
            => Assert.Empty(ComponentRegistry.ComponentIdsFor(backend));

        [Fact]
        public void ComponentIdsFor_MapsSingleBackends()
        {
            Assert.Equal(new[] { ComponentIds.Fsr4AmdSdk }, ComponentRegistry.ComponentIdsFor(Fsr4Backend.LatestAmdSdk));
            Assert.Equal(new[] { ComponentIds.Fsr4Extras }, ComponentRegistry.ComponentIdsFor(Fsr4Backend.Int8Community));
            Assert.Equal(new[] { ComponentIds.CustomMerged }, ComponentRegistry.ComponentIdsFor(Fsr4Backend.CustomMerged));
        }

        [Fact]
        public void AmdSdk_And_Int8_ShareUpscaler_AreMutuallyExclusive()
            => Assert.True(ComponentRegistry.AreMutuallyExclusive(
                ComponentIds.Fsr4AmdSdk, ComponentIds.Fsr4Extras));

        [Fact]
        public void InstallPreview_AmdSdk_HasFullDllSet()
        {
            var preview = ComponentRegistry.BuildInstallPreview(Fsr4Backend.LatestAmdSdk, selectFsr4: true);
            Assert.Contains("amd_fidelityfx_upscaler_dx12.dll", preview.Files);
            Assert.Contains("amd_fidelityfx_framegeneration_dx12.dll", preview.Files);
            Assert.Contains("amd_fidelityfx_dx12.dll", preview.Files);      // loader
            Assert.Contains("amd_fidelityfx_denoiser_dx12.dll", preview.Files);
        }

        [Fact]
        public void InstallPreview_Default_IsCoreOnly_ButStillForcesFsrAvailability()
        {
            var preview = ComponentRegistry.BuildInstallPreview(Fsr4Backend.Default, selectFsr4: true);
            Assert.Contains("dxgi.dll", preview.Files);
            Assert.DoesNotContain("amd_fidelityfx_upscaler_dx12.dll", preview.Files);
            Assert.DoesNotContain("amdxcffx64.dll", preview.Files);
            // Availability flags are forced regardless of backend.
            Assert.Contains(preview.IniKeys, k => k.Section == "FSR" && k.Key == "Fsr4Update" && k.Value == "true");
            Assert.Contains(preview.IniKeys, k => k.Section == "FSR" && k.Key == "UpscalerIndex" && k.Value == "0");
        }

        [Fact]
        public void InstallPreview_CustomMerged_AnnotatesOverwritesAndAdds()
        {
            var preview = ComponentRegistry.BuildInstallPreview(Fsr4Backend.CustomMerged, selectFsr4: true,
                customDlls: new[] { "amd_fidelityfx_upscaler_dx12.dll", "amdxcffx64.dll" });

            // A custom DLL colliding with a base file is marked as an overwrite…
            Assert.Contains(preview.Files, f => f.StartsWith("amd_fidelityfx_upscaler_dx12.dll") && f.Contains("overwrites"));
            // …and an unknown name is marked as added.
            Assert.Contains(preview.Files, f => f.StartsWith("amdxcffx64.dll") && f.Contains("added"));
            // The base loader stays listed unannotated.
            Assert.Contains("amd_fidelityfx_dx12.dll", preview.Files);
        }

        [Fact]
        public void InstallPreview_Fsr4UpdateAlwaysForced()
        {
            foreach (var b in new[] { Fsr4Backend.Default, Fsr4Backend.Int8Community, Fsr4Backend.LatestAmdSdk })
            {
                var p = ComponentRegistry.BuildInstallPreview(b, selectFsr4: false);
                Assert.Contains(p.IniKeys, k => k.Section == "FSR" && k.Key == "Fsr4Update" && k.Value == "true");
            }
        }

        [Theory]
        [InlineData(true, "0")]
        [InlineData(false, "auto")]
        public void InstallPreview_SelectFsr4_DrivesUpscalerIndex(bool select, string expected)
        {
            var preview = ComponentRegistry.BuildInstallPreview(Fsr4Backend.Int8Community, selectFsr4: select);
            Assert.Single(preview.IniKeys, k => k.Section == "FSR" && k.Key == "UpscalerIndex");
            Assert.Contains(preview.IniKeys, k => k.Section == "FSR" && k.Key == "UpscalerIndex" && k.Value == expected);
        }

        [Fact]
        public void InstallPreview_MenuKey_AddsShortcutKey_WhenSet()
        {
            var without = ComponentRegistry.BuildInstallPreview(Fsr4Backend.Int8Community, selectFsr4: true);
            Assert.DoesNotContain(without.IniKeys, k => k.Section == "Menu");

            var with = ComponentRegistry.BuildInstallPreview(Fsr4Backend.Int8Community, selectFsr4: true, menuKeyVk: "0x78");
            Assert.Contains(with.IniKeys, k => k.Section == "Menu" && k.Key == "ShortcutKey" && k.Value == "0x78");
        }
    }
}
