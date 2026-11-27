using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

/// <summary>
/// The Details List's column handles: dragging one resizes its column in the header and every row
/// (DetailsListColumns), saved once when the drag ends; double-clicking puts that column back to its default, and
/// Reset column widths on the header's right-click menu puts back all of them.
/// </summary>
public partial class LibraryView
{
    private void ColumnGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb { Tag: string column }) return;
        double titleWidth = DetailsHeaderGrid.ColumnDefinitions[1].ActualWidth;
        DetailsListColumns.Instance.Resize(column, e.HorizontalChange, titleWidth);
    }

    private void ColumnGrip_DragCompleted(object sender, DragCompletedEventArgs e) => DetailsListColumns.Instance.Save();

    private void ColumnGrip_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Thumb { Tag: string column }) DetailsListColumns.Instance.Reset(column);
        e.Handled = true;
    }

    private void ResetColumnWidths_Click(object sender, RoutedEventArgs e) => DetailsListColumns.Instance.Reset();
}
