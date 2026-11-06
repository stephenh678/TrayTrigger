using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TrayTrigger.Views;

namespace TrayTrigger.Tests;

/// <summary>The pages' card search: word matching, what counts as a card's text, and finding cards
/// on every tab without changing what the page shows.</summary>
public class CardSearchTests
{
    [Fact]
    public void Matches_NeedsEveryWord_IgnoringCaseAndSpacing()
    {
        const string text = "Window & Tray Icon Global hotkey to show or hide the window ";

        Assert.True(CardSearch.Matches(text, CardSearch.SplitTerms("HOTKEY")));
        Assert.True(CardSearch.Matches(text, CardSearch.SplitTerms("  tray   hotkey ")));
        Assert.False(CardSearch.Matches(text, CardSearch.SplitTerms("tray hdr")));
        Assert.Empty(CardSearch.SplitTerms("   "));
        Assert.True(CardSearch.Matches(text, CardSearch.SplitTerms(null)));
    }

    [Fact]
    public void CollectText_ReadsLabelsAndTooltips_NotWhatWasTyped()
    {
        WpfTestHost.Run(() =>
        {
            var card = new StackPanel();
            card.Children.Add(new TextBlock { Text = "Updates" });
            card.Children.Add(new CheckBox { Content = "Include beta releases", ToolTip = "Pre-release builds" });
            card.Children.Add(new TextBox { Text = "secret-api-key" });

            string text = CardSearch.CollectText(card);

            Assert.Contains("Updates", text);
            Assert.Contains("Include beta releases", text);
            Assert.Contains("Pre-release builds", text);
            Assert.DoesNotContain("secret-api-key", text);
        });
    }

    [Fact]
    public void CollectText_ReadsRowsGeneratedFromAnItemsSource()
    {
        WpfTestHost.Run(() =>
        {
            // The shape of About's Help Topics card: groups, each a label and a row of topic buttons.
            const string xaml = """
                <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <ItemsControl>
                        <ItemsControl.ItemTemplate>
                            <DataTemplate>
                                <StackPanel>
                                    <TextBlock Text="{Binding Label}"/>
                                    <ItemsControl ItemsSource="{Binding Topics}">
                                        <ItemsControl.ItemTemplate>
                                            <DataTemplate>
                                                <Button Content="{Binding Title}"/>
                                            </DataTemplate>
                                        </ItemsControl.ItemTemplate>
                                    </ItemsControl>
                                </StackPanel>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                </Border>
                """;
            var card = (Border)System.Windows.Markup.XamlReader.Parse(xaml);
            ((ItemsControl)card.Child).ItemsSource = new[]
            {
                new TrayTrigger.Services.HelpTopicGroup("tweaks", "Performance Tweaks",
                    [new TrayTrigger.Services.HelpTopicLink("tweaks/auto_hdr", "Auto HDR")]),
            };
            card.Measure(new Size(400, 400));
            card.Arrange(new Rect(0, 0, 400, 400));

            string text = CardSearch.CollectText(card);

            Assert.Contains("Performance Tweaks", text);
            Assert.Contains("Auto HDR", text);
        });
    }

    [Fact]
    public void Find_SearchesEveryTab_EvenOnesThePageIsNotShowing()
    {
        WpfTestHost.Run(() =>
        {
            var host = new StackPanel();
            var general = Section(host, "GENERAL", "Updates");
            var diagnostics = Section(host, "DIAGNOSTICS & STORAGE", "Debug log");
            CardSearch.SetTab(general, "General");
            CardSearch.SetTabLabel(general, "General");
            CardSearch.SetTab(diagnostics, "Diagnostics");
            CardSearch.SetTabLabel(diagnostics, "Diagnostics & Storage");
            // The page is on General: the other tab's section is collapsed, as the tab rule leaves it.
            diagnostics.Visibility = Visibility.Collapsed;

            var hits = CardSearch.Find(host, "log");

            var hit = Assert.Single(hits);
            Assert.Equal("Debug log", hit.Title);
            Assert.Equal("Diagnostics", hit.Tab);
            Assert.Equal("Diagnostics & Storage", hit.TabLabel);
            // Searching never changes what the page shows.
            Assert.Equal(Visibility.Collapsed, diagnostics.Visibility);
            Assert.Equal(Visibility.Visible, general.Children[1].Visibility);
        });
    }

    [Fact]
    public void Find_NeedsEveryWord_AndTheTabNameCounts()
    {
        WpfTestHost.Run(() =>
        {
            var host = new StackPanel();
            var tray = Section(host, "TRAY MENU", "Recent games list", "Tray hotkey");
            CardSearch.SetTabLabel(tray, "Tray Menu");

            Assert.Equal(2, CardSearch.Find(host, "tray menu").Count);
            Assert.Single(CardSearch.Find(host, "recent games"));
            Assert.Empty(CardSearch.Find(host, "recent hdr"));
            Assert.Empty(CardSearch.Find(host, "   "));
        });
    }

    [Fact]
    public void Find_PutsATitleMatchAheadOfATextMatch_AndSnipsTheMatchingLine()
    {
        WpfTestHost.Run(() =>
        {
            var host = new StackPanel();
            var section = new StackPanel();
            var other = new StackPanel();
            other.Children.Add(new TextBlock { Text = "Appearance" });
            other.Children.Add(new TextBlock { Text = "Pick a theme. The hotkey colour follows it." });
            var hotkeys = new StackPanel();
            hotkeys.Children.Add(new TextBlock { Text = "Hotkeys" });
            hotkeys.Children.Add(new TextBlock { Text = "Global hotkey to show or hide the window." });
            section.Children.Add(new Border { Child = other });
            section.Children.Add(new Border { Child = hotkeys });
            host.Children.Add(section);

            var hits = CardSearch.Find(host, "hotkey");

            Assert.Equal(["Hotkeys", "Appearance"], hits.Select(h => h.Title));
            Assert.Equal("Global hotkey to show or hide the window.", hits[0].Snippet);
        });
    }

    [Fact]
    public void Find_SkipsCardsTheirOwnRuleHides_AndNeverReadsATextBox()
    {
        WpfTestHost.Run(() =>
        {
            var host = new StackPanel();
            var specs = Section(host, "HARDWARE SPECS", "Graphics Card (GPU)");
            // Like a setting for hardware this PC lacks: collapsed by its own rule.
            specs.Children.Add(new Border { Child = new TextBlock { Text = "Detecting hardware…" }, Visibility = Visibility.Collapsed });
            var keys = new StackPanel();
            keys.Children.Add(new TextBlock { Text = "Art sources" });
            keys.Children.Add(new TextBox { Text = "secret-api-key" });
            specs.Children.Add(new Border { Child = keys });

            Assert.Empty(CardSearch.Find(host, "detecting"));
            Assert.Empty(CardSearch.Find(host, "secret"));
            Assert.Single(CardSearch.Find(host, "art sources"));
        });
    }

    [Fact]
    public void Find_ListsNoMoreThanTheDropdownShows()
    {
        WpfTestHost.Run(() =>
        {
            var host = new StackPanel();
            Section(host, "GENERAL", Enumerable.Range(1, 30).Select(i => $"Option {i}").ToArray());

            Assert.Equal(CardSearch.MaxHits, CardSearch.Find(host, "option", out int total).Count);
            Assert.Equal(30, total);
        });
    }

    [Fact]
    public void Find_NamesACardByWhatItShows_NotByATooltipOnItsFirstControl()
    {
        WpfTestHost.Run(() =>
        {
            var host = new StackPanel();
            var section = new StackPanel();
            var card = new StackPanel();
            card.Children.Add(new CheckBox { Content = "Start with Windows", ToolTip = "Runs at logon" });
            section.Children.Add(new Border { Child = card });
            host.Children.Add(section);

            var hit = Assert.Single(CardSearch.Find(host, "logon"));

            Assert.Equal("Start with Windows", hit.Title);
            Assert.Equal("Runs at logon", hit.Snippet);
        });
    }

    [Fact]
    public void Find_FallsBackToTheTabName_ForACardWithNoReadableTitle()
    {
        WpfTestHost.Run(() =>
        {
            var host = new StackPanel();
            var section = Section(host, "GENERAL");
            CardSearch.SetTabLabel(section, "General");
            section.Children.Add(new Border { Child = new TextBlock { Text = "Go" } });

            var hit = Assert.Single(CardSearch.Find(host, "go"));

            Assert.Equal("General", hit.Title);
        });
    }

    [Fact]
    public void RowOf_OutlinesTheListRowHoldingTheMatch_AndTheCardForAMatchOutsideAList()
    {
        WpfTestHost.Run(() =>
        {
            var template = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(TextBlock)) };
            template.VisualTree.SetBinding(TextBlock.TextProperty, new Binding());
            var list = new ItemsControl
            {
                ItemsSource = new[] { "Reduces the CPU reserve", "Raises the Games task priority" },
                ItemTemplate = template,
            };
            var inner = new StackPanel();
            inner.Children.Add(new TextBlock { Text = "Aggressive profile" });
            inner.Children.Add(list);
            var card = new Border { Child = inner };
            // Visibility needs a presentation source, so the card goes in a window kept off screen.
            var window = new Window
            {
                Content = card, Width = 400, Height = 300, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
            };
            try
            {
                window.Show();
                window.UpdateLayout();

                FrameworkElement? row = CardSearch.RowOf(card, ["games"]);

                Assert.NotNull(row);
                Assert.NotSame(card, row);
                Assert.Same(list, ItemsControl.ItemsControlFromItemContainer(row));
                // A match in the card's own title is not in a list row: the whole card is outlined.
                Assert.Same(card, CardSearch.RowOf(card, ["aggressive"]));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ANewQuery_EndsTheMarkedRow()
    {
        WpfTestHost.Run(() =>
        {
            var host = new StackPanel();
            var viewer = new ScrollViewer { Content = host };
            CardSearch.SetQuery(host, "cpu");
            host.SetValue(CardSearch.HighlightRootProperty, new Border());

            CardSearch.SetQuery(host, "gpu");

            Assert.Null(CardSearch.GetHighlightRoot(host));
            Assert.NotNull(viewer);
        });
    }

    [Fact]
    public void CollectText_ReadsAListsTextItemsBeforeItHasRows()
    {
        WpfTestHost.Run(() =>
        {
            // A ComboBox whose dropdown has never opened has no item containers yet.
            var combo = new ComboBox { ItemsSource = new[] { "Alphabetical (A - Z)", "Recently Played" } };

            Assert.Contains("Recently Played", CardSearch.CollectText(new Border { Child = combo }));
        });
    }

    [Fact]
    public void FindRanges_FindsEveryWord_MergingOverlaps()
    {
        Assert.Equal(new[] { (0, 4), (15, 4) }, SearchHighlight.FindRanges("Tray icon, the tray", ["TRAY"]));
        Assert.Equal(new[] { (0, 4) }, SearchHighlight.FindRanges("tray", ["tray", "ray"]));
        Assert.Equal(new[] { (5, 6), (12, 4) }, SearchHighlight.FindRanges("Show hotkey menu", ["menu", "hotkey"]));
        Assert.Empty(SearchHighlight.FindRanges("Updates", ["hdr"]));
    }

    [Fact]
    public void MatchRects_PutsARectangleOverTheMatchedWord()
    {
        WpfTestHost.Run(() =>
        {
            var block = new TextBlock { Text = "Global hotkey", FontSize = 12 };
            block.Measure(new Size(400, 100));
            block.Arrange(new Rect(0, 0, 400, 100));

            var rects = SearchHighlight.MatchRects(block, ["hotkey"]);

            var rect = Assert.Single(rects);
            Assert.True(rect.Left > 0, "the match starts after \"Global \"");
            Assert.True(rect.Width > 0);
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
