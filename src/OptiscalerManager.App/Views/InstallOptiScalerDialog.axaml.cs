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

    /// <summary>The backend the user confirmed.</summary>
    public Fsr4Backend SelectedBackend { get; private set; } = Fsr4Backend.Default;

    /// <summary>Whether the Manager should select FSR 4 (UpscalerIndex=0) vs leave it auto.</summary>
    public bool SelectFsr4 { get; private set; } = true;

    /// <summary>The INT8 community build version the user confirmed (null unless INT8 chosen).</summary>
    public string? SelectedInt8Version { get; private set; }

    /// <summary>The OptiScaler.ini profile the user confirmed (built-in default = OptiScaler's own config).</summary>
    public OptiScalerProfile? SelectedProfile { get; private set; }

    /// <summary>Install the fakenvapi add-on (nvapi64.dll + fakenvapi.ini).</summary>
    public bool AddFakenvapi { get; private set; }

    /// <summary>Install Nukem's DLSSG-to-FSR3 mod (imported DLL + FGInput=nukems).</summary>
    public bool AddNukemFg { get; private set; }

    /// <summary>Force [Spoofing] Dxgi=true for this game.</summary>
    public bool SpoofNvidia { get; private set; }

    /// <summary>Force [FSR] Fsr4ForceEnableInt8=true.</summary>
    public bool ForceInt8 { get; private set; }

    /// <summary>Force [FSR] Fsr4EnableWatermark=true.</summary>
    public bool Fsr4Watermark { get; private set; }

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

        // INT8 is the default backend — reveal and load its version list on open.
        if (this.FindControl<RadioButton>("RbInt8")!.IsChecked == true)
            OnInt8CheckedChanged(this, new RoutedEventArgs());
    }

    private void SetupBackendOptions()
    {
        var amdSdk = this.FindControl<RadioButton>("RbAmdSdk")!;
        var int8 = this.FindControl<RadioButton>("RbInt8")!;
        var customMerged = this.FindControl<RadioButton>("RbCustomMerged")!;
        var def = this.FindControl<RadioButton>("RbDefault")!;

        customMerged.IsEnabled = _manager.HasCustomDlls;
        if (!customMerged.IsEnabled) customMerged.Content = "Custom DLLs + latest AMD SDK — none imported (Settings)";

        // Pre-select Default: OptiScaler's own release already bundles a working
        // FSR 4.1 upscaler, so the zero-decision path delivers FSR 4 out of the box.
        def.IsChecked = true;

        amdSdk.IsCheckedChanged += OnOptionChanged;
        int8.IsCheckedChanged += OnInt8CheckedChanged;
        int8.IsCheckedChanged += OnOptionChanged;
        customMerged.IsCheckedChanged += OnOptionChanged;
        def.IsCheckedChanged += OnOptionChanged;

        // Step 2 radios drive UpscalerIndex in the preview.
        this.FindControl<RadioButton>("RbSelectNow")!.IsCheckedChanged += OnOptionChanged;
        this.FindControl<RadioButton>("RbSelectInGame")!.IsCheckedChanged += OnOptionChanged;

        // Step 2 toggles + Step 3 add-ons all feed the live preview.
        this.FindControl<CheckBox>("ChkForceInt8")!.IsCheckedChanged += OnOptionChanged;
        this.FindControl<CheckBox>("ChkWatermark")!.IsCheckedChanged += OnOptionChanged;
        this.FindControl<CheckBox>("ChkFakenvapi")!.IsCheckedChanged += OnOptionChanged;
        this.FindControl<CheckBox>("ChkSpoofNvidia")!.IsCheckedChanged += OnOptionChanged;

        var nukem = this.FindControl<CheckBox>("ChkNukemFg")!;
        if (!_manager.IsNukemFgCached)
        {
            nukem.IsEnabled = false;
            nukem.Content = "Nukem DLSSG-to-FSR3 — DLL not imported (Settings)";
        }
        nukem.IsCheckedChanged += (_, e) =>
        {
            // Nukem's mod needs fakenvapi on AMD/Intel — selecting it pulls fakenvapi in.
            if (nukem.IsChecked == true)
                this.FindControl<CheckBox>("ChkFakenvapi")!.IsChecked = true;
            OnOptionChanged(nukem, e);
        };
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
        if (this.FindControl<RadioButton>("RbCustomMerged")!.IsChecked == true) return Fsr4Backend.CustomMerged;
        if (this.FindControl<RadioButton>("RbDefault")!.IsChecked == true) return Fsr4Backend.Default;
        return Fsr4Backend.LatestAmdSdk;
    }

    private bool CurrentSelectFsr4()
        => this.FindControl<RadioButton>("RbSelectInGame")!.IsChecked != true;

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

    private bool IsChecked(string name) => this.FindControl<CheckBox>(name)!.IsChecked == true;

    private void UpdatePreview()
    {
        if (!_ready) return;
        var preview = _manager.BuildInstallPreview(_game, CurrentBackend(), CurrentSelectFsr4(),
            addFakenvapi: IsChecked("ChkFakenvapi"), addNukemFg: IsChecked("ChkNukemFg"),
            spoofNvidia: IsChecked("ChkSpoofNvidia"), forceInt8: IsChecked("ChkForceInt8"),
            fsr4Watermark: IsChecked("ChkWatermark"));

        var files = this.FindControl<StackPanel>("FilesList")!;
        var ini = this.FindControl<StackPanel>("IniList")!;
        files.Children.Clear();
        ini.Children.Clear();

        foreach (var f in preview.Files) files.Children.Add(Mono(f));
        if (preview.IniKeys.Count == 0)
            ini.Children.Add(Mono("(no ini changes)", FontWeight.Normal, "#8A8AAA"));
        foreach (var k in preview.IniKeys) ini.Children.Add(Mono(k.ToString()));

        // Make clear the Manager only overrides these keys; the rest comes from the ini.
        var profile = CurrentProfile();
        var iniName = (profile is null || profile.IsBuiltIn) ? "OptiScaler's default .ini" : $"your \"{profile.Name}\" profile";
        ini.Children.Add(Mono($"…everything else comes from {iniName} (left untouched).", FontWeight.Normal, "#8A8AAA"));

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
        SelectFsr4 = CurrentSelectFsr4();
        SelectedInt8Version = SelectedBackend == Fsr4Backend.Int8Community ? CurrentInt8Version() : null;
        SelectedProfile = CurrentProfile();
        AddFakenvapi = IsChecked("ChkFakenvapi");
        AddNukemFg = IsChecked("ChkNukemFg");
        SpoofNvidia = IsChecked("ChkSpoofNvidia");
        ForceInt8 = IsChecked("ChkForceInt8");
        Fsr4Watermark = IsChecked("ChkWatermark");
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    /// <summary>Shows the dialog modally and returns whether the user confirmed.</summary>
    public Task<bool> ShowDialogFor(Window owner) => ShowDialog<bool>(owner);
}
