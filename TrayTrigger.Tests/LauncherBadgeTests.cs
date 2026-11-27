using TrayTrigger.Models;
using TrayTrigger.Services;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>The Library card's one launcher badge (LauncherLogos.BadgeFor and its logo and label).</summary>
public class LauncherBadgeTests
{
    [Fact]
    public void PlainGame_IsLocal_WithTheLocalLogo()
    {
        var game = new GameEntry { Name = "Local" };
        Assert.Equal(LauncherBadge.Local, LauncherLogos.BadgeFor(game));
        Assert.EndsWith("local_games.png", LauncherLogos.PackUriFor(LauncherBadge.Local));
        Assert.Equal("LOCAL", LauncherLogos.ShortLabelFor(LauncherBadge.Local));
    }

    [Fact]
    public void SteamGame_And_ForcedSteamBadge_AreSteam()
    {
        Assert.Equal(LauncherBadge.Steam, LauncherLogos.BadgeFor(new GameEntry { IsSteamGame = true }));
        Assert.Equal(LauncherBadge.Steam, LauncherLogos.BadgeFor(new GameEntry { ForceSteamOverlayTag = true }));
    }

    [Fact]
    public void StoreFlag_WinsOverAForcedSteamBadge_LikeTheCategory()
    {
        var game = new GameEntry { IsGogGame = true, ForceSteamOverlayTag = true };
        Assert.Equal(LauncherBadge.Gog, LauncherLogos.BadgeFor(game));
        Assert.Equal(LibraryConstants.GogCategory, LibraryConstants.PlatformCategoryFor(game));
    }

    [Theory]
    [InlineData(nameof(GameEntry.IsGogGame), LauncherBadge.Gog)]
    [InlineData(nameof(GameEntry.IsEaGame), LauncherBadge.Ea)]
    [InlineData(nameof(GameEntry.IsEpicGame), LauncherBadge.Epic)]
    [InlineData(nameof(GameEntry.IsUbisoftGame), LauncherBadge.Ubisoft)]
    [InlineData(nameof(GameEntry.IsXboxGame), LauncherBadge.Xbox)]
    [InlineData(nameof(GameEntry.IsBattleNetGame), LauncherBadge.BattleNet)]
    [InlineData(nameof(GameEntry.IsRockstarGame), LauncherBadge.Rockstar)]
    [InlineData(nameof(GameEntry.IsAmazonGame), LauncherBadge.Amazon)]
    [InlineData(nameof(GameEntry.IsItchGame), LauncherBadge.Itch)]
    public void EachStoreFlag_GivesItsBadge_AndTheSameLogoAsTheTray(string flag, LauncherBadge expected)
    {
        var game = new GameEntry();
        typeof(GameEntry).GetProperty(flag)!.SetValue(game, true);

        Assert.Equal(expected, LauncherLogos.BadgeFor(game));
        Assert.Equal(LauncherLogos.PackUriFor(game), LauncherLogos.PackUriFor(expected));
        Assert.False(string.IsNullOrEmpty(LauncherLogos.ShortLabelFor(expected)));
        Assert.NotEqual("LOCAL", LauncherLogos.ShortLabelFor(expected));
    }
}
