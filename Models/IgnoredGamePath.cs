namespace TrayTrigger.Models;

/// <summary>
/// A specific game candidate permanently excluded from "Add Folder" and "Scan for Games" results
/// - e.g. a bundled non-game tool the folder scanner's heuristics mistake for a game, or a Steam
/// app the user just never wants suggested. Exactly one of <see cref="ExePath"/> (folder-based
/// candidates, matched by exe path - the one stable identity both flows always have for those) or
/// <see cref="SteamAppId"/> (Steam candidates, matched by AppId - stable across exe/version
/// changes, unlike its ExePath) is set, depending on where the candidate came from.
/// </summary>
public class IgnoredGamePath
{
    public string Id { get; set; } = System.Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string? ExePath { get; set; }
    public string? SteamAppId { get; set; }
}
