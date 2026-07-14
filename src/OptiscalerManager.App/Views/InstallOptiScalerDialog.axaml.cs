// OptiScaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using OptiscalerManager.App.Services;
using OptiscalerManager.Core.Components;
using OptiscalerManager.Core.Models;

namespace OptiscalerManager.App.Views;

public partial class InstallOptiScalerDialog : Window
{
    private readonly ManagerService _manager = null!;
    private readonly Game _game = null!;
    private bool _ready;

    /// <summary>The FSR 4 backend the user confirmed.</summary>
    public Fsr4Backend SelectedBackend { get; private set; } = Fsr4Backend.LatestAmdSdk;

    /// <summary>The INT8 community build version the user confirmed (null unless INT8 chosen).</summary>
    public string? SelectedInt8Version { get; private set; }

    /// <summary>The OptiScaler.ini profile the user confirmed (built-in default = OptiScaler's own config).</summary>
    public OptiScalerProfile? SelectedProfile { get; private set; }

    // Parameterless ctor for the XAML previewer only.
    public InstallOptiScalerDialog() { InitializeComponent(); }

    public InstallOptiScalerDialog(ManagerService manager, Game game) : this()
    {
        _manager = manager;
        _game = game;

        var title = this.FindControl<TextBlock>("TitleText");
        if (title is not null) title.Text = $"Install OptiScaler — {game.Name}";

        SetupBackendOptions();
        SetupProfiles();

        _ready = true;
        UpdatePreview();
    }

    private void SetupBackendOptions()
    {
        var amdSdk = this.FindControl<RadioButton>("RbAmdSdk")!;
        var int8 = this.FindControl<RadioButton>("RbInt8")!;
        var customSdk = this.FindControl<RadioButton>("RbCustomSdk")!;
        var customDll = this.FindControl<RadioButton>("RbCustomDll")!;
        var none = this.FindControl<RadioButton>("RbNone")!;

        customSdk.IsEnabled = _manager.HasCustomFsrSdk;
        customDll.IsEnabled = _manager.HasCustomFsr4Dll;
        if (!customSdk.IsEnabled) customSdk.Content = "Custom FSR SDK — none imported (Settings)";
        if (!customDll.IsEnabled) customDll.Content = "Custom amdxcffx64.dll — none imported (Settings)";

        // Default: prefer an imported custom backend, else the AMD SDK.
        if (_manager.HasCustomFsrSdk) customSdk.IsChecked = true;
        else if (_manager.HasCustomFsr4Dll) customDll.IsChecked = true;
        else amdSdk.IsChecked = true;

        amdSdk.IsCheckedChanged += OnOptionChanged;
        int8.IsCheckedChanged += OnInt8CheckedChanged;
        int8.IsCheckedChanged += OnOptionChanged;
        customSdk.IsCheckedChanged += OnOptionChanged;
        customDll.IsCheckedChanged += OnOptionChanged;
        none.IsCheckedChanged += OnOptionChanged;
    }

    // Reveal and lazily populate the INT8 version list when INT8 is selected.
    private bool _int8Loaded;
    private async void OnInt8CheckedChanged(object? sender, RoutedEventArgs e)
    {
        var panel = this.FindControl<StackPanel>("Int8VersionPanel")!;
        var int8 = this.FindControl<RadioButton>("RbInt8")!;
        panel.IsVisible = int8.IsChecked == true;
        if (int8.IsChecked != true || _int8Loaded) return;

        _int8Loaded = true;
        var combo = this.FindControl<ComboBox>("Int8VersionCombo")!;
        combo.ItemsSource = new[] { "Loading…" };
        combo.SelectedIndex = 0;
        var versions = await _manager.GetInt8VersionsAsync();
        combo.ItemsSource = versions.Count > 0 ? versions : new[] { "(none available)" };
        combo.SelectedIndex = 0;
        combo.SelectionChanged += OnOptionChanged;
        UpdatePreview();
    }

    private void SetupProfiles()
    {
        var combo = this.FindControl<ComboBox>("ProfileCombo")!;
        var items = new System.Collections.Generic.List<ComboBoxItem>();
        foreach (var p in _manager.GetIniProfiles())
        {
            var label = p.IsBuiltIn ? "OptiScaler default (no custom .ini)" : p.Name;
            items.Add(new ComboBoxItem { Content = label, Tag = p });
        }
        combo.ItemsSource = items;
        combo.SelectedIndex = 0;
        combo.SelectionChanged += OnOptionChanged;
    }

    private Fsr4Backend CurrentBackend()
    {
        if (this.FindControl<RadioButton>("RbInt8")!.IsChecked == true) return Fsr4Backend.Int8Community;
        if (this.FindControl<RadioButton>("RbCustomSdk")!.IsChecked == true) return Fsr4Backend.CustomSdk;
        if (this.FindControl<RadioButton>("RbCustomDll")!.IsChecked == true) return Fsr4Backend.CustomDll;
        if (this.FindControl<RadioButton>("RbNone")!.IsChecked == true) return Fsr4Backend.None;
        return Fsr4Backend.LatestAmdSdk;
    }

    private string? CurrentInt8Version()
    {
        var combo = this.FindControl<ComboBox>("Int8VersionCombo")!;
        var v = combo.SelectedItem as string;
        if (string.IsNullOrEmpty(v) || v == "Loading…" || v == "(none available)") return null;
        return v;
    }

    private OptiScalerProfile? CurrentProfile()
    {
        var combo = this.FindControl<ComboBox>("ProfileCombo")!;
        return (combo.SelectedItem as ComboBoxItem)?.Tag as OptiScalerProfile;
    }

    private void OnOptionChanged(object? sender, RoutedEventArgs e) => UpdatePreview();
    private void OnOptionChanged(object? sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (!_ready) return;
        var preview = _manager.BuildInstallPreview(_game, CurrentBackend(), CurrentProfile());

        var files = this.FindControl<StackPanel>("FilesList")!;
        var ini = this.FindControl<StackPanel>("IniList")!;
        files.Children.Clear();
        ini.Children.Clear();

        foreach (var f in preview.Files) files.Children.Add(Mono(f));
        if (preview.IniKeys.Count == 0)
            ini.Children.Add(Mono("(no ini changes)", FontWeight.Normal, "#8A8AAA"));
        foreach (var k in preview.IniKeys) ini.Children.Add(Mono(k.ToString()));

        var conflictBox = this.FindControl<Border>("ConflictBox")!;
        var conflictList = this.FindControl<StackPanel>("ConflictList")!;
        conflictList.Children.Clear();
        conflictBox.IsVisible = preview.Conflicts.Count > 0;
        foreach (var c in preview.Conflicts)
            conflictList.Children.Add(Mono("⚠ " + c, FontWeight.Normal, "#E06060"));
    }

    private static TextBlock Mono(string text, FontWeight weight = FontWeight.Normal, string color = "#E4E4EF")
        => new()
        {
            Text = text,
            FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
            FontSize = 12,
            FontWeight = weight,
            Foreground = new SolidColorBrush(Color.Parse(color)),
            TextWrapping = TextWrapping.Wrap,
        };

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        SelectedBackend = CurrentBackend();
        SelectedInt8Version = SelectedBackend == Fsr4Backend.Int8Community ? CurrentInt8Version() : null;
        SelectedProfile = CurrentProfile();
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    /// <summary>Shows the dialog modally and returns whether the user confirmed.</summary>
    public Task<bool> ShowDialogFor(Window owner) => ShowDialog<bool>(owner);
}
