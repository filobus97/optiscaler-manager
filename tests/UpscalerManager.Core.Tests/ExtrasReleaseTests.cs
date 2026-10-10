// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.

using System.IO.Compression;
using UpscalerManager.Core.Services;
using Xunit;

namespace UpscalerManager.Core.Tests;

public class ExtrasReleaseTests
{
    private static string Zip(params (string Entry, byte[] Bytes)[] files)
    {
        var path = Path.Combine(Path.GetTempPath(), "extras_" + Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, bytes) in files)
        {
            using var stream = zip.CreateEntry(entry).Open();
            stream.Write(bytes);
        }
        return path;
    }

    private static string NewDir() => Path.Combine(Path.GetTempPath(), "extras_out_" + Guid.NewGuid().ToString("N"));

    // Every Extras release ships the INT8 build as amd_fidelityfx_upscaler_dx12.dll or
    // amdxcffx64.dll — names the swap screen has not offered since v0.29.0, which is
    // what made the unpack throw them away.
    [Theory]
    [InlineData("amd_fidelityfx_upscaler_dx12.dll")]
    [InlineData("FSR4_INT8_4.1.1b/amdxcffx64.dll")]
    public void The_int8_build_is_unpacked_though_fsr_is_no_longer_swapped(string entry)
    {
        var dir = NewDir();
        var written = ComponentManagementService.ExtractExtrasRelease(
            Zip((entry, PeTestData.BuildPe()), ("readme.txt", "hi"u8.ToArray())), dir, "test");

        var name = Path.GetFileName(entry);
        Assert.Equal(new[] { name }, written);
        Assert.True(File.Exists(Path.Combine(dir, name)));
        Assert.Equal(Path.Combine(dir, name), UpscalerManager.Core.Components.Fsr4Int8Build.FindIn(dir));
    }

    [Fact]
    public void What_the_archive_held_is_kept_for_the_error()
    {
        var dir = NewDir();
        ComponentManagementService.ExtractExtrasRelease(Zip(("notes/readme.txt", "hi"u8.ToArray())), dir, "test");
        Assert.Contains("notes/readme.txt", File.ReadAllLines(Path.Combine(dir, "archive-contents.txt")));
    }
}
