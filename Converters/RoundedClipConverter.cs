using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TrayTrigger.Converters;

/// <summary>
/// A border's rounded-corner clip, worked out from its own size and radius so a theme's corner radii reach the
/// art inside it: values are the border's ActualWidth, ActualHeight, CornerRadius and BorderThickness. The clip
/// is the border's inside edge, the radius less the border's thickness at each corner, which is how the art
/// meets the outline exactly at any radius. A border without a line (thickness 0) clips to its own radius.
/// </summary>
public sealed class RoundedClipConverter : IMultiValueConverter
{
    /// <summary>
    /// The corners of a shape of this size: each radius less the line's thickness, and radii that would overlap along a side
    /// scaled down together, as CSS does. A Border cannot do the second part (a radius over half its width distorts it), so
    /// whatever has to follow the same shape as a clip (a card's outline around a poster) takes its radii from here.
    /// </summary>
    public static CornerRadius Scaled(double width, double height, CornerRadius radius, double thickness)
    {
        double w = Math.Max(0, width - 2 * thickness), h = Math.Max(0, height - 2 * thickness);
        double tl = Math.Max(0, radius.TopLeft - thickness), tr = Math.Max(0, radius.TopRight - thickness);
        double br = Math.Max(0, radius.BottomRight - thickness), bl = Math.Max(0, radius.BottomLeft - thickness);

        double f = 1;
        if (tl + tr > w && tl + tr > 0) f = Math.Min(f, w / (tl + tr));
        if (bl + br > w && bl + br > 0) f = Math.Min(f, w / (bl + br));
        if (tl + bl > h && tl + bl > 0) f = Math.Min(f, h / (tl + bl));
        if (tr + br > h && tr + br > 0) f = Math.Min(f, h / (tr + br));
        return new CornerRadius(tl * f, tr * f, br * f, bl * f);
    }

    public static Geometry Build(double width, double height, CornerRadius radius, double thickness)
    {
        double w = Math.Max(0, width - 2 * thickness), h = Math.Max(0, height - 2 * thickness);
        var scaled = Scaled(width, height, radius, thickness);
        double tl = scaled.TopLeft, tr = scaled.TopRight, br = scaled.BottomRight, bl = scaled.BottomLeft;

        var geometry = new StreamGeometry();
        using (var c = geometry.Open())
        {
            c.BeginFigure(new Point(tl, 0), true, true);
            c.LineTo(new Point(w - tr, 0), true, false);
            if (tr > 0) c.ArcTo(new Point(w, tr), new Size(tr, tr), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(w, h - br), true, false);
            if (br > 0) c.ArcTo(new Point(w - br, h), new Size(br, br), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(bl, h), true, false);
            if (bl > 0) c.ArcTo(new Point(0, h - bl), new Size(bl, bl), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(0, tl), true, false);
            if (tl > 0) c.ArcTo(new Point(tl, 0), new Size(tl, tl), 0, false, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 4 || values[0] is not double w || values[1] is not double h
            || values[2] is not CornerRadius radius || values[3] is not Thickness border
            || double.IsNaN(w) || double.IsNaN(h) || w <= 0 || h <= 0)
            return DependencyProperty.UnsetValue;
        return Build(w, h, radius, border.Left);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
