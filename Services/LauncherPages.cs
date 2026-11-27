using System;
using System.IO;
using System.Linq;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// "Open in &lt;launcher&gt;" on Game Details: takes the player to the place where the game's launcher keeps its own
/// Verify, Repair and Uninstall. Steam and GOG Galaxy open on the game's own page; Battle.net is brought to the front
/// (the tested "focus play" command); the EA app, Epic, Ubisoft Connect, the Rockstar Games Launcher and Amazon Games
/// have no documented link to one game's page, so they are just opened. Xbox games are repaired in Windows' own
/// Installed apps, not in a launcher, so they get nothing here.
/// </summary>
public static class LauncherPages
{
    /// <summary>The launcher a game belongs to, as the menu names it, or null for a game with none (Local, Xbox).</summary>
    internal static string? LauncherName(GameEntry game) =>
        game.IsSteamGame && UrlProtocolHelper.IsValidSteamAppId(game.SteamAppId) ? "Steam"
        : game.IsGogGame && IsGogId(game.GogGameId) ? "GOG Galaxy"
        : game.IsEpicGame ? "the Epic Games Launcher"
        : game.IsEaGame ? "the EA App"
        : game.IsUbisoftGame ? "Ubisoft Connect"
        : game.IsBattleNetGame ? "Battle.net"
        : game.IsRockstarGame ? "the Rockstar Games Launcher"
        : game.IsAmazonGame ? "Amazon Games"
        : null;

    /// <summary>"Open in Steam" and so on, or null when the game has no launcher or the launcher isn't installed.</summary>
    public static string? OpenLabel(GameEntry game)
    {
        string? name = LauncherName(game);
        return name != null && ClientTarget(game) != null ? $"Open in {name}" : null;
    }

    /// <summary>Opens the launcher's page for the game, or the launcher itself. False when there is nothing to open.</summary>
    public static bool Open(GameEntry game)
    {
        string? name = LauncherName(game);
        var target = ClientTarget(game);
        if (name == null || target == null) return false;
        return ShellOpen.Open(target.Value.Target, "LauncherPages", $"'{game.Name}' in {name}", arguments: target.Value.Arguments);
    }

    /// <summary>What to open for the game's launcher - a link to its page, or the launcher's program and its
    /// arguments - or null when the launcher isn't installed. Reads the registry, so it is asked when the menu is built.</summary>
    private static (string Target, string? Arguments)? ClientTarget(GameEntry game)
    {
        try
        {
            if (game.IsSteamGame && UrlProtocolHelper.IsValidSteamAppId(game.SteamAppId))
                return UrlProtocolHelper.IsHandlerInstalled("steam", nameof(LauncherPages)) ? ($"steam://nav/games/details/{game.SteamAppId}", null) : null;
            if (game.IsGogGame && IsGogId(game.GogGameId))
                return UrlProtocolHelper.IsHandlerInstalled("goggalaxy", nameof(LauncherPages))
                    ? ($"goggalaxy://openGameView/{game.GogGameId}", null)
                    : Program(new GogScannerService().GetGalaxyClientPath()); // Galaxy without its link registered: just open it
            if (game.IsBattleNetGame)
                return Program(new BattleNetScannerService().GetClientPath(), "--exec=\"focus play\"");
            if (game.IsRockstarGame)
                return Program(new RockstarScannerService().GetLauncherPath());
            if (game.IsAmazonGame)
                return Program(new AmazonScannerService().GetClientPath());
            string? scheme = game.IsEpicGame ? "com.epicgames.launcher" : game.IsEaGame ? "origin2" : game.IsUbisoftGame ? "uplay" : null;
            return scheme == null ? null : Program(UrlProtocolHelper.GetHandlerExecutablePath(scheme, nameof(LauncherPages)));
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("LauncherPages", $"Could not find the launcher for '{game.Name}': {ex.Message}");
            return null;
        }
    }

    private static (string, string?)? Program(string? exe, string? arguments = null) =>
        !string.IsNullOrWhiteSpace(exe) && File.Exists(exe) ? (exe, arguments) : null;

    /// <summary>GOG's game ids are digits; nothing else is put in a link.</summary>
    private static bool IsGogId(string? id) => !string.IsNullOrEmpty(id) && id.Length <= 20 && id.All(char.IsAsciiDigit);
}
