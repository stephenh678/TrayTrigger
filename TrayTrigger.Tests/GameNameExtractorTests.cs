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
    // "V2" now stays whole, like every other short letter-and-digit token: the digit belongs to
    // the token rather than starting a word. It used to come out "V 2".
    [InlineData("some_game_v2", "Some Game V2")]
    public void CleanExecutableStem_SplitsCamelCaseAndStripsEngineSuffixes(string input, string expected)
    {
        Assert.Equal(expected, GameNameExtractor.CleanExecutableStem(input));
    }

    /// <summary>
    /// Splitting at every letter/digit boundary turned Steph's 'R6-Extraction.exe' into
    /// "R 6 Extraction" and Dylan's into "P 3 R" and "G 1 R" - names that match nothing, and that
    /// then became the trusted name later passes are measured against.
    /// </summary>
    [Theory]
    [InlineData("R6-Extraction", "R6 Extraction")]
    [InlineData("R6-Extraction_Plus", "R6 Extraction Plus")]
    [InlineData("R6Extraction", "R6 Extraction")]
    [InlineData("P3R", "P3R")]
    [InlineData("G1R", "G1R")]
    [InlineData("R6S", "R6S")]
    [InlineData("F1", "F1")]
    public void CleanExecutableStem_ShortDesignation_KeepsTheDigitAttached(string input, string expected)
    {
        Assert.Equal(expected, GameNameExtractor.CleanExecutableStem(input));
    }

    /// <summary>A digit after a real word is a sequel or a year, and splitting it is what makes
    /// these searchable.</summary>
    [Theory]
    [InlineData("MortalShell2", "Mortal Shell 2")]
    [InlineData("Cyberpunk2077", "Cyberpunk 2077")]
    [InlineData("Left4Dead", "Left 4 Dead")]
    [InlineData("Left4Dead2", "Left 4 Dead 2")]
    [InlineData("Fallout4", "Fallout 4")]
    [InlineData("HalfLife2", "Half Life 2")]
    [InlineData("DOOM3", "DOOM 3")]
    public void CleanExecutableStem_DigitAfterAWord_StillSplits(string input, string expected)
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

public class KnownNameGuardTests
{
    private static readonly SteamGameMatch ContentWarning = new("Content Warning", "2881650", null, 0.72);

    [Fact]
    public void FallbackMatch_ThatDoesNotResembleTrustedName_IsRejected()
    {
        // "D:\XboxGames\Fortnite\Content" once fuzzy-matched a real Steam game via its "Content" folder.
        Assert.Null(GameNameExtractor.GuardAgainstKnownName(ContentWarning, "Fortnite", 0.6, "test"));
        Assert.Null(GameNameExtractor.GuardAgainstKnownName(ContentWarning, "Roblox", 0.6, "test"));
    }

    [Fact]
    public void FallbackMatch_ThatResemblesTrustedName_IsKept()
    {
        var omd = new SteamGameMatch("Orcs Must Die! 3", "1522820", null, 0.7);
        Assert.Same(omd, GameNameExtractor.GuardAgainstKnownName(omd, "Orcs Must Die 3", 0.6, "test"));
    }

    [Fact]
    public void NoTrustedName_LeavesMatchAlone()
    {
        Assert.Same(ContentWarning, GameNameExtractor.GuardAgainstKnownName(ContentWarning, null, 0.6, "test"));
        Assert.Same(ContentWarning, GameNameExtractor.GuardAgainstKnownName(ContentWarning, "  ", 0.6, "test"));
        Assert.Null(GameNameExtractor.GuardAgainstKnownName(null, "Fortnite", 0.6, "test"));
    }
}
