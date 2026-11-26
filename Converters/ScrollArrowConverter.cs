using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TrayTrigger.Converters;

/// <summary>
/// The side arrows of the SideArrowScrollViewer style (the Library's category and collection strips). Parameter "Show":
/// values are the ScrollViewer's ExtentWidth and ActualWidth, and both arrows show while the tabs are wider than the
/// whole strip. The whole strip, not the viewport between the arrows: measured against the viewport, arrows that once
/// appeared would keep themselves showing after the window grew enough to fit every tab. Parameter "Left" or "Right":
/// values are HorizontalOffset and ScrollableWidth, and that arrow is enabled while there is more past its edge (it dims at the end).
/// </summary>
public sealed class ScrollArrowConverter : IMultiValueConverter
{
    private const double Slack = 0.5;

    public static bool Overflows(double extent, double width) => width > 0 && extent > width + Slack;

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool show = string.Equals(parameter as string, "Show", StringComparison.OrdinalIgnoreCase);
        if (values.Length < 2 || values[0] is not double a || values[1] is not double b)
            return show ? Visibility.Collapsed : false;
        if (show) return Overflows(a, b) ? Visibility.Visible : Visibility.Collapsed;
        bool left = string.Equals(parameter as string, "Left", StringComparison.OrdinalIgnoreCase);
        return ScrollEdgeConverter.HasMore(a, b, left);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
