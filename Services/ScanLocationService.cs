using System;
using System.Collections.Generic;
using System.Linq;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// Keeps <see cref="AppSettings.ScanLocations"/> in sync with what Steam itself reports as its
/// library folders, so "Scan for Games" always covers every Steam library without the user having
/// to add them by hand. Only touches entries this sync itself owns (<see cref="ScanLocation.Source"/>
/// == Steam and <see cref="ScanLocation.IsAutoManaged"/>); a manually-added location is never
/// added, removed, or otherwise modified here, even if its path happens to match a Steam library.
/// </summary>
public static class ScanLocationService
{
    /// <summary>
    /// Adds any newly-detected Steam library folder and drops auto-managed Steam entries for
    /// libraries Steam no longer reports (e.g. a library removed from a drive that's no longer
    /// attached). Returns true if <paramref name="settings"/>.ScanLocations changed and should be
    /// persisted.
    /// </summary>
    public static bool SyncSteamLocations(AppSettings settings, SteamScannerService steamScanner)
    {
        bool changed = false;

        string? steamPath = steamScanner.GetSteamInstallPath();
        if (string.IsNullOrEmpty(steamPath))
        {
            return changed;
        }

        var detected = new HashSet<string>(
            steamScanner.GetLibraryFolders(steamPath).Select(NormalizePath),
            StringComparer.OrdinalIgnoreCase);

        var toRemove = settings.ScanLocations
            .Where(loc => loc.Source == ScanLocationSource.Steam && loc.IsAutoManaged && !detected.Contains(NormalizePath(loc.Path)))
            .ToList();
        foreach (var loc in toRemove)
        {
            LoggingService.Info("ScanLocationService", $"Removing auto-managed Steam scan location no longer reported by Steam: '{loc.Path}'.");
        }
        int removed = settings.ScanLocations.RemoveAll(toRemove.Contains);
        changed |= removed > 0;

        var known = new HashSet<string>(settings.ScanLocations.Select(l => NormalizePath(l.Path)), StringComparer.OrdinalIgnoreCase);
        foreach (var path in detected)
        {
            if (known.Contains(path)) continue;

            settings.ScanLocations.Add(new ScanLocation
            {
                Path = path,
                Source = ScanLocationSource.Steam,
                IsAutoManaged = true,
                IsEnabled = true
            });
            LoggingService.Info("ScanLocationService", $"Added Steam scan location: '{path}'.");
            changed = true;
        }

        return changed;
    }

    private static string NormalizePath(string path) => path.TrimEnd('\\', '/');
}
