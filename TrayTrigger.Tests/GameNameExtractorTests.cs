using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    [InlineData("Cyberpunk.2077.v1.63", "Cyberpunk 2077")]
    [InlineData("Elden Ring (x64)", "Elden Ring")]
    [InlineData("Half-Life-Deluxe-Edition", "Half Life Deluxe Edition")]
    [InlineData("Portal.2.Steam", "Portal 2")]
    public void CleanFolderName_StripsStoreNamesVersionsAndDelimiters(string input, string expected)
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

    /// <summary>Dylan's 'ACBlackFlag.exe' came out "ACBlack Flag".</summary>
    [Theory]
    [InlineData("ACBlackFlag", "AC Black Flag")]
    [InlineData("XCOMChimeraSquad", "XCOM Chimera Squad")]
    public void CleanExecutableStem_AcronymBeforeAWord_Splits(string input, string expected)
    {
        Assert.Equal(expected, GameNameExtractor.CleanExecutableStem(input));
    }

    /// <summary>
    /// Dylan's Gothic 1 Remake and Persona 3 Reload searched only their Unreal project folders
    /// ('G1R', 'P3R'), which match nothing, so both were imported under those code names.
    /// </summary>
    [Fact]
    public void FindMeaningfulFolderName_PackagedUnrealGame_ReturnsTheInstallFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), "TrayTriggerTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            string install = Path.Combine(root, "Gothic 1 Remake");
            string binaries = Path.Combine(install, "G1R", "Binaries", "Win64");
            Directory.CreateDirectory(binaries);
            Directory.CreateDirectory(Path.Combine(install, "Engine"));

            Assert.Equal("Gothic 1 Remake", GameNameExtractor.FindMeaningfulFolderName(Path.Combine(binaries, "G1R-Win64-Shipping.exe"), binaries));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>Without an Engine folder beside it, the folder above Binaries is the game itself.</summary>
    [Fact]
    public void FindMeaningfulFolderName_BinariesWithoutEngineSibling_KeepsTheFolderAboveBinaries()
    {
        string root = Path.Combine(Path.GetTempPath(), "TrayTriggerTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            string binaries = Path.Combine(root, "Stuff", "Hades", "Binaries", "Win64");
            Directory.CreateDirectory(binaries);

            Assert.Equal("Hades", GameNameExtractor.FindMeaningfulFolderName(Path.Combine(binaries, "Hades.exe"), binaries));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}

/// <summary>
/// Answers a search with every catalogue title containing the term, and nothing otherwise - the
/// way Steam's store search returns no results for a term with an unrecognised word in it.
/// </summary>
internal sealed class CatalogueSteamSearchService(params SteamGameMatch[] catalogue) : SteamSearchService
{
    internal override Task<List<SteamGameMatch>> SearchGamesUncachedAsync(string trimmed, CancellationToken cancellationToken) =>
        Task.FromResult(catalogue.Where(g => g.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)).ToList());
}

public class ShortenedFolderNameTests
{
    // Unique per test: SteamSearchService caches results in a static dictionary keyed by term.
    private static string UniqueWord() => "Zq" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// A folder name ending in a word that isn't part of the title found nothing on Steam at all
    /// ("AC Black Flag Resynced" in Dylan's log). Dropping trailing words finds the game without
    /// keeping a list of such words.
    /// </summary>
    [Fact]
    public async Task FolderNameWithTrailingWord_MatchesTheTitleWithoutIt()
    {
        string title = $"Harbor Lights {UniqueWord()}";
        var steam = new CatalogueSteamSearchService(new SteamGameMatch(title, "42", null));
        string folder = $@"C:\Games\{title} Backup";

        var result = await GameNameExtractor.ResolveGameMatchAsync(Path.Combine(folder, "game.exe"), folder, steamSearch: steam);

        Assert.Equal(title, result.ResolvedTitle);
        Assert.Equal("42", result.SteamAppId);
    }

    /// <summary>A shortened term must match decisively, so one word can't claim a longer title.</summary>
    [Fact]
    public async Task ShortenedToOneWord_DoesNotMatchALongerTitle()
    {
        string word = UniqueWord();
        var steam = new CatalogueSteamSearchService(new SteamGameMatch($"Tiny Adventurous {word}", "42", null));
        string folder = $@"C:\Games\{word} Backup";

        var result = await GameNameExtractor.ResolveGameMatchAsync(Path.Combine(folder, "game.exe"), folder, steamSearch: steam);

        Assert.Null(result.SteamAppId);
        Assert.Equal($"{word} Backup", result.ResolvedTitle);
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
