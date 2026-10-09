// Upscaler Manager - UI harness
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using UpscalerManager.Core.Tests;

namespace UpscalerManager.UiHarness;

/// <summary>
/// A small Steam library on disk: real app IDs (so covers load where the network allows),
/// DLLs carrying real version resources, and one game with Easy Anti-Cheat.
/// </summary>
internal static class SampleLibrary
{
    private sealed record SampleGame(string AppId, string Name, string Dir, string ExeDir, (string File, string? Version)[] Files);

    private static readonly SampleGame[] Games =
    {
        new("1091500", "Cyberpunk 2077", "Cyberpunk 2077", "bin/x64", new (string, string?)[]
        {
            ("Cyberpunk2077.exe", null),
            ("nvngx_dlss.dll", "310.1.0.0"), ("nvngx_dlssg.dll", "310.1.0.0"),
            ("libxess.dll", "2.0.1.41"), ("amd_fidelityfx_dx12.dll", "1.0.1.41314"),
        }),
        new("3489700", "Stellar Blade", "StellarBlade", "SB/Binaries/Win64", new (string, string?)[]
        {
            ("SB-Win64-Shipping.exe", null),
            ("nvngx_dlss.dll", "310.2.1.0"), ("libxess.dll", "1.3.1.33"),
        }),
        new("594650", "Hunt: Showdown 1896", "Hunt Showdown", "bin/win_x64", new (string, string?)[]
        {
            ("HuntGame.exe", null), ("nvngx_dlss.dll", "3.7.10.0"),
            ("../../EasyAntiCheat/EasyAntiCheat_EOS_Setup.exe", null),
            ("../../start_protected_game.exe", null),
        }),
        new("990080", "Hogwarts Legacy", "Hogwarts Legacy", "Phoenix/Binaries/Win64", new (string, string?)[]
        {
            ("HogwartsLegacy.exe", null),
            ("nvngx_dlss.dll", "3.5.0.0"), ("libxess.dll", "1.1.0.0"),
        }),
        new("1145350", "Hades II", "Hades II", "Ship", new (string, string?)[]
        {
            ("Hades2.exe", null),
        }),
    };

    public static void Build(string home)
    {
        var steam = Path.Combine(home, ".local", "share", "Steam");
        var steamapps = Path.Combine(steam, "steamapps");
        Directory.CreateDirectory(steamapps);

        File.WriteAllText(Path.Combine(steamapps, "libraryfolders.vdf"),
            $"\"libraryfolders\"\n{{\n\t\"0\"\n\t{{\n\t\t\"path\"\t\t\"{steam}\"\n\t}}\n}}\n");

        foreach (var game in Games)
        {
            File.WriteAllText(Path.Combine(steamapps, $"appmanifest_{game.AppId}.acf"),
                $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{game.AppId}\"\n\t\"name\"\t\t\"{game.Name}\"\n\t\"installdir\"\t\t\"{game.Dir}\"\n}}\n");

            var exeDir = Path.Combine(steamapps, "common", game.Dir, game.ExeDir);
            Directory.CreateDirectory(exeDir);
            foreach (var (file, version) in game.Files)
            {
                var path = Path.GetFullPath(Path.Combine(exeDir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, version is null && !file.EndsWith(".dll")
                    ? PeTestData.BuildPe()
                    : PeTestData.BuildPe(fileVersion: version));
            }
        }
    }
}
