using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace TrayTrigger.Services;

/// <summary>
/// Reads the app's colors by name from <c>Resources/Colors.xaml</c>, for the places that have to build a
/// color in code (a view model that hands a color to a binding, a dialog that sets a brush when it opens).
/// Those places name a color here and never write its value, so the file stays the one place where a
/// color is defined. See docs/style-guide.html.
///
/// <para>The running app answers from its own resources; with no <see cref="Application"/> (the unit tests)
/// <see cref="AppResources"/> loads Colors.xaml itself.</para>
/// </summary>
public static class AppPalette
{
    /// <summary>The brush with this key in Colors.xaml, for example <c>BrushDanger</c>.</summary>
    public static Brush Brush(string key)
    {
        if (Lookup(key) is Brush brush) return brush;
        LoggingService.Warn("Palette", $"No brush named '{key}' in Colors.xaml; using Transparent.");
        return Brushes.Transparent;
    }

    /// <summary>
    /// The color of a brush in Colors.xaml as the string a binding turns into a brush: "#RRGGBB", or
    /// "#AARRGGBB" when it is not fully opaque.
    /// </summary>
    public static string Hex(string brushKey)
    {
        if (Lookup(brushKey) is SolidColorBrush brush)
        {
            var c = brush.Color;
            return c.A == 0xFF
                ? string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B)
                : string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}{3:X2}", c.A, c.R, c.G, c.B);
        }
        LoggingService.Warn("Palette", $"No solid brush named '{brushKey}' in Colors.xaml; using Transparent.");
        return "#00000000";
    }

    /// <summary>The color of a solid brush in Colors.xaml, for the few places that want a <see cref="Color"/> and not a brush.</summary>
    public static Color ColorOf(string brushKey)
    {
        if (Lookup(brushKey) is SolidColorBrush brush) return brush.Color;
        LoggingService.Warn("Palette", $"No solid brush named '{brushKey}' in Colors.xaml; using Transparent.");
        return Colors.Transparent;
    }

    private static object? Lookup(string key) => AppResources.Find(key, "Colors.xaml");
}
