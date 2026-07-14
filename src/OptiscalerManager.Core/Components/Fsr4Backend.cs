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
/// This is decoupled from whether FSR 4 is actually *selected* — see the install flow:
/// FSR 4 is always made available, but selecting it is a separate choice.
/// </summary>
public enum Fsr4Backend
{
    /// <summary>Install OptiScaler's own provided files only; no extra upscaler DLLs.</summary>
    Default,

    /// <summary>
    /// AMD's official open-source FidelityFX SDK (GPUOpen), full DLL set. This is the
    /// FSR 3.1 upscaler SDK — it does not contain FSR 4.
    /// Maps to <see cref="ComponentIds.Fsr4AmdSdk"/>.
    /// </summary>
    LatestAmdSdk,

    /// <summary>
    /// A community FSR 4 INT8 build (amd_fidelityfx_upscaler_dx12.dll) from the
    /// OptiScaler-Extras repo, at a user-chosen version. Maps to
    /// <see cref="ComponentIds.Fsr4Extras"/>.
    /// </summary>
    Int8Community,

    /// <summary>The user-imported custom FSR SDK. Maps to <see cref="ComponentIds.CustomFsrSdk"/>.</summary>
    CustomSdk,

    /// <summary>
    /// The user-imported amdxcffx64.dll (proprietary FSR 4 driver runtime) installed
    /// together with the latest AMD FSR SDK — amdxcffx64.dll does not work on its own.
    /// Maps to <see cref="ComponentIds.CustomFsr4Dll"/> + <see cref="ComponentIds.Fsr4AmdSdk"/>.
    /// </summary>
    CustomDllPlusAmdSdk,
}
