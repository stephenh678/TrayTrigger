using System;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace TrayTrigger.Converters;

/// <summary>
/// True when any of the bound values is a boolean true. WPF's MultiDataTrigger can only AND its
/// conditions, so a trigger that has to fire on "hovered OR focused OR its menu is open" binds
/// one MultiBinding through this and matches a single True - which also lets the trigger animate
/// on the way in and back out on the way out, as one state rather than several overlapping ones.
/// </summary>
public sealed class AnyTrueConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        => values.Any(v => v is bool b && b);

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// values[1] when values[0] is a boolean true, otherwise values[2]. Used to switch a value between
/// an animated source and an instant one as Windows' Animation effects go on and off (see
/// Views/Motion.cs): switching which value is read takes effect at once, where starting or
/// removing storyboards from a template's triggers would leave one holding its last value.
/// </summary>
public sealed class PickByFlagConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        => values.Length < 3 ? System.Windows.DependencyProperty.UnsetValue
            : values[0] is bool flag && flag ? values[1] : values[2];

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
