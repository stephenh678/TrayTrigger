using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// Which files a library save actually touches.
///
/// Every game launch and every game exit raises GameUpdated, and the handler used to call
/// SaveLibrary, which writes settings.json as well as games.json. Nothing in settings changes when
/// a game starts or stops - only the game's LastPlayed and cumulative playtime - so four launches
/// meant eight full encrypt-and-replace cycles of a file whose contents had not moved.
/// </summary>
public class LibrarySaveScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void Sta(Action body) => WpfTestHost.Run(body);

    private (LibraryViewModel Library, StorageService Storage) CreateLibrary(params string[] gameNames)
    {
        var storage = new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"));
        var launcher = new ProcessLauncherService(
            storage, new PerformanceProfileService(storage), new GameScriptService(),
            new SteamScannerService(), new GogScannerService(), new EaScannerService(),
            new EpicScannerService(), new UbisoftScannerService(), new XboxScannerService());

        var library = new LibraryViewModel(
            storage, new IconExtractorService(storage), launcher, new HotkeyManager(),
            new SteamMetadataService(), new SteamSearchService(), new AppSettings(),
            getUseVerticalPosterArt: () => true,
            getSteamGridDbApiKeyOrNull: () => null);

        foreach (var name in gameNames)
        {
            library.Games.Add(library.CreateCardViewModel(new GameEntry
            {
                Name = name,
                Category = "Action",
                ExecutablePath = Path.Combine(_root, "games", name, "game.exe"),
            }));
        }
        library.RebuildCategories();
        return (library, storage);
    }

    /// <summary>The launch path persists the game and leaves settings.json alone.</summary>
    [Fact]
    public void GameUpdatedFromLauncher_WritesGamesButNotSettings() => Sta(() =>
    {
        var (library, storage) = CreateLibrary("Alpha");
        int games = storage.GamesSaveCount;
        int settings = storage.SettingsSaveCount;

        library.OnGameUpdatedFromLauncher(library.Games[0].Game);

        Assert.Equal(games + 1, storage.GamesSaveCount);
        Assert.Equal(settings, storage.SettingsSaveCount);
    });

    /// <summary>A full session - launch then exit - is two library writes and no settings writes.</summary>
    [Fact]
    public void AFullSession_NeverWritesSettings() => Sta(() =>
    {
        var (library, storage) = CreateLibrary("Alpha");
        int settings = storage.SettingsSaveCount;

        var game = library.Games[0].Game;
        game.LastPlayed = DateTime.Now;
        library.OnGameUpdatedFromLauncher(game);   // launch
        game.CumulativePlaytimeMinutes += 2;
        library.OnGameUpdatedFromLauncher(game);   // exit

        Assert.Equal(settings, storage.SettingsSaveCount);
    });

    /// <summary>
    /// SaveLibrary still writes both, because the import paths that call it do change settings -
    /// scan locations, the one-time prompt flags. Narrowing that would lose those.
    /// </summary>
    [Fact]
    public void SaveLibrary_StillWritesBoth() => Sta(() =>
    {
        var (library, storage) = CreateLibrary("Alpha");
        int games = storage.GamesSaveCount;
        int settings = storage.SettingsSaveCount;

        library.SaveLibrary();

        Assert.Equal(games + 1, storage.GamesSaveCount);
        Assert.Equal(settings + 1, storage.SettingsSaveCount);
    });

    /// <summary>The narrower save still round-trips the library to disk.</summary>
    [Fact]
    public void SaveGamesOnly_PersistsTheLibrary() => Sta(() =>
    {
        var (library, storage) = CreateLibrary("Alpha", "Bravo");

        library.SaveGamesOnly();

        var reloaded = storage.LoadGames();
        Assert.Equal(2, reloaded.Count);
        Assert.Contains(reloaded, g => g.Name == "Alpha");
        Assert.Contains(reloaded, g => g.Name == "Bravo");
    });
}
