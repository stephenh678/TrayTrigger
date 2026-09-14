using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TrayTrigger.Converters;

/// <summary>
/// The (active, hovered) pair both segmented-tab converters are fed: values[0] is the button's
/// Tag (a bool when bound to a view-model flag; the string "False" from the style's default
/// setter, which counts as inactive), values[1] its live IsMouseOver.
/// </summary>
internal static class TabState
{
    public static (bool IsActive, bool IsHovered) Read(object[] values) =>
        (values.Length > 0 && values[0] is bool a && a,
         values.Length > 1 && values[1] is bool h && h);
}

/// <summary>
/// Resolves a segmented tab's background from its "is active" flag (values[0], bound via Tag)
/// and live IsMouseOver state (values[1]). Implemented as a converter rather than XAML triggers
/// because matching a string trigger Value against a boxed bool read through an object-typed
/// property (Tag) via RelativeSource TemplatedParent is unreliable in WPF - a converter works
/// with the actual runtime values directly, with no markup-time type coercion involved.
/// </summary>
public sealed class TabBackgroundConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        string? key = TabState.Read(values) switch
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
public sealed class TabForegroundConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var (isActive, isHovered) = TabState.Read(values);

        if (isActive || isHovered) return Brushes.White;
        return Application.Current?.TryFindResource("BrushTextSecondary") as Brush ?? Brushes.Gray;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
