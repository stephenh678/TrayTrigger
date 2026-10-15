using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TrayTrigger.Converters;

/// <summary>
/// The clip for a shade laid over a poster (the dark gradient behind the badges at the top, or behind the title at the bottom): the
/// poster's own shape, so the shade is cut to it and never paints in a corner the art does not fill, whatever shape the theme gives
/// the poster. Values are the poster area's ActualWidth and ActualHeight, the poster's CornerRadius, and the shade's own
/// ActualHeight. The parameter "bottom" says the shade sits at the poster's bottom edge, so the shape is moved up to line up with it.
/// </summary>
public sealed class PosterShadeClipConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 4 || values[0] is not double w || values[1] is not double h || values[2] is not CornerRadius radius || values[3] is not double shade
            || double.IsNaN(w) || double.IsNaN(h) || double.IsNaN(shade) || w <= 0 || h <= 0 || shade < 0)
            return DependencyProperty.UnsetValue;

        var shape = RoundedClipConverter.Build(w, h, radius, 0);
        if (!string.Equals(parameter as string, "bottom", StringComparison.Ordinal) || h - shade == 0) return shape;

        var moved = shape.Clone();
        moved.Transform = new TranslateTransform(0, -(h - shade));
        moved.Freeze();
        return moved;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
