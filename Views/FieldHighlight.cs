using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TrayTrigger.Services;

namespace TrayTrigger.Views;

/// <summary>
/// Brings one setting in a dialog on screen and outlines it, as the settings search does for a card. A link
/// from another page that points at a setting (About's list of game and tool hotkeys) uses it, so it lands
/// on the setting and not only on the dialog. The outline goes at the next click or key press.
/// </summary>
internal static class FieldHighlight
{
    /// <summary>The handler that will end the outline in each window, so a second link replaces the first instead of leaving two outlines and two handlers.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, Action> Ends = new();

    public static void Show(FrameworkElement row)
    {
        row.Dispatcher.BeginInvoke(() =>
        {
            row.UpdateLayout();
            ScrollToUpperPart(row);
            row.UpdateLayout();

            if (Window.GetWindow(row) is not { } window)
            {
                CardSearch.Outline(row, row);
                return;
            }
            // One outline per window: the window holds it, so the next link's replaces it.
            if (Ends.TryGetValue(window, out var previous)) previous();
            CardSearch.Outline(window, row);

            void End(object? sender, InputEventArgs e) => Finish();
            void Finish()
            {
                window.PreviewMouseDown -= End;
                window.PreviewKeyDown -= End;
                CardSearch.ClearOutline(window);
                Ends.Remove(window);
            }
            window.PreviewMouseDown += End;
            window.PreviewKeyDown += End;
            Ends.Add(window, Finish);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Scrolls the row to about a sixth of the way down the scroll area it sits in, as the settings search does, so it isn't left at the bottom edge.</summary>
    private static void ScrollToUpperPart(FrameworkElement row)
    {
        for (DependencyObject? up = VisualTreeHelper.GetParent(row); up != null; up = VisualTreeHelper.GetParent(up))
        {
            if (up is not ScrollViewer viewer) continue;
            if (viewer.ViewportHeight <= 0 || !row.IsVisible)
            {
                row.BringIntoView();
                return;
            }
            double y = row.TranslatePoint(new Point(0, 0), viewer).Y;
            viewer.ScrollToVerticalOffset(System.Math.Max(0, viewer.VerticalOffset + y - viewer.ViewportHeight * 0.15));
            viewer.UpdateLayout();
            return;
        }
        row.BringIntoView();
    }
}
