using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace TrayTrigger.Services.Theming;

/// <summary>Every color of a theme by palette name ("Accent", "TextPrimary", ...), the names in Resources/Colors.xaml without "Brush".</summary>
internal sealed class ThemePalette : Dictionary<string, Color>
{
    public ThemePalette() { }
    public ThemePalette(IReadOnlyDictionary<string, Color> from) : base(from) { }
}

/// <summary>
/// Works out the colors a theme does not name. A theme that sets only <c>Accent</c> still needs its hover,
/// pressed and wash colors; those follow from the base theme's own relationship to its accent. See
/// docs/theming-plan.md, Derived colors.
/// </summary>
internal static class ThemeDerivation
{
    /// <summary>The colors that stand for a group. Setting the anchor moves every member of its group that the theme does not set itself.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Anchors = new Dictionary<string, string[]>
    {
        ["Accent"] = ["AccentHover", "AccentText", "AccentPressed", "AccentWash", "BorderAccent", "Brand", "InfoText"],
        ["Background"] = ["Surface", "SurfaceHover", "DialogCard", "Border", "BorderStrong"],
        ["TextPrimary"] = ["TextSecondary", "TextMuted", "TextSoft", "TextDisabled"],
        ["Danger"] = ["DangerWash", "DangerBorder", "DangerSolid", "DangerSolidHover"],
        ["Warning"] = ["WarningWash", "WarningBorder", "Highlight"],
        ["SuccessText"] = ["SuccessWash", "SuccessSolid"],
    };

    /// <summary>
    /// The base theme with <paramref name="overrides"/> applied. A group whose anchor is not overridden is
    /// the base's, unchanged, to the last digit.
    /// </summary>
    public static ThemePalette Resolve(ThemePalette baseTheme, IReadOnlyDictionary<string, Color> overrides)
    {
        var result = new ThemePalette(baseTheme);

        foreach (var (anchor, members) in Anchors)
        {
            if (!overrides.TryGetValue(anchor, out var newAnchor) || !baseTheme.TryGetValue(anchor, out var baseAnchor)) continue;
            foreach (string member in members)
            {
                if (overrides.ContainsKey(member) || !baseTheme.TryGetValue(member, out var baseMember)) continue;
                result[member] = Move(baseAnchor, baseMember, newAnchor);
            }
        }

        // Only names the palette has: a typo (or a name from a newer version) is not a color the app can show.
        foreach (var (name, color) in overrides)
        {
            if (baseTheme.ContainsKey(name)) result[name] = color;
        }
        return result;
    }

    /// <summary>
    /// Where <paramref name="member"/> goes when its anchor moves from <paramref name="anchor"/> to <paramref name="newAnchor"/>:
    /// the same step in lightness; for a colored anchor the same share of its chroma and the same hue offset; for a
    /// near-neutral one (a background, white text) the same offset in the color plane, since its hue means nothing.
    /// </summary>
    internal static Color Move(Color anchor, Color member, Color newAnchor)
    {
        var a = ThemeColor.ToOklch(anchor);
        var m = ThemeColor.ToOklch(member);
        var n = ThemeColor.ToOklch(newAnchor);
        double lightness = n.L + (m.L - a.L);

        if (a.C < 0.02)
        {
            var (_, aa, ab) = ThemeColor.ToOklab(anchor);
            var (_, ma, mb) = ThemeColor.ToOklab(member);
            var (_, na, nb) = ThemeColor.ToOklab(newAnchor);
            return ThemeColor.FromOklab(lightness, na + (ma - aa), nb + (mb - ab), member.A);
        }

        double chroma = n.C * (m.C / a.C);
        double hue = (n.H + (m.H - a.H) + 720) % 360;
        return ThemeColor.FromOklch(new Oklch(lightness, chroma, hue, m.Alpha));
    }
}
