using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// Collections: the manual, many-per-game grouping shown as a row above the category tabs, and the tools
/// that start or close only for the games in one. Built against a temp-folder StorageService, on an STA
/// thread like the other library tests.
/// </summary>
public class CollectionsTests : IDisposable
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

    /// <summary>A collection "Alpha" starts in, when a test needs one there from the first load.</summary>
    private string? _alphaCollection;

    private LibraryViewModel CreateLibrary(AppSettings? settings = null, params string[] gameNames)
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
        var library = new LibraryViewModel(
            storage,
            new IconExtractorService(storage),
            launcher,
            new HotkeyManager(),
            new SteamMetadataService(),
            new SteamSearchService(),
            settings ?? new AppSettings(),
            getUseVerticalPosterArt: () => true,
            getSteamGridDbApiKeyOrNull: () => null);

        foreach (var name in gameNames)
        {
            library.Games.Add(library.CreateCardViewModel(new GameEntry
            {
                Name = name,
                Category = name == "Gamma" ? "RPG" : "Action",
                ExecutablePath = Path.Combine(_root, "games", name, "game.exe"),
                Collections = name == "Alpha" && _alphaCollection != null ? [_alphaCollection] : [],
            }));
        }
        library.RebuildCategories();
        return library;
    }

    private static GameCardViewModel Card(LibraryViewModel library, string name) => library.Games.Single(c => c.Name == name);

    private static List<GameCardViewModel> Cards(LibraryViewModel library, params string[] names) => names.Select(n => Card(library, n)).ToList();

    private static List<string> Shown(LibraryViewModel library) =>
        library.FilteredGames.Cast<GameCardViewModel>().Select(c => c.Name).OrderBy(n => n).ToList();

    // ------------------------------------------------------------------ the library

    [Fact]
    public void NoCollections_NoRow_AndEveryGameShows() => Sta(() =>
    {
        var library = CreateLibrary(null, "Alpha", "Beta");

        Assert.False(library.HasCollections);
        Assert.Empty(library.CollectionTabs);
        Assert.Equal(["Alpha", "Beta"], Shown(library));
    });

    [Fact]
    public void ApplyCollections_PutsGamesIn_AndTheChipNarrowsTheLibrary() => Sta(() =>
    {
        var library = CreateLibrary(null, "Alpha", "Beta", "Gamma");

        library.ApplyCollections(Cards(library, "Alpha", "Gamma"), ["Co-op"], []);

        Assert.True(library.HasCollections);
        Assert.Equal(["", "Co-op"], library.CollectionTabs.Select(t => t.Name));
        library.SelectCollectionTab("Co-op");
        Assert.Equal(["Alpha", "Gamma"], Shown(library));
        library.SelectCollectionTab(string.Empty);
        Assert.Equal(["Alpha", "Beta", "Gamma"], Shown(library));
    });

    [Fact]
    public void ACollectionChip_NarrowsWithinTheSelectedCategory() => Sta(() =>
    {
        var library = CreateLibrary(null, "Alpha", "Beta", "Gamma");
        library.ApplyCollections(Cards(library, "Alpha", "Gamma"), ["Co-op"], []);

        library.SelectCollectionTab("Co-op");
        library.SelectedCategory = "RPG";

        Assert.Equal(["Gamma"], Shown(library));
    });

    [Fact]
    public void AGameCanBeInSeveralCollections_AndTakenOutOfOne() => Sta(() =>
    {
        var library = CreateLibrary(null, "Alpha", "Beta");
        library.ApplyCollections(Cards(library, "Alpha"), ["Co-op", "Kids"], []);
        library.ApplyCollections(Cards(library, "Beta"), ["Kids"], []);

        library.ApplyCollections(Cards(library, "Alpha"), [], ["Kids"]);

        Assert.Equal(["Co-op"], Card(library, "Alpha").Game.Collections);
        Assert.Equal(["Kids"], Card(library, "Beta").Game.Collections);
    });

    [Fact]
    public void ANameMatchingAnExistingCollection_KeepsItsSpelling() => Sta(() =>
    {
        var library = CreateLibrary(null, "Alpha", "Beta");
        library.ApplyCollections(Cards(library, "Alpha"), ["Co-op"], []);

        library.ApplyCollections(Cards(library, "Beta"), ["co-OP"], []);

        Assert.Equal(["Co-op"], Card(library, "Beta").Game.Collections);
        Assert.Equal(["Co-op"], library.CollectionNames());
    });

    [Fact]
    public void TheLastGameLeaving_RemovesTheChip_AndTheSelectionFallsBack() => Sta(() =>
    {
        var library = CreateLibrary(null, "Alpha", "Beta");
        library.ApplyCollections(Cards(library, "Alpha"), ["Co-op"], []);
        library.SelectCollectionTab("Co-op");

        library.ApplyCollections(Cards(library, "Alpha"), [], ["Co-op"]);

        Assert.False(library.HasCollections);
        Assert.Equal(string.Empty, library.SelectedCollection);
        Assert.Equal(["Alpha", "Beta"], Shown(library));
    });

    [Fact]
    public void AHiddenGameAlone_DoesNotMakeAChip() => Sta(() =>
    {
        var library = CreateLibrary(null, "Alpha", "Beta");
        Card(library, "Alpha").Game.IsHidden = true;
        library.ApplyCollections(Cards(library, "Alpha"), ["Secret"], []);

        Assert.False(library.HasCollections);
        Assert.Contains("Secret", library.CollectionNames());
    });

    [Fact]
    public void Dialog_ShowsEveryCollection_TickedForWhoIsIn_AndMixedForASelection() => Sta(() =>
    {
        var library = CreateLibrary(null, "Alpha", "Beta");
        library.ApplyCollections(Cards(library, "Alpha", "Beta"), ["Both"], []);
        library.ApplyCollections(Cards(library, "Alpha"), ["OnlyAlpha"], []);

        var choices = library.BuildCollectionChoices(Cards(library, "Alpha", "Beta"));

        Assert.Equal(true, choices.Single(c => c.Name == "Both").IsChecked);
        var mixed = choices.Single(c => c.Name == "OnlyAlpha");
        Assert.Null(mixed.IsChecked);
        Assert.True(mixed.IsMixed);
        Assert.False(choices.Single(c => c.Name == "Both").IsMixed);
    });

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("All", false)]
    [InlineData("favorites", false)]
    [InlineData("Hidden", false)]
    [InlineData("action", false)]   // a category the library has
    [InlineData("Co-op", true)]
    public void ValidateNewCollectionName_RefusesBlankAndCategoryNames(string name, bool fine) => Sta(() =>
    {
        var library = CreateLibrary(null, "Alpha");

        Assert.Equal(fine, library.ValidateNewCollectionName(name) == null);
    });

    [Fact]
    public void ValidateNewCollectionName_RefusesAVeryLongName() => Sta(() =>
    {
        var library = CreateLibrary(null, "Alpha");

        Assert.NotNull(library.ValidateNewCollectionName(new string('x', LibraryViewModel.MaxCollectionNameLength + 1)));
        Assert.Null(library.ValidateNewCollectionName(new string('x', LibraryViewModel.MaxCollectionNameLength)));
    });

    [Fact]
    public void Rename_ChangesEveryGame_AndTheSelectedChip_AndTellsTools() => Sta(() =>
    {
        var library = CreateLibrary(null, "Alpha", "Beta");
        library.ApplyCollections(Cards(library, "Alpha", "Beta"), ["Co-op"], []);
        library.SelectCollectionTab("Co-op");
        (string Old, string New)? told = null;
        library.CollectionRenamed += (o, n) => told = (o, n);

        string? problem = library.RenameCollection("Co-op", "Family");

        Assert.Null(problem);
        Assert.Equal(["Family"], Card(library, "Alpha").Game.Collections);
        Assert.Equal(["Family"], Card(library, "Beta").Game.Collections);
        Assert.Equal("Family", library.SelectedCollection);
        Assert.Equal(("Co-op", "Family"), told);
    });

    [Fact]
    public void Rename_RefusesAClash_WithAnotherCollectionOrACategory() => Sta(() =>
    {
        var library = CreateLibrary(null, "Alpha", "Beta");
        library.ApplyCollections(Cards(library, "Alpha"), ["Co-op"], []);
        library.ApplyCollections(Cards(library, "Beta"), ["Kids"], []);

        Assert.NotNull(library.RenameCollection("Co-op", "Kids"));
        Assert.NotNull(library.RenameCollection("Co-op", "Action"));
        Assert.Equal(["Co-op"], Card(library, "Alpha").Game.Collections);
    });

    [Fact]
    public void Rename_OnlyChangingCase_IsAllowed() => Sta(() =>
    {
        var library = CreateLibrary(null, "Alpha");
        library.ApplyCollections(Cards(library, "Alpha"), ["co-op"], []);

        Assert.Null(library.RenameCollection("co-op", "Co-op"));
        Assert.Equal(["Co-op"], Card(library, "Alpha").Game.Collections);
    });

    [Fact]
    public void Delete_TakesGamesOutOfTheCollection_ButNeverRemovesGames() => Sta(() =>
    {
        var library = CreateLibrary(null, "Alpha", "Beta");
        library.ApplyCollections(Cards(library, "Alpha", "Beta"), ["Co-op"], []);

        library.DeleteCollection("Co-op");

        Assert.Equal(2, library.Games.Count);
        Assert.Empty(Card(library, "Alpha").Game.Collections);
        Assert.False(library.HasCollections);
    });

    [Fact]
    public void ASavedCollectionChip_IsRestored_WhenItsGamesStillExist() => Sta(() =>
    {
        var settings = new AppSettings { LastCollectionFilter = "Co-op" };
        _alphaCollection = "Co-op";
        var library = CreateLibrary(settings, "Alpha", "Beta");

        Assert.Equal("Co-op", library.SelectedCollection);
        Assert.Equal(["Alpha"], Shown(library));
    });

    [Fact]
    public void ASavedCollectionChip_WithNoGames_FallsBackToAll() => Sta(() =>
    {
        var settings = new AppSettings { LastCollectionFilter = "Gone" };
        var library = CreateLibrary(settings, "Alpha", "Beta");

        Assert.Equal(string.Empty, library.SelectedCollection);
        Assert.Equal(["Alpha", "Beta"], Shown(library));
    });

    // ------------------------------------------------------------------ show category tabs

    [Fact]
    public void CategoryTabsOff_KeepsAllFavoritesAndHidden_AndDropsTheRealCategories() => Sta(() =>
    {
        var settings = new AppSettings { ShowCategoryTabs = false };
        var library = CreateLibrary(settings, "Alpha", "Gamma");
        Card(library, "Alpha").Game.IsHidden = true;
        library.RebuildCategories();

        Assert.Equal([LibraryConstants.AllCategory, LibraryConstants.FavoritesCategory, LibraryConstants.HiddenCategory],
            library.CategoryTabs.Select(t => t.Name));
        // The categories are still known, for Change Category's suggestions.
        Assert.Contains("RPG", library.Categories);
    });

    [Fact]
    public void CategoryTabsOff_ResetsASelectedCategory_ToAll() => Sta(() =>
    {
        var settings = new AppSettings();
        var library = CreateLibrary(settings, "Alpha", "Gamma");
        library.SelectedCategory = "RPG";

        settings.ShowCategoryTabs = false;
        library.RebuildCategories();

        Assert.Equal(LibraryConstants.AllCategory, library.SelectedCategory);
        Assert.Equal(["Alpha", "Gamma"], Shown(library));
    });

    [Fact]
    public void CategoryTabsOn_ListsEveryCategory() => Sta(() =>
    {
        var library = CreateLibrary(null, "Alpha", "Gamma");

        Assert.Contains(library.CategoryTabs, t => t.Name == "RPG");
        Assert.Contains(library.CategoryTabs, t => t.Name == "Action");
    });

    // ------------------------------------------------------------------ the stored form

    // Through StorageService, so through the source-generated AppJsonContext the app really uses.
    [Fact]
    public void Collections_SurviveSavingAndLoading_AndAnOldFileHasNone()
    {
        var storage = new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"));
        storage.SaveGames([new GameEntry { Id = "g1", Name = "Alpha", Collections = ["Co-op", "Kids"] }]);

        var back = Assert.Single(storage.LoadGames());
        Assert.Equal(["Co-op", "Kids"], back.Collections);

        File.WriteAllText(Path.Combine(storage.BaseDirectory, "games.json"), """[{"Id":"g2","Name":"Old","ExecutablePath":"C:\\Old\\old.exe"}]""");
        var old = Assert.Single(storage.LoadGames());
        Assert.Empty(old.Collections);
    }

    [Fact]
    public void AToolsCollections_SurviveSavingAndLoading()
    {
        var storage = new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"));
        storage.SaveTools([new ToolEntry { Id = "t1", Name = "Discord", TargetPath = @"C:\Tools\Discord.exe", StartWithGames = true, OnlyForCollections = ["Voice", "Co-op"] }]);

        var back = Assert.Single(storage.LoadTools());

        Assert.Equal(["Voice", "Co-op"], back.OnlyForCollections);
    }

    // A launch reads a game's collections on its own thread, so they are replaced, never edited in place.
    [Fact]
    public void ChangingCollections_ReplacesTheList_SoAReaderHoldingTheOldOneIsSafe() => Sta(() =>
    {
        var library = CreateLibrary(null, "Alpha", "Beta");
        library.ApplyCollections(Cards(library, "Alpha", "Beta"), ["Co-op"], []);
        var before = Card(library, "Alpha").Game.Collections;

        library.ApplyCollections(Cards(library, "Alpha"), ["Kids"], []);
        var afterApply = Card(library, "Alpha").Game.Collections;
        library.RenameCollection("Co-op", "Family");
        var afterRename = Card(library, "Alpha").Game.Collections;
        library.DeleteCollection("Family");

        Assert.Equal(["Co-op"], before);
        Assert.NotSame(before, afterApply);
        Assert.Equal(["Co-op", "Kids"], afterApply);
        Assert.Equal(["Family", "Kids"], afterRename);
        Assert.Equal(["Kids"], Card(library, "Alpha").Game.Collections);
    });

    [Fact]
    public void ARenameOntoAnotherSpellingOfTheSameName_DoesNotLeaveADuplicate() => Sta(() =>
    {
        var library = CreateLibrary(null, "Alpha");
        // A hand-edited games.json can hold both spellings.
        Card(library, "Alpha").Game.Collections = ["Co-op", "co-op", "Kids"];

        Assert.Null(library.RenameCollection("Co-op", "Family"));

        Assert.Equal(["Family", "Kids"], Card(library, "Alpha").Game.Collections);
    });

    // ------------------------------------------------------------------ tools limited to a collection

    private static GameEntry GameIn(params string[] collections) => new() { Id = Guid.NewGuid().ToString("N"), Name = "G", Collections = collections.ToList() };

    [Fact]
    public void AToolWithNoCollections_AppliesToEveryGame()
    {
        var tool = new ToolEntry { StartWithGames = true };

        Assert.True(ToolCatalog.AppliesToGame(tool, GameIn()));
        Assert.True(ToolCatalog.AppliesToGame(tool, GameIn("Co-op")));
    }

    [Fact]
    public void AToolLimitedToCollections_AppliesOnlyToGamesInOne()
    {
        var tool = new ToolEntry { StartWithGames = true, OnlyForCollections = ["Voice", "Co-op"] };

        Assert.True(ToolCatalog.AppliesToGame(tool, GameIn("co-op")));
        Assert.True(ToolCatalog.AppliesToGame(tool, GameIn("Kids", "Voice")));
        Assert.False(ToolCatalog.AppliesToGame(tool, GameIn("Kids")));
        Assert.False(ToolCatalog.AppliesToGame(tool, GameIn()));
    }

    [Fact]
    public void StartForGame_StartsALimitedTool_OnlyForGamesInItsCollection()
    {
        var started = new List<string>();
        var discord = new ToolEntry { Id = "d", Name = "Discord", TargetPath = @"C:\Tools\Discord.exe", StartWithGames = true, OnlyForCollections = ["Voice"] };
        var afterburner = new ToolEntry { Id = "a", Name = "Afterburner", TargetPath = @"C:\Tools\ab.exe", StartWithGames = true };
        var service = new CompanionToolService(() => [discord, afterburner], () => true)
        {
            FileExists = _ => true,
            FindRunningCopy = _ => null,
            FindRunningCopies = _ => [],
            StartProcess = tool => { started.Add(tool.Name); return Process.GetCurrentProcess(); },
            FindStartedBy = _ => Array.Empty<HeldProcess>(),
        };

        service.StartForGame(GameIn("Voice"), remember: false);
        Assert.Equal(["Discord", "Afterburner"], started.OrderBy(n => n == "Discord" ? 0 : 1));

        started.Clear();
        service.StartForGame(GameIn("Solo"), remember: false);
        Assert.Equal(["Afterburner"], started);
    }

    [Fact]
    public void StartForGame_ClosesALimitedTool_OnlyForGamesInItsCollection()
    {
        var ended = new List<string>();
        var discord = new ToolEntry { Id = "d", Name = "Discord", TargetPath = @"C:\Tools\Discord.exe", CloseForGames = true, OnlyForCollections = ["Solo"] };
        var service = new CompanionToolService(() => [discord], () => true)
        {
            FileExists = _ => true,
            FindRunningCopy = _ => null,
            FindRunningCopies = _ => [Environment.ProcessId],
            StartProcess = _ => Process.GetCurrentProcess(),
            FindStartedBy = _ => Array.Empty<HeldProcess>(),
            EndCopy = (targets, _) => { ended.Add(targets[0].Name); return CompanionToolService.EndResult.Ended; },
            CloseGrace = TimeSpan.FromMilliseconds(50),
            Wait = _ => { },
        };

        service.StartForGame(GameIn("Voice"), remember: false);
        Assert.Empty(ended);

        service.StartForGame(GameIn("Solo"), remember: false);
        Assert.Single(ended);
    }

    private string ToolFile(string name)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Fact]
    public void AToolWithNullLists_ActsAsNoLimit_AndAGameWithNullCollectionsIsInNone()
    {
        var game = new GameEntry { Name = "G", Collections = null! };

        Assert.True(ToolCatalog.AppliesToGame(new ToolEntry { OnlyForCollections = null! }, game));
        Assert.False(ToolCatalog.AppliesToGame(new ToolEntry { OnlyForCollections = ["Voice"] }, game));
    }

    [Fact]
    public void RenamingACollection_UpdatesTheToolsLimitedToIt_AndLeavesOthersAlone()
    {
        WpfTestHost.Run(() =>
        {
            var storage = new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"));
            var tools = new ToolsViewModel(storage, new IconExtractorService(storage), new ShortcutService(), new ToolLauncherService(), new XboxScannerService(), new AppSettings());
            var limited = new ToolEntry { Name = "Discord", TargetPath = @"C:\Tools\Discord.exe", StartWithGames = true, OnlyForCollections = ["Voice", "co-op"] };
            var unlimited = new ToolEntry { Name = "Afterburner", TargetPath = @"C:\Tools\ab.exe", StartWithGames = true };
            tools.ReplaceToolsForCapture([limited, unlimited]);
            var held = limited.OnlyForCollections;

            tools.RenameCollection("Co-op", "Family");

            Assert.Equal(["Voice", "Family"], limited.OnlyForCollections);
            Assert.NotSame(held, limited.OnlyForCollections);
            Assert.Empty(unlimited.OnlyForCollections);
            Assert.Equal(["Voice", "Family"], storage.LoadTools().Single(t => t.Name == "Discord").OnlyForCollections);
        });
    }

    [Fact]
    public void DeletingACollection_DropsItFromTools_AndTurnsOffOneThatHadOnlyThat()
    {
        WpfTestHost.Run(() =>
        {
            var storage = new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"));
            var tools = new ToolsViewModel(storage, new IconExtractorService(storage), new ShortcutService(), new ToolLauncherService(), new XboxScannerService(), new AppSettings());
            var only = new ToolEntry { Name = "Discord", TargetPath = @"C:\Tools\Discord.exe", StartWithGames = true, CloseAfterGames = true, OnlyForCollections = ["Co-op"] };
            var two = new ToolEntry { Name = "Afterburner", TargetPath = @"C:\Tools\ab.exe", StartWithGames = true, OnlyForCollections = ["Co-op", "Voice"] };
            var other = new ToolEntry { Name = "Vortex", TargetPath = @"C:\Tools\v.exe", StartWithGames = true, OnlyForCollections = ["Voice"] };
            tools.ReplaceToolsForCapture([only, two, other]);

            Assert.Equal(["Discord", "Afterburner"], tools.ToolsLimitedTo("co-op"));
            tools.DeleteCollection("Co-op");

            Assert.Empty(only.OnlyForCollections);
            Assert.False(only.StartWithGames);
            Assert.False(only.CloseAfterGames);
            Assert.Equal(["Voice"], two.OnlyForCollections);
            Assert.True(two.StartWithGames);
            Assert.Equal(["Voice"], other.OnlyForCollections);
            Assert.True(other.StartWithGames);
            Assert.False(storage.LoadTools().Single(t => t.Name == "Discord").StartWithGames);
        });
    }

    [Fact]
    public void DeletingACollection_TellsTools() => Sta(() =>
    {
        var library = CreateLibrary(null, "Alpha");
        library.ApplyCollections(Cards(library, "Alpha"), ["Co-op"], []);
        string? told = null;
        library.CollectionDeleted += n => told = n;

        library.DeleteCollection("Co-op");

        Assert.Equal("Co-op", told);
    });

    [Fact]
    public void TakingOutACollectionsLastGame_TellsToolsItEnded() => Sta(() =>
    {
        var library = CreateLibrary(null, "Alpha", "Beta");
        library.ApplyCollections(Cards(library, "Alpha", "Beta"), ["Co-op"], []);
        var told = new List<string>();
        library.CollectionDeleted += told.Add;

        library.ApplyCollections(Cards(library, "Alpha"), [], ["Co-op"]);
        Assert.Empty(told); // Beta is still in it

        library.ApplyCollections(Cards(library, "Beta"), [], ["Co-op"]);
        Assert.Equal(["Co-op"], told);
    });

    [Fact]
    public void AToolCardsLabel_SaysWhichCollectionsItIsLimitedTo()
    {
        WpfTestHost.Run(() =>
        {
            var storage = new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"));
            var tools = new ToolsViewModel(storage, new IconExtractorService(storage), new ShortcutService(), new ToolLauncherService(), new XboxScannerService(), new AppSettings());
            var starts = new ToolEntry { Name = "Discord", TargetPath = @"C:\Tools\Discord.exe", StartWithGames = true, OnlyForCollections = ["Voice", "Co-op"] };
            var closes = new ToolEntry { Name = "OneDrive", TargetPath = @"C:\Tools\OneDrive.exe", CloseForGames = true };
            tools.ReplaceToolsForCapture([starts, closes]);

            var startCard = tools.Tools.Single(c => c.Name == "Discord");
            var closeCard = tools.Tools.Single(c => c.Name == "OneDrive");

            Assert.Equal("Starts with games in Voice, Co-op", startCard.WithGamesLabel);
            Assert.Equal("Discord, starts with games in Voice, Co-op", startCard.AccessibleName);
            Assert.Equal("Closes for games", closeCard.WithGamesLabel);
            Assert.Equal("OneDrive, closes for games", closeCard.AccessibleName);
        });
    }

    [Fact]
    public void EditTool_Save_KeepsTheCollections_WhenTheToolStartsWithGames() => Sta(() =>
    {
        var tool = new ToolEntry { Name = "Discord", TargetPath = ToolFile("Discord.exe") };
        var storage = new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"));
        var vm = new ToolEditViewModel(tool, [], new IconExtractorService(storage)) { StartWithGames = true };
        Assert.Equal("No limit: every game", vm.CollectionsSummary);

        vm.OnlyForCollections = ["Voice"];
        Assert.Equal("Only games in: Voice", vm.CollectionsSummary);
        Assert.True(vm.HasUnsavedChanges);
        vm.SaveCommand.Execute(null);

        Assert.Equal(["Voice"], tool.OnlyForCollections);
    });

    [Fact]
    public void EditTool_Save_DropsTheCollections_WhenNoActionIsChosen() => Sta(() =>
    {
        var tool = new ToolEntry { Name = "Discord", TargetPath = ToolFile("Discord.exe"), StartWithGames = true, OnlyForCollections = ["Voice"] };
        var storage = new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"));
        var vm = new ToolEditViewModel(tool, [], new IconExtractorService(storage));
        Assert.True(vm.HasGameAction);

        vm.LeaveAlone = true;
        Assert.False(vm.HasGameAction);
        vm.SaveCommand.Execute(null);

        Assert.Empty(tool.OnlyForCollections);
    });
}


/// <summary>A game added by hand while a collection chip is selected starts in that collection.</summary>
[Collection(InstallIndexCollection.Name)]
public class CollectionImportTests : IDisposable
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
        foreach (var game in games) library.Games.Add(library.CreateCardViewModel(game));
        library.RebuildCategories();
        var import = new ImportCoordinator(
            library, new ShortcutService(), icons, new FolderScannerService(),
            steamScanner, gogScanner, eaScanner, epicScanner, ubisoftScanner, xboxScanner, battleNetScanner,
            steamSearch, steamMetadata, storage, settings,
            getSteamGridDbApiKeyOrNull: () => null);
        return (library, import);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnAddedGame_StartsInTheSelectedCollection_OrInNone(bool chipSelected)
    {
        string exe = Path.Combine(_root, "games", "New Game", "game.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllBytes(exe, [0x4D, 0x5A]);

        // Adding a game recaptures the app-wide installed-games index in the background; put the old one back.
        var indexBefore = InstalledGameIndex.Current;
        try
        {
            await WpfTestHost.RunAsync(async () =>
            {
                var settings = new AppSettings { SearchOfficialTitleOnline = false, AutoCategorizeFromSteam = false, UseVerticalPosterArt = false };
                var existing = new GameEntry { Name = "Old", ExecutablePath = Path.Combine(_root, "old.exe"), Collections = ["Co-op"] };
                var (library, import) = NewLibrary(settings, existing);
                if (chipSelected) library.SelectCollectionTab("Co-op");

                await import.AddCandidateAsync(new GameCandidate("New Game", exe, Path.GetDirectoryName(exe)!, 2));
                await library.HeavyStateLoad;

                var added = library.Games.Single(c => c.Name != "Old").Game;
                Assert.Equal(chipSelected ? new[] { "Co-op" } : Array.Empty<string>(), added.Collections);
            });
        }
        finally
        {
            InstalledGameIndex.Current = indexBefore;
        }
    }
}
