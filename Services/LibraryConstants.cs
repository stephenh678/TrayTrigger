using System;
using System.Collections.Generic;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// The category-name sentinels compared as string literals throughout the library UI/ViewModels
/// (the pseudo-categories "All" and "Favorites", and the "Uncategorized"/"Steam" default category
/// names). Centralized here so they can't drift out of sync between call sites. See L-15.
/// </summary>
public static class LibraryConstants
{
    public const string AllCategory = "All";
    public const string FavoritesCategory = "Favorites";
    public const string HiddenCategory = "Hidden";
    public const string Uncategorized = "Uncategorized";
    public const string SteamCategory = "Steam";
    public const string GogCategory = "GOG";
    public const string EaCategory = "EA";
    public const string EpicCategory = "Epic";
    public const string UbisoftCategory = "Ubisoft";
    public const string XboxCategory = "Xbox";
    public const string BattleNetCategory = "Battle.net";
    public const string RockstarCategory = "Rockstar";
    public const string AmazonCategory = "Amazon";

    /// <summary>True when metadata enrichment may overwrite <paramref name="game"/>'s category with a genre.</summary>
    public static bool IsEnrichableCategory(GameEntry game) => IsEnrichableCategory(game.Category, game);

    /// <summary>
    /// True when metadata enrichment may overwrite <paramref name="category"/> with a genre: it's still
    /// Uncategorized, or the placeholder of <paramref name="game"/>'s own platform ("GOG" on a GOG game) - not
    /// something the user picked. Only the game's own platform counts: a Local game filed under "Amazon" by hand,
    /// before Amazon Games had an integration, keeps the category, though "Amazon" is a placeholder for Amazon games.
    /// </summary>
    public static bool IsEnrichableCategory(string? category, GameEntry game)
        => string.IsNullOrWhiteSpace(category)
           || string.Equals(category, Uncategorized, StringComparison.OrdinalIgnoreCase)
           || string.Equals(category, PlatformCategoryFor(game), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The category to store for what a user typed or picked: trimmed, and <see cref="Uncategorized"/>
    /// when it is blank or one of the library views (All, Favorites, Hidden) - those are tabs, not
    /// categories, and a game filed under one of them had no tab of its own to appear in.
    /// </summary>
    public static string NormalizeCategory(string? category)
    {
        string trimmed = category?.Trim() ?? string.Empty;
        if (trimmed.Length == 0
            || string.Equals(trimmed, AllCategory, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, FavoritesCategory, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, HiddenCategory, StringComparison.OrdinalIgnoreCase))
        {
            return Uncategorized;
        }
        return trimmed;
    }

    /// <summary>The placeholder category an entry gets from its platform tag, or null for a Local game.</summary>
    public static string? PlatformCategoryFor(GameEntry game)
        => game.IsGogGame ? GogCategory
         : game.IsEaGame ? EaCategory
         : game.IsEpicGame ? EpicCategory
         : game.IsUbisoftGame ? UbisoftCategory
         : game.IsXboxGame ? XboxCategory
         : game.IsBattleNetGame ? BattleNetCategory
         : game.IsRockstarGame ? RockstarCategory
         : game.IsAmazonGame ? AmazonCategory
         : game.HasSteamOverlay ? SteamCategory
         : null;
}
