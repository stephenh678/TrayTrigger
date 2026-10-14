using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TrayTrigger.Views;

namespace TrayTrigger.Tests;

public class ComboBoxWheelGuardTests
{
    private static void Sta(Action body) => WpfTestHost.Run(body);

    private static (ScrollViewer Scroller, ComboBox Box, Func<int> WheelsSeen) Page()
    {
        ComboBoxWheelGuard.Register();
        var box = new ComboBox { ItemsSource = new[] { "a", "b", "c" }, SelectedIndex = 0 };
        var scroller = new ScrollViewer { Content = new StackPanel { Children = { box } } };
        int seen = 0;
        scroller.AddHandler(UIElement.MouseWheelEvent, new MouseWheelEventHandler((_, _) => seen++), handledEventsToo: true);
        return (scroller, box, () => seen);
    }

    private static void Wheel(ComboBox box) =>
        box.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent, Source = box });

    [Fact]
    public void AClosedDropDown_PassesTheWheelOn_AndKeepsItsValue() => Sta(() =>
    {
        var (_, box, seen) = Page();
        Wheel(box);
        Assert.Equal(0, box.SelectedIndex);
        Assert.Equal(1, seen());
    });

    [Fact]
    public void AnOpenDropDown_KeepsTheWheelForItsList() => Sta(() =>
    {
        var (scroller, box, seen) = Page();
        // A drop-down only opens once it is loaded, so the page needs a real window.
        var window = new Window { Content = scroller, Width = 300, Height = 200, ShowInTaskbar = false };
        try
        {
            window.Show();
            box.IsDropDownOpen = true;
            Assert.True(box.IsDropDownOpen, "The drop-down did not open, so the test proves nothing.");
            Wheel(box);
            Assert.Equal(0, seen());
        }
        finally { window.Close(); }
    });
}
