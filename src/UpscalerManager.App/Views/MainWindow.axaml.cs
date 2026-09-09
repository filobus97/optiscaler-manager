// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UpscalerManager.App.Services;
using UpscalerManager.App.Views.Pages;
using UpscalerManager.App.ViewModels;
using UpscalerManager.Core.Services;

namespace UpscalerManager.App.Views;

public partial class MainWindow : Window
{
    private readonly ManagerService _manager = null!;
    private readonly MainViewModel _vm = new();
    private bool _initialFocusDone;

    // Parameterless ctor for the XAML previewer/designer only.
    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        var list = this.FindControl<ListBox>("GamesList");
        if (list is null) return;

        // Tunnelling: this has to run *before* the ListBox's own key handling, which
        // would otherwise swallow the arrows we need for moving across a row.
        list.AddHandler(KeyDownEvent, OnGamesListKeyDown, RoutingStrategies.Tunnel);
        list.GotFocus += OnGamesListGotFocus;

        // Bubbling, so inner controls get first refusal on Esc.
        AddHandler(KeyDownEvent, OnWindowKeyDown);
    }

    public MainWindow(ManagerService manager) : this()
    {
        _manager = manager;
        Opened += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        DetectGpu();
        RefreshImportSummary();
        if (!Program.RelaunchedAfterUpdate)
            _ = CheckForAppUpdateAsync(); // fire-and-forget; silent unless a newer release exists
        await RescanAsync();
        // Set last so it wins over the scan's "Found N game(s)" status.
        if (Program.RelaunchedAfterUpdate)
            _vm.StatusText = $"Updated to v{_manager.AppVersion} ✓  •  Found {_vm.Games.Count} game(s).";
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
                text.Text = $"Upscaler Manager v{check.LatestVersion} is available (you have v{check.CurrentVersion})." +
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

    private async void OnUpdateNowClick(object? sender, RoutedEventArgs e)
        => await SelfUpdateLauncher.StartAsync(_manager, msg => _vm.StatusText = msg);

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
                var row = new GameRowViewModel(g) { GlobalBusy = _vm.IsBusy };
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
                    // The row container, not the ListBox: focusing the ListBox itself
                    // leaves focus nowhere, so the arrows/D-pad would only scroll.
                    FocusRowCell(list, 0, 0);
                }
                else
                {
                    this.FindControl<Button>("SettingsButton")?.Focus(NavigationMethod.Directional);
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
        await ShowPageAsync(new SettingsPage(_manager)
        {
            ShowPage = ShowPageAsync,
            RevertGame = RevertGameAtAsync,
        });
        RefreshImportSummary();
    }

    private async void OnDetailsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: GameRowViewModel row }) return;
        await ShowGameDetailsAsync(row);
    }

    /// <summary>
    /// Opens a game's details, then acts on whatever the user chose there. Reading the
    /// outcome after the page closes keeps the install and revert flows exactly as they
    /// were, instead of re-entering them from inside the page.
    /// </summary>
    private async Task ShowGameDetailsAsync(GameRowViewModel row)
    {
        var page = new GameDetailsPage(_manager, row);
        await ShowPageAsync(page);

        switch (page.Outcome)
        {
            case GameDetailsPage.DetailsOutcome.Install:
                await InstallForRowAsync(row);
                break;
            case GameDetailsPage.DetailsOutcome.Revert:
                await RevertRowAsync(row);
                break;
        }
    }

    // ── Page host ───────────────────────────────────────────────────────────────
    // Pages replace the game list inside this window rather than opening windows of
    // their own: gamescope (Steam's Gaming Mode) composites a single application
    // surface, and one window also means the controller never has to guess where
    // input should go.

    /// <summary>
    /// The pages currently stacked over the game list, innermost last. A stack rather
    /// than a single page because pages open pages — Settings opens Storage — and Back
    /// has to return to the one underneath, not all the way home.
    /// </summary>
    private readonly List<(IHostedPage Page, TaskCompletionSource<bool> Result)> _pages = new();

    /// <summary>True while a page is covering the game list.</summary>
    private bool PageIsOpen => _pages.Count > 0;

    private Task<bool> ShowPageAsync(IHostedPage page)
    {
        var host = this.FindControl<ContentControl>("PageContent");
        var pageView = this.FindControl<Grid>("PageView");
        var homeView = this.FindControl<Grid>("HomeView");
        if (host is null || pageView is null || homeView is null) return Task.FromResult(false);

        var result = new TaskCompletionSource<bool>();
        _pages.Add((page, result));
        page.RequestClose = ClosePage;

        ShowTopPage();
        homeView.IsVisible = false;
        pageView.IsVisible = true;
        return result.Task;
    }

    /// <summary>Puts the innermost page on screen and gives it focus.</summary>
    private void ShowTopPage()
    {
        var page = _pages[^1].Page;
        var host = this.FindControl<ContentControl>("PageContent");
        var title = this.FindControl<TextBlock>("PageTitleText");

        if (title is not null) title.Text = page.Title;
        if (host is not null) host.Content = page;

        // After layout, so the page's controls exist to receive focus.
        Dispatcher.UIThread.Post(page.FocusFirst, DispatcherPriority.Loaded);
    }

    private void ClosePage(bool result)
    {
        if (_pages.Count == 0) return;

        var (_, pending) = _pages[^1];
        _pages.RemoveAt(_pages.Count - 1);

        if (_pages.Count > 0)
        {
            // Back into the page that opened this one.
            ShowTopPage();
        }
        else
        {
            var host = this.FindControl<ContentControl>("PageContent");
            if (host is not null) host.Content = null;
            this.FindControl<Grid>("PageView")!.IsVisible = false;
            this.FindControl<Grid>("HomeView")!.IsVisible = true;
            RestoreHomeFocus();
        }

        pending.TrySetResult(result);
    }

    private void OnBackClick(object? sender, RoutedEventArgs e) => ClosePage(false);

    /// <summary>
    /// Esc (and B on a controller) leaves the page. Handled on the way *up* so a
    /// control that wants Esc for itself — an open dropdown, say — still gets it first.
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Escape || !PageIsOpen) return;
        e.Handled = true;
        ClosePage(false);
    }

    /// <summary>Puts focus back on the game the user came from.</summary>
    private void RestoreHomeFocus()
    {
        var list = this.FindControl<ListBox>("GamesList");
        if (list is null || list.ItemCount == 0)
        {
            this.FindControl<Button>("SettingsButton")?.Focus(NavigationMethod.Directional);
            return;
        }
        if (list.SelectedIndex < 0) list.SelectedIndex = 0;
        Dispatcher.UIThread.Post(() => FocusRowCell(list, list.SelectedIndex, 0), DispatcherPriority.Loaded);
    }

    // Each game row is a three-cell grid: the game card (0), Install (1), Revert (2).
    private const int RevertColumn = 2;

    /// <summary>
    /// Keyboard/controller navigation of the game list, as a 2D grid: Up/Down moves
    /// between games, Left/Right between the row's three cells (game, Install, Revert).
    ///
    /// Up/Down on the game card is left to the ListBox itself — it already moves the
    /// selection, scrolls the new row into view, and lets focus escape to the header at
    /// the top edge. We only take over when focus sits on one of the row's buttons, to
    /// stay in the same column while moving between games.
    /// </summary>
    private void OnGamesListKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not ListBox list || list.ItemCount == 0 || list.SelectedIndex < 0) return;

        var column = FocusedRowColumn();
        switch (e.Key)
        {
            case Key.Left:
            case Key.Right:
                // Always handled: nothing sits to the left or right of the list, and
                // leaving it unhandled lets the ListBox pull focus back to the card.
                e.Handled = true;
                var cell = Math.Clamp(column + (e.Key == Key.Right ? 1 : -1), 0, RevertColumn);
                if (cell != column) FocusRowCell(list, list.SelectedIndex, cell);
                break;

            case Key.Up:
            case Key.Down:
                if (column == 0) return;                       // the ListBox does this better
                var row = list.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
                if (row < 0 || row >= list.ItemCount) return;  // edge: let focus escape
                list.SelectedIndex = row;
                e.Handled = FocusRowCell(list, row, column);
                break;

            case Key.Enter:
                // Only from the card — on a button, the button's own Enter handling wins.
                // Safe either way: this opens the preview dialog, which still requires
                // an explicit confirm.
                if (column != 0) return;
                if (list.SelectedItem is not GameRowViewModel selected || !selected.IsIdle) return;
                e.Handled = true;
                _ = InstallForRowAsync(selected);
                break;
        }
    }

    /// <summary>
    /// Which cell of a game row currently has focus: 0 for the card itself,
    /// otherwise the grid column of the focused button.
    /// </summary>
    private int FocusedRowColumn()
    {
        for (var v = FocusManager?.GetFocusedElement() as Visual; v is not null; v = v.GetVisualParent())
        {
            if (v is Button button) return Grid.GetColumn(button);
            if (v is ListBoxItem) return 0;
        }
        return 0;
    }

    /// <summary>
    /// Focuses one cell of a game row, scrolling the row into view first so the
    /// container exists even when it was scrolled off-screen.
    /// </summary>
    private static bool FocusRowCell(ListBox list, int row, int column)
    {
        list.ScrollIntoView(row);
        list.UpdateLayout();
        if (list.ContainerFromIndex(row) is not ListBoxItem container) return false;

        if (column != 0)
        {
            var button = container.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => Grid.GetColumn(b) == column && b.IsEffectivelyEnabled);
            // While an install or revert runs the row's buttons are disabled; staying on
            // the card beats dropping focus somewhere unpredictable.
            if (button is not null) return button.Focus(NavigationMethod.Directional);
        }

        return container.Focus(NavigationMethod.Directional);
    }

    /// <summary>
    /// Focusing the ListBox itself leaves focus nowhere, so the arrows/D-pad would only
    /// scroll. Hand focus to the selected row instead.
    /// </summary>
    private void OnGamesListGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (sender is not ListBox list || !ReferenceEquals(e.Source, list) || list.ItemCount == 0) return;
        if (list.SelectedIndex < 0) list.SelectedIndex = 0;
        FocusRowCell(list, list.SelectedIndex, 0);
    }

    private async void OnInstallClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: GameRowViewModel row }) return;
        await InstallForRowAsync(row);
    }

    private async Task InstallForRowAsync(GameRowViewModel row)
    {
        if (!_vm.IsIdle || !row.IsIdle) return;   // a scan or another install is running

        // Configuration + transparent preview first — nothing is written until confirm.
        var dialog = new InstallPage(_manager, row.Game) { TitleFor = row.Game.Name };
        var confirmed = await ShowPageAsync(dialog);
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

    /// <summary>Removes OptiScaler from a game. Reached from the game's details page.</summary>
    /// <summary>
    /// Reverts whichever game is installed in this directory. The Storage screen knows a
    /// backup's game only by its path — the directory recorded at install time, which for
    /// some games is a nested Binaries folder rather than the game's root — so match on
    /// the recorded directory first and fall back to the game containing it.
    /// </summary>
    private async Task<bool> RevertGameAtAsync(string gameDirectory)
    {
        var row = _vm.Games.FirstOrDefault(g =>
                      PathsMatch(_manager.GetInstalledDirectory(g.Game), gameDirectory))
                  ?? _vm.Games.FirstOrDefault(g => IsInside(gameDirectory, g.Game.InstallPath));

        if (row is null) return false;

        await RevertRowAsync(row);
        return !row.Game.IsOptiscalerInstalled;
    }

    private static bool PathsMatch(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string? child, string? parent) =>
        child is not null && parent is { Length: > 0 } &&
        child.StartsWith(parent.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);

    private async Task RevertRowAsync(GameRowViewModel row)
    {
        if (!_vm.IsIdle || !row.IsIdle) return;

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
