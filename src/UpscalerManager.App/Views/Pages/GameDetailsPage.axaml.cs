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
        Render();
    }

    public void FocusFirst() =>
        this.FindControl<Button>("InstallButton")?.Focus(NavigationMethod.Directional);

    private void Render()
    {
        var game = _row.Game;

        var subtitle = this.FindControl<TextBlock>("SubtitleText");
        if (subtitle is not null)
            subtitle.Text = $"{game.Platform}  •  {game.InstallPath}";

        RenderComponents();
        RenderOptiScaler();
    }

    // ── What the game has ───────────────────────────────────────────────────────

    private void RenderComponents()
    {
        var list = this.FindControl<StackPanel>("ComponentsList");
        var empty = this.FindControl<TextBlock>("NoComponentsText");
        if (list is null) return;

        list.Children.Clear();
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
        if (revert is not null) revert.IsVisible = installed;
        if (install is not null) install.Content = installed ? "Reinstall OptiScaler" : "Install OptiScaler";

        if (summary is not null)
        {
            summary.Text = installed
                ? $"Installed — version {game.OptiscalerVersion ?? "unknown"}."
                : "Not installed. Installing it lets this game's DLSS or FSR option drive FSR 4 instead.";
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
