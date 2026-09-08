using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.Views;

namespace TrayTrigger.ViewModels;

/// <summary>
/// Owns self-update check/status state, the "Check for Updates" command, and the periodic
/// background update check. Split out of MainViewModel per L-13.
/// </summary>
public class UpdateCoordinator : ViewModelBase
{
    private readonly StorageService _storageService;
    private readonly AppSettings _settings;
    private readonly Action? _onStatusChanged;
    private readonly Action<string, string>? _onTrayNotification;

    public UpdateCoordinator(
        StorageService storageService,
        AppSettings settings,
        Action? onStatusChanged = null,
        Action<string, string>? onTrayNotification = null)
    {
        _storageService = storageService;
        _settings = settings;
        _onStatusChanged = onStatusChanged;
        _onTrayNotification = onTrayNotification;

        CheckForUpdatesCommand = new AsyncRelayCommand(async () => await CheckForUpdatesAsync(true));
    }

    public ICommand CheckForUpdatesCommand { get; }

    public string AppVersionDisplay => UpdateService.CurrentVersionDisplay;

    private string _updateStatusBadgeText = $"{UpdateService.CurrentVersionDisplay} • Up to date";
    public string UpdateStatusBadgeText
    {
        get => _updateStatusBadgeText;
        set
        {
            if (SetProperty(ref _updateStatusBadgeText, value))
            {
                // Settings tab shows the same status text via SettingsVM; see L-10.
                _onStatusChanged?.Invoke();
            }
        }
    }

    private string _updateStatusIcon = ""; // Segoe checkmark
    public string UpdateStatusIcon
    {
        get => _updateStatusIcon;
        set => SetProperty(ref _updateStatusIcon, value);
    }

    private Brush _updateStatusBrush = new SolidColorBrush(Color.FromRgb(78, 201, 176)); // #4EC9B0
    public Brush UpdateStatusBrush
    {
        get => _updateStatusBrush;
        set => SetProperty(ref _updateStatusBrush, value);
    }

    public async Task CheckForUpdatesAsync(bool interactive)
    {
        try
        {
            UpdateStatusBadgeText = "Checking for updates...";
            UpdateStatusIcon = "";
            if (Application.Current?.TryFindResource("BrushAccentHover") is Brush accentBrush)
            {
                UpdateStatusBrush = accentBrush;
            }

            string repo = string.IsNullOrWhiteSpace(_settings.GitHubRepository) ? "stephenh678/TrayTrigger" : _settings.GitHubRepository.Trim();
            var result = await UpdateService.Instance.CheckForUpdatesAsync(repo, _settings.IncludePrereleaseUpdates);

            if (result.IsUpdateAvailable && result.LatestRelease != null)
            {
                UpdateStatusBadgeText = result.LatestRelease.Prerelease
                    ? $"{result.LatestRelease.TagName} pre-release available!"
                    : $"{result.LatestRelease.TagName} available!";
                UpdateStatusIcon = "";
                if (Application.Current?.TryFindResource("BrushAccentHover") is Brush acBrush)
                {
                    UpdateStatusBrush = acBrush;
                }

                bool mainWindowVisible = Application.Current?.MainWindow is { IsVisible: true };
                bool alreadySnoozed = interactive == false &&
                    string.Equals(_settings.SkippedUpdateVersion, result.LatestRelease.TagName, StringComparison.OrdinalIgnoreCase) &&
                    _settings.RemindAfterUtc.HasValue && DateTime.UtcNow < _settings.RemindAfterUtc.Value;

                if (interactive || mainWindowVisible)
                {
                    // Surface the modal when the user explicitly asked (interactive), or when a
                    // quiet background check (startup / 24h timer) finds the window is actually
                    // visible - a silently-updated badge alone is easy to miss in that case.
                    Window? owner = WindowHelper.ActiveOwner();
                    bool remindLater = UpdateDialog.ShowUpdateDialog(owner, result.LatestRelease, result.CurrentVersion);
                    if (remindLater)
                    {
                        _settings.SkippedUpdateVersion = result.LatestRelease.TagName;
                        _settings.RemindAfterUtc = DateTime.UtcNow.AddHours(24);
                        _storageService.SaveSettings(_settings);
                    }
                }
                else if (!alreadySnoozed)
                {
                    // Background check, window hidden (e.g. a full-screen game): a modal dialog
                    // here would steal focus mid-match. Use a tray balloon instead so the user
                    // isn't interrupted but still finds out.
                    _onTrayNotification?.Invoke("Update Available", $"{result.LatestRelease.TagName} is ready to install. Open TrayTrigger to update.");
                }
            }
            else if (result.IsUpToDate)
            {
                UpdateStatusBadgeText = $"{UpdateService.CurrentVersionDisplay} • Up to date";
                UpdateStatusIcon = "";
                UpdateStatusBrush = new SolidColorBrush(Color.FromRgb(78, 201, 176));

                if (interactive)
                {
                    Window? owner = WindowHelper.ActiveOwner();
                    ModernDialog.ShowInfo(
                        owner,
                        "Check for Updates",
                        "TrayTrigger is Up to Date",
                        $"You are currently running the latest version ({UpdateService.CurrentVersionDisplay}). No updates are available.");
                }
            }
            else if (result.Status == UpdateStatus.NoReleasesFound)
            {
                UpdateStatusBadgeText = $"{UpdateService.CurrentVersionDisplay} • Up to date";
                UpdateStatusIcon = "";
                UpdateStatusBrush = new SolidColorBrush(Color.FromRgb(78, 201, 176));

                if (interactive)
                {
                    Window? owner = WindowHelper.ActiveOwner();
                    ModernDialog.ShowInfo(
                        owner,
                        "Check for Updates",
                        "No Releases Found on GitHub",
                        $"No published releases were found for repository '{repo}'. Once you create a release on GitHub, updates will appear here.");
                }
            }
            else
            {
                UpdateStatusBadgeText = $"{UpdateService.CurrentVersionDisplay} • Check failed";
                UpdateStatusIcon = "";
                UpdateStatusBrush = new SolidColorBrush(Color.FromRgb(224, 108, 117));

                if (interactive)
                {
                    Window? owner = WindowHelper.ActiveOwner();
                    ModernDialog.ShowWarning(
                        owner,
                        "Check for Updates",
                        "Unable to Check for Updates",
                        result.ErrorMessage ?? "Please verify your internet connection and GitHub repository setting.");
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error("UpdateCoordinator", "CheckForUpdatesAsync error", ex);
            UpdateStatusBadgeText = $"{UpdateService.CurrentVersionDisplay} • Check failed";
            UpdateStatusIcon = "";
            UpdateStatusBrush = new SolidColorBrush(Color.FromRgb(224, 108, 117));

            if (interactive)
            {
                Window? owner = WindowHelper.ActiveOwner();
                ModernDialog.ShowWarning(
                    owner,
                    "Check for Updates",
                    "Error Checking for Updates",
                    ex.Message);
            }
        }
    }

    /// <summary>
    /// Schedule quiet background checks for updates if enabled: once shortly after launch, then
    /// again every 24 hours for as long as the app keeps running.
    /// </summary>
    public void StartBackgroundChecks()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3500);
                while (true)
                {
                    if (_settings.AutoCheckForUpdates)
                    {
                        Application.Current?.Dispatcher.InvokeAsync(async () =>
                        {
                            await CheckForUpdatesAsync(false);
                        });
                    }
                    await Task.Delay(TimeSpan.FromHours(24));
                }
            }
            catch
            {
                // Ignore silent update check exceptions
            }
        });
    }
}
