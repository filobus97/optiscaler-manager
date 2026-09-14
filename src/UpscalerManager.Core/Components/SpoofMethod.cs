// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
// Copyright (C) 2026 filobus97
// Licensed under GPL-3.0-or-later (see repository LICENSE).

namespace UpscalerManager.Core.Components;

/// <summary>
/// What the install writes for the "Nvidia override" — the vendor check that hides a
/// game's DLSS option on AMD and Intel.
/// </summary>
/// <remarks>
/// why: every automatic decision OptiScaler makes about DXGI spoofing — its per-game
/// quirks, the Luma and Sekiro detections, turning it off when OptiPatcher lands or when
/// no nvngx replacement is found — is guarded by the ini key having no explicit value.
/// So writing true or false is not just a preference, it opts the game out of all of
/// that, and <see cref="Default"/> has to stay the default.
/// </remarks>
public enum SpoofMethod
{
    /// <summary>
    /// Leave <c>[Spoofing] Dxgi=auto</c>: on for AMD and Intel, off for Nvidia, minus
    /// the games OptiScaler knows it breaks.
    /// </summary>
    Default,

    /// <summary>
    /// Force <c>[Spoofing] Dxgi=true</c> — the game is told it has an RTX 4090.
    /// </summary>
    ForceDxgi,

    /// <summary>
    /// Force <c>[Spoofing] Dxgi=false</c>, for games that crash when the adapter lies.
    /// </summary>
    ForceOff,

    /// <summary>
    /// Install <c>plugins/OptiPatcher.asi</c> and force <c>[Plugins] LoadAsiPlugins=true</c>,
    /// patching the game's vendor checks in memory. Dxgi stays at auto: OptiScaler turns
    /// spoofing off itself once the patch lands, and leaves it on if the patch fails.
    /// </summary>
    OptiPatcher,
}
