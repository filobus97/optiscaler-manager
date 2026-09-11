// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using UpscalerManager.App.Services;
using UpscalerManager.Core.Models;
using UpscalerManager.Core.Services;

namespace UpscalerManager.App.Views.Pages;

/// <summary>
/// Picks which build of one swappable DLL a game should use.
///
/// A nested page rather than a dialog window, like every other screen here: extra
/// top-level windows are unreliable under gamescope, which is how this is used on a
/// Steam Deck.
/// </summary>
public partial class DllSwapPage : UserControl, IHostedPage
{
    private readonly ManagerService _manager = null!;
    private readonly Game _game = null!;
    private SwapSlot _slot = null!;

    public string Title { get; private set; } = "Swap a DLL";
    public Action<bool>? RequestClose { get; set; }

    /// <summary>True when anything was actually changed, so the caller can re-render.</summary>
    public bool Changed { get; private set; }

    // Parameterless ctor for the XAML previewer only.
    public DllSwapPage() { InitializeComponent(); }

    public DllSwapPage(ManagerService manager, Game game, SwapSlot slot) : this()
    {
        _manager = manager;
        _game = game;
        _slot = slot;
        Title = slot.Definition.Label;
        Render();
    }

    public void FocusFirst()
    {
        // The first thing worth pressing, which is whichever build is offered first —
        // falling back to Import when the library and the user's games are both empty.
        var first = this.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.IsEffectivelyVisible && b.IsEnabled && b.Name is null);
        (first ?? this.FindControl<Button>("ImportButton"))?.Focus(NavigationMethod.Directional);
    }

    private IStorageProvider StorageProvider =>
        TopLevel.GetTopLevel(this)?.StorageProvider
        ?? throw new InvalidOperationException("No storage provider is available.");

    // ── Rendering ───────────────────────────────────────────────────────────

    private void Render()
    {
        var definition = _slot.Definition;

        Text("HeadingText", $"{definition.Label}  —  {definition.FileName}");
        Text("ExplanationText", definition.Note);

        var warning = this.FindControl<Border>("WarningBox");
        if (warning is not null)
        {
            warning.IsVisible = !_slot.Verdict.Allowed;
            Text("WarningText", _slot.Verdict.Reason);
        }

        RenderCurrent();
        RenderLibrary();
        RenderHarvestable();
    }

    /// <summary>What is in the game now, and the way back if this app put it there.</summary>
    private void RenderCurrent()
    {
        var panel = this.FindControl<StackPanel>("CurrentPanel");
        if (panel is null) return;
        panel.Children.Clear();

        panel.Children.Add(Label("In the game now", 13, FontWeight.SemiBold, "BrTextPrimary"));

        var version = _slot.Version is { Length: > 0 } v ? v : "no version in the file";
        panel.Children.Add(Label($"{_slot.FileName}  {version}", 12, FontWeight.Normal, "BrTextPrimary"));
        panel.Children.Add(Label(_slot.Directory, 10.5, FontWeight.Normal, "BrTextDisabled"));

        if (_slot.Swapped is not { } swapped)
        {
            panel.Children.Add(Label(
                "This is the game's own build. Swapping keeps a copy of it, so you can always go back.",
                11.5, FontWeight.Normal, "BrTextSecondary"));
            return;
        }

        var original = swapped.ExistedBefore
            ? $"the game's own {swapped.OriginalVersion ?? "build"}"
            : "no file at all — reverting removes it";

        panel.Children.Add(Label(
            $"Swapped by this app, {swapped.SourceLabel}. Reverting puts back {original}.",
            11.5, FontWeight.Normal, "BrTextSecondary"));

        var revert = new Button { Content = "Revert to the game's own build", FontSize = 12, };
        ToolTip.SetTip(revert,
            "Restores the file this app backed up before swapping. Refused if the DLL has " +
            "changed since — a game patch replacing it is the usual reason, and putting the " +
            "older original back would undo that.");
        revert.Click += (_, _) => Revert(swapped, force: false);

        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 4, 0, 0),
            Children = { revert },
        });
    }

    private void RenderLibrary()
    {
        var panel = this.FindControl<StackPanel>("LibraryPanel");
        if (panel is null) return;
        panel.Children.Clear();

        var builds = _manager.LibraryBuilds(_slot.FileName);
        if (builds.Count == 0)
        {
            panel.Children.Add(Label(
                "Nothing held yet. Add one from a game below, or import a file you already have.",
                11.5, FontWeight.Normal, "BrTextSecondary"));
            return;
        }

        foreach (var build in builds)
            panel.Children.Add(BuildRow(
                build.Version,
                build.SourceLabel,
                isCurrent: IsInstalled(build.Version),
                action: "Use this build",
                onAction: () => Swap(build)));
    }

    private void RenderHarvestable()
    {
        var panel = this.FindControl<StackPanel>("HarvestPanel");
        if (panel is null) return;
        panel.Children.Clear();

        var found = _manager.HarvestableBuilds(_slot.FileName, _game)
            .Where(h => !h.InLibrary)
            .ToList();

        if (found.Count == 0)
        {
            panel.Children.Add(Label(
                "No other build of this DLL was found in your other games. Importing a file is the other way in.",
                11.5, FontWeight.Normal, "BrTextSecondary"));
            return;
        }

        foreach (var candidate in found)
            panel.Children.Add(BuildRow(
                candidate.Version,
                $"in {candidate.GameName}",
                isCurrent: false,
                action: "Add and use",
                onAction: () => HarvestAndSwap(candidate)));
    }

    /// <summary>One version, its provenance, and the button that installs it.</summary>
    private Control BuildRow(string version, string source, bool isCurrent, string action, Action onAction)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };

        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Label(version, 12.5, FontWeight.SemiBold, "BrTextPrimary"));
        text.Children.Add(Label(source, 10.5, FontWeight.Normal, "BrTextSecondary"));
        grid.Children.Add(text);

        Control right;
        if (isCurrent)
        {
            // Installing the build that is already there would be a no-op that still
            // rewrote the game's file, so say so instead of offering it.
            right = Label("installed", 11.5, FontWeight.SemiBold, "BrSuccess");
            ((TextBlock)right).VerticalAlignment = VerticalAlignment.Center;
        }
        else
        {
            var button = new Button { Content = action, FontSize = 11.5, IsEnabled = _slot.Verdict.Allowed };
            if (!_slot.Verdict.Allowed) ToolTip.SetTip(button, _slot.Verdict.Reason);
            button.Click += (_, _) => onAction();
            right = button;
        }

        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        return new Border
        {
            Padding = new Avalonia.Thickness(10, 8),
            CornerRadius = new Avalonia.CornerRadius(6),
            Background = Brush("BrBgSurface"),
            Child = grid,
        };
    }

    private bool IsInstalled(string version) =>
        _slot.Version is { Length: > 0 } current
        && string.Equals(current, version, StringComparison.OrdinalIgnoreCase);

    // ── Actions ─────────────────────────────────────────────────────────────

    private void Swap(LibraryDll build)
    {
        Act($"Installing {build.FileName} {build.Version}…", () =>
        {
            _manager.SwapDll(_game, _slot, build);
            return $"{_slot.Definition.Label} is now {build.Version}.";
        });
    }

    private void HarvestAndSwap(HarvestableDll candidate)
    {
        Act($"Copying {candidate.FileName} {candidate.Version} from {candidate.GameName}…", () =>
        {
            var build = _manager.HarvestBuild(candidate);
            _manager.SwapDll(_game, _slot, build);
            return $"{_slot.Definition.Label} is now {build.Version}, kept in your library.";
        });
    }

    private void Revert(SwappedFile swapped, bool force)
    {
        Act("Restoring the game's own build…", () =>
        {
            _manager.RevertSwap(_game, swapped, force);
            return $"{_slot.Definition.Label} is back to the game's own build.";
        });
    }

    /// <summary>
    /// Runs one file operation, reports what happened, and re-reads the game afterwards.
    ///
    /// Every one of these rewrites a file in the player's game folder, so a failure has
    /// to be visible rather than swallowed — and the page has to re-read from disk after,
    /// because the version shown is what decides whether the next press is a no-op.
    /// </summary>
    private void Act(string busyText, Func<string> operation)
    {
        SetStatus(busyText);
        try
        {
            var done = operation();
            Changed = true;
            Reload();
            SetStatus(done);
        }
        catch (Exception ex)
        {
            Reload();
            SetStatus(ex.Message);
        }
    }

    /// <summary>
    /// Re-reads the game and finds this DLL's row again. The slot carries the version on
    /// disk and the swap record, both of which every action changes.
    /// </summary>
    private void Reload()
    {
        try
        {
            _manager.RefreshGameAnalysis(_game);
            var refreshed = _manager.SwapSlots(_game)
                .FirstOrDefault(s => s.FileName.Equals(_slot.FileName, StringComparison.OrdinalIgnoreCase));

            // A reverted "was not there before" swap removes the file, so the row can
            // legitimately disappear. Keep the old slot then, so the page still renders.
            if (refreshed is not null) _slot = refreshed;
        }
        catch (Exception ex)
        {
            UpscalerManager.Core.Logging.Log.Write($"[Swap] Could not re-read {_game.Name}: {ex.Message}");
        }

        Render();
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"Pick a {_slot.FileName}",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Windows DLL") { Patterns = new[] { "*.dll" } },
                },
            });

            if (files.Count == 0 || files[0].TryGetLocalPath() is not { Length: > 0 } path) return;

            Act($"Importing {System.IO.Path.GetFileName(path)}…", () =>
            {
                var build = _manager.ImportSwappableDll(path);
                return build.FileName.Equals(_slot.FileName, StringComparison.OrdinalIgnoreCase)
                    ? $"Imported {build.FileName} {build.Version}. Pick it above to install it."
                    : $"Imported {build.FileName} {build.Version} — that is a different DLL, so it is " +
                      $"in the library but not offered here.";
            });
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => RequestClose?.Invoke(Changed);

    // ── Small helpers ───────────────────────────────────────────────────────

    private void Text(string name, string? value)
    {
        if (this.FindControl<TextBlock>(name) is { } block) block.Text = value;
    }

    private void SetStatus(string text) => Text("StatusText", text);

    private static TextBlock Label(string text, double size, FontWeight weight, string brushKey) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush(brushKey),
    };

    private static IBrush? Brush(string key) =>
        Avalonia.Application.Current?.FindResource(key) as IBrush;
}
