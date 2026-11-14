using System.IO;
using System.Text.Json;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>The launch-last-played hotkey (Ctrl+Alt+L): its default, how it picks the game, and the combo it holds.</summary>
public class LaunchLastPlayedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerLastPlayedTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private LibraryViewModel NewLibrary(params GameEntry[] games)
    {
        var storage = new StorageService(Path.Combine(_root, Guid.NewGuid().ToString("N")), Path.Combine(_root, "local"));
        var launcher = new ProcessLauncherService(
            storage, new PerformanceProfileService(storage), new GameScriptService(),
            new SteamScannerService(), new GogScannerService(), new EaScannerService(),
            new EpicScannerService(), new UbisoftScannerService(), new XboxScannerService(), new BattleNetScannerService());
        var library = new LibraryViewModel(
            storage, new IconExtractorService(storage), launcher, new HotkeyManager(),
            new SteamMetadataService(), new SteamSearchService(), new AppSettings(),
            getUseVerticalPosterArt: () => true,
            getSteamGridDbApiKeyOrNull: () => null);
        foreach (var game in games) library.Games.Add(library.CreateCardViewModel(game));
        return library;
    }

    private static GameEntry Game(string name, DateTime? lastPlayed) =>
        new() { Name = name, ExecutablePath = @"C:\Games\" + name + @"\game.exe", LastPlayed = lastPlayed };

    [Fact]
    public void Hotkey_DefaultsToCtrlAltL_IncludingForASettingsFileWithoutIt()
    {
        Assert.Equal("Ctrl+Alt+L", new AppSettings().LaunchLastPlayedHotkey);
        var older = JsonSerializer.Deserialize<AppSettings>("{\"GlobalManageHotkey\": \"Ctrl+Alt+G\"}");
        Assert.Equal("Ctrl+Alt+L", older!.LaunchLastPlayedHotkey);
    }

    [Fact]
    public void DefaultCombo_IsUsableAndDoesNotClashWithTheOtherAppHotkeys()
    {
        var settings = new AppSettings();
        var combos = new[] { settings.GlobalManageHotkey, settings.TrayMenuHotkey, settings.SuspendGameHotkey, settings.LaunchLastPlayedHotkey };
        Assert.All(combos, c => Assert.True(HotkeyManager.ParseHotkey(c, out _, out _)));
        Assert.Equal(combos.Length, combos.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void PicksTheMostRecentlyPlayedGame() => WpfTestHost.Run(() =>
    {
        var now = DateTime.Now;
        var library = NewLibrary(Game("Old", now.AddDays(-5)), Game("Newest", now.AddHours(-1)), Game("Middle", now.AddDays(-1)), Game("Never", null));
        Assert.Equal("Newest", library.LastPlayedGame()?.Name);
    });

    [Fact]
    public void NoneWhenNothingHasBeenPlayed() => WpfTestHost.Run(() =>
    {
        var library = NewLibrary(Game("A", null), Game("B", null));
        Assert.Null(library.LastPlayedGame());
        Assert.False(library.LaunchLastPlayed());
    });
}
