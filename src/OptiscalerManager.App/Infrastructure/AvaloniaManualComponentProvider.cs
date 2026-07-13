// OptiScaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SharpCompress.Archives;
using SharpCompress.Common;
using OptiscalerManager.Core.Prompts;

namespace OptiscalerManager.App.Infrastructure;

/// <summary>
/// Avalonia implementation of the Core <see cref="IManualComponentProvider"/>
/// seam. Handles the components that cannot be downloaded automatically
/// (currently Nukem's DLSSG-to-FSR3 mod): prompts the user for a local DLL or
/// archive they already possess and drops it into the component cache. This
/// keeps with the bring-your-own philosophy — nothing is fetched from the net.
/// </summary>
public sealed class AvaloniaManualComponentProvider : IManualComponentProvider
{
    private readonly Func<Window?> _ownerAccessor;

    public AvaloniaManualComponentProvider(Func<Window?> ownerAccessor) => _ownerAccessor = ownerAccessor;

    public async Task<bool> ProvideAsync(ManualComponentRequest request)
    {
        var owner = _ownerAccessor();
        if (owner is null) return false;

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Select {request.RequiredFileName} for {request.ComponentName}",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType($"{request.RequiredFileName} or archive")
                {
                    Patterns = new[] { request.RequiredFileName, "*.zip", "*.7z" }
                },
                new FilePickerFileType("All files") { Patterns = new[] { "*.*" } }
            }
        });

        if (files is null || files.Count == 0) return false;
        var selected = files[0].Path.LocalPath;

        try
        {
            Directory.CreateDirectory(request.TargetCachePath);
            var dest = Path.Combine(request.TargetCachePath, request.RequiredFileName);

            if (selected.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                if (!Path.GetFileName(selected).Equals(request.RequiredFileName, StringComparison.OrdinalIgnoreCase))
                    return false;
                File.Copy(selected, dest, overwrite: true);
                return true;
            }

            // Archive (.zip/.7z): pull the required file out of it.
            using var archive = ArchiveFactory.Open(selected);
            var entry = archive.Entries.FirstOrDefault(e =>
                !e.IsDirectory &&
                Path.GetFileName(e.Key ?? "").Equals(request.RequiredFileName, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return false;
            entry.WriteToFile(dest, new ExtractionOptions { Overwrite = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
