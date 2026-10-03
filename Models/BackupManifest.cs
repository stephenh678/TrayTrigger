using System;

namespace TrayTrigger.Models;

/// <summary>
/// backup.json, an entry of a TrayTrigger backup (.zip): what the backup holds and where
/// it was made. The folders are what lets a restore on another PC, or under another Windows
/// account, point paths that named the old ones at the new ones.
/// </summary>
public sealed class BackupManifest
{
    /// <summary>Raised only for a change an older TrayTrigger couldn't restore correctly.</summary>
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string AppVersion { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public string MachineName { get; set; } = string.Empty;
    /// <summary>%AppData%\TrayTrigger where the backup was made.</summary>
    public string DataDirectory { get; set; } = string.Empty;
    /// <summary>%LocalAppData%\TrayTrigger where the backup was made.</summary>
    public string CacheDirectory { get; set; } = string.Empty;
    /// <summary>The Windows user folder (C:\Users\name) where the backup was made.</summary>
    public string UserProfile { get; set; } = string.Empty;
    public int Games { get; set; }
    public int Tools { get; set; }
    public int Scripts { get; set; }
    public int ArtFiles { get; set; }
}
