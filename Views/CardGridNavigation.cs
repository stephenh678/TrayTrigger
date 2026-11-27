using System;
using System.Windows.Input;

namespace TrayTrigger.Views;

/// <summary>Where a key moves keyboard focus in a grid of cards laid out row by row, as File Explorer's icon views do.</summary>
public static class CardGridNavigation
{
    /// <summary>The index focus moves to from <paramref name="index"/>; the same index when the key goes nowhere.</summary>
    public static int Target(Key key, int index, int count, int perRow, int rowsPerPage)
    {
        if (count <= 0) return index;
        perRow = Math.Max(1, perRow);
        int last = count - 1;
        int row = index / perRow, lastRow = last / perRow;
        return key switch
        {
            Key.Left => Math.Max(0, index - 1),
            Key.Right => Math.Min(last, index + 1),
            Key.Up => index >= perRow ? index - perRow : index,
            // The row below may be shorter: go to its last card rather than nowhere.
            Key.Down => row < lastRow ? Math.Min(last, index + perRow) : index,
            Key.Home => 0,
            Key.End => last,
            Key.PageUp => Math.Max(index % perRow, index - perRow * Math.Max(1, rowsPerPage)),
            Key.PageDown => row < lastRow ? Math.Min(last, index + perRow * Math.Max(1, rowsPerPage)) : index,
            _ => index,
        };
    }
}
