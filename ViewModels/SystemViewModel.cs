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

    public string StatusBadgeText => IsOptimal ? "OPTIMAL" : "STANDARD";
    public string StatusBadgeColor => IsOptimal ? "#238636" : "#6E6E7A";
    public string ActionButtonText => IsOptimal ? "Revert to Default" : "Optimize";

    public ICommand ToggleCommand { get; }
    public ICommand CustomActionCommand { get; }

    public SystemTweakViewModel(SystemTweakItem model, SystemTweaksService service, Action<string> notifyParent)
    {
        Model = model;
        _service = service;
        _notifyParent = notifyParent;
        _isOptimal = model.IsOptimal;
        _statusText = model.StatusText;

        ToggleCommand = new RelayCommand(ExecuteToggle, () => CanToggle);
        CustomActionCommand = new RelayCommand(ExecuteCustomAction);
    }

    private void ExecuteToggle()
    {
        bool targetState = !IsOptimal;
        _service.ApplyTweak(Id, targetState);

        // Trust a fresh read of the real system state over ApplyTweak's own return value: an
        // elevated write can report "failed" (e.g. a slow UAC prompt) while it actually went
        // through moments later, or report "succeeded" without the underlying state matching.
        bool actualState = _service.GetTweakState(Id);
        IsOptimal = actualState;
        StatusText = actualState ? "Optimal configuration applied" : "Reverted to standard Windows default";

        if (actualState == targetState)
        {
            _notifyParent($"Toggled '{Name}' to {(targetState ? "Optimal" : "Default")}.");

            if (RequiresReboot)
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
            LoggingService.Warn("SystemTweakViewModel", $"Toggle '{Name}' ({Id}) to {(targetState ? "Optimal" : "Default")} did not take effect - actual state read back as {(actualState ? "Optimal" : "Default")}.");
            _notifyParent($"Failed to update '{Name}'. Administrator privileges may be required.");
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
            LoggingService.Warn("SystemTweakViewModel", $"Failed to initiate restart: {ex.Message}");
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
            catch
            {
                try { using var proc = Process.Start(new ProcessStartInfo("ms-settings:privacy") { UseShellExecute = true }); } catch { }
            }
        }
        else if (Id == "hags")
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo("ms-settings:display-advancedgraphics") { UseShellExecute = true });
            }
            catch { }
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

    /// <summary>"Learn more" target, e.g. "profiles/power_plan" -> Help/profiles/power_plan.md.</summary>
    public string HelpTopicId { get; }

    public string StatusBadgeText => IsEnabled ? "ENABLED" : "DISABLED";
    public string StatusBadgeColor => IsEnabled ? "#238636" : "#6E6E7A";
    public string ActionButtonText => IsEnabled ? "Disable" : "Enable";

    public ICommand ToggleCommand { get; }

    public ProfileTweakToggleViewModel(string name, string shortDescription, string whyItMatters, string helpTopicId, Func<bool> getter, Action<bool> setter, bool isOptIn = false)
    {
        Name = name;
        ShortDescription = shortDescription;
        WhyItMatters = whyItMatters;
        HelpTopicId = helpTopicId;
        _getter = getter;
        _setter = setter;
        IsOptIn = isOptIn;
        ToggleCommand = new RelayCommand(() => IsEnabled = !IsEnabled);
    }

    public bool IsEnabled
    {
        get => _getter();
        set
        {
            if (_getter() != value)
            {
                _setter(value);
                LoggingService.Info("SystemViewModel", $"Performance Profile tweak '{Name}' {(value ? "enabled" : "disabled")}.");
                NotifyStateChanged();
            }
        }
    }

    /// <summary>Re-reads the live config value into the bindings - for when the underlying
    /// setting was changed from elsewhere (Settings → Reset to Defaults).</summary>
    public void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(StatusBadgeText));
        OnPropertyChanged(nameof(StatusBadgeColor));
        OnPropertyChanged(nameof(ActionButtonText));
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
            }
        }
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
    public GpuHardwareInfo PrimaryGpu => Report.Gpus.FirstOrDefault() ?? new GpuHardwareInfo();
    public List<GpuHardwareInfo> GpuList => Report.Gpus;
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
    public IEnumerable<SystemTweakViewModel> InputAndDisplayTweaks => Tweaks.Where(t => t.Category == TweakCategory.InputAndDisplay);
    public IEnumerable<SystemTweakViewModel> CpuAndPowerTweaks => Tweaks.Where(t => t.Category == TweakCategory.CpuAndPower);
    public IEnumerable<SystemTweakViewModel> NetworkAndBackgroundTweaks => Tweaks.Where(t => t.Category == TweakCategory.NetworkAndBackground);
    public IEnumerable<SystemTweakViewModel> SecurityAndAdvancedTweaks => Tweaks.Where(t => t.Category == TweakCategory.SecurityAndAdvanced);

    // Optimal count summary
    public int OptimalTweakCount => Tweaks.Count(t => t.IsOptimal);
    public int TotalTweakCount => Tweaks.Count;
    public string TweaksOptimizationScoreDisplay => $"{OptimalTweakCount} / {TotalTweakCount} Optimizations Active";

    // Restore Point Protection status - read-only here; configured in Settings > Performance Tweaks.
    public string RestorePointBadgeText => _settings.CreateRestorePointBeforeTweaks ? "RESTORE POINT: ON" : "RESTORE POINT: OFF";
    public string RestorePointBadgeColor => _settings.CreateRestorePointBeforeTweaks ? "#238636" : "#6E6E7A";

    // Game-Level Performance Profiles: per-game Optimized/Aggressive tweak sets (see
    // GameEditDialog). Aggressive always applies every tweak Optimized has enabled, plus its own
    // extras below - a tweak is only ever configured once, under whichever tier introduces it.
    // Add a new bool to OptimizedProfileTweakConfig or AggressiveProfileTweakConfig (plus a
    // matching apply/restore pair in PerformanceProfileService) and one more toggle entry below
    // when a new profile tweak ships.
    public ObservableCollection<ProfileTweakToggleViewModel> OptimizedProfileTweaks { get; }
    public ObservableCollection<ProfileTweakToggleViewModel> AggressiveProfileTweaks { get; }

    private ObservableCollection<ProfileTweakToggleViewModel> BuildOptimizedProfileToggles(OptimizedProfileTweakConfig config)
    {
        void Save() => _storageService.SaveSettings(_settings);

        return new ObservableCollection<ProfileTweakToggleViewModel>
        {
            new("\"Ultimate Plan - TrayTrigger\" Power Plan",
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
        };
    }

    private ObservableCollection<ProfileTweakToggleViewModel> BuildAggressiveProfileToggles(AggressiveProfileTweakConfig config)
    {
        void Save() => _storageService.SaveSettings(_settings);

        return new ObservableCollection<ProfileTweakToggleViewModel>
        {
            new("System Responsiveness (MMCSS Gaming Reserve)",
                "Reduces the CPU reserve for lower-priority MMCSS tasks to the supported minimum.",
                "Windows reserves 20% of CPU resources for low-priority background tasks by default. Microsoft's MMCSS documentation clamps any value below 10 back up to 20, so 10 is the lowest reserve Windows actually honors - it leaves more scheduling headroom for latency-sensitive foreground workloads like games.",
                "profiles/system_responsiveness",
                () => config.SystemResponsivenessEnabled,
                v => { config.SystemResponsivenessEnabled = v; Save(); }),
            new("MMCSS \"Games\" Task Scheduling Tuning",
                "Raises the Multimedia Class Scheduler's built-in \"Games\" task from its Medium default to High.",
                "Officially documented by Microsoft, MMCSS grants time-sensitive threads registered under the \"Games\" task category prioritized CPU access - the same mechanism game engines request via AvSetMmThreadCharacteristics. Windows ships this task at Scheduling Category=Medium by default; raising it to High uses the same sanctioned mechanism with more headroom. This is scheduling tuning, not a guaranteed FPS boost - the effect depends on what else is contending for the CPU. (Other fields some optimizer tools also touch here, like SFIO Priority, are documented by Microsoft as not used, so this tweak leaves them alone.)",
                "profiles/mmcss_games_priority",
                () => config.MmcssGamesPriorityEnabled,
                v => { config.MmcssGamesPriorityEnabled = v; Save(); }),
            new("Above Normal Process Priority",
                "Raises the game's own process to Above Normal CPU scheduling priority for the duration of the session.",
                "A real Windows scheduling class (SetPriorityClass), not a registry trick. Community benchmarking consistently finds Above Normal reduces worst-case frame-time stutters with low risk, while pushing further to High priority shows only marginal extra gain and a real risk of starving audio/input threads. Effect varies by game and is not guaranteed. Only takes effect for direct .exe launches - TrayTrigger has no handle to the actual game process for Steam-launched games, so this silently does nothing for those.",
                "profiles/above_normal_priority",
                () => config.AboveNormalPriorityEnabled,
                v => { config.AboveNormalPriorityEnabled = v; Save(); }),
            new("Windows Defender Exclusion for Game Files",
                "Excludes the game's executable from Microsoft Defender real-time scanning while it's running.",
                "Real, Microsoft-supported mechanism (Add-MpPreference), not a workaround - and it measurably reduces CPU/I-O hitches during shader compilation and asset streaming on some games. This is a genuine security tradeoff, not just a performance one: it narrows antivirus coverage for that specific file while the profile is active. Defaults off even under Aggressive for that reason - only enable it if you understand and accept the tradeoff. Only applies when TrayTrigger knows the game's real executable path (not Steam launches). If the path was already excluded before this ran, that exclusion is left alone on restore.",
                "profiles/defender_exclusion",
                () => config.DefenderExclusionEnabled,
                v => { config.DefenderExclusionEnabled = v; Save(); },
                isOptIn: true),
        };
    }

    // Busy state for the Apply Preset / Reset Defaults bulk actions. These can take anywhere
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
            }
        }
    }

    private string _statusMessage = "Ready";
    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (_statusMessage != value)
            {
                _statusMessage = value;
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

    /// <summary>
    /// Refreshes every profile-tweak toggle from the live settings. The toggles read straight
    /// from the config objects they were built against (which Settings → Reset to Defaults now
    /// resets in place rather than replacing), so only their change notifications are needed.
    /// </summary>
    public void RefreshProfileTweakToggles()
    {
        foreach (var t in OptimizedProfileTweaks) t.NotifyStateChanged();
        foreach (var t in AggressiveProfileTweaks) t.NotifyStateChanged();
    }

    public SystemViewModel(SystemInfoService infoService, SystemTweaksService tweaksService, AppSettings settings, StorageService storageService)
    {
        _infoService = infoService;
        _tweaksService = tweaksService;
        _settings = settings;
        _storageService = storageService;

        OptimizedProfileTweaks = BuildOptimizedProfileToggles(_settings.OptimizedProfileTweaks);
        AggressiveProfileTweaks = BuildAggressiveProfileToggles(_settings.AggressiveProfileTweaks);

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
            Report = await _infoService.GetFullHardwareReportAsync();
            StatusMessage = $"Hardware refreshed at {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            LoggingService.Error("SystemViewModel", "Error loading specs", ex);
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
                OnPropertyChanged(nameof(OptimalTweakCount));
                OnPropertyChanged(nameof(TweaksOptimizationScoreDisplay));
            }));
        }
        OnPropertyChanged(nameof(OptimalTweakCount));
        OnPropertyChanged(nameof(TotalTweakCount));
        OnPropertyChanged(nameof(TweaksOptimizationScoreDisplay));
        OnPropertyChanged(nameof(InputAndDisplayTweaks));
        OnPropertyChanged(nameof(CpuAndPowerTweaks));
        OnPropertyChanged(nameof(NetworkAndBackgroundTweaks));
        OnPropertyChanged(nameof(SecurityAndAdvancedTweaks));
    }

    private void RefreshAllTweaks()
    {
        var updatedList = _tweaksService.GetAllTweaks();
        foreach (var vm in Tweaks)
        {
            var updated = updatedList.FirstOrDefault(u => u.Id == vm.Id);
            if (updated != null)
            {
                vm.RefreshState(updated);
            }
        }
        OnPropertyChanged(nameof(OptimalTweakCount));
        OnPropertyChanged(nameof(TweaksOptimizationScoreDisplay));
        OnPropertyChanged(nameof(RestorePointBadgeText));
        OnPropertyChanged(nameof(RestorePointBadgeColor));
        StatusMessage = "Checked current Windows settings.";
    }

    private async Task ExecuteApplyPresetAsync()
    {
        var changing = Tweaks.Where(t => t.CanToggle && !t.IsOptimal).Select(t => t.Name).ToList();
        if (!ConfirmBulkAction(
            "Apply Performance Preset",
            "This will change the following settings:",
            changing))
        {
            return;
        }

        IsApplyingTweaks = true;
        try
        {
            await TryCreateRestorePointAsync("TrayTrigger: Before Performance Preset");

            BusyToastMessage = "Applying recommended performance optimizations...";
            StatusMessage = BusyToastMessage;
            await Task.Run(() => _tweaksService.ApplyRecommendedPerformancePreset());

            RefreshAllTweaks();
            StatusMessage = "Recommended Performance Preset applied successfully!";
        }
        finally
        {
            IsApplyingTweaks = false;
        }

        PromptRestartForBulkAction();
    }

    private async Task ExecuteResetDefaultsAsync()
    {
        var changing = Tweaks.Where(t => t.CanToggle && t.IsOptimal).Select(t => t.Name).ToList();
        if (!ConfirmBulkAction(
            "Reset Defaults",
            "This will restore the following settings to their Windows defaults:",
            changing))
        {
            return;
        }

        IsApplyingTweaks = true;
        try
        {
            await TryCreateRestorePointAsync("TrayTrigger: Before Reset to Defaults");

            BusyToastMessage = "Resetting optimizations to standard Windows defaults...";
            StatusMessage = BusyToastMessage;
            await Task.Run(() => _tweaksService.ResetAllToDefaults());

            RefreshAllTweaks();
            StatusMessage = "Reset all settings to Windows defaults.";
        }
        finally
        {
            IsApplyingTweaks = false;
        }

        PromptRestartForBulkAction();
    }

    private async Task TryCreateRestorePointAsync(string description)
    {
        if (!_settings.CreateRestorePointBeforeTweaks) return;

        BusyToastMessage = "Creating a System Restore Point before applying changes...";
        StatusMessage = BusyToastMessage;
        bool created = await Task.Run(() => SystemTweaksService.CreateSystemRestorePoint(description));
        if (!created)
        {
            LoggingService.Warn("SystemViewModel", "Could not create a System Restore Point (System Restore may be disabled, throttled by Windows to one per 24h, or elevation was cancelled). Continuing anyway.");
            BusyToastMessage = "Could not create a restore point - continuing...";
            StatusMessage = "Could not create a restore point (may be disabled or already created recently). Continuing...";
        }
    }

    private static bool ConfirmBulkAction(string title, string message, List<string> changingTweakNames)
    {
        if (changingTweakNames.Count == 0) return true;

        string detail = "Affected: " + string.Join(", ", changingTweakNames) +
            ". This may require admin approval and a restart to fully take effect.";

        return ModernDialog.Confirm(null, title, message, detail, confirmText: "Continue", cancelText: "Cancel");
    }

    private void PromptRestartForBulkAction()
    {
        var affected = Tweaks
            .Where(t => SystemTweaksService.RebootRequiredTweakIds.Contains(t.Id))
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
            LoggingService.Warn("SystemViewModel", $"Could not launch utility '{target}': {ex.Message}");
        }
    }
}
