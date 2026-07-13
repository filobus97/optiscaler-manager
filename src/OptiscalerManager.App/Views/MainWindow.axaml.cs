// OptiScaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using OptiscalerManager.App.Services;
using OptiscalerManager.App.ViewModels;
using OptiscalerManager.Core.Services;

namespace OptiscalerManager.App.Views;

public partial class MainWindow : Window
{
    private readonly ManagerService _manager = null!;
    private readonly MainViewModel _vm = new();

    // Parameterless ctor for the XAML previewer/designer only.
    public MainWindow() { InitializeComponent(); DataContext = _vm; }

    public MainWindow(ManagerService manager) : this()
    {
        _manager = manager;
        Opened += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        DetectGpu();
        RefreshImportSummary();
        await RescanAsync();
    }

    private void DetectGpu()
    {
        var gpu = _manager.DetectPrimaryGpu();
        if (gpu is null)
        {
            _vm.GpuText = "GPU: not detected (install path guards still apply).";
            _vm.GpuBrush = Brushes.Gray;
            return;
        }

        _vm.GpuText = $"GPU: {gpu.Name}  •  {gpu.Vendor}  •  {gpu.VideoMemoryGB}";
        _vm.GpuBrush = gpu.Vendor switch
        {
            GpuVendor.AMD => new SolidColorBrush(Color.Parse("#E0402A")),
            GpuVendor.NVIDIA => new SolidColorBrush(Color.Parse("#5CB87E")),
            GpuVendor.Intel => new SolidColorBrush(Color.Parse("#4A90D4")),
            _ => Brushes.Gray,
        };
    }

    private void RefreshImportSummary()
    {
        if (_manager.HasCustomFsrSdk)
            _vm.ImportSummary = $"Custom FSR SDK imported ({_manager.LatestCustomFsrSdk}). 'Enable FSR 4' will install it.";
        else if (_manager.HasCustomFsr4Dll)
            _vm.ImportSummary = $"Custom amdxcffx64.dll imported ({_manager.LatestCustomFsr4Dll}). 'Enable FSR 4' will install it.";
        else
            _vm.ImportSummary = "No custom FSR components imported. 'Enable FSR 4' will use OptiScaler's built-in FSR path. Import your own DLL/SDK from Settings.";
    }

    private async Task RescanAsync()
    {
        _vm.IsBusy = true;
        _vm.StatusText = "Scanning game libraries…";
        try
        {
            var games = await _manager.ScanGamesAsync();
            _vm.Games.Clear();
            foreach (var g in games)
            {
                var row = new GameRowViewModel(g);
                row.RefreshFromGame();
                _vm.Games.Add(row);
            }
            _vm.HasNoGames = _vm.Games.Count == 0;
            _vm.StatusText = $"Found {_vm.Games.Count} game(s).";
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            _vm.IsBusy = false;
        }
    }

    private async void OnRescanClick(object? sender, RoutedEventArgs e) => await RescanAsync();

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_manager);
        await win.ShowDialog(this);
        RefreshImportSummary();
    }

    private async void OnEnableFsr4Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: GameRowViewModel row }) return;

        // Transparent preview first — nothing is written until the user confirms.
        var preview = _manager.BuildEnableFsr4Preview(row.Game);
        var confirmed = await new PreviewDialog(row.Game.Name, preview).ShowDialogFor(this);
        if (!confirmed) return;

        _vm.IsBusy = true;
        row.IsBusy = true;
        var progress = new Progress<string>(msg => Dispatcher.UIThread.Post(() =>
        {
            _vm.StatusText = msg;
            row.StatusText = msg;
        }));

        try
        {
            await _manager.EnableFsr4Async(row.Game, progress);
            row.Game.IsOptiscalerInstalled = true;
            row.RefreshFromGame();
            _vm.StatusText = $"FSR 4 enabled for {row.Game.Name}.";
        }
        catch (Exception ex)
        {
            row.StatusText = $"Failed: {ex.Message}";
            _vm.StatusText = $"Enable FSR 4 failed: {ex.Message}";
        }
        finally
        {
            row.IsBusy = false;
            _vm.IsBusy = false;
        }
    }

    private async void OnRevertClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: GameRowViewModel row }) return;

        _vm.IsBusy = true;
        row.IsBusy = true;
        try
        {
            _vm.StatusText = $"Reverting {row.Game.Name}…";
            await _manager.UninstallAsync(row.Game);
            row.Game.IsOptiscalerInstalled = false;
            row.RefreshFromGame();
            _vm.StatusText = $"Reverted {row.Game.Name}.";
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"Revert failed: {ex.Message}";
        }
        finally
        {
            row.IsBusy = false;
            _vm.IsBusy = false;
        }
    }
}
