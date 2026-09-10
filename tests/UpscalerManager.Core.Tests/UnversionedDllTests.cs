// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System.IO;
using UpscalerManager.Core.Models;
using UpscalerManager.Core.Services;
using Xunit;

namespace UpscalerManager.Core.Tests;

/// <summary>
/// A DLL that carries no version resource reads back as 0.0.0.0. The game still has the
/// technology; it just does not say which version — and the UI must not invent one.
/// </summary>
[Collection(AppDataCollection.Name)]
public class UnversionedDllTests : IDisposable
{
    private readonly ScopedAppData _appData = new();

    public void Dispose() => _appData.Dispose();

    [Fact]
    public void TechnologyIsDetectedButNoVersionIsClaimed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "unversioned_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            // Empty stand-ins: present on disk, no version resource to read.
            File.WriteAllBytes(Path.Combine(dir, "nvngx_dlss.dll"), new byte[0]);
            File.WriteAllBytes(Path.Combine(dir, "libxess.dll"), new byte[0]);

            var game = new Game { Name = "Unversioned", InstallPath = dir };
            new GameAnalyzerService().AnalyzeGame(game, forceRefresh: true);

            Assert.NotNull(game.DlssPath);
            Assert.Null(game.DlssVersion);
            Assert.NotNull(game.XessPath);
            Assert.Null(game.XessVersion);

            // The game must still count as having an upscaler, or the "hide games
            // without upscalers" filter would make it disappear.
            Assert.True(game.HasUpscaler);

            // And the per-file list agrees with the summary.
            foreach (var c in game.DetectedComponents)
                Assert.Null(c.Version);
        }
        finally { Directory.Delete(dir, true); }
    }
}
