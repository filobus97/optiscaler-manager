// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
/// One game: install or manage OptiScaler, and update the upscaler libraries it ships.
///
/// One page rather than two tabs. OptiScaler can help any game and a swap only upgrades
/// a library the game already has, so they are not alternatives of equal standing and
/// presenting them as tabs charged the user a decision they should not have to make.
/// </summary>
public partial class GameDetailsPage : UserControl, IHostedPage
{
    /// <summary>What the user asked for on the way out.</summary>
    public enum DetailsOutcome { None, Install, Revert }

    private readonly ManagerService _manager = null!;
    private readonly GameRowViewModel _row = null!;

    /// <summary>Files this app swapped, so the Details list can say so too.</summary>
    private HashSet<string> _swappedFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cancels the archive lookup when the page re-renders or closes.</summary>
    private CancellationTokenSource? _archiveLookup;

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
        DetachedFromVisualTree += (_, _) => CancelArchiveLookup();
    }

    public void FocusFirst() =>
        this.FindControl<Button>("InstallButton")?.Focus(NavigationMethod.Directional);

    /// <summary>
    /// Opens a page from inside this one. Supplied by the main window, which owns the
    /// page stack, so Back from the version list returns here rather than to the games.
    /// </summary>
    public Func<IHostedPage, Task<bool>>? ShowPage { get; set; }

    private void Render()
    {
        var game = _row.Game;

        if (this.FindControl<TextBlock>("SubtitleText") is { } subtitle)
            subtitle.Text = $"{game.Platform}  •  {game.InstallPath}";

        _swappedFiles = SwappedFileNames();
        RenderOptiScaler();
        RenderLibraries();
        RenderComponents();
        RenderConfig();
    }

    // ── OptiScaler ──────────────────────────────────────────────────────────────

    private void RenderOptiScaler()
    {
        var game = _row.Game;
        var installed = game.IsOptiscalerInstalled;

        if (this.FindControl<TextBlock>("OptiScalerStateText") is { } state)
            state.Text = installed
                ? $"Installed — {game.OptiscalerVersion ?? "unknown version"}"
                : "Not installed";

        if (this.FindControl<Button>("InstallButton") is { } install)
            install.Content = installed ? "Reinstall" : "Install OptiScaler";

        if (this.FindControl<Button>("RevertButton") is { } revert)
            revert.IsVisible = installed;
    }

    // ── Upscaler libraries ──────────────────────────────────────────────────────

    /// <summary>The parts of one library row the archive lookup fills in later.</summary>
    /// <param name="Shown">
    /// The version the row is already offering, so a build from the network only
    /// replaces it when it really is newer. Null when the row offers nothing yet.
    /// </param>
    private sealed class RowParts(SwapSlot slot, TextBlock newest, TextBlock note, Button action)
    {
        public SwapSlot Slot { get; } = slot;
        public TextBlock Newest { get; } = newest;
        public TextBlock Note { get; } = note;
        public Button Action { get; } = action;
        public string? Shown { get; set; }
    }

    private readonly List<RowParts> _rows = new();

    private void RenderLibraries()
    {
        var list = this.FindControl<StackPanel>("SwapList");
        var empty = this.FindControl<TextBlock>("NoSwapsText");
        if (list is null) return;

        CancelArchiveLookup();
        list.Children.Clear();
        _rows.Clear();

        var slots = _manager.SwapSlots(_row.Game);

        if (empty is not null)
        {
            empty.IsVisible = slots.Count == 0;
            empty.Text = "No swappable library is present. A swap upgrades a library the "
                         + "game already ships; OptiScaler is the route that can add one.";
        }

        foreach (var slot in slots)
            list.Children.Add(LibraryRow(slot));

        StartArchiveLookup();
    }

    private Control LibraryRow(SwapSlot slot)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var best = _manager.BestLocalBuild(slot.FileName, _row.Game);
        var newer = best is { } b && VersionOrder.IsNewer(b.Version, slot.Version);

        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        line.Children.Add(new TextBlock
        {
            Text = slot.Definition.Technology,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        line.Children.Add(new TextBlock
        {
            Text = slot.VersionText,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var newest = new TextBlock
        {
            Text = newer ? $"→  {best!.Value.Display}" : string.Empty,
            IsVisible = newer,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("BrTextPrimary"),
        };
        line.Children.Add(newest);

        if (slot.IsOurs) line.Children.Add(Chip("swapped by this app"));
        // A game with the same DLL in several places is worth flagging here, not only
        // inside the version list: it is the difference between a swap that works and
        // one that appears to do nothing.
        if (slot.CopyCount > 1) line.Children.Add(Chip($"{slot.CopyCount} copies"));

        var note = new TextBlock
        {
            Text = NoteFor(slot, newer ? best : null),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(slot.Verdict.Allowed ? "BrTextSecondary" : "BrWarning"),
        };

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(line);
        text.Children.Add(note);
        grid.Children.Add(text);

        // Always enabled, even when a swap is refused: the page behind it explains why,
        // and a dead button with no explanation is worse than one that tells you.
        var action = new Button
        {
            Content = ActionFor(slot, newer),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(action, slot.Verdict.Allowed
            ? $"Every build of {slot.FileName} this app can reach, newest first."
            : slot.Verdict.Reason);
        action.Click += async (_, _) => await OpenVersionList(slot);

        Grid.SetColumn(action, 1);
        grid.Children.Add(action);

        _rows.Add(new RowParts(slot, newest, note, action) { Shown = newer ? best!.Value.Version : null });

        return new Border { Classes = { "Row" }, Child = grid };
    }

    private static string ActionFor(SwapSlot slot, bool newer) =>
        newer ? "Update" : slot.IsOurs ? "Change or revert" : "Choose a build";

    /// <summary>
    /// The caption under a row: what stands in the way when something does, otherwise
    /// where the newer build would come from, otherwise what the file is.
    /// </summary>
    private static string NoteFor(SwapSlot slot, ManagerService.AvailableBuild? newer) =>
        !slot.Verdict.Allowed ? slot.Verdict.Reason
        : newer is { } b ? b.Source
        : slot.Definition.Note;

    /// <summary>
    /// Asks the archive whether it holds something newer than each row shows.
    ///
    /// why: the rows render from what is on disk first and are corrected afterwards.
    /// The index is cached for a day, but the first call of the day is a network fetch,
    /// and the page must not wait on it to draw.
    /// </summary>
    private void StartArchiveLookup()
    {
        if (_rows.Count == 0) return;

        var cancel = new CancellationTokenSource();
        _archiveLookup = cancel;
        var rows = _rows.ToList();

        _ = Task.Run(async () =>
        {
            foreach (var row in rows)
            {
                if (cancel.IsCancellationRequested) return;
                if (!ManagerService.TakesRepositoryBuilds(row.Slot.FileName)) continue;

                ManagerService.AvailableBuild? build;
                try { build = await _manager.BestArchiveBuildAsync(row.Slot.FileName, cancel.Token); }
                catch { continue; }

                if (build is not { } found) continue;
                if (!VersionOrder.IsNewer(found.Version, row.Slot.Version)) continue;

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (cancel.IsCancellationRequested) return;
                    // Only when it beats what the row already offers, so a local build
                    // is not replaced by an equal one from the network.
                    if (!VersionOrder.IsNewer(found.Version, row.Shown ?? row.Slot.Version)) return;
                    row.Shown = found.Version;

                    row.Newest.Text = $"→  {found.Display}";
                    row.Newest.IsVisible = true;
                    if (row.Slot.Verdict.Allowed) row.Note.Text = found.Source;
                    row.Action.Content = ActionFor(row.Slot, newer: true);
                });
            }
        }, cancel.Token);
    }

    private void CancelArchiveLookup()
    {
        _archiveLookup?.Cancel();
        _archiveLookup = null;
    }

    private async Task OpenVersionList(SwapSlot slot)
    {
        if (ShowPage is null) return;

        var changed = await ShowPage(new DllSwapPage(_manager, _row.Game, slot));
        if (!changed) return;

        // A swap rewrites a file in place, which the analyzer's folder-timestamp cache
        // cannot see, so the whole page is re-read rather than just the rows.
        _row.RefreshFromGame();
        Render();
    }

    // ── Details: what the game has, and what OptiScaler does with it ────────────

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
                FontSize = 12.5,
                FontWeight = FontWeight.SemiBold,
                Margin = new Avalonia.Thickness(0, 4, 0, 0),
            });

            foreach (var c in group)
                list.Children.Add(ComponentRow(c));
        }
    }

    /// <summary>
    /// Files this app swapped. Attribution otherwise comes from the install manifest,
    /// which knows nothing about swaps — leaving the same file tagged in one part of
    /// this page and untagged in the other.
    /// </summary>
    private HashSet<string> SwappedFileNames()
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
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private Control ComponentRow(DetectedComponent c)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        line.Children.Add(new TextBlock
        {
            Text = c.Version is null ? c.Technology : $"{c.Technology}  {c.VersionText}",
            VerticalAlignment = VerticalAlignment.Center,
        });
        // Only tagged when the manifest proves this app put the file here. Anything else
        // gets no tag: the app cannot tell a file the game shipped from one a mod or the
        // player dropped in, and "came with the game" was asserting exactly that.
        if (c.Source == ComponentSource.Manager)
            line.Children.Add(Chip("added by this app"));
        else if (_swappedFiles.Contains(c.FileName))
            line.Children.Add(Chip("swapped by this app"));

        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(line);
        panel.Children.Add(new TextBlock
        {
            Text = $"{c.Vendor}  •  {c.RelativePath}",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("BrTextDisabled"),
        });

        var row = new Border { Classes = { "Row" }, Child = panel };
        // What the file is, for anyone who wants it, without a paragraph on the page.
        ToolTip.SetTip(row, c.Explanation);
        return row;
    }

    private void RenderConfig()
    {
        var config = this.FindControl<StackPanel>("ConfigList");
        if (config is null) return;

        config.Children.Clear();
        if (!_row.Game.IsOptiscalerInstalled) return;

        var dir = _manager.GetInstalledDirectory(_row.Game) ?? _row.Game.InstallPath;
        var facts = OptiScalerConfigReader.Read(dir);

        config.Children.Add(new TextBlock
        {
            Text = "What OptiScaler is set to do",
            FontSize = 11,
            Foreground = Brush("BrTextSecondary"),
        });

        if (facts.Count == 0)
        {
            config.Children.Add(new TextBlock
            {
                Text = "No OptiScaler.ini found, so its settings could not be read.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("BrTextSecondary"),
            });
            return;
        }

        foreach (var fact in facts)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*") };

            var label = new TextBlock
            {
                Text = fact.Label,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("BrTextSecondary"),
            };
            var value = new TextBlock { Text = fact.Value, TextWrapping = TextWrapping.Wrap };
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

    private Control Chip(string text) => new Border
    {
        Background = Brush("BrBgElevated"),
        CornerRadius = new Avalonia.CornerRadius(4),
        Padding = new Avalonia.Thickness(6, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = Brush("BrTextSecondary") },
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
