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

    /// <summary>
    /// Every platform's default category name. The single source of truth for "is this category
    /// just the platform's placeholder" - used by Steam-genre enrichment (a placeholder may be
    /// replaced by a real genre; a user-chosen category never is) and by the card's category
    /// badge (a placeholder is redundant next to the platform badge). Previously three separate
    /// hard-coded lists that each had to grow by one clause per platform.
    /// </summary>
    public static readonly IReadOnlySet<string> PlatformCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        SteamCategory, GogCategory, EaCategory, EpicCategory, UbisoftCategory, XboxCategory
    };

    /// <summary>True when Steam metadata enrichment may overwrite <paramref name="category"/> with
    /// a genre: it's still Uncategorized or a platform placeholder, not something the user picked.</summary>
    public static bool IsEnrichableCategory(string? category)
        => string.IsNullOrWhiteSpace(category)
           || string.Equals(category, Uncategorized, StringComparison.OrdinalIgnoreCase)
           || PlatformCategories.Contains(category);

    /// <summary>The placeholder category an entry gets from its platform tag, or null for a Local game.</summary>
    public static string? PlatformCategoryFor(GameEntry game)
        => game.IsGogGame ? GogCategory
         : game.IsEaGame ? EaCategory
         : game.IsEpicGame ? EpicCategory
         : game.IsUbisoftGame ? UbisoftCategory
         : game.IsXboxGame ? XboxCategory
         : game.HasSteamOverlay ? SteamCategory
         : null;
}
