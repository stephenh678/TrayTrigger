using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>The Details List's resizable columns: limits, the title's minimum, saving and reset.</summary>
public class DetailsListColumnsTests
{
    private static (DetailsListColumns Columns, List<Dictionary<string, double>> Saves) Attached(Dictionary<string, double>? saved = null)
    {
        var columns = new DetailsListColumns();
        var saves = new List<Dictionary<string, double>>();
        columns.Attach(saved, saves.Add);
        return (columns, saves);
    }

    [Fact]
    public void NothingSaved_IsTheDefaults()
    {
        var (columns, _) = Attached();
        Assert.Equal(135, columns.Width(DetailsListColumns.Category));
        Assert.Equal(90, columns.ProfileWidth);
        Assert.Equal(125, columns.LastPlayedWidth.Value);
    }

    [Fact]
    public void ASavedWidthOutsideItsLimits_IsTheDefault()
    {
        var (columns, _) = Attached(new() { [DetailsListColumns.Category] = 10, [DetailsListColumns.Size] = 5000, [DetailsListColumns.Playtime] = 200 });
        Assert.Equal(135, columns.Width(DetailsListColumns.Category));
        Assert.Equal(90, columns.Width(DetailsListColumns.Size));
        Assert.Equal(200, columns.Width(DetailsListColumns.Playtime));
    }

    private static bool All(string _) => true;

    [Fact]
    public void Resize_KeepsTheColumnWithinItsLimits()
    {
        var (columns, _) = Attached();
        columns.Resize(DetailsListColumns.Size, -500, titleShown: 800, spare: 0, All);
        Assert.Equal(60, columns.Width(DetailsListColumns.Size));
        columns.Resize(DetailsListColumns.Size, 1000, titleShown: 800, spare: 5000, All);
        Assert.Equal(DetailsListColumns.MaxWidth, columns.Width(DetailsListColumns.Size));
    }

    [Fact]
    public void TheFirstDrag_FixesGameTitleWhereItIs()
    {
        var (columns, _) = Attached();
        Assert.True(columns.IsTitleFilling);
        columns.Resize(DetailsListColumns.Size, -10, titleShown: 612.4, spare: 0, All);
        Assert.Equal(612, columns.TitleMaxWidth);
    }

    [Fact]
    public void Widening_UsesTheSpareRoomFirst_ThenTheColumnsAfter_NeverOnesBefore()
    {
        var (columns, _) = Attached();
        // 20 of spare room, then 30 from Profile (90 to its 80 minimum is 10) and Playtime (110 to 90).
        columns.Resize(DetailsListColumns.Category, 50, titleShown: 500, spare: 20, All);
        Assert.Equal(185, columns.Width(DetailsListColumns.Category));
        Assert.Equal(80, columns.Width(DetailsListColumns.Profile));
        Assert.Equal(90, columns.Width(DetailsListColumns.Playtime));
        Assert.Equal(125, columns.Width(DetailsListColumns.LastPlayed));
        Assert.Equal(500, columns.TitleMaxWidth); // the column before it gave nothing
    }

    [Fact]
    public void Widening_StopsWhenEveryColumnAfterIsAtItsMinimum()
    {
        var (columns, _) = Attached();
        // After Playtime: Last Played 125 to 100 and Size 90 to 60 can give 55.
        columns.Resize(DetailsListColumns.Playtime, 200, titleShown: 500, spare: 0, All);
        Assert.Equal(165, columns.Width(DetailsListColumns.Playtime));
        Assert.Equal(100, columns.Width(DetailsListColumns.LastPlayed));
        Assert.Equal(60, columns.Width(DetailsListColumns.Size));
    }

    [Fact]
    public void AColumnOffScreen_GivesNothing()
    {
        var (columns, _) = Attached();
        columns.Resize(DetailsListColumns.Category, 15, titleShown: 500, spare: 0, c => c != DetailsListColumns.Profile);
        Assert.Equal(90, columns.Width(DetailsListColumns.Profile));
        Assert.Equal(95, columns.Width(DetailsListColumns.Playtime));
    }

    [Fact]
    public void GameTitle_ResizesFromItsWidthOnScreen()
    {
        var (columns, _) = Attached(new() { [DetailsListColumns.Title] = 700 });
        // A narrow window shows 400 of its 700 cap: dragging left 50 makes it 350 at once, not 650.
        columns.Resize(DetailsListColumns.Title, -50, titleShown: 400, spare: 0, All);
        Assert.Equal(350, columns.TitleMaxWidth);
    }

    [Fact]
    public void Save_WritesOnlyTheWidthsMovedFromTheirDefault()
    {
        var (columns, saves) = Attached();
        columns.Resize(DetailsListColumns.Profile, 20, titleShown: 600, spare: 100, All);
        Assert.Empty(saves); // a drag saves once, when it ends
        columns.Save();
        Assert.Equal(new Dictionary<string, double> { [DetailsListColumns.Profile] = 110, [DetailsListColumns.Title] = 600 }, saves.Single());
    }

    [Fact]
    public void Reset_PutsOneOrEveryColumnBack_AndSaves()
    {
        var (columns, saves) = Attached(new() { [DetailsListColumns.Category] = 200, [DetailsListColumns.Size] = 150 });
        columns.Reset(DetailsListColumns.Category);
        Assert.Equal(135, columns.Width(DetailsListColumns.Category));
        Assert.Equal(150, columns.Width(DetailsListColumns.Size));
        columns.Reset();
        Assert.Equal(90, columns.Width(DetailsListColumns.Size));
        Assert.True(columns.IsTitleFilling);
        Assert.Empty(saves.Last());
    }
}
