using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TrayTrigger.Services;

public record SteamGameMatch(
    string Name,
    string AppId,
    string? ThumbnailUrl,
    double SimilarityScore = 1.0
);

public partial class SteamSearchService
{
    public const double DefaultMinConfidence = 0.60;

    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        EnableMultipleHttp2Connections = true
    })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private static readonly ConcurrentDictionary<string, List<SteamGameMatch>> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Searches still in flight, so concurrent callers asking the same question wait on one
    /// request instead of each issuing their own. A folder import enriches several games at once
    /// (ImportCoordinator's MaxConcurrentEnrichments), and games under one tree routinely derive
    /// the same folder-name query - the completed-result cache alone cannot help there, because
    /// none of them has finished to populate it yet.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Task<List<SteamGameMatch>>> InFlight = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] PenaltyKeywords =
    [
        "soundtrack", "ost", "demo", "teaser", "prologue", "artbook", 
        "trailer", "dlc", "expansion", "skin pack", "dedicated server", "server"
    ];

    private static readonly char[] WordSeparators = [' ', '\t', ':', '-', '_', '.', ',', '!', '?', '\'', '"', '`', '/', '\\', '(', ')', '[', ']', '{', '}'];

    [GeneratedRegex(@"data-ds-appid=""(?<id>\d+)"".*?<div class=""match_name"">(?<name>[^<]+)</div>(?:.*?<div class=""match_img""><img src=""(?<img>[^""]+)"")?", RegexOptions.Singleline)]
    private static partial Regex SuggestItemRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\[.*?\]")]
    private static partial Regex BracketClutterRegex();

    [GeneratedRegex(@"\(.*?\)", RegexOptions.IgnoreCase)]
    private static partial Regex ParenthesesClutterRegex();

    [GeneratedRegex(@"\b(build\s*\d+|patch\s*\d+|update\s*\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BuildNumberRegex();

    [GeneratedRegex(@"\bv?\d+(\.\d+){1,3}[a-z]?\b", RegexOptions.IgnoreCase)]
    private static partial Regex VersionNumberRegex();

    [GeneratedRegex(@"\b(x64|x86|win64|win32|repack|portable|rip|steamrip)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TechClutterRegex();

    /// <summary>
    /// Sanitizes game title query by fixing common typos, stripping release groups, clutter, and edition tags.
    /// </summary>
    public static string SanitizeSearchQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;

        string cleaned = query;

        // 1. Correct common spelling mistakes
        foreach (var (pattern, correction) in TitleHeuristics.CommonSpellingCorrections)
        {
            cleaned = Regex.Replace(cleaned, pattern, correction, RegexOptions.IgnoreCase);
        }

        // 2. Strip bracketed & parenthesized annotations
        cleaned = BracketClutterRegex().Replace(cleaned, " ");
        cleaned = ParenthesesClutterRegex().Replace(cleaned, " ");

        // 3. Strip release groups
        foreach (var grp in TitleHeuristics.ReleaseGroups)
        {
            cleaned = Regex.Replace(cleaned, $@"\b{Regex.Escape(grp)}\b", "", RegexOptions.IgnoreCase);
        }

        // 4. Strip edition tags (which often break Steam API search matching)
        foreach (var ed in TitleHeuristics.EditionPhrases)
        {
            cleaned = Regex.Replace(cleaned, $@"\b{Regex.Escape(ed)}\b", "", RegexOptions.IgnoreCase);
        }

        // 5. Strip builds, version numbers, and tech architecture tags
        cleaned = BuildNumberRegex().Replace(cleaned, "");
        cleaned = VersionNumberRegex().Replace(cleaned, "");
        cleaned = TechClutterRegex().Replace(cleaned, "");

        // 6. Replace delimiters with space
        cleaned = cleaned.Replace('.', ' ').Replace('_', ' ').Replace('-', ' ');

        // 7. Collapse spaces
        cleaned = WhitespaceRegex().Replace(cleaned, " ").Trim();

        return cleaned;
    }

    /// <summary>
    /// Searches Steam store for games matching query. Uses primary storesearch API and falls back to Steam Suggest endpoint.
    /// </summary>
    public async Task<List<SteamGameMatch>> SearchGamesAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        string trimmed = query.Trim();
        if (Cache.TryGetValue(trimmed, out var cached))
            return cached;

        // Claim the term with a placeholder before doing any work: GetOrAdd's factory overload
        // can run more than once under contention, and a second factory run would fire the very
        // HTTP request this exists to avoid. Whoever's own task lands in the dictionary owns the
        // search; everyone else awaits it.
        var claim = new TaskCompletionSource<List<SteamGameMatch>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = InFlight.GetOrAdd(trimmed, claim.Task);

        if (ReferenceEquals(pending, claim.Task))
        {
            try
            {
                // Deliberately not the caller's token: the result is shared, so one caller
                // walking away must not cancel the search the others are waiting on. They each
                // stop waiting on their own token below instead.
                claim.SetResult(await SearchGamesUncachedAsync(trimmed, CancellationToken.None).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                claim.SetException(ex);
            }
            finally
            {
                InFlight.TryRemove(new KeyValuePair<string, Task<List<SteamGameMatch>>>(trimmed, claim.Task));
            }
        }

        return await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The one network round trip, behind the cache and the in-flight claim. Virtual purely as a
    /// test seam: TrayTrigger.Tests overrides it to count how many searches actually escape.
    /// </summary>
    internal virtual async Task<List<SteamGameMatch>> SearchGamesUncachedAsync(string trimmed, CancellationToken cancellationToken)
    {
        string sanitized = SanitizeSearchQuery(trimmed);
        string effectiveQuery = !string.IsNullOrWhiteSpace(sanitized) ? sanitized : trimmed;

        LoggingService.Verbose("SteamSearch", $"Searching Steam for query='{trimmed}' (sanitized='{effectiveQuery}')");

        var results = await QueryStoreSearchApiAsync(effectiveQuery, cancellationToken).ConfigureAwait(false);

        // If primary API returned nothing, and sanitized was different, try original query
        if (results.Count == 0 && !sanitized.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            LoggingService.Verbose("SteamSearch", $"Sanitized search yielded 0 results; retrying raw query='{trimmed}'");
            results = await QueryStoreSearchApiAsync(trimmed, cancellationToken).ConfigureAwait(false);
        }

        // If still nothing, fallback to Steam Suggest endpoint
        if (results.Count == 0)
        {
            LoggingService.Verbose("SteamSearch", $"Primary storesearch API yielded 0 results; querying Suggest endpoint for '{effectiveQuery}'");
            results = await QuerySuggestApiAsync(effectiveQuery, cancellationToken).ConfigureAwait(false);
        }

        LoggingService.Verbose("SteamSearch", $"Total {results.Count} candidate(s) found for '{trimmed}'");
        Cache[trimmed] = results;
        return results;
    }

    private async Task<List<SteamGameMatch>> QueryStoreSearchApiAsync(string term, CancellationToken cancellationToken)
    {
        string url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term)}&l=english&cc=US";

        try
        {
            using var response = await HttpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return [];

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            var root = doc.RootElement;
            var results = new List<SteamGameMatch>();

            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    string? type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type != null && !type.Equals("app", StringComparison.OrdinalIgnoreCase))
                        continue; // Skip DLCs, soundtracks, packages

                    string? name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? id = null;
                    if (item.TryGetProperty("id", out var idProp))
                    {
                        id = idProp.ValueKind == JsonValueKind.Number ? idProp.GetInt64().ToString() : idProp.GetString();
                    }

                    string? tinyImage = item.TryGetProperty("tiny_image", out var img) ? img.GetString() : null;

                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id))
                    {
                        results.Add(new SteamGameMatch(name.Trim(), id, tinyImage));
                    }
                }
            }

            return results;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SteamSearchService", $"Storesearch error for '{term}': {ex.Message}");
            return [];
        }
    }

    private async Task<List<SteamGameMatch>> QuerySuggestApiAsync(string term, CancellationToken cancellationToken)
    {
        string url = $"https://store.steampowered.com/search/suggest?term={Uri.EscapeDataString(term)}&f=games&cc=US&l=english";

        try
        {
            using var response = await HttpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return [];

            string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(html))
                return [];

            var results = new List<SteamGameMatch>();
            var matches = SuggestItemRegex().Matches(html);

            foreach (Match m in matches)
            {
                string id = m.Groups["id"].Value;
                string rawName = m.Groups["name"].Value;
                string? img = m.Groups["img"].Success ? m.Groups["img"].Value : null;

                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(rawName))
                {
                    string decodedName = WebUtility.HtmlDecode(rawName).Trim();
                    results.Add(new SteamGameMatch(decodedName, id, img));
                }
            }

            return results;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SteamSearchService", $"Suggest error for '{term}': {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Finds the best game match on Steam, evaluating fuzzy similarity against confidence threshold.
    /// Returns null if highest match similarity is below minConfidence threshold.
    /// </summary>
    public async Task<SteamGameMatch?> FindBestMatchAsync(
        string query, 
        double minConfidence = DefaultMinConfidence, 
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var candidates = await SearchGamesAsync(query, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
            return null;

        string sanitizedQuery = SanitizeSearchQuery(query);

        // Score each candidate against both raw query and sanitized query
        SteamGameMatch? bestMatch = null;
        double highestScore = -1.0;

        foreach (var c in candidates)
        {
            double scoreRaw = CalculateSimilarity(query, c.Name);
            double scoreSanitized = !string.IsNullOrWhiteSpace(sanitizedQuery) 
                ? CalculateSimilarity(sanitizedQuery, c.Name) 
                : 0.0;

            double score = Math.Max(scoreRaw, scoreSanitized);
            LoggingService.Verbose("SteamSearch", $"Candidate '{c.Name}' (AppId: {c.AppId}) score={score:F2} (raw={scoreRaw:F2}, sanitized={scoreSanitized:F2})");

            if (score > highestScore)
            {
                highestScore = score;
                bestMatch = c with { SimilarityScore = score };
            }
        }

        if (bestMatch != null && highestScore >= minConfidence)
        {
            LoggingService.Verbose("SteamSearch", $"Accepted best match for '{query}': '{bestMatch.Name}' (AppId: {bestMatch.AppId}) [score {highestScore:F2} >= {minConfidence:F2}]");
            return bestMatch;
        }

        LoggingService.Info("SteamSearchService", $"Best match for '{query}' was '{bestMatch?.Name}' (AppId: {bestMatch?.AppId}) with score {highestScore:F2} < threshold {minConfidence:F2}. Rejected to prevent incorrect identification.");
        return null;
    }

    /// <summary>
    /// Calculates robust similarity between a query/local name and candidate title (0.0 to 1.0).
    /// Combines word-token overlap with normalized edit distance.
    /// </summary>
    public static double CalculateSimilarity(string query, string candidateTitle)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidateTitle))
            return 0.0;

        if (query.Equals(candidateTitle, StringComparison.OrdinalIgnoreCase))
            return 1.0;

        string normQuery = NormalizeForMatching(query);
        string normCand = NormalizeForMatching(candidateTitle);

        if (normQuery.Equals(normCand, StringComparison.OrdinalIgnoreCase))
            return 1.0;

        // A near-empty title carries almost no signal, and the edit-distance terms below reward
        // that: 'P 3 R' scored 0.71 against 'P-T-R' and 0.71 against 'P.3', both clearing the
        // 0.60 bar and handing the library a different game's genre and poster. Under this many
        // characters, demand the compacted forms be identical - 'Doom' still matches 'DOOM', and
        // no longer matches 'Doom II'.
        string compactQuery = CompactAlphanumeric(normQuery);
        string compactCand = CompactAlphanumeric(normCand);
        if (compactQuery.Length <= MinFuzzyMatchLength)
        {
            return compactQuery.Equals(compactCand, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
        }

        string[] qTokens = normQuery.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
        string[] cTokens = normCand.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);

        if (qTokens.Length == 0 || cTokens.Length == 0)
            return 0.0;

        // 1. Token-level matching with Levenshtein tolerance for typos
        double queryTokenScoreSum = 0;
        foreach (var qTok in qTokens)
        {
            double bestTokScore = 0;
            foreach (var cTok in cTokens)
            {
                if (qTok.Equals(cTok, StringComparison.OrdinalIgnoreCase))
                {
                    bestTokScore = 1.0;
                    break;
                }

                // Check fuzzy token similarity (e.g. "reamek" vs "remake")
                int dist = LevenshteinDistance(qTok, cTok);
                int maxLen = Math.Max(qTok.Length, cTok.Length);
                if (maxLen > 0)
                {
                    double tokSim = 1.0 - ((double)dist / maxLen);
                    if (tokSim >= 0.65 && tokSim > bestTokScore)
                    {
                        bestTokScore = tokSim;
                    }
                }
            }
            queryTokenScoreSum += bestTokScore;
        }

        double queryCoverage = queryTokenScoreSum / qTokens.Length;

        // Candidate token coverage (checks how much extra noise candidate has)
        double candTokenScoreSum = 0;
        foreach (var cTok in cTokens)
        {
            double bestTokScore = 0;
            foreach (var qTok in qTokens)
            {
                if (cTok.Equals(qTok, StringComparison.OrdinalIgnoreCase))
                {
                    bestTokScore = 1.0;
                    break;
                }

                int dist = LevenshteinDistance(cTok, qTok);
                int maxLen = Math.Max(cTok.Length, qTok.Length);
                if (maxLen > 0)
                {
                    double tokSim = 1.0 - ((double)dist / maxLen);
                    if (tokSim >= 0.65 && tokSim > bestTokScore)
                    {
                        bestTokScore = tokSim;
                    }
                }
            }
            candTokenScoreSum += bestTokScore;
        }

        double candCoverage = candTokenScoreSum / cTokens.Length;
        double tokenScore = (queryCoverage * 0.70) + (candCoverage * 0.30);

        // 2. Full string Levenshtein distance
        int fullDist = LevenshteinDistance(normQuery, normCand);
        int fullMaxLen = Math.Max(normQuery.Length, normCand.Length);
        double levScore = fullMaxLen > 0 ? 1.0 - ((double)fullDist / fullMaxLen) : 1.0;

        // 3. Combined weighted score
        double finalScore = (tokenScore * 0.65) + (levScore * 0.35);

        // 4. Penalize unwanted candidate types (soundtrack, demo, dlc, server) if query didn't ask for them
        // 3b. A disagreeing number is usually the entire difference between two titles that are
        // otherwise the same string, and the weighted score barely notices one digit: 'Portal 2'
        // scored 0.63 against 'Portal 3', 'Persona 3' 0.64 against 'Persona 5' and 'Doom' 0.79
        // against 'Doom II' - every one of them accepted as a match.
        if (NumericTokensDisagree(qTokens, cTokens))
        {
            finalScore *= NumericMismatchPenalty;
        }

        foreach (var kw in PenaltyKeywords)
        {
            if (normCand.Contains(kw, StringComparison.OrdinalIgnoreCase) && 
                !normQuery.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                finalScore *= 0.65;
            }
        }

        return Math.Clamp(finalScore, 0.0, 1.0);
    }

    /// <summary>
    /// Normalizes title string for similarity matching (strips symbols, standardizes Roman numerals).
    /// </summary>
    public static string NormalizeForMatching(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        string s = input.ToLowerInvariant();

        // Strip trademark, copyright, symbols
        s = s.Replace("®", "").Replace("™", "").Replace("©", "")
             .Replace("(r)", "").Replace("(tm)", "").Replace("(c)", "");

        // Standardize common Roman numerals to digits
        s = Regex.Replace(s, @"\bviii\b", "8");
        s = Regex.Replace(s, @"\bvii\b", "7");
        s = Regex.Replace(s, @"\bvi\b", "6");
        s = Regex.Replace(s, @"\biv\b", "4");
        s = Regex.Replace(s, @"\bv\b", "5");
        s = Regex.Replace(s, @"\biii\b", "3");
        s = Regex.Replace(s, @"\bii\b", "2");
        s = Regex.Replace(s, @"\bix\b", "9");
        // Single "i" and "x" are ambiguous (word "I", franchise letter "X") and are
        // deliberately left unconverted; see M-19.

        // Remove punctuation
        s = s.Replace(':', ' ').Replace('-', ' ').Replace('_', ' ')
             .Replace('.', ' ').Replace(',', ' ').Replace('\'', ' ')
             .Replace('"', ' ').Replace('&', ' ');

        return WhitespaceRegex().Replace(s, " ").Trim();
    }

    /// <summary>
    /// Computes Levenshtein distance between two strings.
    /// </summary>
    /// <summary>
    /// Below this many alphanumeric characters a title is treated as an identifier rather than
    /// something to match fuzzily: one edit is the whole meaning of "P3R" or "F1 22".
    /// </summary>
    private const int MinFuzzyMatchLength = 4;

    /// <summary>
    /// Applied when the two titles disagree about their numbers. Deliberately near-fatal - it
    /// takes the highest realistic pre-penalty score well under any usable confidence threshold -
    /// while still leaving a graded score for callers that rank rather than accept.
    /// </summary>
    private const double NumericMismatchPenalty = 0.45;

    private static string CompactAlphanumeric(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool IsNumericToken(string token) =>
        token.Length > 0 && token.All(char.IsDigit);

    /// <summary>
    /// True when the titles' numbers cannot describe the same game. One side carrying numbers the
    /// other omits entirely is allowed - "Resident Evil 4 (2023)" and "Resident Evil 4" are the
    /// same game - but two non-empty sets that each hold a number the other lacks are not.
    /// </summary>
    private static bool NumericTokensDisagree(string[] qTokens, string[] cTokens)
    {
        var qNums = new HashSet<string>(qTokens.Where(IsNumericToken), StringComparer.Ordinal);
        var cNums = new HashSet<string>(cTokens.Where(IsNumericToken), StringComparer.Ordinal);

        if (qNums.Count == 0 && cNums.Count == 0) return false;
        // "Doom" vs "Doom II": a number on one side only is a sequel, not a spelling variant.
        if (qNums.Count == 0 || cNums.Count == 0) return true;

        return !qNums.IsSubsetOf(cNums) && !cNums.IsSubsetOf(qNums);
    }

    public static int LevenshteinDistance(string s, string t)
    {
        if (string.IsNullOrEmpty(s)) return t?.Length ?? 0;
        if (string.IsNullOrEmpty(t)) return s.Length;

        int n = s.Length;
        int m = t.Length;
        var d = new int[m + 1];

        for (int j = 0; j <= m; j++) d[j] = j;

        for (int i = 1; i <= n; i++)
        {
            int prev = d[0];
            d[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int temp = d[j];
                int cost = char.ToLowerInvariant(s[i - 1]) == char.ToLowerInvariant(t[j - 1]) ? 0 : 1;
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + cost);
                prev = temp;
            }
        }

        return d[m];
    }
}
