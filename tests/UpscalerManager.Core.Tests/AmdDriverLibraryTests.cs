// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.

using System.IO.Compression;
using UpscalerManager.Core.Components;
using UpscalerManager.Core.Services;
using Xunit;

namespace UpscalerManager.Core.Tests;

public class AmdDriverLibraryTests
{
    // A 7-Zip archive laid out like AMD's driver installer: the DirectX 12 file twice (one
    // copy per hardware family) and the Vulkan file once, both at version 2.4.0.1234, under
    // Packages/Drivers/Display/<family>_INF/<id>/. Made with py7zr from PeTestData's
    // BuildVersionedPeExporting("2.4.0.1234", "UpdateFfxApiProvider", ...).
    private const string InstallerLike7z =
        "N3q8ryccAASnpZgRlAEAAAAAAAAXAAAAAAAAAApKaLDgGLsAtl0AIJNEjG3Joqz2Hlvd4G9cOg5TIYsAbAotZV9s3xKD/bGBj7z7yzRHJ5hFzGjo2h7KfyUtQS7QnC7Ze4P6+1McZqVghUwI0zq8IYBZAXdd0Khy2hNrJYu4DgrS8Ek5M/asPOYl9H0Gz1hBcAi7bqCxpxTe8fWmWBAWS4itMVD62UD27R0ZVj2XMeE7KtI/WHOczEyg/YMZYQ+7Vt5Y2iW8wWkIK4vss42mc7FUSLOtzUmHrwFqJgAA4AHrAM5dAACBMweuD9UxByiXJNHP4/dk0VrJb9+AmlPahKZYiH0kZ58wWWEhJrS4U8h+mpydGZ9usi4S/bOAIMGbI3UgXSSdxEHk4mmDqIxRMUC5GrRk8e3o3g3eTdVjKSjQ1pXwsiQCCTAulb8kZWvjLhG3JLNwrtbP7b/HzqBVaLjSYGFsnffg2e6o9OqYSnKHgzvz5llEgx21Wyq4P0brqNCwyLb8ORkRDJaKjWVVKdweTah5JJR5ck1PvMSKGhGrE8l4D7g9DaF45KDJptt4zAAAABcGgL4BCYDWAAcLAQABISEBGAyB7AAA";

    private static string NewDir(string tag) =>
        Path.Combine(Path.GetTempPath(), $"amddrv_{tag}_" + Guid.NewGuid().ToString("N"));

    private static byte[] DriverDll(string version = "2.4.0.1234") =>
        PeTestData.BuildVersionedPeExporting(version, AmdDriverFiles.ProviderExport, "UpdateFfxApiProviderEx");

    private static string WriteFile(string name, byte[] bytes)
    {
        var dir = NewDir("src");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task A_driver_file_is_kept_by_version()
    {
        var library = new AmdDriverLibrary(NewDir("lib"));
        var result = await library.ImportAsync(new[] { WriteFile("amdxcffx64.dll", DriverDll()) });

        var file = Assert.Single(result.Added);
        Assert.Equal(AmdDriverApi.Dx12, file.Api);
        Assert.Equal("2.4.0.1234", file.Version);
        Assert.Equal(file, Assert.Single(library.List()));
    }

    // A community build in the driver file's slot is a renamed FidelityFX module: it has the
    // five ffx functions and no provider export, so it does not count as AMD's file.
    [Fact]
    public async Task A_community_build_under_the_driver_name_is_turned_away()
    {
        var library = new AmdDriverLibrary(NewDir("lib"));
        var result = await library.ImportAsync(new[]
        {
            WriteFile("amdxcffx64.dll", PeTestData.BuildVersionedPeExporting("4.0.2.0", "ffxCreateContext", "ffxQuery")),
        });

        Assert.Empty(result.Added);
        Assert.Contains(result.Notes, n => n.Contains("not AMD's driver file"));
    }

    [Fact]
    public async Task A_folder_is_searched_all_the_way_down()
    {
        var root = NewDir("tree");
        var deep = Path.Combine(root, "Packages", "Drivers", "Display", "WT6A_INF", "B1");
        Directory.CreateDirectory(deep);
        File.WriteAllBytes(Path.Combine(deep, "amdxcffx64.dll"), DriverDll());
        File.WriteAllBytes(Path.Combine(deep, "amdvkffx64.dll"), DriverDll());
        File.WriteAllBytes(Path.Combine(deep, "atidxx64.dll"), PeTestData.BuildPe());

        var library = new AmdDriverLibrary(NewDir("lib"));
        var result = await library.ImportAsync(new[] { root });

        Assert.Equal(new[] { AmdDriverApi.Dx12, AmdDriverApi.Vulkan }, result.Added.Select(a => a.Api).OrderBy(a => a));
    }

    [Fact]
    public async Task A_zip_is_read()
    {
        var zipPath = Path.Combine(NewDir("zip") + ".zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        using (var stream = zip.CreateEntry("driver/amdxcffx64.dll").Open())
            stream.Write(DriverDll("2.4.1.0"));

        var library = new AmdDriverLibrary(NewDir("lib"));
        var result = await library.ImportAsync(new[] { zipPath });

        Assert.Equal("2.4.1.0", Assert.Single(result.Added).Version);
    }

    // The same file appears once per hardware family in a driver package; one copy is kept.
    [Fact]
    public async Task A_7z_laid_out_like_amds_installer_gives_each_file_once()
    {
        var path = NewDir("7z") + ".7z";
        File.WriteAllBytes(path, Convert.FromBase64String(InstallerLike7z));

        var library = new AmdDriverLibrary(NewDir("lib"));
        var result = await library.ImportAsync(new[] { path });

        Assert.Equal(2, result.Added.Count);
        Assert.Contains(result.Added, a => a.Api == AmdDriverApi.Dx12 && a.Version == "2.4.0.1234");
        Assert.Contains(result.Added, a => a.Api == AmdDriverApi.Vulkan && a.Version == "2.4.0.1234");
    }

    // AMD's installer is a program with the 7-Zip archive appended. The program is stood in
    // for by bytes that include a false start of the signature, which must not be taken.
    [Fact]
    public async Task An_installer_with_the_archive_behind_a_program_is_read()
    {
        var stub = new byte[70_000];
        new Random(4).NextBytes(stub);
        stub[0] = (byte)'M';
        stub[1] = (byte)'Z';
        "7z"u8.ToArray().CopyTo(stub, 1234);
        new byte[] { 0xBC, 0xAF, 0x27, 0x1C, 9 }.CopyTo(stub, 1236);

        var path = NewDir("exe") + ".exe";
        File.WriteAllBytes(path, stub.Concat(Convert.FromBase64String(InstallerLike7z)).ToArray());

        var library = new AmdDriverLibrary(NewDir("lib"));
        var result = await library.ImportAsync(new[] { path });

        Assert.Equal(2, result.Added.Count);
    }

    [Fact]
    public async Task Importing_again_says_so_and_keeps_one_copy()
    {
        var library = new AmdDriverLibrary(NewDir("lib"));
        var file = WriteFile("amdxcffx64.dll", DriverDll());
        await library.ImportAsync(new[] { file });
        var again = await library.ImportAsync(new[] { file });

        Assert.Empty(again.Added);
        Assert.Contains(again.Notes, n => n.Contains("already imported"));
        Assert.Single(library.List());
    }

    [Fact]
    public async Task Versions_are_listed_newest_first_and_can_be_removed()
    {
        var library = new AmdDriverLibrary(NewDir("lib"));
        await library.ImportAsync(new[] { WriteFile("amdxcffx64.dll", DriverDll("2.3.9.0")) });
        await library.ImportAsync(new[] { WriteFile("amdxcffx64.dll", DriverDll("2.10.0.0")) });

        Assert.Equal(new[] { "2.10.0.0", "2.3.9.0" }, library.List().Select(f => f.Version));
        Assert.Equal("2.10.0.0", library.Newest(AmdDriverApi.Dx12)!.Version);

        library.Delete(library.List()[0]);
        Assert.Equal("2.3.9.0", Assert.Single(library.List()).Version);
    }

    [Fact]
    public async Task Something_that_is_not_an_archive_says_so()
    {
        var library = new AmdDriverLibrary(NewDir("lib"));
        var result = await library.ImportAsync(new[] { WriteFile("notes.txt", "hello"u8.ToArray()) });
        Assert.Empty(result.Added);
        Assert.Contains(result.Notes, n => n.Contains("holds no"));
    }
}
