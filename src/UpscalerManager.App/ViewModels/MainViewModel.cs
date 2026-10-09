// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;

namespace UpscalerManager.App.ViewModels;

/// <summary>Backing state for the single main screen.</summary>
public sealed class MainViewModel : ViewModelBase
{
    /// <summary>Every scanned game.</summary>
    public ObservableCollection<GameRowViewModel> Games { get; } = new();

    /// <summary>
    /// The games actually shown, which is <see cref="Games"/> minus the ones hidden by
    /// the "no upscaler" filter.
    ///
    /// A separate collection rather than a filter applied during the scan: the games
    /// stay scanned either way, so toggling the setting takes effect immediately and a
    /// mis-detected game is never permanently invisible.
    /// </summary>
    public ObservableCollection<GameRowViewModel> VisibleGames { get; } = new();

    private bool _hideGamesWithoutUpscaler;
    public bool HideGamesWithoutUpscaler
    {
        get => _hideGamesWithoutUpscaler;
        set { if (SetField(ref _hideGamesWithoutUpscaler, value)) RefreshVisibleGames(); }
    }

    private bool _hideGamesWithAntiCheat;

    /// <summary>The toolbar's second filter: games that ship a kernel-level anti-cheat.</summary>
    public bool HideGamesWithAntiCheat
    {
        get => _hideGamesWithAntiCheat;
        set { if (SetField(ref _hideGamesWithAntiCheat, value)) RefreshVisibleGames(); }
    }

    /// <summary>Rebuilds the shown list. Call after a scan, or when a filter changes.</summary>
    public void RefreshVisibleGames()
    {
        var shown = Games
            .Where(g => !(_hideGamesWithoutUpscaler && g.HasNoUpscaler))
            .Where(g => !(_hideGamesWithAntiCheat && g.HasAntiCheat))
            .ToList();

        VisibleGames.Clear();
        foreach (var row in shown) VisibleGames.Add(row);

        HasNoGames = VisibleGames.Count == 0;
        HiddenCount = Games.Count - VisibleGames.Count;
    }

    private int _hiddenCount;

    /// <summary>How many games the filter is holding back, for the status line.</summary>
    public int HiddenCount
    {
        get => _hiddenCount;
        private set
        {
            if (!SetField(ref _hiddenCount, value)) return;
            OnPropertyChanged(nameof(HiddenNotice));
            OnPropertyChanged(nameof(HasHiddenGames));
        }
    }

    public bool HasHiddenGames => _hiddenCount > 0;

    public string HiddenNotice => _hiddenCount == 1
        ? "1 game hidden: no upscaler found."
        : $"{_hiddenCount} games hidden: no upscaler found.";

    private string _gpuText = "Detecting GPU…";
    public string GpuText { get => _gpuText; set => SetField(ref _gpuText, value); }

    private IBrush _gpuBrush = Brushes.Gray;
    public IBrush GpuBrush { get => _gpuBrush; set => SetField(ref _gpuBrush, value); }

    private string _statusText = "Ready.";
    public string StatusText { get => _statusText; set => SetField(ref _statusText, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!SetField(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            // The rows' buttons follow the screen's state, so nothing in the list is
            // clickable while a scan or an install is running.
            foreach (var row in Games) row.GlobalBusy = value;
        }
    }
    public bool IsIdle => !_isBusy;

    private bool _hasNoGames;
    public bool HasNoGames { get => _hasNoGames; set => SetField(ref _hasNoGames, value); }
}
