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

using System.Threading.Tasks;

namespace OptiscalerManager.Core.Prompts;

/// <summary>
/// Describes a component that cannot be downloaded automatically and must be
/// supplied by the user from a local file (currently only Nukem's
/// DLSSG-to-FSR3 mod). The source project raised an Avalonia dialog directly
/// from inside <c>ComponentManagementService</c>; Core instead calls out through
/// this interface so the service layer stays UI-agnostic.
/// </summary>
public sealed class ManualComponentRequest
{
    /// <summary>Human-readable component name, e.g. "Nukem's DLSSG-to-FSR3 Mod".</summary>
    public string ComponentName { get; init; } = string.Empty;

    /// <summary>The exact file name the user must provide, e.g. the mod DLL.</summary>
    public string RequiredFileName { get; init; } = string.Empty;

    /// <summary>
    /// Destination cache directory. The provider must place a file named
    /// <see cref="RequiredFileName"/> here on success (extracting from an archive
    /// if the user selected one).
    /// </summary>
    public string TargetCachePath { get; init; } = string.Empty;

    /// <summary>True when updating an existing copy rather than a first import.</summary>
    public bool IsUpdate { get; init; }
}

/// <summary>
/// Host-provided callback that fulfils a <see cref="ManualComponentRequest"/>.
/// Implementations own the file picker / archive extraction and return whether a
/// valid file was placed in the request's cache directory.
/// </summary>
public interface IManualComponentProvider
{
    /// <summary>
    /// Prompts the user to supply the file described by <paramref name="request"/>.
    /// Returns <c>true</c> when a valid file was written to the cache directory,
    /// <c>false</c> if the user skipped or cancelled.
    /// </summary>
    Task<bool> ProvideAsync(ManualComponentRequest request);
}

/// <summary>Default provider that declines every request (used when the host
/// installs no interactive provider, e.g. in tests or headless runs).</summary>
public sealed class NullManualComponentProvider : IManualComponentProvider
{
    public Task<bool> ProvideAsync(ManualComponentRequest request) => Task.FromResult(false);
}
