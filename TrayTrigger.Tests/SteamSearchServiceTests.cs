using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// Counts how many searches get past the cache and the in-flight claim, and holds each one open
/// until released so several callers are genuinely overlapping rather than merely queued.
/// </summary>
internal sealed class CountingSteamSearchService : SteamSearchService
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _searches;

    public int SearchCount => Volatile.Read(ref _searches);
    public void Release() => _release.TrySetResult();

    internal override async Task<List<SteamGameMatch>> SearchGamesUncachedAsync(string trimmed, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _searches);
        await _release.Task.ConfigureAwait(false);
        return [new SteamGameMatch(trimmed, "1", null)];
    }
}

public class SteamSearchServiceTests
{
    [Theory]
    [InlineData("Cyberpunk 2077 FitGirl Repack", "Cyberpunk 2077")]
    [InlineData("Elden Ring CODEX", "Elden Ring")]
    [InlineData("Half-Life Deluxe Edition", "Half Life")]
    [InlineData("reamek of the fallen", "remake of the fallen")] // common misspelling correction
    [InlineData("GTA V GOG", "GTA V")]
    [InlineData("Portal 2 Steam", "Portal 2")] // "Steam" release-group tag stripped. See L-11.
    public void SanitizeSearchQuery_StripsReleaseGroupsAndEditionTags(string input, string expected)
    {
        Assert.Equal(expected, SteamSearchService.SanitizeSearchQuery(input));
    }

    [Fact]
    public void NormalizeForMatching_DoesNotCollapseStandaloneXOrI()
    {
        // "Mega Man X" and "Mega Man 10" must not normalize identically. See M-19.
        Assert.Equal("mega man x", SteamSearchService.NormalizeForMatching("Mega Man X"));
        Assert.Equal("mega man 10", SteamSearchService.NormalizeForMatching("Mega Man 10"));
    }

    [Fact]
    public void NormalizeForMatching_StillConvertsMultiLetterRomanNumerals()
    {
        Assert.Contains("7", SteamSearchService.NormalizeForMatching("Final Fantasy VII"));
    }

    [Fact]
    public void CalculateSimilarity_MegaManXVsMegaMan10_ScoresBelowDecisiveThreshold()
    {
        // Regression test for M-19: before the fix this scored 1.0 (treated as identical).
        double score = SteamSearchService.CalculateSimilarity("Mega Man X", "Mega Man 10");
        Assert.True(score < 0.9, $"Expected < 0.9, got {score}");
    }

    [Fact]
    public void CalculateSimilarity_IdenticalTitles_ScoresPerfect()
    {
        Assert.Equal(1.0, SteamSearchService.CalculateSimilarity("Half-Life 2", "Half-Life 2"));
    }

    /// <summary>
    /// A folder import enriches several games at once, and games under one tree routinely derive
    /// the same folder-name query. The completed-result cache cannot help while they overlap -
    /// none of them has finished to populate it - so without an in-flight claim each one issues
    /// its own request for the same term.
    /// </summary>
    [Fact]
    public async Task SearchGamesAsync_ConcurrentIdenticalQueries_IssueOneSearch()
    {
        var service = new CountingSteamSearchService();
        string query = $"Concurrent Dedup Probe {Guid.NewGuid():N}"; // unseen by the shared cache

        var callers = Enumerable.Range(0, 8).Select(_ => service.SearchGamesAsync(query)).ToArray();
        service.Release();
        var results = await Task.WhenAll(callers);

        Assert.Equal(1, service.SearchCount);
        Assert.All(results, r => Assert.Equal(query, Assert.Single(r).Name));
    }

    [Fact]
    public async Task SearchGamesAsync_DifferentQueries_EachIssueTheirOwnSearch()
    {
        var service = new CountingSteamSearchService();
        string stem = Guid.NewGuid().ToString("N");

        var callers = Enumerable.Range(0, 3).Select(i => service.SearchGamesAsync($"Probe {stem} {i}")).ToArray();
        service.Release();
        await Task.WhenAll(callers);

        Assert.Equal(3, service.SearchCount);
    }

    /// <summary>
    /// From Dylan's 1.4.0 log: 'P 3 R' was accepted as RAWG's 'P-T-R' (0.71) and given a
    /// SteamGridDB poster for 'P.3' (0.71). Probing the rest of the space turned up the same
    /// hole under every sequel: 'Portal 2'/'Portal 3' scored 0.63 and 'Doom'/'Doom II' 0.79,
    /// all of them over the 0.60 bar.
    /// </summary>
    [Theory]
    [InlineData("P 3 R", "P-T-R")]
    [InlineData("P 3 R", "P.3")]
    [InlineData("Doom", "Doom II")]
    [InlineData("F1 22", "F1 23")]
    [InlineData("Portal 2", "Portal 3")]
    [InlineData("Persona 3", "Persona 5")]
    [InlineData("Mega Man X", "Mega Man 10")]
    [InlineData("Half-Life", "Half-Life 2")]
    public void CalculateSimilarity_DifferentGame_ScoresBelowDefaultConfidence(string query, string candidate)
    {
        double score = SteamSearchService.CalculateSimilarity(query, candidate);
        Assert.True(score < SteamSearchService.DefaultMinConfidence,
            $"Expected < {SteamSearchService.DefaultMinConfidence}, got {score:F3} for '{query}' vs '{candidate}'");
    }

    /// <summary>
    /// A VR edition is a separate product. 'Steam Deck' scored 0.854 against RAWG's 'Steam Deck
    /// VR' in Dylan's 1.4.0 log and pointed the library at it.
    /// </summary>
    [Theory]
    [InlineData("Steam Deck", "Steam Deck VR")]
    [InlineData("Beat Saber", "Beat Saber VR")]
    public void CalculateSimilarity_VrEditionOfAFlatTitle_IsRejected(string query, string candidate)
    {
        double score = SteamSearchService.CalculateSimilarity(query, candidate);
        Assert.True(score < SteamSearchService.DefaultMinConfidence,
            $"Expected < {SteamSearchService.DefaultMinConfidence}, got {score:F3} for '{query}' vs '{candidate}'");
    }

    /// <summary>
    /// The penalty keywords were matched as bare substrings, so a candidate was docked 35% for
    /// merely containing one: "ost" fires inside "Ghost of Tsushima", which dropped a correct
    /// match from 0.685 to 0.445 and put it under the bar.
    /// </summary>
    [Theory]
    [InlineData("Tsushima", "Ghost of Tsushima")]
    [InlineData("Frostpunk", "Frostpunk")]
    [InlineData("Provost", "Provost")]
    [InlineData("Louvre", "Louvre")]
    public void CalculateSimilarity_PenaltyKeywordInsideAWord_IsNotPenalised(string query, string candidate)
    {
        double score = SteamSearchService.CalculateSimilarity(query, candidate);
        Assert.True(score >= SteamSearchService.DefaultMinConfidence,
            $"Expected >= {SteamSearchService.DefaultMinConfidence}, got {score:F3} for '{query}' vs '{candidate}'");
    }

    [Theory]
    [InlineData("Hades", "Hades Soundtrack")]
    [InlineData("Hades", "Hades Demo")]
    [InlineData("Hades", "Hades OST")]
    public void CalculateSimilarity_PenaltyKeywordAsItsOwnWord_StillPenalises(string query, string candidate)
    {
        double score = SteamSearchService.CalculateSimilarity(query, candidate);
        Assert.True(score < SteamSearchService.DefaultMinConfidence,
            $"Expected < {SteamSearchService.DefaultMinConfidence}, got {score:F3} for '{query}' vs '{candidate}'");
    }

    [Theory]
    [InlineData("Doom", "DOOM")]                                // short title, only case differs
    [InlineData("Tunic", "TUNIC")]
    [InlineData("Elden Ring", "ELDEN RING")]
    [InlineData("Gothic 1 reamek", "Gothic 1 Remake")]          // typo, numbers agree
    [InlineData("Civilization VI", "Civilization 6")]           // roman numeral normalisation
    [InlineData("Resident Evil 4 (2023)", "Resident Evil 4")]   // extra number on one side only
    public void CalculateSimilarity_SameGame_ScoresAtOrAboveDefaultConfidence(string query, string candidate)
    {
        double score = SteamSearchService.CalculateSimilarity(query, candidate);
        Assert.True(score >= SteamSearchService.DefaultMinConfidence,
            $"Expected >= {SteamSearchService.DefaultMinConfidence}, got {score:F3} for '{query}' vs '{candidate}'");
    }
}
