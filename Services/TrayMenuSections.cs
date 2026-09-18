using System;
using System.Collections.Generic;
using System.Linq;

namespace TrayTrigger.Services;

/// <summary>Which games go in which section of the tray menu. Pure, so it can be unit tested.</summary>
internal static class TrayMenuSections
{
    /// <summary>
    /// The IDs for the Recent section, most recently played first: games that have been played,
    /// minus any the Favorites section is already showing, up to <paramref name="max"/>. The
    /// favorites are removed before the cap is applied, so Recent still fills up with the next
    /// most recent games rather than coming up short.
    /// </summary>
    public static List<string> SelectRecent(
        IEnumerable<(string Id, DateTime? LastPlayed)> games,
        IEnumerable<string> shownFavoriteIds,
        int max)
    {
        if (max <= 0) return [];

        var favorites = new HashSet<string>(shownFavoriteIds, StringComparer.Ordinal);
        return games
            .Where(g => g.LastPlayed.HasValue && !favorites.Contains(g.Id))
            .OrderByDescending(g => g.LastPlayed!.Value)
            .Take(max)
            .Select(g => g.Id)
            .ToList();
    }
}
