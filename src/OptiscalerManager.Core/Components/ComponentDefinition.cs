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

using System.Collections.Generic;

namespace OptiscalerManager.Core.Components;

/// <summary>Stable identifiers for the components the Manager can install.</summary>
public static class ComponentIds
{
    public const string OptiScaler = "optiscaler";
    public const string Fakenvapi = "fakenvapi";
    public const string NukemFg = "nukem-fg";
    public const string OptiPatcher = "optipatcher";
    /// <summary>Full AMD FidelityFX SDK DLL set (loader + upscaler + frame-gen + denoiser).</summary>
    public const string Fsr4AmdSdk = "fsr4-amd-sdk";
    /// <summary>FSR 4.x INT8 community upscaler build from the OptiScaler "Extras" repo.</summary>
    public const string Fsr4Extras = "fsr4-extras";
    /// <summary>User-supplied custom DLLs merged on top of the latest AMD signedbin set.</summary>
    public const string CustomMerged = "custom-merged";
}

/// <summary>
/// Declarative description of an installable component. This is the
/// "component-as-data" model the design notes call for: the mutual-exclusion
/// rules and the "what will happen" preview are both derived from this record
/// rather than from per-screen conditionals.
/// </summary>
public sealed class ComponentDefinition
{
    /// <summary>Stable id (see <see cref="ComponentIds"/>).</summary>
    public required string Id { get; init; }

    /// <summary>Short human-readable name shown in the UI.</summary>
    public required string DisplayName { get; init; }

    /// <summary>One-line explanation used for tooltips and the preview.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Files written next to the game executable when this component installs
    /// (relative names, e.g. <c>amdxcffx64.dll</c>). Two components that list the
    /// same file are mutually exclusive — that is how the registry derives the
    /// FSR4-INT8-vs-custom-SDK conflict without any per-screen glue.
    /// </summary>
    public IReadOnlyList<string> TargetFiles { get; init; } = new List<string>();

    /// <summary>OptiScaler.ini keys this component sets on install.</summary>
    public IReadOnlyList<IniKeyChange> IniKeys { get; init; } = new List<IniKeyChange>();

    /// <summary>Component ids that must already be present for this one to work.</summary>
    public IReadOnlyList<string> Requires { get; init; } = new List<string>();

    /// <summary>
    /// Component ids explicitly incompatible with this one, beyond any implied by
    /// shared <see cref="TargetFiles"/>.
    /// </summary>
    public IReadOnlyList<string> Conflicts { get; init; } = new List<string>();

    /// <summary>
    /// True for components the user must supply from a local file they already
    /// possess (never downloaded or bundled — see the project's no-AMD-binaries
    /// policy).
    /// </summary>
    public bool IsBringYourOwn { get; init; }
}
