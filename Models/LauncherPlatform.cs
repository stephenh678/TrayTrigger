namespace TrayTrigger.Models;

/// <summary>
/// Which launcher integration a library entry was imported through (see
/// <see cref="GameEntry.ImportedFrom"/>). Provenance, as opposed to the per-entry launch flags
/// (IsSteamGame/IsGogGame/...) which the user can also set by hand in Edit Game - so "remove all
/// this platform's games" when an integration is turned off sweeps only what the integration
/// itself brought in, never a shortcut the user dropped and marked "launch via Steam".
/// </summary>
public enum LauncherPlatform
{
    Steam,
    Gog,
    Ea,
    Epic,
    Ubisoft
}
