using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TrayTrigger.Views;

/// <summary>
/// A row of related controls (a tab strip, the view buttons, the sidebar, a row of links) as one Tab stop, the way
/// File Explorer and Windows Settings group them: Tab goes into the group and on past it in one press, and the arrow
/// keys move inside it (WPF's own directional navigation, so a controller's D-pad moves through it too).
/// <para>
/// Tabbing in lands on the item marked <see cref="IsCurrentProperty"/> (the selected tab, the page you're on), or else
/// on the one used last, as WPF's TabNavigation="Once" remembers it. A click goes where it was aimed: only keyboard
/// focus coming from outside the group, with no mouse button down, is moved.
/// </para>
/// </summary>
public static class FocusGroup
{
    public static readonly DependencyProperty IsGroupProperty = DependencyProperty.RegisterAttached(
        "IsGroup", typeof(bool), typeof(FocusGroup), new PropertyMetadata(false, OnIsGroupChanged));

    public static bool GetIsGroup(DependencyObject d) => (bool)d.GetValue(IsGroupProperty);
    public static void SetIsGroup(DependencyObject d, bool value) => d.SetValue(IsGroupProperty, value);

    /// <summary>The item Tab lands on when coming into the group: the selected tab, the current page.</summary>
    public static readonly DependencyProperty IsCurrentProperty = DependencyProperty.RegisterAttached(
        "IsCurrent", typeof(bool), typeof(FocusGroup), new PropertyMetadata(false));

    public static bool GetIsCurrent(DependencyObject d) => (bool)d.GetValue(IsCurrentProperty);
    public static void SetIsCurrent(DependencyObject d, bool value) => d.SetValue(IsCurrentProperty, value);

    private static void OnIsGroupChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        KeyboardNavigation.SetTabNavigation(element, (bool)e.NewValue ? KeyboardNavigationMode.Once : KeyboardNavigationMode.Continue);
        element.GotKeyboardFocus -= OnGotKeyboardFocus;
        if ((bool)e.NewValue) element.GotKeyboardFocus += OnGotKeyboardFocus;
    }

    private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var group = (UIElement)sender;
        if (e.OldFocus is DependencyObject old && IsWithin(old, group)) return;
        if (Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed) return;
        if (FindCurrent(group) is { } current && !ReferenceEquals(current, e.NewFocus)) current.Focus();
    }

    private static UIElement? FindCurrent(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } candidate && GetIsCurrent(child)) return candidate;
            if (FindCurrent(child) is { } found) return found;
        }
        return null;
    }

    private static bool IsWithin(DependencyObject element, DependencyObject group)
    {
        for (var d = element; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (ReferenceEquals(d, group)) return true;
        return false;
    }
}
