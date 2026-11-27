using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

/// <summary>
/// The Details List's column handles, as in File Explorer: dragging one moves that column's right edge with the mouse,
/// and the columns after it with it (DetailsListColumns), saved once when the drag ends. A column grows into the spare
/// room before Actions, then takes width from the columns after it; never from one before it. Double-clicking a
/// handle puts that column back to its default, and Reset Column Widths on the header's right-click menu puts back
/// all of them.
/// </summary>
public partial class LibraryView
{
    private const int TitleColumn = 1;
    private const int ProfileColumn = 3;
    private const int SpareColumn = 7;

    private void ColumnGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb { Tag: string column }) return;
        var columns = DetailsHeaderGrid.ColumnDefinitions;
        bool profileShown = columns[ProfileColumn].ActualWidth > 0;
        DetailsListColumns.Instance.Resize(column, e.HorizontalChange, columns[TitleColumn].ActualWidth, columns[SpareColumn].ActualWidth,
            c => c != DetailsListColumns.Profile || profileShown);
    }

    private void ColumnGrip_DragCompleted(object sender, DragCompletedEventArgs e) => DetailsListColumns.Instance.Save();

    private void ColumnGrip_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Thumb { Tag: string column }) DetailsListColumns.Instance.Reset(column);
        e.Handled = true;
    }

    private void ResetColumnWidths_Click(object sender, RoutedEventArgs e) => DetailsListColumns.Instance.Reset();
}
