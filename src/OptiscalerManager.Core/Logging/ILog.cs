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

namespace OptiscalerManager.Core.Logging;

/// <summary>
/// UI-agnostic logging sink. The source project logged straight into a
/// <c>DebugWindow</c> static; Core instead writes to whatever sink the host
/// installs (see <see cref="Log"/>), so the service layer never references the UI.
/// </summary>
public interface ILog
{
    /// <summary>Records a single diagnostic line.</summary>
    void Write(string message);
}
