// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UpscalerManager.Core.Services;

/// <summary>
/// Which kernel-level anti-cheat a game ships, if any. OptiScaler and replacement DLLs
/// change what a game loads, which is exactly what these exist to catch, so a game that
/// carries one is flagged before anything is installed into it.
/// </summary>
/// <remarks>
/// The file list follows Optiscaler-Client-Next's AntiCheatHelper; the Steam install
/// script check is the same idea, for anti-cheats whose files arrive later.
/// </remarks>
public static class AntiCheat
{
    private const string EasyAntiCheat = "Easy Anti-Cheat";
    private const string BattlEye = "BattlEye";
    private const string DenuvoAntiCheat = "Denuvo Anti-Cheat";

    private static readonly Dictionary<string, string> Markers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["start_protected_game.exe"] = EasyAntiCheat,
        ["EasyAntiCheat_EOS.exe"] = EasyAntiCheat,
        ["EasyAntiCheat_EOS_Setup.exe"] = EasyAntiCheat,
        ["EasyAntiCheat_x64.dll"] = EasyAntiCheat,
        ["EasyAntiCheat_Setup.exe"] = EasyAntiCheat,
        ["BEService.exe"] = BattlEye,
        ["BEService_x64.exe"] = BattlEye,
        ["BEClient_x64.dll"] = BattlEye,
        ["AntiCheatInstaller.exe"] = DenuvoAntiCheat,
        ["Elytra-Setup.msi"] = DenuvoAntiCheat,
    };

    /// <summary>The file names that mark a game, for the analyzer's one pass over its folder.</summary>
    public static IEnumerable<string> MarkerFiles => Markers.Keys;

    /// <summary>The anti-cheat named by any of these file names, or null.</summary>
    public static string? FromFiles(IEnumerable<string> fileNames)
    {
        foreach (var name in fileNames)
            if (Markers.TryGetValue(Path.GetFileName(name), out var found))
                return found;
        return null;
    }

    /// <summary>
    /// The anti-cheat a Steam install script in the game's root declares, or null. Steam
    /// installs some anti-cheats on first launch, so their files are not on disk until
    /// then — but the script that installs them is.
    /// </summary>
    public static string? FromSteamInstallScripts(string root)
    {
        try
        {
            foreach (var vdf in Directory.EnumerateFiles(root, "*.vdf"))
            {
                var text = File.ReadAllText(vdf);
                if (text.Contains("battleye", StringComparison.OrdinalIgnoreCase))
                    return BattlEye;
                if (text.Contains("easyanticheat", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("easy anti-cheat", StringComparison.OrdinalIgnoreCase))
                    return EasyAntiCheat;
                if (text.Contains("anticheat", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("anti-cheat", StringComparison.OrdinalIgnoreCase))
                    return "an anti-cheat";
            }
        }
        catch (Exception ex)
        {
            Log.Write($"[AntiCheat] Could not read install scripts in '{root}': {ex.Message}");
        }
        return null;
    }
}
