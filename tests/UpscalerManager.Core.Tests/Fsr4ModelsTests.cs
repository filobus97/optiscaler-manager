// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.

using UpscalerManager.Core.Components;
using UpscalerManager.Core.Models;
using UpscalerManager.Core.Services;
using Xunit;

namespace UpscalerManager.Core.Tests;

public class Fsr4ModelsTests
{
    private static string Release(string fsrSection)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fsr4keys_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "OptiScaler.ini"), "[FSR]\n" + fsrSection + "\n[FrameGen]\nEnabled=auto\n");
        return dir;
    }

    // The force key was renamed twice. A release ignores a key it does not know without a
    // word, so each release gets the name its own ini documents.
    [Theory]
    [InlineData("; 0 = No override | 1 = FP8 | 2 = INT8 | 3 = INT8-2 | 4 = INT8 Lite\nFsr4GpuSupport=auto\nFsr4Model=auto", true, Fsr4ForceKey.GpuSupport)]
    [InlineData("; 0 = No override | 1 = FP8 | 2 = INT8\nFsr4ForceModel=auto", false, Fsr4ForceKey.ForceModel)]
    [InlineData("Fsr4Update=auto", false, Fsr4ForceKey.LegacyEnableInt8)]
    public void Each_release_gets_the_keys_its_ini_documents(string fsr, bool hasModel, Fsr4ForceKey force)
        => Assert.Equal(new Fsr4IniSchema(hasModel, force), Fsr4IniSchema.Of(Release(fsr)));

    [Fact]
    public void OptiScaler_10_gets_the_model_and_an_undone_force_written_back()
    {
        var keys = Fsr4Models.IniKeys(new Fsr4IniSchema(true, Fsr4ForceKey.GpuSupport),
            Fsr4ModelChoice.Int8Rdna2, force: false, RadeonGeneration.Rdna2);
        Assert.Equal(new[] { "Fsr4Model=2", "Fsr4GpuSupport=auto" }, keys.Select(k => $"{k.Key}={k.Value}"));
    }

    // Forcing with no model picked forces the GPU's own model, or Lite where FSR 4 has none,
    // since Lite needs nothing special from the GPU.
    [Theory]
    [InlineData(RadeonGeneration.Rdna2, "3")]
    [InlineData(RadeonGeneration.Rdna3, "2")]
    [InlineData(RadeonGeneration.SteamDeck, "4")]
    [InlineData(RadeonGeneration.NotRadeon, "4")]
    [InlineData(RadeonGeneration.Rdna2Integrated, "4")]
    public void Forcing_picks_the_gpus_own_model_or_lite(RadeonGeneration generation, string value)
    {
        var keys = Fsr4Models.IniKeys(new Fsr4IniSchema(true, Fsr4ForceKey.GpuSupport),
            Fsr4ModelChoice.Auto, force: true, generation);
        Assert.Contains(keys, k => k.Key == "Fsr4GpuSupport" && k.Value == value);
        Assert.Contains(keys, k => k.Key == "Fsr4Model" && k.Value == "auto");
    }

    [Fact]
    public void A_picked_model_is_the_one_forced()
        => Assert.Contains(Fsr4Models.IniKeys(new Fsr4IniSchema(true, Fsr4ForceKey.GpuSupport),
                Fsr4ModelChoice.Int8Lite, force: true, RadeonGeneration.Rdna4),
            k => k.Key == "Fsr4GpuSupport" && k.Value == "4");

    // 0.9.5 knows FP8 and INT8 only, and has no model picker.
    [Fact]
    public void OptiScaler_0_9_5_gets_fp8_or_int8()
    {
        var schema = new Fsr4IniSchema(false, Fsr4ForceKey.ForceModel);
        Assert.Equal("2", Assert.Single(Fsr4Models.IniKeys(schema, Fsr4ModelChoice.Int8Rdna2, true, RadeonGeneration.Rdna2)).Value);
        Assert.Equal("1", Assert.Single(Fsr4Models.IniKeys(schema, Fsr4ModelChoice.Fp8, true, RadeonGeneration.Rdna3)).Value);
        Assert.Equal("auto", Assert.Single(Fsr4Models.IniKeys(schema, Fsr4ModelChoice.Auto, false, RadeonGeneration.Rdna3)).Value);
    }

    [Fact]
    public void Older_releases_get_the_old_switch_only_to_turn_it_on()
    {
        var schema = new Fsr4IniSchema(false, Fsr4ForceKey.LegacyEnableInt8);
        Assert.Empty(Fsr4Models.IniKeys(schema, Fsr4ModelChoice.Auto, false, RadeonGeneration.Rdna2));
        Assert.Equal("Fsr4ForceEnableInt8", Assert.Single(Fsr4Models.IniKeys(schema, Fsr4ModelChoice.Auto, true, RadeonGeneration.Rdna2)).Key);
    }

    [Theory]
    [InlineData(RadeonGeneration.Rdna4, Fsr4ModelChoice.Fp8)]
    [InlineData(RadeonGeneration.Rdna2, Fsr4ModelChoice.Int8Rdna2)]
    [InlineData(RadeonGeneration.Rdna3Integrated, Fsr4ModelChoice.Int8Lite)]
    public void Each_generation_has_its_model(RadeonGeneration generation, Fsr4ModelChoice model)
        => Assert.Equal(model, Fsr4Models.OfficialFor(generation));
}

public class OptiScalerChannelsTests
{
    [Theory]
    [InlineData("nightly-20261024", "24 Oct 2026  (nightly-20261024)")]
    [InlineData("0.9.5", "0.9.5")]
    [InlineData("nightly-2026", "nightly-2026")]
    public void Nightlies_read_as_their_date(string version, string shown)
        => Assert.Equal(shown, OptiScalerChannels.Describe(version));
}

public class OptiScalerSourcesTests
{
    private static AppConfiguration Full() => new()
    {
        OptiScaler = new() { RepoOwner = "optiscaler", RepoName = "OptiScaler" },
        OptiScalerBetas = new() { RepoOwner = "Agustinm28", RepoName = "OptiScaler-Betas" },
        OptiScalerNightly = new() { RepoOwner = "optiscaler", RepoName = "OptiScaler-nightly" },
        OptiScalerExtras = new() { RepoOwner = "Agustinm28", RepoName = "OptiScaler-Extras" },
        Fakenvapi = new() { RepoOwner = "optiscaler", RepoName = "fakenvapi" },
        NukemFG = new() { RepoOwner = "Nukem9", RepoName = "dlssg-to-fsr3" },
        OptiPatcher = new() { RepoOwner = "optiscaler", RepoName = "OptiPatcher" },
    };

    [Fact]
    public void A_config_from_before_the_nightlies_is_refilled()
    {
        var saved = Full();
        saved.OptiScalerNightly = new();
        Assert.True(ComponentManagementService.MissesARepository(saved));
        Assert.False(ComponentManagementService.MissesARepository(Full()));
    }

    [Theory]
    [InlineData("nightly", null, false, true)]
    [InlineData("nightly", "https://example.invalid/OptiScaler.7z", false, false)]
    [InlineData("nightly-20261024", null, true, false)]
    [InlineData("0.9.5", null, false, false)]
    public void Only_the_retired_rolling_nightly_is_dropped(string version, string? url, bool nightly, bool dropped)
        => Assert.Equal(dropped, ComponentManagementService.IsRetiredNightlyTag(
            new OptiScalerReleaseEntry { Version = version, DownloadUrl = url, IsNightly = nightly }));
}
