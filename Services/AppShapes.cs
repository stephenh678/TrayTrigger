using System;
using System.Windows;

namespace TrayTrigger.Services;

/// <summary>
/// Reads the app's corner radii by name from <c>Resources/Shapes.xaml</c>, for the few places that build a
/// shape in code. Those places name a radius here and never write its number, so the file stays the one
/// place where a radius is defined. See docs/style-guide.html.
///
/// <para>The running app answers from its own resources; with no <see cref="Application"/> (the unit tests)
/// <see cref="AppResources"/> loads Shapes.xaml itself.</para>
/// </summary>
public static class AppShapes
{
    /// <summary>The radius with this key in Shapes.xaml, for example <c>RadiusCard</c>.</summary>
    public static CornerRadius Radius(string key)
    {
        object? found = AppResources.Find(key, "Shapes.xaml");
        if (found is CornerRadius radius) return radius;
        LoggingService.Warn("Shapes", $"No corner radius named '{key}' in Shapes.xaml; using 0.");
        return new CornerRadius(0);
    }
}
