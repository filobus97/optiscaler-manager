// OptiScaler Client - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System;
using System.IO;
using System.Linq;
using OptiscalerManager.Core.Services;
using Xunit;

namespace OptiscalerManager.Core.Tests
{
    /// <summary>
    /// Section-aware OptiScaler.ini editing used by the custom FSR components.
    /// Editing ini text by hand is fiddly, so these pin the create/update/insert paths.
    /// </summary>
    public class IniEditingTests : IDisposable
    {
        private readonly string _dir;
        private string IniPath => Path.Combine(_dir, "OptiScaler.ini");

        public IniEditingTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "initest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        private string? ValueOf(string section, string key)
        {
            bool inSection = false;
            foreach (var raw in File.ReadAllLines(IniPath))
            {
                var line = raw.Trim();
                if (line.StartsWith("["))
                {
                    inSection = line.Equals($"[{section}]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inSection) continue;
                var eq = line.IndexOf('=');
                if (eq > 0 && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return line[(eq + 1)..].Trim();
            }
            return null;
        }

        [Fact]
        public void CreatesFileAndSectionWhenMissing()
        {
            GameInstallationService.ModifyOptiScalerIniKey(_dir, "FSR", "UpscalerIndex", "0");
            Assert.True(File.Exists(IniPath));
            Assert.Equal("0", ValueOf("FSR", "UpscalerIndex"));
        }

        [Fact]
        public void UpdatesExistingKeyInPlace()
        {
            File.WriteAllText(IniPath, "[FSR]\nUpscalerIndex=auto\nFsr4Update=auto\n");
            GameInstallationService.ModifyOptiScalerIniKey(_dir, "FSR", "UpscalerIndex", "0");
            Assert.Equal("0", ValueOf("FSR", "UpscalerIndex"));
            // The sibling key is untouched
            Assert.Equal("auto", ValueOf("FSR", "Fsr4Update"));
            // No duplicate key was appended
            var count = File.ReadAllLines(IniPath)
                .Count(l => l.Trim().StartsWith("UpscalerIndex=", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(1, count);
        }

        [Fact]
        public void InsertsKeyIntoExistingSection()
        {
            File.WriteAllText(IniPath, "[General]\nFoo=bar\n\n[FSR]\nFsr4Update=auto\n");
            GameInstallationService.ModifyOptiScalerIniKey(_dir, "FSR", "UpscalerIndex", "0");
            Assert.Equal("0", ValueOf("FSR", "UpscalerIndex"));
            Assert.Equal("bar", ValueOf("General", "Foo"));
        }

        [Fact]
        public void AppendsSectionWhenAbsentFromExistingFile()
        {
            File.WriteAllText(IniPath, "[General]\nFoo=bar\n");
            GameInstallationService.ModifyOptiScalerIniKey(_dir, "FSR", "Fsr4Update", "true");
            Assert.Equal("true", ValueOf("FSR", "Fsr4Update"));
            Assert.Equal("bar", ValueOf("General", "Foo"));
        }

        [Fact]
        public void DoesNotMatchKeyInDifferentSection()
        {
            // A key named UpscalerIndex under [Other] must not be changed when we target [FSR]
            File.WriteAllText(IniPath, "[Other]\nUpscalerIndex=99\n\n[FSR]\n");
            GameInstallationService.ModifyOptiScalerIniKey(_dir, "FSR", "UpscalerIndex", "0");
            Assert.Equal("99", ValueOf("Other", "UpscalerIndex"));
            Assert.Equal("0", ValueOf("FSR", "UpscalerIndex"));
        }
    }
}
