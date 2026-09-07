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
    PerformanceTweaks
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
    public string CustomActionLabel => Model.CustomActionLabel;

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
        bool success = _service.ApplyTweak(Id, targetState);
        if (success)
        {
            IsOptimal = targetState;
            StatusText = targetState ? "Optimal configuration applied" : "Reverted to standard Windows default";
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
            _notifyParent($"Failed to update '{Name}'. Administrator privileges may be required.");
        }
    }

    internal static void RestartNow()
    {
        try
        {
            using var proc = Process.Start("shutdown.exe", "/r /t 10");
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

public class SystemViewModel : ViewModelBase
{
    private readonly SystemInfoService _infoService;
    private readonly SystemTweaksService _tweaksService;
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
                OnPropertyChanged(nameof(ShowSpecsSection));
                OnPropertyChanged(nameof(ShowTweaksSection));
            }
        }
    }

    public bool IsAllTab => CurrentSubSection == SystemSubSection.All;
    public bool IsSpecsTab => CurrentSubSection == SystemSubSection.HardwareSpecs;
    public bool IsTweaksTab => CurrentSubSection == SystemSubSection.PerformanceTweaks;

    public bool ShowSpecsSection => CurrentSubSection == SystemSubSection.All || CurrentSubSection == SystemSubSection.HardwareSpecs;
    public bool ShowTweaksSection => CurrentSubSection == SystemSubSection.All || CurrentSubSection == SystemSubSection.PerformanceTweaks;

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
    public ICommand RefreshSpecsCommand { get; }
    public ICommand RefreshTweaksCommand { get; }
    public ICommand ApplyRecommendedPresetCommand { get; }
    public ICommand ResetDefaultsCommand { get; }

    // Quick Tools Commands
    public ICommand OpenTaskManagerCommand { get; }
    public ICommand OpenDeviceManagerCommand { get; }
    public ICommand OpenGraphicsSettingsCommand { get; }
    public ICommand OpenDxDiagCommand { get; }

    public SystemViewModel(SystemInfoService infoService, SystemTweaksService tweaksService)
    {
        _infoService = infoService;
        _tweaksService = tweaksService;

        SelectAllTabCommand = new RelayCommand(() => CurrentSubSection = SystemSubSection.All);
        SelectSpecsTabCommand = new RelayCommand(() => CurrentSubSection = SystemSubSection.HardwareSpecs);
        SelectTweaksTabCommand = new RelayCommand(() => CurrentSubSection = SystemSubSection.PerformanceTweaks);
        RefreshSpecsCommand = new RelayCommand(async () => await LoadHardwareSpecsAsync());
        RefreshTweaksCommand = new RelayCommand(RefreshAllTweaks);
        ApplyRecommendedPresetCommand = new RelayCommand(ExecuteApplyPreset);
        ResetDefaultsCommand = new RelayCommand(ExecuteResetDefaults);

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

        // Initialize tweaks collection
        LoadTweaks();

        // Initial async load
        _ = LoadHardwareSpecsAsync();
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

    private void LoadTweaks()
    {
        Tweaks.Clear();
        var all = _tweaksService.GetAllTweaks();
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
        StatusMessage = "Checked current Windows settings.";
    }

    private void ExecuteApplyPreset()
    {
        var changing = Tweaks.Where(t => t.CanToggle && !t.IsOptimal).Select(t => t.Name).ToList();
        if (!ConfirmBulkAction(
            "Apply Performance Preset",
            "This will change the following settings:",
            changing))
        {
            return;
        }

        StatusMessage = "Applying recommended performance optimizations...";
        _tweaksService.ApplyRecommendedPerformancePreset();
        RefreshAllTweaks();
        StatusMessage = "Recommended Performance Preset applied successfully!";
        PromptRestartForBulkAction();
    }

    private void ExecuteResetDefaults()
    {
        var changing = Tweaks.Where(t => t.CanToggle && t.IsOptimal).Select(t => t.Name).ToList();
        if (!ConfirmBulkAction(
            "Reset Defaults",
            "This will restore the following settings to their Windows defaults:",
            changing))
        {
            return;
        }

        StatusMessage = "Resetting optimizations to standard Windows defaults...";
        _tweaksService.ResetAllToDefaults();
        RefreshAllTweaks();
        StatusMessage = "Reset all settings to Windows defaults.";
        PromptRestartForBulkAction();
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
