namespace TrayTrigger.Services;

/// <summary>
/// Title-cleanup tables shared by <see cref="SteamSearchService"/> (matching a local title
/// against Steam search results) and <see cref="GameNameExtractor"/> (deriving a display name
/// from a file/folder name). These used to be duplicated in both files and had already drifted
/// apart (only one of the two tag lists included "Steam"); kept in one place so a fix to one
/// only needs to be made once. See L-11.
/// </summary>
public static class TitleHeuristics
{
    /// <summary>
    /// Storefront names people append to their own folder names ("Portal.2.Steam"). Deliberately
    /// not a list of uploaders or release groups: TrayTrigger doesn't care where a game came from,
    /// and a trailing word Steam doesn't recognise is handled generically instead - see
    /// GameNameExtractor.FindMatchForShortenedFolderNameAsync.
    /// </summary>
    public static readonly string[] StoreNames = ["GOG", "Steam"];

    /// <summary>
    /// A store name as a trailing tag ("Portal.2.Steam", "GTA V GOG"): only at the end and only
    /// after a separator, so a title that merely contains the word keeps it. Stripping the word
    /// anywhere sent Steam a search for "Tactics" for "Steam Tactics", and "Full Ahead" for "Full
    /// Steam Ahead". Apply it after versions and edition tags are gone, so they can't hide the tag.
    /// </summary>
    public static readonly System.Text.RegularExpressions.Regex TrailingStoreNameRegex = new(
        $@"(?:[-_.\s]+(?:{string.Join("|", System.Array.ConvertAll(StoreNames, System.Text.RegularExpressions.Regex.Escape))}))+[-_.\s]*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static readonly string[] EditionPhrases =
    [
        "Digital Deluxe Edition", "Deluxe Edition", "Definitive Edition",
        "Director's Cut", "Directors Cut", "Game of the Year Edition", "Game of the Year",
        "GOTY Edition", "GOTY", "Collector's Edition", "Collectors Edition",
        "Anniversary Edition", "Complete Edition", "Premium Edition", "Ultimate Edition",
        "Special Edition", "Enhanced Edition", "Standard Edition", "Limited Edition",
        "Gold Edition", "Silver Edition", "Remastered Edition", "HD Remaster"
    ];

    public static readonly (string Misspelling, string Correction)[] CommonSpellingCorrections =
    [
        (@"\breamek\b", "remake"),
        (@"\bedtion\b", "edition"),
        (@"\bdefinative\b", "definitive"),
        (@"\bdelux\b", "deluxe"),
        (@"\bremasterd\b", "remastered"),
        (@"\bdirectors\b", "director's")
    ];
}
