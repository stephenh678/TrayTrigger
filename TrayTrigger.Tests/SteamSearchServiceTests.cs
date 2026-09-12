using TrayTrigger.Services;

namespace TrayTrigger.Tests;

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
