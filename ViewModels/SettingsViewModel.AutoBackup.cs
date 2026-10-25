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

/// <summary>Automatic backups, the art-source keys, and the poster refresh.</summary>
public partial class SettingsViewModel
{
    // ------------------------------------------------------------------ Automatic backups

    public ObservableCollection<string> AutoBackupEveryOptions { get; } = new() { "Every week", "Every 2 weeks", "Every month" };
    public IReadOnlyList<int> AutoBackupKeepOptions => AutoBackupService.KeepChoices;

    public bool AutoBackupEnabled
    {
        get => _settings.AutoBackupEnabled;
        set
        {
            if (_settings.AutoBackupEnabled == value) return;
            _settings.AutoBackupEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoBackupStatus));
            AutoSaveSettings();
            // Turning it on makes the first backup now, so the card shows it working.
            if (value) RunAutoBackupIfDue();
        }
    }

    public string AutoBackupEveryOption
    {
        get => _settings.AutoBackupEveryDays switch { >= 30 => "Every month", >= 14 => "Every 2 weeks", _ => "Every week" };
        set
        {
            int days = value switch { "Every month" => 30, "Every 2 weeks" => 14, _ => 7 };
            if (_settings.AutoBackupEveryDays == days) return;
            _settings.AutoBackupEveryDays = days;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoBackupStatus));
            AutoSaveSettings();
        }
    }

    public int AutoBackupKeep
    {
        get => _settings.AutoBackupKeep;
        set
        {
            if (_settings.AutoBackupKeep == value) return;
            _settings.AutoBackupKeep = value;
            OnPropertyChanged();
            AutoSaveSettings();
        }
    }

    /// <summary>When the last backup was made, when the next is due, and why the last automatic one failed.</summary>
    public string AutoBackupStatus
    {
        get
        {
            var lines = new List<string>();
            if (_settings.LastBackupUtc is { } last)
            {
                bool byHand = _settings.LastAutoBackupUtc is not { } auto || last > auto;
                lines.Add($"Last backup: {last.ToLocalTime():MMM d, h:mm tt}{(byHand ? " (made by hand)" : "")}");
            }
            else
            {
                lines.Add("No backup made yet.");
            }
            if (!string.IsNullOrEmpty(_settings.AutoBackupLastError))
            {
                lines.Add($"The last automatic backup failed: {_settings.AutoBackupLastError}");
            }
            else if (_settings.AutoBackupEnabled && AutoBackupService.NextDueLocal(_settings) is { } next)
            {
                lines.Add($"Next: the first start or daily check after {next:MMM d}");
            }
            return string.Join("\n", lines);
        }
    }

    public ICommand OpenAutoBackupFolderCommand => _openAutoBackupFolderCommand ??= new RelayCommand(() =>
    {
        try
        {
            string folder = new AutoBackupService(_storageService).BackupFolder;
            Directory.CreateDirectory(folder);
            Process.Start(SystemExecutables.Explorer, folder);
        }
        catch (Exception ex)
        {
            LoggingService.Error("Backup", $"Failed to open the automatic backups folder: {ex.Message}");
        }
    });
    private ICommand? _openAutoBackupFolderCommand;

    /// <summary>
    /// Takes an automatic backup when one is due: at startup, once a day and after the last game exits.
    /// Waits while a game runs or another backup or restore is going on. Safe to call at any time, on the UI thread.
    /// </summary>
    public async void RunAutoBackupIfDue()
    {
        try
        {
            if (IsBackupBusy || RunningGameName?.Invoke() != null) return;
            if (BackupService.HasPendingRestore(_storageService.BaseDirectory)) return;
            if (!AutoBackupService.IsDue(_settings, DateTime.UtcNow)) return;

            IsBackupBusy = true;
            try
            {
                SaveEverything();
                var service = new AutoBackupService(_storageService);
                await Task.Run(() => service.Run(_settings, DateTime.UtcNow));
                _storageService.SaveSettings(_settings);
            }
            finally
            {
                IsBackupBusy = false;
                OnPropertyChanged(nameof(AutoBackupStatus));
            }
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed("Backup", ex, "running the automatic backup");
        }
    }

    public bool UseRawgMetadata
    {
        get => _settings.UseRawgMetadata;
        set
        {
            if (_settings.UseRawgMetadata != value)
            {
                _settings.UseRawgMetadata = value;
                OnPropertyChanged();
                AutoSaveSettings();
                if (value)
                    RawgKeyCheck.ScheduleCheck();
            }
        }
    }

    public string RawgApiKey
    {
        get => _settings.RawgApiKey;
        set
        {
            value = value?.Trim() ?? string.Empty;
            if (_settings.RawgApiKey != value)
            {
                bool wasBlank = string.IsNullOrEmpty(_settings.RawgApiKey);
                _settings.RawgApiKey = value;
                // The user owns the field now: if an undecryptable value was being preserved for
                // it, this replaces it - including a deliberate clear.
                _storageService.NoteApiKeyEdited(StorageService.ApiKeyField.Rawg);
                OnPropertyChanged();
                if (wasBlank && value.Length > 0 && !_settings.UseRawgMetadata)
                {
                    _settings.UseRawgMetadata = true;
                    OnPropertyChanged(nameof(UseRawgMetadata));
                }
                AutoSaveSettings();
                RawgKeyCheck.ScheduleCheck();
            }
        }
    }

    /// <summary>Whether the SteamGridDB key works - the line under its key box.</summary>
    public ApiKeyCheckViewModel SteamGridDbKeyCheck { get; }

    /// <summary>Whether the RAWG key works - the line under its key box.</summary>
    public ApiKeyCheckViewModel RawgKeyCheck { get; }

    // The last key each source caught the library up with, so checking the same key again
    // doesn't start another pass over every game.
    private string? _steamGridDbKeyCaughtUp;
    private string? _rawgKeyCaughtUp;

    /// <summary>
    /// A SteamGridDB key that works, and is switched on: give the games already in the library the
    /// posters it can find, as Refresh All Game Posters would. Without this a key added after the
    /// first scan changed nothing until that button was found. Runs on a confirmed key rather than
    /// on the edit, so a key typed by hand doesn't start a pass on its first character.
    /// </summary>
    private void OnSteamGridDbKeyAccepted(string key)
    {
        if (SteamGridDbApiKeyOrNull != key || _steamGridDbKeyCaughtUp == key)
            return;
        _steamGridDbKeyCaughtUp = key;
        if (!_settings.UseVerticalPosterArt)
            return;

        LoggingService.Info("Settings", "SteamGridDB key confirmed; filling in posters for the library.");
        _ = RefreshAllPostersAsync();
    }

    /// <summary>
    /// A RAWG key that works, and is switched on: match the games Steam doesn't list now, rather
    /// than after the retry interval. Their categories fill in when auto-categorize is on, and
    /// either way RAWG's title for each becomes the SteamGridDB poster search.
    /// </summary>
    private void OnRawgKeyAccepted(string key)
    {
        if (RawgApiKeyOrNull != key || _rawgKeyCaughtUp == key)
            return;
        _rawgKeyCaughtUp = key;

        LoggingService.Info("Settings", "RAWG key confirmed; matching the library's non-Steam games.");
        _ = _onRequestEnrichLibrary?.Invoke(true);
    }

    private async Task RefreshAllPostersAsync()
    {
        if (_onRequestRefreshAllPosters == null || IsRefreshingAllPosters)
            return;

        IsRefreshingAllPosters = true;
        try
        {
            await _onRequestRefreshAllPosters(new Progress<string>(msg => PosterRefreshStatus = msg));
        }
        finally
        {
            IsRefreshingAllPosters = false;
        }
    }

    /// <summary>The RAWG key when the feature is enabled and a key is set, else null - the details
    /// window uses this to decide whether the RAWG source is available for the toggle.</summary>
    public string? RawgApiKeyOrNull =>
        _settings.UseRawgMetadata && !string.IsNullOrWhiteSpace(_settings.RawgApiKey) ? _settings.RawgApiKey : null;

    private bool _isRefreshingAllPosters;
    public bool IsRefreshingAllPosters
    {
        get => _isRefreshingAllPosters;
        private set { _isRefreshingAllPosters = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanRefreshAllPosters)); }
    }

    public bool CanRefreshAllPosters => !IsRefreshingAllPosters;

    private string _posterRefreshStatus = string.Empty;
    public string PosterRefreshStatus
    {
        get => _posterRefreshStatus;
        private set { _posterRefreshStatus = value; OnPropertyChanged(); }
    }

    public string? SteamGridDbApiKeyOrNull =>
        _settings.UseSteamGridDbArt && !string.IsNullOrWhiteSpace(_settings.SteamGridDbApiKey)
            ? _settings.SteamGridDbApiKey
            : null;
}
