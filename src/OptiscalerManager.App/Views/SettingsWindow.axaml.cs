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

    // Common overlay keys → OptiScaler [Menu] ShortcutKey virtual-key hex.
    private static readonly (string Label, string? Vk)[] MenuKeys =
    {
        ("OptiScaler default (Insert)", null),
        ("Insert (0x2D)", "0x2D"),
        ("Home (0x24)", "0x24"),
        ("End (0x23)", "0x23"),
        ("Delete (0x2E)", "0x2E"),
        ("Page Up (0x21)", "0x21"),
        ("Page Down (0x22)", "0x22"),
        ("F1 (0x70)", "0x70"), ("F2 (0x71)", "0x71"), ("F3 (0x72)", "0x72"),
        ("F4 (0x73)", "0x73"), ("F5 (0x74)", "0x74"), ("F6 (0x75)", "0x75"),
        ("F7 (0x76)", "0x76"), ("F8 (0x77)", "0x77"), ("F9 (0x78)", "0x78"),
        ("F10 (0x79)", "0x79"), ("F11 (0x7A)", "0x7A"), ("F12 (0x7B)", "0x7B"),
    };

    public SettingsWindow(ManagerService manager) : this()
    {
        _manager = manager;
        SetupMenuKey();
        SetupAboutCard();
        RefreshNukemStatus();
        RefreshInventory();
        RefreshIniProfiles();
    }

    private void SetupAboutCard()
    {
        var text = this.FindControl<TextBlock>("AppVersionText");
        if (text is not null)
            text.Text = $"OptiScaler Manager v{_manager.AppVersion}";
    }

    private void RefreshNukemStatus()
    {
        var text = this.FindControl<TextBlock>("NukemStatusText");
        if (text is null) return;
        text.Text = _manager.IsNukemFgCached
            ? $"Imported ✓ (version tag: {_manager.NukemFgVersion ?? "manual"})"
            : "Not imported yet";
    }

    private async void OnImportNukemFg(object? sender, RoutedEventArgs e)
    {
        try
        {
            var ok = await _manager.ImportNukemFgAsync();
            SetResult(ok
                ? "Nukem's DLSSG-to-FSR3 DLL imported — select the add-on when installing OptiScaler."
                : "Import cancelled or the file did not contain dlssg_to_fsr3_amd_is_better.dll.", error: !ok);
        }
        catch (Exception ex)
        {
            SetResult($"Import failed: {ex.Message}", error: true);
        }
        RefreshNukemStatus();
        RefreshInventory();
    }

    private async void OnCheckAppUpdate(object? sender, RoutedEventArgs e)
    {
        var result = this.FindControl<TextBlock>("AppUpdateResultText");
        if (result is null) return;
        result.Text = "Checking…";
        var check = await _manager.CheckForAppUpdateAsync();
        result.Text = check.LatestVersion is null
            ? "Could not reach GitHub (offline or rate-limited)."
            : check.UpdateAvailable
                ? $"v{check.LatestVersion} is available — close the app and run update.sh / update.ps1."
                : $"Up to date (latest is v{check.LatestVersion}).";
    }

    private void SetupMenuKey()
    {
        var combo = this.FindControl<ComboBox>("MenuKeyCombo");
        if (combo is null) return;

        combo.ItemsSource = MenuKeys.Select(k => k.Label).ToList();
        var current = _manager.MenuShortcutKey;
        var idx = Array.FindIndex(MenuKeys, k => string.Equals(k.Vk, current, StringComparison.OrdinalIgnoreCase));
        combo.SelectedIndex = idx >= 0 ? idx : 0;

        combo.SelectionChanged += (_, _) =>
        {
            var i = combo.SelectedIndex;
            if (i < 0 || i >= MenuKeys.Length) return;
            _manager.MenuShortcutKey = MenuKeys[i].Vk;
            SetResult(MenuKeys[i].Vk is null
                ? "Menu key reset to OptiScaler's default (Insert)."
                : $"Menu key set to {MenuKeys[i].Label}. It will be applied on the next install.");
        };
    }

    private void RefreshInventory()
    {
        var inv = this.FindControl<TextBlock>("InventoryText");
        if (inv is null) return;

        var customs = _manager.GetCustomDlls();
        inv.Text = customs.Count > 0
            ? $"Custom DLLs: {customs.Count} ({string.Join(", ", customs.Select(c => c.Name))})"
            : "Custom DLLs: none imported";

        RefreshCustomDlls();
    }

    private void RefreshCustomDlls()
    {
        var list = this.FindControl<StackPanel>("CustomDllsList");
        if (list is null) return;
        list.Children.Clear();

        var customs = _manager.GetCustomDlls();
        if (customs.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = "No custom DLLs yet.",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#8A8AAA")),
            });
            return;
        }

        foreach (var entry in customs)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var signed = entry.HasAuthenticodeSignature ? "signed" : "unsigned";
            row.Children.Add(new TextBlock
            {
                Text = $"{entry.Name}   v{entry.FileVersion ?? "?"}   ({signed})",
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse("#E4E4EF")),
            });
            var del = new Button { Content = "Delete", Classes = { "BtnDanger" }, FontSize = 11, Padding = new Avalonia.Thickness(10, 4) };
            Grid.SetColumn(del, 1);
            var captured = entry.Name;
            del.Click += (_, _) =>
            {
                _manager.DeleteCustomDll(captured);
                SetResult($"Removed {captured} from the custom-DLL library.");
                RefreshInventory();
            };
            row.Children.Add(del);
            list.Children.Add(row);
        }
    }

    private void SetResult(string message, bool error = false)
    {
        var result = this.FindControl<TextBlock>("ResultText");
        if (result is null) return;
        result.Text = message;
        result.Foreground = new SolidColorBrush(Color.Parse(error ? "#E06060" : "#8B73F8"));
    }

    private async void OnImportDlls(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select custom DLL(s)",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("DLL files") { Patterns = new[] { "*.dll" } },
            }
        });
        if (files is null || files.Count == 0) return;

        await ImportWrap(async () =>
        {
            var names = await _manager.ImportCustomDllsAsync(files.Select(f => f.Path.LocalPath));
            return $"Imported {names.Count} DLL(s): {string.Join(", ", names)}.";
        });
    }

    private async void OnImportDllArchive(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select archive with DLLs",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Archive (.zip/.7z/.rar)") { Patterns = new[] { "*.zip", "*.7z", "*.rar" } },
            }
        });
        if (files is null || files.Count == 0) return;

        await ImportWrap(async () =>
        {
            var names = await _manager.ImportCustomDllsAsync(new[] { files[0].Path.LocalPath });
            return $"Imported {names.Count} DLL(s): {string.Join(", ", names)}.";
        });
    }

    private async void OnImportDllFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select folder with DLLs",
            AllowMultiple = false,
        });
        if (folders is null || folders.Count == 0) return;

        await ImportWrap(async () =>
        {
            var names = await _manager.ImportCustomDllsAsync(new[] { folders[0].Path.LocalPath });
            return $"Imported {names.Count} DLL(s): {string.Join(", ", names)}.";
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
