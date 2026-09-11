// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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
        _statusText = DescribeStatus(game);
    }

    /// <summary>
    /// Only what can be stated about OptiScaler itself. The old line claimed
    /// "FSR 4 enabled", which the app cannot know — whether FSR 4 actually runs depends
    /// on the ini, the release and the GPU, and the chips below already report what is
    /// installed.
    /// </summary>
    private static string DescribeStatus(Game game) => game.IsOptiscalerInstalled
        ? $"OptiScaler {VersionLabel.Short(game.OptiscalerVersion ?? "installed")}"
        : "OptiScaler not installed";

    public string Name => Game.Name;

    public string SubTitle => $"{Game.Platform}  •  {Game.InstallPath}";

    /// <summary>
    /// The card's tooltip. A card fits two rows of chips and clips the rest, so the
    /// full list goes here — nothing detected is only visible on the details page.
    /// </summary>
    public string CardTip => _allBadges.Count == 0
        ? SubTitle
        : $"{SubTitle}\n\n{string.Join("  •  ", _allBadges.Select(b => b.Label))}";

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
        set { if (SetField(ref _statusText, value)) OnPropertyChanged(nameof(StatusBrush)); }
    }

    /// <summary>
    /// Green when OptiScaler is installed, plain grey when it is not. The line used to
    /// be accent-coloured either way, so "not installed" looked like an achievement.
    /// </summary>
    public IBrush? StatusBrush =>
        Application.Current?.FindResource(Game.IsOptiscalerInstalled ? "BrSuccess" : "BrTextSecondary") as IBrush;

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

    /// <summary>
    /// How many chips a card's reserved three rows hold. A game can carry eight
    /// distinct technologies, which needs four rows — reserving that on every card to
    /// suit a case that essentially never occurs would cost 18px of art on all of
    /// them, so past this the rest collapse into a "+N" chip.
    /// </summary>
    private const int MaxCardChips = 6;

    /// <summary>Every chip, for the tooltip, even when the card only shows some.</summary>
    private IReadOnlyList<TechBadge> _allBadges = Array.Empty<TechBadge>();

    private IReadOnlyList<TechBadge> _techBadges = Array.Empty<TechBadge>();
    /// <summary>
    /// What this game has, as presence-only tags: "DLSS", "FSR", "XeSS". Capped at what
    /// the card's chip rows hold; <see cref="CardTip"/> always carries the full list.
    /// </summary>
    public IReadOnlyList<TechBadge> TechBadges
    {
        get => _techBadges;
        private set { if (SetField(ref _techBadges, value)) OnPropertyChanged(nameof(HasTechBadges)); }
    }

    /// <summary>
    /// The chip row keeps a fixed two-row height so a card cannot be pushed out of its
    /// own bounds — which means it has to disappear entirely when there are no chips,
    /// or it reserves that space from the "no upscaler found" tag instead.
    /// </summary>
    public bool HasTechBadges => _techBadges.Count > 0;

    public void RefreshFromGame()
    {
        IsInstalled = Game.IsOptiscalerInstalled;

        StatusText = DescribeStatus(Game);

        _allBadges = TechBadge.For(Game);
        TechBadges = _allBadges.Count <= MaxCardChips
            ? _allBadges
            : _allBadges.Take(MaxCardChips - 1)
                        .Append(new TechBadge($"+{_allBadges.Count - (MaxCardChips - 1)}", TechVendor.Other))
                        .ToList();
        OnPropertyChanged(nameof(HasNoUpscaler));
        OnPropertyChanged(nameof(CardTip));
    }

    /// <summary>
    /// True when nothing upscaling-related was found next to the game.
    ///
    /// Such a game cannot be helped by either route: OptiScaler hooks an upscaler the
    /// game already has rather than adding one, and there is nothing to swap. Worth
    /// saying on the card so the absence does not read as a bug.
    /// </summary>
    public bool HasNoUpscaler => TechBadges.Count == 0 && !Game.IsOptiscalerInstalled;

}
