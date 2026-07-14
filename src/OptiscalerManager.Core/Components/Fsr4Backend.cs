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
/// Which FSR 4 / FSR upscaler backend to install alongside OptiScaler. Chosen by the
/// user in the install dialog; maps to a component in the <see cref="ComponentRegistry"/>.
/// </summary>
public enum Fsr4Backend
{
    /// <summary>Install OptiScaler only; do not force an FSR backend or its ini keys.</summary>
    None,

    /// <summary>
    /// Download AMD's official open-source FidelityFX SDK (GPUOpen) and install its full
    /// prebuilt DLL set (loader + upscaler + frame generation + denoiser + companions).
    /// This is the FSR upscaler SDK, not the FSR 4 INT8 build. Maps to
    /// <see cref="ComponentIds.Fsr4AmdSdk"/>.
    /// </summary>
    LatestAmdSdk,

    /// <summary>
    /// Install a community FSR 4 INT8 build (amd_fidelityfx_upscaler_dx12.dll) from the
    /// OptiScaler-Extras repo, at a user-chosen version. Maps to
    /// <see cref="ComponentIds.Fsr4Extras"/>.
    /// </summary>
    Int8Community,

    /// <summary>Install the user-imported custom FSR SDK. Maps to <see cref="ComponentIds.CustomFsrSdk"/>.</summary>
    CustomSdk,

    /// <summary>Install the user-imported amdxcffx64.dll. Maps to <see cref="ComponentIds.CustomFsr4Dll"/>.</summary>
    CustomDll,
}
