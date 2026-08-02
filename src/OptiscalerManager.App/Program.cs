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
using System.IO;
using System.Linq;
using System.Threading;
using OptiscalerManager.Core.Input;
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

        // Controller diagnostics: prints what input devices exist, which are readable,
        // which report as controllers, then echoes live input. Run this and share the
        // output when a controller isn't working.
        if (args.Contains("--gamepad-test"))
            return GamepadTest();

        // The updater re-execs us with a marker; consume it (don't pass to Avalonia)
        // and surface an "Updated ✓" note on the main screen.
        RelaunchedAfterUpdate = args.Contains(AppUpdateService.UpdatedMarker);
        var avaloniaArgs = args.Where(a => a != AppUpdateService.UpdatedMarker && a != "--self-test-update").ToArray();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(avaloniaArgs);
        return 0;
    }

    private static int GamepadTest()
    {
        Console.WriteLine($"OptiScaler Manager {AppUpdateService.GetCurrentVersion()} — controller diagnostics");
        Console.WriteLine($"user={Environment.UserName}  linux={OperatingSystem.IsLinux()}");
        Console.WriteLine();

        var root = EvdevGamepadSource.DefaultInputRoot;
        if (!Directory.Exists(root))
        {
            Console.WriteLine($"{root} does not exist — no input devices are visible to this process.");
            return 1;
        }

        Console.WriteLine("Devices:");
        var any = false;
        foreach (var path in EvdevGamepadSource.DiscoverCandidatePaths(root).OrderBy(p => p, StringComparer.Ordinal))
        {
            any = true;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var name = EvdevGamepadSource.TryGetDeviceName(stream) ?? "(no name)";
                var pad = EvdevGamepadSource.LooksLikeGamepad(stream, path);
                Console.WriteLine($"  {(pad ? "GAMEPAD " : "        ")}{path}  \"{name}\"");

                // Which axes exist decides where the scroll stick is read from, so show
                // the working out — that is what a "scrolling does nothing" report needs.
                if (pad) Console.WriteLine("           " + DescribeAxes(stream));
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine($"  DENIED   {path}  (no read permission)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ERROR    {path}  ({ex.GetType().Name}: {ex.Message})");
            }
        }
        if (!any) Console.WriteLine("  (none found)");

        Console.WriteLine();
        Console.WriteLine("Listening for 20s — press buttons / move the sticks now:");
        using var source = new EvdevGamepadSource();
        var count = 0;
        source.Input += i =>
        {
            Interlocked.Increment(ref count);
            Console.WriteLine($"  {DateTime.Now:HH:mm:ss.fff}  {i.Action} {(i.Pressed ? "pressed" : "released")}");
        };
        source.Scroll += s =>
        {
            Interlocked.Increment(ref count);
            Console.WriteLine($"  {DateTime.Now:HH:mm:ss.fff}  scroll x={s.X:+0.00;-0.00; 0.00} y={s.Y:+0.00;-0.00; 0.00}");
        };
        source.Start();
        Console.WriteLine($"  reading: {(source.ConnectedDevices.Count > 0 ? string.Join(", ", source.ConnectedDevices) : "(no controller opened)")}");
        Thread.Sleep(TimeSpan.FromSeconds(20));

        Console.WriteLine();
        Console.WriteLine(count > 0
            ? $"Received {count} events — the controller pipeline works."
            : "No events received. If a GAMEPAD line appeared above, another program (e.g. Steam) is likely holding the device exclusively.");
        return 0;
    }

    /// <summary>Names the axes a pad declares and which two the scroll stick will use.</summary>
    private static string DescribeAxes(FileStream stream)
    {
        var declared = EvdevGamepadSource.TryGetAbsAxes(stream);
        if (declared is null) return "axes: could not be probed — assuming ABS_RX/ABS_RY for scrolling";

        var names = new (ushort Code, string Name)[]
        {
            (Evdev.ABS_X, "ABS_X"), (Evdev.ABS_Y, "ABS_Y"), (Evdev.ABS_Z, "ABS_Z"),
            (Evdev.ABS_RX, "ABS_RX"), (Evdev.ABS_RY, "ABS_RY"), (Evdev.ABS_RZ, "ABS_RZ"),
            (Evdev.ABS_HAT0X, "ABS_HAT0X"), (Evdev.ABS_HAT0Y, "ABS_HAT0Y"),
        };
        var present = names.Where(n => n.Code < declared.Length && declared[n.Code]).Select(n => n.Name).ToList();
        var scroll = EvdevGamepadDecoder.ScrollAxes(declared);
        var scrollName = names.Where(n => n.Code == scroll.X || n.Code == scroll.Y).Select(n => n.Name);

        return $"axes: {(present.Count > 0 ? string.Join(", ", present) : "none")}"
             + $"  |  scroll stick: {string.Join(" + ", scrollName)}";
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
