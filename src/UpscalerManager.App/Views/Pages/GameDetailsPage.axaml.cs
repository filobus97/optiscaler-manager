// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using UpscalerManager.App.Services;
using UpscalerManager.App.ViewModels;
using UpscalerManager.Core.Components;
using UpscalerManager.Core.Models;
using UpscalerManager.Core.Services;

namespace UpscalerManager.App.Views.Pages;

/// <summary>
/// What a single game has, and what OptiScaler is set to do with it.
///
/// Written for someone deciding whether to install, not for debugging: versions and
/// plain-language settings, no hashes or absolute paths. The game list only has room
/// for a summary, so this is where "which FSR, exactly?" gets answered.
/// </summary>
public partial class GameDetailsPage : UserControl, IHostedPage
{
    /// <summary>What the user asked for on the way out.</summary>
    public enum DetailsOutcome { None, Install, Revert }

    private readonly ManagerService _manager = null!;
    private readonly GameRowViewModel _row = null!;

    /// <summary>Filled by <see cref="RenderComponents"/>, read by the rows it builds.</summary>
    private System.Collections.Generic.HashSet<string> _swappedFiles = new(StringComparer.OrdinalIgnoreCase);

    public DetailsOutcome Outcome { get; private set; } = DetailsOutcome.None;

    public string Title { get; private set; } = "Game details";
    public Action<bool>? RequestClose { get; set; }

    // Parameterless ctor for the XAML previewer only.
    public GameDetailsPage() { InitializeComponent(); }

    public GameDetailsPage(ManagerService manager, GameRowViewModel row) : this()
    {
        _manager = manager;
        _row = row;
        Title = row.Game.Name;

        // Open on the route this game is already using. A game with swapped DLLs and
        // no OptiScaler would otherwise land on an empty OptiScaler tab and look as
        // though nothing had been done to it.
        _swapTab = !row.Game.IsOptiscalerInstalled && HasSwaps();

        Render();
    }

    public void FocusFirst() =>
        this.FindControl<Button>("OptiScalerTab")?.Focus(NavigationMethod.Directional);

    // ── Tabs ────────────────────────────────────────────────────────────────────

    private bool HasSwaps()
    {
        try { return _manager.SwapSlots(_row.Game).Any(s => s.IsOurs); }
        catch { return false; }
    }


    /// <summary>Which route the page is showing. Not persisted: it follows the game.</summary>
    private bool _swapTab;

    private void OnSelectOptiScalerTab(object? sender, RoutedEventArgs e) => SelectTab(swap: false);

    private void OnSelectSwapTab(object? sender, RoutedEventArgs e) => SelectTab(swap: true);

    private void SelectTab(bool swap)
    {
        _swapTab = swap;
        ApplyTab();
    }

    private void ApplyTab()
    {
        Show("OptiScalerPanel", !_swapTab);
        Show("SwapPanel", _swapTab);

        Mark("OptiScalerTab", !_swapTab);
        Mark("SwapTab", _swapTab);

        // Install and Remove sit inside the OptiScaler panel, so hiding the panel
        // hides them. Remove still depends on there being something to remove.
        Show("RevertButton", _row.Game.IsOptiscalerInstalled);
    }

    private void Show(string name, bool visible)
    {
        if (this.FindControl<Control>(name) is { } control) control.IsVisible = visible;
    }

    private void Mark(string name, bool selected)
    {
        if (this.FindControl<Button>(name) is not { } tab) return;
        if (selected) tab.Classes.Add("selected");
        else tab.Classes.Remove("selected");
    }

    private void Render()
    {
        var game = _row.Game;

        var subtitle = this.FindControl<TextBlock>("SubtitleText");
        if (subtitle is not null)
            subtitle.Text = $"{game.Platform}  •  {game.InstallPath}";

        RenderComponents();
        RenderSwaps();
        RenderOptiScaler();
        ApplyTab();
    }

    /// <summary>
    /// Opens a page from inside this one. Supplied by the main window, which owns the
    /// page stack, so Back from the picker returns here rather than to the game list.
    /// </summary>
    public Func<IHostedPage, Task<bool>>? ShowPage { get; set; }

    // ── Swapping ────────────────────────────────────────────────────────────────

    private void RenderSwaps()
    {
        var list = this.FindControl<StackPanel>("SwapList");
        var empty = this.FindControl<TextBlock>("NoSwapsText");
        if (list is null) return;

        list.Children.Clear();

        var slots = _manager.SwapSlots(_row.Game);
        if (empty is not null) empty.IsVisible = slots.Count == 0;

        foreach (var slot in slots)
            list.Children.Add(SwapRow(slot));
    }

    private Control SwapRow(SwapSlot slot)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heading.Children.Add(new TextBlock
        {
            Text = slot.Version is { Length: > 0 } v
                ? $"{slot.Definition.Label}  {v}"
                : slot.Definition.Label,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("BrTextPrimary"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (slot.IsOurs) heading.Children.Add(SourceTag("swapped by this app"));

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(heading);
        text.Children.Add(new TextBlock
        {
            // What stands in the way, when something does — otherwise what the file is.
            Text = slot.Verdict.Allowed ? slot.Definition.Note : slot.Verdict.Reason,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(slot.Verdict.Allowed ? "BrTextSecondary" : "BrWarning"),
        });
        grid.Children.Add(text);

        // Always enabled, even when a swap is refused: the page behind it explains why,
        // and a dead button with no explanation is worse than one that tells you.
        var button = new Button
        {
            Content = slot.IsOurs ? "Change or revert" : "Choose a build",
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, slot.Verdict.Allowed
            ? $"Pick which build of {slot.FileName} this game should use, from your library or from your other games."
            : slot.Verdict.Reason);
        button.Click += async (_, _) => await OpenSwapPage(slot);

        Grid.SetColumn(button, 1);
        grid.Children.Add(button);

        return new Border
        {
            Padding = new Avalonia.Thickness(10, 8),
            CornerRadius = new Avalonia.CornerRadius(6),
            Background = Brush("BrBgSurface"),
            Child = grid,
        };
    }

    private async Task OpenSwapPage(SwapSlot slot)
    {
        if (ShowPage is null) return;

        var changed = await ShowPage(new DllSwapPage(_manager, _row.Game, slot));
        if (!changed) return;

        // A swap rewrites a file in place, which the analyzer's folder-timestamp cache
        // cannot see, so the whole page is re-read rather than just the swap rows.
        _row.RefreshFromGame();
        Render();
    }

    // ── What the game has ───────────────────────────────────────────────────────

    private void RenderComponents()
    {
        var list = this.FindControl<StackPanel>("ComponentsList");
        var empty = this.FindControl<TextBlock>("NoComponentsText");
        if (list is null) return;

        list.Children.Clear();
        _swappedFiles = SwappedFileNames();
        var components = _row.Game.DetectedComponents;
        if (empty is not null) empty.IsVisible = components.Count == 0;
        if (components.Count == 0) return;

        // Grouped by what they do, because "upscaler" and "frame generation" are the
        // distinction a player actually cares about.
        foreach (var group in components.GroupBy(c => c.Role)
                                        .OrderBy(g => UpscalerCatalog.RoleOrder(g.Key)))
        {
            list.Children.Add(new TextBlock
            {
                Text = UpscalerCatalog.RoleHeading(group.Key),
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush("BrTextPrimary"),
                Margin = new Avalonia.Thickness(0, 4, 0, 0),
            });

            foreach (var c in group)
                list.Children.Add(ComponentRow(c));
        }
    }

    /// <summary>
    /// Files this app swapped, so the list above can say so too. Attribution otherwise
    /// comes from the install manifest, which knows nothing about swaps — leaving the
    /// same file tagged in one section of this page and untagged in the other.
    /// </summary>
    private System.Collections.Generic.HashSet<string> SwappedFileNames()
    {
        try
        {
            return _manager.SwapSlots(_row.Game)
                .Where(s => s.IsOurs)
                .Select(s => s.FileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private Control ComponentRow(DetectedComponent c)
    {
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heading.Children.Add(new TextBlock
        {
            Text = c.Version is null ? c.Technology : $"{c.Technology}  {c.Version}",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("BrTextPrimary"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        // Only tagged when the manifest proves this app put the file here. Anything else
        // gets no tag: the app cannot tell a file the game shipped from one a mod or the
        // player dropped in, and "came with the game" was asserting exactly that.
        if (c.Source == ComponentSource.Manager)
            heading.Children.Add(SourceTag("added by this app"));
        else if (_swappedFiles.Contains(c.FileName))
            heading.Children.Add(SourceTag("swapped by this app"));

        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(heading);
        panel.Children.Add(new TextBlock
        {
            Text = c.Explanation,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("BrTextSecondary"),
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{c.Vendor}  •  {c.RelativePath}",
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("BrTextDisabled"),
        });

        return new Border
        {
            Padding = new Avalonia.Thickness(10, 8),
            CornerRadius = new Avalonia.CornerRadius(6),
            Background = Brush("BrBgSurface"),
            Child = panel,
        };
    }

    // ── OptiScaler ──────────────────────────────────────────────────────────────

    private void RenderOptiScaler()
    {
        var game = _row.Game;
        var summary = this.FindControl<TextBlock>("OptiScalerSummaryText");
        var config = this.FindControl<StackPanel>("ConfigList");
        var revert = this.FindControl<Button>("RevertButton");
        var install = this.FindControl<Button>("InstallButton");

        var installed = game.IsOptiscalerInstalled;
        if (install is not null) install.Content = installed ? "Reinstall OptiScaler" : "Install OptiScaler";
        // Whether Remove is shown is ApplyTab's call; setting it here too would let
        // the two disagree.
        _ = revert;

        if (summary is not null)
        {
            summary.Text = installed
                ? $"Installed — version {game.OptiscalerVersion ?? "unknown"}."
                : "Not installed. Installing it lets this game's existing DLSS or FSR option drive a different upscaler — the route for games a straight DLL swap cannot help.";
        }

        if (config is null) return;
        config.Children.Clear();
        if (!installed) return;

        var dir = _manager.GetInstalledDirectory(game) ?? game.InstallPath;
        var facts = OptiScalerConfigReader.Read(dir);
        if (facts.Count == 0)
        {
            config.Children.Add(new TextBlock
            {
                Text = "No OptiScaler.ini found, so its settings could not be read.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("BrTextSecondary"),
            });
            return;
        }

        config.Children.Add(new TextBlock
        {
            Text = "What it is set to do",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("BrTextPrimary"),
            Margin = new Avalonia.Thickness(0, 6, 0, 0),
        });

        foreach (var fact in facts)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*") };

            var label = new TextBlock
            {
                Text = fact.Label,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("BrTextSecondary"),
            };
            var value = new TextBlock
            {
                Text = fact.Value,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("BrTextPrimary"),
            };
            // The ini key is there for anyone who wants to verify, without shouting.
            ToolTip.SetTip(value, fact.Source);

            Grid.SetColumn(label, 0);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
            config.Children.Add(grid);
        }
    }

    // ── Bits and pieces ─────────────────────────────────────────────────────────

    private Control SourceTag(string text) => new Border
    {
        Background = Brush("BrBgElevated"),
        BorderBrush = Brush("BrBorderSubtle"),
        BorderThickness = new Avalonia.Thickness(1),
        CornerRadius = new Avalonia.CornerRadius(4),
        Padding = new Avalonia.Thickness(6, 1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 10.5, Foreground = Brush("BrTextSecondary") },
    };

    private IBrush? Brush(string key) =>
        Application.Current?.FindResource(key) as IBrush;

    // ── Actions ─────────────────────────────────────────────────────────────────

    private void OnInstallClick(object? sender, RoutedEventArgs e)
    {
        Outcome = DetailsOutcome.Install;
        RequestClose?.Invoke(true);
    }

    private void OnRevertClick(object? sender, RoutedEventArgs e)
    {
        Outcome = DetailsOutcome.Revert;
        RequestClose?.Invoke(true);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => RequestClose?.Invoke(false);

    /// <summary>Re-reads the folder, for when files were changed outside the app.</summary>
    private void OnRescanClick(object? sender, RoutedEventArgs e)
    {
        _manager.RefreshGameAnalysis(_row.Game);
        _row.RefreshFromGame();
        Render();
    }
}
