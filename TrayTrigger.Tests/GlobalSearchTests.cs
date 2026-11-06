using System.Linq;
using System.Windows.Controls;
using TrayTrigger.Views;

namespace TrayTrigger.Tests;

/// <summary>The window's search: which groups it lists, in what order, how many of each, and how a
/// game, a tool and a page's card are matched.</summary>
public class GlobalSearchTests
{
    private static NamedItem Item(string name, string detail = "", object? target = null) => new(name, detail, target ?? name);

    private static List<GlobalGroup> Find(string query, IEnumerable<NamedItem>? games = null, IEnumerable<NamedItem>? tools = null,
        Panel? settings = null, Panel? system = null, Panel? about = null, int activity = 0) =>
        GlobalSearch.Find(query, games ?? [], tools ?? [], settings, system, about, activity);

    [Fact]
    public void NothingTyped_ListsNothing()
    {
        Assert.Empty(Find("   ", games: [Item("Doom")]));
        Assert.Empty(Find("", games: [Item("Doom")]));
    }

    [Fact]
    public void Games_MatchNameOrCategory_EveryWord_NameStartsFirst()
    {
        var games = new[] { Item("The Witcher 3", "RPG"), Item("Witcher Tales", "Cards"), Item("Doom", "Shooter"), Item("Pillars", "witcher-like RPG") };

        var group = Assert.Single(Find("witcher", games));

        Assert.Equal("Games", group.Name);
        // A name starting with the word, then a name containing it, then a category match.
        Assert.Equal(["Witcher Tales", "The Witcher 3", "Pillars"], group.Hits.Select(h => h.Title));

        // Every word must be in the name or the category.
        var both = Assert.Single(Find("witcher rpg", games)).Hits.Select(h => h.Title);
        Assert.Equal(["Pillars", "The Witcher 3"], both);
    }

    [Fact]
    public void Games_AreCappedAt5_WithAShowAllLineThatCarriesTheQuery()
    {
        var games = Enumerable.Range(1, 8).Select(n => Item($"Star Game {n}")).ToList();

        var group = Assert.Single(Find("star", games));

        Assert.Equal(GlobalSearch.MaxGames + 1, group.Hits.Count);
        Assert.All(group.Hits.Take(GlobalSearch.MaxGames), h => Assert.Equal(GlobalHitKind.Game, h.Kind));
        var more = group.Hits.Last();
        Assert.Equal(GlobalHitKind.MoreGames, more.Kind);
        Assert.Contains("Show all 8 games", more.Title);
        Assert.Equal("star", more.Target);
    }

    [Fact]
    public void ExactlyTheCap_HasNoShowAllLine()
    {
        var games = Enumerable.Range(1, GlobalSearch.MaxGames).Select(n => Item($"Star Game {n}")).ToList();

        Assert.DoesNotContain(Find("star", games).Single().Hits, h => h.Kind == GlobalHitKind.MoreGames);
    }

    [Fact]
    public void GroupsComeInOrder_AndAGroupWithNothingIsLeftOut()
    {
        WpfTestHost.Run(() =>
        {
            var settings = new StackPanel();
            Section(settings, "General", "Launch options");
            var about = new StackPanel();
            Section(about, "Overview", "Launch help");

            var groups = Find("launch", games: [Item("Launchpad Quest")], tools: [Item("Launcher Tool")], settings: settings, about: about, activity: 3);

            Assert.Equal(["Games", "Tools", "Settings", "About", "Activity"], groups.Select(g => g.Name));
            Assert.Equal(GlobalHitKind.Setting, groups[2].Hits.Single().Kind);
            Assert.Equal(GlobalHitKind.About, groups[3].Hits.Single().Kind);
        });
    }

    [Fact]
    public void EachGroupHasItsOwnCap_SoOneNeverCrowdsOutTheOthers()
    {
        WpfTestHost.Run(() =>
        {
            var settings = new StackPanel();
            Section(settings, "General", Enumerable.Range(1, 9).Select(n => $"Hotkey setting {n}").ToArray());
            var system = new StackPanel();
            Section(system, "Tweaks", Enumerable.Range(1, 9).Select(n => $"Hotkey tweak {n}").ToArray());

            var groups = Find("hotkey", games: [Item("Hotkey Hero")], settings: settings, system: system);

            Assert.Equal("Games", groups[0].Name);
            Assert.Single(groups[0].Hits);
            Assert.Equal(GlobalSearch.MaxSettings, groups.Single(g => g.Name == "Settings").Hits.Count);
            Assert.Equal(GlobalSearch.MaxSystem, groups.Single(g => g.Name == "System").Hits.Count);
        });
    }

    [Fact]
    public void CardHits_CarryTheirTabAsDetailAndTheCardAsTarget()
    {
        WpfTestHost.Run(() =>
        {
            var settings = new StackPanel();
            var section = Section(settings, "Library", "Scan for Games");
            CardSearch.SetTab(section, "LibraryTab");
            CardSearch.SetTabLabel(section, "Library & Art");

            var hit = Find("scan", settings: settings).Single().Hits.Single();

            Assert.Equal("Scan for Games", hit.Title);
            Assert.Equal("Library & Art", hit.Detail);
            var card = Assert.IsType<CardHit>(hit.Target);
            Assert.Equal("LibraryTab", card.Tab);
        });
    }

    [Fact]
    public void Activity_IsOneLineWithTheCount_AndOnlyWhenSomethingMatches()
    {
        var one = Find("crash", activity: 1).Single().Hits.Single();
        Assert.Equal(GlobalHitKind.Activity, one.Kind);
        Assert.Contains("1 entry", one.Detail);
        Assert.Contains("12 entries", Find("crash", activity: 12).Single().Hits.Single().Detail);
        Assert.Empty(Find("crash", activity: 0));
    }

    [Fact]
    public void ACardWithASentenceForATitle_IsNamedByItsTab()
    {
        WpfTestHost.Run(() =>
        {
            var page = new StackPanel();
            var section = Section(page, "Profiles", "Assigned per game in Edit Game. Applied at launch, reverted at exit, and restored after a crash.");
            CardSearch.SetTabLabel(section, "Performance Profiles");

            var hit = CardSearch.Find(page, "crash").Single();

            Assert.Equal("Performance Profiles", hit.Title);
            Assert.Contains("restored after a crash", hit.Snippet);
        });
    }

    private static StackPanel Section(Panel host, string heading, params string[] cardTitles)
    {
        var section = new StackPanel();
        var header = new Border { Child = new TextBlock { Text = heading } };
        CardSearch.SetIsHeader(header, true);
        section.Children.Add(header);
        foreach (string title in cardTitles)
            section.Children.Add(new Border { Child = new TextBlock { Text = title } });
        host.Children.Add(section);
        return section;
    }
}
