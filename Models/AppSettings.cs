namespace TrayTrigger.Models;

public class AppSettings
{
    public bool StartWithWindows { get; set; } = false;
    public bool StartMinimizedToTray { get; set; } = true;
    public bool GroupTrayMenuByCategory { get; set; } = true;
    public bool AlwaysShowTrayIcon { get; set; } = true;
    public bool SteamIntegrationEnabled { get; set; } = true;
    public bool VerboseLoggingEnabled { get; set; } = false;
    public bool IsSidebarExpanded { get; set; } = false;
    public string GlobalManageHotkey { get; set; } = "Ctrl+Alt+G";
    public string LastCategoryFilter { get; set; } = "All";
    public string LastSortOption { get; set; } = "Alphabetical (A - Z)";
    public bool ShowRecentInTray { get; set; } = true;
    public int MaxRecentInTray { get; set; } = 5;
    public string RecentTraySortOption { get; set; } = "Most Recently Played";
    public bool ShowFavoritesInTray { get; set; } = true;
    public int MaxFavoritesInTray { get; set; } = 5;
    public string FavoritesTraySortOption { get; set; } = "Alphabetical (A - Z)";
    public string TrayMenuSortOption { get; set; } = "Alphabetical (A - Z)";
    public bool PreferExeForGameName { get; set; } = true;
    public bool SearchOfficialTitleOnline { get; set; } = true;
    public double OnlineMatchConfidenceThreshold { get; set; } = 0.60;
    public bool AutoCategorizeFromSteam { get; set; } = true;
    public bool UseVerticalPosterArt { get; set; } = true;
    public bool UseSteamGridDbArt { get; set; } = false;
    public string SteamGridDbApiKey { get; set; } = string.Empty;
    public string LibraryViewMode { get; set; } = "Poster Grid";
    public bool MinimizeOnGameLaunch { get; set; } = true;
    public bool AutoCheckForUpdates { get; set; } = true;
    public string GitHubRepository { get; set; } = "stephenh678/TrayTrigger";
    public bool HasSeenSteamGridDbPrompt { get; set; } = false;
    public string? SkippedUpdateVersion { get; set; }
    public System.DateTime? RemindAfterUtc { get; set; }
}

