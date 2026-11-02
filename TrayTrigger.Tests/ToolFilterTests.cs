using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// The Tools page's filter flyout. The rules (any of within a group, all of for yes/no facts, "not" ticks,
/// saved filters) are the Library's and are tested with it (LibraryFilterTests); these cover the options a tool
/// has and that the flyout works on the Tools page's own list.
/// </summary>
public class ToolFilterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerToolFilterTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private ToolsViewModel NewTools(AppSettings settings, params ToolEntry[] tools)
    {
        var storage = new StorageService(Path.Combine(_root, Guid.NewGuid().ToString("N")), Path.Combine(_root, "local"));
        var vm = new ToolsViewModel(storage, new IconExtractorService(storage), new ShortcutService(), new ToolLauncherService(), new XboxScannerService(), settings);
        vm.ReplaceToolsForCapture(tools);
        return vm;
    }

    private static ToolEntry Program(string name, Action<ToolEntry>? edit = null)
    {
        var tool = new ToolEntry { Name = name, TargetPath = @"C:\Tools\" + name + ".exe" };
        edit?.Invoke(tool);
        return tool;
    }

    private static ToolEntry Script(string name) => new() { Name = name, TargetPath = @"C:\Tools\" + name + ".ps1" };
    private static ToolEntry StoreApp(string name) => new() { Name = name, AppId = "Microsoft." + name + "_8wekyb3d8bbwe!App" };

    private static FilterOption Option(ToolsViewModel vm, string key) => vm.Filter.Groups.SelectMany(g => g.Options).Single(o => o.Key == key);

    private static string[] Shown(ToolsViewModel vm) => vm.FilteredTools.Cast<ToolCardViewModel>().Select(c => c.Name).OrderBy(n => n).ToArray();

    [Fact]
    public void NothingTicked_ShowsEveryTool_AndTheButtonSaysNoFilters()
    {
        WpfTestHost.Run(() =>
        {
            var vm = NewTools(new AppSettings(), Program("Afterburner"), Script("Cleanup"), StoreApp("Xbox"));

            Assert.Equal(["Afterburner", "Cleanup", "Xbox"], Shown(vm));
            Assert.False(vm.Filter.HasActiveFilters);
            Assert.Equal("No filters", vm.Filter.ActiveFilterSummary);
            Assert.Equal("3 tools", vm.Filter.ResultSummary);
        });
    }

    [Fact]
    public void KindOptions_AreOnlyOfferedForKindsThatExist_AndTheGroupIsLeftOutForOneKind()
    {
        WpfTestHost.Run(() =>
        {
            var programsOnly = NewTools(new AppSettings(), Program("A"), Program("B"));
            var kinds = programsOnly.Filter.Groups.Single(g => g.Title == "Kind");
            Assert.False(kinds.IsVisible, "one kind of tool leaves nothing to narrow");

            var mixed = NewTools(new AppSettings(), Program("A"), StoreApp("Xbox"));
            kinds = mixed.Filter.Groups.Single(g => g.Title == "Kind");
            Assert.True(kinds.IsVisible);
            Assert.True(Option(mixed, "kind:program").IsAvailable);
            Assert.False(Option(mixed, "kind:script").IsAvailable);
            Assert.True(Option(mixed, "kind:storeapp").IsAvailable);
        });
    }

    [Fact]
    public void Kind_IsAnyOf_AndFiltersTheList()
    {
        WpfTestHost.Run(() =>
        {
            var vm = NewTools(new AppSettings(), Program("Afterburner"), Script("Cleanup"), StoreApp("Xbox"));

            Option(vm, "kind:script").IsChecked = true;
            Assert.Equal(["Cleanup"], Shown(vm));

            Option(vm, "kind:storeapp").IsChecked = true;
            Assert.Equal(["Cleanup", "Xbox"], Shown(vm));
        });
    }

    [Fact]
    public void SettingsTicks_AreAllOf_AndAnOppositeLeavesOutWhatMatches()
    {
        WpfTestHost.Run(() =>
        {
            var vm = NewTools(new AppSettings(),
                Program("Afterburner", t => { t.IsFavorite = true; t.Hotkey = "Ctrl+Alt+A"; }),
                Program("Vortex", t => t.IsFavorite = true),
                Program("Discord"));

            Option(vm, "status:favorite").IsChecked = true;
            Assert.Equal(["Afterburner", "Vortex"], Shown(vm));

            // Two facts: both must be true.
            Option(vm, "status:hotkey").IsChecked = true;
            Assert.Equal(["Afterburner"], Shown(vm));

            // The opposite of a fact: favorites with no hotkey.
            Option(vm, "status:hotkey").IsExcluded = true;
            Assert.Equal(["Vortex"], Shown(vm));
            Assert.Equal("No hotkey", Option(vm, "status:hotkey").DisplayLabel);
        });
    }

    [Fact]
    public void StartsWithGames_AndClosesForGames_FollowTheRulesTheCardsUse()
    {
        WpfTestHost.Run(() =>
        {
            var vm = NewTools(new AppSettings(),
                Program("Starter", t => t.StartWithGames = true),
                Program("Closer", t => t.CloseForGames = true),
                Program("Plain"),
                // A script can't start with games, so the box on it means nothing: it doesn't count.
                new ToolEntry { Name = "Cleanup", TargetPath = @"C:\Tools\Cleanup.ps1", StartWithGames = true });

            Option(vm, "status:startswithgames").IsChecked = true;
            Assert.Equal(["Starter"], Shown(vm));

            Option(vm, "status:startswithgames").IsChecked = false;
            Option(vm, "status:closesforgames").IsChecked = true;
            Assert.Equal(["Closer"], Shown(vm));

            Option(vm, "status:closesforgames").IsChecked = false;
            Option(vm, "status:startswithgames").IsExcluded = true;
            Assert.Equal(["Cleanup", "Closer", "Plain"], Shown(vm));
        });
    }

    [Fact]
    public void FileMissing_FindsAToolWhoseProgramHasGone()
    {
        WpfTestHost.Run(() =>
        {
            string real = Path.Combine(_root, "real.exe");
            Directory.CreateDirectory(_root);
            File.WriteAllText(real, "x");
            var vm = NewTools(new AppSettings(), new ToolEntry { Name = "Present", TargetPath = real }, Program("Gone"));

            Option(vm, "status:missing").IsChecked = true;

            Assert.Equal(["Gone"], Shown(vm));
        });
    }

    [Fact]
    public void ResultSummary_SaysHowManyTheTicksKept_OutOfWhatTheTabAndBoxShow()
    {
        WpfTestHost.Run(() =>
        {
            var vm = NewTools(new AppSettings(), Program("A", t => t.IsFavorite = true), Program("B"), Program("C"));

            Assert.Equal("3 tools", vm.Filter.ResultSummary);

            Option(vm, "status:favorite").IsChecked = true;
            vm.Filter.IsOpen = true;
            Assert.Equal("Showing 1 of 3", vm.Filter.ResultSummary);

            vm.SearchText = "B";
            Assert.Equal("Showing 0 of 1", vm.Filter.ResultSummary);
        });
    }

    [Fact]
    public void ATickThatHidesEveryTool_IsSaidToBeTheCause_AndClearingBringsThemBack()
    {
        WpfTestHost.Run(() =>
        {
            var vm = NewTools(new AppSettings(), Program("A"), Program("B"));
            Assert.False(vm.IsEmptyBecauseOfFilters);

            Option(vm, "status:favorite").IsChecked = true;

            Assert.Empty(Shown(vm));
            Assert.True(vm.IsEmptyBecauseOfFilters);

            vm.ClearFilters();

            Assert.Equal(["A", "B"], Shown(vm));
            Assert.False(vm.IsEmptyBecauseOfFilters);
        });
    }

    [Fact]
    public void AFilterThatHidesTheFavorites_IsNotReadAsNoFavoritesYet()
    {
        WpfTestHost.Run(() =>
        {
            var vm = NewTools(new AppSettings(), Program("A", t => t.IsFavorite = true), Program("B"));
            vm.SelectedCategory = LibraryConstants.FavoritesCategory;
            Assert.False(vm.ShowFavoritesEmpty);

            // The favorite has no hotkey, so "Has a hotkey" hides it: the list is empty, but not for lack of favorites.
            Option(vm, "status:hotkey").IsChecked = true;

            Assert.Empty(Shown(vm));
            Assert.False(vm.ShowFavoritesEmpty);
            Assert.True(vm.ShowNoMatches);
            Assert.True(vm.IsEmptyBecauseOfFilters);
        });
    }

    [Fact]
    public void Ticks_AreKeptBetweenRuns_AndSavedFiltersWithThem()
    {
        WpfTestHost.Run(() =>
        {
            var settings = new AppSettings();
            var first = NewTools(settings, Program("A", t => t.IsFavorite = true), Program("B"));
            Option(first, "status:favorite").IsChecked = true;
            Option(first, "status:hotkey").IsExcluded = true;
            Assert.True(first.Filter.SaveAs("Favorites without a hotkey"));

            Assert.Equal(["!status:hotkey", "status:favorite"], settings.ToolFilterKeys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            var saved = Assert.Single(settings.SavedToolFilters);
            Assert.Equal("Favorites without a hotkey", saved.Name);

            var second = NewTools(settings, Program("A", t => t.IsFavorite = true), Program("B"));
            Assert.True(Option(second, "status:favorite").IsChecked);
            Assert.True(Option(second, "status:hotkey").IsExcluded);
            Assert.Equal(["A"], Shown(second));
            Assert.Single(second.Filter.SavedFilters);
            Assert.True(second.Filter.SavedFilters[0].IsSelected);
        });
    }

    [Fact]
    public void ChangingTheFilter_ClearsTheSelection_SoNothingIsChangedUnseen()
    {
        WpfTestHost.Run(() =>
        {
            var vm = NewTools(new AppSettings(), Program("A"), Program("B"));
            int cleared = 0;
            vm.SelectionClearRequested += () => cleared++;

            Option(vm, "status:favorite").IsChecked = true;

            Assert.True(cleared >= 1);
        });
    }
}
