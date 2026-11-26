using System;
using System.Windows;
using System.Windows.Controls;
using TrayTrigger.Services;

namespace TrayTrigger.Views;

/// <summary>
/// The Library's tab row, in one layout pass: three children, the category strip (left), the divider and the
/// collection strip (right). Each strip gets the width its tabs need; when both can't fit they share the row at
/// <see cref="Split"/> (see <see cref="LibraryTabRowLayout"/>). A collapsed collection strip hides the divider with it
/// and leaves the row to the categories.
/// </summary>
public sealed class TabRowPanel : Panel
{
    public static readonly DependencyProperty SplitProperty = DependencyProperty.Register(
        nameof(Split), typeof(double), typeof(TabRowPanel),
        new FrameworkPropertyMetadata(LibraryTabRowLayout.DefaultSplit, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The categories' share of the row when both strips can't fit.</summary>
    public double Split
    {
        get => (double)GetValue(SplitProperty);
        set => SetValue(SplitProperty, value);
    }

    public TabRowPanel()
    {
        // A strip is measured at the width it is given, so it doesn't tell this panel when its tabs grow or shrink:
        // the first collection, made while the strip was still empty, stayed a sliver until the divider was dragged.
        // Its scroll extent does change, so a change there lays the row out again.
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (e.ExtentWidthChange != 0) InvalidateMeasure();
        }));
    }

    private UIElement? Categories => InternalChildren.Count > 0 ? InternalChildren[0] : null;
    private UIElement? Divider => InternalChildren.Count > 1 ? InternalChildren[1] : null;
    private UIElement? Collections => InternalChildren.Count > 2 ? InternalChildren[2] : null;
    private bool CollectionsShown => Collections is { Visibility: Visibility.Visible };

    private double _categoriesWidth, _collectionsWidth, _dividerWidth;

    /// <summary>The width the strips share: the row less the divider, when collections show.</summary>
    public double Available => Math.Max(0, ActualWidth - (CollectionsShown ? _dividerWidth : 0));

    /// <summary>The category strip's width in the last layout, for the divider's drag.</summary>
    public double CategoriesWidth => _categoriesWidth;

    /// <summary>The two strips can't both fit, so they share the row at <see cref="Split"/>; the divider's drag only applies then.</summary>
    public bool IsSharing { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var infinite = new Size(double.PositiveInfinity, availableSize.Height);
        double height = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(infinite);
            height = Math.Max(height, child.DesiredSize.Height);
        }

        bool shown = CollectionsShown;
        _dividerWidth = shown ? Divider?.DesiredSize.Width ?? 0 : 0;
        double categoriesNeed = Categories?.DesiredSize.Width ?? 0;
        double collectionsNeed = shown ? Collections!.DesiredSize.Width : 0;
        double width = double.IsInfinity(availableSize.Width) ? categoriesNeed + _dividerWidth + collectionsNeed : availableSize.Width;

        IsSharing = LibraryTabRowLayout.Shares(width - _dividerWidth, categoriesNeed, collectionsNeed);
        (_categoriesWidth, _collectionsWidth) = LibraryTabRowLayout.Widths(width - _dividerWidth, categoriesNeed, collectionsNeed, Split);
        // Not rounded down: a strip a fraction narrower than its tabs would show its arrows and cut the last tab.
        _categoriesWidth = Math.Ceiling(_categoriesWidth);
        _collectionsWidth = Math.Ceiling(_collectionsWidth);

        // Measured again at the width each gets, so a strip that is cut knows it and shows its arrows.
        Categories?.Measure(new Size(_categoriesWidth, availableSize.Height));
        if (shown) Collections!.Measure(new Size(_collectionsWidth, availableSize.Height));
        return new Size(double.IsInfinity(availableSize.Width) ? width : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double h = finalSize.Height;
        Categories?.Arrange(new Rect(0, 0, _categoriesWidth, h));
        if (CollectionsShown)
        {
            double left = finalSize.Width - _collectionsWidth;
            Collections!.Arrange(new Rect(left, 0, _collectionsWidth, h));
            Divider?.Arrange(new Rect(left - _dividerWidth, 0, _dividerWidth, h));
        }
        else
        {
            Collections?.Arrange(new Rect(0, 0, 0, 0));
            Divider?.Arrange(new Rect(0, 0, 0, 0));
        }
        return finalSize;
    }
}
