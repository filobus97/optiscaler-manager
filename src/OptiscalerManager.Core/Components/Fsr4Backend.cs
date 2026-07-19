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
    /// <summary>Install OptiScaler's own provided files only — they already include
    /// the newest FSR upscaler each release can hook (a separate "AMD download"
    /// backend was removed as redundant: only the bundled revisions are hookable).
    /// The zero-decision, recommended path.</summary>
    Default,

    /// <summary>
    /// A community FSR 4 INT8 build (amd_fidelityfx_upscaler_dx12.dll) from the
    /// OptiScaler-Extras repo, at a user-chosen version. Maps to
    /// <see cref="ComponentIds.Fsr4Extras"/>.
    /// </summary>
    Int8Community,

    /// <summary>
    /// The user's imported custom DLLs overlaid on the OptiScaler install:
    /// same-name DLLs overwrite OptiScaler's files in place, unknown names (e.g.
    /// amdxcffx64.dll) are added alongside. Maps to <see cref="ComponentIds.CustomMerged"/>.
    /// </summary>
    CustomMerged,
}
