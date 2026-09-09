// OptiScaler Manager - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System;
using System.IO;
using System.Linq;
using OptiscalerManager.Core.Components;
using OptiscalerManager.Core.Services;
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

            Assert.Contains(preview.IniKeys, k => k.Section == "Upscalers" && k.Key == "Dx12Upscaler");
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
            Assert.Single(preview.IniKeys, k => k.Section == "Upscalers" && k.Key == "Dx12Upscaler");
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
            Assert.Equal(new[] { ComponentIds.Fsr4Extras }, ComponentRegistry.ComponentIdsFor(Fsr4Backend.Int8Community));
            Assert.Equal(new[] { ComponentIds.CustomMerged }, ComponentRegistry.ComponentIdsFor(Fsr4Backend.CustomMerged));
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
            Assert.Contains(preview.IniKeys, k => k.Section == "Upscalers" && k.Key == "Dx12Upscaler");
        }

        [Fact]
        public void InstallPreview_CustomOverlay_ListsOnlyYourDlls_Annotated()
        {
            var preview = ComponentRegistry.BuildInstallPreview(Fsr4Backend.CustomMerged, selectFsr4: true,
                customDlls: new[] { "amd_fidelityfx_upscaler_dx12.dll", "amdxcffx64.dll" });

            // A custom DLL with an OptiScaler-shipped name swaps the bundled file…
            Assert.Contains(preview.Files, f => f.StartsWith("amd_fidelityfx_upscaler_dx12.dll") && f.Contains("swaps"));
            // …and an unknown name is marked as added.
            Assert.Contains(preview.Files, f => f.StartsWith("amdxcffx64.dll") && f.Contains("added"));
            // No phantom base files: only OptiScaler core + the overlay are written.
            Assert.DoesNotContain("amd_fidelityfx_dx12.dll", preview.Files);
            Assert.DoesNotContain("amd_fidelityfx_denoiser_dx12.dll", preview.Files);
        }

        [Fact]
        public void InstallPreview_Fsr4UpdateAlwaysForced()
        {
            foreach (var b in new[] { Fsr4Backend.Default, Fsr4Backend.Int8Community, Fsr4Backend.CustomMerged })
            {
                var p = ComponentRegistry.BuildInstallPreview(b, selectFsr4: false);
                Assert.Contains(p.IniKeys, k => k.Section == "FSR" && k.Key == "Fsr4Update" && k.Value == "true");
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void InstallPreview_SelectFsr4_DrivesTheUpscalerChoice(bool select)
        {
            // The preview must show the key that actually decides which upscaler runs.
            // UpscalerIndex never did: its default is already 0, so the "0" the Manager
            // used to write was indistinguishable from writing nothing.
            var preview = ComponentRegistry.BuildInstallPreview(Fsr4Backend.Int8Community, selectFsr4: select);
            var dx12 = Assert.Single(preview.IniKeys, k => k.Section == "Upscalers" && k.Key == "Dx12Upscaler");

            if (select)
                Assert.DoesNotContain("auto", dx12.Value);
            else
                Assert.Equal("auto", dx12.Value);

            Assert.Contains(preview.IniKeys, k => k.Section == "FSR" && k.Key == "UpscalerIndex" && k.Value == "auto");
        }

        [Fact]
        public void InstallPreview_MenuKey_AddsShortcutKey_WhenSet()
        {
            var without = ComponentRegistry.BuildInstallPreview(Fsr4Backend.Int8Community, selectFsr4: true);
            Assert.DoesNotContain(without.IniKeys, k => k.Section == "Menu");

            var with = ComponentRegistry.BuildInstallPreview(Fsr4Backend.Int8Community, selectFsr4: true, menuKeyVk: "0x78");
            Assert.Contains(with.IniKeys, k => k.Section == "Menu" && k.Key == "ShortcutKey" && k.Value == "0x78");
        }

        [Fact]
        public void InstallPreview_Addons_ListFilesAndFgInputKey()
        {
            var preview = ComponentRegistry.BuildInstallPreview(Fsr4Backend.Default, selectFsr4: true,
                addFakenvapi: true, addNukemFg: true);

            Assert.Contains("nvapi64.dll", preview.Files);
            Assert.Contains("fakenvapi.ini", preview.Files);
            Assert.Contains("dlssg_to_fsr3_amd_is_better.dll", preview.Files);
            // OptiScaler 0.9.x reads [FrameGen] FGInput (legacy FGType is dead).
            Assert.Contains(preview.IniKeys, k => k.Section == "FrameGen" && k.Key == "FGInput" && k.Value == "nukems");
        }

        [Fact]
        public void InstallPreview_NoAddons_OmitsAddonFilesAndKeys()
        {
            var preview = ComponentRegistry.BuildInstallPreview(Fsr4Backend.Default, selectFsr4: true);
            Assert.DoesNotContain("nvapi64.dll", preview.Files);
            Assert.DoesNotContain("dlssg_to_fsr3_amd_is_better.dll", preview.Files);
            Assert.DoesNotContain(preview.IniKeys, k => k.Section == "FrameGen");
            Assert.DoesNotContain(preview.IniKeys, k => k.Section == "Spoofing");
        }

        [Fact]
        public void InstallPreview_SpoofDxgi_ForcesDxgiKey_NoPluginBits()
        {
            var preview = ComponentRegistry.BuildInstallPreview(Fsr4Backend.Default, selectFsr4: true,
                spoofMethod: SpoofMethod.Dxgi);
            Assert.Contains(preview.IniKeys, k => k.Section == "Spoofing" && k.Key == "Dxgi" && k.Value == "true");
            Assert.DoesNotContain(preview.IniKeys, k => k.Section == "Plugins");
            Assert.DoesNotContain(preview.Files, f => f.Contains("OptiPatcher"));
        }

        [Fact]
        public void InstallPreview_SpoofOptiPatcher_AddsPluginAndKey_NoDxgiSpoof()
        {
            var preview = ComponentRegistry.BuildInstallPreview(Fsr4Backend.Default, selectFsr4: true,
                spoofMethod: SpoofMethod.OptiPatcher);
            Assert.Contains(preview.Files, f => f.Contains("OptiPatcher.asi"));
            Assert.Contains(preview.IniKeys, k => k.Section == "Plugins" && k.Key == "LoadAsiPlugins" && k.Value == "true");
            Assert.DoesNotContain(preview.IniKeys, k => k.Section == "Spoofing");
        }

        [Fact]
        public void InstallPreview_Fsr4Toggles_ForceIniKeys()
        {
            var preview = ComponentRegistry.BuildInstallPreview(Fsr4Backend.Default, selectFsr4: true,
                forceInt8: true, fsr4Watermark: true);
            Assert.Contains(preview.IniKeys, k => k.Section == "FSR" && k.Key == "Fsr4ForceEnableInt8" && k.Value == "true");
            Assert.Contains(preview.IniKeys, k => k.Section == "FSR" && k.Key == "Fsr4EnableWatermark" && k.Value == "true");
        }

        [Fact]
        public void Addons_AreIndependentOfBackends_NoConflicts()
        {
            // Add-ons write disjoint files from every backend, so no pair conflicts.
            foreach (var backend in new[] { Fsr4Backend.Int8Community, Fsr4Backend.CustomMerged })
            {
                var preview = ComponentRegistry.BuildInstallPreview(backend, selectFsr4: true,
                    addFakenvapi: true, addNukemFg: true);
                Assert.Empty(preview.Conflicts);
            }
        }
        // ── Regressions found by reviewing upstream OptiScaler Client ────────────

        [Fact]
        public void Int8Build_IsRecognisedUnderEitherName()
        {
            // The community INT8 build was renamed between releases. Accepting only the
            // old name made every recent release fail to install.
            Assert.True(Fsr4Int8Build.IsKnown("amd_fidelityfx_upscaler_dx12.dll"));
            Assert.True(Fsr4Int8Build.IsKnown("AMDXCFFX64.DLL"));
            Assert.False(Fsr4Int8Build.IsKnown("nvngx_dlss.dll"));
            Assert.False(Fsr4Int8Build.IsKnown(null));
        }

        [Fact]
        public void Int8Build_FindsTheShippedDll_PreferringTheCurrentName()
        {
            var dir = Path.Combine(Path.GetTempPath(), "osm_int8_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                Assert.Null(Fsr4Int8Build.FindIn(dir));

                File.WriteAllText(Path.Combine(dir, Fsr4Int8Build.LegacyDllName), "x");
                Assert.EndsWith(Fsr4Int8Build.LegacyDllName, Fsr4Int8Build.FindIn(dir));

                File.WriteAllText(Path.Combine(dir, Fsr4Int8Build.CurrentDllName), "x");
                Assert.EndsWith(Fsr4Int8Build.CurrentDllName, Fsr4Int8Build.FindIn(dir));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void Int8Backend_ConflictsWithCustomDlls_UnderEitherName()
        {
            // Exclusion is derived from the files a component writes, so the renamed
            // build must still be recognised as claiming the same slot.
            var def = ComponentRegistry.All.Single(d => d.Id == ComponentIds.Fsr4Extras);
            Assert.Contains(Fsr4Int8Build.CurrentDllName, def.TargetFiles);
            Assert.Contains(Fsr4Int8Build.LegacyDllName, def.TargetFiles);
            Assert.True(ComponentRegistry.AreMutuallyExclusive(
                ComponentIds.CustomMerged, ComponentIds.Fsr4Extras));
        }

        [Fact]
        public void NukemFrameGen_IsActuallyTurnedOn()
        {
            // OptiScaler defaults [FrameGen] Enabled to false, so setting only the input
            // deployed the DLL and left frame generation off.
            var preview = ComponentRegistry.BuildInstallPreview(
                Fsr4Backend.Default, selectFsr4: true, addNukemFg: true);

            Assert.Contains(preview.IniKeys, k => k.Section == "FrameGen" && k.Key == "Enabled" && k.Value == "true");
            Assert.Contains(preview.IniKeys, k => k.Section == "FrameGen" && k.Key == "FGInput" && k.Value == "nukems");
        }

        [Fact]
        public void FrameGen_IsNotTouchedWhenNukemIsNotSelected()
        {
            var preview = ComponentRegistry.BuildInstallPreview(Fsr4Backend.Default, selectFsr4: true);
            Assert.DoesNotContain(preview.IniKeys, k => k.Section == "FrameGen");
        }

    }
}
