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
    private bool _initialFocusDone;

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
        _ = CheckForAppUpdateAsync(); // fire-and-forget; silent unless a newer release exists
        await RescanAsync();
    }

    private async Task CheckForAppUpdateAsync()
    {
        try
        {
            var check = await _manager.CheckForAppUpdateAsync();
            if (!check.UpdateAvailable) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var banner = this.FindControl<Border>("UpdateBanner");
                var text = this.FindControl<TextBlock>("UpdateBannerText");
                if (banner is null || text is null) return;
                var canSelf = _manager.CanSelfUpdate;
                text.Text = $"OptiScaler Manager v{check.LatestVersion} is available (you have v{check.CurrentVersion})." +
                            (canSelf ? "" : " Close the app and run update.sh / update.ps1 from the install folder.");
                var updateNow = this.FindControl<Button>("UpdateNowButton");
                if (updateNow is not null) updateNow.IsVisible = canSelf;
                banner.IsVisible = true;
            });
        }
        catch { /* best-effort — never bother the user over a failed check */ }
    }

    private void OnOpenReleasesClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _manager.ReleasesPageUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"Could not open the browser: {ex.Message} — {_manager.ReleasesPageUrl}";
        }
    }

    private void OnDismissUpdateBanner(object? sender, RoutedEventArgs e)
    {
        var banner = this.FindControl<Border>("UpdateBanner");
        if (banner is not null) banner.IsVisible = false;
    }

    private void OnUpdateNowClick(object? sender, RoutedEventArgs e)
        => SelfUpdateLauncher.StartAndShutdown(_manager, msg => _vm.StatusText = msg);

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
        var parts = new System.Collections.Generic.List<string>();
        var customs = _manager.GetCustomDlls();
        parts.Add(customs.Count > 0 ? $"Custom DLLs: {customs.Count}" : "Custom DLLs: none");
        var iniCount = _manager.GetIniProfiles().Count(p => !p.IsBuiltIn);
        parts.Add(iniCount > 0 ? $"OptiScaler.ini profiles: {iniCount}" : "OptiScaler.ini profiles: none");
        parts.Add(_manager.IsNukemFgCached ? "Nukem FG: imported" : "Nukem FG: not imported");
        _vm.ImportSummary = "Imported — " + string.Join("  •  ", parts) + ".  Pick these per install.";
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

            // Land keyboard/controller focus on the game list ONLY on the first scan,
            // so a manual Rescan doesn't yank focus away from whatever the user is on.
            if (!_initialFocusDone)
            {
                _initialFocusDone = true;
                var list = this.FindControl<ListBox>("GamesList");
                if (_vm.Games.Count > 0 && list is not null)
                {
                    list.SelectedIndex = 0;
                    list.Focus();
                }
                else
                {
                    this.FindControl<Button>("SettingsButton")?.Focus();
                }
            }
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

    private async void OnInstallClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: GameRowViewModel row }) return;

        // Configuration + transparent preview first — nothing is written until confirm.
        var dialog = new InstallOptiScalerDialog(_manager, row.Game);
        var confirmed = await dialog.ShowDialogFor(this);
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
            await _manager.InstallAsync(row.Game, dialog.SelectedBackend, dialog.SelectedInt8Version, dialog.SelectFsr4, dialog.SelectedProfile, progress,
                addFakenvapi: dialog.AddFakenvapi, addNukemFg: dialog.AddNukemFg,
                spoofMethod: dialog.SelectedSpoofMethod, forceInt8: dialog.ForceInt8, fsr4Watermark: dialog.Fsr4Watermark,
                optiscalerVersion: dialog.SelectedOptiScalerVersion);
            row.Game.IsOptiscalerInstalled = true;
            row.RefreshFromGame();
            _vm.StatusText = $"OptiScaler installed for {row.Game.Name}.";
        }
        catch (Exception ex)
        {
            row.StatusText = $"Failed: {ex.Message}";
            _vm.StatusText = $"Install failed: {ex.Message}";
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

        // Give clear feedback instead of silently doing nothing when there is no install.
        if (!_manager.HasInstall(row.Game))
        {
            _vm.StatusText = $"Nothing to revert for {row.Game.Name} (OptiScaler is not installed by this app).";
            return;
        }

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
            row.StatusText = $"Revert failed: {ex.Message}";
            _vm.StatusText = $"Revert failed: {ex.Message}";
        }
        finally
        {
            row.IsBusy = false;
            _vm.IsBusy = false;
        }
    }
}
