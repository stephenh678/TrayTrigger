using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace TrayTrigger.Views;

/// <summary>
/// The Hardware Specs tab's layout: its cards two to a row, each pair as tall as the taller of the two, and the
/// children marked <see cref="IsWideProperty"/> (the heading, the live tiles, Storage, the tools row) across the whole
/// width. Below <see cref="TwoColumnWidth"/> (a narrow window, or large text) everything takes one column. The cards
/// stay direct children of this panel, so the page's card search (Views/CardSearch) still sees each one.
/// </summary>
public sealed class SpecGridPanel : Panel
{
    /// <summary>Narrower than this, the cards are one to a row: two would wrap every line.</summary>
    public const double TwoColumnWidth = 760;

    /// <summary>The room between the two cards of a row.</summary>
    public const double ColumnGap = 10;

    public static readonly DependencyProperty IsWideProperty = DependencyProperty.RegisterAttached(
        "IsWide", typeof(bool), typeof(SpecGridPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static bool GetIsWide(DependencyObject d) => (bool)d.GetValue(IsWideProperty);
    public static void SetIsWide(DependencyObject d, bool value) => d.SetValue(IsWideProperty, value);

    private readonly List<(UIElement[] Items, double Height)> _rows = [];
    private bool _twoColumns;

    /// <summary>The children in rows: a wide one alone, the others in pairs (the last one alone if odd). Collapsed ones take no place.</summary>
    internal static List<UIElement[]> Rows(IReadOnlyList<UIElement> children, Func<UIElement, bool> isWide, bool twoColumns)
    {
        var rows = new List<UIElement[]>();
        UIElement? pending = null;
        foreach (var child in children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            if (!twoColumns || isWide(child))
            {
                if (pending != null) { rows.Add([pending]); pending = null; }
                rows.Add([child]);
            }
            else if (pending == null) pending = child;
            else { rows.Add([pending, child]); pending = null; }
        }
        if (pending != null) rows.Add([pending]);
        return rows;
    }

    private static double ColumnWidth(double width) => Math.Max(0, (width - ColumnGap) / 2);

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? TwoColumnWidth : availableSize.Width;
        _twoColumns = width >= TwoColumnWidth;
        var children = new List<UIElement>(InternalChildren.Count);
        foreach (UIElement child in InternalChildren) children.Add(child);

        _rows.Clear();
        double total = 0;
        foreach (var row in Rows(children, GetIsWide, _twoColumns))
        {
            double itemWidth = row.Length == 1 && (!_twoColumns || GetIsWide(row[0])) ? width : ColumnWidth(width);
            double height = 0;
            foreach (var item in row)
            {
                item.Measure(new Size(itemWidth, double.PositiveInfinity));
                height = Math.Max(height, item.DesiredSize.Height);
            }
            _rows.Add((row, height));
            total += height;
        }
        // A collapsed child is measured too, at a real width: when it shows, WPF measures it again at that size, and
        // its new height is what tells this panel to lay the page out again. (At zero width it would stay at zero.)
        foreach (var child in children)
            if (child.Visibility == Visibility.Collapsed) child.Measure(new Size(width, double.PositiveInfinity));

        return new Size(double.IsInfinity(availableSize.Width) ? width : availableSize.Width, total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        foreach (var (items, height) in _rows)
        {
            if (items.Length == 2)
            {
                double column = ColumnWidth(finalSize.Width);
                items[0].Arrange(new Rect(0, y, column, height));
                items[1].Arrange(new Rect(column + ColumnGap, y, column, height));
            }
            else
            {
                bool full = !_twoColumns || GetIsWide(items[0]);
                items[0].Arrange(new Rect(0, y, full ? finalSize.Width : ColumnWidth(finalSize.Width), height));
            }
            y += height;
        }
        foreach (UIElement child in InternalChildren)
            if (child.Visibility == Visibility.Collapsed) child.Arrange(new Rect(0, 0, 0, 0));
        return finalSize;
    }
}
