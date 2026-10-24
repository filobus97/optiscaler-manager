// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using UpscalerManager.App.Services;
using UpscalerManager.Core.Components;
using UpscalerManager.Core.Models;

namespace UpscalerManager.App.Views.Pages;

/// <summary>
/// FSR 4 without OptiScaler: pick the build, see every file that will be written and
/// which of the game's files each one replaces, then confirm.
///
/// A nested page rather than a dialog window, like every other screen here: extra
/// top-level windows are unreliable under gamescope, which is how this is used on a
/// Steam Deck.
/// </summary>
public partial class Fsr4SwapPage : UserControl, IHostedPage
{
    private readonly ManagerService _manager = null!;
    private readonly Game _game = null!;
    private readonly ManagerService.Fsr4SwapOverview _overview = null!;

    /// <summary>Bumped on each preview, so an answer that arrives late is dropped.</summary>
    private int _generation;
    private bool _ready;
    private bool _busy;

    public string Title { get; private set; } = "FSR 4 without OptiScaler";
    public Action<bool>? RequestClose { get; set; }

    // Parameterless ctor for the XAML previewer only.
    public Fsr4SwapPage() { InitializeComponent(); }

    public Fsr4SwapPage(ManagerService manager, Game game, ManagerService.Fsr4SwapOverview overview) : this()
    {
        _manager = manager;
        _game = game;
        _overview = overview;
        Title = $"FSR 4 without OptiScaler — {game.Name}";
        Load();
    }

    public void FocusFirst() =>
        this.FindControl<ComboBox>("BuildCombo")?.Focus(NavigationMethod.Directional);

    private async void Load()
    {
        var combo = this.FindControl<ComboBox>("BuildCombo")!;
        try
        {
            SetStatus("Looking up the builds on offer…");
            var options = await _manager.Fsr4SwapBuildOptionsAsync();

            combo.ItemsSource = options.Select(o =>
            {
                var item = new ComboBoxItem { Content = o.Label, Tag = o };
                if (o.Note is { } note) ToolTip.SetTip(item, note);
                return item;
            }).ToList();
            combo.SelectedIndex = 0;
            combo.SelectionChanged += async (_, _) => await RefreshPreviewAsync();
            _ready = true;
            await RefreshPreviewAsync();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private ManagerService.OmniBuildOption? Picked() =>
        (this.FindControl<ComboBox>("BuildCombo")!.SelectedItem as ComboBoxItem)?.Tag as ManagerService.OmniBuildOption;

    /// <summary>Works out what the picked build would do and shows it. Writes nothing.</summary>
    private async System.Threading.Tasks.Task RefreshPreviewAsync()
    {
        if (!_ready || Picked() is not { } build) return;

        var generation = ++_generation;
        var confirm = this.FindControl<Button>("ConfirmButton")!;
        confirm.IsEnabled = false;
        SetStatus("Reading the build…");

        ManagerService.Fsr4Preview preview;
        try
        {
            preview = await _manager.PreviewFsr4SwapAsync(
                _game, build, _overview.GameFiles, fetchBuilds: false, new Progress<string>(SetStatus));
        }
        catch (Exception ex)
        {
            preview = new ManagerService.Fsr4Preview(null, null, ex.Message);
        }
        if (generation != _generation) return;

        var files = this.FindControl<StackPanel>("FilesList")!;
        var notice = this.FindControl<StackPanel>("NoticeList")!;
        var box = this.FindControl<Border>("NoticeBox")!;
        files.Children.Clear();
        notice.Children.Clear();

        // What stands in the way now, said whatever the build: nothing is written until it clears.
        var refusal = _manager.Fsr4SwapRefusal(_game);
        var problem = refusal ?? preview.Problem ?? preview.Plan?.Refusal;
        if (problem is not null) notice.Children.Add(Line(problem, "BrError"));
        foreach (var warning in preview.Plan?.Warnings ?? Array.Empty<string>())
            notice.Children.Add(Line(warning, "BrWarning"));
        box.IsVisible = notice.Children.Count > 0;
        box.BorderBrush = Brush(problem is not null ? "BrError" : "BrWarning");

        if (this.FindControl<TextBlock>("SummaryText") is { } summary)
            summary.Text = string.Join(" ", preview.Plan?.Summary ?? Array.Empty<string>());

        foreach (var step in preview.Plan?.Steps ?? Array.Empty<Fsr4Step>())
            files.Children.Add(Mono(Fsr4Plan.Describe(step, _game.InstallPath)));

        if (preview.Sources is { } sources)
            ShowNote(build, sources);

        confirm.IsEnabled = problem is null && preview.Plan is { Steps.Count: > 0 };
        SetStatus(string.Empty);
    }

    private void ShowNote(ManagerService.OmniBuildOption build, Fsr4Sources sources)
    {
        var note = this.FindControl<TextBlock>("BuildNoteText")!;
        var parts = new System.Collections.Generic.List<string>();
        if (build.Note is { } n) parts.Add(n);
        if (sources.UpscalerPath.Length == 0) parts.Add("Downloaded when you confirm.");
        note.Text = string.Join(" ", parts);
        note.IsVisible = parts.Count > 0;
    }

    private async void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (_busy || Picked() is not { } build) return;

        _busy = true;
        var confirm = this.FindControl<Button>("ConfirmButton")!;
        var combo = this.FindControl<ComboBox>("BuildCombo")!;
        confirm.IsEnabled = false;
        combo.IsEnabled = false;
        try
        {
            SetStatus("Getting the build ready…");
            await _manager.ApplyFsr4SwapAsync(_game, build, new Progress<string>(SetStatus));
            RequestClose?.Invoke(true);
        }
        catch (Exception ex)
        {
            // The service has put the game back before it throws, and says so if it could not.
            _busy = false;
            combo.IsEnabled = true;
            await RefreshPreviewAsync();
            SetStatus(ex.Message);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => RequestClose?.Invoke(false);

    // ── Small helpers ───────────────────────────────────────────────────────

    private void SetStatus(string text)
    {
        if (this.FindControl<TextBlock>("StatusText") is { } block) block.Text = text;
    }

    private static IBrush Brush(string key) =>
        Avalonia.Application.Current?.FindResource(key) as IBrush ?? Brushes.Gray;

    private static TextBlock Line(string text, string brushKey) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush(brushKey),
    };

    private static TextBlock Mono(string text) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
        FontSize = 12.5,
        Foreground = Brush("BrTextPrimary"),
        TextWrapping = TextWrapping.Wrap,
    };
}
