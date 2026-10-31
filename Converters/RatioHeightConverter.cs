using System;
using System.Globalization;
using System.Windows.Data;

namespace TrayTrigger.Converters;

/// <summary>
/// A height that follows a width: width times a ratio, kept between a floor and a ceiling. The parameter is "ratio,min,max",
/// for a picture that should keep its shape as the box it sits in grows (Edit Game's Banner preview).
/// </summary>
public sealed class RatioHeightConverter : IValueConverter
{
    public static double For(double width, string parameter)
    {
        string[] parts = parameter.Split(',');
        double ratio = double.Parse(parts[0], CultureInfo.InvariantCulture);
        double min = double.Parse(parts[1], CultureInfo.InvariantCulture);
        double max = double.Parse(parts[2], CultureInfo.InvariantCulture);
        return double.IsNaN(width) || width <= 0 ? min : Math.Clamp(width * ratio, min, max);
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        For(value is double width ? width : double.NaN, parameter as string ?? "0.3229,118,260");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
