// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.

using System.Security.Cryptography;
using System.Text.Json;
using SharpCompress.Archives;

namespace UpscalerManager.Core.Services;

/// <summary>One file attached to a GitHub release, with the SHA-256 GitHub publishes for it.</summary>
public sealed record ReleaseAsset(string Name, string Url, string? Sha256);

/// <summary>One GitHub release: its tag and its files.</summary>
public sealed record GitHubRelease(string Tag, IReadOnlyList<ReleaseAsset> Assets);

public partial class ComponentManagementService
{
    /// <summary>
    /// The FSR 4 shader overrides' DLL releases, newest first, or empty when GitHub cannot
    /// be reached. Its other releases (the timing kits) are skipped.
    /// </summary>
    public async Task<IReadOnlyList<GitHubRelease>> Fsr4OverridesReleasesAsync()
    {
        var url = $"https://api.github.com/repos/{Fsr4ShaderOverrides.RepoOwner}/{Fsr4ShaderOverrides.RepoName}/releases?per_page=30";
        try
        {
            var response = await GetWithRetryAsync(() => _httpClient, url, maxRetries: 2, timeoutSeconds: 20);
            Log.Write($"[Fsr4Overrides] GET {url} → HTTP {(int)response.StatusCode}");
            if (response.IsSuccessStatusCode)
                return ParseFsr4OverridesReleases(await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex)
        {
            Log.Write($"[Fsr4Overrides] Could not list releases: {ex.Message}");
        }
        return Array.Empty<GitHubRelease>();
    }

    /// <summary>
    /// Every DLL release in GitHub's release list, newest first, with each asset's SHA-256
    /// taken from the "sha256:" digest GitHub publishes.
    /// </summary>
    internal static IReadOnlyList<GitHubRelease> ParseFsr4OverridesReleases(string json)
    {
        var releases = new List<GitHubRelease>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return releases;

        // GitHub lists releases newest first; each DLL release has its own tag.
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (tag is null || !tag.StartsWith("dll-", StringComparison.OrdinalIgnoreCase)) continue;
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;

            var assets = new List<ReleaseAsset>();
            if (release.TryGetProperty("assets", out var list))
                foreach (var asset in list.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var download = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    var digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String
                        ? d.GetString() : null;
                    if (name is null || download is null) continue;
                    assets.Add(new ReleaseAsset(name, download,
                        digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                            ? digest["sha256:".Length..].ToLowerInvariant() : null));
                }

            if (assets.Count > 0) releases.Add(new GitHubRelease(tag, assets));
        }
        return releases;
    }

    /// <summary>
    /// The newest release carrying <paramref name="assetName"/>, with that asset.
    /// </summary>
    /// <remarks>
    /// why not simply the newest release: the project has already renamed its downloads
    /// once — four per-GPU files became two zips overnight, and the app that expected the
    /// old names failed every install. Taking the newest release that still has the file
    /// this build knows means a future rename falls back to the last release it can read
    /// instead of failing.
    /// </remarks>
    public static (GitHubRelease Release, ReleaseAsset Asset)? PickFsr4OverridesRelease(
        IReadOnlyList<GitHubRelease> releases, string assetName)
    {
        foreach (var release in releases)
            if (release.Assets.FirstOrDefault(a => a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)) is { } asset)
                return (release, asset);
        return null;
    }

    /// <summary>
    /// Downloads one build, checks it, and returns the path of the DLL in it. Checked
    /// three ways before it is handed on: against the SHA-256 GitHub publishes for the
    /// asset, as a 64-bit Windows DLL, and as AMD's <see cref="Fsr4ShaderOverrides.RequiredBaseVersion"/>.
    /// </summary>
    public async Task<string> DownloadFsr4OverrideAsync(string tag, ReleaseAsset asset, FasterFsr4Kind kind,
        IProgress<double>? progress = null)
    {
        var dir = Path.Combine(_cacheDir, "Fsr4ShaderOverrides", tag,
            Path.GetFileNameWithoutExtension(asset.Name), kind.ToString().ToLowerInvariant());
        var dll = Path.Combine(dir, Fsr4ShaderOverrides.DllName);
        if (File.Exists(dll) && Fsr4OverrideProblem(dll) is null)
        {
            Log.Write($"[Fsr4Overrides] {asset.Name} ({kind}) from {tag} already cached");
            return dll;
        }

        Directory.CreateDirectory(dir);
        var part = Path.Combine(dir, asset.Name + ".part");
        try
        {
            Log.Write($"[Fsr4Overrides] Downloading {asset.Url}");
            await StreamToFileAsync(NetworkService.GetLargeFileClient, asset.Url, part, progress,
                estimatedBytes: 50L * 1024 * 1024, timeoutSeconds: 300);

            if (asset.Sha256 is { } expected)
            {
                var actual = Sha256Of(part);
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"{asset.Name} does not match the checksum GitHub publishes for it, so it was not installed.");
            }
            else
            {
                Log.Write($"[Fsr4Overrides] GitHub publishes no checksum for {asset.Name}; checked as a DLL only");
            }

            ExtractFsr4Override(part, kind, dll);

            if (Fsr4OverrideProblem(dll) is { } problem)
                throw new InvalidOperationException(problem);
            return dll;
        }
        catch
        {
            try { Directory.Delete(dir, true); } catch { }
            throw;
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
        }
    }

    /// <summary>
    /// Writes the exact or lossy build out of a release zip to <paramref name="destination"/>.
    /// Both builds share one file name, so the folder they sit in is what picks.
    /// </summary>
    internal static void ExtractFsr4Override(string zip, FasterFsr4Kind kind, string destination)
    {
        var wanted = Fsr4ShaderOverrides.EntryFor(kind);
        using var archive = ArchiveFactory.Open(zip);
        var entry = archive.Entries.FirstOrDefault(e => !e.IsDirectory
            && (e.Key ?? "").Replace('\\', '/').EndsWith(wanted, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{Path.GetFileName(zip)} holds no {wanted}.");
        using var source = entry.OpenEntryStream();
        using var target = File.Create(destination);
        source.CopyTo(target, 81920);
    }

    /// <summary>What is wrong with a downloaded build, or null when it can be installed.</summary>
    internal static string? Fsr4OverrideProblem(string dll)
    {
        var info = PeFileInspector.Inspect(dll);
        if (!info.IsValidPe || !info.Is64Bit)
            return $"The downloaded {Fsr4ShaderOverrides.DllName} is not a 64-bit Windows DLL.";
        if (info.FileVersion != Fsr4ShaderOverrides.RequiredBaseVersion)
            return $"The downloaded build is version {info.FileVersion ?? "unknown"}, not {Fsr4ShaderOverrides.RequiredBaseVersion}.";
        return null;
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
