using System;

namespace TrayTrigger.Services;

/// <summary>
/// The address of a game's PCGamingWiki page, for the details window's "PCGamingWiki" button.
/// A Steam game goes by its App ID, which the wiki's own redirect resolves to the exact page.
/// Any other game goes to the wiki's search for its name, which opens the page straight away
/// when the title matches and lists the closest pages when it does not. Nothing is fetched:
/// the link opens in the user's browser.
/// </summary>
public static class PcGamingWikiLink
{
    private const string Host = "https://www.pcgamingwiki.com";

    /// <summary>Null when the game has neither a usable Steam App ID nor a name.</summary>
    public static string? For(string? steamAppId, string? gameName)
    {
        string appId = steamAppId?.Trim() ?? string.Empty;
        if (appId.Length > 0 && IsDigits(appId))
        {
            return $"{Host}/api/appid.php?appid={appId}";
        }

        string name = gameName?.Trim() ?? string.Empty;
        return name.Length == 0
            ? null
            : $"{Host}/w/index.php?search={Uri.EscapeDataString(name)}";
    }

    private static bool IsDigits(string value)
    {
        foreach (char c in value)
        {
            if (c < '0' || c > '9') return false;
        }
        return true;
    }
}
