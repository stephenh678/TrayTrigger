using System.IO;
using System.Runtime.ExceptionServices;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// Game Details as the game's one page: the header's figures, the Overview / Settings tabs, the editor
/// the Settings tab hosts (Save and Cancel start it again, clean), and leaving the page over open edits.
/// </summary>
public class GameDetailsHeroTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

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

    private IconExtractorService Icons() =>
        new(new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local")));

    private sealed class Recorder
    {
        public int Saved;
        public int Launched;
        public int Closed;
    }

    private (GameDetailsViewModel Vm, Recorder Log) Open(GameEntry game, bool withEditor = true, bool scriptsEnabled = true)
    {
        var log = new Recorder();
        var host = withEditor
            ? new GameEditHost(
                g => new GameEditViewModel(g, new[] { "Action" }, Icons(), scriptsEnabled: scriptsEnabled),
                _ => log.Saved++,
                () => scriptsEnabled,
                () => false)
            : null;
        // A catalogue with nothing in it: the page looks for a Steam match by name, and a test must not go to the network.
        var vm = new GameDetailsViewModel(game, new SteamMetadataService(), new CatalogueSteamSearchService(),
            launchAction: _ => log.Launched++, editHost: host);
        vm.RequestClose += () => log.Closed++;
        return (vm, log);
    }

    private static GameEntry Game(string name = "Hades") => new() { Name = "Zq" + Guid.NewGuid().ToString("N")[..8] + name, Category = "Action" };

    [Fact]
    public void WithoutAnEditor_ThePageHasNoSettingsTab_AndNoSetupCards() => Sta(() =>
    {
        var (vm, _) = Open(Game(), withEditor: false);
        Assert.False(vm.CanEdit);
        Assert.False(vm.EditGameCommand.CanExecute(null));
        vm.ShowSettings();
        vm.SelectedTab = GameDetailsTab.Settings;
        Assert.True(vm.IsOverviewTab);
        Assert.Empty(vm.SetupCards);
    });

    [Fact]
    public void Edit_OpensTheSettingsTabInPlace_AndLoadsTheEditorOnlyThen() => Sta(() =>
    {
        var (vm, log) = Open(Game());
        Assert.True(vm.IsOverviewTab);
        Assert.Null(vm.EditViewModel);

        vm.EditGameCommand.Execute(null);

        Assert.True(vm.IsSettingsTab);
        Assert.NotNull(vm.EditViewModel);
        Assert.Equal(0, log.Closed);
    });

    [Fact]
    public void ACardsLink_OpensTheSettingsTab_OnTheTabThatEditsThatArea() => Sta(() =>
    {
        var (vm, _) = Open(Game());
        vm.EditSectionCommand.Execute(GameEditSection.Display);
        Assert.True(vm.IsSettingsTab);
        Assert.Equal(GameEditSection.Display, vm.EditViewModel!.SelectedSection);
    });

    [Fact]
    public void Save_WritesOntoTheGame_StaysOnThePage_AndStartsTheEditorAgainClean() => Sta(() =>
    {
        var game = Game();
        var (vm, log) = Open(game);
        vm.ShowSettings();
        var first = vm.EditViewModel!;
        var changed = new List<string?>();
        var editorsSeen = new List<GameEditViewModel?>();
        vm.PropertyChanged += (_, e) =>
        {
            changed.Add(e.PropertyName);
            if (e.PropertyName == nameof(GameDetailsViewModel.EditViewModel)) editorsSeen.Add(vm.EditViewModel);
        };

        first.Name = "Hades II";
        Assert.True(vm.HasUnsavedEdits);
        first.SaveCommand.Execute(null);

        Assert.Equal("Hades II", game.Name);
        Assert.Equal(1, log.Saved);
        Assert.Equal(0, log.Closed);
        Assert.True(vm.IsSettingsTab);
        Assert.NotSame(first, vm.EditViewModel);
        Assert.False(vm.HasUnsavedEdits);
        Assert.Equal("Changes saved.", vm.EditViewModel!.StatusMessage);
        // Everything the page shows can have changed, so the page reads it all again.
        Assert.Contains(string.Empty, changed);
        // The tab is shown as empty before the clean editor arrives, so its panel is built again rather than handed a second editor.
        Assert.Null(editorsSeen[0]);
        Assert.Same(vm.EditViewModel, editorsSeen[^1]);
    });

    [Fact]
    public void Cancel_ThrowsTheEditsAway_AndStartsAgainFromTheSavedGame() => Sta(() =>
    {
        var game = Game("Hades");
        string saved = game.Name;
        var (vm, log) = Open(game);
        vm.ShowSettings();
        var first = vm.EditViewModel!;

        first.Name = "Something else";
        first.CancelCommand.Execute(null);

        Assert.Equal(saved, game.Name);
        Assert.Equal(0, log.Saved);
        Assert.NotSame(first, vm.EditViewModel);
        Assert.Equal(saved, vm.EditViewModel!.Name);
        Assert.False(vm.HasUnsavedEdits);
    });

    [Fact]
    public void Play_OverOpenEdits_AsksFirst_AndOnlyLaunchesOnAYes() => Sta(() =>
    {
        var (vm, log) = Open(Game());
        vm.ShowSettings();
        vm.EditViewModel!.Name = "Edited";

        bool answer = false;
        int asked = 0;
        vm.ConfirmLeave = () => { asked++; return answer; };

        vm.LaunchGameCommand.Execute(null);
        Assert.Equal(1, asked);
        Assert.Equal(0, log.Launched);
        Assert.Equal(0, log.Closed);

        answer = true;
        vm.LaunchGameCommand.Execute(null);
        Assert.Equal(1, log.Launched);
        Assert.Equal(1, log.Closed);
    });

    [Fact]
    public void Play_WithNothingOpen_LaunchesWithoutAsking() => Sta(() =>
    {
        var (vm, log) = Open(Game());
        // The page's own window asks only when there are edits; with none open the answer is yes.
        vm.ConfirmLeave = () => !vm.HasUnsavedEdits;
        vm.LaunchGameCommand.Execute(null);
        Assert.Equal(1, log.Launched);
    });

    [Fact]
    public void TheHeaderFigures_AreTheRecordedFacts() => Sta(() =>
    {
        var game = Game();
        var (vm, _) = Open(game);
        Assert.Equal("None yet", vm.PlayedStat);
        Assert.Equal("Never", vm.LastPlayedStat);
        Assert.Equal("Off", vm.ProfileStat);
        Assert.Equal("Local", vm.StoreName);

        game.CumulativePlaytimeMinutes = 150;
        game.LastPlayed = DateTime.UtcNow;
        game.PerformanceProfile = PerformanceProfileMode.Aggressive;
        game.IsGogGame = true;
        Assert.Equal("2.5h", vm.PlayedStat);
        Assert.NotEqual("Never", vm.LastPlayedStat);
        Assert.Equal("Aggressive", vm.ProfileStat);
        Assert.Equal("GOG", vm.StoreName);
    });

    [Fact]
    public void PlaytimeIsWrittenTheWayTheLibraryWritesIt() => Sta(() =>
    {
        Assert.Equal("45m", GameEntry.FormatPlaytime(45));
        Assert.Equal("1h", GameEntry.FormatPlaytime(60));
        Assert.Equal("2.5h", GameEntry.FormatPlaytime(150));
        Assert.Equal("1.5h played", new GameEntry { CumulativePlaytimeMinutes = 90 }.PlaytimeDisplay);
        Assert.Equal("0 min played", new GameEntry().PlaytimeDisplay);
    });

    // --- Your setup: what the saved game does, area by area ---

    [Fact]
    public void Setup_PerformanceSaysCustom_WhenOnlyAPerGameChoiceIsSet_AndNamesThemAsEditGameDoes()
    {
        var plain = GameSetupSummary.For(new GameEntry(), scriptsEnabled: true, defaultScriptsConfigured: false);
        var card = plain.Single(c => c.Title == "Performance");
        Assert.Equal("Off", card.Chip);
        Assert.False(card.ChipIsOn);

        var game = new GameEntry { Hdr = HdrMode.On, CpuAffinity = CpuAffinityMode.VCacheCores };
        card = GameSetupSummary.For(game, true, false).Single(c => c.Title == "Performance");
        Assert.Equal("Custom", card.Chip);
        Assert.True(card.ChipIsOn);
        Assert.Contains("HDR: On for this game", card.Summary);
        Assert.Contains("3D V-Cache Cores", card.Summary);
        Assert.Equal(GameEditSection.Performance, card.Section);

        game.PerformanceProfile = PerformanceProfileMode.Aggressive;
        Assert.Equal("Aggressive", GameSetupSummary.For(game, true, false).Single(c => c.Title == "Performance").Chip);
    }

    [Fact]
    public void Setup_HasADlssCard_OnlyWhileAnOverrideIsHeld()
    {
        Assert.DoesNotContain(GameSetupSummary.For(new GameEntry(), true, false), c => c.Title == "NVIDIA DLSS");

        var game = new GameEntry();
        game.DlssSettings.Add(new DlssSettingRecord());
        var card = GameSetupSummary.For(game, true, false).Single(c => c.Title == "NVIDIA DLSS");
        Assert.Equal("On", card.Chip);
    }

    [Fact]
    public void Setup_ScriptsSayOff_WhenSettingsStopsThem_AndCountEachScriptOnce()
    {
        var game = new GameEntry { PreLaunchScriptPath = @"C:\s\pre.ps1" };
        var off = GameSetupSummary.For(game, scriptsEnabled: false, defaultScriptsConfigured: false).Single(c => c.Title == "Scripts");
        Assert.Equal("Off", off.Chip);
        Assert.False(off.ChipIsOn);
        Assert.Contains("do not run", off.Summary);

        game.PostExitScriptPath = game.PreLaunchScriptPath;
        var one = GameSetupSummary.For(game, true, false).Single(c => c.Title == "Scripts");
        Assert.Equal("1 script", one.Chip);

        game.PostExitScriptPath = @"C:\s\post.ps1";
        Assert.Equal("2 scripts", GameSetupSummary.For(game, true, false).Single(c => c.Title == "Scripts").Chip);
    }

    [Fact]
    public void Setup_ScriptsSayDefaults_WhenSettingsDefaultsRunForAGameWithNone()
    {
        var game = new GameEntry();
        Assert.Equal("None set", GameSetupSummary.For(game, true, defaultScriptsConfigured: false).Single(c => c.Title == "Scripts").Chip);

        var card = GameSetupSummary.For(game, true, defaultScriptsConfigured: true).Single(c => c.Title == "Scripts");
        Assert.Equal("Defaults", card.Chip);

        game.SkipDefaultScripts = true;
        Assert.Equal("None set", GameSetupSummary.For(game, true, true).Single(c => c.Title == "Scripts").Chip);
    }

    [Fact]
    public void Setup_DisplayAndLaunch_ListWhatIsSet()
    {
        var game = new GameEntry { DisplayWidth = 2560, DisplayHeight = 1440, DisplayRefreshHz = 165, Hotkey = "Ctrl+Alt+H", RunAsAdmin = true };
        var cards = GameSetupSummary.For(game, true, false);

        var display = cards.Single(c => c.Title == "Display and sound");
        Assert.Equal("Custom", display.Chip);
        Assert.Contains("2560x1440 at 165 Hz", display.Summary);

        var launch = cards.Single(c => c.Title == "Launch");
        Assert.Contains("Ctrl+Alt+H", launch.Summary);
        Assert.Contains("administrator", launch.Summary);

        var untouched = GameSetupSummary.For(new GameEntry(), true, false);
        Assert.Equal("Default", untouched.Single(c => c.Title == "Display and sound").Chip);
        Assert.Equal("Default", untouched.Single(c => c.Title == "Launch").Chip);
    }

    [Theory]
    [InlineData(920, 221)]    // the window as it opens
    [InlineData(700, 210)]    // narrower: the floor that holds the name and the buttons
    [InlineData(1400, 260)]   // wider: the ceiling that leaves the page below room to work
    [InlineData(double.NaN, 210)]
    public void TheHero_GrowsWithTheWindow_BetweenAFloorAndACeiling(double width, double expected)
    {
        Assert.Equal(expected, Math.Round(TrayTrigger.Converters.RatioHeightConverter.For(width, "0.24,210,260")));
    }

    [Fact]
    public void TheHeaderLine_IsTheStoreAndTheGenres_AndFollowsThemWhenTheyArrive() => Sta(() =>
    {
        var (vm, _) = Open(Game());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal("Steam", vm.SourceName);
        // Until Steam's genres arrive the game's own category stands in for them.
        Assert.Equal($"{vm.StoreName} · Action", vm.HeroMetaLine);

        // What the line is made of changing tells the page the line changed too.
        vm.Details = new SteamAppDetails { AppId = "1", Name = "Hades", Genres = new List<string> { "Action", "RPG" } };
        Assert.Contains(nameof(GameDetailsViewModel.HeroMetaLine), changed);
        Assert.Equal($"{vm.StoreName} · {vm.GenresDisplay}", vm.HeroMetaLine);
    });

    // --- The banner behind the header ---

    private static async Task WaitFor(Func<bool> done)
    {
        for (int i = 0; i < 60 && !done(); i++) await Task.Delay(50);
    }

    private string WidePicture(string name)
    {
        Directory.CreateDirectory(Path.Combine(_root, "files"));
        string path = Path.Combine(_root, "files", name);
        using var bmp = new System.Drawing.Bitmap(1920, 620);
        var rng = new Random(5);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.DarkSlateBlue);
            for (int i = 0; i < 300; i++)
                g.FillRectangle(new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(rng.Next(256), rng.Next(256), rng.Next(256))), rng.Next(1900), rng.Next(600), 30, 30);
        }
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Jpeg);
        return path;
    }

    [Fact]
    public async Task TheChosenBanner_IsShown_AndPuttingItBackToAutomaticTakesItAway()
    {
        var urls = new List<string>();
        var service = new HeroArtService(Path.Combine(_root, "Heroes"))
        {
            DownloadBytes = (url, _) => { urls.Add(url); return Task.FromResult<byte[]?>(null); },
        };
        var game = Game();
        game.HeroImagePath = WidePicture("mine.jpg");
        var vm = new GameDetailsViewModel(game, new SteamMetadataService(), new CatalogueSteamSearchService(), heroArt: service);

        await WaitFor(() => vm.HasHeroImage);
        Assert.True(vm.HasHeroImage);

        game.HeroImagePath = null;
        await vm.LoadHeroAsync(replace: true);
        Assert.False(vm.HasHeroImage);
        Assert.Null(vm.HeroImage);
    }

    [Fact]
    public async Task AListingThePageOnlyShowed_ButDidNotLinkTheGameTo_GivesNoBanner()
    {
        var urls = new List<string>();
        var service = new HeroArtService(Path.Combine(_root, "Heroes"))
        {
            DownloadBytes = (url, _) => { urls.Add(url); return Task.FromResult<byte[]?>(null); },
        };
        var game = Game();   // no Steam App ID: a weak name match is shown but not saved on the game
        var vm = new GameDetailsViewModel(game, new SteamMetadataService(), new CatalogueSteamSearchService(), heroArt: service);

        vm.Details = new SteamAppDetails { AppId = "999", Name = "Some Other Game" };
        await vm.LoadHeroAsync();
        await Task.Delay(100);

        Assert.DoesNotContain(urls, u => u.Contains("/999/"));
        Assert.False(vm.HasHeroImage);
    }

    [Fact]
    public void ANameSavedInSettings_BecomesThePagesTitle_EvenOverSteamsSpelling() => Sta(() =>
    {
        var game = Game("Hades");
        var (vm, _) = Open(game);
        vm.Details = new SteamAppDetails { AppId = "1", Name = "Steam's Hades" };
        Assert.Equal("Steam's Hades", vm.GameTitle);

        vm.ShowSettings();
        vm.EditViewModel!.Name = "My Hades";
        vm.EditViewModel.SaveCommand.Execute(null);

        Assert.Equal("My Hades", vm.GameTitle);
    });
}
