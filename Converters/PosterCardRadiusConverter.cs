using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TrayTrigger.Converters;

/// <summary>
/// The corners of a game card that is showing its poster: the poster's own corners, scaled to the card as the art's clip scales
/// them, plus the card's outline, so the card's outline and hover ring follow the art exactly for any shape and any size.
/// Values are the card's ActualWidth and ActualHeight, the poster's CornerRadius and (when given) the card's BorderThickness,
/// which is 2 px rather than 1 while the card is focused or selected.
/// </summary>
public sealed class PosterCardRadiusConverter : IMultiValueConverter
{
    /// <summary>The card's outline when nothing says otherwise: the width of the line a corner has to go round.</summary>
    private const double DefaultOutline = 1;

    public static CornerRadius For(double width, double height, CornerRadius poster, double outline = DefaultOutline)
    {
        // The art sits inside the outline: its clip is worked out on the card less the line on every side.
        var art = RoundedClipConverter.Scaled(width - 2 * outline, height - 2 * outline, poster, 0);
        double Outer(double r) => r > 0 ? r + outline : 0;
        return new CornerRadius(Outer(art.TopLeft), Outer(art.TopRight), Outer(art.BottomRight), Outer(art.BottomLeft));
    }

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 3 || values[0] is not double w || values[1] is not double h || values[2] is not CornerRadius poster
            || double.IsNaN(w) || double.IsNaN(h) || w <= 0 || h <= 0)
            return DependencyProperty.UnsetValue;
        double outline = values.Length > 3 && values[3] is Thickness line ? line.Left : DefaultOutline;
        return For(w, h, poster, outline);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
