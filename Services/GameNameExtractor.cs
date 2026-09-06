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
        "-AnkerGames", "-FitGirl", "-DODI", "-GOG", "-Steam", "-CODEX", 
        "-RUNE", "-TENOKE", "-SKIDROW", "-FLT", "-CPY", "-EMPRESS", 
        "-Razor1911", "-RELOADED", "-PLAZA", "-TiNYiSO", "-DARKSiDERS"
    ];

    /// <summary>
    /// Extracts the best display name for a game, prioritizing the executable's embedded metadata and binary stem.
    /// </summary>
    public static string ExtractGameName(string exePath, string? folderFallback = null, bool preferExe = true)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return !string.IsNullOrWhiteSpace(folderFallback) ? CleanFolderName(folderFallback) : "Unnamed Game";
        }

        // If user explicitly prefers folder names over exe
        if (!preferExe && !string.IsNullOrWhiteSpace(folderFallback))
        {
            string cleanedFolder = CleanFolderName(folderFallback);
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
        if (!string.IsNullOrWhiteSpace(folderFallback))
        {
            string cleanedFolder = CleanFolderName(folderFallback);
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

    public static async Task<GameResolutionResult> ResolveGameMatchAsync(
        string exePath, 
        string? folderFallback = null, 
        bool preferExe = true, 
        bool searchOnline = true, 
        SteamSearchService? steamSearch = null,
        CancellationToken cancellationToken = default)
    {
        string localName = ExtractGameName(exePath, folderFallback, preferExe);

        if (!searchOnline)
            return new GameResolutionResult(localName, null, null);

        steamSearch ??= new SteamSearchService();

        try
        {
            var match = await steamSearch.FindBestMatchAsync(localName, cancellationToken).ConfigureAwait(false);
            if (match != null && !string.IsNullOrWhiteSpace(match.Name))
            {
                return new GameResolutionResult(match.Name, match.AppId, match.ThumbnailUrl);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("GameNameExtractor", $"Online matching failed for '{localName}': {ex.Message}");
        }

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
        CancellationToken cancellationToken = default)
    {
        var result = await ResolveGameMatchAsync(exePath, folderFallback, preferExe, searchOnline, steamSearch, cancellationToken).ConfigureAwait(false);
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

    [GeneratedRegex(@"\(.*?(repack|gog|steam|rip|edition).*?\)", RegexOptions.IgnoreCase)]
    private static partial Regex ParenthesesAnnotationsRegex();

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

    public static string CleanFolderName(string folderPathOrName)
    {
        if (string.IsNullOrWhiteSpace(folderPathOrName)) return string.Empty;

        string folderName = Path.GetFileName(folderPathOrName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(folderName)) return string.Empty;

        string cleaned = folderName;

        // Strip known release group / repack suffixes
        foreach (var grp in ReleaseGroupSuffixes)
        {
            if (cleaned.EndsWith(grp, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Substring(0, cleaned.Length - grp.Length);
            }
            else
            {
                cleaned = cleaned.Replace(grp, "", StringComparison.OrdinalIgnoreCase);
            }
        }

        // Strip bracketed annotations like [FitGirl Repack], [DODI], etc.
        cleaned = BracketedAnnotationsRegex().Replace(cleaned, " ");
        cleaned = ParenthesesAnnotationsRegex().Replace(cleaned, " ");

        // Replace separators
        cleaned = cleaned.Replace('_', ' ').Replace('-', ' ').Replace('.', ' ');

        // Collapse whitespace
        cleaned = WhitespaceRegex().Replace(cleaned, " ").Trim();

        return cleaned;
    }

    private static bool IsGenericFolder(string folderName)
    {
        string lower = folderName.ToLowerInvariant();
        return lower is "games" or "common" or "steamapps" or "steamlibrary" or "bin" or "binaries" or "win64" or "x64";
    }
}
