using System.IO;
using System.Text.Json;
using TrayTrigger.Models;
using TrayTrigger.Services;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>
/// The wikitext reader, against excerpts of 15 real PCGamingWiki pages (Fixtures/PcGamingWiki, taken from
/// the wiki under CC BY-NC-SA 3.0; each file's first line says which page). golden.json is what a second,
/// separately written reader found in the same excerpts, so a change to either shows up here.
/// </summary>
public class PcGamingWikiParserTests
{
    private static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "PcGamingWiki");

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(FixtureDirectory, name + ".wikitext"));

    private sealed record Golden(string Group, string Label, int Support, string Detail);

    private static Dictionary<string, List<Golden>> LoadGolden()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, "golden.json")));
        var result = new Dictionary<string, List<Golden>>();
        foreach (var game in doc.RootElement.EnumerateObject())
        {
            result[game.Name] = game.Value.EnumerateArray()
                .Select(f => new Golden(f.GetProperty("group").GetString()!, f.GetProperty("label").GetString()!, f.GetProperty("support").GetInt32(), f.GetProperty("detail").GetString()!))
                .ToList();
        }
        return result;
    }

    public static IEnumerable<object[]> FixtureNames() => LoadGolden().Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void EveryFixture_ReadsTheSameFactsAsTheReferenceReader(string name)
    {
        var expected = LoadGolden()[name];
        var actual = PcGamingWikiParser.Parse(Fixture(name));

        Assert.Equal(
            expected.Select(f => $"{f.Group}|{f.Label}|{f.Support}|{f.Detail}"),
            actual.Select(f => $"{f.Group}|{f.Label}|{(int)f.Support}|{f.Detail}"));
    }

    [Fact]
    public void EldenRing_ReadsTheValuesTheWikiGives()
    {
        var facts = PcGamingWikiParser.Parse(Fixture("elden-ring"));
        PcGamingWikiFact Get(string label) => facts.Single(f => f.Label == label);

        Assert.Equal(PcGamingWikiSupport.Fix, Get("Ultrawide").Support);
        Assert.Equal(PcGamingWikiSupport.Fix, Get("Multi-monitor").Support);
        Assert.Equal(PcGamingWikiSupport.Yes, Get("4K").Support);
        Assert.Equal(PcGamingWikiSupport.Yes, Get("Controller support").Support);
        Assert.Equal("full controller support", Get("Controller support").Detail);
        Assert.Equal("up to 4 players", Get("Online play").Detail);
        Assert.Equal(PcGamingWikiSupport.No, Get("Local play").Support);
        Assert.Equal(PcGamingWikiSupport.Yes, Get("Steam Cloud").Support);
        Assert.Equal(@"%APPDATA%\EldenRing\<user ID>\", Get("Save location").Detail);
        Assert.Equal("DirectX 12", Get("Graphics API").Detail);
    }

    [Fact]
    public void Groups_AreInTheOrderTheCardShowsThem()
    {
        var groups = PcGamingWikiParser.Parse(Fixture("stardew-valley")).Select(f => f.Group).Distinct().ToList();
        Assert.Equal(new[] { "Display", "Controls", "Audio and accessibility", "Online", "Saves", "Technical" }, groups);
    }

    [Fact]
    public void ANoteIsCleanedToPlainText_OrLeftOut()
    {
        var facts = PcGamingWikiParser.Parse(Fixture("elden-ring"));
        var ultrawide = facts.Single(f => f.Label == "Ultrawide");
        Assert.Contains("Modifications", ultrawide.Note);
        Assert.DoesNotContain("[[", ultrawide.Note);
        Assert.DoesNotContain("{{", ultrawide.Note);
        Assert.All(facts, f =>
        {
            Assert.DoesNotContain("<", f.Note);
            Assert.DoesNotContain("|", f.Note);
            Assert.True(f.Note.Length <= 241, $"{f.Label}: the note is {f.Note.Length} long");
        });
    }

    [Fact]
    public void OldGamesKeepTheirOwnApiList()
    {
        var facts = PcGamingWikiParser.Parse(Fixture("half-life"));
        Assert.Contains("DirectX 6, 9", facts.Single(f => f.Label == "Graphics API").Detail);
        Assert.Contains("OpenGL", facts.Single(f => f.Label == "Graphics API").Detail);
    }

    // ------------------------------------------------------------------ a page with nothing to read

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#REDIRECT [[Alan Wake II]]")]
    [InlineData("{{Infobox game}} A stub with no tables.")]
    [InlineData("{{Video|hdr = true")]
    [InlineData("}} {{ {{{{ |||| ==== [[[[ <ref>")]
    public void APageWithNoFacts_GivesNone_AndDoesNotThrow(string? text)
    {
        Assert.Empty(PcGamingWikiParser.Parse(text));
    }

    [Fact]
    public void ARatingOfUnknownOrNotApplicable_IsLeftOut()
    {
        var facts = PcGamingWikiParser.Parse("{{Video\n|hdr = unknown\n|fov = n/a\n|multimonitor =\n|ultrawidescreen = true\n}}");
        var only = Assert.Single(facts);
        Assert.Equal("Ultrawide", only.Label);
    }

    [Fact]
    public void ACommentOrReferenceInsideAValue_DoesNotChangeIt()
    {
        var facts = PcGamingWikiParser.Parse("{{Video\n|hdr = true<ref>{{Refcheck|user=x|date=2020-01-01}}</ref> <!-- checked | with a pipe -->\n|4k ultra hd = false\n}}");
        Assert.Equal(PcGamingWikiSupport.Yes, facts.Single(f => f.Label == "HDR").Support);
        Assert.Equal(PcGamingWikiSupport.No, facts.Single(f => f.Label == "4K").Support);
    }

    [Fact]
    public void ARandomPileOfWikiCharacters_NeverThrows()
    {
        var random = new Random(20261017);
        const string alphabet = "{}[]|=<>!-#'ab \n\u00B7&;/\\%video input api";
        for (int i = 0; i < 500; i++)
        {
            string text = new(Enumerable.Range(0, random.Next(0, 400)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var exception = Record.Exception(() => PcGamingWikiParser.Parse(text));
            Assert.Null(exception);
        }
    }

    [Fact]
    public void AnEnormousPage_IsNotRead()
    {
        Assert.Empty(PcGamingWikiParser.Parse("{{Video|hdr = true}}" + new string('x', PcGamingWikiParser.MaxWikitextLength)));
    }

    // ------------------------------------------------------------------ controller rule

    [Theory]
    [InlineData("true", "true", PcGamingWikiSupport.Yes, "full controller support")]
    [InlineData("true", "false", PcGamingWikiSupport.Limited, "partial controller support")]
    [InlineData("true", "limited", PcGamingWikiSupport.Limited, "partial controller support")]
    [InlineData("true", "unknown", PcGamingWikiSupport.Yes, "")]
    [InlineData("true", "", PcGamingWikiSupport.Yes, "")]
    [InlineData("limited", "false", PcGamingWikiSupport.Limited, "")]
    [InlineData("false", "false", PcGamingWikiSupport.No, "")]
    [InlineData("hackable", "", PcGamingWikiSupport.Fix, "")]
    [InlineData("", "true", PcGamingWikiSupport.Yes, "full controller support")]
    public void ControllerSupport_CombinesTheTwoFields(string support, string full, PcGamingWikiSupport expected, string detail)
    {
        var facts = PcGamingWikiParser.Parse($"{{{{Input\n|controller support = {support}\n|full controller = {full}\n}}}}");
        var controller = Assert.Single(facts);
        Assert.Equal(expected, controller.Support);
        Assert.Equal(detail, controller.Detail);
    }

    [Fact]
    public void ControllerSupportThatIsUnknownEverywhere_IsLeftOut()
    {
        Assert.Empty(PcGamingWikiParser.Parse("{{Input\n|controller support = unknown\n|full controller = unknown\n}}"));
    }

    [Fact]
    public void SteamAppIds_ComeFromTheInfobox_BothTheGameAndItsOtherEntries()
    {
        var ids = PcGamingWikiParser.Read("{{Infobox game\n|steam appid  = 2362420\n|steam appid side = 24980, 47760; 901242 <!-- note -->\n}}").SteamAppIds;
        Assert.Equal(new[] { "2362420", "24980", "47760", "901242" }, ids);
    }

    [Theory]
    [InlineData("{{Video|hdr = true}}")]
    [InlineData("{{Infobox game|steam appid = }}")]
    [InlineData("{{Infobox game|steam appid = abc, 12x}}")]
    [InlineData("{{Infobox game|steam appid = 123456789012}}")]
    [InlineData("")]
    public void SteamAppIds_AreEmptyWhenThePageGivesNone(string text)
    {
        Assert.Empty(PcGamingWikiParser.Read(text).SteamAppIds);
    }

    [Fact]
    public void EveryFixture_ListsItsOwnSteamAppId()
    {
        var expected = new Dictionary<string, string>
        {
            ["elden-ring"] = "1245620", ["cyberpunk-2077"] = "1091500", ["stardew-valley"] = "413150", ["hades"] = "1145360",
            ["half-life"] = "70", ["terraria"] = "105600", ["dota-2"] = "570", ["baldurs-gate-3"] = "1086940", ["counter-strike"] = "10",
        };
        foreach (var (name, id) in expected)
            Assert.Contains(id, PcGamingWikiParser.Read(Fixture(name)).SteamAppIds);
    }

    // ------------------------------------------------------------------ small readers

    [Theory]
    [InlineData("true", PcGamingWikiSupport.Yes)]
    [InlineData(" TRUE ", PcGamingWikiSupport.Yes)]
    [InlineData("false", PcGamingWikiSupport.No)]
    [InlineData("hackable", PcGamingWikiSupport.Fix)]
    [InlineData("limited", PcGamingWikiSupport.Limited)]
    [InlineData("always on", PcGamingWikiSupport.AlwaysOn)]
    public void ParseSupport_ReadsTheWikisWords(string raw, PcGamingWikiSupport expected)
    {
        Assert.Equal(expected, PcGamingWikiParser.ParseSupport(raw));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("n/a")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("maybe")]
    [InlineData("true but only on Tuesdays")]
    public void ParseSupport_LeavesOutAnythingElse(string? raw)
    {
        Assert.Null(PcGamingWikiParser.ParseSupport(raw));
    }

    [Theory]
    [InlineData("11", "11")]
    [InlineData("11, 12", "11, 12")]
    [InlineData("11,12", "11, 12")]
    [InlineData("9.0c", "9.0c")]
    [InlineData("9, 9Ex", "9, 9Ex")]
    [InlineData("1.2+", "1.2+")]
    [InlineData("11.1", "11.1")]
    public void VersionList_KeepsRealVersions(string raw, string expected)
    {
        Assert.Equal(expected, PcGamingWikiParser.VersionList(raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("true")]
    [InlineData("{{code|x}}")]
    [InlineData("11 (see notes)")]
    [InlineData("1, 2, 3, 4, 5, 6, 7")]
    public void VersionList_RejectsAnythingElse(string? raw)
    {
        Assert.Null(PcGamingWikiParser.VersionList(raw));
    }

    [Theory]
    [InlineData("4", "up to 4 players")]
    [InlineData(" 128 ", "up to 128 players")]
    [InlineData("2-4", "2\u20134 players")]
    [InlineData("2 \u2013 4", "2\u20134 players")]
    [InlineData("", "")]
    [InlineData("many", "")]
    [InlineData("30- 60", "30\u201360 players")]
    [InlineData("1000", "")]
    public void PlayersText_OnlySaysAPlainNumber(string raw, string expected)
    {
        Assert.Equal(expected, PcGamingWikiParser.PlayersText(raw));
    }

    // ------------------------------------------------------------------ notes

    [Fact]
    public void CleanText_TurnsLinksIntoTheirLabel()
    {
        Assert.Equal("See Modifications to remove letterboxing.", PcGamingWikiParser.CleanText("See [[#Modifications|Modifications]] to remove letterboxing."));
        Assert.Equal("Uses Steam Input.", PcGamingWikiParser.CleanText("Uses [[Steam Input]]."));
        Assert.Equal("Download the patch.", PcGamingWikiParser.CleanText("[https://example.com/patch Download the patch]."));
    }

    [Fact]
    public void CleanText_RemovesReferencesTemplatesAndTags()
    {
        Assert.Equal("Up to 7.1.", PcGamingWikiParser.CleanText("Up to 7.1.<ref>{{Refcheck|user=A|date=2022-02-24|comment=Tested.}}</ref>"));
        Assert.Equal("Music, effects and voice.", PcGamingWikiParser.CleanText("Music, effects<br>and voice."));
        Assert.Equal("Press F11.", PcGamingWikiParser.CleanText("Press <code>F11</code>."));
        Assert.Equal("Bold and italic", PcGamingWikiParser.CleanText("'''Bold''' and ''italic''"));
        Assert.Equal("Fish & chips", PcGamingWikiParser.CleanText("Fish &amp; chips"));
    }

    [Theory]
    [InlineData("{{unclosed template")]
    [InlineData("A table: {| class=\"wikitable\"")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("a | b")]
    [InlineData("&lt;b&gt;")]
    public void CleanText_DropsANoteThatStillHasMarkup(string raw)
    {
        Assert.Equal(string.Empty, PcGamingWikiParser.CleanText(raw));
    }

    [Fact]
    public void CleanText_CutsALongNoteAtAWord()
    {
        string cleaned = PcGamingWikiParser.CleanText(string.Join(' ', Enumerable.Repeat("alpha beta gamma", 40)), 100);
        Assert.True(cleaned.Length <= 101);
        Assert.EndsWith("\u2026", cleaned);
        Assert.DoesNotContain("alph\u2026", cleaned);
    }

    // ------------------------------------------------------------------ save folders

    [Theory]
    [InlineData(@"{{p|appdata}}\Game\Saves\", @"%APPDATA%\Game\Saves\")]
    [InlineData(@"{{p|localappdata}}\Larian Studios\Baldur's Gate 3\", @"%LOCALAPPDATA%\Larian Studios\Baldur's Gate 3\")]
    [InlineData(@"{{p|userprofile\Documents}}\My Games\Terraria\", @"%USERPROFILE%\Documents\My Games\Terraria\")]
    [InlineData(@"{{p|userprofile}}\Saved Games\Hades II\Profile*.sav", @"%USERPROFILE%\Saved Games\Hades II\Profile*.sav")]
    [InlineData(@"{{p|game}}\valve\SAVE\", @"<game folder>\valve\SAVE\")]
    [InlineData(@"{{P|Steam}}\userdata\{{p|uid}}\", @"<Steam folder>\userdata\<user ID>\")]
    [InlineData(@"{{p|hkcu}}\Software\Game", @"HKEY_CURRENT_USER\Software\Game")]
    [InlineData(@"{{p|someothertoken}}\x", @"<someothertoken>\x")]
    public void ExpandPath_SpellsOutTheWikisTokens(string raw, string expected)
    {
        Assert.Equal(expected, PcGamingWikiParser.ExpandPath(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"\\server\share\saves")]
    [InlineData("//server/share")]
    [InlineData("{{unknown template|x}}")]
    [InlineData("C:\\Games\\[[Link]]")]
    [InlineData("C:\\a<b>c")]
    [InlineData("a|b")]
    public void ExpandPath_RejectsAnythingThatIsNotAPlainFolder(string? raw)
    {
        Assert.Null(PcGamingWikiParser.ExpandPath(raw));
    }

    [Fact]
    public void ExpandPath_RejectsAPathLongerThanTwoHundred()
    {
        Assert.Null(PcGamingWikiParser.ExpandPath(@"{{p|appdata}}\" + new string('a', 250)));
    }

    [Fact]
    public void SaveLocation_UsesOnlyWindowsRows_AndAtMostTwo()
    {
        const string text = """
            {{Game data|
            {{Game data/saves|Windows|{{p|appdata}}\A\|{{p|localappdata}}\B\|{{p|userprofile}}\C\}}
            {{Game data/saves|Linux|{{p|xdgdatahome}}\A\}}
            {{Game data/saves|Microsoft Store|{{p|localappdata}}\Packages\X\}}
            }}
            """;
        var facts = PcGamingWikiParser.Parse(text);
        Assert.Equal(@"%APPDATA%\A\ ; %LOCALAPPDATA%\B\", Assert.Single(facts).Detail);
    }

    [Fact]
    public void SaveLocation_NeverShowsANetworkPath()
    {
        const string text = "{{Game data|\n{{Game data/saves|Windows|\\\\server\\share\\saves}}\n}}";
        Assert.Empty(PcGamingWikiParser.Parse(text));
    }
}
