using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// Scan for Games and a Steam install of a game whose other copy is already in the library. Every launcher game, and
/// many Local ones, is matched to a Steam App ID for its metadata; that match used to hide the Steam install from the
/// scan (and skip it on import) as though the game were already there from Steam. Only Steam entries do that now, and
/// the Steam copy is offered with a note naming the other one.
/// </summary>
public class SteamCopyBesideOtherStoresTests
{
    private const string Witcher3 = "292030";

    private static GameEntry SteamEntry() => new() { Name = "The Witcher 3", IsSteamGame = true, SteamAppId = Witcher3, ExecutablePath = $"steam://rungameid/{Witcher3}" };
    private static GameEntry GogEntry() => new() { Name = "The Witcher 3", IsGogGame = true, GogGameId = "1207664643", SteamAppId = Witcher3, ExecutablePath = @"C:\GOG Games\The Witcher 3\bin\x64\witcher3.exe" };
    private static GameEntry LocalEntry() => new() { Name = "The Witcher 3", SteamAppId = Witcher3, ExecutablePath = @"C:\Games\Witcher 3\bin\x64\witcher3.exe" };

    [Fact]
    public void OnlyASteamEntry_PutsTheSteamInstallInTheLibrary()
    {
        Assert.Empty(ImportCoordinator.SteamEntryAppIds([GogEntry(), LocalEntry()]));
        Assert.Equal([Witcher3], ImportCoordinator.SteamEntryAppIds([GogEntry(), SteamEntry()]));

        Assert.False(ImportCoordinator.IsSteamEntryFor(GogEntry(), Witcher3));
        Assert.False(ImportCoordinator.IsSteamEntryFor(LocalEntry(), Witcher3));
        Assert.True(ImportCoordinator.IsSteamEntryFor(SteamEntry(), Witcher3));
    }

    [Fact]
    public void AnotherStoresCopy_IsNamedBesideTheSteamOne()
    {
        var copies = ImportCoordinator.OtherCopiesByAppId([GogEntry(), SteamEntry(), new GameEntry { Name = "No match" }]);

        Assert.Equal("GOG", Assert.Single(copies).Value);
        Assert.Equal("Local", ImportCoordinator.OtherCopiesByAppId([LocalEntry()])[Witcher3]);
    }

    [Fact]
    public void TheScanRow_SaysWhereTheOtherCopyIs() => WpfTestHost.Run(() =>
    {
        var steam = new DiscoveredSteamGame(Witcher3, "The Witcher 3", @"C:\Steam\steamapps\common\The Witcher 3", null, null, false);

        Assert.Null(new ScannedGameItemViewModel(steam).AlsoInLibraryNote);
        Assert.Equal("Also in your library from GOG", new ScannedGameItemViewModel(steam with { AlsoInLibraryAs = "GOG" }).AlsoInLibraryNote);
        Assert.Equal("Also in your library as a Local game", new ScannedGameItemViewModel(steam with { AlsoInLibraryAs = "Local" }).AlsoInLibraryNote);
    });

    [Theory]
    [InlineData(@"C:\Steam\steamapps\common\The Witcher 3\bin\x64\witcher3.exe", true)]
    [InlineData(@"C:\Steam\steamapps\common\The Witcher 3 Extras\tool.exe", false)]
    [InlineData(@"C:\GOG Games\The Witcher 3\bin\x64\witcher3.exe", false)]
    public void AnEntryRunningAProgramInTheSteamFolder_IsThatSteamInstall(string entryExe, bool inside)
    {
        Assert.Equal(inside, ImportCoordinator.HasEntryInside([entryExe], @"C:\Steam\steamapps\common\The Witcher 3"));
        Assert.Equal(inside, ImportCoordinator.HasEntryInside([entryExe], @"C:\Steam\steamapps\common\The Witcher 3\"));
    }
}
