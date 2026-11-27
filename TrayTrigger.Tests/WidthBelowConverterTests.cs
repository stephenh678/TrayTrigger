using TrayTrigger.Converters;

namespace TrayTrigger.Tests;

/// <summary>Details List's Profile column gives way below a width, never before the view is measured.</summary>
public class WidthBelowConverterTests
{
    [Theory]
    [InlineData(900, "1100", true)]
    [InlineData(1100, "1100", false)]
    [InlineData(1400, "1100", false)]
    [InlineData(0, "1100", false)]          // not measured yet: show the column
    [InlineData(900, "not a number", false)]
    [InlineData(900, null, false)]
    public void IsBelow_OnlyForAMeasuredWidthUnderTheLimit(double width, string? limit, bool expected) =>
        Assert.Equal(expected, WidthBelowConverter.IsBelow(width, limit));
}
