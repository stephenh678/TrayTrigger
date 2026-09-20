using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;
using TrayTrigger.Views;

namespace TrayTrigger.Tests;

/// <summary>
/// What removing games from the library leaves behind: cached icon/poster files and Steam/RAWG
/// details. Files live in a temp icons/covers pair and both detail caches are redirected to temp
/// files for each test. No other test class touches those two static caches, so redirecting them
/// here can't race a class running in parallel.
/// </summary>
public class GameDataCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"));
    private readonly string _icons;
    private readonly string _covers;

    public GameDataCleanupTests()
    {
        _icons = Directory.CreateDirectory(Path.Combine(_root, "local", "Icons")).FullName;
        _covers = Directory.CreateDirectory(Path.Combine(_root, "local", "Covers")).FullName;
        SteamMetadataService.UseCacheFileForTests(Path.Combine(_root, "steam-cache.json"));
        RawgService.UseCacheFileForTests(Path.Combine(_root, "rawg-cache.json"));
    }

    public void Dispose()
    {
        SteamMetadataService.UseCacheFileForTests(null);
        RawgService.UseCacheFileForTests(null);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static readonly DateTime LongAgo = DateTime.UtcNow.AddDays(-1);

    /// <summary>Creates a small file whose creation and write times are <paramref name="timeUtc"/>
    /// (default: well before any sweep in the test starts).</summary>
    private static string Touch(string path, DateTime? timeUtc = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        var time = timeUtc ?? LongAgo;
        File.SetCreationTimeUtc(path, time);
        File.SetLastWriteTimeUtc(path, time);
        return path;
    }

    private static void SeedSteamCache(params (string AppId, DateTime FetchedUtc)[] entries)
    {
        var cache = entries.ToDictionary(e => e.AppId, e => new SteamAppDetails { AppId = e.AppId, FetchedUtc = e.FetchedUtc });
        File.WriteAllText(SteamMetadataService.CacheFilePath, JsonSerializer.Serialize(cache, AppJsonContext.Default.DictionaryStringSteamAppDetails));
        SteamMetadataService.UseCacheFileForTests(SteamMetadataService.CacheFilePath);
    }

    private static void SeedRawgCache(params (int RawgId, DateTime FetchedUtc)[] entries)
    {
        var cache = entries.ToDictionary(e => e.RawgId, e => new RawgGameDetails { RawgId = e.RawgId, FetchedUtc = e.FetchedUtc });
        File.WriteAllText(RawgService.CacheFilePath, JsonSerializer.Serialize(cache, AppJsonContext.Default.DictionaryInt32RawgGameDetails));
        RawgService.UseCacheFileForTests(RawgService.CacheFilePath);
    }

    /// <summary>Drops the in-memory caches so assertions read what was actually saved to disk.</summary>
    private static void ReloadCachesFromDisk()
    {
        SteamMetadataService.UseCacheFileForTests(SteamMetadataService.CacheFilePath);
        RawgService.UseCacheFileForTests(RawgService.CacheFilePath);
    }

    // ------------------------------------------------------------------ removal

    [Fact]
    public void RemovedGame_DeletesIconCoversFallbackCopiesAndCachedDetails()
    {
        var removed = new GameEntry { SteamAppId = "620", RawgId = 4200 };
        removed.IconPath = Touch(Path.Combine(_icons, $"{removed.Id}.png"));
        removed.CoverImagePath = Touch(Path.Combine(_covers, $"{removed.Id}.png"));   // custom cover
        string steamPoster = Touch(Path.Combine(_covers, "620.jpg"));                 // replaced by the custom cover
        string lockedFallback = Touch(Path.Combine(_covers, "620_638912345678901234.jpg"));
        string nameArt = Touch(Path.Combine(_covers, $"{removed.Id}.jpg"));           // earlier SteamGridDB-by-name art

        var other = new GameEntry { SteamAppId = "400", RawgId = 1 };
        other.IconPath = Touch(Path.Combine(_icons, $"{other.Id}.png"));
        other.CoverImagePath = Touch(Path.Combine(_covers, "400.jpg"));

        SeedSteamCache(("620", LongAgo), ("400", LongAgo));
        SeedRawgCache((4200, LongAgo), (1, LongAgo));

        var result = GameDataCleanup.DeleteRemovedGameData([removed], [other], _icons, _covers);

        Assert.Equal(new GameDataCleanup.Result(5, 1, 1), result);
        Assert.False(File.Exists(removed.IconPath));
        Assert.False(File.Exists(removed.CoverImagePath));
        Assert.False(File.Exists(steamPoster));
        Assert.False(File.Exists(lockedFallback));
        Assert.False(File.Exists(nameArt));
        Assert.True(File.Exists(other.IconPath));
        Assert.True(File.Exists(other.CoverImagePath));

        ReloadCachesFromDisk();
        Assert.False(SteamMetadataService.TryGetCached("620", out _));
        Assert.True(SteamMetadataService.TryGetCached("400", out _));
        Assert.False(RawgService.TryGetCached(4200, out _));
        Assert.True(RawgService.TryGetCached(1, out _));
    }

    [Fact]
    public void DataStillUsedByARemainingEntry_IsKept()
    {
        // A Steam entry and a local-exe entry for the same game share the poster and details;
        // the local entry also points at the Steam entry's icon.
        var steamEntry = new GameEntry { SteamAppId = "620", RawgId = 4200 };
        steamEntry.IconPath = Touch(Path.Combine(_icons, $"{steamEntry.Id}.png"));
        steamEntry.CoverImagePath = Touch(Path.Combine(_covers, "620.jpg"));
        var localEntry = new GameEntry
        {
            SteamAppId = " 620 ",
            RawgId = 4200,
            IconPath = steamEntry.IconPath,
            CoverImagePath = steamEntry.CoverImagePath,
        };
        SeedSteamCache(("620", LongAgo));
        SeedRawgCache((4200, LongAgo));

        var result = GameDataCleanup.DeleteRemovedGameData([steamEntry], [localEntry], _icons, _covers);

        Assert.Equal(default, result);
        Assert.True(File.Exists(steamEntry.IconPath));
        Assert.True(File.Exists(steamEntry.CoverImagePath));
        ReloadCachesFromDisk();
        Assert.True(SteamMetadataService.TryGetCached("620", out _));
        Assert.True(RawgService.TryGetCached(4200, out _));
    }

    [Fact]
    public void RemovedGame_NeverDeletesFilesOutsideTheCacheFolders()
    {
        var removed = new GameEntry();
        removed.CoverImagePath = Touch(Path.Combine(_root, "Pictures", "cover.jpg"));
        // Same prefix as the covers folder but a different folder.
        removed.IconPath = Touch(Path.Combine(_covers + "Old", $"{removed.Id}.png"));

        var result = GameDataCleanup.DeleteRemovedGameData([removed], [], _icons, _covers);

        Assert.Equal(0, result.FilesDeleted);
        Assert.True(File.Exists(removed.CoverImagePath));
        Assert.True(File.Exists(removed.IconPath));
    }

    [Fact]
    public void RemovedGame_NeverTouchesFilesKeyedToOtherGames()
    {
        // Files in the cache folders that belong to no known entry - e.g. a library that is live
        // in memory but was never saved to games.json - must survive someone else's removal.
        var removed = new GameEntry();
        removed.IconPath = Touch(Path.Combine(_icons, $"{removed.Id}.png"));
        string unknownIcon = Touch(Path.Combine(_icons, $"{Guid.NewGuid():N}.png"));
        string unknownPoster = Touch(Path.Combine(_covers, "620.jpg"));

        var result = GameDataCleanup.DeleteRemovedGameData([removed], [], _icons, _covers);

        Assert.Equal(1, result.FilesDeleted);
        Assert.True(File.Exists(unknownIcon));
        Assert.True(File.Exists(unknownPoster));
    }

    [Theory]
    [InlineData("620.jpg", "620")]
    [InlineData("620_638912345678901234.jpg", "620")]
    [InlineData("0f8fad5bd9cb469fa165708767b3a4c1.png", "0f8fad5bd9cb469fa165708767b3a4c1")]
    [InlineData("my_cover.png", "my_cover")]
    [InlineData("620_.jpg", "620_")]
    [InlineData("_123.jpg", "_123")]
    public void KeyOf_StripsExtensionAndLockedFileSuffix(string fileName, string expected) =>
        Assert.Equal(expected, GameDataCleanup.KeyOf(fileName));

    // ------------------------------------------------------------------ LibraryViewModel

    /// <summary>Runs the body on an STA thread (the view model owns WPF collection views and the
    /// hotkey listener's window) and rethrows anything it throws.</summary>
    private static void Sta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private LibraryViewModel CreateLibrary()
    {
        var storage = new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"));
        var launcher = new ProcessLauncherService(
            storage,
            new PerformanceProfileService(storage),
            new GameScriptService(),
            new SteamScannerService(),
            new GogScannerService(),
            new EaScannerService(),
            new EpicScannerService(),
            new UbisoftScannerService(),
            new XboxScannerService(), new BattleNetScannerService());
        return new LibraryViewModel(
            storage,
            new IconExtractorService(storage),
            launcher,
            new HotkeyManager(),
            new SteamMetadataService(),
            new SteamSearchService(),
            new AppSettings(),
            getUseVerticalPosterArt: () => true,
            getSteamGridDbApiKeyOrNull: () => null);
    }

    private GameCardViewModel AddGame(LibraryViewModel library, string name, string? steamAppId = null, LauncherPlatform? importedFrom = null)
    {
        var game = new GameEntry
        {
            Name = name,
            ExecutablePath = Path.Combine(_root, "games", name, "game.exe"),
            SteamAppId = steamAppId,
            ImportedFrom = importedFrom,
        };
        game.IconPath = Touch(Path.Combine(_icons, $"{game.Id}.png"));
        game.CoverImagePath = Touch(Path.Combine(_covers, $"{steamAppId ?? game.Id}.jpg"));
        var card = library.CreateCardViewModel(game);
        library.Games.Add(card);
        library.RebuildCategories();
        return card;
    }

    private static bool FilesExist(GameCardViewModel card) =>
        File.Exists(card.Game.IconPath) && File.Exists(card.Game.CoverImagePath);

    private static bool FilesGone(GameCardViewModel card) =>
        !File.Exists(card.Game.IconPath) && !File.Exists(card.Game.CoverImagePath);

    [Fact]
    public void BatchRemove_KeepsDataThroughUndo_AndDeletesItWhenTheWindowEnds() => Sta(() =>
    {
        var library = CreateLibrary();
        var alpha = AddGame(library, "Alpha");
        var bravo = AddGame(library, "Bravo");
        var charlie = AddGame(library, "Charlie");
        library.ConfirmBatchRemove = _ => true;

        library.SetCardSelected(alpha, true);
        library.SetCardSelected(bravo, true);
        library.BatchRemoveCommand.Execute(null);

        Assert.Single(library.Games);
        Assert.True(FilesExist(alpha), "Undo must still be able to bring the art back.");
        Assert.True(FilesExist(bravo));

        library.UndoDelete();
        Assert.Equal(3, library.Games.Count);
        Assert.True(FilesExist(alpha));

        alpha = library.Games.Single(c => c.Name == "Alpha");
        bravo = library.Games.Single(c => c.Name == "Bravo");
        library.SetCardSelected(alpha, true);
        library.SetCardSelected(bravo, true);
        library.BatchRemoveCommand.Execute(null);

        // What the undo timer, a following removal, and app exit all call.
        library.FinalizePendingRemoval();

        Assert.True(FilesGone(alpha));
        Assert.True(FilesGone(bravo));
        Assert.True(FilesExist(charlie));
        Assert.False(library.IsUndoToastVisible);

        // Nothing left to undo, and finalizing again is harmless.
        library.UndoDelete();
        library.FinalizePendingRemoval();
        Assert.Single(library.Games);
    });

    // --- The DLSS override leaves with the game ---------------------------------------------

    private static void RemoveSelected(LibraryViewModel library, params GameCardViewModel[] cards)
    {
        library.ConfirmBatchRemove = _ => true;
        foreach (var card in cards) library.SetCardSelected(card, true);
        library.BatchRemoveCommand.Execute(null);
    }

    [Fact]
    public void RemovingAGame_PutsItsDlssOverrideBack_OnceTheUndoWindowEnds() => Sta(() =>
    {
        // The records leave with the library entry, and they are the only thing that can undo the
        // override - so it has to be undone before they go.
        var driver = new Fakes.FakeDrsBackend();
        var library = CreateLibrary();
        library.DlssOverrides = new DlssOverrideService(driver);
        var alpha = AddGame(library, "Alpha");
        alpha.Game.DlssSettings.AddRange(library.DlssOverrides.Apply(alpha.Game.ExecutablePath, "Alpha").Records);
        Assert.True(driver.Profiles.ContainsKey("game.exe"));

        RemoveSelected(library, alpha);

        // Inside the window the game may yet come back, so nothing has been touched.
        Assert.NotEmpty(driver.Profiles["game.exe"].Settings);

        library.FinalizePendingRemoval();

        // Settings gone, and the profile TrayTrigger had to create with them.
        Assert.False(driver.Profiles.ContainsKey("game.exe"));
    });

    [Fact]
    public void UndoingTheRemoval_LeavesTheOverrideAndItsRecordsAsTheyWere() => Sta(() =>
    {
        var driver = new Fakes.FakeDrsBackend();
        var library = CreateLibrary();
        library.DlssOverrides = new DlssOverrideService(driver);
        var alpha = AddGame(library, "Alpha");
        alpha.Game.DlssSettings.AddRange(library.DlssOverrides.Apply(alpha.Game.ExecutablePath, "Alpha").Records);
        int held = alpha.Game.DlssSettings.Count;
        int saves = driver.SaveCount;

        RemoveSelected(library, alpha);
        library.UndoDelete();
        library.FinalizePendingRemoval();

        Assert.Equal(saves, driver.SaveCount);
        Assert.Equal(held, library.Games.Single().Game.DlssSettings.Count);
    });

    [Fact]
    public void ASettingAnotherEntryStillHolds_IsLeftAlone() => Sta(() =>
    {
        // Two library entries for one executable. Undoing for the one removed would switch the
        // override off under the one that is staying.
        var driver = new Fakes.FakeDrsBackend();
        var library = CreateLibrary();
        library.DlssOverrides = new DlssOverrideService(driver);
        var alpha = AddGame(library, "Alpha");
        var copy = AddGame(library, "Alpha (copy)");
        var records = library.DlssOverrides.Apply(alpha.Game.ExecutablePath, "Alpha").Records;
        alpha.Game.DlssSettings.AddRange(records);
        copy.Game.DlssSettings.AddRange(records);
        int saves = driver.SaveCount;

        RemoveSelected(library, alpha);
        library.FinalizePendingRemoval();

        Assert.Equal(saves, driver.SaveCount);
        Assert.NotEmpty(driver.Profiles["game.exe"].Settings);
    });

    [Fact]
    public void AnUnavailableDriver_NeverStopsTheRemoval() => Sta(() =>
    {
        var driver = new Fakes.FakeDrsBackend();
        var library = CreateLibrary();
        library.DlssOverrides = new DlssOverrideService(driver);
        var alpha = AddGame(library, "Alpha");
        alpha.Game.DlssSettings.AddRange(library.DlssOverrides.Apply(alpha.Game.ExecutablePath, "Alpha").Records);
        driver.OpenError = "no driver";

        RemoveSelected(library, alpha);
        library.FinalizePendingRemoval();

        Assert.Empty(library.Games);
        Assert.True(FilesGone(alpha));
    });

    // --- A crash inside the undo window ------------------------------------------------------

    [Fact]
    public void ACrashInsideTheUndoWindow_IsFinishedOnTheNextStart() => Sta(() =>
    {
        // games.json was already saved without the game, so without the pending file nothing
        // would know about its artwork - or hold the records that can undo its driver override.
        var driver = new Fakes.FakeDrsBackend();
        var library = CreateLibrary();
        library.DlssOverrides = new DlssOverrideService(driver);
        var alpha = AddGame(library, "Alpha");
        var bravo = AddGame(library, "Bravo");
        alpha.Game.DlssSettings.AddRange(library.DlssOverrides.Apply(alpha.Game.ExecutablePath, "Alpha").Records);

        RemoveSelected(library, alpha);
        // ...and the process dies here: no finalize, no undo.

        var next = CreateLibrary();
        next.DlssOverrides = new DlssOverrideService(driver);
        next.LoadLibrary();

        Assert.Equal("Bravo", Assert.Single(next.Games).Name);
        Assert.True(FilesGone(alpha));
        Assert.True(FilesExist(bravo));
        Assert.False(driver.Profiles.ContainsKey("game.exe"));

        // Consumed, so a third start does not go looking again.
        Assert.Empty(new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local")).LoadPendingRemoval());
    });

    [Fact]
    public void AfterAnUndo_OrACleanFinalize_NothingIsLeftPending() => Sta(() =>
    {
        var library = CreateLibrary();
        var alpha = AddGame(library, "Alpha");
        var storage = new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"));

        RemoveSelected(library, alpha);
        Assert.Single(storage.LoadPendingRemoval());

        library.UndoDelete();
        Assert.Empty(storage.LoadPendingRemoval());

        RemoveSelected(library, library.Games.Single());
        library.FinalizePendingRemoval();
        Assert.Empty(storage.LoadPendingRemoval());
    });

    [Fact]
    public void AGameThatIsBackInTheLibrary_IsNeverCleanedUpAfter() => Sta(() =>
    {
        // The crash came after an undo had saved the game back but before the pending file went.
        var library = CreateLibrary();
        var alpha = AddGame(library, "Alpha");
        var storage = new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"));
        library.SaveLibrary();
        storage.SavePendingRemoval(new[] { alpha.Game });

        var next = CreateLibrary();
        next.LoadLibrary();

        Assert.Single(next.Games);
        Assert.True(FilesExist(alpha));
        Assert.Empty(storage.LoadPendingRemoval());
    });

    [Fact]
    public void RemovePlatformGames_DeletesDataImmediately_AndClearsTheirSelection() => Sta(() =>
    {
        var library = CreateLibrary();
        var steamGame = AddGame(library, "Portal 2", steamAppId: "620", importedFrom: LauncherPlatform.Steam);
        steamGame.Game.RawgId = 4200;
        var localGame = AddGame(library, "Local Game");
        SeedSteamCache(("620", LongAgo));
        SeedRawgCache((4200, LongAgo));
        library.SetCardSelected(steamGame, true);

        int removed = library.RemovePlatformGames(DetectedLauncher.Steam);

        Assert.Equal(1, removed);
        Assert.Equal(0, library.SelectedCount);
        Assert.True(FilesGone(steamGame));
        Assert.True(FilesExist(localGame));
        ReloadCachesFromDisk();
        Assert.False(SteamMetadataService.TryGetCached("620", out _));
        Assert.False(RawgService.TryGetCached(4200, out _));
    });
}
