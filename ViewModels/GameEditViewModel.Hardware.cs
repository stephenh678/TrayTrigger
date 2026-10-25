using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>Edit Game: CPU cores, HDR, display mode, monitor, mouse lock, dimming and sound.</summary>
public partial class GameEditViewModel
{
    // --- CPU Cores (hybrid Intel, Ryzen X3D and multi-CCD Ryzen) ---

    private CpuAffinityMode _cpuAffinity;
    public CpuAffinityMode CpuAffinity
    {
        get => _cpuAffinity;
        set
        {
            _cpuAffinity = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CpuAffinityHint));
            OnPropertyChanged(nameof(ShowCpuCoresDelay));
        }
    }

    public sealed record CpuAffinityOption(CpuAffinityMode Value, string Label);

    /// <summary>
    /// How this PC's cores are built. A property so a test can stand in any CPU; the dialog reads
    /// the real one.
    /// </summary>
    internal CpuTopology Topology { get; init; } = CpuTopologyService.GetTopology();

    /// <summary>
    /// The choices that mean something on this PC, worded for it. A game set to one meant for
    /// another kind of CPU - a library moved from an Intel PC to a Ryzen - keeps it, listed last
    /// and labelled as doing nothing here, rather than being changed behind the user's back.
    /// </summary>
    public IReadOnlyList<CpuAffinityOption> CpuAffinityOptions
    {
        get
        {
            var options = Topology.Options.Select(m => new CpuAffinityOption(m, Topology.OptionLabel(m))).ToList();
            if (options.Count == 0) options.Add(new CpuAffinityOption(CpuAffinityMode.Default, Topology.OptionLabel(CpuAffinityMode.Default)));
            if (options.All(o => o.Value != _initialCpuAffinity))
            {
                options.Add(new CpuAffinityOption(_initialCpuAffinity, $"{CpuTopology.MenuLabel(_initialCpuAffinity)} (does nothing on this PC)"));
            }
            return options;
        }
    }

    /// <summary>The value the dialog opened with, so it stays pickable even when this CPU has no use for it.</summary>
    private CpuAffinityMode _initialCpuAffinity;

    /// <summary>
    /// The CPU Cores box is left out on a CPU where there is nothing to choose (every core alike,
    /// or a single-CCD X3D whose cores all have the cache), unless the game already has a choice.
    /// </summary>
    public bool ShowCpuCores => Topology.HasOptions || _initialCpuAffinity != CpuAffinityMode.Default;

    /// <summary>What the chosen CPU Cores option does on this PC, under the box.</summary>
    public string CpuAffinityHint
    {
        get
        {
            string layout = Topology.Summary;
            string? noEffect = Topology.WhyNoEffect(CpuAffinity);
            string effect = CpuAffinity switch
            {
                CpuAffinityMode.Default => "Windows decides which cores the game runs on.",
                _ when noEffect != null => $"Changes nothing on this PC: {noEffect}.",
                CpuAffinityMode.Auto => (Topology.Recommended == CpuAffinityMode.VCacheCores ? "Uses the 3D V-Cache cores" : "Uses the performance cores")
                    + " here, and whatever suits the CPU if your library moves to another PC.",
                CpuAffinityMode.PerformanceCoresOnly => "Keeps the game off the efficiency cores. Helps games that stutter when a thread lands on one.",
                CpuAffinityMode.VCacheCores => "Keeps the game on the CCD with the extra cache, which most games run fastest on.",
                CpuAffinityMode.FrequencyCores => "Keeps the game on the CCD without the extra cache, which clocks higher. For the few games that prefer clock speed.",
                CpuAffinityMode.OneCcd => "Keeps the game on one CCD, so its threads share one L3 cache.",
                _ => string.Empty
            };
            return layout.Length == 0 ? effect : $"{layout} {effect}";
        }
    }

    private string _cpuCoresDelaySeconds = "0";
    /// <summary>Seconds to wait after the game starts before its cores are set. Text, so Save can refuse a bad value.</summary>
    public string CpuCoresDelaySeconds
    {
        get => _cpuCoresDelaySeconds;
        set { _cpuCoresDelaySeconds = value; OnPropertyChanged(); ClearFieldError(EditField.CpuCoresDelay); }
    }

    /// <summary>The delay only matters when the choice changes something.</summary>
    public bool ShowCpuCoresDelay => CpuAffinity != CpuAffinityMode.Default;

    // --- HDR (per game; overrides the Optimized profile's Enable HDR switch) ---

    private HdrMode _hdr;
    private HdrMode _initialHdr;
    public HdrMode Hdr
    {
        get => _hdr;
        set
        {
            _hdr = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HdrHint));
            NotifyProfileSummary();
        }
    }

    // --- Auto HDR (per game; overrides the Optimized profile's Enable Auto HDR switch) ---

    private HdrMode _autoHdr;
    private HdrMode _initialAutoHdr;
    public HdrMode AutoHdr
    {
        get => _autoHdr;
        set
        {
            _autoHdr = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoHdrHint));
            NotifyProfileSummary();
        }
    }

    /// <summary>Auto HDR is a Windows 11 feature: left out on Windows 10, and on a PC with no HDR display, unless the game already has a choice.</summary>
    public bool ShowAutoHdr => _initialAutoHdr != HdrMode.ProfileDefault || (SystemTweaksService.IsWindows11 && HdrDisplayPresent());

    public string AutoHdrHint => AutoHdr switch
    {
        HdrMode.On => "Auto HDR is switched on for this game while it runs and put back when it exits, whatever its profile. It needs Windows' HDR and the global Auto HDR switch to be on, and covers DirectX 11 and 12 games.",
        HdrMode.Off => "Auto HDR is switched off for this game while it runs and put back when it exits, even when the profile would turn it on. For a game whose HUD blows out or whose whites clip.",
        _ => "Follows the Optimized profile's Enable Auto HDR switch on the System page, like every other game on that profile. It needs Windows' HDR and the global Auto HDR switch to be on.",
    };

    // --- Display (per game: a monitor's resolution and refresh rate while it runs) ---

    /// <summary>One choice in the Resolution list: no change (Width 0), or a resolution.</summary>
    public sealed record ResolutionOption(int Width, int Height, string Label)
    {
        public bool IsNone => Width == 0;
    }

    /// <summary>One choice in the Refresh rate list.</summary>
    public sealed record RateOption(int Hz, string Label)
    {
        public bool IsNone => Hz == 0;
    }

    internal const string DefaultLabel = "Default";

    /// <summary>
    /// The Resolution list: no change, then each resolution the main display offers, largest first. A
    /// resolution the game has saved that this display doesn't offer (another monitor, a remote session)
    /// stays at the end, labelled, so opening and saving Edit Game doesn't quietly drop it.
    /// </summary>
    internal static IReadOnlyList<ResolutionOption> BuildResolutionOptions(IReadOnlyList<DisplayMode> available, DisplayMode? saved)
    {
        var options = new List<ResolutionOption> { new(0, 0, DefaultLabel) };
        foreach (var m in available)
        {
            if (!options.Any(o => o.Width == m.Width && o.Height == m.Height))
                options.Add(new ResolutionOption(m.Width, m.Height, $"{m.Width} × {m.Height}"));
        }
        if (saved is { Width: > 0, Height: > 0 } kept && !options.Any(o => o.Width == kept.Width && o.Height == kept.Height))
        {
            options.Add(new ResolutionOption(kept.Width, kept.Height, $"{kept.Width} × {kept.Height} (this display doesn't list it)"));
        }
        return options;
    }

    /// <summary>
    /// The Refresh rate list: Default, then the rates the display offers at the chosen resolution, fastest first, or, with
    /// the resolution left at Default, at the resolution the desktop runs now (every resolution's when that can't be read).
    /// The game's saved rate stays, labelled, if the display doesn't offer it there.
    /// </summary>
    internal static IReadOnlyList<RateOption> BuildRateOptions(IReadOnlyList<DisplayMode> available, ResolutionOption resolution, DisplayMode? saved, DisplayMode? desktop = null)
    {
        IEnumerable<DisplayMode> at = available;
        if (!resolution.IsNone) at = available.Where(m => m.Width == resolution.Width && m.Height == resolution.Height);
        else if (desktop is { } now) at = available.Where(m => m.Width == now.Width && m.Height == now.Height);
        var rates = at.Select(m => m.RefreshHz).Distinct().OrderByDescending(hz => hz).ToList();
        var options = new List<RateOption> { new(0, DefaultLabel) };
        options.AddRange(rates.Select(hz => new RateOption(hz, $"{hz} Hz")));
        // Only under the resolution it was saved with (none, for a rate saved on its own): under another one it is not a choice.
        if (saved is { RefreshHz: > 0 } kept && kept.Width == resolution.Width && kept.Height == resolution.Height && !rates.Contains(kept.RefreshHz))
        {
            options.Add(new RateOption(kept.RefreshHz, $"{kept.RefreshHz} Hz (this display doesn't list it)"));
        }
        return options;
    }

    private IReadOnlyList<DisplayMode> _availableModes = [];
    private DisplayMode? _savedMode;
    private DisplayMode? _desktopMode;

    /// <summary>The Resolution and Refresh rate lists for one monitor, what is selected in them, and whether they can be used.</summary>
    private sealed record DisplayLists(
        IReadOnlyList<DisplayMode> Modes, DisplayMode? Desktop,
        IReadOnlyList<ResolutionOption> Resolutions, ResolutionOption Resolution,
        IReadOnlyList<RateOption> Rates, RateOption Rate,
        bool Usable, string Note);

    /// <summary>
    /// The lists for a monitor (the main display when <paramref name="monitorId"/> is empty), with <paramref name="chosen"/> selected where
    /// the monitor offers it. A mode the monitor doesn't list is kept at the end, labelled, so opening and saving Edit Game doesn't
    /// quietly drop it. Used when the dialog opens and each time the monitor changes, so both work the same way.
    /// </summary>
    private DisplayLists BuildDisplayLists(string monitorId, DisplayMode? chosen)
    {
        var modes = DisplayModeService.Shared.AvailableModes(monitorId);
        var desktop = DisplayModeService.Shared.CurrentMode(monitorId);
        var resolutions = BuildResolutionOptions(modes, chosen);
        var resolution = resolutions.FirstOrDefault(o => chosen is { Width: > 0 } s && o.Width == s.Width && o.Height == s.Height) ?? resolutions[0];
        var rates = BuildRateOptions(modes, resolution, chosen, desktop);
        var rate = rates.FirstOrDefault(r => r.Hz == chosen?.RefreshHz) ?? rates[0];

        bool usable = modes.Count > 0 || chosen != null;
        bool unplugged = !string.IsNullOrEmpty(monitorId) && !_monitors.Any(m => string.Equals(m.Id, monitorId, StringComparison.OrdinalIgnoreCase));
        string note = usable ? string.Empty
            : unplugged ? "That monitor isn't connected, so its resolutions and refresh rates can't be listed. Pick another monitor."
            : "The display's resolutions and refresh rates can't be read here (a remote session, for example).";
        return new DisplayLists(modes, desktop, resolutions, resolution, rates, rate, usable, note);
    }

    public IReadOnlyList<ResolutionOption> ResolutionOptions { get; private set; } = [];

    private IReadOnlyList<RateOption> _rateOptions = [];
    public IReadOnlyList<RateOption> RateOptions
    {
        get => _rateOptions;
        private set { _rateOptions = value; OnPropertyChanged(); }
    }

    private ResolutionOption _selectedResolution = new(0, 0, DefaultLabel);
    public ResolutionOption SelectedResolution
    {
        get => _selectedResolution;
        set
        {
            var previousRate = _selectedRate?.Hz;
            _selectedResolution = value ?? ResolutionOptions[0];
            RateOptions = BuildRateOptions(_availableModes, _selectedResolution, _savedMode, _desktopMode);
            // The same rate if the new resolution offers it, else Default.
            SelectedRate = RateOptions.FirstOrDefault(r => r.Hz == previousRate) ?? RateOptions[0];
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayHint));
        }
    }

    private RateOption? _selectedRate;
    public RateOption? SelectedRate
    {
        get => _selectedRate;
        set
        {
            _selectedRate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayHint));
        }
    }

    /// <summary>What the two lists add up to, or null when both are Default. A part left at Default is 0.</summary>
    private DisplayMode? SelectedDisplayMode =>
        SelectedResolution.IsNone && (SelectedRate == null || SelectedRate.IsNone)
            ? null
            : new DisplayMode(SelectedResolution.Width, SelectedResolution.Height, SelectedRate?.Hz ?? 0);

    /// <summary>The Resolution and Refresh rate lists can be used: the display lists modes, or the game already has one. Otherwise they are shown disabled, with <see cref="DisplayNote"/>.</summary>
    public bool ShowDisplay { get; private set; }

    /// <summary>Why the Resolution and Refresh rate lists are disabled, or empty.</summary>
    public string DisplayNote { get; private set; } = string.Empty;
    public bool HasDisplayNote => DisplayNote.Length > 0;

    /// <summary>Why a list with one device or none is disabled: what is connected, and when it works. Empty with two or more (or when the game already has a choice).</summary>
    internal static string OnlyOneNote(string kind, IReadOnlyList<string> names, string whenMore)
    {
        return names.Count switch
        {
            0 => $"No {kind} is connected.",
            1 => $"Only one {kind} is connected ({names[0]}), so there is nothing to switch to. {whenMore}",
            _ => string.Empty,
        };
    }

    // --- The monitor, the main display, the mouse, the dimming and the sound ---

    /// <summary>One choice in the Monitor list. An empty id is the main display, whichever monitor that is.</summary>
    public sealed record MonitorOption(string Id, string Label);

    internal const string MainDisplayLabel = "Default (main display)";

    /// <summary>
    /// The Monitor list: the main display, then each attached monitor by name. A monitor the game was set up for that
    /// isn't attached stays at the end, labelled, so opening and saving Edit Game doesn't quietly drop it.
    /// </summary>
    internal static IReadOnlyList<MonitorOption> BuildMonitorOptions(IReadOnlyList<MonitorInfo> monitors, string savedId)
    {
        var options = new List<MonitorOption> { new(string.Empty, MainDisplayLabel) };
        options.AddRange(monitors.Select(m => new MonitorOption(m.Id, m.IsPrimary ? $"{m.Label} (main display now)" : m.Label)));
        if (!string.IsNullOrEmpty(savedId) && !monitors.Any(m => string.Equals(m.Id, savedId, StringComparison.OrdinalIgnoreCase)))
        {
            options.Add(new MonitorOption(savedId, "A monitor that isn't connected"));
        }
        return options;
    }

    /// <summary>One choice in the Sound output list: no change (empty id), or a playback device.</summary>
    public sealed record AudioOption(string Id, string Label);

    internal const string NoAudioChangeLabel = "Default";

    internal static IReadOnlyList<AudioOption> BuildAudioOptions(IReadOnlyList<AudioDevice> devices, string savedId, string missingLabel = "A sound output that isn't connected")
    {
        var options = new List<AudioOption> { new(string.Empty, NoAudioChangeLabel) };
        options.AddRange(devices.Select(d => new AudioOption(d.Id, d.Name)));
        if (!string.IsNullOrEmpty(savedId) && !devices.Any(d => string.Equals(d.Id, savedId, StringComparison.OrdinalIgnoreCase)))
        {
            options.Add(new AudioOption(savedId, missingLabel));
        }
        return options;
    }

    private IReadOnlyList<MonitorInfo> _monitors = [];
    public IReadOnlyList<MonitorOption> MonitorOptions { get; private set; } = [];

    private MonitorOption _selectedMonitor = new(string.Empty, MainDisplayLabel);
    public MonitorOption SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            _selectedMonitor = value ?? MonitorOptions[0];
            // The lists are of this monitor's modes. What is chosen now carries over (nothing chosen stays nothing).
            var chosen = SelectedDisplayMode;
            var lists = BuildDisplayLists(_selectedMonitor.Id, chosen);
            _savedMode = chosen;
            _availableModes = lists.Modes;
            _desktopMode = lists.Desktop;

            // The lists first and the choices after: replacing a ComboBox's items clears its selection and writes that back here.
            ResolutionOptions = lists.Resolutions;
            OnPropertyChanged(nameof(ResolutionOptions));
            RateOptions = lists.Rates;
            _selectedResolution = lists.Resolution;
            _selectedRate = lists.Rate;
            ShowDisplay = lists.Usable;
            DisplayNote = lists.Note;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedResolution));
            OnPropertyChanged(nameof(SelectedRate));
            OnPropertyChanged(nameof(ShowDisplay));
            OnPropertyChanged(nameof(DisplayNote));
            OnPropertyChanged(nameof(HasDisplayNote));
            OnPropertyChanged(nameof(ShowMakeMainDisplay));
            OnPropertyChanged(nameof(DisplayHint));
        }
    }

    /// <summary>The Monitor list can be used: more than one monitor, or the game already names one. Otherwise it is shown disabled, with <see cref="MonitorNote"/>.</summary>
    public bool ShowMonitorChoice { get; private set; }

    public string MonitorNote { get; private set; } = string.Empty;
    public bool HasMonitorNote => MonitorNote.Length > 0;

    private bool _makeMainDisplay;
    public bool MakeMainDisplay
    {
        get => _makeMainDisplay;
        set { _makeMainDisplay = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayHint)); }
    }

    /// <summary>Making a monitor the main display needs a monitor chosen that isn't the main one already.</summary>
    public bool ShowMakeMainDisplay => ShowMonitorChoice && !string.IsNullOrEmpty(SelectedMonitor.Id)
        && !_monitors.Any(m => string.Equals(m.Id, SelectedMonitor.Id, StringComparison.OrdinalIgnoreCase) && m.IsPrimary);

    /// <summary>Flashes a large number on every monitor, as Windows' own Identify does, to tell which is which.</summary>
    public ICommand IdentifyMonitorsCommand => _identifyMonitorsCommand ??= new RelayCommand(() => MonitorIdentifier.Flash(_monitors));
    private ICommand? _identifyMonitorsCommand;

    private bool _dimOtherScreens;
    public bool DimOtherScreens
    {
        get => _dimOtherScreens;
        set { _dimOtherScreens = value; OnPropertyChanged(); }
    }
    private bool _initialDim;

    public IReadOnlyList<AudioOption> AudioOptions { get; private set; } = [];
    private AudioOption _selectedAudio = new(string.Empty, NoAudioChangeLabel);
    public AudioOption SelectedAudio
    {
        get => _selectedAudio;
        set { _selectedAudio = value ?? AudioOptions[0]; OnPropertyChanged(); }
    }

    /// <summary>The Sound output list can be used: more than one playback device, or the game already has one. Otherwise it is shown disabled, with <see cref="AudioNote"/>.</summary>
    public bool ShowAudio { get; private set; }

    public string AudioNote { get; private set; } = string.Empty;
    public bool HasAudioNote => AudioNote.Length > 0;

    internal const string MissingMicrophoneLabel = "A microphone that isn't connected";

    public IReadOnlyList<AudioOption> MicrophoneOptions { get; private set; } = [];
    private AudioOption _selectedMicrophone = new(string.Empty, NoAudioChangeLabel);
    public AudioOption SelectedMicrophone
    {
        get => _selectedMicrophone;
        set { _selectedMicrophone = value ?? MicrophoneOptions[0]; OnPropertyChanged(); }
    }

    /// <summary>The Microphone list can be used: more than one recording device, or the game already has one. Otherwise it is shown disabled, with <see cref="MicrophoneNote"/>.</summary>
    public bool ShowMicrophone { get; private set; }

    public string MicrophoneNote { get; private set; } = string.Empty;
    public bool HasMicrophoneNote => MicrophoneNote.Length > 0;

    private bool _keepMouseOnGameScreen;
    public bool KeepMouseOnGameScreen
    {
        get => _keepMouseOnGameScreen;
        set { _keepMouseOnGameScreen = value; OnPropertyChanged(); }
    }

    private bool _initialKeepMouse;

    /// <summary>How many monitors are attached; a test stands in its own answer.</summary>
    internal Func<int> MonitorCount { get; init; } = Win32DisplayBackend.MonitorCount;

    /// <summary>The mouse and dimming options only do something with more than one monitor, so they show then, or when the game already has them on.</summary>
    public bool ShowMouseLock => _initialKeepMouse || MonitorCount() > 1;
    public bool ShowDim => _initialDim || MonitorCount() > 1;

    /// <summary>The Display and sound card shows on the All tab and on its own Display tab.</summary>
    public bool ShowDisplayCard => IsAllSection || IsDisplaySection;

    public string DisplayHint
    {
        get
        {
            bool main = MakeMainDisplay && ShowMakeMainDisplay;
            string layout = "The monitor is made the main display before the game starts and put back when it ends.";
            return SelectedDisplayMode switch
            {
                null => main ? layout : "The display is left as it is.",
                { Width: > 0, RefreshHz: > 0 } m => $"Runs at {m.Width} × {m.Height} and {m.RefreshHz} Hz. {ModeNote}",
                { Width: > 0 } m => $"Runs at {m.Width} × {m.Height}, at the refresh rate the display has now if it offers it there, otherwise the fastest it does. {ModeNote}",
                var m => $"Runs at the resolution the display has now, at {m!.Value.RefreshHz} Hz. {ModeNote}",
            };
        }
    }

    private const string ModeNote = "It is set before the game starts and put back when it ends. A game that sets its own resolution overrides it while it runs. A different resolution may rearrange open windows and desktop icons.";

    private void LoadDisplay(GameEntry game)
    {
        _savedMode = DisplayModeService.RequestedMode(game);
        _monitors = DisplayModeService.Shared.AvailableMonitors();
        MonitorOptions = BuildMonitorOptions(_monitors, game.DisplayMonitorId);
        _selectedMonitor = MonitorOptions.FirstOrDefault(o => string.Equals(o.Id, game.DisplayMonitorId, StringComparison.OrdinalIgnoreCase)) ?? MonitorOptions[0];
        ShowMonitorChoice = _monitors.Count > 1 || !string.IsNullOrEmpty(game.DisplayMonitorId);
        MonitorNote = ShowMonitorChoice ? string.Empty : _monitors.Count == 0
            ? "The monitors can't be read here (a remote session, for example)."
            : $"Only one monitor is connected ({(string.IsNullOrWhiteSpace(_monitors[0].Name) ? "Monitor" : _monitors[0].Name)}, {_monitors[0].Bounds.Right - _monitors[0].Bounds.Left} × {_monitors[0].Bounds.Bottom - _monitors[0].Bounds.Top}). With two or more, this list picks the monitor, and options to make it the main display, keep the mouse on it and dim the others appear.";
        _makeMainDisplay = game.MakeMainDisplay;
        _dimOtherScreens = game.DimOtherScreens;
        _initialDim = game.DimOtherScreens;

        var lists = BuildDisplayLists(_selectedMonitor.Id, _savedMode);
        _availableModes = lists.Modes;
        _desktopMode = lists.Desktop;
        ResolutionOptions = lists.Resolutions;
        _selectedResolution = lists.Resolution;
        _rateOptions = lists.Rates;
        _selectedRate = lists.Rate;
        ShowDisplay = lists.Usable;
        DisplayNote = lists.Note;

        var devices = AudioOutputService.Shared.AvailableDevices();
        AudioOptions = BuildAudioOptions(devices, game.AudioOutputDeviceId);
        _selectedAudio = AudioOptions.FirstOrDefault(o => string.Equals(o.Id, game.AudioOutputDeviceId, StringComparison.OrdinalIgnoreCase)) ?? AudioOptions[0];
        ShowAudio = devices.Count > 1 || !string.IsNullOrEmpty(game.AudioOutputDeviceId);
        AudioNote = ShowAudio ? string.Empty : OnlyOneNote("sound output", devices.Select(d => d.Name).ToList(), "This list works when there are two or more.");

        var microphones = AudioOutputService.SharedInput.AvailableDevices();
        MicrophoneOptions = BuildAudioOptions(microphones, game.AudioInputDeviceId, MissingMicrophoneLabel);
        _selectedMicrophone = MicrophoneOptions.FirstOrDefault(o => string.Equals(o.Id, game.AudioInputDeviceId, StringComparison.OrdinalIgnoreCase)) ?? MicrophoneOptions[0];
        ShowMicrophone = microphones.Count > 1 || !string.IsNullOrEmpty(game.AudioInputDeviceId);
        MicrophoneNote = ShowMicrophone ? string.Empty : OnlyOneNote("microphone", microphones.Select(d => d.Name).ToList(), "This list works when there are two or more.");
    }

    public sealed record HdrOption(HdrMode Value, string Label);

    public IReadOnlyList<HdrOption> HdrOptions { get; } =
        new[] { HdrMode.ProfileDefault, HdrMode.On, HdrMode.Off }.Select(m => new HdrOption(m, HdrModes.Label(m))).ToList();

    /// <summary>
    /// Whether this PC has a display Windows can switch to HDR. Read once, lazily, since the
    /// display API is not free; a test stands in its own answer.
    /// </summary>
    private static readonly Lazy<bool> s_hdrDisplayPresent = new(() =>
    {
        try { return HdrControlService.GetDisplayStates().Any(s => s.Supported); }
        catch (Exception ex)
        {
            LoggingService.Verbose("GameEdit", $"Could not read the displays' HDR support: {ex.Message}");
            return false;
        }
    });
    internal Func<bool> HdrDisplayPresent { get; init; } = () => s_hdrDisplayPresent.Value;

    /// <summary>The same answer for callers with no edit window, such as the library's filter flyout.</summary>
    internal static bool HdrDisplayPresentOnThisPc => s_hdrDisplayPresent.Value;

    /// <summary>The HDR box is left out on a PC with no HDR-capable display, unless the game already has a choice.</summary>
    public bool ShowHdr => _initialHdr != HdrMode.ProfileDefault || HdrDisplayPresent();

    /// <summary>What the chosen HDR option does, under the box.</summary>
    public string HdrHint => Hdr switch
    {
        HdrMode.On => "Windows' HDR is turned on when this game launches and back off when it exits, whatever its profile - even Off. A display already in HDR is left alone.",
        HdrMode.Off => "HDR is left as it is for this game, even when the profile would turn it on. For a game that looks washed out or over-bright in HDR.",
        _ => "Follows the Optimized profile's Enable HDR switch on the System page, like every other game on that profile.",
    };
}
