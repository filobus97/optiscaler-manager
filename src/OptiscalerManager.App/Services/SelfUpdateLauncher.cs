// OptiScaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace OptiscalerManager.App.Services;

/// <summary>
/// Shared "Update now" flow used by both the main-window banner and the Settings
/// card. On Unix it updates seamlessly in-process (download → swap files → re-exec
/// at the same PID — survives Steam Gaming Mode / gamescope); on Windows it launches
/// the detached updater and shuts the app down (a running .exe is locked). On any
/// failure the app stays open and the error is reported.
/// </summary>
public static class SelfUpdateLauncher
{
    // Re-entrancy guard: a fast second click (or a controller Enter-repeat) must not
    // start a second update. Once we begin, every later call is a no-op.
    private static bool _started;

    /// <summary>
    /// Runs the platform-appropriate update. <paramref name="report"/> receives
    /// status/error messages for the caller's UI. On Unix success the process is
    /// replaced and this never returns; on Windows success the app shuts down.
    /// </summary>
    public static async Task StartAsync(ManagerService manager, Action<string> report)
    {
        if (_started) return;
        _started = true;

        if (manager.UsesInProcessUpdate)
        {
            // Downloads, swaps files, and re-execs at the same PID. Returns only on
            // failure — then let the user try again.
            var error = await manager.RunInProcessUpdateAsync(report);
            report(error ?? "Update did not complete.");
            _started = false;
            return;
        }

        // Windows: confirm we can quit BEFORE spawning the detached updater, else it
        // would wait on a pid that never exits and relaunch a second instance.
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            report("In-app update isn't available here — run update.ps1 from the install folder.");
            _started = false;
            return;
        }

        report("Updating — the app will close and reopen automatically…");
        var err = manager.StartSelfUpdate();
        if (err is not null)
        {
            report($"Update failed to start: {err}");
            _started = false;
            return;
        }
        desktop.Shutdown();
    }
}
