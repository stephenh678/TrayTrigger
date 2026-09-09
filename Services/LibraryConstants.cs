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
}
