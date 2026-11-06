using System.Text.RegularExpressions;
using Xunit;
using static TrayTrigger.Tests.SourceTree;

namespace TrayTrigger.Tests;

/// <summary>
/// Keeps every card the page searches reachable from its results. A result takes you to a card's tab, so
/// each tab's section (Settings, About, System) must name that tab
/// with <c>CardSearch.Tab</c> and <c>TabLabel</c>; one that doesn't is listed with no way to show it.
/// </summary>
public class SearchTabGuardTests
{
    [Theory]
    [InlineData("Views/SettingsView.xaml")]
    [InlineData("Views/AboutView.xaml")]
    [InlineData("Views/SystemView.xaml")]
    public void EverySection_NamesItsTab(string view)
    {
        string xaml = Read(view);
        // Only the sections inside the page's card panel (after its Query), not other tab-driven panels.
        int panel = xaml.IndexOf("CardSearch.Query", StringComparison.Ordinal);
        Assert.True(panel > 0, $"{view} has no card search.");

        var missing = Regex.Matches(xaml[panel..], "<StackPanel\\s+Visibility=\"\\{Binding Show\\w+Section,[^>]*>")
            .Where(m => !m.Value.Contains("CardSearch.Tab=") || !m.Value.Contains("CardSearch.TabLabel="))
            .Select(m => Regex.Replace(m.Value, "\\s+", " "))
            .ToList();

        Assert.True(missing.Count == 0, $"{view}: sections without a CardSearch.Tab and TabLabel:\n" + string.Join('\n', missing));
    }

    /// <summary>
    /// A box that narrows the list under it says "Filter"; one that finds a card and takes you to it says
    /// "Search". The Library, Tools and Activity boxes have no <c>ResultsHost</c>, so they are filters.
    /// </summary>
    [Theory]
    [InlineData("Views/LibraryView.xaml")]
    [InlineData("Views/ToolsView.xaml")]
    [InlineData("Views/ActivityView.xaml")]
    public void ListPageBoxes_AreCalledFilters(string view)
    {
        var hints = Regex.Matches(Read(view), @"<views:SearchBox\b[^>]*Placeholder=""([^""]*)""")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.Single(hints);
        Assert.StartsWith("Filter ", hints[0]);
        // The clear x says the same: it clears a filter here, a search on the card pages.
        Assert.Contains("ClearText=\"Clear filter\"", Read(view));
    }

    [Theory]
    [InlineData("Views/SettingsView.xaml")]
    [InlineData("Views/AboutView.xaml")]
    [InlineData("Views/SystemView.xaml")]
    public void CardPageBoxes_AreCalledSearches(string view)
    {
        var hints = Regex.Matches(Read(view), @"<views:SearchBox\b[^>]*Placeholder=""([^""]*)""")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.Single(hints);
        Assert.StartsWith("Search ", hints[0]);
    }
}
