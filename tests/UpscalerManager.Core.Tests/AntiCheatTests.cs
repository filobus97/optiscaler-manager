// Upscaler Manager - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System;
using System.IO;
using UpscalerManager.Core.Services;
using Xunit;

namespace UpscalerManager.Core.Tests
{
    /// <summary>
    /// A game with kernel-level anti-cheat is flagged before anything goes into it:
    /// OptiScaler and replacement DLLs change what the game loads, which is exactly what
    /// those systems look for.
    /// </summary>
    public class AntiCheatTests : IDisposable
    {
        private readonly string _game = Path.Combine(Path.GetTempPath(), "osm_ac_" + Guid.NewGuid().ToString("N"));

        public AntiCheatTests() => Directory.CreateDirectory(_game);
        public void Dispose() { try { Directory.Delete(_game, true); } catch { } }

        [Theory]
        [InlineData("start_protected_game.exe", "Easy Anti-Cheat")]
        [InlineData("easyanticheat_x64.dll", "Easy Anti-Cheat")]
        [InlineData("BEService_x64.exe", "BattlEye")]
        [InlineData("AntiCheatInstaller.exe", "Denuvo Anti-Cheat")]
        public void AKnownFileNamesIt(string file, string expected)
            => Assert.Equal(expected, AntiCheat.FromFiles(new[] { "Binaries/Win64/game.exe", "x/" + file }));

        [Fact]
        public void AnOrdinaryGameHasNone()
            => Assert.Null(AntiCheat.FromFiles(new[] { "game.exe", "nvngx_dlss.dll" }));

        [Fact]
        public void ASteamScriptDeclaresOneThatIsNotOnDiskYet()
        {
            File.WriteAllText(Path.Combine(_game, "installscript.vdf"),
                "\"InstallScript\"\n{\n  \"Run Process\"\n  {\n    \"BattlEye\"\n    {\n      \"process 1\" \"%INSTALLDIR%\\\\BattlEye\\\\Install_BattlEye.bat\"\n    }\n  }\n}\n");

            Assert.Equal("BattlEye", AntiCheat.FromSteamInstallScripts(_game));
        }

        [Fact]
        public void AScriptThatInstallsSomethingElseIsNotAnAntiCheat()
        {
            File.WriteAllText(Path.Combine(_game, "installscript.vdf"),
                "\"InstallScript\"\n{\n  \"Run Process\"\n  {\n    \"DirectX\" {}\n  }\n}\n");

            Assert.Null(AntiCheat.FromSteamInstallScripts(_game));
        }
    }
}
