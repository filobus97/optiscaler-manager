// OptiScaler Manager - a simple, AMD-focused frontend for the OptiScaler mod.
// Copyright (C) 2026 filobus97
// Licensed under GPL-3.0-or-later (see repository LICENSE).

namespace OptiscalerManager.Core.Components;

/// <summary>
/// How the "Nvidia override" is applied to a game. Chosen per install; null/absent
/// means no override at all.
/// </summary>
public enum SpoofMethod
{
    /// <summary>
    /// OptiScaler's built-in DXGI spoofing: forces [Spoofing] Dxgi=true so the game
    /// sees an Nvidia GPU (RTX 4090 by default). The default method.
    /// </summary>
    Dxgi,

    /// <summary>
    /// The OptiPatcher ASI plugin: installs plugins/OptiPatcher.asi and forces
    /// [Plugins] LoadAsiPlugins=true. Patches games' vendor checks in memory instead
    /// of spoofing the adapter.
    /// </summary>
    OptiPatcher,
}
