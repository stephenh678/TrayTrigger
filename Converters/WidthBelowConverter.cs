using System;
using System.Globalization;
using System.Windows.Data;

namespace TrayTrigger.Converters;

/// <summary>
/// True while a width is below the parameter (a number of pixels), for a part that should give way when its view
/// gets narrow: Details List's Profile column drops out before the game titles are squeezed to nothing.
/// A width not measured yet counts as wide.
/// </summary>
public sealed class WidthBelowConverter : IValueConverter
{
    public static bool IsBelow(double width, string? limit) =>
        width > 0 && double.TryParse(limit, NumberStyles.Float, CultureInfo.InvariantCulture, out double max) && width < max;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        IsBelow(value is double width ? width : 0, parameter as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
