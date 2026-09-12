using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// The toolbar's filter flyout: OR within a group, AND across groups, and the rules that keep a
/// saved filter from reading as a lost library.
/// </summary>
public class LibraryFilterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerFilterTests", Guid.NewGuid().ToString("N"));

    /// <summary>Card view models touch WPF input state, which needs an STA thread with a real
    /// dispatcher - see WpfTestHost. Without it these tests throw rather than pass vacuously.</summary>
    private static void Sta(Action body) => WpfTestHost.Run(body);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private LibraryViewModel NewLibrary(AppSettings? settings = null, params GameEntry[] games)
    {
        var storage = new StorageService(Path.Combine(_root, Guid.NewGuid().ToString("N")), Path.Combine(_root, "local"));
        var launcher = new ProcessLauncherService(
            storage, new PerformanceProfileService(storage), new GameScriptService(),
            new SteamScannerService(), new GogScannerService(), new EaScannerService(),
            new EpicScannerService(), new UbisoftScannerService(), new XboxScannerService());

        var library = new LibraryViewModel(
            storage, new IconExtractorService(storage), launcher, new HotkeyManager(),
            new SteamMetadataService(), new SteamSearchService(), settings ?? new AppSettings(),
            getUseVerticalPosterArt: () => true,
            getSteamGridDbApiKeyOrNull: () => null);

        foreach (var game in games)
        {
            library.Games.Add(library.CreateCardViewModel(game));
        }
        library.RebuildCategories();
        return library;
    }

    private static GameEntry Steam(string name) => new() { Name = name, IsSteamGame = true, ImportedFrom = LauncherPlatform.Steam, ExecutablePath = "steam://rungameid/1" };
    private static GameEntry Epic(string name) => new() { Name = name, IsEpicGame = true, ImportedFrom = LauncherPlatform.Epic, ExecutablePath = "com.epicgames.launcher://apps/x?action=launch" };
    private static GameEntry Local(string name) => new() { Name = name, ExecutablePath = @"C:\Games\" + name + @"\game.exe" };

    private static LibraryFilterOption Option(LibraryFilterViewModel filter, string key) =>
        filter.Groups.SelectMany(g => g.Options).Single(o => o.Key == key);

    private static List<string> Visible(LibraryViewModel library) =>
        library.FilteredGames.Cast<GameCardViewModel>().Select(c => c.Name).ToList();

    [Fact]
    public void NoTicks_HidesNothing() => Sta(() =>
    {
        var library = NewLibrary(null, Steam("A"), Epic("B"), Local("C"));
        Assert.False(library.Filter.HasActiveFilters);
        Assert.Equal(3, Visible(library).Count);
    });

    [Fact]
    public void WithinAGroup_TicksAreOred() => Sta(() =>
    {
        var library = NewLibrary(null, Steam("A"), Epic("B"), Local("C"));

        Option(library.Filter, "launcher:steam").IsChecked = true;
        Assert.Equal(["A"], Visible(library));

        Option(library.Filter, "launcher:epic").IsChecked = true;
        Assert.Equal(["A", "B"], Visible(library).Order().ToList());
    });

    [Fact]
    public void AcrossGroups_TicksAreAnded() => Sta(() =>
    {
        var played = Steam("Played");
        played.LastPlayed = DateTime.Now;
        var library = NewLibrary(null, played, Steam("Fresh"), Epic("OtherStore"));

        Option(library.Filter, "launcher:steam").IsChecked = true;
        Option(library.Filter, "status:neverplayed").IsChecked = true;

        Assert.Equal(["Fresh"], Visible(library));
    });

    /// <summary>The flyout narrows within the selected tab; it does not reach past it.</summary>
    [Fact]
    public void TheCategoryTabStillWins() => Sta(() =>
    {
        var rpg = Steam("RpgOne");
        rpg.Category = "RPG";
        var library = NewLibrary(null, rpg, Steam("ActionOne"));

        library.SelectedCategory = "RPG";
        Option(library.Filter, "launcher:steam").IsChecked = true;

        Assert.Equal(["RpgOne"], Visible(library));
    });

    /// <summary>A tick box that can only ever return nothing is not offered.</summary>
    [Fact]
    public void LauncherOptions_OnlyListLaunchersTheLibraryHas() => Sta(() =>
    {
        var library = NewLibrary(null, Steam("A"), Local("C"));
        var keys = library.Filter.Groups.Single(g => g.Title == "Launcher").Options.Select(o => o.Key).ToList();

        Assert.Contains("launcher:steam", keys);
        Assert.Contains("launcher:local", keys);
        Assert.DoesNotContain("launcher:epic", keys);
        Assert.DoesNotContain("launcher:xbox", keys);
    });

    /// <summary>
    /// Entries added before ImportedFrom existed only carry the per-platform bool, and a filter
    /// that missed them would quietly hide part of an older library.
    /// </summary>
    [Fact]
    public void LegacyEntriesWithoutImportedFrom_StillClassify() => Sta(() =>
    {
        var legacy = new GameEntry { Name = "Legacy", IsGogGame = true, ExecutablePath = @"C:\GOG\g.exe" };
        Assert.Equal(LauncherPlatform.Gog, LibraryFilterViewModel.PlatformOf(legacy));
        Assert.Null(LibraryFilterViewModel.PlatformOf(new GameEntry { Name = "Plain" }));
    });

    [Fact]
    public void TicksSurviveARestart() => Sta(() =>
    {
        var settings = new AppSettings();
        var first = NewLibrary(settings, Steam("A"), Epic("B"));
        Option(first.Filter, "launcher:steam").IsChecked = true;
        Option(first.Filter, "status:neverplayed").IsChecked = true;

        Assert.Contains("launcher:steam", settings.LibraryFilterKeys);
        Assert.Contains("status:neverplayed", settings.LibraryFilterKeys);

        // Same settings object, fresh view model - what the next launch does.
        var second = NewLibrary(settings, Steam("A"), Epic("B"));
        Assert.True(second.Filter.HasActiveFilters);
        Assert.True(Option(second.Filter, "launcher:steam").IsChecked);
        Assert.Equal(["A"], Visible(second));
    });

    /// <summary>
    /// A saved filter is invisible once the flyout closes, so the empty state has to say so - the
    /// alternative is a library that looks like it lost its games.
    /// </summary>
    [Fact]
    public void FilteredToNothing_IsDistinguishableFromAnEmptyLibrary() => Sta(() =>
    {
        var library = NewLibrary(null, Steam("A"), Steam("B"));
        Assert.False(library.IsEmptyBecauseOfFilters);

        Option(library.Filter, "status:missing").IsChecked = true;

        Assert.Empty(Visible(library));
        Assert.True(library.HasAnyGames);
        Assert.True(library.IsEmptyBecauseOfFilters);

        library.ClearFilters();
        Assert.False(library.IsEmptyBecauseOfFilters);
        Assert.Equal(2, Visible(library).Count);
    });

    [Fact]
    public void ClearRemovesEveryTickAndTheSavedKeys() => Sta(() =>
    {
        var settings = new AppSettings();
        var library = NewLibrary(settings, Steam("A"), Epic("B"));
        Option(library.Filter, "launcher:steam").IsChecked = true;
        Option(library.Filter, "profile:off").IsChecked = true;
        Assert.Equal(2, library.Filter.ActiveFilterCount);

        library.ClearFilters();

        Assert.False(library.Filter.HasActiveFilters);
        Assert.Empty(settings.LibraryFilterKeys);
        Assert.Equal(2, Visible(library).Count);
    });

    /// <summary>
    /// A launcher tick whose launcher is no longer in the library must not survive as an invisible
    /// filter - there would be no box on screen to untick.
    /// </summary>
    [Fact]
    public void ATickForALauncherNoLongerPresent_IsDropped() => Sta(() =>
    {
        var settings = new AppSettings { LibraryFilterKeys = ["launcher:xbox"] };
        var library = NewLibrary(settings, Steam("A"));

        Assert.False(library.Filter.HasActiveFilters);
        Assert.Equal(["A"], Visible(library));
    });
}
