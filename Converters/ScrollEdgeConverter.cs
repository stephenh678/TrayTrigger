using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TrayTrigger.Converters;

/// <summary>
/// Whether a horizontally scrolling strip has more to show past one edge: values[0] is the
/// ScrollViewer's HorizontalOffset, values[1] its ScrollableWidth, and the parameter "Left" or
/// "Right" picks the edge. Drives the chevrons of the EdgeChevronScrollViewer style (UX-14f).
/// </summary>
public sealed class ScrollEdgeConverter : IMultiValueConverter
{
    /// <summary>Half a pixel of slack, so layout rounding never leaves a chevron showing at an end.</summary>
    private const double Slack = 0.5;

    public static bool HasMore(double offset, double scrollable, bool left) =>
        left ? offset > Slack : offset < scrollable - Slack;

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double offset || values[1] is not double scrollable)
            return Visibility.Collapsed;
        bool left = string.Equals(parameter as string, "Left", StringComparison.OrdinalIgnoreCase);
        return HasMore(offset, scrollable, left) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
