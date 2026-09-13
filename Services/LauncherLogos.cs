using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// The launcher logo for a library entry, as a pack URI into Assets/LauncherLogos. Used where a
/// game has no icon of its own (the tray menu's fallback), so a game still reads as "the Steam
/// one" or "the Battle.net one" rather than as a generic controller.
/// </summary>
public static class LauncherLogos
{
    private const string Folder = "pack://application:,,,/Assets/LauncherLogos/";

    public const string LocalGames = Folder + "local_games.png";

    /// <summary>The logo of the platform the entry belongs to, or null for a plain local game.</summary>
    public static string? PackUriFor(GameEntry game)
    {
        if (game.IsSteamGame) return Folder + "steam.png";
        if (game.IsGogGame) return Folder + "gog_galaxy.png";
        if (game.IsEaGame) return Folder + "ea_app.png";
        if (game.IsEpicGame) return Folder + "epic_games.png";
        if (game.IsUbisoftGame) return Folder + "ubisoft_connect.png";
        if (game.IsXboxGame) return Folder + "xbox.png";
        if (game.IsBattleNetGame) return Folder + "battlenet.png";
        return null;
    }
}
