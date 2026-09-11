using System.IO;
using System.Runtime.ExceptionServices;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// Multi-select and batch editing in <see cref="LibraryViewModel"/>. The view model is built
/// for real, against a <see cref="StorageService"/> scoped to a temp folder so nothing touches
/// the user's library, and on an STA thread because it owns WPF collection views and the
/// hotkey listener's message-only window.
/// </summary>
public class LibraryViewModelSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Runs the body on an STA thread and rethrows anything it throws.</summary>
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

    private LibraryViewModel CreateLibrary(params string[] gameNames)
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
            new UbisoftScannerService());
        var settings = new AppSettings();
        var library = new LibraryViewModel(
            storage,
            new IconExtractorService(storage),
            launcher,
            new HotkeyManager(),
            new SteamMetadataService(),
            new SteamSearchService(),
            settings,
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
        return library;
    }

    private static GameCardViewModel Card(LibraryViewModel library, string name) =>
        library.Games.Single(c => c.Name == name);

    [Fact]
    public void CtrlClick_TogglesCards_AndHintAppearsAtTwo() => Sta(() =>
    {
        var library = CreateLibrary("Alpha", "Bravo", "Charlie");

        Card(library, "Alpha").ToggleSelectCommand.Execute(null);
        Assert.Equal(1, library.SelectedCount);
        Assert.True(library.HasSelection);
        Assert.Equal(string.Empty, library.SelectionHint);

        Card(library, "Bravo").ToggleSelectCommand.Execute(null);
        Assert.Equal(2, library.SelectedCount);
        Assert.Equal("2 selected - right-click one for batch actions", library.SelectionHint);

        Card(library, "Alpha").ToggleSelectCommand.Execute(null);
        Assert.Equal(1, library.SelectedCount);
        Assert.False(Card(library, "Alpha").IsSelected);
        Assert.True(Card(library, "Bravo").IsSelected);
    });

    [Fact]
    public void ShiftClick_SelectsRangeFromAnchor() => Sta(() =>
    {
        var library = CreateLibrary("Alpha", "Bravo", "Charlie", "Delta");

        Card(library, "Bravo").ToggleSelectCommand.Execute(null);
        Card(library, "Delta").RangeSelectCommand.Execute(null);

        Assert.False(Card(library, "Alpha").IsSelected);
        Assert.True(Card(library, "Bravo").IsSelected);
        Assert.True(Card(library, "Charlie").IsSelected);
        Assert.True(Card(library, "Delta").IsSelected);
        Assert.Equal(3, library.SelectedCount);
    });

    [Fact]
    public void SelectAll_SkipsHiddenGames_AndFilterChangeClears() => Sta(() =>
    {
        var library = CreateLibrary("Alpha", "Bravo", "Charlie");
        Card(library, "Charlie").Game.IsHidden = true;
        library.RebuildCategories();
        library.FilteredGames.Refresh();

        library.SelectAllVisible();
        Assert.Equal(2, library.SelectedCount);
        Assert.False(Card(library, "Charlie").IsSelected);

        library.SearchText = "zzz";
        Assert.Equal(0, library.SelectedCount);
        Assert.False(library.HasSelection);
    });

    [Fact]
    public void PlainClick_ClearsSelection() => Sta(() =>
    {
        var library = CreateLibrary("Alpha", "Bravo");
        library.SelectAllVisible();
        Assert.Equal(2, library.SelectedCount);

        library.ClearSelection();
        Assert.Equal(0, library.SelectedCount);
        Assert.All(library.Games, c => Assert.False(c.IsSelected));
    });

    [Fact]
    public void BatchFavorite_AddsThenRemoves_WithFlippingLabel() => Sta(() =>
    {
        var library = CreateLibrary("Alpha", "Bravo", "Charlie");
        Card(library, "Alpha").ToggleSelectCommand.Execute(null);
        Card(library, "Bravo").ToggleSelectCommand.Execute(null);

        Assert.Equal("Add to Favorites", library.BatchFavoriteLabel);
        Assert.False(library.BatchAllFavorite);

        library.BatchFavoriteCommand.Execute(null);
        Assert.True(Card(library, "Alpha").Game.IsFavorite);
        Assert.True(Card(library, "Bravo").Game.IsFavorite);
        Assert.False(Card(library, "Charlie").Game.IsFavorite);
        Assert.Equal("Remove from Favorites", library.BatchFavoriteLabel);
        Assert.True(library.BatchAllFavorite);
        Assert.Equal(2, library.SelectedCount); // favorites keep the selection

        library.BatchFavoriteCommand.Execute(null);
        Assert.False(Card(library, "Alpha").Game.IsFavorite);
        Assert.False(Card(library, "Bravo").Game.IsFavorite);
    });

    [Fact]
    public void BatchHide_HidesAll_ClearsSelection_AndLeavesTheAllTab() => Sta(() =>
    {
        var library = CreateLibrary("Alpha", "Bravo", "Charlie");
        Card(library, "Alpha").ToggleSelectCommand.Execute(null);
        Card(library, "Bravo").ToggleSelectCommand.Execute(null);
        Assert.Equal("Hide", library.BatchHideLabel);

        library.BatchHideCommand.Execute(null);

        Assert.True(Card(library, "Alpha").Game.IsHidden);
        Assert.True(Card(library, "Bravo").Game.IsHidden);
        Assert.False(Card(library, "Charlie").Game.IsHidden);
        Assert.Equal(0, library.SelectedCount);
        Assert.Single(library.FilteredGames.Cast<GameCardViewModel>());
        Assert.Contains(LibraryConstants.HiddenCategory, library.Categories);
    });

    [Fact]
    public void ApplyCategoryToMany_SetsCategory_AndRegistersIt() => Sta(() =>
    {
        var library = CreateLibrary("Alpha", "Bravo", "Charlie");
        var cards = new List<GameCardViewModel> { Card(library, "Alpha"), Card(library, "Charlie") };

        library.ApplyCategoryToMany(cards, "  Strategy ");

        Assert.Equal("Strategy", Card(library, "Alpha").Category);
        Assert.Equal("Strategy", Card(library, "Charlie").Category);
        Assert.Equal("Action", Card(library, "Bravo").Category);
        Assert.Contains("Strategy", library.Categories);
    });

    [Fact]
    public void BatchProfile_SetsTier_AndChecksOnlyWhenShared() => Sta(() =>
    {
        var library = CreateLibrary("Alpha", "Bravo");
        library.SelectAllVisible();
        Assert.True(library.BatchProfileIsOff);

        library.BatchSetProfileCommand.Execute(PerformanceProfileMode.Aggressive);
        Assert.All(library.Games, c => Assert.Equal(PerformanceProfileMode.Aggressive, c.Game.PerformanceProfile));
        Assert.True(library.BatchProfileIsAggressive);
        Assert.False(library.BatchProfileIsOff);

        Card(library, "Alpha").Game.PerformanceProfile = PerformanceProfileMode.Optimized;
        library.SetCardSelected(Card(library, "Alpha"), true); // re-evaluates
        Assert.Null(library.BatchProfile);
        Assert.False(library.BatchProfileIsAggressive);
        Assert.False(library.BatchProfileIsOptimized);
    });

    [Fact]
    public void BatchRemove_RemovesConfirmedCards_AndUndoRestoresOrder() => Sta(() =>
    {
        var library = CreateLibrary("Alpha", "Bravo", "Charlie", "Delta");
        List<GameCardViewModel>? asked = null;
        library.ConfirmBatchRemove = cards => { asked = cards; return true; };
        Card(library, "Alpha").ToggleSelectCommand.Execute(null);
        Card(library, "Charlie").ToggleSelectCommand.Execute(null);

        library.BatchRemoveCommand.Execute(null);

        Assert.NotNull(asked);
        Assert.Equal(2, asked!.Count);
        Assert.Equal(new[] { "Bravo", "Delta" }, library.Games.Select(c => c.Name));
        Assert.True(library.IsUndoToastVisible);
        Assert.Equal(0, library.SelectedCount);
        Assert.True(File.Exists(Path.Combine(_root, "roaming", "games.json")));

        library.UndoDelete();
        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie", "Delta" }, library.Games.Select(c => c.Name));
        Assert.False(library.IsUndoToastVisible);
    });

    [Fact]
    public void BatchRemove_DeclinedConfirmation_ChangesNothing() => Sta(() =>
    {
        var library = CreateLibrary("Alpha", "Bravo");
        library.ConfirmBatchRemove = _ => false;
        library.SelectAllVisible();

        library.BatchRemoveCommand.Execute(null);

        Assert.Equal(2, library.Games.Count);
        Assert.Equal(2, library.SelectedCount);
        Assert.False(library.IsUndoToastVisible);
    });
}
