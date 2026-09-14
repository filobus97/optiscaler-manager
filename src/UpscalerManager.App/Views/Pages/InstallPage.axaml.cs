// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using UpscalerManager.App.Services;
using UpscalerManager.Core.Components;
using UpscalerManager.Core.Models;
using UpscalerManager.Core.Services;

namespace UpscalerManager.App.Views.Pages;

public partial class InstallPage : UserControl, IHostedPage
{
    private readonly ManagerService _manager = null!;
    private readonly Game _game = null!;
    private bool _ready;

    /// <summary>The backend the user confirmed.</summary>
    public Fsr4Backend SelectedBackend { get; private set; } = Fsr4Backend.Default;

    /// <summary>Which upscaler the user chose for OptiScaler to run, and which FSR provider.</summary>
    public UpscalerSelection SelectedUpscaler { get; private set; } = UpscalerSelection.NewestFsr;

    /// <summary>The INT8 community build version the user confirmed (null unless INT8 chosen).</summary>
    public string? SelectedInt8Version { get; private set; }

    /// <summary>The OptiScaler.ini profile the user confirmed (built-in default = OptiScaler's own config).</summary>
    public OptiScalerProfile? SelectedProfile { get; private set; }

    /// <summary>Install the fakenvapi add-on (nvapi64.dll + fakenvapi.ini).</summary>
    public bool AddFakenvapi { get; private set; }

    /// <summary>Install Nukem's DLSSG-to-FSR3 mod (imported DLL + FGInput=nukems).</summary>
    public bool AddNukemFg { get; private set; }

    /// <summary>What the user chose for the Nvidia override.</summary>
    public SpoofMethod SelectedSpoofMethod { get; private set; } = SpoofMethod.Default;

    /// <summary>The OptiScaler release version to install (null = latest).</summary>
    public string? SelectedOptiScalerVersion { get; private set; }

    /// <summary>Force [FSR] Fsr4ForceEnableInt8=true.</summary>
    public bool ForceInt8 { get; private set; }

    /// <summary>Force [FSR] Fsr4EnableWatermark=true.</summary>
    public bool Fsr4Watermark { get; private set; }

    // Parameterless ctor for the XAML previewer only.
    public InstallPage() { InitializeComponent(); }

    public string Title => $"Install OptiScaler — {TitleFor}";

    /// <summary>Game name shown in the page header.</summary>
    public string TitleFor { get; set; } = string.Empty;
    public Action<bool>? RequestClose { get; set; }

    /// <summary>Starts on the first of the two decisions this screen really asks.</summary>
    public void FocusFirst() =>
        this.FindControl<ComboBox>("OptiScalerVersionCombo")?.Focus(NavigationMethod.Directional);

    public InstallPage(ManagerService manager, Game game) : this()
    {
        _manager = manager;
        _game = game;

        SetupBackendOptions();
        SetupProfiles();
        SetupUpscalerPickers();
        SetupOptiScalerVersions();

        _ready = true;
        UpdatePreview();

        // The default option is pre-selected, so the INT8 version list stays hidden
        // until it is picked. Kept as a guard in case that default ever changes.
        if (this.FindControl<RadioButton>("RbInt8")!.IsChecked == true)
            OnInt8CheckedChanged(this, new RoutedEventArgs());
    }

    /// <summary>Shows or hides the advanced options, and turns the chevron over.</summary>
    private void OnAdvancedToggled(object? sender, RoutedEventArgs e)
    {
        var open = this.FindControl<ToggleButton>("AdvancedToggle")?.IsChecked == true;
        if (this.FindControl<Border>("AdvancedPanel") is { } panel) panel.IsVisible = open;
        if (this.FindControl<TextBlock>("AdvancedChevron") is { } chevron)
            chevron.Text = open ? "\u2303" : "\u2304";
    }

    private void SetupBackendOptions()
    {
        var int8 = this.FindControl<RadioButton>("RbInt8")!;
        var customMerged = this.FindControl<RadioButton>("RbCustomMerged")!;
        var def = this.FindControl<RadioButton>("RbDefault")!;

        customMerged.IsEnabled = _manager.HasCustomDlls;
        if (!customMerged.IsEnabled) customMerged.Content = "Custom DLLs — none imported (Settings)";

        // Pre-selected, and under Advanced: OptiScaler's own release already bundles a
        // working FSR 4.x upscaler, so the zero-decision path delivers FSR 4 as it is.
        def.IsChecked = true;

        int8.IsCheckedChanged += OnInt8CheckedChanged;
        int8.IsCheckedChanged += OnOptionChanged;
        customMerged.IsCheckedChanged += OnOptionChanged;
        def.IsCheckedChanged += OnOptionChanged;

        // Step 2 radios drive the [Upscalers] selection in the preview.

        // Step 2 toggles + Step 3 add-ons all feed the live preview.
        this.FindControl<CheckBox>("ChkForceInt8")!.IsCheckedChanged += OnOptionChanged;
        this.FindControl<CheckBox>("ChkWatermark")!.IsCheckedChanged += OnOptionChanged;
        this.FindControl<CheckBox>("ChkFakenvapi")!.IsCheckedChanged += OnOptionChanged;

        // Nvidia override. Four outcomes in one list, because the old checkbox could
        // only ever write true: left unticked it wrote nothing, and OptiScaler's own
        // default then spoofed anyway on AMD and Intel — so the screen said off while
        // the game was told it had an RTX 4090.
        var spoofCombo = this.FindControl<ComboBox>("SpoofCombo")!;
        spoofCombo.ItemsSource = new[]
        {
            "Let OptiScaler decide",
            "Force it on — report an RTX 4090",
            "Force it off",
            "Patch the game instead — OptiPatcher",
        };
        spoofCombo.SelectedIndex = 0;
        spoofCombo.SelectionChanged += (s, e) =>
        {
            UpdateSpoofNote();
            OnOptionChanged(s, e);
        };
        UpdateSpoofNote();

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
        combo.ItemsSource = new System.Collections.Generic.List<ComboBoxItem> { new() { Content = "Loading…" } };
        combo.SelectedIndex = 0;
        var releases = await _manager.GetInt8ReleasesAsync();
        combo.ItemsSource = releases.Count > 0
            ? releases.Select(r => new ComboBoxItem
              {
                  // Mark the ones upstream has not declared stable — the list is every
                  // release the repo publishes, pre-releases included.
                  Content = r.IsPreRelease ? $"{r.Version}  •  pre-release" : r.Version,
                  Tag = r.Version,
              }).ToList()
            : new System.Collections.Generic.List<ComboBoxItem> { new() { Content = "(none available)" } };
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

    // Lazily populate the OptiScaler version list; "Latest" stays selected until the
    // fetch completes so the dialog is usable immediately (and offline). Stable
    // releases and pre-releases are grouped under non-selectable separators.
    private async void SetupOptiScalerVersions()
    {
        var combo = this.FindControl<ComboBox>("OptiScalerVersionCombo")!;
        ComboBoxItem Selectable(string label, string? version) => new() { Content = label, Tag = version };
        ComboBoxItem Separator(string label) => new()
        {
            Content = label,
            IsEnabled = false,
            FontSize = 11,
            Foreground = Brush("BrTextSecondary"),
        };

        combo.ItemsSource = new[] { Selectable("Latest stable (recommended)", null) };
        combo.SelectedIndex = 0;

        var versions = await _manager.GetOptiScalerVersionsAsync();
        var stables = versions.Where(v => !_manager.IsBetaOptiScalerVersion(v)).ToList();
        var betas = versions.Where(_manager.IsBetaOptiScalerVersion).ToList();

        var items = new System.Collections.Generic.List<ComboBoxItem>
        {
            Selectable("Latest stable (recommended)", null),
        };
        if (stables.Count > 0)
        {
            items.Add(Separator("— Stable releases —"));
            items.AddRange(stables.Select(v => Selectable(v, v)));
        }
        if (betas.Count > 0)
        {
            items.Add(Separator("— Pre-releases / betas —"));
            items.AddRange(betas.Select(v => Selectable(v, v)));
        }
        combo.ItemsSource = items;
        combo.SelectedIndex = 0;
        combo.SelectionChanged += OnOptionChanged;
    }

    private string? CurrentOptiScalerVersion()
    {
        var combo = this.FindControl<ComboBox>("OptiScalerVersionCombo")!;
        return (combo.SelectedItem as ComboBoxItem)?.Tag as string;
    }

    private SpoofMethod CurrentSpoofMethod() =>
        this.FindControl<ComboBox>("SpoofCombo")!.SelectedIndex switch
        {
            1 => SpoofMethod.ForceDxgi,
            2 => SpoofMethod.ForceOff,
            3 => SpoofMethod.OptiPatcher,
            _ => SpoofMethod.Default,
        };

    /// <summary>
    /// What the chosen override actually does, including what the default resolves to on
    /// the GPU in this machine — the one thing the list itself cannot say.
    /// </summary>
    private void UpdateSpoofNote()
    {
        var note = this.FindControl<TextBlock>("SpoofNoteText");
        if (note is null) return;
        note.Text = CurrentSpoofMethod() switch
        {
            SpoofMethod.ForceDxgi =>
                "Writes Dxgi=true. OptiScaler stops applying its own fixes for the games "
                + "this is known to break, because it reads those only while the key is auto.",
            SpoofMethod.ForceOff =>
                "Writes Dxgi=false, for a game that crashes when the adapter lies. A game "
                + "that hides DLSS behind a vendor check will not offer it.",
            SpoofMethod.OptiPatcher =>
                "Patches the game's vendor checks in memory instead, and leaves Dxgi at "
                + "auto — OptiScaler turns spoofing off itself once the patch lands.",
            _ => "Writes Dxgi=auto. " + DefaultSpoofReads(),
        };
    }

    /// <summary>What OptiScaler's own default comes out as, named for this machine's GPU.</summary>
    private string DefaultSpoofReads() => _manager.DetectPrimaryGpu()?.Vendor switch
    {
        GpuVendor.AMD => "On for your AMD GPU, minus the games OptiScaler knows it breaks.",
        GpuVendor.Intel => "On for your Intel GPU, minus the games OptiScaler knows it breaks.",
        GpuVendor.NVIDIA => "Off for your Nvidia GPU — there is nothing to spoof.",
        _ => "On for AMD and Intel, off for Nvidia, minus the games OptiScaler knows it breaks.",
    };

    private Fsr4Backend CurrentBackend()
    {
        if (this.FindControl<RadioButton>("RbInt8")!.IsChecked == true) return Fsr4Backend.Int8Community;
        if (this.FindControl<RadioButton>("RbCustomMerged")!.IsChecked == true) return Fsr4Backend.CustomMerged;
        return Fsr4Backend.Default;
    }

    private UpscalerSelection CurrentUpscaler()
    {
        var choice = (this.FindControl<ComboBox>("UpscalerCombo")!.SelectedItem as ComboBoxItem)?.Tag
            as UpscalerChoice.Choice ?? UpscalerChoice.Auto;

        if (!choice.Id.Equals(UpscalerChoice.FidelityFxId, StringComparison.OrdinalIgnoreCase))
            return new UpscalerSelection(choice.Id, null);

        var index = (this.FindControl<ComboBox>("FfxVersionCombo")!.SelectedItem as ComboBoxItem)?.Tag as int?;
        return new UpscalerSelection(choice.Id, index ?? 0);
    }

    /// <summary>
    /// Fills the upscaler picker from OptiScaler's own list, and the FSR version picker
    /// from whatever the upscaler module in this game reports.
    ///
    /// DLSS is shown only on Nvidia, which is the rule OptiScaler applies in its own
    /// picker — it skips the option unless the primary GPU is DLSS-capable. Where the
    /// GPU cannot be identified, everything is offered rather than nothing: guessing a
    /// user out of the option they came for is worse than letting the overlay refuse it.
    /// </summary>
    private void SetupUpscalerPickers()
    {
        var combo = this.FindControl<ComboBox>("UpscalerCombo")!;
        var vendor = _manager.DetectPrimaryGpu()?.Vendor.ToString();

        combo.Items.Clear();
        foreach (var choice in UpscalerChoice.AvailableFor(vendor))
            combo.Items.Add(new ComboBoxItem
            {
                Content = choice.Vendor is { Length: > 0 } v ? $"{choice.Label}  ({v})" : choice.Label,
                Tag = choice,
            });

        // The FidelityFX family, which is what "pre-enable FSR 4" used to mean.
        combo.SelectedIndex = Math.Max(0, combo.Items
            .OfType<ComboBoxItem>()
            .ToList()
            .FindIndex(i => (i.Tag as UpscalerChoice.Choice)?.Id == UpscalerChoice.FidelityFxId));

        RefreshFfxVersionPicker();
    }

    /// <summary>
    /// Signature of the inputs the FSR version list depends on, so the combo is rebuilt
    /// when they change and left alone when they have not — rebuilding it on every
    /// option change would reset the user's pick, and doing it inside its own
    /// SelectionChanged handler would loop.
    /// </summary>
    private string _ffxVersionSource = string.Empty;

    private void RefreshFfxVersionPicker()
    {
        var backend = CurrentBackend();
        var int8 = CurrentInt8Version();
        var release = CurrentOptiScalerVersion();
        var signature = $"{backend}|{int8}|{release}";
        if (signature == _ffxVersionSource) return;
        _ffxVersionSource = signature;

        var combo = this.FindControl<ComboBox>("FfxVersionCombo")!;
        var options = _manager.FidelityFxVersionOptions(_game, backend, int8, release);

        combo.Items.Clear();

        // Index 0 is exact whatever the list turns out to hold, because AMD sorts the
        // list it reports newest-first. Naming the version it resolves to matters: left
        // as a bare "newest", a user installing FSR 4 could see 3.1.2 and older spelled
        // out below and conclude FSR 4 was not on offer.
        combo.Items.Add(new ComboBoxItem
        {
            Content = options.Versions.Count > 0
                ? $"Newest available — FSR {options.Versions[0]} (recommended)"
                : "Newest available (recommended)",
            Tag = (int?)0,
        });

        for (var i = 1; i < options.Versions.Count; i++)
            combo.Items.Add(new ComboBoxItem { Content = $"FSR {options.Versions[i]}", Tag = (int?)i });

        combo.SelectedIndex = 0;

        var note = this.FindControl<TextBlock>("FfxVersionNoteText")!;
        note.Text = options.Versions.Count > 1
            ? $"{options.Explanation} Anything but \u201cnewest\u201d is a request: AMD builds the "
              + "list at run time and drops providers your GPU cannot run."
            : options.Explanation;
    }

    /// <summary>Shows the FSR version row only for the family where it means something.</summary>
    private void RefreshUpscalerRows()
    {
        var selection = CurrentUpscaler();
        var choice = selection.Choice;

        RefreshFfxVersionPicker();
        this.FindControl<TextBlock>("FfxVersionLabel")!.IsVisible = selection.IsFidelityFx;
        this.FindControl<ComboBox>("FfxVersionCombo")!.IsVisible = selection.IsFidelityFx;
        this.FindControl<TextBlock>("FfxVersionNoteText")!.IsVisible = selection.IsFidelityFx;
        this.FindControl<TextBlock>("UpscalerNoteText")!.Text = choice.Note;
    }

    private string? CurrentInt8Version()
    {
        var combo = this.FindControl<ComboBox>("Int8VersionCombo")!;
        // Tag carries the raw tag; Content may be decorated with "pre-release".
        return (combo.SelectedItem as ComboBoxItem)?.Tag as string;
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
        RefreshUpscalerRows();
        var preview = _manager.BuildInstallPreview(_game, CurrentBackend(), CurrentUpscaler(),
            addFakenvapi: IsChecked("ChkFakenvapi"), addNukemFg: IsChecked("ChkNukemFg"),
            spoofMethod: CurrentSpoofMethod(), forceInt8: IsChecked("ChkForceInt8"),
            fsr4Watermark: IsChecked("ChkWatermark"),
            optiscalerVersion: CurrentOptiScalerVersion());

        var files = this.FindControl<StackPanel>("FilesList")!;
        var ini = this.FindControl<StackPanel>("IniList")!;
        files.Children.Clear();
        ini.Children.Clear();

        foreach (var f in preview.Files) files.Children.Add(Mono(f));
        if (preview.IniKeys.Count == 0)
            ini.Children.Add(Mono("(no ini changes)", FontWeight.Normal, "BrTextSecondary"));
        foreach (var k in preview.IniKeys) ini.Children.Add(Mono(k.ToString()));

        // Make clear the Manager only overrides these keys; the rest comes from the ini.
        var profile = CurrentProfile();
        var iniName = (profile is null || profile.IsBuiltIn) ? "OptiScaler's default .ini" : $"your \"{profile.Name}\" profile";
        ini.Children.Add(Mono($"…everything else comes from {iniName} (left untouched).", FontWeight.Normal, "BrTextSecondary"));

        var conflictBox = this.FindControl<Border>("ConflictBox")!;
        var conflictList = this.FindControl<StackPanel>("ConflictList")!;
        conflictList.Children.Clear();
        conflictBox.IsVisible = preview.Conflicts.Count > 0;
        foreach (var c in preview.Conflicts)
            conflictList.Children.Add(Mono("⚠ " + c, FontWeight.Normal, "BrError"));
    }

    private static IBrush Brush(string key) =>
        Avalonia.Application.Current?.FindResource(key) as IBrush ?? Brushes.Gray;

    private static TextBlock Mono(string text, FontWeight weight = FontWeight.Normal, string brushKey = "BrTextPrimary")
        => new()
        {
            Text = text,
            FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
            FontSize = 12.5,
            FontWeight = weight,
            Foreground = Brush(brushKey),
            TextWrapping = TextWrapping.Wrap,
        };

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        SelectedBackend = CurrentBackend();
        SelectedUpscaler = CurrentUpscaler();
        SelectedInt8Version = SelectedBackend == Fsr4Backend.Int8Community ? CurrentInt8Version() : null;
        SelectedProfile = CurrentProfile();
        AddFakenvapi = IsChecked("ChkFakenvapi");
        AddNukemFg = IsChecked("ChkNukemFg");
        SelectedSpoofMethod = CurrentSpoofMethod();
        ForceInt8 = IsChecked("ChkForceInt8");
        Fsr4Watermark = IsChecked("ChkWatermark");
        SelectedOptiScalerVersion = CurrentOptiScalerVersion();
        RequestClose?.Invoke(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => RequestClose?.Invoke(false);
}
