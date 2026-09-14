using System;
using System.Collections.Generic;

namespace TrayTrigger.Models;

/// <summary>
/// The one rule for turning a source's genre list into the library category, shared by
/// <see cref="SteamAppDetails.PrimaryGenre"/> and <see cref="RawgGameDetails.PrimaryGenre"/> so the
/// same game is filed the same way whichever source answered. "Indie" is a scale, not a kind of
/// game, so it is skipped when anything else is listed (Steam lists it first for most indie
/// titles: "Indie, RPG, Simulation"). When a source lists no genre the result is empty and nothing
/// is invented - every caller guards on empty.
/// </summary>
public static class GenreRules
{
    public static string PrimaryOf(IReadOnlyList<string> genres)
    {
        foreach (var g in genres)
        {
            if (!string.Equals(g, "Indie", StringComparison.OrdinalIgnoreCase))
                return g;
        }
        return genres.Count > 0 ? genres[0] : string.Empty;
    }
}
