using System;
using System.IO;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>Which code path <see cref="ProcessLauncherService.LaunchGame"/> takes for an entry.</summary>
public enum LaunchRoute
{
    /// <summary>ExecutablePath is blank.</summary>
    MissingPath,
    /// <summary>"steam://rungameid/&lt;id&gt;" (or "steam://run/&lt;id&gt;//&lt;args&gt;/") dispatch, tracked via Steam's Running flag.</summary>
    Steam,
    /// <summary>GalaxyClient.exe /command=runGame - only when Galaxy is installed and not already running.</summary>
    GogGalaxy,
    /// <summary>GOG game's own exe, tracked by install directory.</summary>
    GogDirect,
    /// <summary>origin2://game/launch dispatch, tracked by install directory.</summary>
    EaClient,
    EaDirect,
    /// <summary>com.epicgames.launcher://apps/&lt;name&gt;?action=launch dispatch, tracked by install directory.</summary>
    EpicClient,
    EpicDirect,
    /// <summary>uplay://launch/&lt;id&gt;/0 dispatch, tracked by install directory.</summary>
    UbisoftClient,
    UbisoftDirect,
    /// <summary>Shell activation of the game's AUMID (PC Game Pass / Store GDK title), tracked by
    /// package root. No direct variant: GDK exes refuse to start without package identity.</summary>
    Xbox,
    /// <summary>A non-file URL whose scheme is on the allow-list: fire-and-forget, pre-launch script only.</summary>
    ProtocolUrl,
    /// <summary>A non-file URL whose scheme is NOT on the allow-list: refused.</summary>
    RefusedUrl,
    /// <summary>A plain local executable: one process, one session.</summary>
    DirectExe
}

/// <summary>What the launcher knows about installed platform clients at launch time.</summary>
public readonly record struct LaunchClientAvailability(
    bool GogGalaxyInstalled,
    bool GogGalaxyRunning,
    bool EaAppInstalled,
    bool EpicLauncherInstalled,
    bool UbisoftConnectInstalled);

/// <summary>
/// The pure decision half of the launcher: given an entry and which clients exist, which route is
/// taken. Kept free of process/registry access so the whole matrix is unit-testable
/// (see LaunchRoutingTests).
/// </summary>
public static class LaunchRouter
{
    public static LaunchRoute Resolve(GameEntry game, LaunchClientAvailability clients, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        string path = game.ExecutablePath ?? string.Empty;

        // A Steam-tagged entry whose user asked for a direct launch and whose ExecutablePath is a
        // real file (not a steam:// URL) is a plain exe - Edit Game's "launch this executable
        // directly" option.
        bool isSteamUrl = path.StartsWith("steam://", StringComparison.OrdinalIgnoreCase);
        bool directSteamExe = game.LaunchDirectly && !isSteamUrl && !string.IsNullOrWhiteSpace(path) && fileExists(path);
        if ((game.IsSteamGame || isSteamUrl) && !directSteamExe)
        {
            return LaunchRoute.Steam;
        }

        if (game.IsGogGame && !string.IsNullOrWhiteSpace(game.GogGameId))
        {
            // Galaxy's command-line launch only starts the game silently on a cold start; when
            // Galaxy is already open the same command just shows the game's page with a Play
            // button, so a direct exe launch gets the same features without the extra click.
            bool viaGalaxy = !game.LaunchDirectly && clients.GogGalaxyInstalled && !clients.GogGalaxyRunning;
            return viaGalaxy ? LaunchRoute.GogGalaxy : LaunchRoute.GogDirect;
        }

        if (game.IsEaGame && !string.IsNullOrWhiteSpace(game.EaContentId))
        {
            return !game.LaunchDirectly && clients.EaAppInstalled ? LaunchRoute.EaClient : LaunchRoute.EaDirect;
        }

        if (game.IsEpicGame && !string.IsNullOrWhiteSpace(game.EpicAppName))
        {
            return !game.LaunchDirectly && clients.EpicLauncherInstalled ? LaunchRoute.EpicClient : LaunchRoute.EpicDirect;
        }

        if (game.IsUbisoftGame && !string.IsNullOrWhiteSpace(game.UbisoftGameId))
        {
            return !game.LaunchDirectly && clients.UbisoftConnectInstalled ? LaunchRoute.UbisoftClient : LaunchRoute.UbisoftDirect;
        }

        // LaunchDirectly is deliberately ignored: there is no way to run a GDK exe outside its
        // package, so Edit Game hides the option for Xbox entries.
        if (game.IsXboxGame && !string.IsNullOrWhiteSpace(game.XboxAumid))
        {
            return LaunchRoute.Xbox;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return LaunchRoute.MissingPath;
        }

        if (ProcessLauncherService.IsNonFileProtocolUrl(path))
        {
            return UrlProtocolHelper.IsAllowedLaunchUrl(path) ? LaunchRoute.ProtocolUrl : LaunchRoute.RefusedUrl;
        }

        return LaunchRoute.DirectExe;
    }

    /// <summary>
    /// The Steam URL for an AppId. "steam://rungameid/&lt;id&gt;" cannot carry launch options;
    /// "steam://run/&lt;id&gt;//&lt;args&gt;/" can (Valve's documented form, with spaces as %20), so the
    /// Arguments box in Edit Game actually does something for Steam games.
    /// </summary>
    public static string BuildSteamLaunchUrl(string appId, string? arguments)
    {
        string args = arguments?.Trim() ?? string.Empty;
        if (args.Length == 0)
        {
            return $"steam://rungameid/{appId}";
        }
        // Only spaces need encoding for Steam's own parser; escaping "/" or ":" would corrupt
        // path-style options like -config "C:\foo". A "/" would end the URL early, so encode that too.
        string encoded = args.Replace("%", "%25").Replace(" ", "%20").Replace("/", "%2F");
        return $"steam://run/{appId}//{encoded}/";
    }

    /// <summary>Which platform client, if any, a route launches through - for "close launcher after exit".</summary>
    public static LauncherPlatform? ClientPlatformFor(LaunchRoute route) => route switch
    {
        LaunchRoute.Steam => LauncherPlatform.Steam,
        LaunchRoute.GogGalaxy or LaunchRoute.GogDirect => LauncherPlatform.Gog,
        LaunchRoute.EaClient or LaunchRoute.EaDirect => LauncherPlatform.Ea,
        LaunchRoute.EpicClient or LaunchRoute.EpicDirect => LauncherPlatform.Epic,
        LaunchRoute.UbisoftClient or LaunchRoute.UbisoftDirect => LauncherPlatform.Ubisoft,
        LaunchRoute.Xbox => LauncherPlatform.Xbox,
        _ => null
    };

    public static string PlatformLabelFor(LaunchRoute route) => route switch
    {
        LaunchRoute.Steam => "Steam",
        LaunchRoute.GogGalaxy => "GOG Galaxy",
        LaunchRoute.GogDirect => "GOG",
        LaunchRoute.EaClient => "EA App",
        LaunchRoute.EaDirect => "EA",
        LaunchRoute.EpicClient or LaunchRoute.EpicDirect => "Epic Games",
        LaunchRoute.UbisoftClient or LaunchRoute.UbisoftDirect => "Ubisoft Connect",
        LaunchRoute.Xbox => "Xbox",
        LaunchRoute.ProtocolUrl => "link",
        _ => "Local"
    };
}
