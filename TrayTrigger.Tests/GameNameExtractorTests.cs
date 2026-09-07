using TrayTrigger.Services;

namespace TrayTrigger.Tests;

public class GameNameExtractorTests
{
    [Theory]
    [InlineData("Microsoft Flight Simulator", true)]
    [InlineData("Unreal Tournament", true)] // multi-word title containing a generic-sounding word. See M-20.
    [InlineData("Microsoft Visual C++ Redistributable", false)] // utility keyword, disqualifies regardless of length
    [InlineData("Setup", false)] // single generic word
    [InlineData("CrashReportClient", false)] // utility keyword
    [InlineData("", false)]
    public void IsAcceptableTitle_DistinguishesRealTitlesFromUtilityNames(string title, bool expected)
    {
        Assert.Equal(expected, GameNameExtractor.IsAcceptableTitle(title));
    }

    [Theory]
    [InlineData("Cyberpunk.2077.FitGirl.Repack", "Cyberpunk 2077")]
    [InlineData("Elden.Ring-CODEX", "Elden Ring")]
    [InlineData("Half-Life-Deluxe-Edition", "Half Life Deluxe Edition")]
    [InlineData("Portal.2.Steam", "Portal 2")]
    public void CleanFolderName_StripsReleaseGroupsAndDelimiters(string input, string expected)
    {
        Assert.Equal(expected, GameNameExtractor.CleanFolderName(input));
    }

    [Theory]
    [InlineData("MyGame-Win64-Shipping", "My Game")]
    [InlineData("TheWitcher3", "The Witcher 3")]
    [InlineData("some_game_v2", "Some Game V 2")]
    public void CleanExecutableStem_SplitsCamelCaseAndStripsEngineSuffixes(string input, string expected)
    {
        Assert.Equal(expected, GameNameExtractor.CleanExecutableStem(input));
    }

    [Fact]
    public void CleanExecutableStem_AllCapsStemIsLeftAsIs()
    {
        // Only all-lowercase stems get title-cased; ELDEN_RING is already presentable.
        Assert.Equal("ELDEN RING", GameNameExtractor.CleanExecutableStem("ELDEN_RING"));
    }

    [Fact]
    public void CleanExecutableStem_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, GameNameExtractor.CleanExecutableStem(""));
    }
}
