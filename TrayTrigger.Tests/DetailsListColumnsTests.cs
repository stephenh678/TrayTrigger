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

    [Fact]
    public void Resize_KeepsTheColumnWithinItsLimits()
    {
        var (columns, _) = Attached();
        columns.Resize(DetailsListColumns.Size, -500, titleWidth: 800);
        Assert.Equal(60, columns.Width(DetailsListColumns.Size));
        columns.Resize(DetailsListColumns.Size, 1000, titleWidth: 2000);
        Assert.Equal(DetailsListColumns.MaxWidth, columns.Width(DetailsListColumns.Size));
    }

    [Fact]
    public void Resize_OnlyTakesWhatTheTitleCanGiveAboveItsMinimum()
    {
        var (columns, _) = Attached();
        columns.Resize(DetailsListColumns.Category, 100, titleWidth: DetailsListColumns.TitleMinWidth + 30);
        Assert.Equal(165, columns.Width(DetailsListColumns.Category));
        // Narrowing always works, however squeezed the title is.
        columns.Resize(DetailsListColumns.Category, -40, titleWidth: DetailsListColumns.TitleMinWidth);
        Assert.Equal(125, columns.Width(DetailsListColumns.Category));
    }

    [Fact]
    public void Save_WritesOnlyTheWidthsMovedFromTheirDefault()
    {
        var (columns, saves) = Attached();
        columns.Resize(DetailsListColumns.Profile, 20, titleWidth: 600);
        Assert.Empty(saves); // a drag saves once, when it ends
        columns.Save();
        Assert.Equal(new Dictionary<string, double> { [DetailsListColumns.Profile] = 110 }, saves.Single());
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
        Assert.Empty(saves.Last());
    }
}
