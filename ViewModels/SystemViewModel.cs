using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.Views;

namespace TrayTrigger.ViewModels;

public enum SystemSubSection
{
    All,
    HardwareSpecs,
    PerformanceTweaks,
    GameProfiles
}

public class SystemTweakViewModel : ViewModelBase
{
    private readonly SystemTweaksService _service;
    private readonly Action<string> _notifyParent;
    private readonly Action<SystemTweakViewModel, bool>? _onToggled;

    public SystemTweakItem Model { get; }

    public string Id => Model.Id;
    public string Name => Model.Name;
    public TweakCategory Category => Model.Category;
    public string ShortDescription => Model.ShortDescription;
    public string WhyItMatters => Model.WhyItMatters;
    public bool RequiresAdmin => Model.RequiresAdmin;
    public bool RequiresReboot => Model.RequiresReboot;
    public bool CanToggle => Model.CanToggle;
    public bool HasCustomAction => Model.HasCustomAction;
    public bool IsOptIn => Model.IsOptIn;
    public string CustomActionLabel => Model.CustomActionLabel;
    public bool IsInformational => Model.IsInformational;
    public bool IsAvailable => Model.IsAvailable;
    public string UnavailableReason => Model.UnavailableReason;
    public bool ShowUnavailableReason => !Model.IsAvailable && !string.IsNullOrWhiteSpace(Model.UnavailableReason);
    public bool IsRecommended => Model.IsRecommended;

    private bool _isBusy;
    /// <summary>True while this row's toggle is being applied off the UI thread (UAC prompt, reg import).</summary>
    public bool IsBusy
    {
        get => _isBusy;
        internal set
        {
            if (_isBusy != value)
            {
                _isBusy = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActionButtonText));
            }
        }
    }

    /// <summary>
    /// Deliberately independent of <see cref="IsBusy"/>: the row's Optimize/Revert button must not
    /// disable itself while its own click is being handled.
    ///
    /// WPF moves keyboard focus off an element the moment it becomes disabled, and the element it
    /// moves to gets scrolled into view - so a button that greys itself out mid-click threw the
    /// tweak list's scroll position to wherever the next focusable row happened to be. The button
    /// staying enabled also keeps it out of the re-enable path that needs a CommandManager
    /// requery, which is what made the next click on it do nothing.
    ///
    /// Re-entry is still blocked - <see cref="ExecuteToggleAsync"/> returns immediately while busy
    /// - and "Working..." in <see cref="ActionButtonText"/> is what tells the user it is running.
    /// </summary>
    public bool CanExecuteToggle => CanToggle && IsAvailable;

    /// <summary>"Learn more" target: Help/tweaks/&lt;id&gt;.md, embedded at build time.</summary>
    public string HelpTopicId => "tweaks/" + Model.Id;

    private bool _isOptimal;
    public bool IsOptimal
    {
        get => _isOptimal;
        set
        {
            if (_isOptimal != value)
            {
                _isOptimal = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusBadgeText));
                OnPropertyChanged(nameof(StatusBadgeColor));
                OnPropertyChanged(nameof(StatusBadgeForeground));
                OnPropertyChanged(nameof(IsNotApplied));
                OnPropertyChanged(nameof(ActionButtonText));
                OnPropertyChanged(nameof(AccessibleStatus));
            }
        }
    }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (_statusText != value)
            {
                _statusText = value;
                OnPropertyChanged();
            }
        }
    }

    // Informational rows (Core Isolation) get a neutral ON/OFF badge: "optimal" would mean a
    // security feature is off. Unavailable rows show a grey N/A badge.
    public string StatusBadgeText => IsInformational ? (IsOptimal ? "ON" : "OFF")
        : !IsAvailable ? "N/A"
        : IsOptimal ? "OPTIMAL" : IsRecommended ? "NOT APPLIED" : "STANDARD";
    public string StatusBadgeColor => IsInformational ? "#2F5F8F"
        : !IsAvailable ? "#4A4A55"
        : IsOptimal ? "#238636" : IsRecommended ? "#3D2F14" : "#6E6E7A";
    /// <summary>A recommended tweak that is off gets the amber badge (RESTART's colours), so it is the row you see first.</summary>
    public string StatusBadgeForeground => IsNotApplied ? "#E8B84E" : "#FFFFFF";
    /// <summary>Part of the preset and currently off: what Apply Performance Preset would change.</summary>
    public bool IsNotApplied => IsRecommended && !IsOptimal;
    // "Restore Previous", not "Revert to Default": it puts back the value TrayTrigger found before
    // it changed it, which is not always the Windows default (Help/tweaks/overview.md).
    public string ActionButtonText => IsBusy ? "Working..." : IsOptimal ? "Restore Previous" : "Optimize";

    /// <summary>
    /// The row as a screen reader should hear it: "Windows Game Mode, optimal, needs a restart".
    /// The name, the status badge and the OPT-IN / RESTART / ADMIN tags are separate text
    /// elements on screen; the row's buttons carry this so each one says what it acts on.
    /// </summary>
    public string AccessibleStatus => ComposeAccessibleStatus(Name, StatusBadgeText, IsOptIn, RequiresReboot, RequiresAdmin);

    internal static string ComposeAccessibleStatus(string name, string badge, bool isOptIn, bool requiresReboot, bool requiresAdmin)
    {
        var parts = new List<string> { name, badge == "N/A" ? "not available" : badge.ToLowerInvariant() };
        if (isOptIn) parts.Add("opt-in");
        if (requiresReboot) parts.Add("needs a restart");
        if (requiresAdmin) parts.Add("asks for administrator permission");
        return string.Join(", ", parts);
    }

    public ICommand ToggleCommand { get; }
    public ICommand CustomActionCommand { get; }

    public SystemTweakViewModel(SystemTweakItem model, SystemTweaksService service, Action<string> notifyParent, Action<SystemTweakViewModel, bool>? onToggled = null)
    {
        Model = model;
        _service = service;
        _notifyParent = notifyParent;
        _onToggled = onToggled;
        _isOptimal = model.IsOptimal;
        _statusText = model.StatusText;

        ToggleCommand = new AsyncRelayCommand(ExecuteToggleAsync, () => CanExecuteToggle);
        CustomActionCommand = new RelayCommand(ExecuteCustomAction);
    }

    /// <summary>
    /// Applies the toggle off the UI thread: an HKLM tweak is an elevated reg import behind a UAC
    /// prompt with a two-minute ceiling, and the window must keep painting meanwhile.
    /// </summary>
    internal async Task ExecuteToggleAsync()
    {
        if (IsBusy) return;
        bool targetState = !IsOptimal;
        IsBusy = true;
        bool actualState;
        try
        {
            actualState = await Task.Run(() =>
            {
                _service.ApplyTweak(Id, targetState);
                // Trust a fresh read of the real system state over ApplyTweak's own return value:
                // an elevated write can report "failed" (a slow UAC prompt) while it actually went
                // through moments later, or "succeeded" without the underlying state matching.
                return _service.GetTweakState(Id);
            });
        }
        finally
        {
            IsBusy = false;
        }

        bool changed = actualState != IsOptimal;
        IsOptimal = actualState;
        StatusText = actualState ? "Optimal configuration applied" : "Reverted to standard Windows default";

        if (actualState == targetState)
        {
            _notifyParent($"Toggled '{Name}' to {(targetState ? "Optimal" : "Default")}.");
            if (changed) _onToggled?.Invoke(this, actualState);

            if (RequiresReboot && changed)
            {
                bool restartNow = ModernDialog.PromptRestart(null,
                    $"\"{Name}\" has been updated, but Windows won't apply it until you restart your PC.");
                if (restartNow)
                {
                    RestartNow();
                }
            }
        }
        else
        {
            LoggingService.Warn("System", $"Toggle '{Name}' ({Id}) to {(targetState ? "Optimal" : "Default")} did not take effect - actual state read back as {(actualState ? "Optimal" : "Default")}.");
            _notifyParent(RequiresAdmin
                ? $"Failed to update '{Name}'. The administrator prompt was cancelled or the change was rejected."
                : $"Failed to update '{Name}'.");
        }
    }

    internal static void RestartNow()
    {
        try
        {
            using var proc = Process.Start("shutdown.exe", "/r /t 30 /c \"TrayTrigger: restarting to apply performance settings\"");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("System", $"Failed to initiate restart: {ex.Message}");
        }
    }

    private void ExecuteCustomAction()
    {
        if (Id == "core_isolation")
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo("windowsdefender://coreisolation") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LoggingService.Swallowed("System", ex, "opening Core Isolation in Windows Security");
                try { using var proc = Process.Start(new ProcessStartInfo("ms-settings:privacy") { UseShellExecute = true }); } catch (Exception inner) { LoggingService.Swallowed("System", inner, "opening Windows Settings"); }
            }
        }
        else if (Id == "hags")
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo("ms-settings:display-advancedgraphics") { UseShellExecute = true });
            }
            catch (Exception ex) { LoggingService.Swallowed("System", ex, "opening Windows graphics settings"); }
        }
    }

    public void RefreshState(SystemTweakItem updated)
    {
        IsOptimal = updated.IsOptimal;
        StatusText = updated.StatusText;
    }
}

/// <summary>
/// One toggleable tweak within a single Performance Profile tier (Optimized or Aggressive).
/// Reads/writes a bool on that tier's own <see cref="PerformanceProfileTweakConfig"/> via
/// delegates, so adding a new profile tweak later is just one more instance of this class -
/// no new bound property or XAML template needed.
/// </summary>
public class ProfileTweakToggleViewModel : ViewModelBase
{
    private readonly Func<bool> _getter;
    private readonly Action<bool> _setter;

    public string Name { get; }
    public string ShortDescription { get; }
    public string WhyItMatters { get; }
    public bool IsOptIn { get; }

    /// <summary>"Optimized" or "Aggressive", for its line in Activity &amp; History.</summary>
    public string Tier { get; internal set; } = string.Empty;

    /// <summary>The value last recorded, so a change made elsewhere (Reset to Defaults) can be told apart.</summary>
    private bool _recorded;

    /// <summary>True when applying this tweak raises a UAC prompt (an HKLM write or an elevated
    /// PowerShell cmdlet). Shown as the same ADMIN badge the permanent tweak rows use.</summary>
    public bool RequiresAdmin { get; }

    /// <summary>Optional one-line dependency or caveat shown under the description - e.g. the
    /// 0.5 ms timer request only reaching the game while the permanent timer tweak is on.</summary>
    public string Note { get; }
    public bool HasNote => !string.IsNullOrEmpty(Note);

    /// <summary>"Learn more" target, e.g. "profiles/power_plan" -> Help/profiles/power_plan.md.</summary>
    public string HelpTopicId { get; }

    public string StatusBadgeText => IsEnabled ? "ENABLED" : "DISABLED";
    public string StatusBadgeColor => IsEnabled ? "#238636" : "#6E6E7A";
    public string ActionButtonText => IsEnabled ? "Disable" : "Enable";

    /// <summary>The row for a screen reader: "Set power plan, enabled, asks for administrator permission".</summary>
    public string AccessibleStatus => SystemTweakViewModel.ComposeAccessibleStatus(Name, StatusBadgeText, IsOptIn, requiresReboot: false, RequiresAdmin);

    public ICommand ToggleCommand { get; }

    public ProfileTweakToggleViewModel(string name, string shortDescription, string whyItMatters, string helpTopicId, Func<bool> getter, Action<bool> setter, bool isOptIn = false, bool requiresAdmin = false, string note = "")
    {
        Name = name;
        ShortDescription = shortDescription;
        WhyItMatters = whyItMatters;
        HelpTopicId = helpTopicId;
        _getter = getter;
        _setter = setter;
        IsOptIn = isOptIn;
        RequiresAdmin = requiresAdmin;
        Note = note;
        ToggleCommand = new RelayCommand(() => IsEnabled = !IsEnabled);
        _recorded = getter();
    }

    public bool IsEnabled
    {
        get => _getter();
        set
        {
            if (_getter() != value)
            {
                _setter(value);
                LoggingService.Info("System", $"Performance Profile tweak '{Name}' {(value ? "enabled" : "disabled")}.");
                _recorded = value;
                PerformanceActivity.TierTweakChanged(Tier, Name, value);
                NotifyStateChanged();
            }
        }
    }

    /// <summary>When the setting changed elsewhere since it was last recorded: the line for it, once.</summary>
    internal string? TakeOutsideChange()
    {
        bool now = _getter();
        if (now == _recorded) return null;
        _recorded = now;
        return PerformanceActivity.DescribeTierTweakChanged(Tier, Name, now).Text;
    }

    /// <summary>Re-reads the live config value into the bindings - for when the underlying
    /// setting was changed from elsewhere (Settings → Reset to Defaults).</summary>
    public void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(StatusBadgeText));
        OnPropertyChanged(nameof(StatusBadgeColor));
        OnPropertyChanged(nameof(ActionButtonText));
        OnPropertyChanged(nameof(AccessibleStatus));
    }
}

public class SystemViewModel : ViewModelBase
{
    private readonly SystemInfoService _infoService;
    private readonly SystemTweaksService _tweaksService;
    private readonly AppSettings _settings;
    private readonly StorageService _storageService;
    private readonly DispatcherTimer _telemetryTimer;

    // Sub-section Navigation
    private SystemSubSection _currentSubSection = SystemSubSection.All;
    public SystemSubSection CurrentSubSection
    {
        get => _currentSubSection;
        set
        {
            if (SetProperty(ref _currentSubSection, value))
            {
                OnPropertyChanged(nameof(IsAllTab));
                OnPropertyChanged(nameof(IsSpecsTab));
                OnPropertyChanged(nameof(IsTweaksTab));
                OnPropertyChanged(nameof(IsGameProfilesTab));
                OnPropertyChanged(nameof(ShowSpecsSection));
                OnPropertyChanged(nameof(ShowTweaksSection));
                OnPropertyChanged(nameof(ShowGameProfilesSection));
                OnPropertyChanged(nameof(SearchScope));
            }
        }
    }

    private string _systemSearchText = string.Empty;
    /// <summary>
    /// The page's card search (see Views/CardSearch). Like Settings and About, it searches the selected
    /// tab and stays as you switch tabs.
    /// </summary>
    public string SystemSearchText
    {
        get => _systemSearchText;
        set => SetProperty(ref _systemSearchText, value ?? string.Empty);
    }

    public bool IsAllTab => CurrentSubSection == SystemSubSection.All;
    public bool IsSpecsTab => CurrentSubSection == SystemSubSection.HardwareSpecs;
    public bool IsTweaksTab => CurrentSubSection == SystemSubSection.PerformanceTweaks;
    public bool IsGameProfilesTab => CurrentSubSection == SystemSubSection.GameProfiles;

    public bool ShowSpecsSection => CurrentSubSection == SystemSubSection.All || CurrentSubSection == SystemSubSection.HardwareSpecs;
    public bool ShowTweaksSection => CurrentSubSection == SystemSubSection.All || CurrentSubSection == SystemSubSection.PerformanceTweaks;
    public bool ShowGameProfilesSection => CurrentSubSection == SystemSubSection.All || CurrentSubSection == SystemSubSection.GameProfiles;

    // Hardware Report
    private SystemHardwareReport _report = new();
    public SystemHardwareReport Report
    {
        get => _report;
        private set
        {
            _report = value;
            GpuCards.Clear();
            foreach (var gpu in value.Gpus)
                GpuCards.Add(new GpuCardViewModel(gpu, _nvidiaDrivers));
            OnPropertyChanged();
            OnPropertyChanged(nameof(Cpu));
            OnPropertyChanged(nameof(PrimaryGpu));
            OnPropertyChanged(nameof(Ram));
            OnPropertyChanged(nameof(PrimaryDisplay));
            OnPropertyChanged(nameof(Displays));
            OnPropertyChanged(nameof(SecondaryDisplays));
            OnPropertyChanged(nameof(HasMultipleDisplays));
            OnPropertyChanged(nameof(Drives));
            OnPropertyChanged(nameof(Os));
            OnPropertyChanged(nameof(Power));
            OnPropertyChanged(nameof(Network));
            OnPropertyChanged(nameof(GpuList));
        }
    }

    public CpuHardwareInfo Cpu => Report.Cpu;

    /// <summary>
    /// How the cores are built, as the per-game CPU Cores choice sees them: "Hybrid: 8 performance
    /// cores and 8 efficiency cores.", or which CCD has the 3D V-Cache. Empty, and hidden, when every
    /// core is alike.
    /// </summary>
    public string CpuCoreLayoutDisplay
    {
        get
        {
            var topology = CpuTopologyService.GetTopology();
            return topology.NoChoiceNote.Length > 0 ? $"{topology.Summary} {topology.NoChoiceNote}" : topology.Summary;
        }
    }
    public bool HasCpuCoreLayout => CpuCoreLayoutDisplay.Length > 0;
    public GpuHardwareInfo PrimaryGpu => Report.Gpus.FirstOrDefault() ?? new GpuHardwareInfo();
    public List<GpuHardwareInfo> GpuList => Report.Gpus;

    /// <summary>Every GPU, dedicated first: a laptop's or a desktop with the CPU's own graphics
    /// turned on has two, and which one a game lands on matters.</summary>
    public ObservableCollection<GpuCardViewModel> GpuCards { get; } = new();

    private readonly NvidiaDriverService _nvidiaDrivers = new();

    /// <summary>The library's games, for the Storage card's per-drive counts. Set by the main view model.</summary>
    public Func<IReadOnlyList<GameEntry>>? GetLibraryGames { get; set; }

    /// <summary>
    /// Counts the library's games on each drive, off the UI thread (each game's folder is checked on
    /// disk), so a drive holding games shows how many - and a hard drive holding any says they'd load
    /// faster from an SSD.
    /// </summary>
    private async Task CountGamesPerDriveAsync(SystemHardwareReport report)
    {
        var games = GetLibraryGames?.Invoke();
        if (games == null || report.Drives.Count == 0) return;
        try
        {
            var counts = await Task.Run(() => games
                .Select(SystemInfoService.GameDriveLetter)
                .Where(letter => letter != null)
                .GroupBy(letter => letter!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase));
            foreach (var drive in report.Drives)
                drive.GameCount = counts.GetValueOrDefault(drive.DriveLetter);
            if (ReferenceEquals(report, Report))
                OnPropertyChanged(nameof(Drives));
        }
        catch (Exception ex)
        {
            LoggingService.Warn("System", $"Couldn't count games per drive: {ex.Message}");
        }
    }

    /// <summary>Created on first use: the counters cost nothing until the specs are on screen.</summary>
    private GpuTelemetryService? _gpuTelemetry;
    private bool _isSamplingGpu;

    /// <summary>
    /// Reads GPU load and video memory off the UI thread and hands each card its own. Cards are
    /// matched by adapter LUID; with one GPU and one adapter in the counters, they're each other's.
    /// </summary>
    private async Task SampleGpusAsync()
    {
        if (_isSamplingGpu || GpuCards.Count == 0) return;
        _isSamplingGpu = true;
        try
        {
            _gpuTelemetry ??= await Task.Run(() => new GpuTelemetryService());
            var samples = await Task.Run(() => _gpuTelemetry.Sample());
            if (samples.Count == 0) return;

            foreach (var card in GpuCards)
            {
                if (card.Info.AdapterLuid != 0 && samples.TryGetValue(card.Info.AdapterLuid, out var sample))
                    card.ApplySample(sample);
            }
            if (GpuCards.Count == 1 && GpuCards[0].Info.AdapterLuid == 0 && samples.Count == 1)
                GpuCards[0].ApplySample(samples.Values.First());
        }
        catch (Exception ex)
        {
            LoggingService.Warn("System", $"GPU telemetry sample failed: {ex.Message}");
        }
        finally
        {
            _isSamplingGpu = false;
        }
    }
    public RamHardwareInfo Ram => Report.Ram;
    public DisplayHardwareInfo PrimaryDisplay => Report.Displays.FirstOrDefault() ?? new DisplayHardwareInfo();
    public List<DisplayHardwareInfo> Displays => Report.Displays;
    public IEnumerable<DisplayHardwareInfo> SecondaryDisplays => Report.Displays.Skip(1);
    public bool HasMultipleDisplays => Report.Displays.Count > 1;
    public List<DriveStorageInfo> Drives => Report.Drives;
    public OsEnvironmentInfo Os => Report.Os;
    public PowerBatteryInfo Power => Report.Power;
    public NetworkTelemetryInfo Network => Report.Network;

    // Tweaks
    public ObservableCollection<SystemTweakViewModel> Tweaks { get; } = new();
    // Recommended rows first, opt-in rows after, so each card reads in the order the
    // "N / M Recommended" score counts them. OrderBy is stable: definition order is kept
    // within each half.
    /// <summary>
    /// Rows in the order someone scanning for what to do wants them: the recommended tweaks that
    /// are off first, then the applied ones, then opt-in, with tweaks this PC can't use last. The
    /// order is taken when the list loads and after a refresh or a preset, not after each click,
    /// so a row doesn't jump away from under the button that was just pressed.
    /// </summary>
    private IEnumerable<SystemTweakViewModel> ForCategory(TweakCategory category) =>
        Tweaks.Where(t => t.Category == category).OrderBy(SortKey);

    internal static int SortKey(SystemTweakViewModel t) => SortKey(t.IsRecommended, t.IsOptimal, t.IsOptIn, t.IsAvailable, t.IsInformational);

    internal static int SortKey(bool recommended, bool optimal, bool optIn, bool available, bool informational) =>
        !available && !informational ? 3
        : recommended && !optimal ? 0
        : recommended ? 1
        : 2;

    public IEnumerable<SystemTweakViewModel> InputAndDisplayTweaks => ForCategory(TweakCategory.InputAndDisplay);
    public IEnumerable<SystemTweakViewModel> CpuAndPowerTweaks => ForCategory(TweakCategory.CpuAndPower);
    public IEnumerable<SystemTweakViewModel> NetworkAndBackgroundTweaks => ForCategory(TweakCategory.NetworkAndBackground);
    public IEnumerable<SystemTweakViewModel> SecurityAndAdvancedTweaks => ForCategory(TweakCategory.SecurityAndAdvanced);

    // The count line under each group heading ("8 tweaks · 6 optimal"), so a group reads as a group
    // rather than as one more card. Kept current by NotifyTweakStateChanged and the profile toggle
    // handlers in the constructor.
    private string GroupSummary(TweakCategory category)
    {
        var tweaks = ForCategory(category).ToList();
        return GroupSummaryText(
            tweaks.Count(t => t.IsNotApplied),
            tweaks.Count(t => !t.IsAvailable && !t.IsInformational),
            tweaks.Count(t => t.IsRecommended),
            tweaks.Count(t => t.IsOptIn && t.IsAvailable));
    }

    /// <summary>
    /// "1 of 7 preset tweaks not applied · 3 opt-in", or "all 7 preset tweaks applied · 3 opt-in":
    /// the same word, preset, as the card above and the PRESET tag on each row, so the counts
    /// visibly add up.
    /// </summary>
    internal static string GroupSummaryText(int notApplied, int notAvailable, int recommended, int optIn)
    {
        var parts = new List<string>();
        if (recommended > 0)
        {
            string noun = recommended == 1 ? "preset tweak" : "preset tweaks";
            parts.Add(notApplied > 0 ? $"{notApplied} of {recommended} {noun} not applied" : $"all {recommended} {noun} applied");
        }
        if (notAvailable > 0) parts.Add($"{notAvailable} n/a");
        if (optIn > 0) parts.Add($"{optIn} opt-in");
        return string.Join(" · ", parts);
    }

    private static string ProfileSummary(IReadOnlyCollection<ProfileTweakToggleViewModel> toggles) =>
        $"{Plural(toggles.Count, "tweak")} · {toggles.Count(t => t.IsEnabled)} enabled";

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    public string InputAndDisplaySummary => GroupSummary(TweakCategory.InputAndDisplay);
    public string CpuAndPowerSummary => GroupSummary(TweakCategory.CpuAndPower);
    public string NetworkAndBackgroundSummary => GroupSummary(TweakCategory.NetworkAndBackground);
    public string OptimizedProfileSummary => ProfileSummary(OptimizedProfileTweaks);
    public string AggressiveProfileSummary => ProfileSummary(AggressiveProfileTweaks);

    /// <summary>
    /// What the page's card search re-filters on (Views/CardSearch.Scope): the tab, the spec cards
    /// replacing their loading placeholder, and the tweak and profile rows' state - each changes what's
    /// on screen to search.
    /// </summary>
    public object SearchScope =>
        (CurrentSubSection, IsSpecsLoaded, Tweaks.Count, TweaksOptimizationScoreDisplay, OptimizedProfileSummary, AggressiveProfileSummary);

    /// <summary>After any change to the tweaks' state: the score line, each group's count line, and the
    /// search, which may need to re-filter what the rows now say.</summary>
    private void NotifyTweakStateChanged()
    {
        OnPropertyChanged(nameof(OptimalTweakCount));
        OnPropertyChanged(nameof(TweaksOptimizationScoreDisplay));
        OnPropertyChanged(nameof(PresetRingText));
        OnPropertyChanged(nameof(PresetRingColor));
        OnPropertyChanged(nameof(PresetScoreDisplay));
        OnPropertyChanged(nameof(PresetNotAppliedLine));
        OnPropertyChanged(nameof(PresetNotCountedLine));
        OnPropertyChanged(nameof(PresetLeadText));
        OnPropertyChanged(nameof(HasPresetNotCounted));
        OnPropertyChanged(nameof(InputAndDisplaySummary));
        OnPropertyChanged(nameof(CpuAndPowerSummary));
        OnPropertyChanged(nameof(NetworkAndBackgroundSummary));
        OnPropertyChanged(nameof(SearchScope));
    }

    // Optimal count summary
    // The score counts only the recommended set (available, toggleable, not opt-in, not
    // informational) so it reads as "how much of the preset is on", not as a nudge to enable
    // every trade-off tweak or to turn Memory Integrity off.
    public int OptimalTweakCount => Tweaks.Count(t => t.IsRecommended && t.IsOptimal);
    public int TotalTweakCount => Tweaks.Count(t => t.IsRecommended);
    public int OptInActiveCount => Tweaks.Count(t => t.IsOptIn && t.IsAvailable && t.IsOptimal);
    public string TweaksOptimizationScoreDisplay =>
        $"{OptimalTweakCount} / {TotalTweakCount} Recommended Optimizations Active" + (OptInActiveCount > 0 ? $"  ·  {OptInActiveCount} opt-in on" : "");

    // The Performance Preset card: the score as a ring, which recommended tweaks are off and what
    // applying them will ask for, and which ones this PC can't use and so aren't counted.
    public string PresetRingText => $"{OptimalTweakCount}/{TotalTweakCount}";
    /// <summary>Both cards' rings follow one rule: green when nothing is left to do, amber when something is.</summary>
    public string PresetRingColor => RingColor(allGood: TotalTweakCount == 0 || OptimalTweakCount == TotalTweakCount);
    internal static string RingColor(bool allGood) => allGood ? "#238636" : "#E8B84E";
    public string PresetScoreDisplay => PresetScoreText(OptimalTweakCount, TotalTweakCount);
    public string PresetNotAppliedLine => PresetNotAppliedText(
        Tweaks.Where(t => t.IsNotApplied).Select(t => (t.Name, t.RequiresAdmin, t.RequiresReboot)).ToList());
    public string PresetNotCountedLine => PresetNotCountedText(
        Tweaks.Where(t => !t.IsAvailable && t.CanToggle && !t.IsOptIn && !t.IsInformational).Select(t => (t.Name, t.UnavailableReason)).ToList());
    public bool HasPresetNotCounted => PresetNotCountedLine.Length > 0;

    internal static string PresetScoreText(int applied, int total) =>
        total == 0 ? "No preset tweak applies to this PC"
        : applied == total ? $"All {total} preset tweaks applied"
        : $"{applied} of {total} preset tweaks applied";

    /// <summary>The card's lead names the count and the tag, so "which tweaks" is answered by scanning for PRESET.</summary>
    public string PresetLeadText => TotalTweakCount == 0
        ? "The tweaks below marked PRESET, applied together. None applies to this PC."
        : $"The {TotalTweakCount} tweaks below marked PRESET, applied together. Opt-in tweaks aren't part of it; switch them on below if you want them.";

    /// <summary>
    /// "Not applied: HAGS and Game Bar Captures. Apply turns on both; HAGS asks for administrator
    /// permission and needs a restart." People decline a UAC prompt they weren't told to expect.
    /// </summary>
    internal static string PresetNotAppliedText(IReadOnlyList<(string Name, bool Admin, bool Reboot)> missing)
    {
        if (missing.Count == 0) return "Every preset tweak is on. Undo Preset puts them all back to what TrayTrigger found.";
        string apply = missing.Count == 1 ? "Apply turns it on" : missing.Count == 2 ? "Apply turns on both" : $"Apply turns on all {missing.Count}";
        string line = $"Not applied: {StutterCheckService.JoinNames(missing.Select(m => m.Name).ToList())}. {apply}";
        var notes = missing.Where(m => m.Admin || m.Reboot)
            .Select(m => m.Name + " " + (m.Admin && m.Reboot ? "asks for administrator permission and needs a restart" : m.Admin ? "asks for administrator permission" : "needs a restart"))
            .ToList();
        return notes.Count == 0 ? line + "." : line + "; " + string.Join("; ", notes) + ".";
    }

    /// <summary>"Not counted: Variable Refresh Rate for Windowed Games: No VRR display found." Empty when every recommended tweak is usable.</summary>
    internal static string PresetNotCountedText(IReadOnlyList<(string Name, string Reason)> unavailable)
    {
        if (unavailable.Count == 0) return "";
        return "Not counted: " + string.Join(" ", unavailable.Select(u =>
            string.IsNullOrWhiteSpace(u.Reason) ? u.Name + "." : $"{u.Name}: {u.Reason.Trim().TrimEnd('.')}."));
    }

    // Game-Level Performance Profiles: per-game Optimized/Aggressive tweak sets (see
    // GameEditDialog). Aggressive always applies every tweak Optimized has enabled, plus its own
    // extras below - a tweak is only ever configured once, under whichever tier introduces it.
    // Add a new bool to OptimizedProfileTweakConfig or AggressiveProfileTweakConfig (plus a
    // matching apply/restore pair in PerformanceProfileService) and one more toggle entry below
    // when a new profile tweak ships.
    public ObservableCollection<ProfileTweakToggleViewModel> OptimizedProfileTweaks { get; }
    public ObservableCollection<ProfileTweakToggleViewModel> AggressiveProfileTweaks { get; }

    /// <summary>
    /// The profile tweaks a tier will apply, in page order: Optimized's enabled toggles, and for
    /// Aggressive those plus its own. Off applies none. An opt-in that is switched off is simply
    /// not enabled, so it is left out. Edit Game lists these under its profile box.
    /// </summary>
    public IReadOnlyList<ProfileTweakToggleViewModel> EnabledTweaksFor(PerformanceProfileMode mode) =>
        EnabledTweaksFor(mode, OptimizedProfileTweaks, AggressiveProfileTweaks);

    internal static IReadOnlyList<ProfileTweakToggleViewModel> EnabledTweaksFor(
        PerformanceProfileMode mode,
        IEnumerable<ProfileTweakToggleViewModel> optimized,
        IEnumerable<ProfileTweakToggleViewModel> aggressive) => mode switch
    {
        PerformanceProfileMode.Optimized => optimized.Where(t => t.IsEnabled).ToList(),
        PerformanceProfileMode.Aggressive => optimized.Concat(aggressive).Where(t => t.IsEnabled).ToList(),
        _ => Array.Empty<ProfileTweakToggleViewModel>(),
    };

    private ObservableCollection<ProfileTweakToggleViewModel> BuildOptimizedProfileToggles(OptimizedProfileTweakConfig config)
    {
        void Save() => _storageService.SaveSettings(_settings);

        return new ObservableCollection<ProfileTweakToggleViewModel>
        {
            new("\"Ultimate Plan - TrayTrigger\" Power Plan (profile)",
                SystemTweaksService.KeepsCoreParking(CpuTopologyService.GetTopology().Layout)
                    ? "Switches to a full-clock power plan (no PCIe/USB power saving) while the game runs, then switches back. " + SystemTweaksService.X3dCoreParkingNote
                    : "Switches to a full-clock power plan (no core parking, no PCIe/USB power saving) while the game runs, then switches back.",
                "The Windows 'Balanced' plan downclocks cores and parks idle ones during quiet moments, taking 5–15ms to ramp back up and inducing 1% low frame drops when action begins. \"Ultimate Plan - TrayTrigger\" pins the CPU at 100% min/max state with aggressive boost and active cooling, and disables PCIe Link State Power Management and USB selective suspend so the GPU and input devices never stutter through a power-state transition mid-match.",
                "profiles/power_plan",
                () => config.PowerPlanEnabled,
                v => { config.PowerPlanEnabled = v; Save(); }),
            new("Windows High-Performance GPU Preference",
                "Tells Windows to run this game's exe on the high-performance GPU instead of an integrated one.",
                "On laptops with both an integrated and discrete GPU, Windows or the driver sometimes defaults an unrecognized game to the integrated GPU. This writes the same per-executable preference the Settings app itself uses, so the discrete GPU is used without you having to set it manually. No effect on single-GPU desktops. Only applies when TrayTrigger knows the game's real executable path - not available for Steam-launched games, which report a steam:// launch URL rather than a file path.",
                "profiles/gpu_preference",
                () => config.GpuPreferenceEnabled,
                v => { config.GpuPreferenceEnabled = v; Save(); }),
            new("NVIDIA: Prefer Maximum Performance",
                "Sets NVIDIA's power management mode to \"Prefer maximum performance\" while the game runs, then puts back what you had.",
                "By default the driver lowers the GPU's clocks whenever a scene asks less of it, and raising them again takes a moment - long enough to show up as an uneven frame time when the action picks up again, most often in lighter or CPU-bound games. Prefer maximum performance keeps the clocks up, which steadies the 1% lows; the average frame rate barely moves. It's the Control Panel's own setting, written to the Global profile for the length of the session, so it covers Steam games too, and the GPU idles normally again once you stop playing. A game whose own NVIDIA profile sets a power mode keeps it. If the setting is changed elsewhere during the session, TrayTrigger leaves that change alone at the end.",
                "profiles/nvidia_max_performance",
                () => config.NvidiaMaxPerformanceEnabled,
                v => { config.NvidiaMaxPerformanceEnabled = v; Save(); },
                note: "NVIDIA GPUs only. Does nothing on AMD or Intel graphics."),
            new("Exempt the Game From Power Throttling",
                "Stops Windows from throttling the game's process when it decides the game is in the background.",
                "Windows' power throttling (EcoQoS) lowers a background process's clock speed and, on an Intel hybrid CPU, moves it onto the efficiency cores. Windows can decide a game is in the background while you're still watching it - on a second monitor while you type in Discord, or alt-tabbed for a moment - and the frame rate drops until you click back. Windows 11 also ignores the timer requests of a window it can't see, which upsets frame pacing in some engines. This opts the game's process out of both, with the documented SetProcessInformation call. Nothing to undo: it ends with the game. A game running as administrator can refuse it; TrayTrigger logs that and carries on.",
                "profiles/power_throttling",
                () => config.PowerThrottlingExemptEnabled,
                v => { config.PowerThrottlingExemptEnabled = v; Save(); }),
            new("Enable HDR",
                "Turns on Windows' native HDR display mode while the game runs, then reverts every display to its prior setting on exit.",
                "Uses the same Connecting and Configuring Displays (CCD) API behind Settings > System > Display > HDR - not a registry hack, since HDR is a live per-display color pipeline negotiated with the monitor over EDID rather than a static value. Only touches displays Windows already reports as HDR-capable; a display already in HDR is left alone (and left in HDR on exit) rather than being forced off. A game that doesn't render HDR content well can look washed out or over-bright with this on, so it's your call. Risk: a display currently in Windows' Auto Color Management \"WCG\" mode is also forced to full HDR, but Windows' HDR API can only turn a display fully off on restore, not back to WCG specifically - that display may render in plain SDR after the game closes until Windows re-negotiates WCG on its own (e.g. switching to another app).",
                "profiles/hdr",
                () => config.HdrEnabled,
                v => { config.HdrEnabled = v; Save(); },
                isOptIn: true),
            new("Do Not Disturb While Playing",
                "Silences Windows toast notifications for the length of the session, then puts the switch back the way it was.",
                "Windows 11 only auto-enables Do Not Disturb for games it detects as fullscreen; a borderless title still gets Teams/Discord/Update toasts popping over it. This flips the notification centre's global toast switch (the same value its own Do Not Disturb toggle writes) when the game starts and restores your prior setting on exit - a user who already had notifications off stays off. Opt-in because some people are waiting on a message mid-game.",
                "profiles/do_not_disturb",
                () => config.DoNotDisturbEnabled,
                v => { config.DoNotDisturbEnabled = v; Save(); },
                isOptIn: true),
            new("Unmute Speakers While Playing",
                "Unmutes your current playback device when the game starts, and puts the mute back the way it was on exit.",
                "Starting a game into silence because the speakers were muted from earlier is a two-minute detour through the volume flyout - and on a machine that boots muted, every time. This clears the mute flag on whichever playback device Windows currently treats as the default, using the same Core Audio call the volume flyout's own speaker button makes, so it follows a switch to a headset without any per-device setup. Volume level is left exactly where you had it: this only lifts the mute, so a device sitting at 5% still plays at 5%. If the device was not muted at the start of the session, nothing is recorded and nothing is restored. If it was, the mute goes back on when the game exits - TrayTrigger puts what it changes back. Opt-in because sound off is often deliberate.",
                "profiles/unmute_audio",
                () => config.UnmuteAudioEnabled,
                v => { config.UnmuteAudioEnabled = v; Save(); },
                isOptIn: true),
            new("Frame Cap Just Under Your Refresh Rate",
                "Caps the frame rate a little under the primary display's refresh rate while the game runs - 138 fps at 144 Hz, 224 at 240 Hz - for a G-SYNC or FreeSync monitor.",
                "Variable refresh only works while the frame rate stays under the refresh rate. A game that runs past it falls back to V-Sync, with its added input lag, or to tearing. A cap just underneath keeps every frame inside the variable refresh range, and frame times come out more even as well; testing by Blur Busters and NVIDIA found this the lowest-latency way to run G-SYNC. The cap is NVIDIA's own formula, the one Reflex uses: refresh rate minus refresh rate squared over 3600. It's set with the Control Panel's Max Frame Rate on the Global profile, for the length of the session. A Max Frame Rate you set yourself is left alone, and a game with its own cap can still set a lower one. Opt-in because on a screen without variable refresh a cap below the refresh rate judders, and because some competitive players would rather have every frame.",
                "profiles/frame_cap",
                () => config.FrameCapEnabled,
                v => { config.FrameCapEnabled = v; Save(); },
                isOptIn: true,
                note: "NVIDIA GPUs only, for a G-SYNC or FreeSync monitor. Uses the primary display's refresh rate."),
        };
    }

    private ObservableCollection<ProfileTweakToggleViewModel> BuildAggressiveProfileToggles(AggressiveProfileTweakConfig config)
    {
        void Save() => _storageService.SaveSettings(_settings);

        return new ObservableCollection<ProfileTweakToggleViewModel>
        {
            new("System Responsiveness",
                "Reduces the CPU reserve for lower-priority MMCSS tasks to the supported minimum.",
                "Windows reserves 20% of CPU resources for low-priority background tasks by default. Microsoft's MMCSS documentation clamps any value below 10 back up to 20, so 10 is the lowest reserve Windows actually honors - it leaves more scheduling headroom for latency-sensitive foreground workloads like games.",
                "profiles/system_responsiveness",
                () => config.SystemResponsivenessEnabled,
                v => { config.SystemResponsivenessEnabled = v; Save(); },
                requiresAdmin: true),
            new("MMCSS \"Games\" Task Priority",
                "Raises the Multimedia Class Scheduler's built-in \"Games\" task from its Medium default to High.",
                "Officially documented by Microsoft, MMCSS grants time-sensitive threads registered under the \"Games\" task category prioritized CPU access - the same mechanism game engines request via AvSetMmThreadCharacteristics. Windows ships this task at Scheduling Category=Medium by default; raising it to High uses the same sanctioned mechanism with more headroom. This is scheduling tuning, not a guaranteed FPS boost - the effect depends on what else is contending for the CPU. (Other fields some optimizer tools also touch here, like SFIO Priority, are documented by Microsoft as not used, so this tweak leaves them alone.)",
                "profiles/mmcss_games_priority",
                () => config.MmcssGamesPriorityEnabled,
                v => { config.MmcssGamesPriorityEnabled = v; Save(); },
                requiresAdmin: true),
            new("Above Normal Process Priority",
                "Raises the game's own process to Above Normal CPU scheduling priority for the duration of the session.",
                "A real Windows scheduling class (SetPriorityClass), not a registry trick. Community benchmarking consistently finds Above Normal reduces worst-case frame-time stutters with low risk, while pushing further to High priority shows only marginal extra gain and a real risk of starving audio/input threads. Effect varies by game and is not guaranteed. Applied as soon as TrayTrigger finds the game's process - for Steam games too, once Steam reports the game running and its process is found under the install folder.",
                "profiles/above_normal_priority",
                () => config.AboveNormalPriorityEnabled,
                v => { config.AboveNormalPriorityEnabled = v; Save(); }),
            new("Windows Defender Exclusion for Game Files",
                "Excludes the game's executable from Microsoft Defender real-time scanning while it's running.",
                "Real, Microsoft-supported mechanism (Add-MpPreference), not a workaround - and it measurably reduces CPU/I-O hitches during shader compilation and asset streaming on some games. This is a genuine security tradeoff, not just a performance one: it narrows antivirus coverage for that specific file while the profile is active. Defaults off even under Aggressive for that reason - only enable it if you understand and accept the tradeoff. Only applies when TrayTrigger knows the game's real executable path (not Steam launches). If the path was already excluded before this ran, that exclusion is left alone on restore.",
                "profiles/defender_exclusion",
                () => config.DefenderExclusionEnabled,
                v => { config.DefenderExclusionEnabled = v; Save(); },
                isOptIn: true,
                requiresAdmin: true,
                note: "Expect a User Account Control prompt when the game starts and again when it exits."),
            new("0.5 ms Timer Resolution Request",
                "Holds a high-resolution system timer request (NtSetTimerResolution, 0.5 ms) while the game runs, released when the last session ends.",
                "What TimerTool and ISLC do: a finer scheduler tick means Sleep()/timer waits inside the game and its driver stack wake on time instead of up to 15.6 ms late, which shows up as smoother frame pacing in engines that don't request a fine timer themselves. On Windows 10 2004+ and Windows 11 timer resolution is per-process, so this request only reaches the game when the permanent \"System Timer Resolution\" tweak (GlobalTimerResolutionRequests) is on - turn that on under Performance Tweaks first. Costs a little idle power only while a game is running.",
                "profiles/timer_resolution",
                () => config.TimerResolutionEnabled,
                v => { config.TimerResolutionEnabled = v; Save(); },
                note: "Only reaches the game while the permanent \"System Timer Resolution\" tweak (Performance Tweaks tab) is on - turn that on first, then restart once."),
            new("Resizable BAR for Games NVIDIA Hasn't Decided On",
                "Turns on NVIDIA's Resizable BAR for the games NVIDIA hasn't tested, while the game runs. Games NVIDIA approved or rejected keep NVIDIA's choice.",
                "Resizable BAR lets the CPU reach all of the graphics card's memory at once instead of through a 256 MB window. NVIDIA only switches it on for the games it has tested and approved, about 60 of them, and switches it off for the ones it rejected, such as Final Fantasy XVI, Delta Force and Hogwarts Legacy. Every other game gets nothing, though many of them gain: tested gains run from nothing to around 20% (Gears 5) and 46% (Dead Space), averaging a few percent. This sets it on the Global profile for the length of the session. A game's own NVIDIA profile outranks the Global one, so the approved games stay on and the rejected ones stay off; only the undecided games are affected. Some of those may run worse - lower 1% lows or stutter, more often in older games - which is why it's in Aggressive: if a game runs worse, move it to Optimized in Edit Game. Skipped when Resizable BAR is off in the BIOS (Hardware Specs says), and when you've set it on the Global profile yourself.",
                "profiles/resizable_bar",
                () => config.ResizableBarEnabled,
                v => { config.ResizableBarEnabled = v; Save(); },
                note: "NVIDIA GPUs only, with Resizable BAR on in the BIOS."),
        };
    }

    // Busy state for the Apply Preset / Restore Previous Settings bulk actions. These can take anywhere
    // from a couple seconds to well over a minute (elevated UAC prompts, powercfg, a restore
    // point snapshot), so the actual work runs off the UI thread and this drives a floating
    // toast + disables the action buttons for the duration instead of the window looking frozen.
    private bool _isApplyingTweaks;
    public bool IsApplyingTweaks
    {
        get => _isApplyingTweaks;
        set
        {
            if (_isApplyingTweaks != value)
            {
                _isApplyingTweaks = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanRunBulkAction));
            }
        }
    }

    public bool CanRunBulkAction => !IsApplyingTweaks;

    private string _busyToastMessage = "";
    public string BusyToastMessage
    {
        get => _busyToastMessage;
        set
        {
            if (_busyToastMessage != value)
            {
                _busyToastMessage = value;
                OnPropertyChanged();
            }
        }
    }

    // UI State
    private bool _isLoadingSpecs;
    public bool IsLoadingSpecs
    {
        get => _isLoadingSpecs;
        set
        {
            if (_isLoadingSpecs != value)
            {
                _isLoadingSpecs = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSpecsLoaded));
                OnPropertyChanged(nameof(SearchScope));
            }
        }
    }

    /// <summary>Inverse of <see cref="IsLoadingSpecs"/> for the spec cards' visibility.</summary>
    public bool IsSpecsLoaded => !IsLoadingSpecs;

    private DateTime? _specsRefreshedAt;
    /// <summary>"Last read 14:02:11" in the Hardware Specs heading; empty, and hidden with <see cref="HasSpecs"/>, until the first read.</summary>
    public string SpecsRefreshedDisplay => _specsRefreshedAt is DateTime t ? $"Last read {t:HH:mm:ss}" : "";

    private string _statusMessage = "Ready";
    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (_statusMessage != value)
            {
                _statusMessage = value;
                LoggingService.Shown("System status", value);
                OnPropertyChanged();
            }
        }
    }

    // Commands
    public ICommand SelectAllTabCommand { get; }
    public ICommand SelectSpecsTabCommand { get; }
    public ICommand SelectTweaksTabCommand { get; }
    public ICommand SelectGameProfilesTabCommand { get; }
    public ICommand RefreshSpecsCommand { get; }
    public ICommand ApplyRecommendedPresetCommand { get; }
    public ICommand ResetDefaultsCommand { get; }

    // --- Stutter Check (Services/StutterCheckService) ---

    private StutterCheckReport? _stutterReport;
    private bool _isRunningStutterCheck;
    private bool _stutterRanAtStartup;

    /// <summary>A result older than this is re-read when the System page opens.</summary>
    internal static readonly TimeSpan StutterCheckMaxAge = TimeSpan.FromMinutes(10);

    public ObservableCollection<StutterCheckRowViewModel> StutterRows { get; } = new();
    public bool HasStutterReport => _stutterReport != null;
    public bool ShowStutterIntro => _stutterReport == null;
    /// <summary>Before the first result: what's happening, or that nothing has run yet.</summary>
    public string StutterIntroText => IsRunningStutterCheck
        ? "Checking this PC"
        : "Not checked yet";
    public string StutterIntroSubline => IsRunningStutterCheck
        ? "A second or two · nothing here changes by itself."
        : "Run Stutter Check reads everything in a second or two and changes nothing by itself.";
    public string StutterSummary => _stutterReport?.Summary ?? "";
    public int StutterTotalCount => _stutterReport?.Items.Count ?? 0;
    /// <summary>"12/13": checks that are fine over checks run, the way the preset ring reads applied over total.</summary>
    public string StutterRingText => _stutterReport == null ? "" : $"{_stutterReport.FineCount}/{_stutterReport.Items.Count}";
    public string StutterRingColor => RingColor(allGood: _stutterReport == null || _stutterReport.NeedsLookCount + _stutterReport.CanFixCount == 0);
    public string StutterSubline => _stutterReport == null ? ""
        : (_stutterRanAtStartup
            ? $"Checked at startup, {_stutterReport.RanAt:t}"
            : $"Checked {ActivityViewModel.FormatWhen(_stutterReport.RanAt.ToUniversalTime(), DateTime.Now)}")
          + $" · {Math.Max(0.1, _stutterReport.Took.TotalSeconds):0.0} s · nothing here changes by itself.";
    /// <summary>The checks that passed, in two columns under the rows that didn't; always shown.</summary>
    public IReadOnlyList<string> StutterFineTitles => _stutterReport?.Fine.Select(i => i.Title).ToList() ?? [];
    public bool HasStutterFine => (_stutterReport?.FineCount ?? 0) > 0;
    public string StutterFineHeading => _stutterReport == null ? ""
        : _stutterReport.FineCount == _stutterReport.Items.Count ? "Every check is fine"
        : $"{_stutterReport.FineCount} of {_stutterReport.Items.Count} checks fine";

    /// <summary>
    /// Whether a run is due: never while a game is running (nothing interrupts a game), and
    /// otherwise when there is no result yet or the last one is older than <paramref name="maxAge"/>.
    /// </summary>
    internal static bool ShouldRunStutterCheck(DateTime? lastRunUtc, DateTime nowUtc, bool gameRunning, TimeSpan maxAge) =>
        !gameRunning && (lastRunUtc == null || nowUtc - lastRunUtc.Value >= maxAge);

    /// <summary>
    /// Runs the check when it is due (see <see cref="ShouldRunStutterCheck"/>): a few seconds after
    /// startup, and when the System page opens on a stale result. <paramref name="atStartup"/> is
    /// what the subline says afterwards.
    /// </summary>
    public async Task RunStutterCheckIfDueAsync(bool gameRunning, bool atStartup = false)
    {
        if (!ShouldRunStutterCheck(_stutterReport?.RanAt.ToUniversalTime(), DateTime.UtcNow, gameRunning, StutterCheckMaxAge)) return;
        await RunStutterCheckAsync();
        _stutterRanAtStartup = atStartup && _stutterReport != null;
        OnPropertyChanged(nameof(StutterSubline));
    }
    public bool IsRunningStutterCheck
    {
        get => _isRunningStutterCheck;
        private set
        {
            if (SetProperty(ref _isRunningStutterCheck, value))
            {
                OnPropertyChanged(nameof(StutterRunButtonText));
                OnPropertyChanged(nameof(StutterIntroText));
                OnPropertyChanged(nameof(StutterIntroSubline));
            }
        }
    }
    public string StutterRunButtonText => IsRunningStutterCheck ? "Checking..." : HasStutterReport ? "Run Again" : "Run Stutter Check";
    /// <summary>When the last check ran, for the "is it due" decision; null until one has.</summary>
    public DateTime? LastStutterRunUtc => _stutterReport?.RanAt.ToUniversalTime();
    /// <summary>The last run's lines, for Copy Diagnostic Info; null until a check has been run.</summary>
    public IReadOnlyList<string>? StutterReportLines => _stutterReport?.ToLines();

    public ICommand RunStutterCheckCommand { get; }
    public ICommand CopyStutterResultsCommand { get; }

    /// <summary>
    /// Run Again, the heading's Refresh and the capture mode: re-reads every tweak and the probes
    /// off the UI thread, evaluates, and says the result in the status line. The hardware report
    /// is read only when there is none yet; the Hardware Specs heading's Refresh is what re-detects.
    /// </summary>
    public Task RunStutterCheckAsync() => RunStutterCheckAsync(afterAction: false);

    /// <summary>A re-check was asked for while a run was in flight; that run may have read the tweaks before the action.</summary>
    private bool _stutterRerunWanted;

    /// <summary>
    /// <paramref name="afterAction"/> is the re-check after Fix it, Apply Performance Preset or
    /// Undo Preset: the action read the tweaks back itself and the status line says what it did
    /// (the restore point's fate, for one), so neither is redone here.
    /// </summary>
    private async Task RunStutterCheckAsync(bool afterAction)
    {
        if (IsRunningStutterCheck)
        {
            if (afterAction) _stutterRerunWanted = true;
            return;
        }
        IsRunningStutterCheck = true;
        var sw = Stopwatch.StartNew();
        try
        {
            if (Tweaks.Count == 0) await LoadTweaksAsync();
            else if (!afterAction)
            {
                await RefreshAllTweaksAsync(statusOnDone: null);
                ReportTweaksResetSinceApplied();
            }
            if (!HasSpecs) await LoadHardwareSpecsAsync();

            var hardware = Report;
            var tweaks = Tweaks.Select(t => new SystemTweakItem
            {
                Id = t.Id, Name = t.Name, IsOptimal = t.IsOptimal, IsAvailable = t.IsAvailable,
                IsOptIn = t.IsOptIn, IsInformational = t.IsInformational, CanToggle = t.CanToggle,
            }).ToList();
            bool profilePowerPlan = _settings.OptimizedProfileTweaks.PowerPlanEnabled;
            string windowsDrive = System.IO.Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "";

            var inputs = await Task.Run(async () =>
            {
                // The indexer sample sleeps for most of a run; the other probes read meanwhile.
                var search = Task.Run(() => StutterProbes.SearchIndexerBusy());
                var overlays = StutterProbes.RunningOverlayApps();
                var apps = StutterProbes.AppAccelerationStates();
                var apo = StutterProbes.IntelApo(hardware.Cpu.ModelName);
                var (indexerBusy, indexerCpu) = await search;
                return new StutterCheckInputs
                {
                    Hardware = hardware,
                    WindowsDriveLetter = windowsDrive,
                    Tweaks = tweaks,
                    ProfileSwitchesPowerPlan = profilePowerPlan,
                    OverlayApps = overlays,
                    Apps = apps,
                    SearchIndexerBusy = indexerBusy,
                    SearchIndexerCpuPercent = indexerCpu,
                    IntelApo = apo,
                };
            });

            _stutterReport = StutterCheckService.Run(inputs, DateTime.Now, sw.Elapsed);
            _stutterRanAtStartup = false;
            StutterRows.Clear();
            foreach (var item in _stutterReport.Attention)
                StutterRows.Add(new StutterCheckRowViewModel(item, OnStutterAction));
            LoggingService.Info("StutterCheck", _stutterReport.Summary + " (" + string.Join("; ", _stutterReport.Attention.Select(i => i.Title)) + ")");
            if (!afterAction) StatusMessage = "Stutter Check: " + _stutterReport.Summary + ".";
        }
        catch (Exception ex)
        {
            LoggingService.Warn("StutterCheck", $"Stutter Check failed: {ex.Message}");
            if (!afterAction) StatusMessage = "Stutter Check couldn't finish; the log has the details.";
        }
        finally
        {
            IsRunningStutterCheck = false;
            OnPropertyChanged(nameof(HasStutterReport));
            OnPropertyChanged(nameof(ShowStutterIntro));
            OnPropertyChanged(nameof(StutterSummary));
            OnPropertyChanged(nameof(StutterTotalCount));
            OnPropertyChanged(nameof(StutterRingText));
            OnPropertyChanged(nameof(StutterRingColor));
            OnPropertyChanged(nameof(StutterSubline));
            OnPropertyChanged(nameof(StutterFineTitles));
            OnPropertyChanged(nameof(HasStutterFine));
            OnPropertyChanged(nameof(StutterFineHeading));
            OnPropertyChanged(nameof(StutterRunButtonText));
            OnPropertyChanged(nameof(StutterIntroText));
            OnPropertyChanged(nameof(StutterIntroSubline));
            OnPropertyChanged(nameof(SearchScope));
        }
        if (_stutterRerunWanted)
        {
            _stutterRerunWanted = false;
            await RunStutterCheckAsync(afterAction: true);
        }
    }

    /// <summary>Fix it applies the named tweak the same way its own button does, prompt and all, then checks again.</summary>
    private async void OnStutterAction(StutterCheckItem item)
    {
        try
        {
            switch (item.Action)
            {
                case StutterAction.HelpTopic:
                    HelpCommands.ShowTopic.Execute(item.ActionArg);
                    break;
                case StutterAction.OpenSetting:
                    SafeLaunchProcess(item.ActionArg);
                    break;
                case StutterAction.ApplyTweak:
                    var tweak = Tweaks.FirstOrDefault(t => t.Id == item.ActionArg);
                    if (tweak == null || tweak.IsOptimal || !tweak.CanExecuteToggle) return;
                    await tweak.ExecuteToggleAsync();
                    await RunStutterCheckAsync(afterAction: true);
                    break;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("StutterCheck", $"The action for \"{item.Title}\" failed: {ex.Message}");
        }
    }

    private void CopyStutterResults()
    {
        if (_stutterReport == null) return;
        try
        {
            System.Windows.Clipboard.SetText(_stutterReport.ToText());
            StatusMessage = "Stutter Check results copied.";
        }
        catch (Exception ex)
        {
            LoggingService.Warn("StutterCheck", $"Could not copy the results: {ex.Message}");
        }
    }


    // Quick Tools Commands
    public ICommand OpenTaskManagerCommand { get; }
    public ICommand OpenDeviceManagerCommand { get; }
    public ICommand OpenGraphicsSettingsCommand { get; }
    public ICommand OpenDxDiagCommand { get; }
    /// <summary>Windows Settings > Display > Advanced display, where a screen's refresh rate is chosen.</summary>
    public ICommand OpenAdvancedDisplaySettingsCommand { get; }

    /// <summary>
    /// Refreshes every profile-tweak toggle from the live settings. The toggles read straight
    /// from the config objects they were built against (which Settings → Reset to Defaults now
    /// resets in place rather than replacing), so only their change notifications are needed.
    /// </summary>
    public void RefreshProfileTweakToggles()
    {
        // Called after Settings' Reset to Defaults: what it changed in the profiles is recorded.
        var changes = OptimizedProfileTweaks.Concat(AggressiveProfileTweaks)
            .Select(t => t.TakeOutsideChange()).OfType<string>().ToList();
        if (_recordedNewGameProfile != _settings.NewGameProfile)
        {
            changes.Add($"Profile for new games: {_recordedNewGameProfile} to {_settings.NewGameProfile}");
            _recordedNewGameProfile = _settings.NewGameProfile;
        }
        PerformanceActivity.ResetToDefaults(changes);
        foreach (var t in OptimizedProfileTweaks) t.NotifyStateChanged();
        foreach (var t in AggressiveProfileTweaks) t.NotifyStateChanged();
        OnPropertyChanged(nameof(NewGameProfile));
        OnPropertyChanged(nameof(NewGameProfileIsAggressive));
    }

    public IReadOnlyList<PerformanceProfileMode> NewGameProfileOptions { get; } =
        [PerformanceProfileMode.Off, PerformanceProfileMode.Optimized, PerformanceProfileMode.Aggressive];

    private PerformanceProfileMode _recordedNewGameProfile;

    /// <summary>The profile a game gets when it's added to the library. Games already there keep theirs.</summary>
    public PerformanceProfileMode NewGameProfile
    {
        get => _settings.NewGameProfile;
        set
        {
            if (_settings.NewGameProfile == value) return;
            var was = _settings.NewGameProfile;
            _settings.NewGameProfile = value;
            _recordedNewGameProfile = value;
            PerformanceActivity.NewGameProfileChanged(was, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(NewGameProfileIsAggressive));
            _storageService.SaveSettings(_settings);
        }
    }

    /// <summary>Aggressive asks for administrator permission at every launch and exit; the card says so.</summary>
    public bool NewGameProfileIsAggressive => NewGameProfile == PerformanceProfileMode.Aggressive;

    public SystemViewModel(SystemInfoService infoService, SystemTweaksService tweaksService, AppSettings settings, StorageService storageService)
    {
        _infoService = infoService;
        _tweaksService = tweaksService;
        _settings = settings;
        _storageService = storageService;

        OptimizedProfileTweaks = BuildOptimizedProfileToggles(_settings.OptimizedProfileTweaks);
        AggressiveProfileTweaks = BuildAggressiveProfileToggles(_settings.AggressiveProfileTweaks);
        foreach (var t in OptimizedProfileTweaks) t.Tier = nameof(PerformanceProfileMode.Optimized);
        foreach (var t in AggressiveProfileTweaks) t.Tier = nameof(PerformanceProfileMode.Aggressive);
        _recordedNewGameProfile = _settings.NewGameProfile;

        // A profile toggle announces its own change; its tier's count line and the search follow it.
        foreach (var toggle in OptimizedProfileTweaks)
            toggle.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(OptimizedProfileSummary));
                OnPropertyChanged(nameof(SearchScope));
            };
        foreach (var toggle in AggressiveProfileTweaks)
            toggle.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(AggressiveProfileSummary));
                OnPropertyChanged(nameof(SearchScope));
            };

        SelectAllTabCommand = new RelayCommand(() => CurrentSubSection = SystemSubSection.All);
        SelectSpecsTabCommand = new RelayCommand(() => CurrentSubSection = SystemSubSection.HardwareSpecs);
        SelectTweaksTabCommand = new RelayCommand(() => CurrentSubSection = SystemSubSection.PerformanceTweaks);
        SelectGameProfilesTabCommand = new RelayCommand(() => CurrentSubSection = SystemSubSection.GameProfiles);
        RefreshSpecsCommand = new AsyncRelayCommand(LoadHardwareSpecsAsync, () => CanRefreshSpecs);
        ApplyRecommendedPresetCommand = new AsyncRelayCommand(ExecuteApplyPresetAsync, () => CanRunBulkAction);
        ResetDefaultsCommand = new AsyncRelayCommand(ExecuteResetDefaultsAsync, () => CanRunBulkAction);
        RunStutterCheckCommand = new AsyncRelayCommand(RunStutterCheckAsync, () => !IsRunningStutterCheck && CanRunBulkAction);
        CopyStutterResultsCommand = new RelayCommand(CopyStutterResults);

        OpenTaskManagerCommand = new RelayCommand(() => SafeLaunchProcess("taskmgr.exe"));
        OpenDeviceManagerCommand = new RelayCommand(() => SafeLaunchProcess("devmgmt.msc"));
        OpenGraphicsSettingsCommand = new RelayCommand(() => SafeLaunchProcess("ms-settings:display-advancedgraphics"));
        OpenDxDiagCommand = new RelayCommand(() => SafeLaunchProcess("dxdiag.exe"));
        OpenAdvancedDisplaySettingsCommand = new RelayCommand(() => SafeLaunchProcess("ms-settings:display-advanced"));

        // Setup background telemetry ticker (every 3 seconds)
        _telemetryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _telemetryTimer.Tick += OnTelemetryTick;

        // Tweaks and hardware specs are loaded lazily on first visit to the System tab (see
        // MainViewModel.CurrentSection) rather than here, so launching - especially with
        // --minimized - doesn't pay for ~20 registry reads and a hardware/network probe that
        // may never be looked at this session.
    }

    public void StartTelemetry()
    {
        if (!_telemetryTimer.IsEnabled)
            _telemetryTimer.Start();
    }

    public void StopTelemetry()
    {
        if (_telemetryTimer.IsEnabled)
            _telemetryTimer.Stop();
    }

    private void OnTelemetryTick(object? sender, EventArgs e)
    {
        if (!ShowSpecsSection) return;
        if (Application.Current?.MainWindow is { IsVisible: false }) return;

        try
        {
            var (cpuPercent, ram) = _infoService.GetQuickTelemetry();
            if (Cpu != null)
            {
                Cpu.CurrentUsagePercent = cpuPercent;
                OnPropertyChanged(nameof(Cpu));
            }
            if (Ram != null && ram.TotalGigabytes > 0)
            {
                Ram.UsedGigabytes = ram.UsedGigabytes;
                Ram.AvailableGigabytes = ram.AvailableGigabytes;
                Ram.UsagePercent = ram.UsagePercent;
                OnPropertyChanged(nameof(Ram));
            }
            _ = SampleGpusAsync();
        }
        catch
        {
            // Suppress background tick exceptions
        }
    }

    /// <summary>A hardware report has been read at least once this session.</summary>
    public bool HasSpecs => _specsRefreshedAt != null;

    private Task? _specsLoad;
    /// <summary>The heading's Refresh: one read at a time.</summary>
    public bool CanRefreshSpecs => _specsLoad is not { IsCompleted: false };

    /// <summary>Reads the hardware report. A call while a read is in flight waits for that one instead of starting another.</summary>
    public async Task LoadHardwareSpecsAsync()
    {
        if (_specsLoad is { IsCompleted: false })
        {
            await _specsLoad;
            return;
        }
        _specsLoad = LoadHardwareSpecsCoreAsync();
        OnPropertyChanged(nameof(CanRefreshSpecs));
        try
        {
            await _specsLoad;
        }
        finally
        {
            OnPropertyChanged(nameof(CanRefreshSpecs));
        }
    }

    private async Task LoadHardwareSpecsCoreAsync()
    {
        // The "Detecting hardware" placeholder stands in for the cards only while there are no
        // cards yet. A re-detect keeps the cards up and swaps the values in when it finishes, so
        // nothing below them moves.
        IsLoadingSpecs = !HasSpecs;
        StatusMessage = HasSpecs ? "Refreshing hardware specs..." : "Analyzing system hardware...";
        try
        {
            var report = await _infoService.GetFullHardwareReportAsync();
            await CountGamesPerDriveAsync(report);
            Report = report;
            _specsRefreshedAt = DateTime.Now;
            OnPropertyChanged(nameof(HasSpecs));
            OnPropertyChanged(nameof(SpecsRefreshedDisplay));
            // The cards' text changed under any search in progress.
            OnPropertyChanged(nameof(SearchScope));
            // The load counter is a rate: this first read sets the baseline the next tick measures from.
            _ = SampleGpusAsync();
            StatusMessage = $"Hardware refreshed at {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            LoggingService.Error("System", "Error loading specs", ex);
            StatusMessage = "Failed to refresh hardware specifications.";
        }
        finally
        {
            IsLoadingSpecs = false;
        }
    }

    private Task? _tweaksLoad;
    /// <summary>Builds the rows. A call while a load is in flight shares it: two loads would add every row twice.</summary>
    public Task LoadTweaksAsync()
    {
        if (_tweaksLoad is { IsCompleted: false }) return _tweaksLoad;
        return _tweaksLoad = LoadTweaksCoreAsync();
    }

    private async Task LoadTweaksCoreAsync()
    {
        var all = await Task.Run(() => _tweaksService.GetAllTweaks());

        Tweaks.Clear();
        foreach (var item in all)
        {
            Tweaks.Add(new SystemTweakViewModel(item, _tweaksService, msg =>
            {
                StatusMessage = msg;
                NotifyTweakStateChanged();
            }, OnTweakToggled));
        }
        OnPropertyChanged(nameof(TotalTweakCount));
        NotifyTweakStateChanged();
        OnPropertyChanged(nameof(InputAndDisplayTweaks));
        OnPropertyChanged(nameof(CpuAndPowerTweaks));
        OnPropertyChanged(nameof(NetworkAndBackgroundTweaks));
        OnPropertyChanged(nameof(SecurityAndAdvancedTweaks));
        ReportTweaksResetSinceApplied();
    }

    // ---- What TrayTrigger applied, for Activity & History ------------------------------------------

    /// <summary>One tweak switched from its row: remembered, and recorded as a change.</summary>
    private void OnTweakToggled(SystemTweakViewModel tweak, bool nowOptimal)
    {
        UpdateAppliedTweaks([(tweak.Id, nowOptimal)]);
        ActivityService.Add(ActivityLevel.Change,
            nowOptimal ? $"Applied the System tweak \"{tweak.Name}\"" : $"Put back \"{tweak.Name}\" to what it was before",
            groupKey: $"tweak|{tweak.Id}|{nowOptimal}");
    }

    /// <summary>A preset or Restore Previous Settings: what actually changed, as one entry.</summary>
    private void RecordBulkTweakChange(Dictionary<string, bool> before, bool applying)
    {
        var changed = Tweaks.Where(t => before.TryGetValue(t.Id, out bool was) && was != t.IsOptimal).ToList();
        if (changed.Count == 0) return;
        UpdateAppliedTweaks(changed.Select(t => (t.Id, t.IsOptimal)));
        string names = string.Join(", ", changed.Select(t => t.Name));
        ActivityService.Add(ActivityLevel.Change,
            applying ? $"Applied the Performance Preset ({changed.Count} {(changed.Count == 1 ? "setting" : "settings")})"
                     : $"Restored previous settings ({changed.Count} {(changed.Count == 1 ? "setting" : "settings")})",
            detail: names);
    }

    private void UpdateAppliedTweaks(IEnumerable<(string Id, bool Optimal)> changes)
    {
        var applied = new HashSet<string>(_settings.TweaksAppliedByTrayTrigger, StringComparer.Ordinal);
        foreach (var (id, optimal) in changes)
        {
            if (optimal) applied.Add(id); else applied.Remove(id);
        }
        _settings.TweaksAppliedByTrayTrigger = applied.OrderBy(id => id, StringComparer.Ordinal).ToList();
        _storageService.SaveSettings(_settings, source: "SystemViewModel.TweaksApplied");
    }

    /// <summary>
    /// A tweak TrayTrigger applied that reads back at standard now - a Windows update, usually - is
    /// reported once and forgotten, rather than kept as something to dismiss: the user may have
    /// changed it themselves.
    /// </summary>
    private void ReportTweaksResetSinceApplied()
    {
        if (_settings.TweaksAppliedByTrayTrigger.Count == 0 || Tweaks.Count == 0) return;
        var reset = Tweaks.Where(t => _settings.TweaksAppliedByTrayTrigger.Contains(t.Id) && t.IsAvailable && !t.IsOptimal).ToList();
        if (reset.Count == 0) return;
        UpdateAppliedTweaks(reset.Select(t => (t.Id, false)));
        ActivityService.Add(ActivityLevel.Problem,
            reset.Count == 1 ? $"\"{reset[0].Name}\" was changed back since TrayTrigger applied it"
                             : $"{reset.Count} System tweaks were changed back since TrayTrigger applied them",
            detail: $"{string.Join(", ", reset.Select(t => t.Name))}. A Windows update usually does this. Optimize them again on the System page.",
            groupKey: "tweaks.reset");
    }

    /// <summary>
    /// The startup check for tweaks Windows changed back: reads them only when TrayTrigger has
    /// applied some, off the UI thread, and fills the System page's list while it's at it.
    /// </summary>
    public async Task CheckAppliedTweaksAsync()
    {
        if (_settings.TweaksAppliedByTrayTrigger.Count == 0) return;
        if (Tweaks.Count == 0)
        {
            await LoadTweaksAsync();
        }
        else
        {
            await RefreshAllTweaksAsync(statusOnDone: null);
            ReportTweaksResetSinceApplied();
        }
    }

    /// <summary>Re-reads every tweak off the UI thread (GetAllTweaks spawns powercfg and a WMI query).</summary>
    private async Task RefreshAllTweaksAsync(string? statusOnDone = "Checked current Windows settings.")
    {
        var updatedList = await Task.Run(() => _tweaksService.GetAllTweaks());
        foreach (var vm in Tweaks)
        {
            var updated = updatedList.FirstOrDefault(u => u.Id == vm.Id);
            if (updated != null)
            {
                vm.RefreshState(updated);
            }
        }
        OnPropertyChanged(nameof(TotalTweakCount));
        NotifyTweakStateChanged();
        OnPropertyChanged(nameof(InputAndDisplayTweaks));
        OnPropertyChanged(nameof(CpuAndPowerTweaks));
        OnPropertyChanged(nameof(NetworkAndBackgroundTweaks));
        OnPropertyChanged(nameof(SecurityAndAdvancedTweaks));
        if (statusOnDone != null) StatusMessage = statusOnDone;
    }

    private Dictionary<string, bool> SnapshotOptimalState() => Tweaks.ToDictionary(t => t.Id, t => t.IsOptimal, StringComparer.Ordinal);

    private async Task ExecuteApplyPresetAsync()
    {
        var changing = Tweaks.Where(t => t.IsRecommended && !t.IsOptimal).Select(t => t.Name).ToList();
        if (changing.Count == 0)
        {
            StatusMessage = "Every recommended optimization is already active.";
            return;
        }
        if (!ConfirmBulkAction(
            "Apply Performance Preset",
            "This will change the following settings:",
            changing))
        {
            return;
        }

        var before = SnapshotOptimalState();
        IsApplyingTweaks = true;
        try
        {
            var restorePoint = await TryCreateRestorePointAsync("TrayTrigger: Before Performance Preset");

            BusyToastMessage = "Applying recommended performance optimizations...";
            StatusMessage = BusyToastMessage;
            await Task.Run(() => _tweaksService.ApplyRecommendedPerformancePreset());

            await RefreshAllTweaksAsync(BulkActionStatus("Recommended Performance Preset applied.", restorePoint));
            RecordBulkTweakChange(before, applying: true);
        }
        finally
        {
            IsApplyingTweaks = false;
        }
        PromptRestartForBulkAction(before);
        if (HasStutterReport) await RunStutterCheckAsync(afterAction: true);
    }

    private async Task ExecuteResetDefaultsAsync()
    {
        // Undo Preset puts back what Apply Performance Preset turns on: the recommended tweaks
        // that are applied. An opt-in tweak the user switched on themselves is theirs, and has its
        // own Restore Previous on its row. Only applied tweaks are reverted - never a Balanced plan
        // onto a machine that never used the power-plan tweak.
        var applied = Tweaks.Where(t => t.IsRecommended && t.IsOptimal).ToList();
        if (applied.Count == 0)
        {
            StatusMessage = "No preset tweaks are currently applied.";
            return;
        }
        if (!ConfirmBulkAction(
            "Undo Preset",
            "This will put back the following settings to what they were before TrayTrigger changed them:",
            applied.Select(t => t.Name).ToList()))
        {
            return;
        }

        var before = SnapshotOptimalState();
        var ids = applied.Select(t => t.Id).ToList();
        IsApplyingTweaks = true;
        try
        {
            var restorePoint = await TryCreateRestorePointAsync("TrayTrigger: Before Undo Preset");

            BusyToastMessage = "Undoing the preset...";
            StatusMessage = BusyToastMessage;
            await Task.Run(() => _tweaksService.ResetToDefaults(ids));

            await RefreshAllTweaksAsync(BulkActionStatus("Preset undone; previous settings restored.", restorePoint));
            RecordBulkTweakChange(before, applying: false);
        }
        finally
        {
            IsApplyingTweaks = false;
        }
        PromptRestartForBulkAction(before);
        if (HasStutterReport) await RunStutterCheckAsync(afterAction: true);
    }

    /// <summary>What happened to the restore point a preset or restore asked for.</summary>
    public enum RestorePointOutcome { Skipped, Created, Failed }

    private async Task<RestorePointOutcome> TryCreateRestorePointAsync(string description)
    {
        if (!_settings.CreateRestorePointBeforeTweaks) return RestorePointOutcome.Skipped;

        BusyToastMessage = "Creating a System Restore Point before applying changes...";
        StatusMessage = BusyToastMessage;
        bool created = await Task.Run(() => SystemTweaksService.CreateSystemRestorePoint(description));
        if (!created)
        {
            LoggingService.Warn("System", "Could not create a System Restore Point (System Restore may be disabled, throttled by Windows to one per 24h, or elevation was cancelled). Continuing anyway.");
            BusyToastMessage = "Could not create a restore point - continuing...";
            StatusMessage = "Could not create a restore point (may be disabled or already created recently). Continuing...";
            return RestorePointOutcome.Failed;
        }
        return RestorePointOutcome.Created;
    }

    /// <summary>The status line after a preset or restore: what was done, then the restore point's fate.</summary>
    internal static string BulkActionStatus(string done, RestorePointOutcome restorePoint) => $"{done} {RestorePointNote(restorePoint)}";

    internal static string RestorePointNote(RestorePointOutcome outcome) => outcome switch
    {
        RestorePointOutcome.Created => "A restore point was created first.",
        RestorePointOutcome.Failed => "No restore point was created: Windows refused it (System Restore may be off, or one was made in the last 24 hours).",
        _ => "No restore point was made: that option is off in Settings.",
    };

    private bool ConfirmBulkAction(string title, string message, List<string> changingTweakNames)
    {
        if (changingTweakNames.Count == 0) return true;
        string detail = BulkActionDetail(changingTweakNames, _settings.CreateRestorePointBeforeTweaks);
        return ModernDialog.Confirm(null, title, message, detail, confirmText: "Continue", cancelText: "Cancel");
    }

    /// <summary>
    /// The dialog's small print: what changes, and whether a System Restore point is made first.
    /// The restore point preference lives in Settings and is said here, where it matters, rather
    /// than on the card.
    /// </summary>
    internal static string BulkActionDetail(IReadOnlyList<string> changingTweakNames, bool restorePointOn) =>
        "Affected: " + string.Join(", ", changingTweakNames) +
        ". This may require admin approval and a restart to fully take effect. " +
        (restorePointOn
            ? "A System Restore point will be created first."
            : "No System Restore point will be created first; turn that on in Settings > Performance Tweaks.");

    /// <summary>Prompts for a restart only when a reboot-required tweak actually changed state in this run.</summary>
    private void PromptRestartForBulkAction(Dictionary<string, bool> before)
    {
        var affected = Tweaks
            .Where(t => SystemTweaksService.RebootRequiredTweakIds.Contains(t.Id))
            .Where(t => !before.TryGetValue(t.Id, out bool was) || was != t.IsOptimal)
            .Select(t => t.Name)
            .ToList();

        if (affected.Count == 0) return;

        bool restartNow = ModernDialog.PromptRestart(null,
            $"Some of the applied settings ({string.Join(", ", affected)}) only take effect after a restart.");
        if (restartNow)
        {
            SystemTweakViewModel.RestartNow();
        }
    }

    private static void SafeLaunchProcess(string target)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LoggingService.Warn("System", $"Could not launch utility '{target}': {ex.Message}");
        }
    }
}

/// <summary>One Stutter Check row that needs attention: what was found, its badge, and its one button.</summary>
public sealed class StutterCheckRowViewModel
{
    public StutterCheckRowViewModel(StutterCheckItem item, Action<StutterCheckItem> act)
    {
        Item = item;
        ActionCommand = new RelayCommand(() => act(item));
    }

    public StutterCheckItem Item { get; }
    public string Title => Item.Title;
    public string Detail => Item.Detail;
    public bool HasDetail => Item.Detail.Length > 0;
    public bool HasAction => Item.Action != StutterAction.None && Item.ActionLabel.Length > 0;
    public string ActionLabel => Item.ActionLabel;
    /// <summary>Fix it is the accent button; Here's how and the Open buttons are outline.</summary>
    public bool IsPrimaryAction => HasAction && Item.Action == StutterAction.ApplyTweak;
    public bool IsSecondaryAction => HasAction && Item.Action != StutterAction.ApplyTweak;
    public ICommand ActionCommand { get; }
    /// <summary>Info rows are dimmed: worth knowing, nothing to do.</summary>
    public double RowOpacity => Item.Verdict == StutterVerdict.Info ? 0.65 : 1.0;

    public string BadgeText => Item.Verdict switch
    {
        StutterVerdict.NeedsLook => "NEEDS A LOOK",
        StutterVerdict.CanFix => "TRAYTRIGGER CAN FIX",
        StutterVerdict.Info => "INFO",
        _ => "FINE",
    };
    public string BadgeBackground => Item.Verdict switch
    {
        StutterVerdict.NeedsLook => "#3D2F14",
        StutterVerdict.CanFix => "#14304D",
        StutterVerdict.Info => "#2A2A3A",
        _ => "#238636",
    };
    public string BadgeForeground => Item.Verdict switch
    {
        StutterVerdict.NeedsLook => "#E8B84E",
        StutterVerdict.CanFix => "#6CB6FF",
        StutterVerdict.Info => "#B8B8CC",
        _ => "#FFFFFF",
    };
    public string AccessibleName => Item.Verdict switch
    {
        StutterVerdict.NeedsLook => "Needs a look",
        StutterVerdict.CanFix => "TrayTrigger can fix",
        StutterVerdict.Info => "Info",
        _ => "Fine",
    } + $": {Title}";
}
