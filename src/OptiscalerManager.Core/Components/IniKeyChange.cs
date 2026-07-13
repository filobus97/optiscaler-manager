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

namespace OptiscalerManager.Core.Components;

/// <summary>
/// A single, section-aware change to <c>OptiScaler.ini</c>. Applied through
/// <see cref="Services.GameInstallationService.ModifyOptiScalerIniKey"/>, and
/// surfaced verbatim in the "what will happen" preview so a user can reproduce
/// it by hand.
/// </summary>
public sealed record IniKeyChange(string Section, string Key, string Value)
{
    /// <summary>Renders as it appears in the ini, e.g. <c>[FSR] UpscalerIndex = 0</c>.</summary>
    public override string ToString() => $"[{Section}] {Key} = {Value}";
}
