// OptiScaler Manager - GPL-3.0-or-later. See repository LICENSE.
using OptiscalerManager.Core.Models;

namespace OptiscalerManager.App.ViewModels;

/// <summary>One row in the game list. Wraps a scanned <see cref="Game"/> and the
/// bits of display state the single screen needs.</summary>
public sealed class GameRowViewModel : ViewModelBase
{
    public Game Game { get; }

    public GameRowViewModel(Game game)
    {
        Game = game;
        _statusText = game.IsOptiscalerInstalled ? "OptiScaler installed" : "Not installed";
    }

    public string Name => Game.Name;

    public string SubTitle => $"{Game.Platform}  •  {Game.InstallPath}";

    private string _statusText;
    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { if (SetField(ref _isBusy, value)) OnPropertyChanged(nameof(IsIdle)); }
    }

    private bool _globalBusy;
    /// <summary>
    /// True while the whole screen is busy — a scan, or another game's install. A row
    /// has to follow that too, or a press could start an install mid-scan.
    /// </summary>
    public bool GlobalBusy
    {
        get => _globalBusy;
        set { if (SetField(ref _globalBusy, value)) OnPropertyChanged(nameof(IsIdle)); }
    }

    public bool IsIdle => !_isBusy && !_globalBusy;

    private bool _isInstalled;
    public bool IsInstalled
    {
        get => _isInstalled;
        set => SetField(ref _isInstalled, value);
    }

    public void RefreshFromGame()
    {
        IsInstalled = Game.IsOptiscalerInstalled;
        StatusText = Game.IsOptiscalerInstalled
            ? $"FSR 4 enabled (OptiScaler {Game.OptiscalerVersion})"
            : "Not installed";
    }
}
