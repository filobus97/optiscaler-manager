// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using UpscalerManager.App.Services;
using UpscalerManager.Core.Models;
using UpscalerManager.Core.Services;

namespace UpscalerManager.App.Views.Pages;

/// <summary>
/// What the app is keeping on disk, and what can safely be removed.
///
/// The screen is organised by how recoverable each thing is rather than by what it is,
/// because that is the only question that matters when deleting: a downloaded component
/// comes back, an imported DLL does not, and a backup that a game is still relying on
/// must not go at all.
/// </summary>
public partial class StoragePage : UserControl, IHostedPage
{
    private readonly ManagerService? _manager;

    public StoragePage() { InitializeComponent(); }

    public StoragePage(ManagerService manager, Func<string, Task<bool>>? revertGame = null) : this()
    {
        _manager = manager;
        _revertGame = revertGame;
        Refresh();
    }

    public string Title => "Storage";
    public Action<bool>? RequestClose { get; set; }

    /// <summary>
    /// Reverts the game installed in this directory. Supplied by the main window, which
    /// owns the game list and the revert flow; a live backup is the one thing this
    /// screen cannot delete, and reverting is what makes it deletable.
    ///
    /// Passed in rather than set afterwards: the rows are built during construction, and
    /// a property assigned by an object initializer would arrive too late to enable them.
    /// </summary>
    private readonly Func<string, Task<bool>>? _revertGame;

    public void FocusFirst() =>
        this.FindControl<StackPanel>("GroupsPanel")?
            .GetVisualDescendants().OfType<Button>().FirstOrDefault()?
            .Focus(NavigationMethod.Directional);

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Tiers in the order they are shown: reclaim the safe space first.</summary>
    private static readonly (StorageTier Tier, string Heading, string Blurb)[] Sections =
    {
        (StorageTier.SpentBackup, "Backups no longer in use",
            "The games these belong to have been reverted or are no longer on this machine. Nothing depends on them."),
        (StorageTier.Downloaded, "Downloaded components",
            "Fetched automatically and normally downloadable again — but releases do get withdrawn upstream, especially betas and nightlies, so a version removed here may not come back."),
        (StorageTier.UserImport, "Files you imported",
            "These came from you, and this is the only copy. Removing one is permanent unless you have your own backup of it elsewhere."),
        (StorageTier.LiveBackup, "Backups in use",
            "The original files of games that still have OptiScaler installed. Revert restores from here and has nowhere else to look, so these cannot be removed — revert the game first and it will move to the section above."),
    };

    private void Refresh()
    {
        if (_manager is null) return;

        var panel = this.FindControl<StackPanel>("GroupsPanel");
        var total = this.FindControl<TextBlock>("TotalText");
        if (panel is null) return;

        var items = _manager.ScanStorage();
        panel.Children.Clear();

        if (items.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Nothing stored yet. Components you install and files you import will appear here.",
                Foreground = Brush("BrTextSecondary"),
                TextWrapping = TextWrapping.Wrap,
            });
            if (total is not null) total.Text = "";
            return;
        }

        if (total is not null)
            total.Text = $"{StorageInventoryService.FormatSize(items.Sum(i => i.Bytes))} in total";

        foreach (var (tier, heading, blurb) in Sections)
        {
            var inTier = items.Where(i => i.Tier == tier).ToList();
            if (inTier.Count == 0) continue;
            panel.Children.Add(BuildSection(heading, blurb, tier, inTier));
        }
    }

    private Border BuildSection(string heading, string blurb, StorageTier tier, List<StorageItem> items)
    {
        var body = new StackPanel { Spacing = 8 };

        body.Children.Add(new TextBlock
        {
            Text = $"{heading}  —  {StorageInventoryService.FormatSize(items.Sum(i => i.Bytes))}",
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("BrTextPrimary"),
        });
        body.Children.Add(new TextBlock
        {
            Text = blurb,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("BrTextSecondary"),
        });

        // Grouped by component so a long list of versions stays readable.
        foreach (var group in items.GroupBy(i => i.Group).OrderByDescending(g => g.Sum(i => i.Bytes)))
        {
            if (items.Select(i => i.Group).Distinct().Count() > 1)
            {
                body.Children.Add(new TextBlock
                {
                    Text = group.Key,
                    FontSize = 11,
                    Margin = new Avalonia.Thickness(0, 6, 0, 0),
                    Foreground = Brush("BrTextSecondary"),
                });
            }

            foreach (var item in InReadingOrder(group))
                body.Children.Add(BuildRow(item, tier));
        }

        return new Border
        {
            Padding = new Avalonia.Thickness(14),
            CornerRadius = new Avalonia.CornerRadius(8),
            Background = Brush("BrBgCard"),
            BorderBrush = Brush("BrBorderSubtle"),
            BorderThickness = new Avalonia.Thickness(1),
            Child = body,
        };
    }

    /// <summary>
    /// Version lists read newest-first; anything else (file names, game names) is ordered
    /// biggest-first, since reclaiming space is why the list is being read at all.
    /// </summary>
    private static IEnumerable<StorageItem> InReadingOrder(IEnumerable<StorageItem> items)
    {
        var list = items.ToList();
        var allVersions = list.All(i => VersionOrder.BaseVersion(i.Label) > new Version(0, 0));
        return allVersions
            ? list.OrderBy(i => i.Label, VersionOrder.Descending)
            : list.OrderByDescending(i => i.Bytes);
    }

    private Control BuildRow(StorageItem item, StorageTier tier)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Avalonia.Thickness(0, 2, 0, 2),
        };

        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        labels.Children.Add(new TextBlock
        {
            Text = item.Label,
            Foreground = Brush("BrTextPrimary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (item.Note is not null)
        {
            labels.Children.Add(new TextBlock
            {
                Text = item.Note,
                FontSize = 11,
                Foreground = Brush("BrTextSecondary"),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        Grid.SetColumn(labels, 0);
        grid.Children.Add(labels);

        var size = new TextBlock
        {
            Text = StorageInventoryService.FormatSize(item.Bytes),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(10, 0, 10, 0),
            Foreground = Brush("BrTextSecondary"),
            FontSize = 12,
        };
        Grid.SetColumn(size, 1);
        grid.Children.Add(size);

        // A backup kept alive only by swapped DLLs has no OptiScaler to remove, so the
        // Revert button here would do nothing. Reverting a swap is per-file and lives on
        // the game's own page, which is where this points.
        Control action = tier != StorageTier.LiveBackup ? BuildDeleteButton(item, tier)
            : item.SwapsOnly ? BuildSwapRevertHint()
            : BuildRevertButton(item);
        Grid.SetColumn(action, 2);
        grid.Children.Add(action);

        return grid;
    }

    private Button BuildDeleteButton(StorageItem item, StorageTier tier)
    {
        var button = new Button { Content = "Remove", FontSize = 11 };

        // An import has no other copy, so it takes a second press to confirm.
        var needsConfirming = tier == StorageTier.UserImport;
        var armed = false;

        button.Click += (_, _) =>
        {
            if (needsConfirming && !armed)
            {
                armed = true;
                button.Content = "Really remove?";
                SetStatus($"{item.Label} is a file you imported — this is the only copy. Press again to remove it.");
                return;
            }

            if (_manager!.DeleteStorageItem(item))
            {
                SetStatus($"Removed {item.Label} ({StorageInventoryService.FormatSize(item.Bytes)} freed).");
                Refresh();
            }
            else
            {
                SetStatus($"Could not remove {item.Label}. It may be in use.");
            }
        };
        return button;
    }

    private Button BuildRevertButton(StorageItem item)
    {
        var button = new Button
        {
            Content = "Revert game",
            FontSize = 11,
            IsEnabled = RevertGameAvailable(item),
        };
        ToolTip.SetTip(button,
            "Removes OptiScaler from this game and restores these original files. " +
            "Afterwards the backup is no longer needed and can be removed here.");

        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            SetStatus($"Reverting {item.Label}…");
            var ok = await _revertGame!(item.GameDirectory!);
            SetStatus(ok
                ? $"Reverted {item.Label}. Its backup can now be removed."
                : $"Could not revert {item.Label}. Try it from the game's Details page.");
            Refresh();
        };
        return button;
    }

    /// <summary>
    /// Stands in for the Revert button on a swap-only backup: there is no single action
    /// here, because a game can have several DLLs swapped and each is reverted on its own.
    /// </summary>
    private static TextBlock BuildSwapRevertHint()
    {
        var hint = new TextBlock
        {
            Text = "Revert on the game's page",
            FontSize = 11,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = Brush("BrTextSecondary"),
        };
        ToolTip.SetTip(hint,
            "Each swapped DLL is reverted separately, from the game's own page. " +
            "Once none is swapped, this backup moves to the section above and can be removed.");
        return hint;
    }

    private bool RevertGameAvailable(StorageItem item) =>
        _revertGame is not null && item.GameDirectory is not null;

    private void SetStatus(string text)
    {
        var status = this.FindControl<TextBlock>("StatusText");
        if (status is not null) status.Text = text;
        var bar = this.FindControl<Border>("StatusBar");
        if (bar is not null) bar.IsVisible = !string.IsNullOrEmpty(text);
    }

    private static IBrush? Brush(string key) =>
        Application.Current?.FindResource(key) as IBrush;
}
