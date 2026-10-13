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
            new EpicScannerService(), new UbisoftScannerService(), new XboxScannerService(), new BattleNetScannerService());

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

    /// <summary>
    /// The badge carries a number, so the number has to be right: it counts ticked values, not
    /// groups with something ticked.
    /// </summary>
    [Fact]
    public void TheCount_IsTickedValuesNotGroups() => Sta(() =>
    {
        var library = NewLibrary(null, Steam("A"), Epic("B"), Local("C"));
        Assert.Equal(0, library.Filter.ActiveFilterCount);

        Option(library.Filter, "launcher:steam").IsChecked = true;
        Option(library.Filter, "launcher:epic").IsChecked = true;
        Assert.Equal(2, library.Filter.ActiveFilterCount);

        Option(library.Filter, "profile:off").IsChecked = true;
        Assert.Equal(3, library.Filter.ActiveFilterCount);
        Assert.Contains("3 filters active", library.Filter.AccessibleName);
    });

    /// <summary>
    /// Clear is a reset, not a close: every tick goes and the flyout stays open, so the user can
    /// pick a different filter without reopening it.
    /// </summary>
    [Fact]
    public void Clear_ResetsFilters_AndLeavesTheFlyoutOpen() => Sta(() =>
    {
        var library = NewLibrary(null, Steam("A"), Epic("B"));
        Option(library.Filter, "launcher:steam").IsChecked = true;
        library.Filter.IsOpen = true;

        library.Filter.ClearCommand.Execute(null);

        Assert.True(library.Filter.IsOpen);
        Assert.False(library.Filter.HasActiveFilters);
        Assert.Equal(2, Visible(library).Count);
    });

    /// <summary>
    /// A library of nothing but Steam games leaves the Launcher group with one box whose only
    /// effect would be to hide nothing. It is hidden, and being hidden it must not filter or count
    /// either - otherwise a tick restored from settings would hide games with no box to untick.
    /// </summary>
    [Fact]
    public void AGroupDownToOneOption_IsHiddenAndInert() => Sta(() =>
    {
        var settings = new AppSettings { LibraryFilterKeys = ["launcher:steam"] };
        var library = NewLibrary(settings, Steam("A"), Steam("B"));

        var launchers = library.Filter.Groups.Single(g => g.Title == "Launcher");
        Assert.Single(launchers.Options);
        Assert.False(launchers.IsVisible);

        Assert.False(library.Filter.HasActiveFilters);
        Assert.Equal(0, library.Filter.ActiveFilterCount);
        Assert.Equal(2, Visible(library).Count);
    });

    /// <summary>Two launchers is enough to be worth offering.</summary>
    [Fact]
    public void AGroupWithTwoOptions_IsShown() => Sta(() =>
    {
        var library = NewLibrary(null, Steam("A"), Epic("B"));

        var launchers = library.Filter.Groups.Single(g => g.Title == "Launcher");
        Assert.Equal(2, launchers.Options.Count);
        Assert.True(launchers.IsVisible);
    });

    // --- Groups: how ticks combine ---

    /// <summary>The settings flags are independent yes/no facts, so ticking two means both.</summary>
    [Fact]
    public void SettingsGroup_IsAllOf() => Sta(() =>
    {
        var both = Local("Both"); both.Hotkey = "Ctrl+1"; both.PreLaunchScriptPath = "a.ps1";
        var hotkeyOnly = Local("HotkeyOnly"); hotkeyOnly.Hotkey = "Ctrl+2";
        var neither = Local("Neither");
        var library = NewLibrary(null, both, hotkeyOnly, neither);

        var group = library.Filter.Groups.Single(g => g.Title == "TrayTrigger settings");
        Assert.True(group.MatchAll);

        Option(library.Filter, "status:hotkey").IsChecked = true;
        Assert.Equal(["Both", "HotkeyOnly"], Visible(library).Order().ToList());

        Option(library.Filter, "status:scripts").IsChecked = true;
        Assert.Equal(["Both"], Visible(library));
    });

    [Fact]
    public void ValueGroups_AreAnyOf() => Sta(() =>
    {
        var library = NewLibrary(null, Steam("A"), Epic("B"));
        foreach (string title in new[] { "Launcher", "Last played", "Performance Profile", "Needs attention" })
        {
            var group = library.Filter.Groups.Single(g => g.Title == title);
            Assert.False(group.MatchAll);
        }
    });

    // --- The new options ---

    [Fact]
    public void LastPlayedOptions_FollowTheClock() => Sta(() =>
    {
        var recent = Local("Recent"); recent.LastPlayed = DateTime.UtcNow.AddDays(-2);
        var old = Local("Old"); old.LastPlayed = DateTime.UtcNow.AddDays(-400);
        var never = Local("Never");
        var library = NewLibrary(null, recent, old, never);

        Option(library.Filter, "status:played30d").IsChecked = true;
        Assert.Equal(["Recent"], Visible(library));

        Option(library.Filter, "status:played30d").IsChecked = false;
        Option(library.Filter, "status:stale6m").IsChecked = true;
        Assert.Equal(["Old"], Visible(library));

        // Any of: a stale backlog is never played or not touched in half a year.
        Option(library.Filter, "status:neverplayed").IsChecked = true;
        Assert.Equal(["Never", "Old"], Visible(library).Order().ToList());
    });

    [Fact]
    public void SettingsOptions_FindWhatIsSet() => Sta(() =>
    {
        var dlss = Local("Dlss"); dlss.DlssSettings.Add(new DlssSettingRecord { Feature = "SR" });
        var cores = Local("Cores"); cores.CpuAffinity = CpuAffinityMode.Auto;
        var hdr = Local("Hdr"); hdr.Hdr = HdrMode.On;
        var plain = Local("Plain");
        var library = NewLibrary(null, dlss, cores, hdr, plain);

        void Only(string key, string expected)
        {
            library.Filter.ClearAll();
            Option(library.Filter, key).IsChecked = true;
            Assert.Equal([expected], Visible(library));
        }
        Only("setting:dlss", "Dlss");
        Only("setting:cpucores", "Cores");
        Only("setting:hdr", "Hdr");
    });

    [Fact]
    public void NeedsAttentionOptions_FindWhatIsUnfinished() => Sta(() =>
    {
        var clean = Local("Clean"); clean.CoverImagePath = "poster.png"; clean.SteamAppId = "1"; clean.Category = "RPG";
        var bare = Local("Bare");
        var library = NewLibrary(null, clean, bare);

        foreach (string key in new[] { "status:noposter", "status:nomatch" })
        {
            library.Filter.ClearAll();
            Option(library.Filter, key).IsChecked = true;
            Assert.Equal(["Bare"], Visible(library));
        }
    });

    /// <summary>An option the PC can't use is left out: a stranded tick must not filter or count.</summary>
    [Fact]
    public void UnavailableOption_NeitherFiltersNorCounts() => Sta(() =>
    {
        var library = NewLibrary(null, Local("A"), Local("B"));
        var dlss = Option(library.Filter, "setting:dlss");

        dlss.IsAvailable = true;
        dlss.IsChecked = true;
        Assert.Empty(Visible(library));
        Assert.Equal(1, library.Filter.ActiveFilterCount);

        dlss.IsAvailable = false;
        library.FilteredGames.Refresh();
        Assert.Equal(2, Visible(library).Count);
        Assert.False(library.Filter.HasActiveFilters);
        Assert.Equal(0, library.Filter.ActiveFilterCount);
    });

    [Fact]
    public void SavedOldKeys_StillRestore() => Sta(() =>
    {
        // "status:favorite" lived in the Status group; it sits in TrayTrigger settings now, under the same key.
        var settings = new AppSettings { LibraryFilterKeys = ["status:favorite"] };
        var fav = Local("Fav"); fav.IsFavorite = true;
        var library = NewLibrary(settings, fav, Local("Other"));

        Assert.True(Option(library.Filter, "status:favorite").IsChecked);
        Assert.Equal(["Fav"], Visible(library));
    });

    // --- Saved filters ---

    [Fact]
    public void SavedFilter_IsSavedApplied_AndReadsAsEditedOnceChanged() => Sta(() =>
    {
        var settings = new AppSettings();
        var played = Steam("Played"); played.LastPlayed = DateTime.UtcNow.AddDays(-1);
        var library = NewLibrary(settings, Steam("Unplayed"), played, Epic("OtherLauncher"));
        var filter = library.Filter;

        Assert.False(filter.SaveAs("Nothing ticked"));
        Option(filter, "launcher:steam").IsChecked = true;
        Option(filter, "status:neverplayed").IsChecked = true;
        Assert.True(filter.SaveAs("  Unplayed   Steam games "));

        var saved = Assert.Single(filter.SavedFilters);
        Assert.Equal("Unplayed Steam games", saved.Name);
        Assert.True(saved.IsSelected);
        Assert.False(saved.IsEdited);
        Assert.Equal("Unplayed Steam games", Assert.Single(settings.SavedLibraryFilters).Name);

        filter.ClearAll();
        Assert.False(saved.IsSelected);
        Assert.Equal(3, Visible(library).Count);

        filter.ApplySaved(saved);
        Assert.Equal(["Unplayed"], Visible(library));
        Assert.True(saved.IsSelected);

        Option(filter, "status:neverplayed").IsChecked = false;
        Assert.False(saved.IsSelected);
        Assert.True(saved.IsEdited);

        // Saving under the same name updates it rather than adding a second one.
        Assert.True(filter.SaveAs("unplayed steam games"));
        Assert.Single(filter.SavedFilters);
        Assert.True(filter.SavedFilters[0].IsSelected);
        Assert.Equal(["launcher:steam"], filter.SavedFilters[0].Keys);
    });

    [Fact]
    public void SavedFilter_IsKept_AcrossARestart_AndHonoursAMissingLauncher() => Sta(() =>
    {
        var settings = new AppSettings();
        var first = NewLibrary(settings, Steam("A"), Epic("B"));
        Option(first.Filter, "launcher:epic").IsChecked = true;
        Option(first.Filter, "status:noposter").IsChecked = true;
        first.Filter.SaveAs("Epic without posters");

        // A later run with no Epic games: the saved Epic tick has nothing to attach to and is skipped.
        var second = NewLibrary(settings, Steam("A"), Steam("C"));
        var saved = Assert.Single(second.Filter.SavedFilters);
        second.Filter.ApplySaved(saved);
        Assert.DoesNotContain("launcher:epic", second.Filter.ActiveKeys());
        Assert.True(Option(second.Filter, "status:noposter").IsChecked);
        Assert.True(saved.IsSelected);
    });

    [Fact]
    public void SavedFilter_Delete_AsksFirst() => Sta(() =>
    {
        var settings = new AppSettings();
        var library = NewLibrary(settings, Steam("A"), Epic("B"));
        var filter = library.Filter;
        Option(filter, "launcher:steam").IsChecked = true;
        filter.SaveAs("Steam");
        var saved = filter.SavedFilters[0];

        filter.ConfirmDelete = _ => false;
        saved.DeleteCommand.Execute(null);
        Assert.Single(filter.SavedFilters);
        Assert.Single(settings.SavedLibraryFilters);

        string? asked = null;
        filter.ConfirmDelete = name => { asked = name; return true; };
        saved.DeleteCommand.Execute(null);
        Assert.Equal("Steam", asked);
        Assert.Empty(filter.SavedFilters);
        Assert.Empty(settings.SavedLibraryFilters);
        Assert.False(filter.HasSavedFilters);
    });

    // --- "Not" ticks: the third state of a box ---

    [Fact]
    public void ThreeStateBox_CyclesOffTickedNotOff() => Sta(() =>
    {
        var library = NewLibrary(null, Steam("A"), Epic("B"));
        var hotkey = Option(library.Filter, "status:hotkey");

        Assert.False(hotkey.State);
        Assert.Equal("Has a hotkey", hotkey.DisplayLabel);
        hotkey.State = true;
        Assert.True(hotkey.IsChecked);
        Assert.False(hotkey.IsExcluded);
        Assert.Equal(1, library.Filter.ActiveFilterCount);

        hotkey.State = null;
        Assert.False(hotkey.IsChecked);
        Assert.True(hotkey.IsExcluded);
        Assert.Null(hotkey.State);
        Assert.Equal("No hotkey", hotkey.DisplayLabel);
        Assert.Equal(1, library.Filter.ActiveFilterCount);

        hotkey.State = false;
        Assert.False(hotkey.IsActive);
        Assert.False(library.Filter.HasActiveFilters);
    });

    [Fact]
    public void ANotTick_LeavesOutWhatMatches() => Sta(() =>
    {
        var scriptsOnly = Local("ScriptsOnly"); scriptsOnly.PreLaunchScriptPath = "a.ps1";
        var both = Local("Both"); both.PreLaunchScriptPath = "a.ps1"; both.Hotkey = "Ctrl+1";
        var neither = Local("Neither");
        var library = NewLibrary(null, scriptsOnly, both, neither);

        Option(library.Filter, "status:hotkey").IsExcluded = true;
        Assert.Equal(["Neither", "ScriptsOnly"], Visible(library).Order().ToList());

        // Has launch scripts, and no hotkey.
        Option(library.Filter, "status:scripts").IsChecked = true;
        Assert.Equal(["ScriptsOnly"], Visible(library));
    });

    [Fact]
    public void ANotTick_AnswersDlssOverrideOff() => Sta(() =>
    {
        var withDlss = Local("WithDlss"); withDlss.DlssSettings.Add(new DlssSettingRecord { Feature = "SR" }); withDlss.Hotkey = "Ctrl+1";
        var without = Local("Without"); without.Hotkey = "Ctrl+2";
        var plain = Local("Plain");
        var library = NewLibrary(null, withDlss, without, plain);

        Option(library.Filter, "setting:dlss").IsExcluded = true;
        Assert.Equal(["Plain", "Without"], Visible(library).Order().ToList());

        // In the all-of group: has a hotkey, and no DLSS Override.
        Option(library.Filter, "status:hotkey").IsChecked = true;
        Assert.Equal(["Without"], Visible(library));
    });

    [Fact]
    public void AGroupWithOnlyNotTicks_OnlyRemoves() => Sta(() =>
    {
        var keys = Local("Keys"); keys.Hotkey = "Ctrl+1";
        var library = NewLibrary(null, keys, Local("Plain"));

        Option(library.Filter, "status:hotkey").IsExcluded = true;
        Assert.Equal(["Plain"], Visible(library));
    });

    [Fact]
    public void NotTicks_AreSaved_Restored_AndSavedWithAFilter() => Sta(() =>
    {
        var settings = new AppSettings();
        var keys = Local("Keys"); keys.Hotkey = "Ctrl+1";
        var first = NewLibrary(settings, keys, Local("Plain"));
        // DLSS Override is only offered on a PC with an NVIDIA driver, which a CI runner lacks.
        Option(first.Filter, "setting:dlss").IsAvailable = true;
        Option(first.Filter, "status:hotkey").IsExcluded = true;
        Option(first.Filter, "setting:dlss").IsExcluded = true;
        Assert.Equal(["!setting:dlss", "!status:hotkey"], first.Filter.ActiveKeys().Order().ToList());

        first.Filter.SaveAs("No hotkey, no DLSS");
        Assert.Equal(["!setting:dlss", "!status:hotkey"], settings.SavedLibraryFilters[0].Keys.Order().ToList());

        // A restart restores the ticks, the "not" ones too.
        var restored = NewLibrary(new AppSettings { LibraryFilterKeys = first.Filter.ActiveKeys() }, keys, Local("Plain"));
        Assert.True(Option(restored.Filter, "status:hotkey").IsExcluded);
        Assert.True(Option(restored.Filter, "setting:dlss").IsExcluded);
        Assert.Equal(["Plain"], Visible(restored));

        // Applying the saved filter puts the same "not" ticks back.
        first.Filter.ClearAll();
        first.Filter.ApplySaved(first.Filter.SavedFilters[0]);
        Assert.True(Option(first.Filter, "status:hotkey").IsExcluded);
        Assert.True(first.Filter.SavedFilters[0].IsSelected);
    });

    [Fact]
    public void ANotTickOnAnUnavailableOption_IsInert() => Sta(() =>
    {
        var library = NewLibrary(null, Local("A"), Local("B"));
        var dlss = Option(library.Filter, "setting:dlss");
        dlss.IsAvailable = true;
        dlss.IsExcluded = true;
        Assert.Equal(2, Visible(library).Count);

        var withDlss = Local("C"); withDlss.DlssSettings.Add(new DlssSettingRecord { Feature = "SR" });
        library.Games.Add(library.CreateCardViewModel(withDlss));
        library.FilteredGames.Refresh();
        Assert.Equal(2, Visible(library).Count);

        dlss.IsAvailable = false;
        library.FilteredGames.Refresh();
        Assert.Equal(3, Visible(library).Count);
        Assert.False(library.Filter.HasActiveFilters);
    });

    /// <summary>A choice between values has no "not": ticking the others says the same thing.</summary>
    [Fact]
    public void NotIsOnlyForYesNoOptions() => Sta(() =>
    {
        var library = NewLibrary(null, Steam("A"), Epic("B"));
        var steam = Option(library.Filter, "launcher:steam");
        Assert.False(steam.AllowExclude);
        steam.IsExcluded = true;
        Assert.False(steam.IsExcluded);
        steam.State = null;
        Assert.False(steam.IsExcluded);
        Assert.False(steam.IsActive);

        // An old or hand-edited key that asks for one is ignored, not half-applied.
        var restored = NewLibrary(new AppSettings { LibraryFilterKeys = ["!launcher:steam", "!status:neverplayed"] }, Steam("A"), Epic("B"));
        Assert.False(restored.Filter.HasActiveFilters);
        Assert.Equal(2, Visible(restored).Count);

        Assert.True(Option(library.Filter, "setting:dlss").AllowExclude);
        Assert.Equal("DLSS Override off", Option(library.Filter, "setting:dlss").NegatedLabel);
    });

    [Fact]
    public void TheNarrowedOptionSet_HasNoRedundantOptions() => Sta(() =>
    {
        var keys = NewLibrary(null, Steam("A"), Epic("B")).Filter.Groups.SelectMany(g => g.Options).Select(o => o.Key).ToList();
        Assert.DoesNotContain("status:played", keys);
        Assert.DoesNotContain("setting:closelauncher", keys);
        Assert.DoesNotContain("status:uncategorized", keys);
    });

    [Fact]
    public void AGroup_CanBeClearedOnItsOwn() => Sta(() =>
    {
        var library = NewLibrary(null, Steam("A"), Epic("B"));
        var filter = library.Filter;
        var launchers = filter.Groups.Single(g => g.Title == "Launcher");
        Assert.False(launchers.HasChecked);

        Option(filter, "launcher:steam").IsChecked = true;
        Option(filter, "status:neverplayed").IsChecked = true;
        Assert.True(launchers.HasChecked);

        launchers.ClearCommand.Execute(null);
        Assert.False(launchers.HasChecked);
        Assert.False(Option(filter, "launcher:steam").IsChecked);
        Assert.True(Option(filter, "status:neverplayed").IsChecked);
        Assert.Equal(1, filter.ActiveFilterCount);
    });

    [Fact]
    public void ResultSummary_SaysWhatTheTicksDid() => Sta(() =>
    {
        var hidden = Steam("Hidden"); hidden.IsHidden = true;
        var library = NewLibrary(null, Steam("Zorblax"), Steam("B"), Epic("C"), hidden);
        var filter = library.Filter;

        // Nothing ticked: just how many games are in view. The hidden one is not.
        Assert.Equal("3 games", filter.ResultSummary);

        Option(filter, "launcher:steam").IsChecked = true;
        Assert.Equal("Showing 2 of 3", filter.ResultSummary);

        // The total follows the search box, which the flyout works inside.
        library.SearchText = "Zorblax";
        Assert.Equal("Showing 1 of 1", filter.ResultSummary);

        library.SearchText = string.Empty;
        Option(filter, "launcher:steam").IsChecked = false;
        Option(filter, "status:hotkey").IsChecked = true;
        Assert.Equal("Showing 0 of 3", filter.ResultSummary);
    });

    [Fact]
    public void SaveCurrent_UnderAnExistingName_AsksBeforeReplacing() => Sta(() =>
    {
        var settings = new AppSettings();
        var library = NewLibrary(settings, Steam("A"), Epic("B"));
        var filter = library.Filter;
        Option(filter, "launcher:steam").IsChecked = true;
        filter.SaveAs("Mine");

        Option(filter, "status:hotkey").IsExcluded = true;
        filter.PromptForName = () => "mine";
        string? asked = null, saved = null, now = null;

        filter.ConfirmReplace = (name, was, will) => { asked = name; saved = was; now = will; return false; };
        filter.SaveCurrentCommand.Execute(null);
        Assert.Equal("Mine", asked);
        Assert.Equal("Steam", saved);
        Assert.Equal("Steam, No hotkey", now);
        Assert.Equal(["launcher:steam"], settings.SavedLibraryFilters[0].Keys);

        filter.ConfirmReplace = (_, _, _) => true;
        filter.SaveCurrentCommand.Execute(null);
        Assert.Single(filter.SavedFilters);
        Assert.Equal(["launcher:steam", "!status:hotkey"], settings.SavedLibraryFilters[0].Keys.Order(StringComparer.Ordinal).Reverse().ToList());
    });

    /// <summary>The replace question can't be dodged by spacing or by a name that is cut short.</summary>
    [Fact]
    public void SaveCurrent_ReplaceQuestion_IgnoresSpacingAndLength() => Sta(() =>
    {
        var library = NewLibrary(new AppSettings(), Steam("A"), Epic("B"));
        var filter = library.Filter;
        Option(filter, "launcher:steam").IsChecked = true;
        filter.SaveAs("Unplayed Steam games");

        Option(filter, "launcher:epic").IsChecked = true;
        int asked = 0;
        filter.ConfirmReplace = (_, _, _) => { asked++; return false; };

        filter.PromptForName = () => "  unplayed    STEAM   games ";
        filter.SaveCurrentCommand.Execute(null);
        Assert.Equal(1, asked);
        Assert.Equal(["launcher:steam"], filter.SavedFilters.Single().Keys);

        // Forty characters is the limit: a longer name that is cut back to an existing one is that one.
        filter.SaveAs(new string('x', 40));
        filter.PromptForName = () => new string('x', 55);
        filter.SaveCurrentCommand.Execute(null);
        Assert.Equal(2, asked);
        Assert.Equal(2, filter.SavedFilters.Count);
    });

    /// <summary>A name cut at the limit never ends on half an emoji, which would show as a broken character.</summary>
    [Fact]
    public void ASavedName_IsNotCutInTheMiddleOfAnEmoji() => Sta(() =>
    {
        var library = NewLibrary(new AppSettings(), Steam("A"), Epic("B"));
        var filter = library.Filter;
        Option(filter, "launcher:steam").IsChecked = true;

        // 39 letters, then an emoji whose two halves straddle the 40-character limit.
        Assert.True(filter.SaveAs(new string('a', 39) + "\U0001F3AE" + "more"));

        string name = filter.SavedFilters.Single().Name;
        Assert.Equal(new string('a', 39), name);
        Assert.False(char.IsSurrogate(name[^1]));
    });

    /// <summary>A tick the PC doesn't offer filters nothing, so it must not make a saved filter read as edited.</summary>
    [Fact]
    public void SavedFilter_IgnoresATickOnAnOptionThePcDoesNotOffer() => Sta(() =>
    {
        var library = NewLibrary(new AppSettings(), Steam("A"), Epic("B"));
        var filter = library.Filter;
        var dlss = Option(filter, "setting:dlss");
        dlss.IsAvailable = false;
        dlss.IsChecked = true;

        Option(filter, "launcher:steam").IsChecked = true;
        Assert.True(filter.SaveAs("Steam"));

        Assert.Equal(["launcher:steam"], filter.SavedFilters[0].Keys);
        Assert.True(filter.SavedFilters[0].IsSelected);
        Assert.False(filter.SavedFilters[0].IsEdited);
    });

    /// <summary>Clearing one state must not quietly clear the other.</summary>
    [Fact]
    public void SettingOneState_LeavesTheOtherAlone() => Sta(() =>
    {
        var library = NewLibrary(null, Local("A"), Local("B"));
        var hotkey = Option(library.Filter, "status:hotkey");

        hotkey.IsExcluded = true;
        hotkey.IsChecked = false;
        Assert.True(hotkey.IsExcluded);

        hotkey.IsChecked = true;
        Assert.True(hotkey.IsChecked);
        Assert.False(hotkey.IsExcluded);

        hotkey.IsExcluded = false;
        Assert.True(hotkey.IsChecked);
    });

    /// <summary>The total is only worked out while the flyout can show it.</summary>
    [Fact]
    public void ResultSummary_IsOnlyRaisedWhileTheFlyoutIsOpen() => Sta(() =>
    {
        var library = NewLibrary(null, Steam("A"), Epic("B"));
        var filter = library.Filter;
        var raised = new List<string?>();
        filter.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Option(filter, "launcher:steam").IsChecked = true;
        Assert.DoesNotContain(nameof(LibraryFilterViewModel.ResultSummary), raised);

        raised.Clear();
        filter.IsOpen = true;
        Assert.Contains(nameof(LibraryFilterViewModel.ResultSummary), raised);

        raised.Clear();
        Option(filter, "launcher:epic").IsChecked = true;
        Assert.Contains(nameof(LibraryFilterViewModel.ResultSummary), raised);
    });

    /// <summary>Saved filters and the Game Details size live in settings.json: they have to survive a save and a load.</summary>
    [Fact]
    public void SavedFilters_AndTheDetailsSize_SurviveAnotherRun()
    {
        var storage = new StorageService(Path.Combine(_root, "roundtrip"), Path.Combine(_root, "roundtrip-local"));
        storage.SaveSettings(new AppSettings
        {
            GameDetailsWidth = 940,
            GameDetailsHeight = 710,
            SavedLibraryFilters = [new SavedLibraryFilter { Name = "No DLSS", Keys = ["!setting:dlss", "launcher:steam"] }],
        });

        var loaded = new StorageService(Path.Combine(_root, "roundtrip"), Path.Combine(_root, "roundtrip-local")).LoadSettings();
        Assert.Equal(940, loaded.GameDetailsWidth);
        Assert.Equal(710, loaded.GameDetailsHeight);
        var saved = Assert.Single(loaded.SavedLibraryFilters);
        Assert.Equal("No DLSS", saved.Name);
        Assert.Equal(["!setting:dlss", "launcher:steam"], saved.Keys);
    }

    [Fact]
    public void SaveCurrent_AsksForAName_AndCancelSavesNothing() => Sta(() =>
    {
        var library = NewLibrary(null, Steam("A"), Epic("B"));
        var filter = library.Filter;
        Option(filter, "launcher:steam").IsChecked = true;

        filter.PromptForName = () => null;
        filter.SaveCurrentCommand.Execute(null);
        Assert.Empty(filter.SavedFilters);

        filter.PromptForName = () => "Steam only";
        filter.SaveCurrentCommand.Execute(null);
        Assert.Equal("Steam only", Assert.Single(filter.SavedFilters).Name);

        filter.ClearAll();
        Assert.False(filter.SaveCurrentCommand.CanExecute(null));
    });
}
