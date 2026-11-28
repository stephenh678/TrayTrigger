using System.Windows.Input;
using TrayTrigger.Views;

namespace TrayTrigger.Tests;

/// <summary>
/// Arrow keys in the card views, File Explorer style. The grid used: 11 cards, 4 per row.
///   0  1  2  3
///   4  5  6  7
///   8  9 10
/// </summary>
public class CardGridNavigationTests
{
    private static int Move(Key key, int from, int rowsPerPage = 1) => CardGridNavigation.Target(key, from, 11, 4, rowsPerPage);

    [Theory]
    [InlineData(Key.Right, 0, 1)]
    [InlineData(Key.Right, 3, 4)]   // carries on to the next row
    [InlineData(Key.Right, 10, 10)] // last card: stays
    [InlineData(Key.Left, 4, 3)]    // back to the end of the row above
    [InlineData(Key.Left, 0, 0)]
    [InlineData(Key.Down, 1, 5)]
    [InlineData(Key.Down, 7, 10)]   // nothing below in the short last row: its last card
    [InlineData(Key.Down, 9, 9)]    // already on the last row
    [InlineData(Key.Up, 9, 5)]
    [InlineData(Key.Up, 2, 2)]      // first row: stays
    [InlineData(Key.Home, 6, 0)]
    [InlineData(Key.End, 1, 10)]
    public void Arrows_Home_End(Key key, int from, int expected) => Assert.Equal(expected, Move(key, from));

    [Theory]
    [InlineData(Key.PageDown, 1, 2, 9)]  // two rows down, same column
    [InlineData(Key.PageDown, 3, 2, 10)] // the column runs out: last card
    [InlineData(Key.PageDown, 9, 2, 9)]  // already on the last row
    [InlineData(Key.PageUp, 10, 2, 2)]
    [InlineData(Key.PageUp, 6, 5, 2)]    // past the top: same column, first row
    public void PageUp_PageDown(Key key, int from, int rowsPerPage, int expected) => Assert.Equal(expected, Move(key, from, rowsPerPage));

    [Fact]
    public void One_card_per_row_and_other_keys()
    {
        Assert.Equal(1, CardGridNavigation.Target(Key.Down, 0, 3, 1, 1));
        Assert.Equal(0, CardGridNavigation.Target(Key.Up, 1, 3, 1, 1));
        Assert.Equal(5, Move(Key.Enter, 5));
        Assert.Equal(0, CardGridNavigation.Target(Key.Right, 0, 0, 4, 1));
    }
}
