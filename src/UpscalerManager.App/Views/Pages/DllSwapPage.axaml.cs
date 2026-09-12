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
using UpscalerManager.Core.Components;
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
        RenderCommunity();
        RenderRepository();
        RenderVendor();
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

        // The games and the downloaded OptiScaler releases are one list: from here they
        // are the same thing — a build already on the disk that costs nothing to copy.
        var found = _manager.HarvestableBuilds(_slot.FileName, _game)
            .Concat(_manager.OptiScalerBuilds(_slot.FileName))
            .Concat(_manager.CommunityBuilds(_slot.FileName))
            .Where(h => !h.InLibrary)
            .GroupBy(h => h.Version, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(h => h.Version, UpscalerManager.Core.Models.VersionOrder.Descending)
            .ToList();

        if (found.Count == 0)
        {
            panel.Children.Add(Label(
                "No other build of this DLL was found in your games, or in the OptiScaler "
                + "releases and community builds you have downloaded.",
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

    /// <summary>
    /// FSR 4 INT8 community builds, offered only for the two filenames they ship as.
    ///
    /// A separate section from the vendor downloads on purpose: these come from a third
    /// party rather than from AMD, and collapsing the two would present them as
    /// equally official.
    /// </summary>
    private async void RenderCommunity()
    {
        var section = this.FindControl<StackPanel>("CommunitySection");
        var panel = this.FindControl<StackPanel>("CommunityPanel");
        if (section is null || panel is null) return;

        section.IsVisible = ManagerService.TakesCommunityBuilds(_slot.FileName);
        if (!section.IsVisible) return;

        panel.Children.Clear();
        panel.Children.Add(Label("Checking what has been published…",
            11.5, FontWeight.Normal, "BrTextSecondary"));

        IReadOnlyList<(string Version, bool IsPreRelease)> releases;
        try
        {
            releases = await _manager.CommunityBuildReleasesAsync();
        }
        catch (Exception ex)
        {
            panel.Children.Clear();
            panel.Children.Add(Label($"Could not list the community builds: {ex.Message}",
                11.5, FontWeight.Normal, "BrTextSecondary"));
            return;
        }

        if (!ReferenceEquals(panel, this.FindControl<StackPanel>("CommunityPanel"))) return;

        panel.Children.Clear();
        if (releases.Count == 0)
        {
            panel.Children.Add(Label(
                "Nothing could be listed — the network may be unavailable.",
                11.5, FontWeight.Normal, "BrTextSecondary"));
            return;
        }

        foreach (var (version, isPreRelease) in releases.Take(8))
            panel.Children.Add(BuildRow(
                version,
                isPreRelease ? "community build · pre-release" : "community build",
                isCurrent: false,
                action: "Download and use",
                onAction: () => DownloadCommunityAndSwap(version)));
    }

    private async void DownloadCommunityAndSwap(string version)
    {
        SetStatus($"Downloading the {version} community build…");
        try
        {
            var progress = new Progress<double>(fraction =>
                SetStatus($"Downloading the {version} community build… {fraction:P0}"));

            var entry = await _manager.DownloadCommunityBuildAsync(version, _slot.FileName, progress);
            _manager.SwapDll(_game, _slot, entry);
            Changed = true;
            Reload();
            SetStatus($"{_slot.Definition.Label} is now the {entry.Version} community build.");
        }
        catch (Exception ex)
        {
            Reload();
            SetStatus(ex.Message);
        }
    }

    /// <summary>
    /// What the DLSS Swapper archive holds.
    ///
    /// This is the only source that reaches builds the user never owned — past DLSS
    /// releases going back to 2018, and the FidelityFX runtimes, which AMD does not
    /// publish loose. Listed on open because that is the question the page exists to
    /// answer, but the index is cached for a day and nothing is fetched until a row is
    /// pressed.
    /// </summary>
    private async void RenderRepository()
    {
        var section = this.FindControl<StackPanel>("RepositorySection");
        var panel = this.FindControl<StackPanel>("RepositoryPanel");
        if (section is null || panel is null) return;

        section.IsVisible = ManagerService.TakesRepositoryBuilds(_slot.FileName)
            && _manager.SwapRepositoryDownloadsEnabled;
        if (!section.IsVisible) return;

        Text("RepositoryNoteText",
            $"From the {DllRepository.SourceName} project's archive of shipped builds — a "
            + "third-party mirror, not the vendor. Downloads come from their host on a press "
            + "and are checked against the hashes their index publishes before anything is "
            + "installed. Nothing is mirrored by this project.");

        panel.Children.Clear();
        panel.Children.Add(Label("Reading the archive's index…",
            11.5, FontWeight.Normal, "BrTextSecondary"));

        IReadOnlyList<RepositoryBuild> builds;
        try
        {
            builds = await _manager.RepositoryBuildsAsync(_slot.FileName);
        }
        catch (Exception ex)
        {
            panel.Children.Clear();
            panel.Children.Add(Label($"Could not read the archive's index: {ex.Message}",
                11.5, FontWeight.Normal, "BrTextSecondary"));
            return;
        }

        // The page can be re-rendered while this is in flight; bail if it has been.
        if (!ReferenceEquals(panel, this.FindControl<StackPanel>("RepositoryPanel"))) return;

        panel.Children.Clear();

        // Development builds last: they exist in the archive but are not what a player
        // wants unless they went looking.
        var offered = builds
            .Where(b => !b.InLibrary)
            .OrderBy(b => b.IsDevFile)
            .ThenBy(b => b.Version, VersionOrder.Descending)
            .Take(10)
            .ToList();

        if (offered.Count == 0)
        {
            panel.Children.Add(Label(
                builds.Count == 0
                    ? "The archive's index could not be read, or it holds nothing for this file."
                    : "You already hold every build the archive has for this file.",
                11.5, FontWeight.Normal, "BrTextSecondary"));
            return;
        }

        foreach (var build in offered)
            panel.Children.Add(BuildRow(
                DescribeRepositoryVersion(build),
                DescribeRepositorySource(build),
                isCurrent: IsInstalled(build.Version),
                action: "Download and use",
                onAction: () => DownloadRepositoryAndSwap(build)));
    }

    /// <summary>
    /// How an archived build is titled. The file version leads, because that is what
    /// the app reads off a real file and therefore what the "in the game now" row shows
    /// — but for the FidelityFX runtimes that number is an SDK build nobody quotes, so
    /// the vendor's own label rides alongside it.
    /// </summary>
    private static string DescribeRepositoryVersion(RepositoryBuild build) =>
        build.Label.Length > 0 && !build.Version.StartsWith(build.Label, StringComparison.Ordinal)
            ? $"{build.Version}  ·  {build.Label}"
            : build.Version;

    private static string DescribeRepositorySource(RepositoryBuild build)
    {
        var parts = new System.Collections.Generic.List<string> { DllRepository.SourceName };
        if (build.Provenance.Length > 0) parts.Add($"originally from {build.Provenance}");
        if (build.IsDevFile) parts.Add("development build");
        if (!build.SignatureValid) parts.Add("unsigned or signature not verified");
        if (DllRepositoryService.DescribeSize(build.ZipFileSize) is { Length: > 0 } size) parts.Add(size);
        return string.Join(" · ", parts);
    }

    private async void DownloadRepositoryAndSwap(RepositoryBuild build)
    {
        SetStatus($"Downloading {build.FileName} {build.Version}…");
        try
        {
            var progress = new Progress<double>(fraction =>
                SetStatus($"Downloading {build.FileName} {build.Version}… {fraction:P0}"));

            var entry = await _manager.DownloadRepositoryBuildAsync(build, progress);
            _manager.SwapDll(_game, _slot, entry);
            Changed = true;
            Reload();
            SetStatus($"{_slot.Definition.Label} is now {entry.Version}.");
        }
        catch (Exception ex)
        {
            Reload();
            SetStatus(ex.Message);
        }
    }

    /// <summary>
    /// What the vendor publishes. Listed on open, because knowing whether a newer build
    /// exists is the reason to be on this page — but nothing is fetched until a row is
    /// pressed, and these files run to tens of megabytes.
    /// </summary>
    private async void RenderVendor()
    {
        var panel = this.FindControl<StackPanel>("VendorPanel");
        if (panel is null) return;
        panel.Children.Clear();

        if (!_manager.SwapVendorDownloadsEnabled)
        {
            Text("VendorNoteText",
                "Turned off in Settings, under DLL swapper. Nothing is asked of the vendor.");
            return;
        }

        if (UpscalerManager.Core.Components.VendorDllSource.For(_slot.FileName) is not { } source)
        {
            Text("VendorNoteText",
                $"{_slot.FileName} is not published on its own by its vendor, so it cannot be "
                + "downloaded. OptiScaler's releases carry it, and those are listed above.");
            return;
        }

        Text("VendorNoteText",
            $"Downloaded straight from {source.Vendor}, never from a mirror this project runs. "
            + source.Licence);
        panel.Children.Add(Label($"Asking {source.Vendor} what is available…",
            11.5, FontWeight.Normal, "BrTextSecondary"));

        IReadOnlyList<VendorBuild> builds;
        try
        {
            builds = await _manager.VendorBuildsAsync(_slot.FileName);
        }
        catch (Exception ex)
        {
            panel.Children.Clear();
            panel.Children.Add(Label($"Could not reach {source.Vendor}: {ex.Message}",
                11.5, FontWeight.Normal, "BrTextSecondary"));
            return;
        }

        // The page can be re-rendered while this is in flight; bail if it has been.
        if (!ReferenceEquals(panel, this.FindControl<StackPanel>("VendorPanel"))) return;

        panel.Children.Clear();
        var offered = builds.Where(b => !b.InLibrary).Take(8).ToList();
        if (offered.Count == 0)
        {
            panel.Children.Add(Label(
                builds.Count == 0
                    ? $"{source.Vendor} published nothing that could be read, or the network is unavailable."
                    : "You already hold every build the vendor publishes.",
                11.5, FontWeight.Normal, "BrTextSecondary"));
            return;
        }

        foreach (var build in offered)
            panel.Children.Add(BuildRow(
                build.Version,
                $"from {build.Vendor}",
                isCurrent: IsInstalled(build.Version),
                action: "Download and use",
                onAction: () => DownloadAndSwap(build)));
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

    private async void DownloadAndSwap(VendorBuild build)
    {
        SetStatus($"Downloading {build.FileName} {build.Version} from {build.Vendor}… " +
                  "these are large files, so this can take a minute.");
        try
        {
            var progress = new Progress<double>(fraction =>
                SetStatus($"Downloading {build.FileName} {build.Version} from {build.Vendor}… " +
                          $"{fraction:P0}"));

            var entry = await _manager.DownloadVendorBuildAsync(build, progress);
            _manager.SwapDll(_game, _slot, entry);
            Changed = true;
            Reload();
            SetStatus($"{_slot.Definition.Label} is now {entry.Version}, kept in your library.");
        }
        catch (Exception ex)
        {
            Reload();
            SetStatus(ex.Message);
        }
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
