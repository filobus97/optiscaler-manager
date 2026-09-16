// Upscaler Manager - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System;
using System.IO;
using UpscalerManager.Core.Services;
using Xunit;

namespace UpscalerManager.Core.Tests
{
    /// <summary>
    /// An ASI plugin in the wrong folder fails silently: OptiScaler writes one debug
    /// line and returns. So these pin the folder resolution against OptiScaler's own
    /// rules, which changed when releases started shipping an OptiScaler subfolder.
    /// </summary>
    public class OptiPatcherLayoutTests : IDisposable
    {
        private readonly string _game = Path.Combine(Path.GetTempPath(), "osm_plug_" + Guid.NewGuid().ToString("N"));

        public OptiPatcherLayoutTests() => Directory.CreateDirectory(_game);
        public void Dispose() { try { Directory.Delete(_game, true); } catch { } }

        private void WriteIni(string body) => File.WriteAllText(Path.Combine(_game, "OptiScaler.ini"), body);

        [Fact]
        public void OnAFlatInstallItIsTheGameFolder()
        {
            WriteIni("[Libraries]\nOptiDllPath=auto\n\n[Plugins]\nPath=auto\n");

            Assert.Equal(Path.Combine(_game, "plugins"),
                GameInstallationService.OptiScalerPluginsDirectory(_game));
        }

        [Fact]
        public void OnAReleaseThatShipsItsOwnSubfolderItMovesThere()
        {
            // The break: up to 0.9.4 the plugin belonged in <game>/plugins, and from the
            // release laying down this folder OptiScaler reads <game>/OptiScaler/plugins.
            Directory.CreateDirectory(Path.Combine(_game, "OptiScaler"));
            WriteIni("[Libraries]\nOptiDllPath=auto\n");

            Assert.Equal(Path.Combine(_game, "OptiScaler", "plugins"),
                GameInstallationService.OptiScalerPluginsDirectory(_game));
        }

        [Fact]
        public void AConfiguredDllPathWinsOverBoth()
        {
            Directory.CreateDirectory(Path.Combine(_game, "OptiScaler"));
            Directory.CreateDirectory(Path.Combine(_game, "Mods"));
            WriteIni("[Libraries]\nOptiDllPath=Mods\n");

            Assert.Equal(Path.Combine(_game, "Mods", "plugins"),
                GameInstallationService.OptiScalerPluginsDirectory(_game));
        }

        [Fact]
        public void AConfiguredPluginPathWinsOverEverything()
        {
            Directory.CreateDirectory(Path.Combine(_game, "OptiScaler"));
            var asi = Path.Combine(_game, "asi");
            Directory.CreateDirectory(asi);
            WriteIni("[Libraries]\nOptiDllPath=auto\n\n[Plugins]\nPath=asi\n");

            Assert.Equal(asi, GameInstallationService.OptiScalerPluginsDirectory(_game));
        }

        [Fact]
        public void AConfiguredPathThatIsNotThereIsIgnored()
        {
            // OptiScaler falls back the same way rather than trusting the key.
            WriteIni("[Plugins]\nPath=nowhere\n");

            Assert.Equal(Path.Combine(_game, "plugins"),
                GameInstallationService.OptiScalerPluginsDirectory(_game));
        }

        [Fact]
        public void WithNoIniAtAllItStillAnswers()
        {
            Assert.Equal(Path.Combine(_game, "plugins"),
                GameInstallationService.OptiScalerPluginsDirectory(_game));
        }

        [Theory]
        [InlineData("auto", null)]
        [InlineData("", null)]
        [InlineData("Mods", "Mods")]
        public void AutoMeansUnsetRatherThanAPath(string written, string? expected)
        {
            WriteIni($"[Libraries]\nOptiDllPath={written}\n");

            Assert.Equal(expected, GameInstallationService.ReadIniValue(_game, "Libraries", "OptiDllPath"));
        }

        [Fact]
        public void ACommentedKeyIsNotAValue()
        {
            WriteIni("[Libraries]\n; OptiDllPath=Mods\n");

            Assert.Null(GameInstallationService.ReadIniValue(_game, "Libraries", "OptiDllPath"));
        }
    }
}
