using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TrayTrigger.Converters;

/// <summary>
/// Resolves a segmented tab's background from its "is active" flag (values[0], bound via Tag)
/// and live IsMouseOver state (values[1]). Implemented as a converter rather than XAML triggers
/// because matching a string trigger Value against a boxed bool read through an object-typed
/// property (Tag) via RelativeSource TemplatedParent is unreliable in WPF - a converter works
/// with the actual runtime values directly, with no markup-time type coercion involved.
/// </summary>
public class TabBackgroundConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isActive = values.Length > 0 && values[0] is bool a && a;
        bool isHovered = values.Length > 1 && values[1] is bool h && h;

        string? key = (isActive, isHovered) switch
        {
            (true, true) => "BrushAccentHover",
            (true, false) => "BrushAccent",
            (false, true) => "BrushSurfaceHover",
            (false, false) => null
        };

        if (key == null) return Brushes.Transparent;
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Companion to <see cref="TabBackgroundConverter"/> - resolves a segmented tab's text color for
/// the same active/hover state combination.
/// </summary>
public class TabForegroundConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isActive = values.Length > 0 && values[0] is bool a && a;
        bool isHovered = values.Length > 1 && values[1] is bool h && h;

        if (isActive || isHovered) return Brushes.White;
        return Application.Current?.TryFindResource("BrushTextSecondary") as Brush ?? Brushes.Gray;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
