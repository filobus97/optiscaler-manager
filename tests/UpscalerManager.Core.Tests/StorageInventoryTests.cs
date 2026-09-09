// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.IO;
using System.Linq;
using UpscalerManager.Core.Models;
using UpscalerManager.Core.Services;
using Xunit;

namespace UpscalerManager.Core.Tests;

/// <summary>
/// The tiers decide whether a delete button appears, so getting them wrong either
/// hides reclaimable space or offers to strand a game in its modified state.
/// </summary>
[Collection(AppDataCollection.Name)]
public class StorageInventoryTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(104857600, "100 MB")]
    [InlineData(1610612736, "1.5 GB")]
    public void FormatsSizesForHumans(long bytes, string expected)
        => Assert.Equal(expected, StorageInventoryService.FormatSize(bytes));

    [Fact]
    public void ALiveBackupIsNotDeletable()
    {
        var item = new StorageItem(StorageTier.LiveBackup, "Backups", "game", "/tmp/x", 10);
        Assert.False(item.CanDelete);
    }

    [Theory]
    [InlineData(StorageTier.Downloaded)]
    [InlineData(StorageTier.UserImport)]
    [InlineData(StorageTier.SpentBackup)]
    public void EverythingElseIsDeletable(StorageTier tier)
        => Assert.True(new StorageItem(tier, "g", "l", "/tmp/x", 10).CanDelete);

    [Fact]
    public void DeleteRefusesALiveBackupEvenIfAskedDirectly()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "keep.txt"), "original");
            var item = new StorageItem(StorageTier.LiveBackup, "Backups", "game", dir, 8);

            Assert.False(new StorageInventoryService(new AppConfiguration()).Delete(item));
            Assert.True(Directory.Exists(dir));   // still there
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void BackupOfAStillInstalledGameIsLiveAndOneOfARevertedGameIsSpent()
    {
        using var env = new FakeAppData();

        var installed = env.NewGameDir("StillModded");
        File.WriteAllText(Path.Combine(installed, "OptiScaler.dll"), "x");
        env.AddBackup(installed);

        var reverted = env.NewGameDir("Reverted");   // exists, but no OptiScaler files
        env.AddBackup(reverted);

        var items = new StorageInventoryService(new AppConfiguration()).Scan();

        Assert.Equal(StorageTier.LiveBackup, TierOfBackupFor(items, installed));
        Assert.Equal(StorageTier.SpentBackup, TierOfBackupFor(items, reverted));
    }

    [Fact]
    public void ABackupIsNamedAfterTheGameNotTheBuildFolder()
    {
        using var env = new FakeAppData();

        // OptiScaler often installs into a nested engine folder, so the recorded
        // directory ends in something like bin/x64 — useless as a label on its own.
        var nested = env.NewGameDir(Path.Combine("Cyberpunk 2077", "bin", "x64"));
        env.AddBackup(nested);

        var backup = Assert.Single(new StorageInventoryService(new AppConfiguration()).Scan(),
            i => i.Group == "Backups");
        Assert.Equal("Cyberpunk 2077", backup.Label);
    }

    [Fact]
    public void BackupOfADeletedGameIsSpent()
    {
        using var env = new FakeAppData();

        var gone = env.NewGameDir("Uninstalled");
        env.AddBackup(gone);
        Directory.Delete(gone, true);               // the player uninstalled the game

        var items = new StorageInventoryService(new AppConfiguration()).Scan();
        var backup = Assert.Single(items, i => i.Group == "Backups");
        Assert.Equal(StorageTier.SpentBackup, backup.Tier);
        Assert.Contains("gone", backup.Note);
    }

    [Fact]
    public void DownloadedVersionsAreSeparatedFromImportedOnes()
    {
        using var env = new FakeAppData();
        env.AddCacheVersion("OptiScaler", "0.9.3", 100);
        env.AddCacheVersion("OptiScaler", "my-build", 100);
        env.AddCacheVersion("Fakenvapi", "1.2.0", 50);

        var config = new AppConfiguration();
        config.CustomOptiScalerVersions.Add("my-build");

        var items = new StorageInventoryService(config).Scan();

        Assert.Equal(StorageTier.Downloaded, items.Single(i => i.Label == "0.9.3").Tier);
        Assert.Equal(StorageTier.Downloaded, items.Single(i => i.Label == "1.2.0").Tier);
        // Imported by hand: we hold the only copy, so it is not "downloaded".
        Assert.Equal(StorageTier.UserImport, items.Single(i => i.Label == "my-build").Tier);
    }

    [Fact]
    public void SharedOptiScalerFoldersAreNotListedAsVersions()
    {
        using var env = new FakeAppData();
        env.AddCacheVersion("OptiScaler", "Licenses", 10);
        env.AddCacheVersion("OptiScaler", "DlssOverrides", 10);
        env.AddCacheVersion("OptiScaler", "0.9.3", 10);

        var items = new StorageInventoryService(new AppConfiguration()).Scan();
        Assert.Equal(new[] { "0.9.3" }, items.Where(i => i.Group == "OptiScaler").Select(i => i.Label));
    }

    [Fact]
    public void ImportedDllsAreListedAndSized()
    {
        using var env = new FakeAppData();
        env.AddCustomDll("amdxcffx64.dll", 2048);

        var item = Assert.Single(new StorageInventoryService(new AppConfiguration()).Scan(),
            i => i.Group == "Custom DLLs");
        Assert.Equal(StorageTier.UserImport, item.Tier);
        Assert.Equal(2048, item.Bytes);
    }

    private static StorageTier TierOfBackupFor(System.Collections.Generic.IEnumerable<StorageItem> items, string gameDir)
        => items.Single(i => i.GameDirectory == gameDir).Tier;

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "storage_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Points ApplicationData at a scratch directory so a scan sees only this test's
    /// files, then puts it back.
    /// </summary>
    private sealed class FakeAppData : IDisposable
    {
        private readonly string _root = NewTempDir();
        private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

        public FakeAppData() => Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root);

        public string AppDir => Path.Combine(_root, "UpscalerManager");

        public string NewGameDir(string name)
        {
            var dir = Path.Combine(_root, "games", name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public void AddBackup(string gameDir)
        {
            var dir = Path.Combine(AppDir, "Backups", BackupStoreService.ComputeGameSlug(gameDir));
            Directory.CreateDirectory(Path.Combine(dir, "files"));
            File.WriteAllText(Path.Combine(dir, "files", "original.dll"), "original bytes");
            var manifest = new InstallationManifest
            {
                OperationStatus = "committed",
                InstalledGameDirectory = gameDir,
            };
            File.WriteAllText(Path.Combine(dir, "manifest.json"),
                System.Text.Json.JsonSerializer.Serialize(manifest, OptimizerContext.Default.InstallationManifest));
        }

        public void AddCacheVersion(string component, string version, int bytes)
        {
            var dir = Path.Combine(AppDir, "Cache", component, version);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "payload.bin"), new byte[bytes]);
        }

        public void AddCustomDll(string name, int bytes)
        {
            var dir = Path.Combine(AppDir, "Cache", "CustomDlls");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, name), new byte[bytes]);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previous);
            try { Directory.Delete(_root, true); } catch { }
        }
    }
}
