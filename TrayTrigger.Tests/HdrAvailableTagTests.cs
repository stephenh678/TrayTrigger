using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>Steam's "HDR available" category as a Game Details tag, placed after the play modes.</summary>
public class HdrAvailableTagTests
{
    [Theory]
    [InlineData("HDR available", true)]
    [InlineData("hdr available", true)]
    [InlineData("Single-player", true)]
    [InlineData("Steam Trading Cards", false)]
    [InlineData("In-App Purchases", false)]
    [InlineData(null, false)]
    public void HdrAvailable_IsKept_AmongTheTagsShown(string? category, bool shown)
    {
        Assert.Equal(shown, SteamMetadataService.IsRelevantPlayMode(category));
    }

    [Fact]
    public void HdrAvailable_ComesAfterThePlayModes_AndBeforeTheAntiCheatTag()
    {
        // Steam lists its categories in its own order; HDR can come before the play modes.
        var tags = GameDetailsViewModel.BuildTagList(["HDR available", "Single-player", "Steam Cloud"], "Anti-cheat: EasyAntiCheat");
        Assert.Equal(["Single-player", "Steam Cloud", "HDR available", "Anti-cheat: EasyAntiCheat"], tags);
    }

    [Fact]
    public void WithoutHdr_TheRowIsAsBefore()
    {
        Assert.Equal(["Single-player", "Co-op"], GameDetailsViewModel.BuildTagList(["Single-player", "Co-op"], null));
        Assert.Empty(GameDetailsViewModel.BuildTagList([], null));
    }
}
