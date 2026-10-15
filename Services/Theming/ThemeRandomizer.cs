using System;
using System.Windows.Media;

namespace TrayTrigger.Services.Theming;

/// <summary>
/// Makes a starting point for the theme editor's Randomize button: a set of main colors built from one hue (a tinted background, text
/// that stands out from it, an accent of that hue, and red, amber and green for danger, warning and success). A set is only offered
/// once the caller's check says its text is easy to read, so what comes out is always inside the editor's contrast gate; the
/// person then changes whatever they like by hand. The random source is passed in, so a test can repeat a result.
/// </summary>
internal static class ThemeRandomizer
{
    /// <summary>How many sets are tried before giving up (a theme whose fine-tuning colors are set by hand can make every set fail).</summary>
    internal const int MaxTries = 80;

    /// <summary>The six main colors that are chosen; Surface (cards and panels) follows the background.</summary>
    internal sealed record Colors(Color Background, Color Text, Color Accent, Color Danger, Color Warning, Color Success);

    /// <summary>The first set of colors that <paramref name="passes"/>, or null when none did in <see cref="MaxTries"/> tries.</summary>
    internal static Colors? Roll(Random random, bool isDark, Func<Colors, bool> passes)
    {
        for (int i = 0; i < MaxTries; i++)
        {
            var candidate = Candidate(random, isDark);
            if (passes(candidate)) return candidate;
        }
        return null;
    }

    private static Colors Candidate(Random random, bool isDark)
    {
        double Between(double low, double high) => low + random.NextDouble() * (high - low);
        double Wrap(double hue) => ((hue % 360) + 360) % 360;
        Color Hsv(double hue, double saturation, double value) => ThemeColor.FromHsv(Wrap(hue), saturation, value);

        double hue = Between(0, 360);
        return isDark
            ? new Colors(
                Background: Hsv(hue + Between(-15, 15), Between(0.15, 0.50), Between(0.05, 0.10)),
                Text: Hsv(hue, Between(0, 0.05), Between(0.96, 1.0)),
                Accent: Hsv(hue, Between(0.55, 0.90), Between(0.55, 0.78)),
                Danger: Hsv(Between(350, 375), Between(0.50, 0.72), Between(0.80, 0.92)),
                Warning: Hsv(Between(38, 48), Between(0.50, 0.75), Between(0.85, 0.95)),
                Success: Hsv(Between(125, 145), Between(0.55, 0.75), Between(0.65, 0.85)))
            : new Colors(
                Background: Hsv(hue + Between(-15, 15), Between(0.03, 0.12), Between(0.95, 0.99)),
                Text: Hsv(hue, Between(0.10, 0.30), Between(0.08, 0.14)),
                Accent: Hsv(hue, Between(0.70, 0.95), Between(0.50, 0.72)),
                Danger: Hsv(Between(355, 370), Between(0.75, 0.90), Between(0.65, 0.80)),
                Warning: Hsv(Between(30, 40), Between(0.85, 1.0), Between(0.60, 0.75)),
                Success: Hsv(Between(130, 145), Between(0.70, 0.90), Between(0.40, 0.55)));
    }
}
