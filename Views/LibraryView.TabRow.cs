using System;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TrayTrigger.Services;

namespace TrayTrigger.Views;

/// <summary>
/// The divider between the category and collection strips (<see cref="TabRowPanel"/> lays the row out): dragging it
/// moves the split, which is saved when the drag ends; double-clicking puts it back to the default. While both strips
/// fit, the divider sits beside the collections and the split decides nothing, so a drag then changes nothing either:
/// it would otherwise save a split measured from the categories' edge, far from where the divider is.
/// </summary>
public partial class LibraryView
{
    private void InitializeTabRow()
    {
        Loaded += (_, _) => TabRow.Split = ViewModel.Library.TabSplit;
    }

    private void TabDivider_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double available = TabRow.Available;
        if (!TabRow.IsSharing || available <= 0) return;
        TabRow.Split = LibraryTabRowLayout.SplitFor(TabRow.CategoriesWidth + e.HorizontalChange, available);
    }

    private void TabDivider_DragCompleted(object sender, DragCompletedEventArgs e) => SaveTabSplit();

    private void TabDivider_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        TabRow.Split = LibraryTabRowLayout.DefaultSplit;
        SaveTabSplit();
        e.Handled = true;
    }

    /// <summary>Saves the split when it changed; a plain click on the divider writes nothing.</summary>
    private void SaveTabSplit()
    {
        if (Math.Abs(TabRow.Split - ViewModel.Library.TabSplit) > 1e-9) ViewModel.Library.TabSplit = TabRow.Split;
    }
}
