using System.Text.Json.Serialization;

namespace TrayTrigger.Models;

public class AppSettings
{
    /// <summary>
    /// Folders "Scan for Games" looks in for installed games - Steam library folders (auto-synced
    /// by <see cref="Services.ScanLocationService"/>) plus any the user added by hand. See
    /// <see cref="ScanLocation"/>.
    /// </summary>
    public List<ScanLocation> ScanLocations { get; set; } = new();
    /// <summary>Runs "Scan for Games" silently at startup - skips the "no scan locations" prompt if none are configured, and only surfaces the install prompt when it actually finds something.</summary>
    public bool AutoScanForGamesOnStartup { get; set; } = false;
    /// <summary>Executable paths permanently excluded from "Add Folder" and "Scan for Games" results. See <see cref="IgnoredGamePath"/>.</summary>
    public List<IgnoredGamePath> IgnoredGamePaths { get; set; } = new();

    public bool StartWithWindows { get; set; } = false;
    public bool StartMinimizedToTray { get; set; } = true;
    public bool GroupTrayMenuByCategory { get; set; } = true;
    public bool AlwaysShowTrayIcon { get; set; } = true;
    public bool SteamIntegrationEnabled { get; set; } = true;
    public bool GogIntegrationEnabled { get; set; } = true;
    public bool EaIntegrationEnabled { get; set; } = true;
    public bool EpicIntegrationEnabled { get; set; } = true;
    public bool UbisoftIntegrationEnabled { get; set; } = true;
    public bool XboxIntegrationEnabled { get; set; } = true;
    public bool VerboseLoggingEnabled { get; set; } = false;
    public bool IsSidebarExpanded { get; set; } = false;
    /// <summary>
    /// Last main-window placement (WPF device-independent units), captured while the window is
    /// in its Normal state and restored on the next launch. Null until the window has been shown
    /// once, in which case the XAML default size and CenterScreen apply. See MainWindow.
    /// </summary>
    public double? MainWindowLeft { get; set; }
    public double? MainWindowTop { get; set; }
    public double? MainWindowWidth { get; set; }
    public double? MainWindowHeight { get; set; }
    public bool MainWindowMaximized { get; set; } = false;
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
    /// <summary>User's own RAWG (rawg.io) API key for non-Steam game metadata. Encrypted on disk
    /// like <see cref="SteamGridDbApiKey"/>.</summary>
    public string RawgApiKey { get; set; } = string.Empty;
    /// <summary>Enables the RAWG metadata source (the Steam/RAWG toggle in Game Details). Off by
    /// default; needs a <see cref="RawgApiKey"/> to do anything.</summary>
    public bool UseRawgMetadata { get; set; } = false;
    /// <summary>How long cached Steam and RAWG details are trusted before the Game Details window
    /// re-fetches them in the background. See <see cref="Services.MetadataFreshness"/>.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<MetadataRefreshInterval>))]
    public MetadataRefreshInterval MetadataRefreshInterval { get; set; } = MetadataRefreshInterval.Every3Days;
    public string LibraryViewMode { get; set; } = "Poster Grid";
    public bool MinimizeOnGameLaunch { get; set; } = true;
    /// <summary>
    /// The game-scripts feature switch. Shows the Pre-Launch &amp; Post-Exit Scripts card in Edit
    /// Game, and is also the runtime kill-switch: while off, no script runs for any game, even
    /// one that still has script paths configured (that game keeps showing the card, with a
    /// notice that its scripts are disabled). See <see cref="Services.GameScriptService"/>.
    /// </summary>
    public bool EnableGameScripts { get; set; } = false;
    public bool AutoCheckForUpdates { get; set; } = true;
    public bool IncludePrereleaseUpdates { get; set; } = false;
    public string GitHubRepository { get; set; } = "stephenh678/TrayTrigger";
    public bool HasSeenPerformanceProfileMigrationPrompt { get; set; } = false;
    /// <summary>Gates the one-time "Welcome to TrayTrigger" dialog to the first time the main
    /// window is actually shown on a fresh install - see MainWindow.MaybeShowWelcomePrompt.</summary>
    public bool HasSeenWelcomePrompt { get; set; } = false;
    /// <summary>One-time tray balloon shown the first time the window is hidden via its title-bar
    /// X, so a new user learns TrayTrigger is still running in the tray.</summary>
    public bool HasSeenTrayHideNotice { get; set; } = false;
    /// <summary>
    /// Gates the one-time "Game Launchers Found" prompt (see ImportCoordinator.ScanForGamesAsync)
    /// to the first time the user explicitly presses "Scan for Games" - set the moment that first
    /// press happens, before the prompt is even shown, so it never asks a second time regardless
    /// of what the user chooses.
    /// </summary>
    public bool HasSeenLauncherDetectionPrompt { get; set; } = false;
    public string? SkippedUpdateVersion { get; set; }
    public System.DateTime? RemindAfterUtc { get; set; }
    public bool CreateRestorePointBeforeTweaks { get; set; } = true;
    public OptimizedProfileTweakConfig OptimizedProfileTweaks { get; set; } = new() { PowerPlanEnabled = true, GpuPreferenceEnabled = true };
    public AggressiveProfileTweakConfig AggressiveProfileTweaks { get; set; } = new() { SystemResponsivenessEnabled = true, MmcssGamesPriorityEnabled = true, AboveNormalPriorityEnabled = true, DefenderExclusionEnabled = false, TimerResolutionEnabled = true };

    /// <summary>
    /// What a permanent System &amp; Performance tweak found on the machine before it was applied,
    /// keyed by tweak id (e.g. "power_plan" -> the previously active scheme GUID). "Revert to
    /// Default" restores this exact prior state instead of a hard-coded Windows default. Survives
    /// "Reset settings to defaults" on purpose: it describes the machine, not a preference.
    /// </summary>
    public Dictionary<string, string> TweakPriorState { get; set; } = new();
}

