using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace TrayTrigger.Views;

/// <summary>
/// Sends a sub-tabbed page (Settings, System, About) back to the top when its tab changes.
/// <c>ResetOn</c> goes on the page's ScrollViewer, bound to the selected tab, so programmatic jumps
/// (About › Open Diagnostics &amp; Storage) land at the top too. <c>Target</c> goes on the tab strip
/// so clicking the tab you are already on also scrolls up - a value change alone can't catch that.
/// </summary>
public static class ScrollToTop
{
    public static readonly DependencyProperty ResetOnProperty = DependencyProperty.RegisterAttached(
        "ResetOn", typeof(object), typeof(ScrollToTop),
        new PropertyMetadata(null, (d, _) => (d as ScrollViewer)?.ScrollToTop()));

    public static object? GetResetOn(DependencyObject d) => d.GetValue(ResetOnProperty);
    public static void SetResetOn(DependencyObject d, object? value) => d.SetValue(ResetOnProperty, value);

    public static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached(
        "Target", typeof(ScrollViewer), typeof(ScrollToTop), new PropertyMetadata(null, OnTargetChanged));

    public static ScrollViewer? GetTarget(DependencyObject d) => (ScrollViewer?)d.GetValue(TargetProperty);
    public static void SetTarget(DependencyObject d, ScrollViewer? value) => d.SetValue(TargetProperty, value);

    private static readonly RoutedEventHandler TabClicked =
        (sender, _) => GetTarget((DependencyObject)sender)?.ScrollToTop();

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement strip) return;
        strip.RemoveHandler(ButtonBase.ClickEvent, TabClicked);
        if (e.NewValue != null) strip.AddHandler(ButtonBase.ClickEvent, TabClicked);
    }
}
