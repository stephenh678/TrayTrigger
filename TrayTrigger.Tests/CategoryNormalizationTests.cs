using TrayTrigger.Services;

namespace TrayTrigger.Tests;

public class CategoryNormalizationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("All")]
    [InlineData("favorites")]
    [InlineData(" HIDDEN ")]
    public void NormalizeCategory_MapsBlankAndLibraryViewsToUncategorized(string? typed)
    {
        Assert.Equal(LibraryConstants.Uncategorized, LibraryConstants.NormalizeCategory(typed));
    }

    [Theory]
    [InlineData("RPG", "RPG")]
    [InlineData("  Strategy ", "Strategy")]
    [InlineData("Allegro", "Allegro")]
    [InlineData("Steam", "Steam")]
    public void NormalizeCategory_KeepsRealCategoriesTrimmed(string typed, string expected)
    {
        Assert.Equal(expected, LibraryConstants.NormalizeCategory(typed));
    }
}
