// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
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
/// Every build of one swappable DLL this app can reach, newest first.
///
/// One list rather than a section per source. Where a build comes from is a caption on
/// its row, because it changes what pressing the row costs — a file already on the disk
/// is instant, an archived one is a download — but it is not how anybody chooses. They
/// choose by version.
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

    /// <summary>Archive builds, once the index has answered. Null until then.</summary>
    private IReadOnlyList<RepositoryBuild>? _archive;

    public string Title { get; private set; } = "Builds";
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
        LoadArchive();
    }

    public void FocusFirst()
    {
        // Whichever build is offered first, falling back to Import when there are none.
        var first = this.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.IsEffectivelyVisible && b.IsEnabled && b.Name is null);
        (first ?? this.FindControl<Button>("ImportButton"))?.Focus(NavigationMethod.Directional);
    }

    private IStorageProvider StorageProvider =>
        TopLevel.GetTopLevel(this)?.StorageProvider
        ?? throw new InvalidOperationException("No storage provider is available.");

    // ── One build, from wherever ────────────────────────────────────────────

    /// <param name="Version">The raw version, which orders the list and dedupes it.</param>
    /// <param name="Display">How the version reads, which for FSR is not the same thing.</param>
    /// <param name="Source">Where it is, as the row's caption.</param>
    /// <param name="Action">The button's label, or null for a row with nothing to press.</param>
    /// <param name="Refused">
    /// The generation of a build the guard will not install here, or NotFidelityFx when
    /// there is nothing in the way. The row tags it; the page explains it once.
    /// </param>
    private sealed record Candidate(
        string Version,
        string Display,
        string Source,
        string? Action,
        Action? Run,
        bool IsCurrent = false,
        FidelityFxRole Refused = FidelityFxRole.NotFidelityFx);

    // ── Rendering ───────────────────────────────────────────────────────────

    private void Render()
    {
        var copies = _slot.CopyCount > 1 ? $"  •  {_slot.CopyCount} copies" : string.Empty;
        Text("SubtitleText", $"{_slot.FileName}  •  in the game now: {_slot.VersionText}{copies}");

        if (this.FindControl<Border>("WarningBox") is { } warning)
        {
            warning.IsVisible = !_slot.Verdict.Allowed;
            Text("WarningText", _slot.Verdict.Reason);
        }

        var list = this.FindControl<StackPanel>("BuildList");
        if (list is null) return;

        list.Children.Clear();
        var candidates = Candidates();
        foreach (var candidate in candidates)
            list.Children.Add(Row(candidate));

        Text("ListNoteText", Note(candidates.Count));
        ShowGuardNote(candidates);
    }

    /// <summary>
    /// Every build worth listing, newest first and one row per version.
    ///
    /// Deduplicated by version with the cheapest source winning: the game's own file,
    /// then the library, then another game, then the archive. Listing one version four
    /// times because four places have it is noise.
    /// </summary>
    private List<Candidate> Candidates()
    {
        var all = new List<Candidate>();

        if (_slot.Version is { Length: > 0 }) all.Add(Current());
        all.AddRange(_manager.LibraryBuilds(_slot.FileName).Select(Held));
        all.AddRange(_manager.HarvestableBuilds(_slot.FileName, _game)
            .Concat(_manager.OptiScalerBuilds(_slot.FileName))
            .Where(h => !h.InLibrary)
            .Select(Elsewhere));
        if (_archive is { } archived) all.AddRange(archived.Where(b => !b.InLibrary).Select(Archived));

        return all
            .GroupBy(c => c.Version, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(c => c.Version, VersionOrder.Descending)
            .ToList();
    }

    private Candidate Current()
    {
        var swapped = _slot.Swapped;
        var source = swapped is null
            ? "in the game now, and the build it shipped with"
            : $"in the game now, put there by this app — {swapped.SourceLabel}";

        // Revert restores what was backed up, so it is only offered for our own swap.
        return new Candidate(
            _slot.Version!,
            _slot.VersionText,
            source,
            swapped is null ? null : RevertLabel(swapped),
            swapped is null ? null : () => Revert(swapped),
            IsCurrent: true);
    }

    private static string RevertLabel(SwappedFile swapped) =>
        swapped.ExistedBefore ? "Put the game's own build back" : "Remove it";

    private Candidate Held(LibraryDll build) => new(
        build.Version,
        DllSwapService.DescribeBuild(_slot.FileName, build.Version, build.Path),
        build.SourceLabel,
        "Use this build",
        () => Swap(build),
        Refused: RefusedRole(DllSwapService.RoleOf(_slot.FileName, build.Path)));

    private Candidate Elsewhere(HarvestableDll candidate) => new(
        candidate.Version,
        DllSwapService.DescribeBuild(_slot.FileName, candidate.Version, candidate.Path),
        $"in {candidate.GameName}",
        "Use this build",
        () => HarvestAndSwap(candidate),
        Refused: RefusedRole(DllSwapService.RoleOf(_slot.FileName, candidate.Path)));

    private Candidate Archived(RepositoryBuild build) => new(
        build.Version,
        DllRepositoryService.DescribeBuild(build),
        ArchiveSource(build),
        "Download and use",
        () => DownloadAndSwap(build),
        // No file to read yet, so the generation is judged from the version the index
        // publishes. Every FidelityFX build the archive holds is an SDK 1 runtime.
        Refused: RefusedRole(FidelityFxLayout.Identify(build.FileName, build.Version, null)));

    /// <summary>
    /// The candidate's role when the guard refuses it, and NotFidelityFx when it does
    /// not — so a row only has to ask "was I refused, and as what?".
    /// </summary>
    private FidelityFxRole RefusedRole(FidelityFxRole candidate) =>
        FidelityFxLayout.Interchangeable(DllSwapService.CurrentRole(_slot), candidate)
            ? FidelityFxRole.NotFidelityFx
            : candidate;

    private static string ArchiveSource(RepositoryBuild build)
    {
        var parts = new List<string> { $"in the {DllRepository.SourceName} archive" };
        if (build.Provenance.Length > 0) parts.Add($"originally from {build.Provenance}");
        if (build.IsDevFile) parts.Add("development build");
        if (!build.SignatureValid) parts.Add("unsigned");
        if (DllRepositoryService.DescribeSize(build.ZipFileSize) is { Length: > 0 } size) parts.Add(size);
        return string.Join("  ·  ", parts);
    }

    private string Note(int shown)
    {
        if (_archive is null && ManagerService.TakesRepositoryBuilds(_slot.FileName)
            && _manager.SwapRepositoryDownloadsEnabled)
            return $"Reading the {DllRepository.SourceName} index…";

        if (shown > 1) return string.Empty;

        return _manager.SwapRepositoryDownloadsEnabled
            ? "No other build was found in your games, in the releases this app has downloaded, "
              + "or in the archive. Import one to add it."
            : "Archive downloads are off in Settings, so only builds already on your disk are "
              + "listed. Import one to add it.";
    }

    /// <summary>
    /// The generation rule, said once for however many rows it refuses. Repeating it on
    /// each row is how this page grew to nine copies of the same two lines.
    /// </summary>
    private void ShowGuardNote(List<Candidate> candidates)
    {
        if (this.FindControl<TextBlock>("GuardNoteText") is not { } note) return;

        var refused = candidates
            .Where(c => c.Refused != FidelityFxRole.NotFidelityFx)
            .GroupBy(c => c.Refused)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        note.IsVisible = refused is not null;
        if (refused is null) return;

        var count = refused.Count();
        note.Text = $"{count} {(count == 1 ? "build is" : "builds are")} "
                    + $"{FidelityFxLayout.Generation(refused.Key)} and cannot be installed here: this "
                    + $"game uses {FidelityFxLayout.Noun(DllSwapService.CurrentRole(_slot))}, and AMD "
                    + "split the runtime in SDK 2.0.0 so the two generations cannot stand in for "
                    + "each other.";
    }

    private Control Row(Candidate candidate)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        line.Children.Add(new TextBlock
        {
            Text = candidate.Display,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (candidate.IsCurrent) line.Children.Add(Chip("in the game"));

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(line);
        var refused = candidate.Refused != FidelityFxRole.NotFidelityFx;
        text.Children.Add(new TextBlock
        {
            Text = refused
                ? $"{candidate.Source}  ·  {FidelityFxLayout.Generation(candidate.Refused)} build"
                : candidate.Source,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(refused ? "BrWarning" : "BrTextSecondary"),
        });
        grid.Children.Add(text);

        // A row with nothing to press is the game's own build, or one the guard refused.
        // Either way the caption says why, so there is no disabled button to explain.
        if (candidate.Action is { } label && candidate.Run is { } run && !refused
            && (_slot.Verdict.Allowed || candidate.IsCurrent))
        {
            var button = new Button { Content = label, VerticalAlignment = VerticalAlignment.Center };
            button.Click += (_, _) => run();
            Grid.SetColumn(button, 1);
            grid.Children.Add(button);
        }

        return new Border { Classes = { "Row" }, Child = grid };
    }

    // ── The archive ─────────────────────────────────────────────────────────

    /// <summary>
    /// Asks the archive index what it holds. Listed on open because that is the question
    /// the page exists to answer, but the index is cached for a day and nothing is
    /// fetched until a row is pressed.
    /// </summary>
    private async void LoadArchive()
    {
        if (!ManagerService.TakesRepositoryBuilds(_slot.FileName)) return;
        if (!_manager.SwapRepositoryDownloadsEnabled) return;

        try { _archive = await _manager.RepositoryBuildsAsync(_slot.FileName); }
        catch (Exception ex)
        {
            _archive = Array.Empty<RepositoryBuild>();
            SetStatus($"Could not read the {DllRepository.SourceName} index: {ex.Message}");
        }

        Render();
    }

    // ── Actions ─────────────────────────────────────────────────────────────

    private void Swap(LibraryDll build) =>
        Act($"Installing {build.FileName} {build.Version}…", () =>
        {
            _manager.SwapDll(_game, _slot, build);
            return $"{_slot.Definition.Label} is now {build.Version}.";
        });

    private void HarvestAndSwap(HarvestableDll candidate) =>
        Act($"Copying {candidate.FileName} {candidate.Version} from {candidate.GameName}…", () =>
        {
            var build = _manager.HarvestBuild(candidate);
            _manager.SwapDll(_game, _slot, build);
            return $"{_slot.Definition.Label} is now {build.Version}.";
        });

    private async void DownloadAndSwap(RepositoryBuild build)
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

    private void Revert(SwappedFile swapped) =>
        Act("Restoring the game's own build…", () =>
        {
            _manager.RevertSwap(_game, swapped, force: false);
            return $"{_slot.Definition.Label} is back to the game's own build.";
        });

    /// <summary>
    /// Runs one file operation, reports what happened, and re-reads the game afterwards.
    ///
    /// why: every one of these rewrites a file in the player's game folder, so a failure
    /// has to be visible rather than swallowed — and the page has to re-read from disk
    /// after, because the version shown is what decides whether the next press is a
    /// no-op.
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
                Title = $"Pick a {_slot.FileName}, or a zip holding one",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("DLLs and zips") { Patterns = new[] { "*.dll", "*.zip" } },
                },
            });

            var paths = files
                .Select(f => f.TryGetLocalPath())
                .Where(p => p is { Length: > 0 })
                .Select(p => p!)
                .ToList();
            if (paths.Count == 0) return;

            SetStatus(paths.Count == 1
                ? $"Importing {System.IO.Path.GetFileName(paths[0])}…"
                : $"Importing {paths.Count} files…");

            var results = _manager.ImportSwappable(paths);
            Changed = results.Any(r => r.Added.Count > 0);
            Reload();
            SetStatus(DescribeImport(results));
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    /// <summary>
    /// What an import did, in one line. Builds of another DLL are still worth keeping —
    /// they just belong to a different row, so say so rather than looking like nothing
    /// happened.
    /// </summary>
    private string DescribeImport(IReadOnlyList<ManagerService.ImportResult> results)
    {
        var added = results.SelectMany(r => r.Added).ToList();
        var failed = results.Where(r => r.Error is not null).ToList();

        var mine = added.Count(b => b.FileName.Equals(_slot.FileName, StringComparison.OrdinalIgnoreCase));
        var others = added.Count - mine;

        var parts = new List<string>();
        if (mine > 0) parts.Add(mine == 1 ? "Imported 1 build." : $"Imported {mine} builds.");
        if (others > 0) parts.Add($"{others} more belong to other files, and are listed on their rows.");
        if (failed.Count > 0)
            parts.Add(failed.Count == 1
                ? $"{failed[0].Source}: {failed[0].Error}"
                : $"{failed.Count} files could not be imported.");

        return parts.Count > 0 ? string.Join("  ", parts) : "Nothing was imported.";
    }

    private void OnClose(object? sender, RoutedEventArgs e) => RequestClose?.Invoke(Changed);

    // ── Small helpers ───────────────────────────────────────────────────────

    private void Text(string name, string? value)
    {
        if (this.FindControl<TextBlock>(name) is { } block) block.Text = value;
    }

    private void SetStatus(string text) => Text("StatusText", text);

    private static Control Chip(string text) => new Border
    {
        Background = Brush("BrBgElevated"),
        CornerRadius = new Avalonia.CornerRadius(4),
        Padding = new Avalonia.Thickness(6, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = Brush("BrTextSecondary") },
    };

    private static IBrush? Brush(string key) =>
        Avalonia.Application.Current?.FindResource(key) as IBrush;
}
