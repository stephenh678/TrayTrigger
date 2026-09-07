namespace TrayTrigger.Services;

/// <summary>
/// Title-cleanup tables shared by <see cref="SteamSearchService"/> (matching a local title
/// against Steam search results) and <see cref="GameNameExtractor"/> (deriving a display name
/// from a file/folder name). These used to be duplicated in both files and had already drifted
/// apart (only one of the two release-group lists included "Steam"); kept in one place so a
/// fix to one only needs to be made once. See L-11.
/// </summary>
public static class TitleHeuristics
{
    public static readonly string[] ReleaseGroups =
    [
        "AnkerGames", "FitGirl", "DODI", "ElAmigos", "KaOs", "TENOKE", "RUNE",
        "FLT", "FairLight", "SKIDROW", "CODEX", "Razor1911", "RELOADED", "PLAZA",
        "TiNYiSO", "DARKSiDERS", "EMPRESS", "CPY", "GOG", "Steam", "PROPHET", "HOODLUM",
        "CHRONOS", "VACE", "Goldberg", "ALI213", "3DM"
    ];

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
