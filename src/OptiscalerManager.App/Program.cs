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
using System.Linq;
using OptiscalerManager.Core.Services;

namespace OptiscalerManager.App;

internal static class Program
{
    /// <summary>True when this process was re-exec'd by a successful in-place update.</summary>
    public static bool RelaunchedAfterUpdate { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static int Main(string[] args)
    {
        // Hidden self-test: run the in-process update headlessly and report — lets the
        // repo harness exercise the real download→swap→re-exec flow without a display.
        // The re-exec preserves argv, so the relaunched process keeps this flag too.
        if (args.Contains("--self-test-update"))
            return SelfTestUpdate(args);

        // The updater re-execs us with a marker; consume it (don't pass to Avalonia)
        // and surface an "Updated ✓" note on the main screen.
        RelaunchedAfterUpdate = args.Contains(AppUpdateService.UpdatedMarker);
        var avaloniaArgs = args.Where(a => a != AppUpdateService.UpdatedMarker && a != "--self-test-update").ToArray();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(avaloniaArgs);
        return 0;
    }

    private static int SelfTestUpdate(string[] args)
    {
        if (args.Contains(AppUpdateService.UpdatedMarker))
        {
            // Second life: the re-exec landed here. Report the running version + pid.
            Console.WriteLine($"SELFTEST relaunched pid={Environment.ProcessId} version={AppUpdateService.GetCurrentVersion()}");
            return 0;
        }
        Console.WriteLine($"SELFTEST start pid={Environment.ProcessId} version={AppUpdateService.GetCurrentVersion()}");
        var svc = new AppUpdateService(new Core.Models.RepositoryConfig { RepoOwner = "x", RepoName = "y" });
        var error = svc.RunInProcessUpdateAsync(m => Console.WriteLine($"SELFTEST: {m}")).GetAwaiter().GetResult();
        // Only reached on failure (success re-execs into the branch above).
        Console.WriteLine($"SELFTEST error: {error}");
        return 1;
    }

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
