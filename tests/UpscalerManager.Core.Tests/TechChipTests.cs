// Upscaler Manager - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System.Collections.Generic;
using System.Linq;
using UpscalerManager.Core.Models;
using Xunit;

namespace UpscalerManager.Core.Tests
{
    /// <summary>
    /// What a game card says it found.
    ///
    /// These exist because the card used to read the game's summary version fields,
    /// which deliberately skip files this app installed — so reinstalling OptiScaler
    /// made the FSR chip disappear from a game that plainly had FSR in it. The chips
    /// are built from the per-file detection now, and these pin that.
    /// </summary>
    public class TechChipTests
    {
        private static Game GameWith(params (string tech, string vendor, TechRole role, string? version)[] parts)
        {
            var game = new Game { Name = "Test", InstallPath = "/games/test" };
            foreach (var (tech, vendor, role, version) in parts)
            {
                game.DetectedComponents.Add(new DetectedComponent
                {
                    Technology = tech, Vendor = vendor, Role = role, Version = version,
                    FileName = tech + ".dll",
                });
            }
            return game;
        }

        [Fact]
        public void AnUpscalerThisAppInstalledStillAppears()
        {
            // The regression: OptiScaler's own FSR library is a Manager file, and the
            // summary fields skip those. A card must still show it.
            var game = GameWith(("FSR (FidelityFX upscaler)", "AMD", TechRole.Upscaler, "4.0.2.1"));
            game.DetectedComponents[0].Source = ComponentSource.Manager;

            var chip = Assert.Single(TechChip.For(game));
            Assert.Equal("FSR", chip.Label);
            Assert.Equal(TechVendor.Amd, chip.Vendor);
        }

        [Fact]
        public void OneChipPerTechnologyHoweverManyFilesCarryIt()
        {
            var game = GameWith(
                ("FSR (FidelityFX upscaler)", "AMD", TechRole.Upscaler, "3.1.0"),
                ("FSR (FidelityFX upscaler)", "AMD", TechRole.Upscaler, "4.1.1.2740"),
                ("DLSS", "Nvidia", TechRole.Upscaler, "310.2.0"));

            var chips = TechChip.For(game);
            Assert.Equal(2, chips.Count);
            Assert.Equal("DLSS", chips[0].Label);
            Assert.Equal("FSR", chips[1].Label);
        }

        [Fact]
        public void NoVersionIsEverPutOnAChip()
        {
            // Two FSR files at different versions is the normal case once OptiScaler is
            // installed, and a card cannot know which one will load. Naming either is a
            // claim the app cannot support — it read "FSR 4.1.1" on a game that had just
            // had the 4.0.2 community build installed. Versions live on the game's page,
            // per file, where they are attributable.
            var game = GameWith(
                ("FSR (FidelityFX upscaler)", "AMD", TechRole.Upscaler, "4.0.2.1"),
                ("FSR 3 (older API)", "AMD", TechRole.Upscaler, "4.1.1.2740"));

            var chip = Assert.Single(TechChip.For(game));
            Assert.Equal("FSR", chip.Label);
            Assert.DoesNotContain("4.", chip.Label);
        }

        [Fact]
        public void AFileWithNoVersionStillGetsAChip()
        {
            var game = GameWith(("XeSS", "Intel", TechRole.Upscaler, null));
            var chip = Assert.Single(TechChip.For(game));
            Assert.Equal("XeSS", chip.Label);
            Assert.Equal(TechVendor.Intel, chip.Vendor);
        }

        [Fact]
        public void RuntimesAndLatencyLibrariesAreNotChips()
        {
            // A card has room for what you choose between, not every support library.
            var game = GameWith(
                ("FidelityFX runtime (DX12)", "AMD", TechRole.Runtime, "2.3.0"),
                ("XeLL", "Intel", TechRole.LatencyReduction, "1.3.0"),
                ("DLSS", "Nvidia", TechRole.Upscaler, "310.2"));

            var chip = Assert.Single(TechChip.For(game));
            Assert.Equal("DLSS", chip.Label);
        }

        [Fact]
        public void UpscalersComeBeforeFrameGeneration()
        {
            var game = GameWith(
                ("Nukem DLSSG-to-FSR3", "Nukem (mod)", TechRole.FrameGeneration, null),
                ("DLSS Frame Generation", "Nvidia", TechRole.FrameGeneration, "310.1"),
                ("XeSS", "Intel", TechRole.Upscaler, "2.0.2"),
                ("DLSS", "Nvidia", TechRole.Upscaler, "310.2"));

            Assert.Equal(new[] { "DLSS", "XeSS", "DLSS FG", "Nukem FG" },
                TechChip.For(game).Select(c => c.Label));
        }

        [Fact]
        public void AGameWithNothingDetectedHasNoChips()
            => Assert.Empty(TechChip.For(new Game { Name = "Bare", InstallPath = "/games/bare" }));

        [Theory]
        [InlineData("Nvidia", TechVendor.Nvidia)]
        [InlineData("AMD", TechVendor.Amd)]
        [InlineData("Intel", TechVendor.Intel)]
        [InlineData("Nukem (mod)", TechVendor.Other)]
        [InlineData("OptiScaler project", TechVendor.Other)]
        public void VendorsMapToTheirColourGroup(string vendor, TechVendor expected)
            => Assert.Equal(expected, TechChip.VendorOf(vendor));

        [Theory]
        [InlineData("DLSS Frame Generation", "DLSS FG")]
        [InlineData("DLSS Ray Reconstruction", "DLSS RR")]
        [InlineData("FSR (FidelityFX upscaler)", "FSR")]
        [InlineData("FSR 3 (older API)", "FSR")]
        [InlineData("XeSS Frame Generation", "XeSS FG")]
        public void LongCatalogueNamesShortenForAChip(string full, string expected)
            => Assert.Equal(expected, TechChip.ShortName(full));
    }
}
