// OptiScaler Manager - a simple, AMD-focused frontend for the OptiScaler mod.
// Copyright (C) 2026 filobus97
//
// Based on OptiScaler Client (Copyright (C) 2026 Agustín Montaña / Agustinm28).
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See the repository LICENSE for details.

using Avalonia;
using System;

namespace OptiscalerManager.App;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by the visual designer.
    // On Linux we prefer the native (experimental) Wayland backend when running under
    // a Wayland session; otherwise fall back to platform detect (X11 / Windows / macOS).
    // The Wayland backend is not selected by UsePlatformDetect(), so we opt in explicitly.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>();

        var underWayland = OperatingSystem.IsLinux()
            && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

        // The experimental Wayland backend is not selected by UsePlatformDetect() and,
        // unlike it, does not auto-configure rendering/text — so we add Skia and
        // HarfBuzz explicitly. UsePlatformDetect() wires both itself elsewhere.
        builder = underWayland
            ? builder.UseWayland().UseSkia().UseHarfBuzz()
            : builder.UsePlatformDetect();

        return builder
            .WithInterFont()
            .LogToTrace();
    }
}
