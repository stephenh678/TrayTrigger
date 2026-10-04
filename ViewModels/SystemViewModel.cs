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
        : IsOptimal ? "OPTIMAL" : "STANDARD";
    public string StatusBadgeColor => IsInformational ? "#2F5F8F"
        : !IsAvailable ? "#4A4A55"
        : IsOptimal ? "#238636" : "#6E6E7A";
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
    private async Task ExecuteToggleAsync()
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
                OnPropertyChanged(nameof(RestorePointBadgeText));
                OnPropertyChanged(nameof(RestorePointBadgeColor));
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
    private IEnumerable<SystemTweakViewModel> ForCategory(TweakCategory category) =>
        Tweaks.Where(t => t.Category == category).OrderBy(t => t.IsOptIn ? 1 : 0);

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
        return $"{Plural(tweaks.Count, "tweak")} · {tweaks.Count(t => t.IsOptimal)} optimal";
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

    // Restore Point Protection status - read-only here; configured in Settings > Performance Tweaks.
    // A preference, not a result: whether a restore point is attempted before a preset or restore.
    // What actually happened is reported in the status line afterwards (see RestorePointNote).
    public string RestorePointBadgeText => _settings.CreateRestorePointBeforeTweaks ? "Restore point before changes: On" : "Restore point before changes: Off";
    public string RestorePointBadgeColor => _settings.CreateRestorePointBeforeTweaks ? "#238636" : "#6E6E7A";

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
                "Switches to a full-clock power plan (no core parking, no PCIe/USB power saving) while the game runs, then switches back.",
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
                OnPropertyChanged(nameof(SpecsRefreshedDisplay));
                OnPropertyChanged(nameof(SearchScope));
            }
        }
    }

    /// <summary>Inverse of <see cref="IsLoadingSpecs"/> for the spec cards' visibility.</summary>
    public bool IsSpecsLoaded => !IsLoadingSpecs;

    private DateTime? _specsRefreshedAt;
    /// <summary>"Last read 14:02:11" beside the Refresh Specs button; empty while loading, when the
    /// placeholder card already says "Detecting hardware…".</summary>
    public string SpecsRefreshedDisplay => IsLoadingSpecs ? ""
        : _specsRefreshedAt is DateTime t ? $"Last read {t:HH:mm:ss}" : "";

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
    public ICommand RefreshTweaksCommand { get; }
    public ICommand ApplyRecommendedPresetCommand { get; }
    public ICommand ResetDefaultsCommand { get; }

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
        RefreshSpecsCommand = new AsyncRelayCommand(async () => await LoadHardwareSpecsAsync());
        RefreshTweaksCommand = new RelayCommand(RefreshAllTweaks);
        ApplyRecommendedPresetCommand = new AsyncRelayCommand(ExecuteApplyPresetAsync, () => CanRunBulkAction);
        ResetDefaultsCommand = new AsyncRelayCommand(ExecuteResetDefaultsAsync, () => CanRunBulkAction);

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

    public async Task LoadHardwareSpecsAsync()
    {
        IsLoadingSpecs = true;
        StatusMessage = "Analyzing system hardware...";
        try
        {
            var report = await _infoService.GetFullHardwareReportAsync();
            await CountGamesPerDriveAsync(report);
            Report = report;
            _specsRefreshedAt = DateTime.Now;
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

    public async Task LoadTweaksAsync()
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
        OnPropertyChanged(nameof(RestorePointBadgeText));
        OnPropertyChanged(nameof(RestorePointBadgeColor));
        if (statusOnDone != null) StatusMessage = statusOnDone;
    }

    private void RefreshAllTweaks() => _ = RefreshAndCheckTweaksAsync();

    private async Task RefreshAndCheckTweaksAsync()
    {
        await RefreshAllTweaksAsync();
        ReportTweaksResetSinceApplied();
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
    }

    private async Task ExecuteResetDefaultsAsync()
    {
        // Only tweaks that are actually applied are reverted - never a Balanced plan onto a
        // machine that never used the power-plan tweak, or animations back on for someone who
        // turned them off in Windows themselves.
        var applied = Tweaks.Where(t => t.CanToggle && !t.IsInformational && t.IsOptimal).ToList();
        if (applied.Count == 0)
        {
            StatusMessage = "No TrayTrigger optimizations are currently applied.";
            return;
        }
        if (!ConfirmBulkAction(
            "Restore Previous Settings",
            "This will restore the following settings to what they were before TrayTrigger changed them:",
            applied.Select(t => t.Name).ToList()))
        {
            return;
        }

        var before = SnapshotOptimalState();
        var ids = applied.Select(t => t.Id).ToList();
        IsApplyingTweaks = true;
        try
        {
            var restorePoint = await TryCreateRestorePointAsync("TrayTrigger: Before Restore Previous Settings");

            BusyToastMessage = "Restoring previous settings...";
            StatusMessage = BusyToastMessage;
            await Task.Run(() => _tweaksService.ResetToDefaults(ids));

            await RefreshAllTweaksAsync(BulkActionStatus("Previous settings restored.", restorePoint));
            RecordBulkTweakChange(before, applying: false);
        }
        finally
        {
            IsApplyingTweaks = false;
        }

        PromptRestartForBulkAction(before);
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

    private static bool ConfirmBulkAction(string title, string message, List<string> changingTweakNames)
    {
        if (changingTweakNames.Count == 0) return true;

        string detail = "Affected: " + string.Join(", ", changingTweakNames) +
            ". This may require admin approval and a restart to fully take effect.";

        return ModernDialog.Confirm(null, title, message, detail, confirmText: "Continue", cancelText: "Cancel");
    }

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
