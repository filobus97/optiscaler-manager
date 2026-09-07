// OptiScaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.IO;
using System.Linq;
using Avalonia.Platform;
using OptiscalerManager.Core.Logging;

namespace OptiscalerManager.App.Services;

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
    private const string AppId = "OptiscalerManager";
    private const string IconName = "optiscaler-manager";

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

    private static void InstallIcons(string dataHome)
    {
        foreach (var size in IconSizes)
        {
            var uri = new Uri($"avares://OptiscalerManager/Assets/icon-{size}.png");
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
        }
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
            "Name=OptiScaler Manager",
            "GenericName=FSR 4 mod installer",
            "Comment=Install OptiScaler and get FSR 4 working in your games",
            $"Exec={quoted}",
            $"Icon={IconName}",
            "Terminal=false",
            "Categories=Utility;Game;",
            "Keywords=OptiScaler;FSR;FSR4;upscaler;",
            $"StartupWMClass={AppId}",
            "");

        if (File.Exists(dest) && File.ReadAllText(dest) == entry) return;
        File.WriteAllText(dest, entry);
        Log.Write($"[Desktop] Installed desktop entry: {dest}");
    }
}
