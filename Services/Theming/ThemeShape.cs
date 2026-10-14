using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows;

namespace TrayTrigger.Services.Theming;

/// <summary>
/// The corner radii a theme can set, and how the whole set is worked out from them. A theme names only the radii it
/// changes, as it does colors: <c>"shape": { "RadiusCard": 20, "RadiusControl": [0, 12, 0, 12] }</c>, one number for all
/// four corners or four (top-left, top-right, bottom-right, bottom-left). The rest come from Shapes.xaml, and the
/// radii that follow a card (the poster art and the card's inner edges) follow the theme's card unless it sets them.
/// </summary>
internal static class ThemeShape
{
    /// <summary>The radii a theme may set, with the largest value each takes. RadiusLarge (circles) and the inner edges are not set by a theme.</summary>
    public static readonly IReadOnlyDictionary<string, double> Settable = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["RadiusSmall"] = 16,
        ["RadiusControl"] = 48,
        ["RadiusCard"] = 64,
        ["RadiusPoster"] = 96,
    };

    /// <summary>Reads the <c>shape</c> block of a theme file; what cannot be used is skipped with a warning.</summary>
    public static Dictionary<string, CornerRadius> Read(JsonElement block, List<string> warnings)
    {
        var shape = new Dictionary<string, CornerRadius>(StringComparer.Ordinal);
        foreach (var prop in block.EnumerateObject())
        {
            string key = prop.Name.StartsWith("Radius", StringComparison.Ordinal) ? prop.Name : "Radius" + prop.Name;
            if (!Settable.TryGetValue(key, out double max))
            {
                warnings.Add($"shape.{prop.Name} is not a radius a theme can set ({string.Join(", ", Settable.Keys)}); skipped.");
                continue;
            }

            double[]? numbers = Numbers(prop.Value);
            if (numbers == null)
            {
                warnings.Add($"shape.{prop.Name} must be a number or four numbers (top-left, top-right, bottom-right, bottom-left); skipped.");
                continue;
            }

            for (int i = 0; i < numbers.Length; i++) numbers[i] = Math.Clamp(numbers[i], 0, max);
            shape[key] = numbers.Length == 1
                ? new CornerRadius(numbers[0])
                : new CornerRadius(numbers[0], numbers[1], numbers[2], numbers[3]);
        }
        return shape;
    }

    private static double[]? Numbers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetDouble(out double one) && double.IsFinite(one) ? new[] { one } : null;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 4) return null;
        var four = new double[4];
        int i = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out four[i]) || !double.IsFinite(four[i])) return null;
            i++;
        }
        return four;
    }

    /// <summary>A copy of Shapes.xaml with the theme's radii in place of the file's own, and the radii that follow the card worked out from it.</summary>
    internal static ResourceDictionary Build(IReadOnlyDictionary<string, CornerRadius>? shape)
    {
        var dictionary = AppResources.LoadFresh("Shapes.xaml");
        Fill(dictionary, shape);
        return dictionary;
    }

    /// <summary>
    /// Puts the radii a theme sets into <paramref name="dictionary"/> and works out the ones that follow the card from it.
    /// A dictionary that is filled again (the editor's preview) is given all of Small, Control and Card, so nothing of an
    /// earlier fill is left over.
    /// </summary>
    internal static void Fill(ResourceDictionary dictionary, IReadOnlyDictionary<string, CornerRadius>? shape)
    {
        if (shape != null)
            foreach (var (name, radius) in shape) dictionary[name] = radius;

        var card = (CornerRadius)dictionary["RadiusCard"];
        var inner = Inset(card, 1);
        if (shape == null || !shape.ContainsKey("RadiusPoster")) dictionary["RadiusPoster"] = inner;

        // The inner edge of a card with a 1 px border: its radius less the border, corner by corner, so the fill meets the outline.
        dictionary["RadiusInnerTop"] = new CornerRadius(inner.TopLeft, inner.TopRight, 0, 0);
        dictionary["RadiusInnerBottom"] = new CornerRadius(0, 0, inner.BottomRight, inner.BottomLeft);
    }

    /// <summary>A radius less a line's thickness at every corner, never below 0 (a CornerRadius cannot be negative).</summary>
    internal static CornerRadius Inset(CornerRadius radius, double thickness) => new(
        Math.Max(0, radius.TopLeft - thickness), Math.Max(0, radius.TopRight - thickness),
        Math.Max(0, radius.BottomRight - thickness), Math.Max(0, radius.BottomLeft - thickness));
}
