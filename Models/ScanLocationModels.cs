using System;

namespace TrayTrigger.Models;

/// <summary>
/// Which platform a scan location came from. Steam locations are kept in sync automatically by
/// <see cref="Services.ScanLocationService"/> and shown under the Steam integration toggle in
/// Settings; Manual ones are user-added and shown in the Scan Locations list. Gog/Ea are unused:
/// those platforms turned out to have no library-folder concept (their scanners read install
/// paths from the registry/manifests directly), and the values are kept only so the enum's
/// serialized numbering stays stable.
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
    /// These have no Remove button in Settings (it would just come back on the next sync);
    /// untick the row to skip that library, or turn the platform's integration off.
    /// </summary>
    public bool IsAutoManaged { get; set; }
}
