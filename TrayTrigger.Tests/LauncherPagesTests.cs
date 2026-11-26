using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>Which launcher Game Details' "Open in &lt;launcher&gt;" names for a game. Whether that launcher is
/// installed is read from this PC's registry, so only the naming is tested here.</summary>
public class LauncherPagesTests
{
    public static TheoryData<GameEntry, string?> Games() => new()
    {
        { new GameEntry { IsSteamGame = true, SteamAppId = "440" }, "Steam" },
        { new GameEntry { IsGogGame = true, GogGameId = "1207658924" }, "GOG Galaxy" },
        { new GameEntry { IsEpicGame = true, EpicAppName = "Fortnite" }, "the Epic Games Launcher" },
        { new GameEntry { IsEaGame = true, EaContentId = "Origin.OFR.50.0001" }, "the EA App" },
        { new GameEntry { IsUbisoftGame = true, UbisoftGameId = "635" }, "Ubisoft Connect" },
        { new GameEntry { IsBattleNetGame = true, BattleNetUid = "hs_beta" }, "Battle.net" },
        { new GameEntry { IsRockstarGame = true, RockstarGameId = "Grand Theft Auto V" }, "the Rockstar Games Launcher" },
        { new GameEntry { IsAmazonGame = true, AmazonGameId = "790f3b07-fc9c-4efe-bb66-32bd348a9d23" }, "Amazon Games" },
        // Repaired in the Xbox app or Windows, not by a launcher TrayTrigger opens.
        { new GameEntry { IsXboxGame = true, XboxAumid = "A_b!App" }, null },
        { new GameEntry { ExecutablePath = @"C:\Games\Local\game.exe" }, null },
        // Nothing unchecked goes into a link: a Steam game without a real App ID, a GOG id that isn't digits.
        { new GameEntry { IsSteamGame = true, SteamAppId = "not-an-id" }, null },
        { new GameEntry { IsGogGame = true, GogGameId = "12/../x" }, null },
        // A GOG or Epic game matched to a Steam App ID for its details still opens its own launcher.
        { new GameEntry { IsGogGame = true, GogGameId = "1207658924", SteamAppId = "440" }, "GOG Galaxy" },
    };

    [Theory]
    [MemberData(nameof(Games))]
    public void EachGame_IsOpenedInItsOwnLauncher(GameEntry game, string? expected)
    {
        Assert.Equal(expected, LauncherPages.LauncherName(game));
    }

    [Fact]
    public void AGameWithNoLauncher_HasNothingToOpen()
    {
        var local = new GameEntry { ExecutablePath = @"C:\Games\Local\game.exe" };
        Assert.Null(LauncherPages.OpenLabel(local));
        Assert.False(LauncherPages.Open(local));
    }
}
