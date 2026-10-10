// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.

using System.Text;
using UpscalerManager.Core.Components;
using UpscalerManager.Core.Models;
using UpscalerManager.Core.Services;
using Xunit;

namespace UpscalerManager.Core.Tests;

public class OmniloaderTests
{
    private static readonly string[] Ffx =
        { "ffxConfigure", "ffxCreateContext", "ffxDestroyContext", "ffxDispatch", "ffxQuery" };

    private static string Write(byte[] bytes, string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnitest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Reads_every_exported_name()
    {
        var path = Write(PeTestData.BuildPeExporting("ffxQuery", "UpdateFfxApiProvider"), "a.dll");
        Assert.Equal(new[] { "ffxQuery", "UpdateFfxApiProvider" }, PeExports.Read(path));
    }

    [Fact]
    public void A_file_without_exports_or_not_a_dll_reads_as_none()
    {
        Assert.Empty(PeExports.Read(Write(PeTestData.BuildPe(), "plain.dll")));
        Assert.Empty(PeExports.Read(Write(Encoding.ASCII.GetBytes("not a dll at all, just text"), "text.dll")));
    }

    [Fact]
    public void An_upscaler_with_the_five_functions_goes_behind_the_omniloader()
        => Assert.Equal(OmniBuildKind.Module,
            Omniloader.Classify(Write(PeTestData.BuildPeExporting(Ffx), "amd_fidelityfx_upscaler_dx12.dll")));

    [Theory]
    [InlineData("omniloader.dll")]
    [InlineData("fsr-4.1.1b.dll")]
    public void An_upscaler_under_any_other_name_is_still_one(string name)
        => Assert.Equal(OmniBuildKind.Module, Omniloader.Classify(Write(PeTestData.BuildPeExporting(Ffx), name)));

    [Fact]
    public void A_driver_style_build_is_told_apart()
        => Assert.Equal(OmniBuildKind.DriverStyle,
            Omniloader.Classify(Write(PeTestData.BuildPeExporting("UpdateFfxApiProvider"), "amdxcffx64.dll")));

    [Theory]
    [InlineData("amd_fidelityfx_framegeneration_dx12.dll")]
    [InlineData("amd_fidelityfx_loader_dx12.dll")]
    public void Frame_generation_and_the_loader_are_not_upscalers_though_they_export_the_same(string name)
        => Assert.Equal(OmniBuildKind.NotUpscaler, Omniloader.Classify(Write(PeTestData.BuildPeExporting(Ffx), name)));

    [Fact]
    public void Anything_else_is_not_an_upscaler()
        => Assert.Equal(OmniBuildKind.NotUpscaler,
            Omniloader.Classify(Write(PeTestData.BuildPeExporting("DllMain", "SomethingElse"), "nvapi64.dll")));

    [Fact]
    public void Each_build_gets_its_own_file_name_from_its_label()
        => Assert.Equal(
            new[] { "optiscaler-s-own-fsr.dll", "faster-fsr-4.1.1-exact.dll", "your-fsr-4.1.1b.dll", "build.dll", "build-2.dll" }
                .Select(n => Omniloader.BuildPrefix + n),
            Omniloader.FileNamesFor(new[] { "OptiScaler's own FSR", "Faster FSR 4.1.1, exact", "Your fsr-4.1.1b.dll", "!!", "" }));

    // OptiScaler answers FSR 4's GPU check on Linux only for DLLs whose name starts with
    // "amd", and takes any DLL whose name ends like AMD's upscaler for the upscaler itself.
    [Theory]
    [InlineData("OptiScaler's own FSR")]
    [InlineData("Your amd_fidelityfx_upscaler_dx12.dll")]
    [InlineData("")]
    public void A_build_name_starts_like_amds_and_never_ends_like_it(string label)
    {
        var name = Omniloader.FileNamesFor(new[] { label })[0];
        Assert.StartsWith("amd", name);
        Assert.False(name.EndsWith(Omniloader.UpscalerDllName, StringComparison.OrdinalIgnoreCase), name);
        Assert.False(Path.GetFileNameWithoutExtension(name).EndsWith("amd_fidelityfx_upscaler_dx12", StringComparison.OrdinalIgnoreCase), name);
    }

    [Fact]
    public void The_list_is_written_in_order_with_windows_paths()
    {
        var ini = Omniloader.IniFor(new[] { ("a.dll", "First"), ("b.dll", "Second") });
        Assert.Contains("[Modules]\r\nomni\\upscaler\\a.dll = First\r\nomni\\upscaler\\b.dll = Second\r\n", ini);
    }

    [Fact]
    public void The_preview_shows_the_omniloader_the_list_and_every_build()
    {
        var preview = ComponentRegistry.BuildInstallPreview(Fsr4Backend.Omniloader, UpscalerSelection.NewestFsr,
            omniFiles: new[] { "omni/upscaler/a.dll  (First)" });
        Assert.Contains(preview.Files, f => f.StartsWith(Omniloader.UpscalerDllName + "  (the omniloader"));
        Assert.Contains(preview.Files, f => f.StartsWith(Omniloader.IniName));
        Assert.Contains("omni/upscaler/a.dll  (First)", preview.Files);
        Assert.Equal(1, preview.Files.Count(f => f.StartsWith(Omniloader.UpscalerDllName)));
    }
}
