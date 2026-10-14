using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TrayTrigger.Views;

/// <summary>
/// A closed drop-down must not take the mouse wheel. Scrolling a page with the wheel and passing
/// over a drop-down that has focus changed its value, silently, instead of scrolling on. A closed
/// drop-down now hands the wheel to the scrolling area around it; an open one keeps it for its own
/// list. Registered once for every ComboBox in the app.
/// </summary>
internal static class ComboBoxWheelGuard
{
    private static bool _registered;

    internal static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(ComboBox), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel));
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ComboBox { IsDropDownOpen: false } box) return;
        e.Handled = true;
        if (VisualTreeHelper.GetParent(box) is not UIElement parent) return;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = box });
    }
}
