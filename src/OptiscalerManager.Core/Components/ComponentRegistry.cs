// OptiScaler Manager - a simple, AMD-focused frontend for the OptiScaler mod.
// Copyright (C) 2026 filobus97
//
// Based on OptiScaler Client (Copyright (C) 2026 Agustín Montaña / Agustinm28).
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;

namespace OptiscalerManager.Core.Components;

/// <summary>
/// The single source of truth describing every component the Manager can install,
/// modelled as data. Mutual-exclusion rules and the "what will happen" preview are
/// both derived from these definitions — there is no per-screen conditional logic.
///
/// The ini keys and target file names mirror exactly what the ported
/// <see cref="Services.GameInstallationService"/> writes, so the preview is an
/// honest, hand-reproducible account of the install.
/// </summary>
public static class ComponentRegistry
{
    /// <summary>Default OptiScaler injection DLL (matches the installer default).</summary>
    public const string DefaultInjectionDll = "dxgi.dll";

    /// <summary>
    /// The OptiScaler.ini keys that "engage FSR 4" on non-RDNA4 GPUs, applied by
    /// the Manager's one-click flow exactly as the ported installer does
    /// (<c>[FSR] UpscalerIndex = 0</c> on current builds, <c>Fsr4Update = true</c>
    /// on older 0.7.x builds; unknown keys are ignored by OptiScaler's parser).
    /// </summary>
    public static readonly IReadOnlyList<IniKeyChange> Fsr4EnableKeys = new[]
    {
        new IniKeyChange("FSR", "UpscalerIndex", "0"),
        new IniKeyChange("FSR", "Fsr4Update", "true"),
    };

    private static readonly IReadOnlyList<ComponentDefinition> _definitions = BuildDefinitions();

    private static readonly Dictionary<string, ComponentDefinition> _byId =
        _definitions.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every known component definition, in display order.</summary>
    public static IReadOnlyList<ComponentDefinition> All => _definitions;

    /// <summary>Looks up a definition by id, or throws if unknown.</summary>
    public static ComponentDefinition Get(string id) =>
        _byId.TryGetValue(id, out var d)
            ? d
            : throw new KeyNotFoundException($"Unknown component id '{id}'.");

    /// <summary>True when the id maps to a known component.</summary>
    public static bool Exists(string id) => _byId.ContainsKey(id);

    /// <summary>
    /// Two components are mutually exclusive when they write any of the same files,
    /// or when either explicitly lists the other in <see cref="ComponentDefinition.Conflicts"/>.
    /// This is what encodes "FSR 4 INT8 (Extras) vs custom SDK" — both write
    /// <c>amd_fidelityfx_upscaler_dx12.dll</c> — with no bespoke logic.
    /// </summary>
    public static bool AreMutuallyExclusive(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return false;
        var da = Get(a);
        var db = Get(b);

        if (da.Conflicts.Contains(b, StringComparer.OrdinalIgnoreCase)) return true;
        if (db.Conflicts.Contains(a, StringComparer.OrdinalIgnoreCase)) return true;

        return da.TargetFiles.Any(f => db.TargetFiles.Contains(f, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Every component id that is mutually exclusive with <paramref name="id"/>.</summary>
    public static IReadOnlyList<string> ConflictsFor(string id) =>
        _definitions.Where(d => AreMutuallyExclusive(id, d.Id)).Select(d => d.Id).ToList();

    /// <summary>
    /// Builds the transparent "what will happen" preview for installing the given
    /// components. Files and ini keys are de-duplicated and returned in a stable
    /// order so the UI can list them and a user can replicate the change by hand.
    /// </summary>
    /// <param name="componentIds">Components to be installed.</param>
    /// <param name="injectionDll">
    /// OptiScaler injection DLL name (defaults to <see cref="DefaultInjectionDll"/>);
    /// substituted into the core component's file list.
    /// </param>
    public static InstallPreview BuildPreview(IEnumerable<string> componentIds, string? injectionDll = null)
    {
        injectionDll ??= DefaultInjectionDll;
        var ids = componentIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var files = new List<string>();
        var iniKeys = new List<IniKeyChange>();
        var lines = new List<PreviewComponent>();
        var conflicts = new List<string>();

        // Surface any mutually-exclusive pair the caller asked for so the UI can warn.
        for (int i = 0; i < ids.Count; i++)
            for (int j = i + 1; j < ids.Count; j++)
                if (AreMutuallyExclusive(ids[i], ids[j]))
                    conflicts.Add($"{Get(ids[i]).DisplayName} and {Get(ids[j]).DisplayName} both write the same file and cannot be installed together.");

        foreach (var id in ids)
        {
            var def = Get(id);
            var componentFiles = def.TargetFiles
                .Select(f => f == "{injection}" ? injectionDll : f)
                .ToList();

            foreach (var f in componentFiles)
                if (!files.Contains(f, StringComparer.OrdinalIgnoreCase))
                    files.Add(f);

            foreach (var k in def.IniKeys)
                if (!iniKeys.Any(existing => existing.Section == k.Section && existing.Key == k.Key))
                    iniKeys.Add(k);

            lines.Add(new PreviewComponent(def, componentFiles));
        }

        return new InstallPreview(lines, files, iniKeys, conflicts);
    }

    /// <summary>
    /// Maps an <see cref="Fsr4Backend"/> to the registry component id(s) it installs.
    /// </summary>
    public static IReadOnlyList<string> ComponentIdsFor(Fsr4Backend backend) => backend switch
    {
        Fsr4Backend.Int8Community => new[] { ComponentIds.Fsr4Extras },
        Fsr4Backend.CustomMerged => new[] { ComponentIds.CustomMerged },
        _ => Array.Empty<string>(),
    };

    /// <summary>
    /// The transparent "what will happen" preview for the Manager's **Install OptiScaler**
    /// flow, under the decoupled model:
    /// <list type="bullet">
    /// <item>Files come from the chosen backend component(s).</item>
    /// <item>The Manager writes ONLY the keys it is responsible for — <c>[FSR] Fsr4Update</c>
    /// (always, to make FSR 4 available), <c>[FSR] UpscalerIndex</c> (<c>0</c> when the user
    /// asks the Manager to select FSR 4, else <c>auto</c> to select it in-game), and
    /// <c>[Menu] ShortcutKey</c> when a menu key is configured. Every other key comes from
    /// the chosen OptiScaler.ini (default or custom) and is left untouched.</item>
    /// </list>
    /// </summary>
    /// <param name="backend">The backend/DLL set to install.</param>
    /// <param name="selectFsr4">True to have the Manager select FSR 4 (UpscalerIndex=0).</param>
    /// <param name="injectionDll">Injection DLL name (defaults to dxgi.dll).</param>
    /// <param name="menuKeyVk">Configured overlay key (VK hex, e.g. "0x78"), or null to leave OptiScaler's default.</param>
    /// <param name="customDlls">
    /// For <see cref="Fsr4Backend.CustomMerged"/>: the user's imported DLL names.
    /// Each is annotated in the file list — "overwrites" when it collides with a base
    /// file, "added" otherwise.
    /// </param>
    /// <param name="addFakenvapi">Include the fakenvapi add-on (nvapi64.dll + fakenvapi.ini).</param>
    /// <param name="addNukemFg">Include Nukem's DLSSG-to-FSR3 mod (adds [FrameGen] FGInput=nukems).</param>
    /// <param name="spoofMethod">
    /// Nvidia override method for this game, or null for none. Dxgi forces
    /// [Spoofing] Dxgi=true; OptiPatcher installs plugins/OptiPatcher.asi and forces
    /// [Plugins] LoadAsiPlugins=true.
    /// </param>
    /// <param name="forceInt8">Force [FSR] Fsr4ForceEnableInt8=true (INT8 model on unsupported GPUs).</param>
    /// <param name="fsr4Watermark">Force [FSR] Fsr4EnableWatermark=true (on-screen FSR4/FSR4-i8/FSR3 verification).</param>
    public static InstallPreview BuildInstallPreview(
        Fsr4Backend backend, bool selectFsr4, string? injectionDll = null, string? menuKeyVk = null,
        IReadOnlyList<string>? customDlls = null,
        bool addFakenvapi = false, bool addNukemFg = false,
        SpoofMethod? spoofMethod = null, bool forceInt8 = false, bool fsr4Watermark = false)
    {
        var ids = new List<string> { ComponentIds.OptiScaler };
        ids.AddRange(ComponentIdsFor(backend));
        if (addFakenvapi) ids.Add(ComponentIds.Fakenvapi);
        if (addNukemFg) ids.Add(ComponentIds.NukemFg);
        if (spoofMethod == SpoofMethod.OptiPatcher) ids.Add(ComponentIds.OptiPatcher);

        var preview = BuildPreview(ids, injectionDll);

        // The custom backend writes ONLY the user's DLLs, overlaid on OptiScaler's
        // installed files: known names swap the bundled file in place, unknown names
        // are added. Replace the component's potential-swap list with the actual
        // overlay, honestly annotated.
        if (backend == Fsr4Backend.CustomMerged)
        {
            var swappable = Get(ComponentIds.CustomMerged).TargetFiles;
            var files = preview.Files
                .Where(f => !swappable.Contains(f, StringComparer.OrdinalIgnoreCase))
                .ToList();
            foreach (var dll in customDlls ?? Array.Empty<string>())
            {
                files.Add(swappable.Contains(dll, StringComparer.OrdinalIgnoreCase)
                    ? $"{dll}  (your DLL — swaps OptiScaler's in place)"
                    : $"{dll}  (your DLL — added)");
            }
            preview = preview with { Files = files };
        }

        // The forced keys are the ONLY ini keys the Manager writes.
        var iniKeys = new List<IniKeyChange>
        {
            new IniKeyChange("FSR", "Fsr4Update", "true"),
            new IniKeyChange("FSR", "UpscalerIndex", selectFsr4 ? "0" : "auto"),
        };
        if (forceInt8)
            iniKeys.Add(new IniKeyChange("FSR", "Fsr4ForceEnableInt8", "true"));
        if (fsr4Watermark)
            iniKeys.Add(new IniKeyChange("FSR", "Fsr4EnableWatermark", "true"));
        if (addNukemFg)
            iniKeys.Add(new IniKeyChange("FrameGen", "FGInput", "nukems"));
        if (spoofMethod == SpoofMethod.Dxgi)
            iniKeys.Add(new IniKeyChange("Spoofing", "Dxgi", "true"));
        if (spoofMethod == SpoofMethod.OptiPatcher)
            iniKeys.Add(new IniKeyChange("Plugins", "LoadAsiPlugins", "true"));
        if (!string.IsNullOrWhiteSpace(menuKeyVk))
            iniKeys.Add(new IniKeyChange("Menu", "ShortcutKey", menuKeyVk!));

        return preview with { IniKeys = iniKeys };
    }

    private static IReadOnlyList<ComponentDefinition> BuildDefinitions() => new List<ComponentDefinition>
    {
        new ComponentDefinition
        {
            Id = ComponentIds.OptiScaler,
            DisplayName = "OptiScaler (core)",
            Description = "The OptiScaler upscaler shim itself: the injection DLL, OptiScaler.dll and the OptiScaler.ini config file.",
            // "{injection}" is substituted with the chosen injection DLL (default dxgi.dll).
            TargetFiles = new[] { "{injection}", "OptiScaler.dll", "OptiScaler.ini" },
        },
        new ComponentDefinition
        {
            Id = ComponentIds.Fsr4Extras,
            DisplayName = "FSR 4 INT8 community build",
            Description = "A community FSR 4.x INT8 upscaler build (amd_fidelityfx_upscaler_dx12.dll) from the OptiScaler-Extras repo, at a version you pick. No proprietary AMD binary is bundled.",
            TargetFiles = new[] { "amd_fidelityfx_upscaler_dx12.dll" },
            IniKeys = new[]
            {
                new IniKeyChange("FSR", "UpscalerIndex", "0"),
                new IniKeyChange("FSR", "Fsr4Update", "true"),
            },
            Requires = new[] { ComponentIds.OptiScaler },
        },
        new ComponentDefinition
        {
            Id = ComponentIds.CustomMerged,
            DisplayName = "Custom DLLs",
            Description = "Your imported custom DLLs overlaid on the OptiScaler install: names OptiScaler ships (e.g. amd_fidelityfx_upscaler_dx12.dll) are swapped in place, unknown names (e.g. amdxcffx64.dll) are added alongside. Your DLLs are never downloaded — bring your own.",
            // These are the OptiScaler-shipped names the overlay MAY swap in place
            // (drives the derived conflict with the INT8 backend); the actual custom
            // overlay is dynamic and annotated into the preview by BuildInstallPreview.
            TargetFiles = new[]
            {
                "amd_fidelityfx_dx12.dll",
                "amd_fidelityfx_loader_dx12.dll",
                "amd_fidelityfx_upscaler_dx12.dll",
                "amd_fidelityfx_framegeneration_dx12.dll",
                "amd_fidelityfx_denoiser_dx12.dll",
            },
            IniKeys = new[]
            {
                new IniKeyChange("FSR", "UpscalerIndex", "0"),
                new IniKeyChange("FSR", "Fsr4Update", "true"),
            },
            Requires = new[] { ComponentIds.OptiScaler },
            IsBringYourOwn = true,
            // Redundant with the shared-file rule, but stated explicitly for readers.
            Conflicts = new[] { ComponentIds.Fsr4Extras },
        },
        new ComponentDefinition
        {
            Id = ComponentIds.Fakenvapi,
            DisplayName = "fakenvapi",
            Description = "Optional NVAPI shim (nvapi64.dll) that translates Reflex calls to AMD Anti-Lag 2 / LatencyFlex, and is required for Nukem's frame-gen mod on AMD/Intel GPUs. Downloaded from the optiscaler/fakenvapi releases.",
            TargetFiles = new[] { "nvapi64.dll", "fakenvapi.ini" },
            Requires = new[] { ComponentIds.OptiScaler },
        },
        new ComponentDefinition
        {
            Id = ComponentIds.NukemFg,
            DisplayName = "Nukem DLSSG-to-FSR3 (frame gen)",
            Description = "Optional frame-generation mod for games with DLSS-G. Cannot be downloaded automatically — you supply the DLL from a local file (Nexus Mods).",
            TargetFiles = new[] { "dlssg_to_fsr3_amd_is_better.dll" },
            // OptiScaler 0.9.x selects Nukem's mod via [FrameGen] FGInput (the legacy
            // sectionless FGType key is no longer read).
            IniKeys = new[] { new IniKeyChange("FrameGen", "FGInput", "nukems") },
            Requires = new[] { ComponentIds.OptiScaler },
            IsBringYourOwn = true,
        },
        new ComponentDefinition
        {
            Id = ComponentIds.OptiPatcher,
            DisplayName = "OptiPatcher",
            Description = "ASI plugin that patches games' GPU vendor checks in memory — the alternative Nvidia-override method to DXGI spoofing. Downloaded from the optiscaler/OptiPatcher releases.",
            TargetFiles = new[] { @"plugins\OptiPatcher.asi" },
            IniKeys = new[] { new IniKeyChange("Plugins", "LoadAsiPlugins", "true") },
            Requires = new[] { ComponentIds.OptiScaler },
        },
    };
}
