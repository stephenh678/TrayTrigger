namespace TrayTrigger.Models;

/// <summary>
/// A specific game candidate permanently excluded from "Add Folder" and "Scan for Games" results
/// - e.g. a bundled non-game tool the folder scanner's heuristics mistake for a game, or a Steam
/// app the user just never wants suggested. Exactly one of <see cref="ExePath"/> (folder-based
/// candidates, matched by exe path - the one stable identity both flows always have for those),
/// <see cref="SteamAppId"/> (Steam candidates, matched by AppId - stable across exe/version
/// changes, unlike its ExePath), <see cref="GogGameId"/> (GOG candidates, same reasoning as
/// SteamAppId), <see cref="EaContentId"/> (EA candidates, same reasoning),
/// <see cref="EpicAppName"/> (Epic candidates, same reasoning), <see cref="UbisoftGameId"/>
/// (Ubisoft candidates, same reasoning), <see cref="XboxAumid"/> (Xbox candidates, same
/// reasoning), <see cref="BattleNetUid"/> (Battle.net candidates, same reasoning), or
/// <see cref="FolderPath"/> (a whole folder the scanner must not look inside - added by hand in
/// Settings for a Utilities or Mods folder under a scan location) is set, depending on where the
/// candidate came from.
/// </summary>
public class IgnoredGamePath
{
    public string Id { get; set; } = System.Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string? ExePath { get; set; }
    /// <summary>A folder (no trailing separator) that folder scans skip entirely, along with everything under it.</summary>
    public string? FolderPath { get; set; }
    public string? SteamAppId { get; set; }
    public string? GogGameId { get; set; }
    public string? EaContentId { get; set; }
    public string? EpicAppName { get; set; }
    public string? UbisoftGameId { get; set; }
    public string? XboxAumid { get; set; }
    public string? BattleNetUid { get; set; }
}
