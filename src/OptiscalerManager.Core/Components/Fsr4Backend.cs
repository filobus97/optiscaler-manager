// OptiScaler Manager - a simple, AMD-focused frontend for the OptiScaler mod.
// Copyright (C) 2026 filobus97
//
// Based on OptiScaler Client (Copyright (C) 2026 Agustín Montaña / Agustinm28).
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version. See the repository LICENSE.

namespace OptiscalerManager.Core.Components;

/// <summary>
/// Which upscaler backend / DLL set to install alongside OptiScaler ("what files").
/// Decoupled from whether FSR 4 is *selected* — FSR 4 availability flags are always
/// forced; selecting it is a separate choice in the install flow.
/// </summary>
public enum Fsr4Backend
{
    /// <summary>Install OptiScaler's own provided files only (they already include a
    /// working FSR 4.1 upscaler). The zero-decision, recommended path.</summary>
    Default,

    /// <summary>
    /// AMD's signed prebuilt FSR DLLs fetched from the official FidelityFX-SDK
    /// repository tree (signedbin) — the exact artifacts OptiScaler bundles, at AMD's
    /// newest revision — swapped in place. Maps to <see cref="ComponentIds.Fsr4AmdSdk"/>.
    /// </summary>
    LatestAmdSdk,

    /// <summary>
    /// A community FSR 4 INT8 build (amd_fidelityfx_upscaler_dx12.dll) from the
    /// OptiScaler-Extras repo, at a user-chosen version. Maps to
    /// <see cref="ComponentIds.Fsr4Extras"/>.
    /// </summary>
    Int8Community,

    /// <summary>
    /// The user's imported custom DLLs merged on top of the latest AMD signedbin
    /// set: same-name DLLs overwrite the AMD/OptiScaler files, unknown names (e.g.
    /// amdxcffx64.dll) are added alongside. Maps to <see cref="ComponentIds.CustomMerged"/>.
    /// </summary>
    CustomMerged,
}
