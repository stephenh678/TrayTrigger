using System;

namespace TrayTrigger.Models;

/// <summary>
/// Which platform a scan location came from. Steam locations are kept in sync automatically by
/// <see cref="Services.ScanLocationService"/>; Gog/Ea are reserved for future platform scanners
/// that will follow the same "detect and add to this list" pattern Steam uses today.
/// </summary>
public enum ScanLocationSource
{
    Manual,
    Steam,
    Gog,
    Ea
}

/// <summary>
/// One folder the "Scan for Games" feature looks in for installed games - either a Steam library
/// folder (auto-detected and kept in sync) or a folder the user added by hand (e.g. "D:\Games").
/// Every location is treated as a library root that may contain many game subfolders, the same
/// way Nvidia's "Add a Scan Location" works.
/// </summary>
public class ScanLocation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = string.Empty;
    public ScanLocationSource Source { get; set; } = ScanLocationSource.Manual;
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// True for entries <see cref="Services.ScanLocationService"/> keeps in sync automatically.
    /// Removing one from the UI is not permanent - it reappears the next time the owning
    /// platform's locations are synced, unless that platform's integration is turned off.
    /// </summary>
    public bool IsAutoManaged { get; set; }
}
