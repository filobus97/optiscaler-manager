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
        RefreshIniProfiles();
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

    private void RefreshIniProfiles()
    {
        var list = this.FindControl<StackPanel>("IniProfilesList");
        if (list is null) return;
        list.Children.Clear();

        var custom = _manager.GetIniProfiles().Where(p => !p.IsBuiltIn).ToList();
        if (custom.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = "No custom .ini profiles yet.",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#8A8AAA")),
            });
            return;
        }

        foreach (var profile in custom)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(new TextBlock
            {
                Text = profile.Name,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse("#E4E4EF")),
            });
            var del = new Button { Content = "Delete", Classes = { "BtnDanger" }, FontSize = 11, Padding = new Avalonia.Thickness(10, 4) };
            Grid.SetColumn(del, 1);
            var captured = profile;
            del.Click += (_, _) =>
            {
                try { _manager.DeleteIniProfile(captured); SetResult($"Deleted profile '{captured.Name}'."); }
                catch (Exception ex) { SetResult($"Delete failed: {ex.Message}", error: true); }
                RefreshIniProfiles();
            };
            row.Children.Add(del);
            list.Children.Add(row);
        }
    }

    private async void OnImportIni(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select an OptiScaler.ini file",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("INI files") { Patterns = new[] { "*.ini" } },
                new FilePickerFileType("All files") { Patterns = new[] { "*.*" } },
            }
        });
        if (files is null || files.Count == 0) return;

        var path = files[0].Path.LocalPath;
        var nameBox = this.FindControl<TextBox>("ProfileNameBox");
        var name = nameBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = System.IO.Path.GetFileNameWithoutExtension(path);

        try
        {
            SetResult("Importing .ini…");
            await _manager.ImportIniProfileAsync(path, name!);
            SetResult($"Imported OptiScaler.ini profile '{name}'.");
            if (nameBox is not null) nameBox.Text = string.Empty;
            RefreshIniProfiles();
        }
        catch (Exception ex)
        {
            SetResult($"Import failed: {ex.Message}", error: true);
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
