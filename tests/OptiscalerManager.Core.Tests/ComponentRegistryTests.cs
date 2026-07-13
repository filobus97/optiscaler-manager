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
    }
}
