using TrayTrigger.Converters;

namespace TrayTrigger.Tests;

/// <summary>UX-14f: the tab strip's chevrons show only on a side that has more tabs past it.</summary>
public class ScrollEdgeConverterTests
{
    [Theory]
    [InlineData(0, 0, false, false)]      // everything fits
    [InlineData(0, 120, false, true)]     // at the start: more to the right
    [InlineData(60, 120, true, true)]     // in the middle: both
    [InlineData(120, 120, true, false)]   // at the end: more to the left
    [InlineData(0.3, 120.2, false, true)] // rounding slack at the ends
    public void Chevrons_FollowTheOffset(double offset, double scrollable, bool left, bool right)
    {
        Assert.Equal(left, ScrollEdgeConverter.HasMore(offset, scrollable, left: true));
        Assert.Equal(right, ScrollEdgeConverter.HasMore(offset, scrollable, left: false));
    }
}
