using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>The launcher a library card is badged with: one per card, Local when it has none.</summary>
public enum LauncherBadge
{
    Local,
    Steam,
    Gog,
    Ea,
    Epic,
    Ubisoft,
    Xbox,
    BattleNet,
    Rockstar,
    Amazon,
    Itch
}

/// <summary>
/// The launcher logo for a library entry, as a pack URI into Assets/LauncherLogos. Used where a
/// game has no icon of its own (the tray menu's fallback), so a game still reads as "the Steam
/// one" or "the Battle.net one" rather than as a generic controller, and for the Library's
/// launcher badges.
/// </summary>
public static class LauncherLogos
{
    private const string Folder = "pack://application:,,,/Assets/LauncherLogos/";

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
        if (game.IsRockstarGame) return Folder + "rockstar_games.png";
        if (game.IsAmazonGame) return Folder + "amazon_games.png";
        if (game.IsItchGame) return Folder + "itchio.png";
        return null;
    }

    /// <summary>
    /// The badge a Library card shows, in the same order as the entry's placeholder category
    /// (<see cref="LibraryConstants.PlatformCategoryFor"/>): a store's own flag first, then a
    /// Steam badge (real or forced), else Local.
    /// </summary>
    public static LauncherBadge BadgeFor(GameEntry game)
        => game.IsGogGame ? LauncherBadge.Gog
         : game.IsEaGame ? LauncherBadge.Ea
         : game.IsEpicGame ? LauncherBadge.Epic
         : game.IsUbisoftGame ? LauncherBadge.Ubisoft
         : game.IsXboxGame ? LauncherBadge.Xbox
         : game.IsBattleNetGame ? LauncherBadge.BattleNet
         : game.IsRockstarGame ? LauncherBadge.Rockstar
         : game.IsAmazonGame ? LauncherBadge.Amazon
         : game.IsItchGame ? LauncherBadge.Itch
         : game.HasSteamOverlay ? LauncherBadge.Steam
         : LauncherBadge.Local;

    /// <summary>The badge's logo; Local has the generic "Local Games" one.</summary>
    public static string PackUriFor(LauncherBadge badge) => Folder + badge switch
    {
        LauncherBadge.Steam => "steam.png",
        LauncherBadge.Gog => "gog_galaxy.png",
        LauncherBadge.Ea => "ea_app.png",
        LauncherBadge.Epic => "epic_games.png",
        LauncherBadge.Ubisoft => "ubisoft_connect.png",
        LauncherBadge.Xbox => "xbox.png",
        LauncherBadge.BattleNet => "battlenet.png",
        LauncherBadge.Rockstar => "rockstar_games.png",
        LauncherBadge.Amazon => "amazon_games.png",
        LauncherBadge.Itch => "itchio.png",
        _ => "local_games.png"
    };

    /// <summary>The short name on the Library's badge pills.</summary>
    public static string ShortLabelFor(LauncherBadge badge) => badge switch
    {
        LauncherBadge.Steam => "STEAM",
        LauncherBadge.Gog => "GOG",
        LauncherBadge.Ea => "EA",
        LauncherBadge.Epic => "EPIC",
        LauncherBadge.Ubisoft => "UBI",
        LauncherBadge.Xbox => "XBOX",
        LauncherBadge.BattleNet => "B.NET",
        LauncherBadge.Rockstar => "ROCKSTAR",
        LauncherBadge.Amazon => "AMAZON",
        LauncherBadge.Itch => "ITCH.IO",
        _ => "LOCAL"
    };
}
