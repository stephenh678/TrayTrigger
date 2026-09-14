using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>Tools is off by default, its tray submenu too, and the page opens in its default view, sort and tab.</summary>
public class ToolSettingsDefaultsTests
{
    [Fact]
    public void Tools_AreOffByDefault_WithDefaultViewSortAndTab()
    {
        var settings = new AppSettings();

        Assert.False(settings.EnableTools);
        Assert.False(settings.ShowToolsInTray);
        Assert.Equal(ToolCatalog.SortAlphabetical, settings.ToolsTraySortOption);
        Assert.Equal(ToolCatalog.SortAlphabetical, settings.ToolsSortOption);
        Assert.Equal(ToolCatalog.ViewLargeIcons, settings.ToolsViewMode);
        Assert.Equal(LibraryConstants.AllCategory, settings.LastToolsCategoryTab);
    }

    [Fact]
    public void NewTool_IsUncategorizedAndNotAFavorite()
    {
        var tool = new ToolEntry();

        Assert.Equal(LibraryConstants.Uncategorized, tool.Category);
        Assert.False(tool.IsFavorite);
        Assert.False(tool.RunAsAdmin);
        Assert.False(string.IsNullOrWhiteSpace(tool.Id));
    }
}
