using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TrayTrigger.Views;

namespace TrayTrigger.Tests;

/// <summary>Settings' card search: word matching, what counts as a card's text, and hiding cards
/// without breaking the page's own visibility rules.</summary>
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
    public void Apply_HidesCardsAndEmptySections_ThenHandsVisibilityBackToTheirOwnRules()
    {
        WpfTestHost.Run(() =>
        {
            var host = new StackPanel();
            var general = Section(host, "GENERAL", "Window & Tray Icon", "Updates");
            var tray = Section(host, "TRAY MENU", "Recent games list");
            var trayTabRule = new TabRule();
            BindingOperations.SetBinding(tray, UIElement.VisibilityProperty, new Binding(nameof(TabRule.Visibility)) { Source = trayTabRule });

            CardSearch.Apply(host, "updates");
            Assert.Equal(Visibility.Collapsed, general.Children[1].Visibility);
            Assert.Equal(Visibility.Visible, general.Children[2].Visibility);
            Assert.Equal(Visibility.Collapsed, tray.Visibility);
            Assert.False(CardSearch.GetNoMatches(host));

            // A matching heading keeps its whole section, even cards that don't mention the words.
            CardSearch.Apply(host, "tray menu");
            Assert.Equal(Visibility.Visible, tray.Visibility);
            Assert.Equal(Visibility.Visible, tray.Children[1].Visibility);
            Assert.Equal(Visibility.Collapsed, general.Visibility);

            CardSearch.Apply(host, "zzz");
            Assert.True(CardSearch.GetNoMatches(host));

            CardSearch.Apply(host, "");
            Assert.False(CardSearch.GetNoMatches(host));
            Assert.Equal(DependencyProperty.UnsetValue, general.ReadLocalValue(UIElement.VisibilityProperty));
            Assert.Equal(DependencyProperty.UnsetValue, general.Children[1].ReadLocalValue(UIElement.VisibilityProperty));
            Assert.Equal(Visibility.Visible, tray.Visibility);

            // The section's tab rule is still live after the search let go of it.
            trayTabRule.Visibility = Visibility.Collapsed;
            Assert.Equal(Visibility.Collapsed, tray.Visibility);
        });
    }

    [Fact]
    public void Apply_OnlySearchesSectionsTheSelectedTabShows()
    {
        WpfTestHost.Run(() =>
        {
            var host = new StackPanel();
            var general = Section(host, "GENERAL", "Updates");
            var diagnostics = Section(host, "DIAGNOSTICS & STORAGE", "Debug log");
            var diagnosticsTab = new TabRule { Visibility = Visibility.Collapsed };
            BindingOperations.SetBinding(diagnostics, UIElement.VisibilityProperty, new Binding(nameof(TabRule.Visibility)) { Source = diagnosticsTab });

            // On a tab without Diagnostics, a word only Diagnostics has finds nothing, and the search
            // doesn't reveal that other tab's section.
            CardSearch.Apply(host, "log");
            Assert.True(CardSearch.GetNoMatches(host));
            Assert.Equal(Visibility.Collapsed, general.Visibility);
            Assert.Equal(Visibility.Collapsed, diagnostics.Visibility);

            // The same text after switching to a tab that shows it.
            diagnosticsTab.Visibility = Visibility.Visible;
            CardSearch.Apply(host, "log");
            Assert.False(CardSearch.GetNoMatches(host));
            Assert.Equal(Visibility.Visible, diagnostics.Visibility);
            Assert.Equal(Visibility.Visible, diagnostics.Children[1].Visibility);
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

    /// <summary>Stands in for a Show*Section property the section's Visibility is bound to.</summary>
    private sealed class TabRule : INotifyPropertyChanged
    {
        private Visibility _visibility = Visibility.Visible;

        public Visibility Visibility
        {
            get => _visibility;
            set
            {
                _visibility = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Visibility)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
