// OptiScaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System.Collections.ObjectModel;
using Avalonia.Media;

namespace OptiscalerManager.App.ViewModels;

/// <summary>Backing state for the single main screen.</summary>
public sealed class MainViewModel : ViewModelBase
{
    public ObservableCollection<GameRowViewModel> Games { get; } = new();

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
        set { if (SetField(ref _isBusy, value)) OnPropertyChanged(nameof(IsIdle)); }
    }
    public bool IsIdle => !_isBusy;

    private string _importSummary = "No custom FSR components imported yet.";
    public string ImportSummary { get => _importSummary; set => SetField(ref _importSummary, value); }

    private bool _hasNoGames;
    public bool HasNoGames { get => _hasNoGames; set => SetField(ref _hasNoGames, value); }
}
