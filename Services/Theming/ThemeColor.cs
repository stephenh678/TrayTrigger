using System;
using System.Globalization;
using System.Windows.Media;

namespace TrayTrigger.Services.Theming;

/// <summary>
/// A color in OKLCH (lightness, chroma, hue), the space derived theme colors are worked out in: a step in
/// lightness or chroma looks the same size whatever the hue, which an RGB or HSL step does not.
/// </summary>
internal readonly record struct Oklch(double L, double C, double H, double Alpha);

/// <summary>Parsing and formatting of theme colors, and the conversion to and from OKLCH.</summary>
internal static class ThemeColor
{
    /// <summary>"#RRGGBB" or "#AARRGGBB". Anything else (a name, a short form, junk) is not a theme color.</summary>
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        if (text is not { Length: 7 or 9 } || text[0] != '#') return false;
        if (!uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint v)) return false;

        color = text.Length == 7
            ? Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    /// <summary>"#RRGGBB", or "#AARRGGBB" when the color is not fully opaque.</summary>
    public static string Format(Color c) => c.A == 0xFF
        ? string.Create(CultureInfo.InvariantCulture, $"#{c.R:X2}{c.G:X2}{c.B:X2}")
        : string.Create(CultureInfo.InvariantCulture, $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}");

    /// <summary>The same color with another opacity (0 clear, 255 solid).</summary>
    public static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    /// <summary>What a person types for a color: "#RRGGBB", "RRGGBB", "#RGB" or "RGB". Always opaque.</summary>
    public static bool TryParseTyped(string? text, out Color color)
    {
        color = default;
        string t = (text ?? string.Empty).Trim().TrimStart('#');
        if (t.Length == 3) t = string.Concat(t[0], t[0], t[1], t[1], t[2], t[2]);
        return t.Length == 6 && TryParse("#" + t, out color);
    }

    // ---- HSV, the space a color square and hue slider work in ----------------------------------------

    /// <summary>Hue in degrees (0 to 360), saturation and value (0 to 1).</summary>
    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0
            : max == r ? 60 * (((g - b) / d) % 6)
            : max == g ? 60 * ((b - r) / d + 2)
            : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    public static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        static byte B(double n) => (byte)Math.Clamp(Math.Round(n * 255), 0, 255);
        return Color.FromRgb(B(r + m), B(g + m), B(b + m));
    }

    // ---- sRGB <-> OKLab (Björn Ottosson's matrices) ------------------------------------------

    private static double ToLinear(byte v)
    {
        double s = v / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    private static double FromLinear(double l) =>
        l <= 0.0031308 ? 12.92 * l : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;

    public static (double L, double A, double B) ToOklab(Color c)
    {
        double r = ToLinear(c.R), g = ToLinear(c.G), b = ToLinear(c.B);
        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    /// <summary>The sRGB color for an OKLab triple, or null when it falls outside the sRGB gamut.</summary>
    private static Color? FromOklabInGamut(double L, double a, double b, byte alpha)
    {
        double l = L + 0.3963377774 * a + 0.2158037573 * b;
        double m = L - 0.1055613458 * a - 0.0638541728 * b;
        double s = L - 0.0894841775 * a - 1.2914855480 * b;
        l = l * l * l; m = m * m * m; s = s * s * s;

        double r = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
        double g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
        double bl = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;

        const double eps = 1e-4;
        if (r < -eps || r > 1 + eps || g < -eps || g > 1 + eps || bl < -eps || bl > 1 + eps) return null;

        static byte B(double lin) => (byte)Math.Clamp(Math.Round(FromLinear(Math.Clamp(lin, 0, 1)) * 255), 0, 255);
        return Color.FromArgb(alpha, B(r), B(g), B(bl));
    }

    public static Oklch ToOklch(Color c)
    {
        var (L, a, b) = ToOklab(c);
        double chroma = Math.Sqrt(a * a + b * b);
        double hue = chroma < 1e-6 ? 0 : Math.Atan2(b, a) * 180 / Math.PI;
        return new Oklch(L, chroma, hue < 0 ? hue + 360 : hue, c.A / 255.0);
    }

    /// <summary>
    /// The sRGB color for these values. A color outside the gamut keeps its lightness and hue and gives up
    /// chroma until it fits, so a bright accent never turns into a different color to fit.
    /// </summary>
    public static Color FromOklch(Oklch v)
    {
        byte alpha = (byte)Math.Clamp(Math.Round(v.Alpha * 255), 0, 255);
        double L = Math.Clamp(v.L, 0, 1);
        double rad = v.H * Math.PI / 180;

        double lo = 0, hi = Math.Max(v.C, 0);
        var direct = FromOklabInGamut(L, hi * Math.Cos(rad), hi * Math.Sin(rad), alpha);
        if (direct is { } ok) return ok;

        Color best = FromOklabInGamut(L, 0, 0, alpha) ?? Color.FromArgb(alpha, 0, 0, 0);
        for (int i = 0; i < 24; i++)
        {
            double mid = (lo + hi) / 2;
            if (FromOklabInGamut(L, mid * Math.Cos(rad), mid * Math.Sin(rad), alpha) is { } fit) { best = fit; lo = mid; }
            else hi = mid;
        }
        return best;
    }

    public static Color FromOklab(double L, double a, double b, byte alpha)
    {
        double chroma = Math.Sqrt(a * a + b * b);
        double hue = chroma < 1e-9 ? 0 : Math.Atan2(b, a) * 180 / Math.PI;
        return FromOklch(new Oklch(L, chroma, hue < 0 ? hue + 360 : hue, alpha / 255.0));
    }

    /// <summary>The color <paramref name="amount"/> (0 to 1) of the way from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static Color Mix(Color from, Color to, double amount)
    {
        static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
        return Color.FromArgb(0xFF, Lerp(from.R, to.R, amount), Lerp(from.G, to.G, amount), Lerp(from.B, to.B, amount));
    }

    /// <summary>Relative luminance, 0 (black) to 1 (white).</summary>
    public static double Luminance(Color c) => 0.2126 * ToLinear(c.R) + 0.7152 * ToLinear(c.G) + 0.0722 * ToLinear(c.B);

    /// <summary>WCAG contrast ratio of two opaque colors, 1 to 21.</summary>
    public static double Contrast(Color x, Color y)
    {
        double a = Luminance(x), b = Luminance(y);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
}
