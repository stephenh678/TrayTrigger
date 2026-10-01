using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// System › Performance Profiles' "New games start on".
/// </summary>
public class NewGameProfileAndSortTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private (LibraryViewModel Library, ImportCoordinator Import) NewLibrary(AppSettings settings, params GameEntry[] games)
    {
        var storage = new StorageService(Path.Combine(_root, Guid.NewGuid().ToString("N")), Path.Combine(_root, "local"));
        var icons = new IconExtractorService(storage);
        var steamScanner = new SteamScannerService();
        var gogScanner = new GogScannerService();
        var eaScanner = new EaScannerService();
        var epicScanner = new EpicScannerService();
        var ubisoftScanner = new UbisoftScannerService();
        var xboxScanner = new XboxScannerService();
        var battleNetScanner = new BattleNetScannerService();
        var steamSearch = new SteamSearchService();
        var steamMetadata = new SteamMetadataService();
        var launcher = new ProcessLauncherService(
            storage, new PerformanceProfileService(storage), new GameScriptService(),
            steamScanner, gogScanner, eaScanner, epicScanner, ubisoftScanner, xboxScanner, battleNetScanner);

        var library = new LibraryViewModel(
            storage, icons, launcher, new HotkeyManager(), steamMetadata, steamSearch, settings,
            getUseVerticalPosterArt: () => false,
            getSteamGridDbApiKeyOrNull: () => null);
        foreach (var game in games)
        {
            library.Games.Add(library.CreateCardViewModel(game));
        }

        var import = new ImportCoordinator(
            library, new ShortcutService(), icons, new FolderScannerService(),
            steamScanner, gogScanner, eaScanner, epicScanner, ubisoftScanner, xboxScanner, battleNetScanner,
            steamSearch, steamMetadata, storage, settings,
            getSteamGridDbApiKeyOrNull: () => null);
        return (library, import);
    }

    /// <summary>Settings with every online lookup off, so adding a game never leaves the machine.</summary>
    private static AppSettings Offline(PerformanceProfileMode newGameProfile) => new()
    {
        NewGameProfile = newGameProfile,
        SearchOfficialTitleOnline = false,
        AutoCategorizeFromSteam = false,
        UseVerticalPosterArt = false,
    };

    [Fact]
    public void NewGames_StartOnOptimized_ByDefault()
    {
        Assert.Equal(PerformanceProfileMode.Optimized, new AppSettings().NewGameProfile);
    }

    [Theory]
    [InlineData(PerformanceProfileMode.Off)]
    [InlineData(PerformanceProfileMode.Optimized)]
    [InlineData(PerformanceProfileMode.Aggressive)]
    public async Task AnAddedGame_StartsOnTheChosenProfile(PerformanceProfileMode chosen)
    {
        string exe = Path.Combine(_root, "games", "Some Game", "game.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllBytes(exe, [0x4D, 0x5A]);

        await WpfTestHost.RunAsync(async () =>
        {
            var (library, import) = NewLibrary(Offline(chosen));
            await import.AddCandidateAsync(new GameCandidate("Some Game", exe, Path.GetDirectoryName(exe)!, 2));

            var added = Assert.Single(library.Games);
            Assert.Equal(chosen, added.Game.PerformanceProfile);
        });
    }

    /// <summary>The choice is for games added from now on: one already in the library keeps its own.</summary>
    [Fact]
    public void ChangingIt_LeavesGamesAlreadyInTheLibraryAlone() => WpfTestHost.Run(() =>
    {
        var settings = Offline(PerformanceProfileMode.Optimized);
        var storage = new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"));
        var existing = new GameEntry { Name = "Old", PerformanceProfile = PerformanceProfileMode.Optimized };
        var (library, _) = NewLibrary(settings, existing);
        var system = new SystemViewModel(new SystemInfoService(), new SystemTweaksService(), settings, storage);

        system.NewGameProfile = PerformanceProfileMode.Aggressive;

        Assert.Equal(PerformanceProfileMode.Aggressive, settings.NewGameProfile);
        Assert.Equal(PerformanceProfileMode.Aggressive, storage.LoadSettings().NewGameProfile);
        Assert.Equal(PerformanceProfileMode.Optimized, library.Games.Single().Game.PerformanceProfile);
        Assert.True(system.NewGameProfileIsAggressive);
    });
}
