// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
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

    /// <summary>Which faster FSR 4.1.1 build the user confirmed (read only when that option is chosen).</summary>
    public FasterFsr4Kind SelectedFasterKind { get; private set; } = FasterFsr4Kind.Exact;

    /// <summary>The builds the user ticked for the omniloader, in list order (read only when that option is chosen).</summary>
    public IReadOnlyList<ManagerService.OmniBuildOption> SelectedOmniBuilds { get; private set; }
        = Array.Empty<ManagerService.OmniBuildOption>();

    /// <summary>The INT8 community build version the user confirmed (null unless INT8 chosen).</summary>
    public string? SelectedInt8Version { get; private set; }

    /// <summary>The OptiScaler.ini profile the user confirmed (built-in default = OptiScaler's own config).</summary>
    public OptiScalerProfile? SelectedProfile { get; private set; }

    /// <summary>Install the fakenvapi add-on (nvapi64.dll + fakenvapi.ini).</summary>
    public bool AddFakenvapi { get; private set; }

    /// <summary>Install Nukem's DLSSG-to-FSR3 mod (the imported DLL, and the keys that turn it on).</summary>
    public bool AddNukemFg { get; private set; }

    /// <summary>What the user chose for the Nvidia override.</summary>
    public SpoofMethod SelectedSpoofMethod { get; private set; } = SpoofMethod.Off;

    /// <summary>The OptiScaler release version to install (null = latest).</summary>
    public string? SelectedOptiScalerVersion { get; private set; }

    /// <summary>The FSR 4 model, force and AMD driver files the user confirmed.</summary>
    public Fsr4Options SelectedFsr4 { get; private set; } = Fsr4Options.None;

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

        if (!string.IsNullOrEmpty(game.AntiCheat))
        {
            this.FindControl<Border>("AntiCheatBox")!.IsVisible = true;
            this.FindControl<TextBlock>("AntiCheatText")!.Text =
                $"This game ships {game.AntiCheat}. OptiScaler changes what the game loads, which is "
                + "what anti-cheat looks for — use it offline only, or the account can be banned.";
        }

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

        // A source switched off in Settings is not offered at all, rather than offered
        // and refused.
        int8.IsVisible = _manager.OptiScalerExtrasEnabled;
        SetupFasterOption();
        SetupOmniOption();

        // Pre-selected, and under Advanced: OptiScaler's own release already bundles a
        // working FSR 4.x upscaler, so the zero-decision path delivers FSR 4 as it is.
        def.IsChecked = true;

        int8.IsCheckedChanged += OnInt8CheckedChanged;
        int8.IsCheckedChanged += OnOptionChanged;
        customMerged.IsCheckedChanged += OnOptionChanged;
        def.IsCheckedChanged += OnOptionChanged;

        // Step 2 radios drive the [Upscalers] selection in the preview.

        // Step 2 toggles + Step 3 add-ons all feed the live preview.
        SetupFsr4Options();
        this.FindControl<CheckBox>("ChkWatermark")!.IsCheckedChanged += OnOptionChanged;
        this.FindControl<CheckBox>("ChkFakenvapi")!.IsCheckedChanged += OnOptionChanged;

        // Nvidia override. Off unless asked: OptiScaler's own default for the key turns
        // spoofing on for AMD and Intel, and the old checkbox could only ever write
        // true — so left alone the screen said off while the game was being told it had
        // an RTX 4090. Only a DLSS option the game hides needs this at all.
        var spoofCombo = this.FindControl<ComboBox>("SpoofCombo")!;
        spoofCombo.ItemsSource = new[]
        {
            "Off — the game sees your real GPU",
            "On — the game is told it has an RTX 4090",
            "Let OptiScaler decide",
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

    // The release channels and their versions. "Latest stable" stays selected until the
    // lists arrive, so the screen is usable at once (and offline).
    private IReadOnlyList<string> _stables = Array.Empty<string>();
    private IReadOnlyList<string> _betas = Array.Empty<string>();
    private IReadOnlyList<string> _nightlies = Array.Empty<string>();
    private string _channel = "RbChannelStable";

    private async void SetupOptiScalerVersions()
    {
        var combo = this.FindControl<ComboBox>("OptiScalerVersionCombo")!;
        combo.ItemsSource = new[] { new ComboBoxItem { Content = "Latest stable (recommended)", Tag = null } };
        combo.SelectedIndex = 0;
        combo.SelectionChanged += OnOptionChanged;
        foreach (var name in new[] { "RbChannelStable", "RbChannelBeta", "RbChannelNightly" })
            this.FindControl<RadioButton>(name)!.IsCheckedChanged += (s, e) =>
            {
                if (s is not RadioButton { IsChecked: true } picked) return;
                // Read from the button just checked: the one it replaces reports unchecked
                // only after this runs.
                _channel = picked.Name!;
                FillVersions();
                OnOptionChanged(s, e);
            };
        UpdateChannelButtons();

        var versions = await _manager.GetOptiScalerVersionsAsync();
        _nightlies = versions.Where(_manager.IsNightlyOptiScalerVersion).ToList();
        _betas = versions.Where(v => _manager.IsBetaOptiScalerVersion(v) && !_manager.IsNightlyOptiScalerVersion(v)).ToList();
        _stables = versions.Except(_betas).Except(_nightlies).ToList();
        UpdateChannelButtons();
        FillVersions();
    }

    /// <summary>A channel with nothing in it is not offered.</summary>
    private void UpdateChannelButtons()
    {
        this.FindControl<RadioButton>("RbChannelBeta")!.IsVisible = _betas.Count > 0;
        this.FindControl<RadioButton>("RbChannelNightly")!.IsVisible = _nightlies.Count > 0;
    }

    /// <summary>The chosen channel's versions, newest first, under its own "latest".</summary>
    private void FillVersions()
    {
        var combo = this.FindControl<ComboBox>("OptiScalerVersionCombo")!;
        var note = this.FindControl<TextBlock>("ChannelNoteText")!;
        var items = new System.Collections.Generic.List<ComboBoxItem>();

        if (_channel == "RbChannelNightly" && _nightlies.Count > 0)
        {
            items.Add(new ComboBoxItem { Content = $"Latest nightly — {OptiScalerChannels.Describe(_nightlies[0])}", Tag = _nightlies[0] });
            items.AddRange(_nightlies.Select(v => new ComboBoxItem { Content = OptiScalerChannels.Describe(v), Tag = v }));
            note.Text = "Built from OptiScaler's development branch every day and published untested. "
                        + "New features land here first: OptiScaler 10's FSR 4 model picker and Vulkan FSR 4 are "
                        + "nightly-only until it ships.";
            note.IsVisible = true;
        }
        else if (_channel == "RbChannelBeta" && _betas.Count > 0)
        {
            items.Add(new ComboBoxItem { Content = $"Latest beta — {_betas[0]}", Tag = _betas[0] });
            items.AddRange(_betas.Select(v => new ComboBoxItem { Content = v, Tag = v }));
            note.IsVisible = false;
        }
        else
        {
            items.Add(new ComboBoxItem { Content = "Latest stable (recommended)", Tag = null });
            items.AddRange(_stables.Select(v => new ComboBoxItem { Content = v, Tag = v }));
            note.IsVisible = false;
        }

        combo.ItemsSource = items;
        combo.SelectedIndex = 0;
    }

    private string? CurrentOptiScalerVersion()
    {
        var combo = this.FindControl<ComboBox>("OptiScalerVersionCombo")!;
        return (combo.SelectedItem as ComboBoxItem)?.Tag as string;
    }

    private SpoofMethod CurrentSpoofMethod() =>
        this.FindControl<ComboBox>("SpoofCombo")!.SelectedIndex switch
        {
            1 => SpoofMethod.On,
            2 => SpoofMethod.Auto,
            3 => SpoofMethod.OptiPatcher,
            _ => SpoofMethod.Off,
        };

    /// <summary>
    /// What the chosen override actually does, including what "let OptiScaler decide"
    /// resolves to on the GPU in this machine — the one thing the list cannot say.
    /// </summary>
    private void UpdateSpoofNote()
    {
        var note = this.FindControl<TextBlock>("SpoofNoteText");
        if (note is null) return;
        note.Text = CurrentSpoofMethod() switch
        {
            SpoofMethod.On =>
                "Writes Dxgi=true. This is what unlocks a DLSS option the game hides from "
                + "AMD and Intel; OptiScaler then drives FSR or XeSS through it.",
            SpoofMethod.Auto =>
                "Writes Dxgi=auto. " + DefaultSpoofReads(),
            SpoofMethod.OptiPatcher =>
                "Patches the game's vendor checks in memory and leaves Dxgi at auto, which "
                + "OptiScaler turns off itself once the patch lands. Only games on "
                + "OptiPatcher's list can be patched — the overlay title shows (OP) when it worked.",
            _ => "Writes Dxgi=false. Turn it on only if this game's DLSS option is hidden "
                + "— a game that offers FSR or XeSS needs no lie, and some crash with one.",
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

    /// <summary>
    /// The faster FSR 4.1.1 builds: shown only when their source is on, and refused —
    /// in place, with the reason — on a GPU no build is made for.
    /// </summary>
    private void SetupFasterOption()
    {
        var faster = this.FindControl<RadioButton>("RbFaster")!;
        faster.IsVisible = _manager.Fsr4ShaderOverridesEnabled;

        if (_manager.FasterFsr4Refusal() is { } refused)
        {
            faster.IsEnabled = false;
            faster.Content = _manager.GpuGeneration == RadeonGeneration.Rdna4
                ? "Faster FSR 4.1.1 — not for RX 9000"
                : "Faster FSR 4.1.1 — not for this GPU";
            ToolTip.SetTip(faster, refused);
        }

        var kind = this.FindControl<ComboBox>("FasterKindCombo")!;
        kind.ItemsSource = new[]
        {
            "Exact — the same image as AMD's",
            "Lossy — about twice as fast, softer",
        };
        kind.SelectedIndex = 0;
        kind.SelectionChanged += (s, e) => { UpdateFasterNote(); OnOptionChanged(s, e); };
        faster.IsCheckedChanged += (s, e) =>
        {
            this.FindControl<StackPanel>("FasterPanel")!.IsVisible = faster.IsChecked == true;
            UpdateFasterNote();
            OnOptionChanged(s, e);
        };
    }

    /// <summary>
    /// The omniloader option: offered only when this build of the app carries it, with
    /// every build the switched-on sources have listed when it is picked.
    /// </summary>
    private void SetupOmniOption()
    {
        var omni = this.FindControl<RadioButton>("RbOmni")!;
        omni.IsVisible = _manager.IsBackendAvailable(Fsr4Backend.Omniloader);
        omni.IsCheckedChanged += async (s, e) =>
        {
            this.FindControl<StackPanel>("OmniPanel")!.IsVisible = omni.IsChecked == true;
            if (omni.IsChecked == true) await LoadOmniBuildsAsync();
            OnOptionChanged(s, e);
        };
    }

    private bool _omniLoaded;
    private async System.Threading.Tasks.Task LoadOmniBuildsAsync()
    {
        if (_omniLoaded) return;
        _omniLoaded = true;
        var list = this.FindControl<StackPanel>("OmniList")!;
        list.Children.Add(new TextBlock { Text = "Loading the builds on offer…", Foreground = Brush("BrTextSecondary") });

        var options = await _manager.OmniBuildOptionsAsync();
        list.Children.Clear();
        foreach (var option in options)
        {
            var box = new CheckBox
            {
                Content = option.Label,
                Tag = option,
                // OptiScaler's own is the one every install already has, so it starts ticked.
                IsChecked = option.Id == "optiscaler",
            };
            if (option.Note is { } note) ToolTip.SetTip(box, note);
            box.IsCheckedChanged += OnOptionChanged;
            list.Children.Add(box);
        }
        UpdatePreview();
    }

    private IReadOnlyList<ManagerService.OmniBuildOption> CurrentOmniBuilds() =>
        this.FindControl<StackPanel>("OmniList")!.Children
            .OfType<CheckBox>()
            .Where(c => c.IsChecked == true)
            .Select(c => (ManagerService.OmniBuildOption)c.Tag!)
            .ToList();

    /// <summary>
    /// The FSR 4 model picker, the force switch and the AMD driver files. The picker is shown
    /// only for a release that takes [FSR] Fsr4Model (OptiScaler 10 and newer).
    /// </summary>
    private void SetupFsr4Options()
    {
        var model = this.FindControl<ComboBox>("Fsr4ModelCombo")!;
        model.ItemsSource = Fsr4Models.All
            .Select(m => new ComboBoxItem { Content = Fsr4Models.Label(m), Tag = m })
            .ToList();
        model.SelectedIndex = 0;
        model.SelectionChanged += OnOptionChanged;
        this.FindControl<CheckBox>("ChkForceFsr4")!.IsCheckedChanged += OnOptionChanged;

        var driver = this.FindControl<CheckBox>("ChkAmdDriver")!;
        var versions = this.FindControl<ComboBox>("AmdDriverVersionCombo")!;
        var imported = _manager.AmdDriverFilesImported
            .GroupBy(f => f.Version)
            .Select(g => new ComboBoxItem
            {
                Content = $"{g.Key}  ({string.Join(" + ", g.Select(f => AmdDriverFiles.Describe(f.Api)))})",
                Tag = g.ToList(),
            })
            .ToList();

        if (imported.Count == 0)
        {
            driver.IsEnabled = false;
            driver.Content = "AMD driver FSR 4 files — none imported (Settings)";
        }
        else
        {
            versions.ItemsSource = imported;
            versions.SelectedIndex = 0;
            versions.SelectionChanged += OnOptionChanged;
            // On by default where nothing else provides them.
            driver.IsChecked = ManagerService.DriverFilesNeeded;
        }
        driver.IsCheckedChanged += (s, e) => { RefreshDriverRow(); OnOptionChanged(s, e); };
        RefreshDriverRow();
    }

    private void RefreshDriverRow()
    {
        var versions = this.FindControl<ComboBox>("AmdDriverVersionCombo")!;
        this.FindControl<StackPanel>("AmdDriverVersionPanel")!.IsVisible =
            IsChecked("ChkAmdDriver") && versions.ItemCount > 1;
    }

    /// <summary>Shows the model picker where the release takes it, with what "for this GPU" means here.</summary>
    private void RefreshFsr4Rows()
    {
        var schema = _manager.Fsr4Schema(CurrentOptiScalerVersion());
        this.FindControl<StackPanel>("Fsr4ModelPanel")!.IsVisible = schema.HasModelKey;
        var note = Fsr4Models.DescribeFor(_manager.GpuGeneration);
        if (CurrentBackend() == Fsr4Backend.Omniloader)
            note += " Builds behind the omniloader run the model their own file picks: OptiScaler's model choice reaches only the DLL it loads itself.";
        this.FindControl<TextBlock>("Fsr4ModelNoteText")!.Text = note;
    }

    private Fsr4Options CurrentFsr4()
    {
        var model = (this.FindControl<ComboBox>("Fsr4ModelCombo")!.SelectedItem as ComboBoxItem)?.Tag as Fsr4ModelChoice?
            ?? Fsr4ModelChoice.Auto;
        if (!_manager.Fsr4Schema(CurrentOptiScalerVersion()).HasModelKey) model = Fsr4ModelChoice.Auto;
        var driverFiles = IsChecked("ChkAmdDriver")
            ? (this.FindControl<ComboBox>("AmdDriverVersionCombo")!.SelectedItem as ComboBoxItem)?.Tag as List<AmdDriverFile>
            : null;
        return new Fsr4Options(model, IsChecked("ChkForceFsr4"), (IReadOnlyList<AmdDriverFile>?)driverFiles ?? Array.Empty<AmdDriverFile>());
    }

    private FasterFsr4Kind CurrentFasterKind() =>
        this.FindControl<ComboBox>("FasterKindCombo")!.SelectedIndex == 1 ? FasterFsr4Kind.Lossy : FasterFsr4Kind.Exact;

    /// <summary>What the chosen build is for this GPU, and how to see that it is the one running.</summary>
    private void UpdateFasterNote()
    {
        if (this.FindControl<TextBlock>("FasterNoteText") is not { } note) return;
        var generation = _manager.GpuGeneration;
        var label = Fsr4ShaderOverrides.OverlayLabel(generation, CurrentFasterKind());
        // These builds are cut from the RX 7000 model; since driver 26.10.1 AMD has one of
        // its own for RX 6000, and which is better there is not settled.
        var rx6000 = generation == RadeonGeneration.Rdna2
            ? " AMD's own RX 6000 model (driver 26.10.1) may suit this card better: compare them with \u201cSeveral FSR builds\u201d."
            : "";
        note.Text = CurrentFasterKind() == FasterFsr4Kind.Lossy
            ? $"The build for {RadeonGenerations.Describe(generation)}. Changes the image: softer, and frame "
              + $"times alternate between short and long. OptiScaler's overlay names it {label} when it is the one running." + rx6000
            : $"The build for {RadeonGenerations.Describe(generation)}, with AMD's image unchanged. "
              + $"OptiScaler's overlay names it {label} when it is the one running." + rx6000;
    }

    private Fsr4Backend CurrentBackend()
    {
        if (this.FindControl<RadioButton>("RbOmni")!.IsChecked == true) return Fsr4Backend.Omniloader;
        if (this.FindControl<RadioButton>("RbFaster")!.IsChecked == true) return Fsr4Backend.FasterShaders;
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
        RefreshFsr4Rows();
        var preview = _manager.BuildInstallPreview(_game, CurrentBackend(), CurrentUpscaler(),
            addFakenvapi: IsChecked("ChkFakenvapi"), addNukemFg: IsChecked("ChkNukemFg"),
            spoofMethod: CurrentSpoofMethod(), fsr4: CurrentFsr4(),
            fsr4Watermark: IsChecked("ChkWatermark"),
            optiscalerVersion: CurrentOptiScalerVersion(), fasterKind: CurrentFasterKind(),
            omniBuilds: CurrentOmniBuilds());

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
        foreach (var c in preview.Conflicts)
            conflictList.Children.Add(Mono("⚠ " + c, FontWeight.Normal, "BrError"));

        // OptiScaler brings its own FidelityFX files, which would be written over the FSR 4 set.
        var fsr4 = _manager.HasFsr4Swap(_game);
        if (fsr4)
            conflictList.Children.Add(Mono(
                "⚠ FSR 4 without OptiScaler is set up in this game. Undo it on the game's page first.",
                FontWeight.Normal, "BrError"));
        conflictBox.IsVisible = conflictList.Children.Count > 0;
        this.FindControl<Button>("ConfirmButton")!.IsEnabled = !fsr4;
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
        SelectedFsr4 = CurrentFsr4();
        Fsr4Watermark = IsChecked("ChkWatermark");
        SelectedOptiScalerVersion = CurrentOptiScalerVersion();
        SelectedFasterKind = CurrentFasterKind();
        SelectedOmniBuilds = SelectedBackend == Fsr4Backend.Omniloader
            ? CurrentOmniBuilds()
            : Array.Empty<ManagerService.OmniBuildOption>();
        RequestClose?.Invoke(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => RequestClose?.Invoke(false);
}
