using System.Text.Json;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>UX-16: which games and tools the tray menu's search box lists, and in what order.</summary>
public class TraySearchTests
{
    private sealed record Item(string Name, string Category);

    private static readonly Item[] Library =
    {
        new("Hades", "Roguelike"),
        new("Hades II", "Roguelike"),
        new("Shadow of the Tomb Raider", "Action"),
        new("Halo Infinite", "Shooter"),
        new("Dead Cells", "Roguelike"),
        new("Cyberpunk 2077", "RPG"),
    };

    private static List<string> Names(string? query, int max = TraySearch.MaxGames) =>
        TraySearch.Match(Library, i => i.Name, i => i.Category, query, max).Select(i => i.Name).ToList();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyQuery_ListsNothing(string? query) => Assert.Empty(Names(query));

    [Fact]
    public void NameStartsWith_ComesBeforeContains_ThenCategory()
    {
        // "ha": Hades, Hades II, Halo start with it; Shadow contains it; no category has it.
        Assert.Equal(new[] { "Hades", "Hades II", "Halo Infinite", "Shadow of the Tomb Raider" }, Names("ha"));
    }

    [Fact]
    public void CategoryMatches_AreListedAfterNameMatches()
    {
        // "rogue" is only in categories.
        Assert.Equal(new[] { "Dead Cells", "Hades", "Hades II" }, Names("rogue"));
    }

    [Fact]
    public void MatchIsCaseInsensitive_AndTrimmed() => Assert.Equal(new[] { "Cyberpunk 2077" }, Names("  CYBER "));

    [Fact]
    public void NoMatch_IsEmpty() => Assert.Empty(Names("zzz"));

    [Fact]
    public void ResultsAreCapped() => Assert.Equal(2, Names("a", max: 2).Count);

    [Fact]
    public void TrayMenuHotkey_DefaultsToCtrlAltT_IncludingForASettingsFileWithoutIt()
    {
        Assert.Equal("Ctrl+Alt+T", new AppSettings().TrayMenuHotkey);
        var older = JsonSerializer.Deserialize<AppSettings>("{\"GlobalManageHotkey\": \"Ctrl+Alt+G\"}");
        Assert.Equal("Ctrl+Alt+T", older!.TrayMenuHotkey);
    }

    [Fact]
    public void SearchBox_IsOnByDefault_IncludingForASettingsFileFromBefore150()
    {
        Assert.True(new AppSettings().ShowTraySearch);
        var older = JsonSerializer.Deserialize<AppSettings>("{\"CompactTrayMenu\": true}");
        Assert.True(older!.ShowTraySearch);
    }
}
