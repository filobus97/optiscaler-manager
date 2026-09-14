// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
// Copyright (C) 2026 filobus97
// Licensed under GPL-3.0-or-later (see repository LICENSE).

namespace UpscalerManager.Core.Components;

/// <summary>
/// What the install writes for the "Nvidia override" — the lie that unlocks a DLSS
/// option a game hides from AMD and Intel. <see cref="Off"/> is the app's default:
/// nothing but a hidden DLSS option needs it, and some games crash with it.
/// </summary>
public enum SpoofMethod
{
    /// <summary>
    /// Write <c>[Spoofing] Dxgi=false</c>. Costs nothing but a DLSS option the game
    /// gates on the vendor: OptiScaler's own automatic decisions about this key only
    /// ever turn it off, and the one place that turns it on regardless — the FSR 4 INT8
    /// path, which needs the hook to reach FidelityFX — overrides the key anyway and
    /// reports the real GPU while doing it.
    /// </summary>
    Off,

    /// <summary>
    /// Write <c>[Spoofing] Dxgi=true</c> — the game is told it has an RTX 4090.
    /// </summary>
    On,

    /// <summary>
    /// Leave <c>[Spoofing] Dxgi=auto</c>: on for AMD and Intel, off for Nvidia, minus
    /// the games OptiScaler's own quirk table knows it breaks. Those quirks, and its
    /// Luma and Sekiro detections, are read only while the key is auto.
    /// </summary>
    Auto,

    /// <summary>
    /// Install <c>plugins/OptiPatcher.asi</c> and force <c>[Plugins] LoadAsiPlugins=true</c>,
    /// patching the game's vendor checks in memory. Dxgi stays at auto: OptiScaler turns
    /// spoofing off itself once the patch lands, and leaves it on if the patch fails.
    /// </summary>
    OptiPatcher,
}
