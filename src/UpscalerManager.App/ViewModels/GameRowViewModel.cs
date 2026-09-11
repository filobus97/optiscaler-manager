// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using UpscalerManager.Core.Logging;
using UpscalerManager.Core.Models;
using UpscalerManager.Core.Services;

namespace UpscalerManager.App.ViewModels;

/// <summary>One card in the game list. Wraps a scanned <see cref="Game"/> and the
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

    /// <summary>The platform alone, for the card, where there is no room for a path.</summary>
    public string PlatformName => Game.Platform.ToString();

    private Bitmap? _coverImage;

    /// <summary>
    /// Cover art, or null when the game has none — the card shows a placeholder then,
    /// which is most non-Steam games, since only Steam publishes art we can look up
    /// without an API key.
    /// </summary>
    public Bitmap? CoverImage
    {
        get => _coverImage;
        private set
        {
            SetField(ref _coverImage, value);
            OnPropertyChanged(nameof(HasCoverImage));
        }
    }

    public bool HasCoverImage => _coverImage is not null;

    /// <summary>
    /// Fetches and decodes the cover, if there is one. Never throws: a card without art
    /// is a cosmetic loss and must not disturb the scan.
    /// </summary>
    public async Task LoadCoverAsync(CoverArtService covers, CancellationToken cancel = default)
    {
        try
        {
            if (await covers.GetCoverAsync(Game, cancel) is not { } path) return;

            // Decoding is the expensive part, so keep it off the UI thread.
            var bitmap = await Task.Run(() => new Bitmap(path), cancel);
            CoverImage = bitmap;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write($"[Covers] Could not show art for {Game.Name}: {ex.Message}");
        }
    }

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

    private IReadOnlyList<string> _techBadges = Array.Empty<string>();
    /// <summary>
    /// Short "what this game has" labels with versions, e.g. "FSR 4.1.1". The point of
    /// showing the version here is that presence alone answers nothing — FSR 3.1 and
    /// FSR 4.1 are the same badge but a completely different result.
    /// </summary>
    public IReadOnlyList<string> TechBadges
    {
        get => _techBadges;
        private set => SetField(ref _techBadges, value);
    }

    public void RefreshFromGame()
    {
        IsInstalled = Game.IsOptiscalerInstalled;
        StatusText = Game.IsOptiscalerInstalled
            ? $"FSR 4 enabled (OptiScaler {Game.OptiscalerVersion})"
            : "Not installed";
        TechBadges = BuildBadges();
    }

    private IReadOnlyList<string> BuildBadges()
    {
        var badges = new List<string>();

        // Presence comes from having found the file; the version is extra. Some DLLs
        // carry no version resource, and "DLSS" alone beats claiming "DLSS 0.0".
        void Add(string label, string? path, string? version)
        {
            if (path is null) return;
            badges.Add(version is null ? label : $"{label} {VersionLabel.Short(version)}");
        }

        Add("DLSS", Game.DlssPath, Game.DlssVersion);
        Add("DLSS FG", Game.DlssFrameGenPath, Game.DlssFrameGenVersion);
        Add("FSR", Game.FsrPath, Game.FsrVersion);
        Add("XeSS", Game.XessPath, Game.XessVersion);
        return badges;
    }
}
