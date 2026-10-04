using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// Which drive a game is installed on, and what kind of drive that is - for Game Details' "Installed
/// on" line and the per-drive game counts on System › Hardware Specs. The drive types come from
/// <see cref="GetDriveLetterMediaTypes"/>, the same WMI join the Storage card uses, read once and
/// kept: it takes a few hundred milliseconds, and drives don't change type.
/// </summary>
public partial class SystemInfoService
{
    private static Dictionary<string, string>? s_driveMediaTypes;
    private static readonly Lock s_driveMediaTypesLock = new();

    /// <summary>Drive letter ("E:") to "NVMe SSD" / "SATA SSD" / "HDD" / "USB". Slow the first time
    /// (WMI); call it off the UI thread.</summary>
    public static IReadOnlyDictionary<string, string> DriveMediaTypes()
    {
        lock (s_driveMediaTypesLock)
            return s_driveMediaTypes ??= GetDriveLetterMediaTypes();
    }

    /// <summary>Keeps what the Storage card just read, so a later lookup needn't ask WMI again.</summary>
    private static void RememberDriveMediaTypes(Dictionary<string, string> types)
    {
        lock (s_driveMediaTypesLock)
            s_driveMediaTypes = types;
    }

    /// <summary>
    /// The folder a game lives in: its executable when that's a real file, otherwise its working
    /// folder with any link followed - an Xbox game's folder is a junction into D:\XboxGames, and
    /// it's the drive the junction points to that the game loads from. Null for a game launched by
    /// a URL with no folder recorded.
    /// </summary>
    internal static string? GameFolder(GameEntry game)
    {
        try
        {
            if (Path.IsPathFullyQualified(game.ExecutablePath ?? "") && File.Exists(game.ExecutablePath))
                return Path.GetDirectoryName(game.ExecutablePath);
            if (Path.IsPathFullyQualified(game.WorkingDirectory ?? "") && Directory.Exists(game.WorkingDirectory))
            {
                return LinkedDirectory.Resolve(game.WorkingDirectory, out string resolved) == LinkedDirectoryState.BrokenLink
                    ? null
                    : resolved;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            LoggingService.Verbose("SystemInfo", $"Couldn't find the folder of '{game.Name}': {ex.Message}");
        }
        return null;
    }

    /// <summary>"E:" for a game in E:\Games\..., or null for one with no local folder.</summary>
    internal static string? GameDriveLetter(GameEntry game)
    {
        string? folder = GameFolder(game);
        if (folder == null) return null;
        string? root = Path.GetPathRoot(folder);
        // A UNC share or a volume mounted in a folder has no letter of its own to report.
        return root is { Length: 3 } && root[1] == ':' ? root[..2].ToUpperInvariant() : null;
    }

    /// <summary>"E: · NVMe SSD", or just "E:" when the drive's type can't be read; null without a drive.</summary>
    public static string? GameDriveDisplay(GameEntry game)
    {
        string? letter = GameDriveLetter(game);
        if (letter == null) return null;
        return DriveMediaTypes().TryGetValue(letter, out string? type) ? $"{letter} · {type}" : letter;
    }
}
