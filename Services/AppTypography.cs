using System;
using System.Windows;

namespace TrayTrigger.Services;

/// <summary>
/// Reads the app's text sizes by name from <c>Resources/Typography.xaml</c>, for the few places that set a
/// size in code. Those places name a size here and never write its number, so the file stays the one place
/// where a size is defined. See docs/style-guide.html.
///
/// <para>The running app answers from its own resources; with no <see cref="Application"/> (the unit tests)
/// <see cref="AppResources"/> loads Typography.xaml itself.</para>
/// </summary>
public static class AppTypography
{
    /// <summary>The size with this key in Typography.xaml, for example <c>FontSizeBody</c>.</summary>
    public static double Size(string key)
    {
        object? found = AppResources.Find(key, "Typography.xaml");
        if (found is double size) return size;
        LoggingService.Warn("Typography", $"No text size named '{key}' in Typography.xaml; using 12.");
        return 12;
    }
}
