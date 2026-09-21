using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// The context menu's cascading quick settings, single-game and batch. They write the same
/// GameEntry fields Edit Game Properties writes, so the value they matter for is that both routes
/// agree and that the check marks describe what a click will do.
/// </summary>
public class BatchQuickSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerBatchTests", Guid.NewGuid().ToString("N"));

    /// <summary>Card view models touch WPF input state, which needs an STA thread with a real
    /// dispatcher - see WpfTestHost. Without it these tests throw rather than pass vacuously.</summary>
    private static void Sta(Action body) => WpfTestHost.Run(body);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private (LibraryViewModel Library, StorageService Storage) NewLibrary(params GameEntry[] games)
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
        library.RebuildCategories();
        return (library, storage);
    }

    private static GameEntry Game(string name) => new() { Name = name, ExecutablePath = @"C:\Games\" + name + @"\game.exe" };

    private static void SelectAll(LibraryViewModel library)
    {
        foreach (var card in library.Games) card.IsSelected = true;
        library.SelectAllCommand.Execute(null);
    }

    // --- Batch ---------------------------------------------------------------------------

    /// <summary>
    /// All-or-nothing, matching the Favorite and Hide batch toggles: a mixed selection turns every
    /// game on rather than inverting each one, so the result is never a different kind of mixed.
    /// </summary>
    [Fact]
    public void BatchRunAsAdmin_MixedSelection_TurnsEveryGameOn() => Sta(() =>
    {
        var a = Game("A");
        var b = Game("B");
        b.RunAsAdmin = true;
        var (library, _) = NewLibrary(a, b);
        SelectAll(library);

        Assert.False(library.BatchAllRunAsAdmin);

        library.BatchRunAsAdminCommand.Execute(null);

        Assert.True(a.RunAsAdmin);
        Assert.True(b.RunAsAdmin);
        Assert.True(library.BatchAllRunAsAdmin);
    });

    [Fact]
    public void BatchRunAsAdmin_AllOn_TurnsEveryGameOff() => Sta(() =>
    {
        var a = Game("A");
        var b = Game("B");
        a.RunAsAdmin = b.RunAsAdmin = true;
        var (library, _) = NewLibrary(a, b);
        SelectAll(library);

        library.BatchRunAsAdminCommand.Execute(null);

        Assert.False(a.RunAsAdmin);
        Assert.False(b.RunAsAdmin);
    });

    [Fact]
    public void BatchCloseLauncher_TogglesAllOrNothing() => Sta(() =>
    {
        var a = Game("A");
        var b = Game("B");
        b.CloseLauncherOnExit = true;
        var (library, _) = NewLibrary(a, b);
        SelectAll(library);

        library.BatchCloseLauncherCommand.Execute(null);
        Assert.True(a.CloseLauncherOnExit);
        Assert.True(b.CloseLauncherOnExit);

        library.BatchCloseLauncherCommand.Execute(null);
        Assert.False(a.CloseLauncherOnExit);
        Assert.False(b.CloseLauncherOnExit);
    });

    [Fact]
    public void BatchCpuAffinity_SetsEveryGameAndTheCheckMark() => Sta(() =>
    {
        var a = Game("A");
        var b = Game("B");
        var (library, _) = NewLibrary(a, b);
        SelectAll(library);

        Assert.True(library.BatchCpuAffinityIsDefault);

        library.BatchSetCpuAffinityCommand.Execute(CpuAffinityMode.PerformanceCoresOnly);

        Assert.Equal(CpuAffinityMode.PerformanceCoresOnly, a.CpuAffinity);
        Assert.Equal(CpuAffinityMode.PerformanceCoresOnly, b.CpuAffinity);
        Assert.True(library.BatchCpuAffinityIsPerformanceCores);
        Assert.False(library.BatchCpuAffinityIsDefault);
    });

    /// <summary>A selection that disagrees shows no check at all, rather than a misleading one.</summary>
    [Fact]
    public void BatchCpuAffinity_MixedSelection_ShowsNoCheck() => Sta(() =>
    {
        var a = Game("A");
        var b = Game("B");
        b.CpuAffinity = CpuAffinityMode.PerformanceCoresOnly;
        var (library, _) = NewLibrary(a, b);
        SelectAll(library);

        Assert.Null(library.BatchCpuAffinity);
        Assert.False(library.BatchCpuAffinityIsDefault);
        Assert.False(library.BatchCpuAffinityIsPerformanceCores);
    });

    /// <summary>Nothing selected is a no-op, not a write across the whole library.</summary>
    [Fact]
    public void BatchCommands_WithNoSelection_DoNothing() => Sta(() =>
    {
        var a = Game("A");
        var (library, storage) = NewLibrary(a);
        int saves = storage.GamesSaveCount;

        library.BatchRunAsAdminCommand.Execute(null);
        library.BatchCloseLauncherCommand.Execute(null);
        library.BatchSetCpuAffinityCommand.Execute(CpuAffinityMode.PerformanceCoresOnly);

        Assert.False(a.RunAsAdmin);
        Assert.False(a.CloseLauncherOnExit);
        Assert.Equal(CpuAffinityMode.Default, a.CpuAffinity);
        Assert.Equal(saves, storage.GamesSaveCount);
    });

    /// <summary>These change the library, never settings - see LibrarySaveScopeTests.</summary>
    [Fact]
    public void BatchCommands_WriteGamesButNotSettings() => Sta(() =>
    {
        var (library, storage) = NewLibrary(Game("A"), Game("B"));
        SelectAll(library);
        int settings = storage.SettingsSaveCount;
        int games = storage.GamesSaveCount;

        library.BatchRunAsAdminCommand.Execute(null);
        library.BatchSetCpuAffinityCommand.Execute(CpuAffinityMode.PerformanceCoresOnly);
        library.BatchSetProfileCommand.Execute(PerformanceProfileMode.Optimized);

        Assert.Equal(games + 3, storage.GamesSaveCount);
        Assert.Equal(settings, storage.SettingsSaveCount);
    });

    // --- Single game ---------------------------------------------------------------------

    [Fact]
    public void SingleGame_QuickSettings_MatchTheBatchFields() => Sta(() =>
    {
        var game = Game("A");
        var (library, storage) = NewLibrary(game);
        var card = library.Games[0];
        int settings = storage.SettingsSaveCount;

        card.SetProfileCommand.Execute(PerformanceProfileMode.Aggressive);
        card.SetCpuAffinityCommand.Execute(CpuAffinityMode.PerformanceCoresOnly);
        card.ToggleRunAsAdminCommand.Execute(null);
        card.ToggleCloseLauncherCommand.Execute(null);

        Assert.Equal(PerformanceProfileMode.Aggressive, game.PerformanceProfile);
        Assert.Equal(CpuAffinityMode.PerformanceCoresOnly, game.CpuAffinity);
        Assert.True(game.RunAsAdmin);
        Assert.True(game.CloseLauncherOnExit);

        Assert.True(card.ProfileIsAggressive);
        Assert.False(card.ProfileIsOff);
        Assert.True(card.CpuAffinityIsPerformanceCores);
        Assert.True(card.RunAsAdmin);
        Assert.True(card.CloseLauncherOnExit);

        Assert.Equal(settings, storage.SettingsSaveCount);
    });

    /// <summary>Picking the profile already set should not cost a disk write.</summary>
    [Fact]
    public void SingleGame_SettingTheSameProfileTwice_WritesOnce() => Sta(() =>
    {
        var (library, storage) = NewLibrary(Game("A"));
        var card = library.Games[0];

        card.SetProfileCommand.Execute(PerformanceProfileMode.Optimized);
        int after = storage.GamesSaveCount;
        card.SetProfileCommand.Execute(PerformanceProfileMode.Optimized);

        Assert.Equal(after, storage.GamesSaveCount);
    });

    /// <summary>"Close Launcher After Game Exits" has nothing to close for a plain local exe.</summary>
    [Fact]
    public void CloseLauncherItem_IsOnlyOfferedForLauncherGames() => Sta(() =>
    {
        var local = Game("Local");
        var steam = new GameEntry { Name = "Steam", IsSteamGame = true, ImportedFrom = LauncherPlatform.Steam, ExecutablePath = "steam://rungameid/1" };
        var (library, _) = NewLibrary(local, steam);

        Assert.False(library.Games[0].HasLauncherToClose);
        Assert.True(library.Games[1].HasLauncherToClose);
    });

    // --- DLSS Override -------------------------------------------------------------------
    // The one quick setting that writes to the NVIDIA driver rather than a field on the game. What
    // matters is that the tick says the same thing as the switch in Edit Game - both read the
    // game's ownership records - and that the records are what the menu leaves behind, since they
    // are the only thing that can ever take an override back off.

    private static DlssProbeService.ProbeResult WithDlss() => new()
    {
        ShippedRuntimes = new[] { new DlssProbeService.ShippedRuntime("Super Resolution", @"bin
vngx_dlss.dll", "310.1.0") }
    };

    private static DlssProbeService.ProbeResult WithoutDlss() => new();

    /// <summary>The tick is the records, exactly as Edit Game > Performance reports it.</summary>
    [Fact]
    public void DlssOverrideTick_ReadsTheGamesOwnershipRecords() => Sta(() =>
    {
        var (library, _) = NewLibrary(Game("A"));
        var card = library.Games[0];

        Assert.False(card.DlssOverrideOn);

        card.Game.DlssSettings.Add(new DlssSettingRecord
        {
            SettingId = 1, ProfileName = "A", ApplicationName = "game.exe"
        });
        card.NotifyDlssOverrideChanged();

        Assert.True(card.DlssOverrideOn);
    });

    /// <summary>
    /// Most games ship no DLSS. The menu must say so rather than leave a tick that quietly refuses,
    /// and must not write a record for an override that never happened.
    /// </summary>
    [Fact]
    public async Task DlssOverride_OnAGameWithNoDlss_SaysSoAndChangesNothing()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var (library, _) = NewLibrary(Game("A"));
            library.DlssProbe = (_, _) => WithoutDlss();
            var card = library.Games[0];

            card.ToggleDlssOverrideCommand.Execute(null);
            await WaitWhileBusy(card);

            Assert.False(card.DlssOverrideOn);
            Assert.Empty(card.Game.DlssSettings);
            Assert.Contains(DlssCardViewModel.NotAvailableLine, library.StatusMessage);
        });
    }

    /// <summary>The command refuses a second click while the driver write is still in flight.</summary>
    [Fact]
    public void DlssOverride_WhileBusy_RefusesASecondClick() => Sta(() =>
    {
        var (library, _) = NewLibrary(Game("A"));
        var card = library.Games[0];

        Assert.True(card.ToggleDlssOverrideCommand.CanExecute(null));
        card.IsDlssOverrideBusy = true;
        Assert.False(card.ToggleDlssOverrideCommand.CanExecute(null));
        card.IsDlssOverrideBusy = false;
        Assert.True(card.ToggleDlssOverrideCommand.CanExecute(null));
    });

    /// <summary>
    /// Whether the driver is there is a fact about the PC, not about a game, so every card agrees.
    /// Asserted as agreement rather than a fixed value: this has to mean the same on a machine with
    /// an NVIDIA driver and without.
    /// </summary>
    [Fact]
    public void DlssOverride_DriverPresence_IsTheSameOnEveryCard() => Sta(() =>
    {
        var (library, _) = NewLibrary(Game("A"), Game("B"));

        Assert.Equal(library.Games[0].HasNvidiaDriver, library.Games[1].HasNvidiaDriver);
    });

    private static async Task WaitWhileBusy(GameCardViewModel card)
    {
        // The command starts the work and returns; the toggle clears the flag in its finally.
        for (int i = 0; i < 200 && card.IsDlssOverrideBusy; i++)
        {
            await Task.Delay(10);
        }
    }
}
