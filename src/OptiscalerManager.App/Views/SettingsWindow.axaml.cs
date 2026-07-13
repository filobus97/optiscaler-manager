// OptiScaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using OptiscalerManager.App.Services;

namespace OptiscalerManager.App.Views;

public partial class SettingsWindow : Window
{
    private readonly ManagerService _manager = null!;

    public SettingsWindow() { InitializeComponent(); }

    public SettingsWindow(ManagerService manager) : this()
    {
        _manager = manager;
        RefreshInventory();
    }

    private void RefreshInventory()
    {
        var inv = this.FindControl<TextBlock>("InventoryText");
        if (inv is null) return;

        var dlls = _manager.CustomFsr4DllVersions;
        var sdks = _manager.CustomFsrSdkVersions;
        var lines = new System.Collections.Generic.List<string>();
        lines.Add(dlls.Count > 0 ? $"amdxcffx64.dll: {string.Join(", ", dlls)}" : "amdxcffx64.dll: none imported");
        lines.Add(sdks.Count > 0 ? $"FSR SDK: {string.Join(", ", sdks)}" : "FSR SDK: none imported");
        inv.Text = string.Join("\n", lines);
    }

    private void SetResult(string message, bool error = false)
    {
        var result = this.FindControl<TextBlock>("ResultText");
        if (result is null) return;
        result.Text = message;
        result.Foreground = new SolidColorBrush(Color.Parse(error ? "#E06060" : "#8B73F8"));
    }

    private async void OnImportDll(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select amdxcffx64.dll",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("amdxcffx64.dll") { Patterns = new[] { "amdxcffx64.dll", "*.dll" } },
            }
        });
        if (files is null || files.Count == 0) return;

        await ImportWrap(async () =>
        {
            var version = await _manager.ImportCustomFsr4DllAsync(files[0].Path.LocalPath);
            return $"Imported amdxcffx64.dll (version {version}).";
        });
    }

    private async void OnImportSdkArchive(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select FSR SDK archive",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Archive (.zip/.7z)") { Patterns = new[] { "*.zip", "*.7z" } },
            }
        });
        if (files is null || files.Count == 0) return;

        await ImportWrap(async () =>
        {
            var summary = await _manager.ImportCustomFsrSdkAsync(files[0].Path.LocalPath);
            return $"Imported FSR SDK: {summary}.";
        });
    }

    private async void OnImportSdkFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select extracted FSR SDK folder",
            AllowMultiple = false,
        });
        if (folders is null || folders.Count == 0) return;

        await ImportWrap(async () =>
        {
            var summary = await _manager.ImportCustomFsrSdkAsync(folders[0].Path.LocalPath);
            return $"Imported FSR SDK: {summary}.";
        });
    }

    private async Task ImportWrap(Func<Task<string>> import)
    {
        try
        {
            SetResult("Importing…");
            var message = await import();
            SetResult(message);
            RefreshInventory();
        }
        catch (Exception ex)
        {
            SetResult($"Import failed: {ex.Message}", error: true);
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
