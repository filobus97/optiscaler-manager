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

/// <summary>One component's contribution to an install preview.</summary>
/// <param name="Component">The component definition.</param>
/// <param name="Files">The concrete file names it will write (injection DLL resolved).</param>
public sealed record PreviewComponent(ComponentDefinition Component, IReadOnlyList<string> Files);

/// <summary>
/// The transparent "what will happen" account shown before any install: every
/// file that will be written and every OptiScaler.ini key that will change, so a
/// user can verify or reproduce the change by hand. Nothing here is a black box.
/// </summary>
/// <param name="Components">Per-component breakdown.</param>
/// <param name="Files">All distinct files that will be written, next to the game exe.</param>
/// <param name="IniKeys">All distinct OptiScaler.ini keys that will be set.</param>
/// <param name="Conflicts">
/// Human-readable warnings for any mutually-exclusive components requested together
/// (empty in the normal case).
/// </param>
public sealed record InstallPreview(
    IReadOnlyList<PreviewComponent> Components,
    IReadOnlyList<string> Files,
    IReadOnlyList<IniKeyChange> IniKeys,
    IReadOnlyList<string> Conflicts);
