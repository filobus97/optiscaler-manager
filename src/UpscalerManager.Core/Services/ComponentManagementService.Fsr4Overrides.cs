// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.

using System.Security.Cryptography;
using System.Text.Json;
using SharpCompress.Archives;

namespace UpscalerManager.Core.Services;

/// <summary>One file attached to a GitHub release, with the SHA-256 GitHub publishes for it.</summary>
public sealed record ReleaseAsset(string Name, string Url, string? Sha256);

public partial class ComponentManagementService
{
    /// <summary>
    /// The newest release of the FSR 4 shader overrides that carries DLLs, or null when
    /// GitHub cannot be reached. Its other releases (the timing kit) are skipped.
    /// </summary>
    public async Task<(string Tag, IReadOnlyList<ReleaseAsset> Assets)?> LatestFsr4OverridesReleaseAsync()
    {
        var url = $"https://api.github.com/repos/{Fsr4ShaderOverrides.RepoOwner}/{Fsr4ShaderOverrides.RepoName}/releases?per_page=20";
        try
        {
            var response = await GetWithRetryAsync(() => _httpClient, url, maxRetries: 2, timeoutSeconds: 20);
            Log.Write($"[Fsr4Overrides] GET {url} → HTTP {(int)response.StatusCode}");
            if (!response.IsSuccessStatusCode) return null;

            return ParseFsr4OverridesReleases(await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex)
        {
            Log.Write($"[Fsr4Overrides] Could not list releases: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// The newest DLL release in GitHub's release list, with each asset's SHA-256 taken
    /// from the "sha256:" digest GitHub publishes. Null when there is none.
    /// </summary>
    internal static (string Tag, IReadOnlyList<ReleaseAsset> Assets)? ParseFsr4OverridesReleases(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

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

            if (assets.Count > 0) return (tag, assets);
        }
        return null;
    }

    /// <summary>
    /// Downloads one build, checks it, and returns the path of the DLL in it. Checked
    /// three ways before it is handed on: against the SHA-256 GitHub publishes for the
    /// asset, as a 64-bit Windows DLL, and as AMD's <see cref="Fsr4ShaderOverrides.RequiredBaseVersion"/>.
    /// </summary>
    public async Task<string> DownloadFsr4OverrideAsync(string tag, ReleaseAsset asset, IProgress<double>? progress = null)
    {
        var dir = Path.Combine(_cacheDir, "Fsr4ShaderOverrides", tag, Path.GetFileNameWithoutExtension(asset.Name));
        var dll = Path.Combine(dir, Fsr4ShaderOverrides.DllName);
        if (File.Exists(dll) && Fsr4OverrideProblem(dll) is null)
        {
            Log.Write($"[Fsr4Overrides] {asset.Name} from {tag} already cached");
            return dll;
        }

        Directory.CreateDirectory(dir);
        var part = Path.Combine(dir, asset.Name + ".part");
        try
        {
            Log.Write($"[Fsr4Overrides] Downloading {asset.Url}");
            await StreamToFileAsync(NetworkService.GetLargeFileClient, asset.Url, part, progress,
                estimatedBytes: 40L * 1024 * 1024, timeoutSeconds: 300);

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

            if (asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var archive = ArchiveFactory.Open(part);
                var entry = archive.Entries.FirstOrDefault(e => !e.IsDirectory
                    && ArchiveEntryName(e.Key).Equals(Fsr4ShaderOverrides.DllName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"{asset.Name} holds no {Fsr4ShaderOverrides.DllName}.");
                using var source = entry.OpenEntryStream();
                using var target = File.Create(dll);
                source.CopyTo(target, 81920);
            }
            else
            {
                File.Move(part, dll, overwrite: true);
            }

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
