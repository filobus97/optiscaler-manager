// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.IO;
using System.Linq;
using Avalonia.Platform;
using UpscalerManager.Core.Logging;

namespace UpscalerManager.App.Services;

/// <summary>
/// Installs the Linux desktop entry and icon theme files.
///
/// Setting <c>Window.Icon</c> is enough on Windows and X11, but a Wayland compositor
/// never receives an icon from the application: it takes the window's app-id, looks up
/// the matching <c>.desktop</c> file, and uses the <c>Icon=</c> named there. Without
/// these files the app can only ever show a generic placeholder in the taskbar.
///
/// It also makes the app appear in the application menu, which is how it gets added as
/// a non-Steam game in the first place.
///
/// Best-effort and idempotent: writes only when something changed, and never throws
/// into startup.
/// </summary>
internal static class DesktopEntryService
{
    /// <summary>Must match the window app-id / WM_CLASS for the compositor to pair them.</summary>
    private const string AppId = "UpscalerManager";
    private const string IconName = "upscaler-manager";

    /// <summary>
    /// What the app was called before. Its entry and icons are removed when the new ones
    /// are written — otherwise a stale, broken launcher lingers in the menu, and anyone
    /// who added the app to Steam as a non-Steam game keeps pointing at a name that is
    /// no longer installed.
    /// </summary>
    private const string PreviousAppId = "OptiscalerManager";
    private const string PreviousIconName = "optiscaler-manager";

    /// <summary>Sizes shipped as Avalonia resources (see the csproj).</summary>
    private static readonly int[] IconSizes = { 16, 24, 32, 48, 64, 128, 256 };

    public static void EnsureInstalled()
    {
        if (!OperatingSystem.IsLinux()) return;

        try
        {
            // The binary moves when the user relocates the install, so always point the
            // entry at where we are actually running from.
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;

            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrEmpty(dataHome))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(home)) return;
                dataHome = Path.Combine(home, ".local", "share");
            }

            InstallIcons(dataHome);
            InstallEntry(dataHome, exe);
        }
        catch (Exception ex)
        {
            // A missing taskbar icon must never stop the app from starting.
            Log.Write($"[Desktop] Could not install the desktop entry: {ex.Message}");
        }
    }

    /// <summary>Deletes the launcher and icons written under the app's previous name.</summary>
    private static void RemovePreviousEntry(string dataHome)
    {
        try
        {
            var stale = Path.Combine(dataHome, "applications", PreviousAppId + ".desktop");
            if (File.Exists(stale)) File.Delete(stale);

            foreach (var size in IconSizes)
            {
                var icon = Path.Combine(dataHome, "icons", "hicolor", $"{size}x{size}", "apps",
                    PreviousIconName + ".png");
                if (File.Exists(icon)) File.Delete(icon);
            }
        }
        catch
        {
            // Cosmetic tidy-up; never worth failing a launch over.
        }
    }

    private static void InstallIcons(string dataHome)
    {
        var changed = false;
        foreach (var size in IconSizes)
        {
            var uri = new Uri($"avares://UpscalerManager/Assets/icon-{size}.png");
            if (!AssetLoader.Exists(uri)) continue;

            var dir = Path.Combine(dataHome, "icons", "hicolor", $"{size}x{size}", "apps");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, IconName + ".png");

            using var source = AssetLoader.Open(uri);
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            var bytes = buffer.ToArray();

            // Rewrite only on change, so we are not touching the icon cache every launch.
            if (File.Exists(dest) && File.ReadAllBytes(dest).SequenceEqual(bytes)) continue;
            File.WriteAllBytes(dest, bytes);
            changed = true;
        }

        if (changed) RefreshIconCache(dataHome);
    }

    /// <summary>
    /// Tells the desktop that the icons on disk have changed.
    ///
    /// Writing the PNGs is not enough: GNOME and KDE both read the icon theme through a
    /// cache, so a changed icon keeps showing the old image in the taskbar and the
    /// window bar — sometimes until the next login. That is exactly what happened when
    /// this app's mark changed: the in-app header updated with the binary while the
    /// launcher kept the previous one.
    ///
    /// Every step is best-effort. None of these tools is guaranteed to exist, and a
    /// missing taskbar icon must never stop the app from starting, so nothing here is
    /// waited on or allowed to throw.
    /// </summary>
    private static void RefreshIconCache(string dataHome)
    {
        var hicolor = Path.Combine(dataHome, "icons", "hicolor");

        // Some desktops only re-read when the theme directory's own timestamp moves.
        try { Directory.SetLastWriteTimeUtc(hicolor, DateTime.UtcNow); } catch { }

        // -t skips the index.theme check: a per-user hicolor directory rarely has one,
        // and without the flag the cache update simply refuses.
        foreach (var (exe, args) in new[]
                 {
                     ("gtk-update-icon-cache", $"-t -f -q \"{hicolor}\""),
                     ("xdg-desktop-menu", "forceupdate"),
                 })
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(exe, args)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    });
            }
            catch
            {
                // Not installed on this system; the next login picks the icons up anyway.
            }
        }

        Log.Write("[Desktop] Icons changed; asked the desktop to refresh its icon cache.");
    }

    private static void InstallEntry(string dataHome, string exe)
    {
        var dir = Path.Combine(dataHome, "applications");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, AppId + ".desktop");

        // Exec fields quote with double quotes and escape backslashes (desktop spec).
        var quoted = "\"" + exe.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        var entry = string.Join('\n',
            "[Desktop Entry]",
            "Type=Application",
            "Name=Upscaler Manager",
            "GenericName=Upscaler manager",
            "Comment=Manage the upscalers in your games — swap DLSS, FSR and XeSS DLLs, or install OptiScaler",
            $"Exec={quoted}",
            $"Icon={IconName}",
            "Terminal=false",
            "Categories=Utility;Game;",
            "Keywords=upscaler;DLSS;FSR;XeSS;OptiScaler;frame generation;",
            $"StartupWMClass={AppId}",
            "");

        if (File.Exists(dest) && File.ReadAllText(dest) == entry)
        {
            RemovePreviousEntry(dataHome);
            return;
        }
        File.WriteAllText(dest, entry);
        RemovePreviousEntry(dataHome);
        Log.Write($"[Desktop] Installed desktop entry: {dest}");
    }
}
