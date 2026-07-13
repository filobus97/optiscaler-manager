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
            DisplayName = "FSR 4 (INT8, OptiScaler Extras)",
            Description = "OptiScaler's own FSR 4.x INT8 upscaler backend (amd_fidelityfx_upscaler_dx12.dll). No proprietary AMD binary is bundled.",
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
            Id = ComponentIds.CustomFsr4Dll,
            DisplayName = "Custom FSR 4 DLL (amdxcffx64.dll)",
            Description = "Your own amdxcffx64.dll (FSR 4.x INT8 runtime), imported from a local file you already have. Never downloaded by this app.",
            TargetFiles = new[] { "amdxcffx64.dll" },
            IniKeys = new[]
            {
                new IniKeyChange("FSR", "UpscalerIndex", "0"),
                new IniKeyChange("FSR", "Fsr4Update", "true"),
            },
            Requires = new[] { ComponentIds.OptiScaler },
            IsBringYourOwn = true,
        },
        new ComponentDefinition
        {
            Id = ComponentIds.CustomFsrSdk,
            DisplayName = "Custom FSR SDK",
            Description = "Your own FSR SDK DLL set (amd_fidelityfx_upscaler_dx12.dll + frame-generation companion), imported from a local archive or folder. Replaces the Extras upscaler.",
            TargetFiles = new[] { "amd_fidelityfx_upscaler_dx12.dll", "amd_fidelityfx_framegeneration_dx12.dll" },
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
            Description = "Optional NVAPI shim that lets OptiScaler expose Reflex/anti-lag paths to more games.",
            TargetFiles = new[] { "nvapi64.dll", "fakenvapi.ini" },
            Requires = new[] { ComponentIds.OptiScaler },
        },
        new ComponentDefinition
        {
            Id = ComponentIds.NukemFg,
            DisplayName = "Nukem DLSSG-to-FSR3 (frame gen)",
            Description = "Optional frame-generation mod. Cannot be downloaded automatically — you supply the DLL from a local file (Nexus Mods).",
            TargetFiles = new[] { "dlssg_to_fsr3_amd_is_better.dll" },
            Requires = new[] { ComponentIds.OptiScaler },
            IsBringYourOwn = true,
        },
        new ComponentDefinition
        {
            Id = ComponentIds.OptiPatcher,
            DisplayName = "OptiPatcher",
            Description = "Optional ASI plugin that patches certain games so OptiScaler can hook them.",
            TargetFiles = new[] { @"plugins\OptiPatcher.asi" },
            Requires = new[] { ComponentIds.OptiScaler },
        },
    };
}
