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
    public Fsr4Backend SelectedBackend { get; private set; } = Fsr4Backend.LatestSdkFromSource;

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
        var fromSource = this.FindControl<RadioButton>("RbFromSource")!;
        var customSdk = this.FindControl<RadioButton>("RbCustomSdk")!;
        var customDll = this.FindControl<RadioButton>("RbCustomDll")!;
        var none = this.FindControl<RadioButton>("RbNone")!;

        customSdk.IsEnabled = _manager.HasCustomFsrSdk;
        customDll.IsEnabled = _manager.HasCustomFsr4Dll;
        if (!customSdk.IsEnabled) customSdk.Content = "Custom FSR SDK — none imported (Settings)";
        if (!customDll.IsEnabled) customDll.Content = "Custom amdxcffx64.dll — none imported (Settings)";

        // Default: prefer an imported custom backend, else the from-source SDK.
        if (_manager.HasCustomFsrSdk) customSdk.IsChecked = true;
        else if (_manager.HasCustomFsr4Dll) customDll.IsChecked = true;
        else fromSource.IsChecked = true;

        fromSource.IsCheckedChanged += OnOptionChanged;
        customSdk.IsCheckedChanged += OnOptionChanged;
        customDll.IsCheckedChanged += OnOptionChanged;
        none.IsCheckedChanged += OnOptionChanged;
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
        if (this.FindControl<RadioButton>("RbCustomSdk")!.IsChecked == true) return Fsr4Backend.CustomSdk;
        if (this.FindControl<RadioButton>("RbCustomDll")!.IsChecked == true) return Fsr4Backend.CustomDll;
        if (this.FindControl<RadioButton>("RbNone")!.IsChecked == true) return Fsr4Backend.None;
        return Fsr4Backend.LatestSdkFromSource;
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
        SelectedProfile = CurrentProfile();
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    /// <summary>Shows the dialog modally and returns whether the user confirmed.</summary>
    public Task<bool> ShowDialogFor(Window owner) => ShowDialog<bool>(owner);
}
