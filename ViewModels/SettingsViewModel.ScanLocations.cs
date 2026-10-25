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

/// <summary>Settings > Scan for Games: the scan locations, the ignored paths, and the undo for removing them.</summary>
public partial class SettingsViewModel
{
    // --- Scan for Games: Scan Locations ---
    // The folder list "Scan for Games" (Library page) looks in, split into two views of the same
    // AppSettings.ScanLocations list: Steam library folders (auto-managed by ScanLocationService,
    // shown under the Steam integration toggle, checkbox only) and manually-added folders (fully
    // user-owned, shown in the Scan Locations section with a Remove button).

    /// <summary>Re-reads AppSettings.ScanLocations - call after something outside SettingsViewModel
    /// (e.g. ImportCoordinator adding a "remembered" folder) has changed it.</summary>
    public void RefreshScanLocations() => RebuildScanLocationRows();

    private void RebuildScanLocationRows()
    {
        SteamLibraryLocations.Clear();
        ScanLocations.Clear();
        foreach (var loc in _settings.ScanLocations.OrderBy(l => l.Path, StringComparer.OrdinalIgnoreCase))
        {
            var row = new ScanLocationRowViewModel(loc, () => AutoSaveSettings());
            if (loc.Source == ScanLocationSource.Steam)
                SteamLibraryLocations.Add(row);
            else
                ScanLocations.Add(row);
        }
        OnPropertyChanged(nameof(HasNoSteamLibraryLocations));
        OnPropertyChanged(nameof(HasNoScanLocations));
    }

    private void AddScanLocation()
    {
        var dialog = new OpenFolderDialog { Title = "Add a Scan Location", Multiselect = false };
        if (FileDialogCloak.Show(dialog) != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;

        string path = dialog.FolderName.TrimEnd('\\', '/');
        var existing = _settings.ScanLocations.FirstOrDefault(l => string.Equals(l.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (existing.Source == ScanLocationSource.Steam && !_settings.SteamIntegrationEnabled)
            {
                // A Steam library row lingers in settings while Steam integration is off, hidden
                // from the UI and skipped by the scan - it must not block the user from folder-
                // scanning that same directory for non-Steam installs. Convert it in place.
                existing.Source = ScanLocationSource.Manual;
                existing.IsAutoManaged = false;
                existing.IsEnabled = true;
                AutoSaveSettings();
                RebuildScanLocationRows();
                LoggingService.Info("Settings", $"Converted hidden Steam library row '{path}' to a manual scan location.");
                StatusMessage = "Added scan location (it was a Steam library folder; it's now scanned as a plain folder while Steam integration is off).";
                return;
            }

            StatusMessage = existing.Source == ScanLocationSource.Steam
                ? "That folder is already one of your Steam libraries - see the Steam integration above."
                : "That folder is already a scan location.";
            return;
        }

        _settings.ScanLocations.Add(new ScanLocation { Path = path, Source = ScanLocationSource.Manual, IsEnabled = true });
        AutoSaveSettings();
        RebuildScanLocationRows();
        LoggingService.Info("Settings", $"Added scan location: '{path}'.");
        StatusMessage = "Added scan location.";
    }

    private void RemoveScanLocation(ScanLocationRowViewModel row)
    {
        int index = _settings.ScanLocations.FindIndex(l => l.Id == row.Model.Id);
        if (index < 0) return;
        var removed = _settings.ScanLocations[index];
        _settings.ScanLocations.RemoveAt(index);
        AutoSaveSettings();
        RebuildScanLocationRows();
        LoggingService.Info("Settings", $"Removed scan location: '{row.Path}'.");
        OfferUndo($"Removed scan location {row.Path}", () =>
        {
            _settings.ScanLocations.Insert(Math.Min(index, _settings.ScanLocations.Count), removed);
            AutoSaveSettings();
            RebuildScanLocationRows();
            LoggingService.Info("Settings", $"Undid removal of scan location '{removed.Path}'.");
        });
    }

    private void RefreshSteamScanLocations()
    {
        if (!_settings.SteamIntegrationEnabled)
        {
            StatusMessage = "Steam integration is disabled - enable it first.";
            return;
        }

        if (ScanLocationService.SyncSteamLocations(_settings, _steamScannerService))
        {
            AutoSaveSettings();
            RebuildScanLocationRows();
            StatusMessage = "Steam libraries refreshed.";
        }
        else
        {
            StatusMessage = SteamLibraryLocations.Count == 0
                ? "No Steam libraries found - is Steam installed?"
                : "Steam libraries are already up to date.";
        }
    }

    // --- Scan for Games: Ignored Paths ---
    // Executables permanently excluded from "Add Folder" and "Scan for Games" results after the
    // user clicks "Ignore" on a false-positive candidate (e.g. a bundled non-game tool).

    /// <summary>Re-reads AppSettings.IgnoredGamePaths - call after something outside SettingsViewModel
    /// (e.g. ImportCoordinator recording a new ignore) has changed it.</summary>
    public void RefreshIgnoredGamePaths() => RebuildIgnoredGamePathRows();

    private void RebuildIgnoredGamePathRows()
    {
        IgnoredGamePaths.Clear();
        foreach (var p in _settings.IgnoredGamePaths.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            IgnoredGamePaths.Add(new IgnoredGamePathRowViewModel(p));
        }
        OnPropertyChanged(nameof(HasNoIgnoredGamePaths));
    }

    private void RemoveIgnoredGamePath(IgnoredGamePathRowViewModel row)
    {
        int index = _settings.IgnoredGamePaths.FindIndex(p => p.Id == row.Model.Id);
        if (index < 0) return;
        var removed = _settings.IgnoredGamePaths[index];
        _settings.IgnoredGamePaths.RemoveAt(index);
        AutoSaveSettings();
        RebuildIgnoredGamePathRows();
        OfferUndo($"No longer ignoring {removed.Name}", () =>
        {
            _settings.IgnoredGamePaths.Insert(Math.Min(index, _settings.IgnoredGamePaths.Count), removed);
            AutoSaveSettings();
            RebuildIgnoredGamePathRows();
            LoggingService.Info("Settings", $"Undid removal of ignored entry '{removed.Name}'.");
        });
    }

    // --- Undo for Settings removals (UX-11) ---
    // Removing a scan location, an ignored game or an ignored folder is saved at once, as before,
    // and the same kind of toast as the Library's offers to put it back for 10 seconds (paused
    // while hovered or focused). There is nothing to finalize: the removal is already on disk,
    // so exiting or a second removal simply ends the chance to undo the first.

    private readonly UndoTimer _undoTimer = new();
    private Action? _pendingUndo;
    private bool _isUndoToastVisible;
    private string _undoToastMessage = string.Empty;

    public bool IsUndoToastVisible
    {
        get => _isUndoToastVisible;
        private set => SetProperty(ref _isUndoToastVisible, value);
    }

    public string UndoToastMessage
    {
        get => _undoToastMessage;
        private set => SetProperty(ref _undoToastMessage, value);
    }

    private ICommand? _undoRemovalCommand;
    private ICommand? _dismissUndoToastCommand;
    public ICommand UndoRemovalCommand => _undoRemovalCommand ??= new RelayCommand(UndoRemoval);
    public ICommand DismissUndoToastCommand => _dismissUndoToastCommand ??= new RelayCommand(EndUndoWindow);

    private void OfferUndo(string message, Action undo)
    {
        _pendingUndo = undo;
        LoggingService.Shown("Settings undo toast", message);
        UndoToastMessage = message;
        IsUndoToastVisible = true;
        _undoTimer.Start(EndUndoWindow);
    }

    public void UndoRemoval()
    {
        var undo = _pendingUndo;
        EndUndoWindow();
        undo?.Invoke();
        if (undo != null) StatusMessage = "Put back.";
    }

    private void EndUndoWindow()
    {
        _undoTimer.Stop();
        _pendingUndo = null;
        IsUndoToastVisible = false;
    }

    /// <summary>The pointer is over the undo toast, or it has keyboard focus: hold the window open.</summary>
    public void SetUndoToastHeld(bool held) => _undoTimer.Hold(held);

    /// <summary>
    /// The one kind of ignore that is added by hand: a whole folder the scanner must not look
    /// inside. Single exes and platform games are ignored from a scan result, where the name is known.
    /// </summary>
    private void AddIgnoredFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Ignore a folder", Multiselect = false };
        if (FileDialogCloak.Show(dialog) != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;

        string path = dialog.FolderName.TrimEnd('\\', '/');
        if (_settings.IgnoredGamePaths.Any(p => string.Equals(p.FolderPath, path, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = "That folder is already ignored.";
            return;
        }
        if (_settings.ScanLocations.Any(l => string.Equals(l.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = "That folder is a scan location. Untick or remove it above instead of ignoring it.";
            return;
        }

        _settings.IgnoredGamePaths.Add(new IgnoredGamePath { FolderPath = path, Name = Path.GetFileName(path) is { Length: > 0 } n ? n : path });
        AutoSaveSettings();
        RebuildIgnoredGamePathRows();
        LoggingService.Info("Settings", $"Ignored folder '{path}' - folder scans skip it and everything under it.");
        StatusMessage = $"Ignoring {path}";
    }
}
