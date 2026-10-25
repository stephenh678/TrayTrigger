using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.Views;

namespace TrayTrigger.ViewModels;

/// <summary>Saving and resetting settings, backup and restore, and the storage and log folders.</summary>
public partial class SettingsViewModel
{
    // --- Operations ---

    public void AutoSaveSettings([CallerMemberName] string callerMember = "", [CallerFilePath] string callerFile = "")
    {
        _storageService.SaveSettings(_settings, callerMember, callerFile);
        LoggingService.Verbose("Settings", "Settings auto-saved.");
    }

    public void SaveInWindowSettings()
    {
        _storageService.SaveSettings(_settings);
        _startupManager.SetStartupEnabled(StartWithWindows, StartMinimizedToTray);

        if (AlwaysShowTrayIcon)
        {
            _trayPromotionService.TrySetAlwaysShow(true, out _);
        }

        _onHotkeySettingChanged?.Invoke();
        LoggingService.Info("Settings", "User clicked Save Settings. Settings saved & applied.");
        StatusMessage = "Settings saved.";
        OnPropertyChanged(nameof(LogFileSizeDisplay));
    }

    public void ResetSettingsToDefaults(bool promptConfirm = true)
    {
        if (promptConfirm)
        {
            Window? owner = WindowHelper.ActiveOwner();
            var confirmed = ModernDialog.Confirm(
                owner,
                "Reset Settings",
                "Are you sure you want to reset all settings to their recommended default values?",
                "Your games, tools, scripts, artwork, API keys, scan locations, theme, text size and font are kept.",
                confirmText: "Reset to Defaults",
                cancelText: "Cancel");

            if (!confirmed) return;
        }

        string existingApiKey = _settings.SteamGridDbApiKey;
        string existingRawgKey = _settings.RawgApiKey;

        // Copy every default from a fresh AppSettings instead of a hand-maintained literal
        // list, so a newly added setting is reset automatically instead of silently staying
        // un-reset if someone forgets to add it here. Excludes properties that aren't a
        // user "preference" in the sense this dialog means - library view state
        // (LastCategoryFilter/LastSortOption), one-time-prompt/update-snooze state
        // (SkippedUpdateVersion/RemindAfterUtc), and the API key (preserved explicitly below,
        // same as before). See L-23.
        var defaults = new AppSettings();
        var excludedFromReset = new HashSet<string>
        {
            nameof(AppSettings.LastCategoryFilter),
            nameof(AppSettings.LastSortOption),
            // Tools page view state, the same bucket as the Library's two above.
            nameof(AppSettings.ToolsSortOption),
            nameof(AppSettings.ToolsViewMode),
            nameof(AppSettings.LastToolsCategoryTab),
            // Same bucket: which slice of the library is on screen, not a preference. Also the
            // only sane answer here - the tick boxes live in LibraryFilterViewModel, and clearing
            // the saved keys behind its back would leave them ticked and write them straight back.
            nameof(AppSettings.LibraryFilterKeys),
            // Filters the player named and saved: curated by hand, like the library itself.
            nameof(AppSettings.SavedLibraryFilters),
            nameof(AppSettings.SkippedUpdateVersion),
            nameof(AppSettings.RemindAfterUtc),
            nameof(AppSettings.SteamGridDbApiKey),
            nameof(AppSettings.RawgApiKey),
            // With the keys: a kept key that reset switched off isn't kept in any way that matters.
            nameof(AppSettings.UseSteamGridDbArt),
            nameof(AppSettings.UseRawgMetadata),
            // Manually-curated, not a "preference" in the dialog's sense - same bucket as the
            // library/categories/artwork the confirmation text already promises to leave alone.
            nameof(AppSettings.ScanLocations),
            nameof(AppSettings.IgnoredGamePaths),
            // Set once from actual detected launchers on the first "Scan for Games" press (see
            // ImportCoordinator.DetectInstalledLaunchers) rather than a real "preference" default -
            // resetting to the AppSettings class default would blindly re-enable every
            // platform regardless of what's actually installed. Same bucket as ScanLocations.
            nameof(AppSettings.SteamIntegrationEnabled),
            nameof(AppSettings.GogIntegrationEnabled),
            nameof(AppSettings.EaIntegrationEnabled),
            nameof(AppSettings.EpicIntegrationEnabled),
            nameof(AppSettings.UbisoftIntegrationEnabled),
            nameof(AppSettings.XboxIntegrationEnabled),
            nameof(AppSettings.BattleNetIntegrationEnabled),
            // UI layout state / one-time-prompt state, same as LastCategoryFilter above.
            nameof(AppSettings.IsSidebarExpanded),
            nameof(AppSettings.MainWindowLeft),
            nameof(AppSettings.MainWindowTop),
            nameof(AppSettings.MainWindowWidth),
            nameof(AppSettings.MainWindowHeight),
            nameof(AppSettings.MainWindowMaximized),
            nameof(AppSettings.GameDetailsWidth),
            nameof(AppSettings.GameDetailsHeight),
            nameof(AppSettings.WindowSizes),
            nameof(AppSettings.HasSeenPerformanceProfileMigrationPrompt),
            nameof(AppSettings.HasSeenLauncherDetectionPrompt),
            nameof(AppSettings.HasSeenWelcomePrompt),
            nameof(AppSettings.HasSeenMetadataSourcesReminder),
            nameof(AppSettings.HasSeenTrayHideNotice),
            nameof(AppSettings.HasTurnedOnToolsFor148),
            nameof(AppSettings.HasTurnedOnCompactTrayFor149),
            // The look the person picked, kept like the window's layout: a reset would change the stored choice without the
            // theme on screen changing, so the picker and the window would disagree.
            nameof(AppSettings.ThemeId),
            nameof(AppSettings.ShowThemeBackground),
            // The text size is the same kind of choice (and an accessibility one: a reset must not shrink it).
            nameof(AppSettings.TextSizePercent),
            nameof(AppSettings.FontName),
            // Describes the machine (what a tweak found before it was applied), not a preference.
            nameof(AppSettings.TweakPriorState),
            nameof(AppSettings.TweaksAppliedByTrayTrigger),
            nameof(AppSettings.LastRunVersion),
            // Records of what happened, not preferences: a reset would forget when the last backup was made (and have
            // the automatic one run again at once), the error it ended on, how often TrayTrigger has started, and the driver notice.
            nameof(AppSettings.LastBackupUtc),
            nameof(AppSettings.LastAutoBackupUtc),
            nameof(AppSettings.AutoBackupLastError),
            nameof(AppSettings.SessionsStarted),
            nameof(AppSettings.NvidiaDriverGpu),
            nameof(AppSettings.NvidiaDriverVersion),
            nameof(AppSettings.NvidiaDriverReleased),
            nameof(AppSettings.NvidiaDriverUrl),
            nameof(AppSettings.NvidiaDriverCheckedUtc),
        };
        foreach (var prop in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite || excludedFromReset.Contains(prop.Name)) continue;

            // Nested config objects (OptimizedProfileTweaks/AggressiveProfileTweaks) are reset
            // field-by-field into the *existing* instance rather than swapped for a new one:
            // SystemViewModel's tweak toggles captured the original instances at construction,
            // so replacing the reference would leave them editing a detached object whose
            // changes never get saved.
            object? current = prop.GetValue(_settings);
            object? fresh = prop.GetValue(defaults);
            if (current != null && fresh != null && prop.PropertyType.IsClass && prop.PropertyType != typeof(string)
                && !typeof(System.Collections.IEnumerable).IsAssignableFrom(prop.PropertyType))
            {
                foreach (var inner in prop.PropertyType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (inner.CanWrite) inner.SetValue(current, inner.GetValue(fresh));
                }
                continue;
            }

            prop.SetValue(_settings, fresh);
        }
        _settings.SteamGridDbApiKey = existingApiKey;
        _settings.RawgApiKey = existingRawgKey;

        // Execute side effects
        _startupManager.SetStartupEnabled(false, true);
        _trayPromotionService.TrySetAlwaysShow(true, out _);
        _onHotkeySettingChanged?.Invoke();
        LoggingService.Initialize(false);
        _onPosterArtSettingChanged?.Invoke();
        _onProfileTweaksReset?.Invoke();

        // Fire property changed for all settings
        OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(StartMinimizedToTray));
        OnPropertyChanged(nameof(CanConfigureMinimized));
        OnPropertyChanged(nameof(AlwaysShowTrayIcon));
        OnPropertyChanged(nameof(GroupTrayMenuByCategory));
        OnPropertyChanged(nameof(ShowRecentInTray));
        OnPropertyChanged(nameof(MaxRecentInTray));
        OnPropertyChanged(nameof(RecentTraySortOption));
        OnPropertyChanged(nameof(ShowFavoritesInTray));
        OnPropertyChanged(nameof(MaxFavoritesInTray));
        OnPropertyChanged(nameof(FavoritesTraySortOption));
        OnPropertyChanged(nameof(TrayMenuSortOption));
        OnPropertyChanged(nameof(SearchOfficialTitleOnline));
        OnPropertyChanged(nameof(OnlineMatchSensitivity));
        OnPropertyChanged(nameof(AutoCategorizeFromSteam));
        OnPropertyChanged(nameof(UseVerticalPosterArt));
        OnPropertyChanged(nameof(UseSteamGridDbArt));
        OnPropertyChanged(nameof(SteamGridDbApiKey));
        OnPropertyChanged(nameof(RawgApiKey));
        OnPropertyChanged(nameof(UseRawgMetadata));
        OnPropertyChanged(nameof(MetadataRefreshOption));
        OnPropertyChanged(nameof(AutoBackupEnabled));
        OnPropertyChanged(nameof(AutoBackupEveryOption));
        OnPropertyChanged(nameof(AutoBackupKeep));
        OnPropertyChanged(nameof(AutoBackupStatus));
        OnPropertyChanged(nameof(SteamIntegrationEnabled));
        OnPropertyChanged(nameof(AutoScanForGamesOnStartup));
        OnPropertyChanged(nameof(NotifyWhenScanAddsGames));
        OnPropertyChanged(nameof(ShowWhatsNew));
        OnPropertyChanged(nameof(MinimizeOnGameLaunch));
        OnPropertyChanged(nameof(KeepLaunchersMinimized));
        OnPropertyChanged(nameof(ShowLaunchPopup));
        OnPropertyChanged(nameof(ShowLaunchPopupOnEveryLaunch));
        OnPropertyChanged(nameof(ShowTrayMenuIcons));
        OnPropertyChanged(nameof(CompactTrayMenu));
        OnPropertyChanged(nameof(TrayLeftClickOpensMenu));
        OnPropertyChanged(nameof(AutoCheckForUpdates));
        OnPropertyChanged(nameof(IncludePrereleaseUpdates));
        OnPropertyChanged(nameof(GlobalManageHotkey));
        OnPropertyChanged(nameof(TrayMenuHotkey));
        OnPropertyChanged(nameof(SuspendGameHotkey));
        OnPropertyChanged(nameof(EnableGameScripts));
        OnPropertyChanged(nameof(EnableTools));
        OnPropertyChanged(nameof(ShowToolsInTray));
        OnPropertyChanged(nameof(ToolsTraySortOption));
        OnPropertyChanged(nameof(LaunchPopupDescription));
        OnPropertyChanged(nameof(LaunchPopupEveryLaunchLabel));
        // "Reset to defaults" swaps the ScriptDefaults object, so re-derive the mirrored-path tick.
        _defaultUseSameScriptForBoth = null;
        NotifyDefaultScriptsChanged();
        OnPropertyChanged(nameof(CreateRestorePointBeforeTweaks));
        OnPropertyChanged(nameof(VerboseLoggingEnabled));
        OnPropertyChanged(nameof(LibraryViewMode));
        OnPropertyChanged(nameof(IsGridView));
        OnPropertyChanged(nameof(IsExtraLargeView));
        OnPropertyChanged(nameof(IsIconsView));
        OnPropertyChanged(nameof(IsListView));
        OnPropertyChanged(nameof(IsPosterView));
        OnPropertyChanged(nameof(PosterDetailsOnHover));
        OnPropertyChanged(nameof(PosterDetailsOnHoverAccessibleName));
        PosterDetails.Refresh();

        AutoSaveSettings();
        _onTrayMenuSettingChanged?.Invoke();

        StatusMessage = "Settings restored to recommended defaults.";
    }

    // --- Backup & Restore (see BackupService) ---

    /// <summary>The game TrayTrigger is following right now, if any. A restore restarts TrayTrigger, so it waits until none is.</summary>
    public Func<string?>? RunningGameName { get; set; }

    /// <summary>Writes the library and tools to disk, so a backup holds what is on screen.</summary>
    public Action? FlushData { get; set; }

    /// <summary>A restore is unpacked: TrayTrigger restarts to put it in place.</summary>
    public event Action? RequestRestart;

    public ICommand BackUpCommand => _backUpCommand ??= new AsyncRelayCommand(BackUpAsync, () => !IsBackupBusy);
    private ICommand? _backUpCommand;
    public ICommand RestoreCommand => _restoreCommand ??= new AsyncRelayCommand(RestoreAsync, () => !IsBackupBusy);
    private ICommand? _restoreCommand;

    private bool _isBackupBusy;
    public bool IsBackupBusy
    {
        get => _isBackupBusy;
        private set
        {
            if (SetProperty(ref _isBackupBusy, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    private void SaveEverything()
    {
        FlushData?.Invoke();
        _storageService.SaveSettings(_settings);
    }

    private async Task BackUpAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Back Up TrayTrigger",
            FileName = BackupService.DefaultFileName(DateTime.Now),
            Filter = "TrayTrigger backup (*.zip)|*.zip",
            DefaultExt = ".zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (FileDialogCloak.Show(dialog) != true) return;

        string path = dialog.FileName;
        IsBackupBusy = true;
        StatusMessage = "Backing up...";
        try
        {
            SaveEverything();
            bool includeActivity = BackupIncludesActivity;
            var manifest = await Task.Run(() => new BackupService(_storageService).Create(path, includeActivity));
            // A backup made by hand counts for the automatic one: its clock starts again from here.
            _settings.LastBackupUtc = DateTime.UtcNow;
            _storageService.SaveSettings(_settings);
            OnPropertyChanged(nameof(AutoBackupStatus));
            StatusMessage = $"Backed up {manifest.Games} game(s), {manifest.Tools} tool(s), your settings, {manifest.Scripts} script(s) and {manifest.ArtFiles} artwork file(s){(manifest.HasActivity ? ", with Activity & History," : "")} to {path}";
        }
        catch (Exception ex)
        {
            LoggingService.Error("Backup", $"Backing up to '{path}' failed: {ex.Message}", ex);
            StatusMessage = $"The backup failed: {ex.Message}";
        }
        finally
        {
            IsBackupBusy = false;
        }
    }

    private async Task RestoreAsync()
    {
        var owner = WindowHelper.ActiveOwner();
        if (RunningGameName?.Invoke() is { } running)
        {
            ModernDialog.ShowWarning(owner, "Restore from Backup", $"Close {running} first.",
                "Restoring restarts TrayTrigger, and a game it launched would be left without its Performance Profile put back or its post-exit script run.");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Restore TrayTrigger from a Backup",
            Filter = "TrayTrigger backup (*.zip)|*.zip|All files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (FileDialogCloak.Show(dialog) != true) return;
        string path = dialog.FileName;

        BackupManifest manifest;
        try
        {
            manifest = BackupService.ReadManifest(path);
        }
        catch (InvalidDataException ex)
        {
            LoggingService.Info("Restore", $"'{path}' wasn't restored: {ex.Message}");
            ModernDialog.ShowWarning(owner, "Restore from Backup", "This file can't be restored.", ex.Message);
            return;
        }

        string from = string.IsNullOrWhiteSpace(manifest.MachineName) ? "" : $" on {manifest.MachineName}";
        bool confirmed = ModernDialog.Confirm(owner, "Restore from Backup",
            $"Replace your library, settings, tools and scripts with the backup made {manifest.CreatedUtc.ToLocalTime():MMM d, yyyy h:mm tt}{from}?",
            $"It holds {manifest.Games} game(s), {manifest.Tools} tool(s), {manifest.Scripts} script(s) and {manifest.ArtFiles} artwork file(s), from TrayTrigger {manifest.AppVersion}"
                + (manifest.HasActivity ? ", and its Activity & History, which replaces yours. " : ". ")
                + $"What you have now is backed up first, to the Backups folder in {_storageService.BaseDirectory}, so you can go back. "
                + "TrayTrigger restarts to finish. Game saves aren't touched.",
            confirmText: "Restore and Restart",
            cancelText: "Cancel");
        if (!confirmed)
        {
            LoggingService.Verbose("Restore", $"Restore from '{path}' cancelled at the confirmation.");
            return;
        }

        // Asked again: a game hotkey or the tray can start one while the dialogs are open.
        if (RunningGameName?.Invoke() is { } startedSince)
        {
            ModernDialog.ShowWarning(owner, "Restore from Backup", $"Close {startedSince} first.",
                "It started while the restore was being chosen. Restoring restarts TrayTrigger, and a game it launched would be left without its Performance Profile put back or its post-exit script run.");
            return;
        }

        IsBackupBusy = true;
        StatusMessage = "Backing up what you have now, then restoring...";
        try
        {
            SaveEverything();
            await Task.Run(() =>
            {
                var service = new BackupService(_storageService);
                string safety = service.CreateSafetyBackup();
                service.StageRestore(path, safety);
            });
        }
        catch (Exception ex)
        {
            LoggingService.Error("Restore", $"Restoring from '{path}' failed before anything was changed: {ex.Message}", ex);
            StatusMessage = $"The restore failed, and nothing was changed: {ex.Message}";
            IsBackupBusy = false;
            return;
        }

        LoggingService.Info("Restore", $"Restoring from '{path}'; restarting to finish.");
        RequestRestart?.Invoke();
    }

    private void OpenStorageFolder()
    {
        try
        {
            _storageService.EnsureDirectories();
            Process.Start(SystemExecutables.Explorer, _storageService.BaseDirectory);
        }
        catch (Exception ex)
        {
            LoggingService.Error("Storage", $"Failed to open folder: {ex.Message}");
        }
    }

    private void OpenCacheFolder()
    {
        try
        {
            _storageService.EnsureDirectories();
            Process.Start(SystemExecutables.Explorer, _storageService.LocalCacheDirectory);
        }
        catch (Exception ex)
        {
            LoggingService.Error("Storage", $"Failed to open cache folder: {ex.Message}");
        }
    }

    private void OpenLogFile()
    {
        LoggingService.OpenLogInEditor();
    }

    private void ClearLogFile()
    {
        LoggingService.ClearLog();
        OnPropertyChanged(nameof(LogFileSizeDisplay));
        StatusMessage = "Debug log cleared.";
    }

    private void OpenLogFolder()
    {
        try
        {
            string dir = Path.GetDirectoryName(LoggingService.LogFilePath) ?? _storageService.BaseDirectory;
            Process.Start(SystemExecutables.Explorer, dir);
        }
        catch (Exception ex)
        {
            LoggingService.Error("App", $"Failed to open log folder: {ex.Message}");
        }
    }
}
