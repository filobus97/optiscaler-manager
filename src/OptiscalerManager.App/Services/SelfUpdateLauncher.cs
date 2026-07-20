// OptiScaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace OptiscalerManager.App.Services;

/// <summary>
/// Shared "Update now" flow used by both the main-window banner and the Settings
/// card: launch the detached updater (which waits for this process, updates in
/// place, and relaunches the app), then shut the app down. On any failure to
/// start the updater the app stays open and the error is reported.
/// </summary>
public static class SelfUpdateLauncher
{
    // Shutdown is asynchronous, so a fast second click (or a controller Enter-repeat)
    // could otherwise spawn a second detached updater racing the first. Once we begin,
    // every later call is a no-op.
    private static bool _started;

    /// <summary>
    /// Starts the updater and, on success, shuts the app down. <paramref name="report"/>
    /// receives a status/error message for the caller's UI. Returns true when the
    /// shutdown was initiated (the app is closing), false when it stayed open.
    /// </summary>
    public static bool StartAndShutdown(ManagerService manager, Action<string> report)
    {
        if (_started) return true; // already updating; ignore the repeat click

        // Confirm we can actually quit BEFORE spawning the updater — otherwise the
        // detached script would wait on a pid that never exits, then (with
        // --relaunch) launch a second instance alongside this still-running one.
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            report("In-app update isn't available here — run update.sh / update.ps1 from the install folder.");
            return false;
        }

        report("Updating — the app will close and reopen automatically…");

        var error = manager.StartSelfUpdate();
        if (error is not null)
        {
            report($"Update failed to start: {error}");
            return false;
        }

        _started = true;
        desktop.Shutdown();
        return true;
    }
}
