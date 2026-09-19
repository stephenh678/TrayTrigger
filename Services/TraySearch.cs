using System;
using System.Collections.Generic;
using System.Linq;

namespace TrayTrigger.Services;

/// <summary>
/// What the tray menu's search box lists for a query (UX-16). Pure, so it can be unit tested.
/// Matches name and category, case-insensitively, the fields the Library search uses minus the
/// path (in a menu a path match would look like a wrong result). Best matches first: a name that
/// starts with the query, then a name that contains it, then a category-only match, each
/// alphabetical. Enter launches the first row, so the order matters.
/// </summary>
internal static class TraySearch
{
    /// <summary>Game rows shown at most; a menu is not a results page.</summary>
    public const int MaxGames = 10;
    /// <summary>Tool rows shown at most, after the games.</summary>
    public const int MaxTools = 5;

    public static List<T> Match<T>(IEnumerable<T> items, Func<T, string> name, Func<T, string> category, string? query, int max)
    {
        string q = query?.Trim() ?? string.Empty;
        if (q.Length == 0 || max <= 0) return [];

        return items
            .Select(item => (Item: item, Name: name(item) ?? string.Empty, Category: category(item) ?? string.Empty))
            .Select(x => (x.Item, x.Name, Rank:
                x.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 :
                x.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ? 1 :
                x.Category.Contains(q, StringComparison.OrdinalIgnoreCase) ? 2 : -1))
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(x => x.Item)
            .ToList();
    }
}
