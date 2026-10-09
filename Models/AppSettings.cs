using System.Text.Json.Serialization;
using TrayTrigger.Services;

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
    public bool BattleNetIntegrationEnabled { get; set; } = true;
    /// <summary>
    /// "Keep game launchers minimized when launching a game": each launcher's own start-quietly switch.
    /// Steam cold-starts with -silent, Epic gets its silent launch link, and GOG Galaxy starts with
    /// /launchViaAutostart. The EA app and Ubisoft Connect have no such switch, so their windows are
    /// left as they are. Xbox launches never open the Xbox app, and Battle.net is left alone because
    /// its launch retries and Play-tab fallback need its window.
    /// </summary>
    public bool KeepLaunchersMinimized { get; set; } = true;
    public bool VerboseLoggingEnabled { get; set; } = false;
    /// <summary>Expanded on a new install, so the page names are visible from the start (UX-14i).
    /// Every settings.json since 1.0.0 stores this value, so existing users keep theirs.</summary>
    public bool IsSidebarExpanded { get; set; } = true;
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
    /// <summary>Opens the tray menu by the tray icon from anywhere, search box ready. Blank means none.
    /// A settings file from before it existed gets the default.</summary>
    public string TrayMenuHotkey { get; set; } = "Ctrl+Alt+T";
    /// <summary>Suspends the game in front (or the only one running), and resumes it when pressed
    /// again. Blank means none. A settings file from before 1.4.7 gets the default; a game or tool
    /// already on the combo keeps it (see HotkeyManager.RegisterHotkeys).</summary>
    public string SuspendGameHotkey { get; set; } = "Ctrl+Alt+P";
    public string LastCategoryFilter { get; set; } = LibraryConstants.AllCategory;
    public string LastSortOption { get; set; } = "Alphabetical (A - Z)";

    /// <summary>
    /// Ticked options in the library filter flyout ("launcher:steam", "status:neverplayed"), kept
    /// across restarts the same way the category tab and sort order are. Unlike those two, an
    /// active filter is not visible in the toolbar on its own, so the filter button carries a blue
    /// dot and the empty-library message offers to clear it - a saved filter must never look like
    /// a library that lost its games.
    /// </summary>
    public List<string> LibraryFilterKeys { get; set; } = new();
    public bool ShowRecentInTray { get; set; } = true;
    public int MaxRecentInTray { get; set; } = 5;
    public string RecentTraySortOption { get; set; } = "Most Recently Played";
    public bool ShowFavoritesInTray { get; set; } = true;
    public int MaxFavoritesInTray { get; set; } = 5;
    public string FavoritesTraySortOption { get; set; } = "Alphabetical (A - Z)";
    public string TrayMenuSortOption { get; set; } = "Alphabetical (A - Z)";
    /// <summary>Game icons (or the launcher's logo when a game has none) beside each game in the tray menu.</summary>
    public bool ShowTrayMenuIcons { get; set; } = true;
    /// <summary>Tighter rows and smaller icons in the tray menu, for a long library. On by default
    /// from 1.5.0, for new installs and Reset to Defaults.</summary>
    public bool CompactTrayMenu { get; set; } = true;
    /// <summary>Left-click on the tray icon opens the game menu instead of showing or hiding the window. Double-click always opens the window.</summary>
    public bool TrayLeftClickOpensMenu { get; set; } = false;
    /// <summary>A small always-on-top popup near the tray while a game launches from a hotkey or the tray menu with
    /// the window hidden: the game, what it is waiting on, and any launch error. It never takes focus and closes
    /// once the game starts. Off: those launches show nothing, as before 1.4.3.</summary>
    public bool ShowLaunchPopup { get; set; } = true;
    /// <summary>With <see cref="ShowLaunchPopup"/> on, also show the popup for launches from the open window, in place
    /// of the in-window launch notice. Off: only while the window is out of sight, or about to hide because
    /// MinimizeOnGameLaunch is on.</summary>
    public bool ShowLaunchPopupOnEveryLaunch { get; set; } = false;
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
    /// default, matching <see cref="UseSteamGridDbArt"/>: both are third-party services the user
    /// supplies their own key for, so neither is on until the user asks for it.</summary>
    public bool UseRawgMetadata { get; set; } = false;
    /// <summary>How long cached Steam and RAWG details are trusted before the Game Details window
    /// re-fetches them in the background. See <see cref="Services.MetadataFreshness"/>.</summary>
    [JsonConverter(typeof(MetadataRefreshIntervalJsonConverter))]
    public MetadataRefreshInterval MetadataRefreshInterval { get; set; } = MetadataRefreshInterval.Every3Days;
    public string LibraryViewMode { get; set; } = "Poster Grid";
    /// <summary>The poster views (Poster Grid and Extra Large) show only the artwork until a card is
    /// pointed at, focused or right-clicked; then the art zooms within the card and the badges,
    /// title and playtime fade in. Off: every card shows them all the time, as before 1.4.7.
    /// Compact Icons and the Details List ignore it.</summary>
    public bool PosterDetailsOnHover { get; set; } = false;
    /// <summary>Which details stay on the card at rest while <see cref="PosterDetailsOnHover"/> is on.</summary>
    public PosterDetailsAtRest PosterDetailsAtRest { get; set; } = new();
    public bool MinimizeOnGameLaunch { get; set; } = true;
    /// <summary>
    /// The game-scripts feature switch. Shows the Pre-Launch &amp; Post-Exit Scripts card in Edit
    /// Game, and is also the runtime kill-switch: while off, no script runs for any game - not a
    /// game's own scripts, not the <see cref="ScriptDefaults"/> - even one that still has script
    /// paths configured (that game keeps showing the card, with a notice that its scripts are
    /// disabled). See <see cref="Services.GameScriptService"/>.
    /// </summary>
    public bool EnableGameScripts { get; set; } = false;
    /// <summary>Scripts that run for every game without one of its own. See <see cref="Models.ScriptDefaults"/>.</summary>
    public ScriptDefaults ScriptDefaults { get; set; } = new();
    /// <summary>
    /// The Tools feature switch: a sidebar page of saved program shortcuts (DLSS Swapper, Vortex,
    /// Afterburner). On by default since 1.4.8, when tools gained "Start when I launch a game" and
    /// became part of playing rather than an extra, and switched on once for existing installs too
    /// (see <see cref="ApplyOneTimeUpgrades"/>). While off, the page, the tray submenu and tool
    /// hotkeys are gone, but tools.json is kept, so turning it back on restores every tool.
    /// </summary>
    public bool EnableTools { get; set; } = true;
    /// <summary>A "Tools" submenu in the tray menu, after the games. Off by default; only applies while <see cref="EnableTools"/> is on.</summary>
    public bool ShowToolsInTray { get; set; } = false;
    /// <summary>A search box as the first row of the tray menu (UX-16): type to filter games (and
    /// tools, when they are in the menu) by name or category; Enter launches the first match. On by
    /// default for new installs and existing users alike: a settings.json from before 1.4.6 has no
    /// value for it, so it takes this initializer.</summary>
    public bool ShowTraySearch { get; set; } = true;
    /// <summary>Order of the tray's Tools submenu. One of <see cref="Services.ToolCatalog.SortOptions"/>.</summary>
    public string ToolsTraySortOption { get; set; } = ToolCatalog.SortAlphabetical;
    /// <summary>Order of the Tools page, separate from the tray's. View state, like <see cref="LastSortOption"/>.</summary>
    public string ToolsSortOption { get; set; } = ToolCatalog.SortAlphabetical;
    /// <summary>Tools page layout. One of <see cref="Services.ToolCatalog.ViewModes"/>.</summary>
    public string ToolsViewMode { get; set; } = ToolCatalog.ViewLargeIcons;
    /// <summary>The Tools page's selected category tab, kept across restarts like <see cref="LastCategoryFilter"/>.</summary>
    public string LastToolsCategoryTab { get; set; } = LibraryConstants.AllCategory;
    /// <summary>
    /// How far back Recent on the Activity &amp; History page goes, set by the Keep dropdown on the
    /// page: one of <see cref="Services.ActivityService.RetentionChoices"/> (30, 90 or 365 days), 90 by
    /// default. Any other value in a hand-edited settings.json reads as the default.
    /// </summary>
    public int ActivityRetentionDays { get; set; } = ActivityService.DefaultRetentionDays;
    public bool AutoCheckForUpdates { get; set; } = true;
    public bool IncludePrereleaseUpdates { get; set; } = false;
    public bool HasSeenPerformanceProfileMigrationPrompt { get; set; } = false;
    /// <summary>Gates the one-time "Welcome to TrayTrigger" dialog to the first time the main
    /// window is actually shown on a fresh install - see MainWindow.MaybeShowWelcomePrompt.</summary>
    public bool HasSeenWelcomePrompt { get; set; } = false;
    /// <summary>How many times TrayTrigger has started (capture and test runs aside). The Library
    /// status bar's "Hotkey: ... to show or hide this window" hint retires after the first five;
    /// from then on the hotkey is in the tray icon's tooltip.</summary>
    public int SessionsStarted { get; set; } = 0;
    /// <summary>Gates the one-time 1.4.0 reminder that SteamGridDB poster art and RAWG game info
    /// are available - see MainWindow.MaybeShowMetadataSourcesReminder.</summary>
    public bool HasSeenMetadataSourcesReminder { get; set; } = false;
    /// <summary>One-time tray balloon shown the first time the window is hidden via its title-bar
    /// X, so a new user learns TrayTrigger is still running in the tray.</summary>
    public bool HasSeenTrayHideNotice { get; set; } = false;
    /// <summary>Set once 1.4.8's one-time "Tools on for everyone" has run on this settings file -
    /// see <see cref="ApplyOneTimeUpgrades"/>. After that, Tools stays however the user leaves it.</summary>
    public bool HasTurnedOnToolsFor148 { get; set; } = false;
    /// <summary>Set once 1.5.0's one-time "compact tray menu for everyone" has run on this settings
    /// file - see <see cref="ApplyOneTimeUpgrades"/>. Named while 1.5.0 was still 1.4.9; the name
    /// stays, since it is the key saved in settings.json.</summary>
    public bool HasTurnedOnCompactTrayFor149 { get; set; } = false;

    /// <summary>
    /// The changes a release makes once to a settings file it finds, run at startup. Returns true
    /// when anything changed, so the caller saves. Each has its own flag, so each runs once and a
    /// user who changes the setting back afterwards keeps their choice.
    ///
    /// <para>1.4.8: Tools on. It had been off by default, so a saved "off" mostly meant nobody had
    /// chosen; since tools can now start with a game, the page is switched on once for everyone.</para>
    ///
    /// <para>1.5.0: compact tray menu on. It had been off by default, and every settings file saves
    /// the value, so a saved "off" is mostly the old default too.</para>
    /// </summary>
    public bool ApplyOneTimeUpgrades()
    {
        bool changed = false;

        if (!HasTurnedOnToolsFor148)
        {
            HasTurnedOnToolsFor148 = true;
            bool wasOff = !EnableTools;
            EnableTools = true;
            LoggingService.Info("Settings", wasOff
                ? "1.4.8 upgrade: turned Tools on (it was off; Settings > General turns it off again)."
                : "1.4.8 upgrade: Tools was already on.");
            changed = true;
        }

        if (!HasTurnedOnCompactTrayFor149)
        {
            HasTurnedOnCompactTrayFor149 = true;
            bool wasOff = !CompactTrayMenu;
            CompactTrayMenu = true;
            LoggingService.Info("Settings", wasOff
                ? "1.5.0 upgrade: turned the compact tray menu on (it was off; Settings > Tray Menu turns it off again)."
                : "1.5.0 upgrade: the compact tray menu was already on.");
            changed = true;
        }

        return changed;
    }
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
    public OptimizedProfileTweakConfig OptimizedProfileTweaks { get; set; } = new();
    public AggressiveProfileTweakConfig AggressiveProfileTweaks { get; set; } = new();
    /// <summary>The Performance Profile a game gets when it's added to the library - by a scan, Add
    /// Game, Add Folder or a drop. Games already in the library keep their own. Set on the System
    /// page's Performance Profiles tab.</summary>
    public PerformanceProfileMode NewGameProfile { get; set; } = PerformanceProfileMode.Optimized;

    /// <summary>
    /// What a permanent System &amp; Performance tweak found on the machine before it was applied,
    /// keyed by tweak id (e.g. "power_plan" -> the previously active scheme GUID). "Revert to
    /// Default" restores this exact prior state instead of a hard-coded Windows default. Survives
    /// "Reset settings to defaults" on purpose: it describes the machine, not a preference.
    /// </summary>
    public Dictionary<string, string> TweakPriorState { get; set; } = new();

    /// <summary>
    /// The System &amp; Performance tweaks TrayTrigger itself switched to optimal, by id. One of these
    /// found back at standard - a Windows update, usually - is reported once in Activity &amp;
    /// History and dropped from here. Describes the machine, like <see cref="TweakPriorState"/>.
    /// </summary>
    public List<string> TweaksAppliedByTrayTrigger { get; set; } = new();

    /// <summary>"Notify me when a scan adds games": a toast as well as the Activity &amp; History
    /// entry, the startup scan included. The only notification setting.</summary>
    public bool NotifyWhenScanAddsGames { get; set; } = false;

    /// <summary>The version that last ran, so an update can be noted once in Activity &amp; History.</summary>
    public string LastRunVersion { get; set; } = string.Empty;
}

