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
    public const string Uncategorized = "Uncategorized";
    public const string SteamCategory = "Steam";

    /// <summary>
    /// Fallback tray item count used when a persisted MaxRecentInTray/MaxFavoritesInTray value
    /// is zero or negative (e.g. pre-migration data). Matches AppSettings' own default for both
    /// - the two fallbacks had drifted to 3 (Recent) and 3 (Favorites) while AppSettings itself
    /// defaults both to 5 and App.xaml.cs's own Favorites fallback was already 5. See L-20.
    /// </summary>
    public const int DefaultTrayItemCount = 5;
}
