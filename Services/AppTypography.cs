using System;
using System.Collections.Generic;
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
    /// <summary>The smallest and largest text scale the setting allows, in percent. 100 is the sizes as designed.</summary>
    public const int MinScalePercent = 90, MaxScalePercent = 150;

    /// <summary>
    /// The largest scale every screen is checked at. Past it the setting still works and says some screens may clip text,
    /// the way the corner roundness does past its usual range.
    /// </summary>
    public const int RecommendedMaxScalePercent = 130;

    private static IReadOnlyDictionary<string, double>? _designed;

    /// <summary>The scale as a number the app can use: anything out of range, or not a number, is brought to the nearest allowed value.</summary>
    public static double ClampScale(double scale) =>
        double.IsNaN(scale) || double.IsInfinity(scale) ? 1.0 : Math.Clamp(scale, MinScalePercent / 100.0, MaxScalePercent / 100.0);

    /// <summary>
    /// One text size at this scale: to the nearest half pixel, so text stays crisp. The badge text, the smallest in the
    /// app, never goes below its own size, since smaller would be too small to read.
    /// </summary>
    public static double Scaled(string key, double designed, double scale)
    {
        double size = Math.Round(designed * ClampScale(scale) * 2) / 2;
        return key == "FontSizeBadge" ? Math.Max(size, designed) : size;
    }

    /// <summary>
    /// Puts every text size in the running app at this scale. The views use the sizes as live resources, so open windows
    /// change at once. A no-op with no <see cref="Application"/> (the unit tests).
    /// </summary>
    public static void Apply(double scale)
    {
        if (Application.Current == null) return;
        Fill(Application.Current.Resources, scale);
    }

    /// <summary>
    /// Puts every text size in this dictionary at this scale: the app's own, or a window's, such as the theme editor's
    /// preview, which shows a theme's size before it is saved.
    /// </summary>
    public static void Fill(ResourceDictionary target, double scale)
    {
        _designed ??= ReadDesigned();
        foreach (var (key, designed) in _designed)
            target[key] = Scaled(key, designed, scale);
    }

    /// <summary>A size given when the app was started ("--text-scale", for a capture): it wins over every theme's own and is not saved.</summary>
    public static double? OverrideScale { get; set; }

    /// <summary>The size Settings asks for, in percent: what every theme that does not set its own size is shown at.</summary>
    public static int SystemPercent { get; set; } = 100;

    /// <summary>
    /// Puts the text at the size for the theme on screen: its own <c>textSize</c> in percent when it has one, and otherwise the
    /// size from Settings.
    /// </summary>
    public static void ApplyForTheme(int? percent) => Apply(OverrideScale ?? (percent ?? SystemPercent) / 100.0);

    private static IReadOnlyDictionary<string, double> ReadDesigned()
    {
        var file = AppResources.LoadFresh("Typography.xaml");
        var sizes = new Dictionary<string, double>();
        foreach (var key in file.Keys)
            if (key is string name && file[name] is double size) sizes[name] = size;
        return sizes;
    }

    /// <summary>The size with this key in Typography.xaml, for example <c>FontSizeBody</c>.</summary>
    public static double Size(string key)
    {
        object? found = AppResources.Find(key, "Typography.xaml");
        if (found is double size) return size;
        LoggingService.Warn("Typography", $"No text size named '{key}' in Typography.xaml; using 12.");
        return 12;
    }
}
