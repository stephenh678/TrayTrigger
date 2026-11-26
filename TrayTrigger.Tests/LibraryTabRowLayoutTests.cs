using TrayTrigger.Converters;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>The Library's one tab row: categories left, collections right, a divider between them.</summary>
public class LibraryTabRowLayoutTests
{
    [Fact]
    public void BothFit_EachTakesWhatItNeeds() =>
        Assert.Equal((300d, 200d), LibraryTabRowLayout.Widths(1000, 300, 200, 0.6));

    [Fact]
    public void NoCollections_CategoriesHaveTheRow()
    {
        Assert.Equal((300d, 0d), LibraryTabRowLayout.Widths(1000, 300, 0, 0.6));
        Assert.Equal((1000d, 0d), LibraryTabRowLayout.Widths(1000, 1400, 0, 0.6));
    }

    [Fact]
    public void BothOverflow_TheyShareAtTheSplit() =>
        Assert.Equal((600d, 400d), LibraryTabRowLayout.Widths(1000, 900, 800, 0.6));

    [Fact]
    public void OneNeedsLessThanItsShare_TheOtherGetsTheRest()
    {
        Assert.Equal((200d, 800d), LibraryTabRowLayout.Widths(1000, 200, 1500, 0.6));
        Assert.Equal((850d, 150d), LibraryTabRowLayout.Widths(1000, 1500, 150, 0.6));
    }

    [Fact]
    public void NeitherGoesBelowTheMinimum()
    {
        Assert.Equal((880d, 120d), LibraryTabRowLayout.Widths(1000, 1500, 1500, 0.99));
        Assert.Equal((120d, 880d), LibraryTabRowLayout.Widths(1000, 1500, 1500, 0.01));
    }

    [Fact]
    public void ADrag_IsKeptToTheMinimums()
    {
        Assert.Equal(0.5, LibraryTabRowLayout.SplitFor(500, 1000), 3);
        Assert.Equal(0.12, LibraryTabRowLayout.SplitFor(10, 1000), 3);
        Assert.Equal(0.88, LibraryTabRowLayout.SplitFor(990, 1000), 3);
    }

    [Fact]
    public void TheDividerOnlyMatters_WhenBothCantFit()
    {
        Assert.False(LibraryTabRowLayout.Shares(1000, 300, 200)); // both fit: a drag must not change the saved split
        Assert.False(LibraryTabRowLayout.Shares(1000, 1500, 0));  // no collections: no divider
        Assert.True(LibraryTabRowLayout.Shares(1000, 900, 800));
        Assert.True(LibraryTabRowLayout.Shares(1000, 200, 1500)); // one side needs little: still sharing
    }

    [Fact]
    public void WhileBothFit_TheSplitChangesNothing() =>
        Assert.Equal(LibraryTabRowLayout.Widths(1000, 300, 200, 0.1), LibraryTabRowLayout.Widths(1000, 300, 200, 0.9));

    [Fact]
    public void AnUnreadableSplit_IsTheDefault() =>
        Assert.Equal(LibraryTabRowLayout.DefaultSplit, LibraryTabRowLayout.ClampSplit(double.NaN));

    [Theory]
    [InlineData(300, 400, false)] // the tabs fit
    [InlineData(400.3, 400, false)] // rounding
    [InlineData(500, 400, true)]
    [InlineData(500, 0, false)] // not laid out yet
    public void Arrows_ShowOnlyWhenTheTabsAreWiderThanTheStrip(double extent, double width, bool show) =>
        Assert.Equal(show, ScrollArrowConverter.Overflows(extent, width));
}
