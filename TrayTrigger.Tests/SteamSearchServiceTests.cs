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
}
