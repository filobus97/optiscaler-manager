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
    /// <summary>
    /// Starts the updater and, on success, shuts the app down. <paramref name="report"/>
    /// receives a status/error message for the caller's UI. Returns true when the
    /// shutdown was initiated (the app is closing), false when it stayed open.
    /// </summary>
    public static bool StartAndShutdown(ManagerService manager, Action<string> report)
    {
        report("Updating — the app will close and reopen automatically…");

        var error = manager.StartSelfUpdate();
        if (error is not null)
        {
            report($"Update failed to start: {error}");
            return false;
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        return true;
    }
}
