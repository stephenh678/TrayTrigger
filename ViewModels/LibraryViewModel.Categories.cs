using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.Views;

namespace TrayTrigger.ViewModels;

/// <summary>The category tabs, sorting and the library filter.</summary>
public partial class LibraryViewModel
{
    public void RebuildCategories()
    {
        string previous = SelectedCategory;
        Categories.Clear();
        Categories.Add(LibraryConstants.AllCategory);
        Categories.Add(LibraryConstants.FavoritesCategory);

        // Excludes hidden games - a category only hidden games belong to would otherwise still
        // show as a tab (since FilterGameItem excludes hidden games from every non-Hidden tab),
        // leaving a clickable tab that always renders empty.
        var distinctCategories = Games
            .Where(g => !g.Game.IsHidden)
            .Select(g => g.Category)
            .Where(c => !string.IsNullOrWhiteSpace(c) && c != LibraryConstants.AllCategory && c != LibraryConstants.FavoritesCategory && c != LibraryConstants.HiddenCategory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c);

        foreach (var cat in distinctCategories)
        {
            Categories.Add(cat);
        }

        // Hidden is a filter, not a real category, so it's pinned last rather than sorted in
        // alphabetically among actual categories - same treatment as All/Favorites up front.
        if (Games.Any(g => g.Game.IsHidden))
        {
            Categories.Add(LibraryConstants.HiddenCategory);
        }

        // Categories are deduped case-insensitively above, so the kept spelling can differ from
        // the selected one ("RPG" vs "rpg"); match the same way.
        string? kept = Categories.FirstOrDefault(c => string.Equals(c, previous, StringComparison.OrdinalIgnoreCase));
        if (kept != null)
        {
            _selectedCategory = kept;
        }
        else
        {
            _selectedCategory = LibraryConstants.AllCategory;
            _settings.LastCategoryFilter = LibraryConstants.AllCategory;
            FilteredGames.Refresh();
        }
        OnPropertyChanged(nameof(SelectedCategory));

        RebuildCategoryTabs();
        // Same trigger points as the category tabs, and for the same reason: the Launcher options
        // only list launchers the library actually contains. The refresh afterwards is not
        // optional: this is where a launcher tick saved from the last run is re-applied (its
        // option did not exist until now), and where one whose launcher has left the library is
        // dropped. Either changes what should be on screen.
        Filter.RebuildLauncherOptions(Games);
        FilteredGames.Refresh();
        OnPropertyChanged(nameof(IsEmptyBecauseOfFilters));
    }

    public void RebuildCategoryTabs()
    {
        CategoryTabs.Clear();
        foreach (var cat in Categories)
        {
            string display = cat == LibraryConstants.AllCategory ? "All Games" : cat == LibraryConstants.FavoritesCategory ? LibraryConstants.FavoritesCategory : cat;
            bool isSelected = string.Equals(cat, SelectedCategory, StringComparison.OrdinalIgnoreCase);
            CategoryTabs.Add(new CategoryTabItem(cat, display, isSelected, SelectCategoryTab));
        }
    }

    public void SelectCategoryTab(string category)
    {
        SelectedCategory = category;
    }

    public void ApplySort()
    {
        FilteredGames.SortDescriptions.Clear();
        switch (SelectedSortOption)
        {
            case "Alphabetical (Z - A)":
                FilteredGames.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Descending));
                break;
            case "Favorites First (A - Z)":
                FilteredGames.SortDescriptions.Add(new SortDescription("Game.IsFavorite", ListSortDirection.Descending));
                FilteredGames.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
                break;
            case "Favorites First (Z - A)":
                FilteredGames.SortDescriptions.Add(new SortDescription("Game.IsFavorite", ListSortDirection.Descending));
                FilteredGames.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Descending));
                break;
            case SortFavoritesFirstRecent:
                FilteredGames.SortDescriptions.Add(new SortDescription("Game.IsFavorite", ListSortDirection.Descending));
                FilteredGames.SortDescriptions.Add(new SortDescription("Game.LastPlayed", ListSortDirection.Descending));
                FilteredGames.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
                break;
            case "Most Recently Played":
                FilteredGames.SortDescriptions.Add(new SortDescription("Game.LastPlayed", ListSortDirection.Descending));
                FilteredGames.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
                break;
            case "Cumulative Playtime":
                FilteredGames.SortDescriptions.Add(new SortDescription("Game.CumulativePlaytimeMinutes", ListSortDirection.Descending));
                FilteredGames.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
                break;
            case "Alphabetical (A - Z)":
            default:
                FilteredGames.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
                break;
        }
        FilteredGames.Refresh();
    }

    private bool FilterGameItem(object obj) => obj is GameCardViewModel card && MatchesView(card, applyFlyout: true);

    /// <summary>
    /// What the flyout's total counts: the games the current tab and search would show, and how many of
    /// those survive its ticks.
    /// </summary>
    private (int Shown, int InView) CountForFilterSummary()
    {
        int shown = 0, inView = 0;
        foreach (var card in Games)
        {
            if (!MatchesView(card, applyFlyout: false)) continue;
            inView++;
            if (Filter.Matches(card)) shown++;
        }
        return (shown, inView);
    }

    private bool MatchesView(GameCardViewModel card, bool applyFlyout)
    {
        // "Hidden" is its own filter tab, like Favorites, rather than a separate toggle - a
        // hidden game only ever shows there, and every other tab (including All) excludes it.
        // The record itself is untouched either way, so scans never treat it as new.
        if (SelectedCategory == LibraryConstants.HiddenCategory)
        {
            if (!card.Game.IsHidden) return false;
        }
        else
        {
            if (card.Game.IsHidden) return false;

            // Category filter
            if (SelectedCategory == LibraryConstants.FavoritesCategory)
            {
                if (!card.Game.IsFavorite) return false;
            }
            else if (SelectedCategory != LibraryConstants.AllCategory &&
                !card.Category.Equals(SelectedCategory, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // Flyout filters: launcher, performance profile, per-entry state. AND'd with the tab and
        // the search box, so they narrow what those already chose rather than reaching past them.
        if (applyFlyout && !Filter.Matches(card)) return false;

        // Search text filter
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            return card.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                   card.Category.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                   card.Game.ExecutablePath.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }
}
