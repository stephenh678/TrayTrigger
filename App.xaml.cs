using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using H.NotifyIcon;
using System.Runtime.InteropServices;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;
using TrayTrigger.Views;

namespace TrayTrigger;

public partial class App : Application
{
    private const string MutexName = "TrayTrigger_SingleInstance_Mutex";
    private const string EventName = "TrayTrigger_ShowWindow_Event";

    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _showWindowEvent;

    private const int MaxDispatcherExceptionsPerWindow = 3;
    private static readonly TimeSpan DispatcherExceptionWindow = TimeSpan.FromSeconds(30);
    private int _dispatcherExceptionCount;
    private DateTime _dispatcherExceptionWindowStart = DateTime.MinValue;

    private StorageService _storageService = null!;
    private ShortcutService _shortcutService = null!;
    private IconExtractorService _iconExtractorService = null!;
    private SteamScannerService _steamScannerService = null!;
    private GogScannerService _gogScannerService = null!;
    private EaScannerService _eaScannerService = null!;
    private EpicScannerService _epicScannerService = null!;
    private UbisoftScannerService _ubisoftScannerService = null!;
    private XboxScannerService _xboxScannerService = null!;
    private PerformanceProfileService _performanceProfileService = null!;
    private ProcessLauncherService _launcherService = null!;
    private GameScriptService? _gameScriptService;
    private HotkeyManager _hotkeyManager = null!;
    private StartupManager _startupManager = null!;
    private TrayPromotionService _trayPromotionService = null!;
    private MainViewModel _mainViewModel = null!;

    private TaskbarIcon? _trayIcon;

    /// <summary>The tray tooltip while nothing is playing.</summary>
    private const string DefaultTrayToolTip = "TrayTrigger - Game Launcher";
    /// <summary>The shell truncates a tray tooltip past this; do it ourselves so it ends cleanly.</summary>
    private const int MaxTrayToolTipLength = 127;
    /// <summary>Re-writes the tooltip's elapsed time while a game is running; stopped when none is.</summary>
    private System.Windows.Threading.DispatcherTimer? _trayToolTipTimer;

    private const int SM_CXSMICON = 49;
    private const int SM_CYSMICON = 50;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    private MainWindow? _mainWindow;
    private bool _isShuttingDown = false;
    private Action<string> _logger = _ => { };

    public App()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);

        LoggingService.EnsureLogFileExists();
        _storageService = new StorageService();
        AppSettings startupSettings;
        try
        {
            startupSettings = _storageService.LoadSettings();
            LoggingService.Initialize(startupSettings.VerboseLoggingEnabled);
        }
        catch
        {
            startupSettings = new AppSettings();
            LoggingService.Initialize(false);
        }
        void Log(string msg) => LoggingService.Info("App", msg);
        _logger = Log;

        // Drop installers left in %TEMP% by earlier in-app updates (off the UI thread: it's
        // file I/O against a folder that may hold several 50 MB files).
        _ = Task.Run(UpdateService.CleanupDownloadedInstallers);

        AppDomain.CurrentDomain.ProcessExit += (s, args) =>
        {
            try
            {
                if (_mainViewModel != null && _storageService != null)
                {
                    _storageService.SaveSettings(_mainViewModel.Settings);
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn("App", $"Failed to save settings during ProcessExit: {ex.Message}");
            }
            LoggingService.Info("App", "ProcessExit triggered and finished.");
        };

        Exit += (s, args) =>
        {
            LoggingService.Info("App", $"Application.Exit triggered! Code={args.ApplicationExitCode}");
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            LoggingService.Error("App", $"FATAL AppDomain Exception: {args.ExceptionObject}");
        };

        DispatcherUnhandledException += (s, args) =>
        {
            LoggingService.Error("App", $"FATAL Dispatcher Exception: {args.Exception}");

            var now = DateTime.UtcNow;
            if (now - _dispatcherExceptionWindowStart > DispatcherExceptionWindow)
            {
                _dispatcherExceptionWindowStart = now;
                _dispatcherExceptionCount = 0;
            }
            _dispatcherExceptionCount++;

            if (_dispatcherExceptionCount > MaxDispatcherExceptionsPerWindow)
            {
                // Repeated exceptions in a short window mean the app is likely in a
                // corrupted state rather than hitting one isolated fluke - shut down
                // cleanly (saving settings) instead of continuing to run broken.
                LoggingService.Error("App", "Too many unhandled exceptions in a short window; exiting instead of continuing in a possibly corrupted state.");
                // Notify before ExitApplication() disposes the tray icon. See L-24.
                _trayIcon?.ShowNotification("TrayTrigger", "Too many errors occurred; TrayTrigger is closing. Check the log for details.");
                args.Handled = true;
                ExitApplication();
                return;
            }

            // Keep the tray app alive after an isolated exception rather than letting WPF
            // terminate the process - but a silently swallowed exception is invisible to the
            // user, so surface it as a non-modal toast instead of only the log file. See L-24.
            _trayIcon?.ShowNotification("TrayTrigger", "An error was logged. The app is still running - check the log if something looks wrong.");
            args.Handled = true;
        };

        Log("Application starting...");

#if DEBUG
        ProcessDevArguments(e);
        if (_isShuttingDown) return;
#endif

        // Single-instance check
        bool isScreenshot = false;
#if DEBUG
        isScreenshot = e.Args.Any(a => a.StartsWith("--screenshot", StringComparison.OrdinalIgnoreCase) ||
                                       a.StartsWith("-screenshot", StringComparison.OrdinalIgnoreCase) ||
                                       a.StartsWith("--test", StringComparison.OrdinalIgnoreCase) ||
                                       a.StartsWith("-test", StringComparison.OrdinalIgnoreCase));
#endif
        if (!isScreenshot)
        {
            try
            {
                _singleInstanceMutex = new Mutex(true, MutexName, out bool isFirstInstance);
                Log($"Single instance check: isFirstInstance={isFirstInstance}");
                if (!isFirstInstance)
                {
                    try
                    {
                        using var evt = EventWaitHandle.OpenExisting(EventName);
                        evt.Set();
                        Log("Signaled existing instance.");
                    }
                    catch (Exception ex)
                    {
                        Log($"Could not signal existing instance: {ex.Message}");
                    }

                    Shutdown();
                    Environment.Exit(0);
                    return;
                }
            }
            catch (Exception ex)
            {
                Log($"Mutex error: {ex}");
            }
        }

        // Setup background listener for wake-up events
        if (!isScreenshot)
        {
            try
            {
                _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
                var listenerThread = new Thread(() =>
                {
                    while (!_isShuttingDown)
                    {
                        if (_showWindowEvent.WaitOne())
                        {
                            if (_isShuttingDown) break;
                            Dispatcher.Invoke(ShowMainWindow);
                        }
                    }
                })
                {
                    IsBackground = true
                };
                listenerThread.Start();
                Log("Event listener thread started.");
            }
            catch (Exception ex)
            {
                Log($"Could not initialize ShowWindow event listener: {ex.Message}");
            }
        }

        // Check command-line arguments
        bool startMinimized = isScreenshot || e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
                                                              a.Equals("-minimized", StringComparison.OrdinalIgnoreCase));
        Log($"startMinimized={startMinimized}");

        // Initialize Core Services
        Log("Initializing Services...");
        _shortcutService = new ShortcutService();
        _iconExtractorService = new IconExtractorService(_storageService);
        _steamScannerService = new SteamScannerService();
        _gogScannerService = new GogScannerService();
        _eaScannerService = new EaScannerService();
        _epicScannerService = new EpicScannerService();
        _ubisoftScannerService = new UbisoftScannerService();
        _xboxScannerService = new XboxScannerService();
        _performanceProfileService = new PerformanceProfileService(_storageService);
        _performanceProfileService.RecoverFromCrashIfNeeded();
        // The Settings "Enable game scripts" switch is enforced here, not just in the edit dialog.
        _gameScriptService = new GameScriptService(
            () => (_mainViewModel?.Settings ?? startupSettings).EnableGameScripts,
            () => (_mainViewModel?.Settings ?? startupSettings).ScriptDefaults);
        // Blank templates, examples and README land in the scripts folder once the feature is on
        // (missing files only - user edits are never overwritten). Off the UI thread: file I/O.
        if (startupSettings.EnableGameScripts)
        {
            var scriptLibrary = new ScriptLibraryService(_storageService.BaseDirectory);
            _ = Task.Run(() => scriptLibrary.EnsureInstalled());
        }
        _launcherService = new ProcessLauncherService(_storageService, _performanceProfileService, _gameScriptService, _steamScannerService, _gogScannerService, _eaScannerService, _epicScannerService, _ubisoftScannerService, _xboxScannerService);
        _hotkeyManager = new HotkeyManager();
        _startupManager = new StartupManager();
        _startupManager.ReconcilePath();
        _trayPromotionService = new TrayPromotionService();
        TrayPromotionService.CleanStaleRegistrations();

        // Initialize ViewModel & MainWindow
        Log("Initializing ViewModel...");
        _mainViewModel = new MainViewModel(
            _storageService,
            startupSettings,
            _shortcutService,
            _iconExtractorService,
            _steamScannerService,
            _gogScannerService,
            _eaScannerService,
            _epicScannerService,
            _ubisoftScannerService,
            _xboxScannerService,
            _launcherService,
            _hotkeyManager,
            _startupManager,
            _trayPromotionService);

        // Constructing MainWindow initializes WPF's D3D render stack (GPU driver DLLs load
        // immediately) even if it's never shown, so skip it when starting minimized to the tray -
        // ShowMainWindow() constructs it lazily on first use. Screenshot mode still needs it up
        // front since the dev args processed below operate on _mainWindow directly.
        if (!startMinimized || isScreenshot)
        {
            Log("Initializing MainWindow...");
            _mainWindow = new MainWindow(_mainViewModel);
            MainWindow = _mainWindow;
        }

#if DEBUG
        ProcessDevArguments(e);
        if (_isShuttingDown) return;
#endif

        // Global Hotkey Trigger
        _hotkeyManager.ManageHotkeyTriggered += OnManageHotkeyTriggered;

        // Auto-refresh tray menu when games change
        _mainViewModel.LibraryUpdated += UpdateTrayContextMenu;
        // The "Now Playing" tray section follows the launcher's session registry directly.
        _launcherService.SessionStarted += _ => { UpdateTrayContextMenu(); UpdateTrayToolTip(); };
        _launcherService.SessionEnded += _ => { UpdateTrayContextMenu(); UpdateTrayToolTip(); };

        // Windows shutdown / sign-out: WPF raises SessionEnding instead of going through the tray
        // Exit path, so without this a running game's Performance Profile (power plan, MMCSS,
        // HDR) simply stayed applied until the next TrayTrigger start. Anything that would need
        // a UAC prompt is skipped here (nobody can answer it during shutdown) and finished by the
        // crash-recovery snapshot on the next start.
        SessionEnding += (s, args) =>
        {
            LoggingService.Info("App", $"Windows session ending ({args.ReasonSessionEnding}); restoring active Performance Profile state.");
            try
            {
                _performanceProfileService?.RestoreActiveSessionOnShutdown(skipElevated: true);
                _gameScriptService?.RunPendingPostExitScriptsOnShutdown();
                FinalizePendingRemovalOnShutdown();
                if (_mainViewModel != null && _storageService != null)
                {
                    _storageService.SaveSettings(_mainViewModel.Settings);
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn("App", $"Restore during session end failed: {ex.Message}");
            }
        };
        _mainViewModel.RequestExitApplication += ExitApplication;
        _mainViewModel.RequestTrayNotification += (title, message) => _trayIcon?.ShowNotification(title, message);

        // Setup Taskbar Tray Icon
        Log("Initializing Tray Icon...");
        InitializeTrayIcon();

        // Apply "Always show in tray" if configured
        if (_mainViewModel.Settings.AlwaysShowTrayIcon)
        {
            _trayPromotionService.TrySetAlwaysShow(true, out _);
        }

        // Show window if not started with --minimized
        if (!startMinimized)
        {
            Log("Showing MainWindow...");
            ShowMainWindow();
        }

        Log("OnStartup completed successfully.");
    }


    private void InitializeTrayIcon()
    {
        try
        {
            _trayIcon = new TaskbarIcon
            {
                ToolTipText = DefaultTrayToolTip,
                LeftClickCommand = new RelayCommand(ToggleMainWindow),
                DoubleClickCommand = new RelayCommand(ShowMainWindow)
            };

            // Ask for the shell's small-icon size (16 px at 100 % DPI, 20/24/32 as DPI rises) so the
            // .ico's hand-tuned small frame is used as-is instead of a downscaled 32 px one.
            var traySize = new System.Drawing.Size(
                Math.Max(16, GetSystemMetrics(SM_CXSMICON)),
                Math.Max(16, GetSystemMetrics(SM_CYSMICON)));

            // Attempt to load native Icon from application resources, fallback to file
            System.Drawing.Icon? loadedIcon = null;
            try
            {
                var iconUri = new Uri("pack://application:,,,/Assets/app_icon.ico", UriKind.Absolute);
                var resInfo = GetResourceStream(iconUri);
                if (resInfo?.Stream != null)
                {
                    using var stream = resInfo.Stream;
                    loadedIcon = new System.Drawing.Icon(stream, traySize);
                }
            }
            catch
            {
                // Fallback below
            }

            if (loadedIcon == null)
            {
                string diskPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app_icon.ico");
                if (File.Exists(diskPath))
                {
                    loadedIcon = new System.Drawing.Icon(diskPath, traySize);
                }
            }

            if (loadedIcon != null)
            {
                _trayIcon.Icon = loadedIcon;
            }

            UpdateTrayContextMenu();
            UpdateTrayToolTip();
            if (!_trayIcon.IsCreated)
            {
                _trayIcon.ForceCreate();
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error("App", $"Error initializing TaskbarIcon: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Puts what is playing in the tray tooltip - the one place the tray can say something while
    /// the window is hidden. Driven by the launcher's session registry, the same source as the
    /// "Now Playing" menu section, and re-run on a timer only while a session is active so the
    /// elapsed time stays current. Deliberately no CPU or RAM figures: those are system-wide, and
    /// beside a game's name they read as that game's usage. The System page shows them properly.
    /// </summary>
    public void UpdateTrayToolTip()
    {
        if (_trayIcon == null) return;

        try
        {
            var sessions = _launcherService?.GetActiveSessions() ?? [];
            string text;

            if (sessions.Count == 0)
            {
                text = DefaultTrayToolTip;
            }
            else if (sessions.Count == 1)
            {
                var session = sessions[0];
                text = session.GameStarted
                    ? $"Playing {session.Game.Name} · {FormatPlayingElapsed(DateTime.Now - session.StartedAt)}"
                    : $"Starting {session.Game.Name} via {session.PlatformLabel}";
            }
            else
            {
                text = $"Playing {sessions.Count} games · {string.Join(", ", sessions.Select(s => s.Game.Name))}";
            }

            if (text.Length > MaxTrayToolTipLength)
            {
                text = text[..(MaxTrayToolTipLength - 1)].TrimEnd() + "…";
            }

            _trayIcon.ToolTipText = text;

            // The timer exists only to age the elapsed time, so it runs only while something is
            // actually playing (a session still starting has no elapsed time to age yet).
            bool needsTicking = sessions.Any(s => s.GameStarted);
            if (needsTicking && _trayToolTipTimer == null)
            {
                _trayToolTipTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                _trayToolTipTimer.Tick += (_, _) => UpdateTrayToolTip();
                _trayToolTipTimer.Start();
            }
            else if (!needsTicking && _trayToolTipTimer != null)
            {
                _trayToolTipTimer.Stop();
                _trayToolTipTimer = null;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("App", $"Could not update the tray tooltip: {ex.Message}");
        }
    }

    /// <summary>"47m" under an hour, "1h 12m" past it - the tray tooltip has no room for more.</summary>
    private static string FormatPlayingElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        int minutes = (int)elapsed.TotalMinutes;
        return minutes < 60 ? $"{minutes}m" : $"{minutes / 60}h {minutes % 60}m";
    }

    public void UpdateTrayContextMenu()
    {
        if (_trayIcon == null) return;

        Dispatcher.Invoke(() =>
        {
            var menu = new ContextMenu();

            var games = _mainViewModel.Games.Where(g => !g.Game.IsHidden).ToList();

            if (games.Count == 0)
            {
                var emptyItem = new MenuItem
                {
                    Header = "No games in library",
                    IsEnabled = false,
                    Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 150))
                };
                menu.Items.Add(emptyItem);
            }
            else
            {
                // 0. "Now Playing": every session the launcher is tracking, with an escape hatch
                //    for a stuck one (restore tweaks now) and a way to kill a hung game.
                var activeSessions = _launcherService?.GetActiveSessions() ?? [];
                if (activeSessions.Count > 0)
                {
                    menu.Items.Add(CreateSectionHeader("Now Playing"));
                    foreach (var session in activeSessions)
                    {
                        var card = games.FirstOrDefault(g => g.Id == session.GameId);
                        string elapsed = session.GameStarted
                            ? $"{Math.Max(0, (DateTime.Now - session.StartedAt).TotalMinutes):0}m"
                            : $"starting via {session.PlatformLabel}";
                        var sessionItem = card != null
                            ? CreateGameMenuItem(card)
                            : new MenuItem { Header = session.Game.Name, FontWeight = FontWeights.SemiBold, FontSize = 12.5 };
                        sessionItem.Header = $"{session.Game.Name}  ·  {elapsed}";
                        sessionItem.Command = null;

                        string gameId = session.GameId;
                        sessionItem.Items.Add(CreateNavMenuItem("End Session (restore tweaks)", "", () =>
                            Task.Run(() => _launcherService!.EndSessionNow(gameId, forceCloseGame: false))));
                        var forceClose = CreateNavMenuItem("Force Close Game", "", () =>
                        {
                            if (card != null)
                            {
                                card.ForceCloseCommand.Execute(null);
                            }
                            else
                            {
                                Task.Run(() => _launcherService!.EndSessionNow(gameId, forceCloseGame: true));
                            }
                        }, iconBrush: new SolidColorBrush(Color.FromRgb(255, 120, 100)));
                        forceClose.IsEnabled = session.CanForceClose;
                        sessionItem.Items.Add(forceClose);
                        menu.Items.Add(sessionItem);
                    }
                    menu.Items.Add(new Separator());
                }

                // 1. Persistent "Recent" section directly in root menu
                if (_mainViewModel.Settings.ShowRecentInTray && _mainViewModel.Settings.MaxRecentInTray > 0)
                {
                    var recentGames = ApplySortOption(
                        games
                            .Where(g => g.Game.LastPlayed.HasValue)
                            .OrderByDescending(g => g.Game.LastPlayed!.Value)
                            .Take(_mainViewModel.Settings.MaxRecentInTray),
                        _mainViewModel.Settings.RecentTraySortOption)
                        .ToList();

                    menu.Items.Add(CreateSectionHeader("Recent"));

                    if (recentGames.Count > 0)
                    {
                        foreach (var card in recentGames)
                        {
                            menu.Items.Add(CreateGameMenuItem(card));
                        }
                    }
                    else
                    {
                        menu.Items.Add(new MenuItem
                        {
                            Header = "(No recently played games)",
                            IsEnabled = false,
                            IsHitTestVisible = false,
                            FontSize = 12,
                            Foreground = (Brush)FindResource("BrushTextMuted")
                        });
                    }

                    menu.Items.Add(new Separator());
                }

                // 1b. Persistent "Favorites" section directly in root menu. Unlike Recent, an
                //     empty Favorites section is simply omitted - the user knows how to star a
                //     game, and "(No favorites yet)" only made the menu longer.
                bool favoritesSectionShown = false;
                if (_mainViewModel.Settings.ShowFavoritesInTray && _mainViewModel.Settings.MaxFavoritesInTray > 0)
                {
                    var favoriteGames = ApplySortOption(
                        games.Where(g => g.Game.IsFavorite),
                        _mainViewModel.Settings.FavoritesTraySortOption)
                        .Take(_mainViewModel.Settings.MaxFavoritesInTray)
                        .ToList();

                    if (favoriteGames.Count > 0)
                    {
                        favoritesSectionShown = true;
                        menu.Items.Add(CreateSectionHeader(LibraryConstants.FavoritesCategory));
                        foreach (var card in favoriteGames)
                        {
                            menu.Items.Add(CreateGameMenuItem(card));
                        }
                        menu.Items.Add(new Separator());
                    }
                }

                // The main list gets its own "All Games" / "Categories" header whenever any
                // section precedes it, so it never runs straight on from Recent or Favorites.
                bool mainListNeedsHeader = activeSessions.Count > 0
                    || (_mainViewModel.Settings.ShowRecentInTray && _mainViewModel.Settings.MaxRecentInTray > 0)
                    || favoritesSectionShown;

                // 2. Sorting helper, reused per-section with each section's own sort option
                IEnumerable<GameCardViewModel> ApplySortOption(IEnumerable<GameCardViewModel> source, string sortOption)
                {
                    return sortOption switch
                    {
                        "Most Recently Played" => source.OrderByDescending(c => c.Game.LastPlayed ?? DateTime.MinValue).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase),
                        "Cumulative Playtime" => source.OrderByDescending(c => c.Game.CumulativePlaytimeMinutes).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase),
                        "Alphabetical (Z - A)" => source.OrderByDescending(c => c.Name, StringComparer.OrdinalIgnoreCase),
                        _ => source.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    };
                }

                IEnumerable<GameCardViewModel> ApplyTraySort(IEnumerable<GameCardViewModel> source) =>
                    ApplySortOption(source, _mainViewModel.Settings.TrayMenuSortOption);

                bool groupByCategory = _mainViewModel.Settings.GroupTrayMenuByCategory;

                if (!groupByCategory)
                {
                    // Flat sorted list of all games
                    if (mainListNeedsHeader)
                    {
                        menu.Items.Add(CreateSectionHeader("All Games"));
                    }

                    foreach (var card in ApplyTraySort(games))
                    {
                        menu.Items.Add(CreateGameMenuItem(card));
                    }
                }
                else
                {
                    // Group games by category (excluding any user category named 'Recent' only if the dynamic Recent section is active)
                    var grouped = games
                        .GroupBy(g => string.IsNullOrWhiteSpace(g.Category) ? LibraryConstants.Uncategorized : g.Category)
                        .Where(g => !_mainViewModel.Settings.ShowRecentInTray || !string.Equals(g.Key, "Recent", StringComparison.OrdinalIgnoreCase))
                        .Where(g => !_mainViewModel.Settings.ShowFavoritesInTray || !string.Equals(g.Key, LibraryConstants.FavoritesCategory, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(g => g.Key)
                        .ToList();

                    // Only list directly if all games in library are "Uncategorized"
                    if (grouped.Count == 1 && string.Equals(grouped[0].Key, LibraryConstants.Uncategorized, StringComparison.OrdinalIgnoreCase))
                    {
                        if (mainListNeedsHeader)
                        {
                            menu.Items.Add(CreateSectionHeader("All Games"));
                        }

                        foreach (var card in ApplyTraySort(grouped[0]))
                        {
                            menu.Items.Add(CreateGameMenuItem(card));
                        }
                    }
                    else
                    {
                        if (mainListNeedsHeader)
                        {
                            menu.Items.Add(CreateSectionHeader("Categories"));
                        }

                        // Multiple categories or a user-defined category: create category submenus
                        foreach (var group in grouped)
                        {
                            var categoryMenu = new MenuItem
                            {
                                Header = $"{group.Key} ({group.Count()})",
                                FontSize = 12.5,
                                FontWeight = FontWeights.Normal
                            };

                            var catIconBox = new Border
                            {
                                Width = 24,
                                Height = 24,
                                CornerRadius = new CornerRadius(4),
                                Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                                VerticalAlignment = VerticalAlignment.Center,
                                HorizontalAlignment = HorizontalAlignment.Center
                            };
                            catIconBox.Child = new TextBlock
                            {
                                Text = "\uED25",
                                FontFamily = (FontFamily)FindResource("IconFont"),
                                FontSize = 12,
                                Foreground = (Brush)FindResource("BrushTextSecondary"),
                                HorizontalAlignment = HorizontalAlignment.Center,
                                VerticalAlignment = VerticalAlignment.Center
                            };
                            categoryMenu.Icon = catIconBox;

                            foreach (var card in ApplyTraySort(group))
                            {
                                categoryMenu.Items.Add(CreateGameMenuItem(card));
                            }

                            menu.Items.Add(categoryMenu);
                        }
                    }
                }
            }

            menu.Items.Add(new Separator());

            // Navigation & exit items. A tray-first launcher should reach its two most common
            // non-launch tasks from the tray too, not only after opening the window.
            menu.Items.Add(CreateNavMenuItem("Games Library", "\uE7FC", () => OpenSection(NavSection.Library), (Brush)FindResource("BrushAccentHover")));
            menu.Items.Add(CreateNavMenuItem("Scan for Games...", "\uE721", () =>
            {
                OpenSection(NavSection.Library);
                _mainViewModel.OpenScanForGamesCommand.Execute(null);
            }));
            menu.Items.Add(CreateNavMenuItem("Settings", "\uE713", () => OpenSection(NavSection.Settings)));

            menu.Items.Add(new Separator());

            var exitBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x75));
            var exitItem = CreateNavMenuItem("Exit TrayTrigger", "\uE7E8", ExitApplication, exitBrush, exitBrush);
            menu.Items.Add(exitItem);

            _trayIcon.ContextMenu = menu;
        });
    }

    private MenuItem CreateSectionHeader(string text)
    {
        return new MenuItem
        {
            Header = text.ToUpperInvariant(),
            Style = (Style)FindResource("MenuSectionHeaderStyle"),
            Focusable = false,
            IsHitTestVisible = false
        };
    }

    private MenuItem CreateNavMenuItem(string header, string iconGlyph, Action onClick, Brush? iconBrush = null, Brush? textBrush = null)
    {
        var item = new MenuItem
        {
            Header = header,
            FontWeight = FontWeights.Normal,
            FontSize = 12.5,
            Command = new RelayCommand(onClick)
        };

        if (textBrush != null)
        {
            item.Foreground = textBrush;
        }

        var iconBox = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var glyph = new TextBlock
        {
            Text = iconGlyph,
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = iconBrush ?? (Brush)FindResource("BrushTextSecondary")
        };

        iconBox.Child = glyph;
        item.Icon = iconBox;
        return item;
    }

    private MenuItem CreateGameMenuItem(GameCardViewModel card)
    {
        var item = new MenuItem
        {
            Header = card.Name,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5,
            Command = new RelayCommand(() => _mainViewModel.LaunchGame(card))
        };

        var iconBox = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 38)),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        if (card.IconImage != null)
        {
            var img = new Image
            {
                Source = card.IconImage,
                Width = 24,
                Height = 24,
                Stretch = Stretch.UniformToFill
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            iconBox.Child = img;
        }
        else
        {
            var fallback = new TextBlock
            {
                Text = "\uE7FC",
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 12,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            iconBox.Child = fallback;
        }

        item.Icon = iconBox;
        return item;
    }

    public void OpenSection(NavSection section)
    {
        if (_mainViewModel != null)
        {
            _mainViewModel.CurrentSection = section;
        }
        ShowMainWindow();
    }

    public void ShowMainWindow()
    {
        if (_isShuttingDown) return;

        try
        {
            if (_mainWindow == null)
            {
                _mainWindow = new MainWindow(_mainViewModel);
                MainWindow = _mainWindow;
            }

            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }

            if (!_mainWindow.IsVisible)
            {
                _mainWindow.Show();
            }

            // On the very first show the window is cloaked until its first frame renders;
            // defer the activation dance until then so it doesn't compete with the first
            // paint. On subsequent shows this runs immediately.
            var window = _mainWindow;
            WindowThemeService.WhenContentRendered(window, () =>
            {
                if (_isShuttingDown || !window.IsVisible) return;
                window.Activate();
                window.Topmost = true;
                window.Topmost = false;
                window.Focus();
            });
        }
        catch (Exception ex)
        {
            LoggingService.Error("App", $"Exception in ShowMainWindow: {ex.Message}", ex);
            try
            {
                _mainWindow = new MainWindow(_mainViewModel);
                MainWindow = _mainWindow;
                _mainWindow.Show();
                _mainWindow.Activate();
            }
            catch (Exception ex2)
            {
                LoggingService.Error("App", $"Fatal exception recreating MainWindow: {ex2.Message}", ex2);
            }
        }
    }

    /// <summary>
    /// The global "manage" hotkey (default Ctrl+Alt+G) is meant as a jump-to-library shortcut,
    /// not just a raise/hide toggle - so unlike the tray icon's left-click (plain ToggleMainWindow),
    /// it also switches to the Library section.
    /// </summary>
    private void OnManageHotkeyTriggered()
    {
        _mainViewModel.CurrentSection = NavSection.Library;
        ToggleMainWindow();
    }

    public void ToggleMainWindow()
    {
        if (_mainWindow == null)
        {
            ShowMainWindow();
            return;
        }

        if (_mainWindow.IsVisible && _mainWindow.IsActive)
        {
            _mainWindow.Hide();
        }
        else
        {
            ShowMainWindow();
        }
    }

    public void ExitApplication()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;

        try
        {
            _performanceProfileService?.RestoreActiveSessionOnShutdown();
            _gameScriptService?.RunPendingPostExitScriptsOnShutdown();
            FinalizePendingRemovalOnShutdown();

            if (_mainViewModel != null && _storageService != null)
            {
                _storageService.SaveSettings(_mainViewModel.Settings);
                LoggingService.Info("App", "Settings successfully saved during application exit.");
            }

            _trayToolTipTimer?.Stop();
            _trayToolTipTimer = null;
            _hotkeyManager?.Dispose();
            _trayIcon?.Dispose();

            if (_mainWindow != null)
            {
                _mainWindow.MarkExplicitExit();
                _mainWindow.Close();
            }

            try
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch (Exception ex)
            {
                LoggingService.Verbose("App", $"ReleaseMutex on shutdown failed (usually benign): {ex.Message}");
            }

            _singleInstanceMutex?.Dispose();
            _showWindowEvent?.Dispose();
        }
        catch (Exception ex)
        {
            LoggingService.Error("App", $"Error during shutdown: {ex.Message}", ex);
        }
        finally
        {
            Shutdown();
            Environment.Exit(0);
        }
    }

    /// <summary>
    /// A game removed in the last 6 seconds is still inside its undo window, which lives only in
    /// memory - finish its cleanup (cached art, Steam/RAWG details) before the process goes away.
    /// Isolated so a failure here can't skip the settings save or profile restore around it.
    /// </summary>
    private void FinalizePendingRemovalOnShutdown()
    {
        try
        {
            _mainViewModel?.FinalizePendingRemoval();
        }
        catch (Exception ex)
        {
            LoggingService.Warn("App", $"Failed to finish a pending game removal during shutdown: {ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LoggingService.Info("App", $"OnExit called. ExitCode={e.ApplicationExitCode}");
        FinalizePendingRemovalOnShutdown();
        try
        {
            if (_mainViewModel != null && _storageService != null)
            {
                _storageService.SaveSettings(_mainViewModel.Settings);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("App", $"Failed to save settings during OnExit: {ex.Message}");
        }
        _hotkeyManager?.Dispose();
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
