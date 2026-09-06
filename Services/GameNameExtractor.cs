using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace TrayTrigger.Services;

public static partial class GameNameExtractor
{
    private static readonly FrozenSet<string> GenericNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "game", "game client", "gameclient", "launcher", "launch", "app", 
        "application", "client", "shipping", "executable", "main", "release", 
        "start", "play", "bin", "desktop", "bootstrapper", "bootstrap", "loader", "run"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] GenericKeywords =
    [
        "crash", "reporter", "reportclient", "setup", "installer", "updater",
        "patcher", "unreal", "unity", "engine", "directx", "redist", "microsoft",
        "windows", "prereq", "easyanticheat", "battleye", "bootstrapper"
    ];

    private static readonly string[] EngineSuffixes =
    [
        "-Win64-Shipping", "-WinGDK-Shipping", "-Win32-Shipping",
        "_Shipping", "-Shipping",
        "_retail_", "_retail",
        "_dx12", "_dx11", "_dx9", "_vulkan",
        "_x64", "_x86", "_64", "_32",
        "_final", "_release", "_preview", "_demo"
    ];

    private static readonly string[] ReleaseGroupSuffixes =
    [
        "AnkerGames", "FitGirl", "DODI", "GOG", "Steam", "CODEX", 
        "RUNE", "TENOKE", "SKIDROW", "FLT", "FairLight", "CPY", "EMPRESS", 
        "Razor1911", "RELOADED", "PLAZA", "TiNYiSO", "DARKSiDERS",
        "ElAmigos", "KaOs", "Goldberg", "ALI213", "3DM", "HOODLUM", "PROPHET", "CHRONOS", "VACE"
    ];

    private static readonly string[] GenericFolders =
    [
        "bin", "binaries", "win64", "win32", "wingdk", "x64", "x86",
        "shipping", "release", "retail", "_retail_", "_retail",
        "game", "app", "client", "engine", "build", "intermediate"
    ];

    private static readonly string[] LibraryFolders =
    [
        "games", "my games", "steamlibrary", "common", "steamapps",
        "gog games", "xboxgames", "installed games", "game library",
        "pc games", "epic games", "ubisoft games", "ea games"
    ];

    private static readonly string[] EditionPhrases =
    [
        "Digital Deluxe Edition", "Deluxe Edition", "Definitive Edition",
        "Director's Cut", "Directors Cut", "Game of the Year Edition", "Game of the Year",
        "GOTY Edition", "GOTY", "Collector's Edition", "Collectors Edition",
        "Anniversary Edition", "Complete Edition", "Premium Edition", "Ultimate Edition",
        "Special Edition", "Enhanced Edition", "Standard Edition", "Limited Edition",
        "Gold Edition", "Silver Edition", "Remastered Edition", "HD Remaster"
    ];

    private static readonly (string Misspelling, string Correction)[] CommonSpellingCorrections =
    [
        (@"\breamek\b", "remake"),
        (@"\bedtion\b", "edition"),
        (@"\bdefinative\b", "definitive"),
        (@"\bdelux\b", "deluxe"),
        (@"\bremasterd\b", "remastered"),
        (@"\bdirectors\b", "director's")
    ];

    /// <summary>
    /// Extracts the best display name for a game, prioritizing the executable's embedded metadata and binary stem.
    /// </summary>
    public static string ExtractGameName(string exePath, string? folderFallback = null, bool preferExe = true)
    {
        string? resolvedFolder = FindMeaningfulFolderName(exePath, folderFallback);

        if (string.IsNullOrWhiteSpace(exePath))
        {
            return !string.IsNullOrWhiteSpace(resolvedFolder) ? CleanFolderName(resolvedFolder) : "Unnamed Game";
        }

        // If user explicitly prefers folder names over exe
        if (!preferExe && !string.IsNullOrWhiteSpace(resolvedFolder))
        {
            string cleanedFolder = CleanFolderName(resolvedFolder);
            if (!string.IsNullOrWhiteSpace(cleanedFolder) && !IsGenericFolder(cleanedFolder))
                return cleanedFolder;
        }

        string rawStem = Path.GetFileNameWithoutExtension(exePath);

        // 1. Inspect PE FileVersionInfo for official product / description title
        if (File.Exists(exePath))
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(exePath);

                string? desc = CleanMetadataTitle(vi.FileDescription);
                if (!string.IsNullOrWhiteSpace(desc) && IsAcceptableTitle(desc))
                {
                    return desc;
                }

                string? prod = CleanMetadataTitle(vi.ProductName);
                if (!string.IsNullOrWhiteSpace(prod) && IsAcceptableTitle(prod))
                {
                    return prod;
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn("GameNameExtractor", $"Error reading FileVersionInfo for '{exePath}': {ex.Message}");
            }
        }

        // 2. Clean executable file stem
        string cleanStem = CleanExecutableStem(rawStem);

        // 3. If clean stem is not generic, use it
        if (!string.IsNullOrWhiteSpace(cleanStem) && !GenericNames.Contains(cleanStem))
        {
            return cleanStem;
        }

        // 4. Fallback to folder name if stem was generic (e.g. game.exe, launcher.exe)
        if (!string.IsNullOrWhiteSpace(resolvedFolder))
        {
            string cleanedFolder = CleanFolderName(resolvedFolder);
            if (!string.IsNullOrWhiteSpace(cleanedFolder) && !IsGenericFolder(cleanedFolder))
            {
                return cleanedFolder;
            }
        }

        // 5. Ultimate fallback
        return string.IsNullOrWhiteSpace(cleanStem) ? rawStem : cleanStem;
    }

    /// <summary>
    /// Resolves the game name, checking local executable metadata first, and optionally querying Steam online for the official commercial title.
    /// </summary>
    public record GameResolutionResult(
        string ResolvedTitle,
        string? SteamAppId,
        string? ThumbnailUrl
    );

    /// <summary>
    /// Resolves game match with dual-pass search (Pass 1: local/exe name, Pass 2: parent folder fallback).
    /// Enforces fuzzy confidence threshold to reject incorrect matches.
    /// </summary>
    public static async Task<GameResolutionResult> ResolveGameMatchAsync(
        string exePath, 
        string? folderFallback = null, 
        bool preferExe = true, 
        bool searchOnline = true, 
        SteamSearchService? steamSearch = null,
        double minConfidence = SteamSearchService.DefaultMinConfidence,
        CancellationToken cancellationToken = default)
    {
        string localName = ExtractGameName(exePath, folderFallback, preferExe);

        if (!searchOnline)
            return new GameResolutionResult(localName, null, null);

        steamSearch ??= new SteamSearchService();

        try
        {
            // Pass 1: Search using local extracted name
            var match1 = await steamSearch.FindBestMatchAsync(localName, minConfidence, cancellationToken).ConfigureAwait(false);
            if (match1 != null && match1.SimilarityScore >= Math.Max(0.85, minConfidence))
            {
                return new GameResolutionResult(match1.Name, match1.AppId, match1.ThumbnailUrl);
            }

            // Pass 2: If Pass 1 wasn't decisive, search using cleaned meaningful folder name
            string folderCandidate = FindMeaningfulFolderName(exePath, folderFallback);
            string cleanedFolder = CleanFolderName(folderCandidate);

            if (!string.IsNullOrWhiteSpace(cleanedFolder) && 
                !cleanedFolder.Equals(localName, StringComparison.OrdinalIgnoreCase) && 
                !IsGenericFolder(cleanedFolder))
            {
                var match2 = await steamSearch.FindBestMatchAsync(cleanedFolder, minConfidence, cancellationToken).ConfigureAwait(false);

                if (match2 != null)
                {
                    if (match1 == null || match2.SimilarityScore > match1.SimilarityScore)
                    {
                        return new GameResolutionResult(match2.Name, match2.AppId, match2.ThumbnailUrl);
                    }
                }
            }

            if (match1 != null && match1.SimilarityScore >= minConfidence)
            {
                return new GameResolutionResult(match1.Name, match1.AppId, match1.ThumbnailUrl);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("GameNameExtractor", $"Online matching failed for '{localName}': {ex.Message}");
        }

        // Safe fallback: preserve clean local name, do not attach incorrect SteamAppId
        return new GameResolutionResult(localName, null, null);
    }

    /// <summary>
    /// Resolves the game name, checking local executable metadata first, and optionally querying Steam online for the official commercial title.
    /// </summary>
    public static async Task<string> ResolveGameNameAsync(
        string exePath, 
        string? folderFallback = null, 
        bool preferExe = true, 
        bool searchOnline = true, 
        SteamSearchService? steamSearch = null,
        double minConfidence = SteamSearchService.DefaultMinConfidence,
        CancellationToken cancellationToken = default)
    {
        var result = await ResolveGameMatchAsync(exePath, folderFallback, preferExe, searchOnline, steamSearch, minConfidence, cancellationToken).ConfigureAwait(false);
        return result.ResolvedTitle;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])")]
    private static partial Regex CamelCaseBoundaryRegex();

    [GeneratedRegex(@"(?<=[a-zA-Z])(?=[0-9])")]
    private static partial Regex LetterNumberBoundaryRegex();

    [GeneratedRegex(@"(?<=[0-9])(?=[a-zA-Z])")]
    private static partial Regex NumberLetterBoundaryRegex();

    [GeneratedRegex(@"\[.*?\]")]
    private static partial Regex BracketedAnnotationsRegex();

    [GeneratedRegex(@"\(.*?\)", RegexOptions.IgnoreCase)]
    private static partial Regex ParenthesesAnnotationsRegex();

    [GeneratedRegex(@"\b(build\s*\d+|patch\s*\d+|update\s*\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BuildRegex();

    [GeneratedRegex(@"\bv?\d+(\.\d+){1,3}[a-z]?\b", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"\b(x64|x86|win64|win32|repack|portable|rip|steamrip|gog|multi\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ClutterWordRegex();

    public static string CleanMetadataTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        string cleaned = title;

        // Strip trademark and copyright symbols
        cleaned = cleaned.Replace("®", "").Replace("™", "").Replace("©", "")
                         .Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
                         .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
                         .Replace("(C)", "", StringComparison.OrdinalIgnoreCase);

        // Collapse whitespace
        cleaned = WhitespaceRegex().Replace(cleaned, " ").Trim();
        return cleaned;
    }

    public static bool IsAcceptableTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        if (title.Length < 2 || title.Length > 80) return false;

        string lower = title.ToLowerInvariant();

        if (GenericNames.Contains(lower)) return false;

        foreach (var kw in GenericKeywords)
        {
            if (lower.Contains(kw)) return false;
        }

        return true;
    }

    public static string CleanExecutableStem(string rawStem)
    {
        if (string.IsNullOrWhiteSpace(rawStem)) return string.Empty;

        string stem = rawStem;

        // Strip known engine / compilation suffixes
        foreach (var suffix in EngineSuffixes)
        {
            if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                stem = stem.Substring(0, stem.Length - suffix.Length);
            }
            else
            {
                stem = stem.Replace(suffix, "", StringComparison.OrdinalIgnoreCase);
            }
        }

        // Replace delimiters with spaces
        stem = stem.Replace('_', ' ').Replace('-', ' ').Replace('.', ' ');

        // Split CamelCase / PascalCase and number boundaries
        stem = CamelCaseBoundaryRegex().Replace(stem, " ");
        stem = LetterNumberBoundaryRegex().Replace(stem, " ");
        stem = NumberLetterBoundaryRegex().Replace(stem, " ");

        // Collapse multiple spaces
        stem = WhitespaceRegex().Replace(stem, " ").Trim();

        if (string.IsNullOrWhiteSpace(stem))
            return rawStem;

        // If all lowercase, capitalize each word for clean presentation
        if (stem.All(c => !char.IsLetter(c) || char.IsLower(c)))
        {
            stem = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(stem);
        }

        return stem;
    }

    /// <summary>
    /// Cleans folder names by removing release tags, repackers, editions, versions, and correcting common typos.
    /// </summary>
    public static string CleanFolderName(string folderPathOrName)
    {
        if (string.IsNullOrWhiteSpace(folderPathOrName)) return string.Empty;

        string folderName = Path.GetFileName(folderPathOrName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(folderName)) return string.Empty;

        string cleaned = folderName;

        // 1. Correct common spelling mistakes
        foreach (var (pattern, correction) in CommonSpellingCorrections)
        {
            cleaned = Regex.Replace(cleaned, pattern, correction, RegexOptions.IgnoreCase);
        }

        // 2. Strip bracketed & parenthesized annotations like [FitGirl Repack], [DODI], (MULTi12), etc.
        cleaned = BracketedAnnotationsRegex().Replace(cleaned, " ");
        cleaned = ParenthesesAnnotationsRegex().Replace(cleaned, " ");

        // 3. Strip known release group / repack tags
        foreach (var grp in ReleaseGroupSuffixes)
        {
            cleaned = Regex.Replace(cleaned, $@"(?:^|[-_.\s])+{Regex.Escape(grp)}(?:[-_.\s]|$)+", " ", RegexOptions.IgnoreCase);
        }

        // 4. Strip edition tags
        foreach (var ed in EditionPhrases)
        {
            cleaned = Regex.Replace(cleaned, $@"\b{Regex.Escape(ed)}\b", " ", RegexOptions.IgnoreCase);
        }

        // 5. Strip build and version tags
        cleaned = BuildRegex().Replace(cleaned, " ");
        cleaned = VersionRegex().Replace(cleaned, " ");
        cleaned = ClutterWordRegex().Replace(cleaned, " ");

        // 6. Replace separators with space
        cleaned = cleaned.Replace('_', ' ').Replace('-', ' ').Replace('.', ' ');

        // 7. Collapse whitespace
        cleaned = WhitespaceRegex().Replace(cleaned, " ").Trim();

        // 8. If all lowercase or uppercase with spaces, capitalize words for clean presentation
        if (!string.IsNullOrWhiteSpace(cleaned) && (cleaned.All(c => !char.IsLetter(c) || char.IsLower(c)) || cleaned.All(c => !char.IsLetter(c) || char.IsUpper(c))))
        {
            cleaned = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(cleaned.ToLowerInvariant());
        }

        return cleaned;
    }

    /// <summary>
    /// Walks up the directory tree from an executable or working directory to locate the true root game folder,
    /// bypassing generic subdirectories like Binaries\Win64 and stopping at library roots like C:\Games.
    /// </summary>
    public static string FindMeaningfulFolderName(string? exePath, string? folderFallback = null)
    {
        // 1. Check folderFallback if explicitly provided
        if (!string.IsNullOrWhiteSpace(folderFallback))
        {
            string candidate = folderFallback.Trim();
            if (candidate.Contains(Path.DirectorySeparatorChar) || candidate.Contains(Path.AltDirectorySeparatorChar))
            {
                try
                {
                    var dir = new DirectoryInfo(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    while (dir != null && IsGenericFolder(dir.Name))
                    {
                        dir = dir.Parent;
                    }

                    if (dir != null && !IsGenericFolder(dir.Name) && !IsLibraryFolder(dir.Name))
                    {
                        return dir.Name;
                    }
                }
                catch { }
            }
            else if (!IsGenericFolder(candidate) && !IsLibraryFolder(candidate))
            {
                return candidate;
            }
        }

        // 2. Walk up directory tree from executable path
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            try
            {
                var dir = Directory.GetParent(exePath);
                string? topNonLibrary = null;

                while (dir != null)
                {
                    string name = dir.Name;
                    if (IsLibraryFolder(name))
                    {
                        // Hit library root (e.g. C:\Games, D:\SteamLibrary\steamapps\common).
                        // The folder directly under the library root is the true game folder!
                        break;
                    }

                    if (!IsGenericFolder(name))
                    {
                        topNonLibrary = name;
                    }

                    dir = dir.Parent;
                }

                if (!string.IsNullOrWhiteSpace(topNonLibrary))
                {
                    return topNonLibrary;
                }
            }
            catch { }
        }

        return folderFallback ?? string.Empty;
    }

    public static bool IsGenericFolder(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return true;
        string lower = folderName.Trim().ToLowerInvariant();
        return GenericFolders.Contains(lower);
    }

    public static bool IsLibraryFolder(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return false;
        string lower = folderName.Trim().ToLowerInvariant();
        return LibraryFolders.Contains(lower);
    }
}
