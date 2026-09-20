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
    private BattleNetScannerService _battleNetScannerService = null!;
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
    /// <summary>Set by the first shutdown save (see <see cref="SaveSettingsOnShutdown"/>), so the
    /// paths that follow it - OnExit, the ProcessExit fallback - do not write the same file again.</summary>
    private volatile bool _settingsSavedOnExit;
    /// <summary>Process exit code for <see cref="ExitApplication"/>: non-zero when the release
    /// screenshot could not be written, so a script refreshing the site image can tell.</summary>
    private int _exitCode;
    /// <summary>Set by the release screenshot: that run resizes the window for the capture and may
    /// share settings.json with a running instance, so nothing it holds is written back.</summary>
    private volatile bool _skipSettingsSaveOnExit;
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
                // Last-resort save, for a shutdown that never ran ExitApplication (killed from
                // Task Manager, a Windows log-off). When the normal exit path did run it has
                // already written the same object moments earlier, so repeating it here is a
                // second full encrypt-and-replace of settings.json for no gain.
                SaveSettingsOnShutdown("App.ProcessExit");
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

        // The uninstaller's last call: the override lives in NVIDIA's driver, not in TrayTrigger,
        // so removing the program would otherwise leave every game's setting behind for good.
        if (e.Args.Any(a => a.Equals("--restore-dlss", StringComparison.OrdinalIgnoreCase)))
        {
            RestoreDlssOverridesAndExit();
            return;
        }

#if DEBUG
        ProcessDevArguments(e);
        if (_isShuttingDown) return;
#endif

        // Single-instance check
        // "--screenshot <file>" works in every build (see App.Screenshot.cs); the rest of the
        // capture and test modes are Debug-only. A screenshot run skips the single-instance
        // check so it can be taken while the real app is running.
        bool isScreenshot = IsReleaseScreenshotRequest(e.Args);
#if DEBUG
        isScreenshot = isScreenshot || e.Args.Any(a => a.StartsWith("--screenshot", StringComparison.OrdinalIgnoreCase) ||
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
        _battleNetScannerService = new BattleNetScannerService();
        // Same live-settings provider as _gameScriptService below, rather than the convenience
        // constructor's () => storageService.LoadSettings(): that re-read and re-decrypted
        // settings.json from disk on every profile apply and restore - twice per game launch, on
        // the launch hot path - and used the on-disk copy, so a toggle not yet flushed was
        // ignored. Falls back to startupSettings for the window before the ViewModel exists.
        _performanceProfileService = new PerformanceProfileService(
            _storageService,
            () => _mainViewModel?.Settings ?? startupSettings,
            new WindowsTweakBackend());
        // A screenshot run skips the single-instance check, so the snapshot on disk may belong to
        // a game the real instance is running right now - not a crash to recover from.
        if (!isScreenshot)
        {
            _performanceProfileService.RecoverFromCrashIfNeeded();
        }
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
        _launcherService = new ProcessLauncherService(_storageService, _performanceProfileService, _gameScriptService, _steamScannerService, _gogScannerService, _eaScannerService, _epicScannerService, _ubisoftScannerService, _xboxScannerService, _battleNetScannerService);
        _hotkeyManager = new HotkeyManager();
        _startupManager = new StartupManager();
        _startupManager.ReconcilePath();
        _trayPromotionService = new TrayPromotionService();
        TrayPromotionService.CleanStaleRegistrations();

        // Initialize ViewModel & MainWindow
        Log("Initializing ViewModel...");
        // Counts real sessions only, for the status bar's hotkey hint (UX-14e); saved with the
        // rest of the settings on exit.
        if (!isScreenshot) startupSettings.SessionsStarted++;

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
            _battleNetScannerService,
            _launcherService,
            _hotkeyManager,
            _startupManager,
            _trayPromotionService);

        // The Run key is the truth for "Start with Windows" - the installer writes it when the
        // user ticks the task, Task Manager's Startup tab can disable it, and a fresh install
        // therefore starts with a settings.json that says false while Windows is in fact set to
        // launch us. Reconciling only when the Settings page opened left that stored value stale
        // until the user happened to go there.
        _mainViewModel.SettingsVM.ReconcileStartWithWindows();

        // Constructing MainWindow initializes WPF's D3D render stack (GPU driver DLLs load
        // immediately) even if it's never shown, so skip it when starting minimized to the tray -
        // ShowMainWindow() constructs it lazily on first use. Screenshot mode still needs it up
        // front since the dev args processed below operate on _mainWindow directly.
        if (!startMinimized || isScreenshot)
        {
            Log("Initializing MainWindow...");
            // A capture or test run must not stop on the Welcome / migration / metadata prompts,
            // nor let those prompts write settings.json (see _skipSettingsSaveOnExit).
            _mainWindow = new MainWindow(_mainViewModel) { SuppressOneTimePrompts = isScreenshot };
            MainWindow = _mainWindow;
        }

#if DEBUG
        ProcessDevArguments(e);
        if (_isShuttingDown) return;
#endif
        if (TryHandleReleaseScreenshot(e.Args)) return;

        // Global Hotkey Trigger
        _hotkeyManager.ManageHotkeyTriggered += OnManageHotkeyTriggered;
        _hotkeyManager.TrayMenuHotkeyTriggered += OnTrayMenuHotkeyTriggered;

        // Auto-refresh tray menu when games change
        _mainViewModel.LibraryUpdated += UpdateTrayContextMenu;
        // The "Now Playing" tray section follows the launcher's session registry directly.
        _launcherService.SessionStarted += _ => { UpdateTrayContextMenu(); UpdateTrayToolTip(); };
        // Without this the tray stays on the text SessionStarted painted - "Starting <game> via
        // Steam" - for as long as the game runs, because nothing else re-reads GameStarted.
        _launcherService.SessionGameStarted += _ => { UpdateTrayContextMenu(); UpdateTrayToolTip(); };
        _launcherService.SessionEnded += _ => { UpdateTrayContextMenu(); UpdateTrayToolTip(); };

        // The launch popup: what a game hotkey or tray-menu launch shows while the window is out of
        // sight. The launcher raises its events on background threads.
        _launchPopup = new LaunchPopupCoordinator(
            isEnabled: () => _mainViewModel?.Settings.ShowLaunchPopup == true,
            // "Minimize to system tray when launching a game" hides the window right after the
            // launch, so the in-window notice would vanish with it: the popup is used instead.
            showWhileAppInFront: () => _mainViewModel?.Settings is { } s && (s.ShowLaunchPopupOnEveryLaunch || s.MinimizeOnGameLaunch),
            isAppInFront: IsAppInFront,
            isWaitingForGame: id => _launcherService?.IsWaitingForGame(id) == true,
            showMainWindow: ShowMainWindow,
            iconFor: GetLaunchTargetIcon,
            view: new LaunchPopupHost());
        _mainViewModel.Library.LaunchPopup = _launchPopup;
        _mainViewModel.Tools.LaunchPopup = _launchPopup;
        _launcherService.SessionStarted += s => Dispatcher.BeginInvoke(() => _launchPopup?.OnSessionStarted(s.GameId, s.PlatformLabel));
        _launcherService.SessionGameStarted += s => Dispatcher.BeginInvoke(() => _launchPopup?.OnGameStarted(s.GameId));
        _launcherService.SessionEnded += s => Dispatcher.BeginInvoke(() => _launchPopup?.OnSessionEnded(s.GameId));

        // "Keep game launchers minimized when launching a game" (Settings > General > Window & Tray Icon).
        _launcherService.KeepLaunchersMinimized = () => _mainViewModel?.Settings.KeepLaunchersMinimized == true;
        // The pre-launch DLSS reapply can mark a game conflicted, and the in-session observer
        // records what loaded; both must survive a restart. Marshalled to the UI thread because
        // the observer runs on a timer and saving walks the library collection.
        _launcherService.PersistLibrary = () =>
            Dispatcher.Invoke(() => _mainViewModel?.Library.SaveGamesOnly());

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
                _launcherService?.RecordPlaytimeOnShutdown();
                FinalizePendingRemovalOnShutdown();
                SaveSettingsOnShutdown("App.SessionEnding");
                // WPF answers the session-end query by calling Shutdown(), which closes this window
                // with Closing's Cancel ignored. Without this the close ran the title-bar-X path:
                // hide, mark the tray-hide notice as seen, save settings again and show a "still
                // running" balloon - during a log-off.
                _mainWindow?.MarkExplicitExit();
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

        // Explorer only re-reads an icon's NotifyIconSettings entry when the icon is registered,
        // so a freshly written "always show" flag takes effect only after the icon is re-added.
        _trayPromotionService.PromotionApplied += ReAddTrayIcon;

        // Apply "Always show in tray" if configured. Windows creates the icon's settings entry a
        // moment after the icon first appears, so on a first run (or after an update moved the
        // exe) the first attempt lands before the entry exists: retry over the first seconds.
        if (_mainViewModel.Settings.AlwaysShowTrayIcon)
        {
            ApplyTrayPromotionWithRetry(attempt: 0);
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
            // The tooltip names the window hotkey, so it follows a change to it in Settings.
            _mainViewModel.SettingsVM.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingsViewModel.GlobalManageHotkey)) UpdateTrayToolTip();
            };
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

        // SessionStarted/SessionEnded are raised on the launcher's monitor thread, so both the
        // TaskbarIcon and the DispatcherTimer below have to be touched from the UI thread.
        // BeginInvoke rather than UpdateTrayContextMenu's Invoke: a tooltip has no return value
        // and nothing orders against it, so there is no reason to park the launcher's thread
        // until the UI thread is free. The try/catch lives inside the callback because by the
        // time it runs the caller is long gone - an escaping exception would reach the
        // dispatcher's unhandled handler and be treated as fatal.
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var sessions = _launcherService?.GetActiveSessions() ?? [];
                _trayIcon.ToolTipText = BuildTrayToolTipText(sessions, DateTime.Now, _mainViewModel?.Settings.GlobalManageHotkey);

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
        });
    }

    /// <summary>
    /// The tray tooltip for a set of live sessions. "Starting X via Steam" is only ever correct
    /// before the game itself is up: <see cref="ProcessLauncherService.SessionGameStarted"/> is
    /// what brings <see cref="UpdateTrayToolTip"/> back once it is, and without that subscription
    /// this text is painted once at dispatch and never replaced.
    /// </summary>
    internal static string BuildTrayToolTipText(IReadOnlyList<ActiveGameSession> sessions, DateTime now, string? windowHotkey = null)
    {
        string text;

        if (sessions.Count == 0)
        {
            // The window hotkey lives here once the status bar stops mentioning it (UX-14e).
            text = string.IsNullOrWhiteSpace(windowHotkey)
                ? DefaultTrayToolTip
                : $"{DefaultTrayToolTip}\n{windowHotkey} shows or hides the window";
        }
        else if (sessions.Count == 1)
        {
            var session = sessions[0];
            text = session.GameStarted
                ? $"Playing {session.Game.Name} · {FormatPlayingElapsed(now.ToUniversalTime() - session.StartedAtUtc)}"
                : $"Starting {session.Game.Name} via {session.PlatformLabel}";
        }
        else
        {
            text = $"Playing {sessions.Count} games · {string.Join(", ", sessions.Select(s => s.Game.Name))}";
        }

        return text.Length > MaxTrayToolTipLength
            ? text[..(MaxTrayToolTipLength - 1)].TrimEnd() + "…"
            : text;
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
            // Replacing the menu while it is open closes it, and would throw away a search in
            // progress (UX-16). Build the new one once this one closes instead.
            if (_trayIcon.ContextMenu is { IsOpen: true })
            {
                _trayMenuRebuildPending = true;
                return;
            }

            var menu = new ContextMenu();

            var games = _mainViewModel.Games.Where(g => !g.Game.IsHidden).ToList();

            if (games.Count == 0)
            {
                var emptyItem = new MenuItem
                {
                    Header = "No games in library",
                    Style = TrayItemStyle,
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
                            ? $"{Math.Max(0, (DateTime.UtcNow - session.StartedAtUtc).TotalMinutes):0}m"
                            : $"starting via {session.PlatformLabel}";
                        var sessionItem = card != null
                            ? CreateGameMenuItem(card)
                            : new MenuItem { Header = session.Game.Name, Style = TrayItemStyle };
                        // The one row in the menu that earns bold: it's the game you're in.
                        sessionItem.FontWeight = FontWeights.SemiBold;
                        sessionItem.Header = $"{session.Game.Name}  ·  {elapsed}";
                        sessionItem.Command = null;

                        string gameId = session.GameId;
                        string gameName = session.Game.Name;
                        var closeGame = CreateNavMenuItem("Close Game", "", () => CloseGameFromTray(gameId, gameName));
                        closeGame.ToolTip = "Asks the game to quit, as its own close button does. Once it exits, TrayTrigger restores the Performance Profile and runs the post-exit script.";
                        sessionItem.Items.Add(closeGame);
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
                        }, iconBrush: (Brush)FindResource("BrushDanger"));
                        forceClose.ToolTip = "Kills the game's process, then ends the session. Anything unsaved in the game is lost.";
                        forceClose.IsEnabled = session.CanForceClose;
                        sessionItem.Items.Add(forceClose);
                        menu.Items.Add(sessionItem);
                    }
                    menu.Items.Add(new Separator());
                }

                // The Favorites section's games, worked out before Recent so that Recent can leave
                // them out: a favorite you played yesterday was listed twice, one row apart.
                var favoriteGames = _mainViewModel.Settings.ShowFavoritesInTray && _mainViewModel.Settings.MaxFavoritesInTray > 0
                    ? ApplySortOption(
                        games.Where(g => g.Game.IsFavorite),
                        _mainViewModel.Settings.FavoritesTraySortOption)
                        .Take(_mainViewModel.Settings.MaxFavoritesInTray)
                        .ToList()
                    : [];

                // 1. Persistent "Recent" section directly in root menu
                bool anyGamePlayed = games.Any(g => g.Game.LastPlayed.HasValue);
                var recentIds = TrayMenuSections.SelectRecent(
                    games.Select(g => (g.Id, g.Game.LastPlayed)),
                    favoriteGames.Select(g => g.Id),
                    _mainViewModel.Settings.MaxRecentInTray);
                // Shown when it has rows, or when nothing has been played yet (the placeholder says
                // so). Left out when every recent game is already listed under Favorites.
                bool showRecent = _mainViewModel.Settings.ShowRecentInTray && _mainViewModel.Settings.MaxRecentInTray > 0
                    && (recentIds.Count > 0 || !anyGamePlayed);
                if (showRecent)
                {
                    var recentSet = new HashSet<string>(recentIds, StringComparer.Ordinal);
                    var recentGames = ApplySortOption(
                        games.Where(g => recentSet.Contains(g.Id)),
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
                            Style = TrayItemStyle,
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
                bool favoritesSectionShown = favoriteGames.Count > 0;
                if (favoritesSectionShown)
                {
                    menu.Items.Add(CreateSectionHeader(LibraryConstants.FavoritesCategory));
                    foreach (var card in favoriteGames)
                    {
                        menu.Items.Add(CreateGameMenuItem(card));
                    }
                    menu.Items.Add(new Separator());
                }

                // The main list gets its own "All Games" / "Categories" header whenever any
                // section precedes it, so it never runs straight on from Recent or Favorites.
                bool mainListNeedsHeader = activeSessions.Count > 0
                    || showRecent
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
                                Style = TrayItemStyle
                            };
                            if (_mainViewModel.Settings.ShowTrayMenuIcons)
                            {
                                categoryMenu.Icon = CreateTrayGlyph("\uED25");
                            }

                            foreach (var card in ApplyTraySort(group))
                            {
                                categoryMenu.Items.Add(CreateGameMenuItem(card));
                            }

                            menu.Items.Add(categoryMenu);
                        }
                    }
                }
            }

            // Tools: one flat submenu after the games, in its own sort order. Only with Tools on and its
            // tray option ticked (both off by default); no categories or favorites section here.
            if (_mainViewModel.Settings.EnableTools && _mainViewModel.Settings.ShowToolsInTray)
            {
                var tools = _mainViewModel.Tools.TrayTools();
                if (tools.Count > 0)
                {
                    var toolsMenu = new MenuItem
                    {
                        Header = $"Tools ({tools.Count})",
                        Style = TrayItemStyle
                    };
                    if (_mainViewModel.Settings.ShowTrayMenuIcons)
                    {
                        toolsMenu.Icon = CreateTrayGlyph("");
                    }
                    foreach (var tool in tools)
                    {
                        toolsMenu.Items.Add(CreateToolMenuItem(tool));
                    }
                    menu.Items.Add(toolsMenu);
                }
            }

            // Where the navigation block starts: the search box leaves everything from here on alone.
            int navStart = menu.Items.Count;
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

            var exitBrush = (Brush)FindResource("BrushDanger");
            var exitItem = CreateNavMenuItem("Exit TrayTrigger", "\uE7E8", ExitApplication, exitBrush, exitBrush);
            menu.Items.Add(exitItem);

            // The optional search row at the very top (UX-16); with the setting off the menu is
            // exactly as before. Nothing to search in an empty library.
            if (_mainViewModel.Settings.ShowTraySearch && games.Count > 0)
            {
                AttachTraySearch(menu, navStart, games);
            }
            menu.Closed += OnTrayMenuClosed;

            _trayIcon.ContextMenu = menu;

            // Left-click: the window (default) or this menu. Applied here, because this is the
            // one place that runs both at startup and whenever a tray setting changes.
            if (_mainViewModel.Settings.TrayLeftClickOpensMenu)
            {
                _trayIcon.LeftClickCommand = null;
                _trayIcon.MenuActivation = H.NotifyIcon.Core.PopupActivationMode.LeftOrRightClick;
            }
            else
            {
                _trayIcon.LeftClickCommand ??= new RelayCommand(ToggleMainWindow);
                _trayIcon.MenuActivation = H.NotifyIcon.Core.PopupActivationMode.RightClick;
            }
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
            Style = TrayItemStyle,
            Command = new RelayCommand(onClick)
        };

        if (textBrush != null)
        {
            item.Foreground = textBrush;
        }

        // Nav glyphs stay even with game icons off: a menu with no marks at all reads as one
        // undifferentiated list, and there are only four of them.
        item.Icon = CreateTrayGlyph(iconGlyph, iconBrush);
        return item;
    }

    private MenuItem CreateGameMenuItem(GameCardViewModel card)
    {
        var item = new MenuItem
        {
            Header = card.Name,
            Style = TrayItemStyle,
            Command = new RelayCommand(() => _mainViewModel.LaunchGame(card))
        };

        if (!_mainViewModel.Settings.ShowTrayMenuIcons)
        {
            return item;
        }

        // The game's own icon; failing that its launcher's logo, so it still reads as "the Steam
        // one". The generic controller is reserved for a local game with no icon - it is also the
        // Games Library glyph, and a whole menu of it made games look like navigation.
        var source = card.IconImage ?? GetLauncherLogo(card.Game);
        if (source != null)
        {
            item.Icon = CreateTrayIconTile(source);
        }
        else
        {
            item.Icon = CreateTrayGlyph("\uE7FC", (Brush)FindResource("BrushTextMuted"));
        }

        return item;
    }

    private MenuItem CreateToolMenuItem(ToolCardViewModel card)
    {
        var item = new MenuItem
        {
            Header = card.Name,
            Style = TrayItemStyle,
            Command = new RelayCommand(() => _mainViewModel.Tools.Launch(card))
        };

        if (!_mainViewModel.Settings.ShowTrayMenuIcons)
        {
            return item;
        }

        if (card.IconImage != null)
        {
            item.Icon = CreateTrayIconTile(card.IconImage);
        }
        else
        {
            item.Icon = CreateTrayGlyph("", (Brush)FindResource("BrushTextMuted"));
        }

        return item;
    }

    // ------------------------------------------------------------------ tray menu helpers

    /// <summary>
    /// Every game and tool icon in the menu as the same thing: a rounded square of one size. Icons
    /// arrive as full-bleed box art, round logos on transparency and ordinary exe icons, and side by
    /// side they read as three different kinds of row. Art fills the tile and is clipped to its
    /// corners; a transparent logo sits on the tile's faint backing, so it occupies the same shape.
    /// </summary>
    private FrameworkElement CreateTrayIconTile(ImageSource source)
    {
        double size = TrayIconSize;
        double radius = TrayCompact ? 3 : 4;

        var image = new Image
        {
            Source = source,
            Width = size,
            Height = size,
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(radius),
            Background = TrayIconTileBrush,
            Clip = new RectangleGeometry(new Rect(0, 0, size, size), radius, radius),
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
            Child = image
        };
    }

    private static readonly Brush TrayIconTileBrush = CreateFrozenBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private bool TrayCompact => _mainViewModel?.Settings.CompactTrayMenu == true;

    private Style TrayItemStyle => (Style)FindResource(TrayCompact ? "TrayMenuCompactItemStyle" : "TrayMenuItemStyle");

    private int TrayIconSize => TrayCompact ? 16 : 20;

    /// <summary>A Segoe glyph sized to the same column as a game icon, so rows line up.</summary>
    private TextBlock CreateTrayGlyph(string glyph, Brush? brush = null)
    {
        var block = new TextBlock
        {
            Text = glyph,
            Style = (Style)FindResource("MenuGlyphIcon"),
            Width = TrayIconSize,
            FontSize = TrayCompact ? 11 : 13
        };
        if (brush != null) block.Foreground = brush;
        return block;
    }

    private LaunchPopupCoordinator? _launchPopup;

    /// <summary>
    /// The TrayTrigger window is on screen and one of TrayTrigger's windows has focus, so the
    /// in-window launch toast and dialogs can be seen. The tray menu is a popup rather than a
    /// window, so a launch from it counts as out of sight.
    /// </summary>
    private bool IsAppInFront()
    {
        if (_mainWindow == null || !_mainWindow.IsVisible || _mainWindow.WindowState == WindowState.Minimized) return false;
        foreach (Window window in Windows)
        {
            if (window.IsActive && window is not LaunchPopupWindow) return true;
        }
        return false;
    }

    /// <summary>The launch popup's picture: the entry's own icon, else its fallback logo.</summary>
    private ImageSource? GetLaunchTargetIcon(LaunchTarget target) =>
        _mainViewModel?.Library.Games.FirstOrDefault(c => c.Id == target.Id)?.IconImage
        ?? _mainViewModel?.Tools.FindCard(target.Id)?.IconImage
        ?? GetLauncherLogo(target.FallbackIconUri);

    // Decoded once per platform: the menu is rebuilt on every launch, exit and library change.
    private readonly Dictionary<string, BitmapImage> _launcherLogoCache = new(StringComparer.Ordinal);

    private BitmapImage? GetLauncherLogo(GameEntry game) => GetLauncherLogo(LauncherLogos.PackUriFor(game));

    private BitmapImage? GetLauncherLogo(string? uri)
    {
        if (uri == null) return null;
        if (_launcherLogoCache.TryGetValue(uri, out var cached)) return cached;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(uri, UriKind.Absolute);
            image.DecodePixelWidth = 40;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            _launcherLogoCache[uri] = image;
            return image;
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("Tray", $"Launcher logo '{uri}' failed to load: {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------ "always show in tray"

    private static readonly int[] TrayPromotionRetryDelaysMs = [1000, 2000, 4000, 8000];

    private void ApplyTrayPromotionWithRetry(int attempt)
    {
        var outcome = _trayPromotionService.TrySetAlwaysShow(true, out string message);
        LoggingService.Info("App", $"Always show in tray (attempt {attempt + 1}): {outcome} - {message}");

        if (outcome == TrayPromotionService.Outcome.NotRegisteredYet && attempt < TrayPromotionRetryDelaysMs.Length)
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TrayPromotionRetryDelaysMs[attempt]) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!_isShuttingDown) ApplyTrayPromotionWithRetry(attempt + 1);
            };
            timer.Start();
            return;
        }

        if (_mainViewModel?.SettingsVM != null) _mainViewModel.SettingsVM.TrayPromotionStatus = message;
    }

    /// <summary>
    /// Removes and re-adds the shell icon so Explorer looks up its NotifyIconSettings entry again.
    /// TaskbarIcon maps its Visibility onto the shell icon, so a collapse/show round trip is a
    /// delete and re-add without rebuilding the menu or tooltip.
    /// </summary>
    private void ReAddTrayIcon()
    {
        if (_trayIcon == null || _isShuttingDown) return;
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                _trayIcon.Visibility = Visibility.Collapsed;
                _trayIcon.Visibility = Visibility.Visible;
                LoggingService.Verbose("App", "Tray icon re-added so Windows re-reads its 'always show' setting.");
            }
            catch (Exception ex)
            {
                LoggingService.Warn("App", $"Could not re-add the tray icon: {ex.Message}");
            }
        });
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

            // The window is cloaked until a painted frame is on screen - on the first show by
            // PrepareForFirstShow, and when it comes back from the tray (it hides rather than
            // closes) by ShowCloaked, so neither shows a white box. The activation dance waits
            // until then so it doesn't compete with the paint.
            var window = _mainWindow;
            WindowThemeService.ShowCloaked(window, () =>
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
                // Close the failed window first: until it closes it is still subscribed to the view
                // model's dialog requests, and every request would open a dialog from both windows.
                if (_mainWindow is { } failed)
                {
                    try { failed.MarkExplicitExit(); failed.Close(); }
                    catch (Exception closeEx) { LoggingService.Verbose("App", $"Closing the failed MainWindow threw: {closeEx.Message}"); }
                }
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

    /// <summary>
    /// Close Game from the tray's Now Playing submenu. Off the UI thread, since it waits for the
    /// game to quit; the window may be hidden, so a game that didn't close says so in a notification.
    /// </summary>
    private void CloseGameFromTray(string gameId, string gameName)
    {
        Task.Run(() =>
        {
            var result = _launcherService.CloseGameNow(gameId);
            if (result is ProcessLauncherService.CloseGameResult.StillRunning or ProcessLauncherService.CloseGameResult.CouldNotAsk)
            {
                Dispatcher.BeginInvoke(() => _trayIcon?.ShowNotification("TrayTrigger", LibraryViewModel.DescribeCloseGame(result, gameName)));
            }
        });
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
            _launcherService?.RecordPlaytimeOnShutdown();
            FinalizePendingRemovalOnShutdown();

            SaveSettingsOnShutdown("App.ExitApplication");

            _trayToolTipTimer?.Stop();
            _trayToolTipTimer = null;
            _hotkeyManager?.Dispose();
            _launchPopup?.Dispose();
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
            Shutdown(_exitCode);
            Environment.Exit(_exitCode);
        }
    }

    /// <summary>
    /// A game removed in the last 6 seconds is still inside its undo window, which lives only in
    /// memory - finish its cleanup (cached art, Steam/RAWG details) before the process goes away.
    /// Isolated so a failure here can't skip the settings save or profile restore around it.
    /// </summary>
    /// <summary>
    /// Headless: no window, no tray icon, no single-instance check. Reads the library, puts every
    /// DLSS Override back, saves, and exits. Never throws - an uninstall must not stall on it.
    /// </summary>
    private void RestoreDlssOverridesAndExit()
    {
        try
        {
            var storage = new StorageService();
            var games = storage.LoadGames();
            var result = new DlssOverrideService().RestoreAll(games);
            if (result.Games > 0) storage.SaveGames(games);
            LoggingService.Info("App", $"--restore-dlss: {result.Games} game(s) had the DLSS Override on, {result.Failed} could not be put back.");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("App", $"--restore-dlss failed: {ex.Message}");
        }
        Shutdown(0);
    }

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

    /// <summary>
    /// The one settings write of a shutdown. Every exit path ends here - the tray Exit, a Windows
    /// log-off, WPF's Exit and the ProcessExit fallback - and the first to run wins: the ones after
    /// it would only encrypt and replace settings.json again with the same object. The release
    /// screenshot never writes (<see cref="_skipSettingsSaveOnExit"/>): its window was resized for
    /// the capture and its settings may be shared with a running instance.
    /// </summary>
    private void SaveSettingsOnShutdown(string source)
    {
        if (_skipSettingsSaveOnExit || _settingsSavedOnExit || _mainViewModel == null || _storageService == null)
        {
            LoggingService.Verbose("App", $"{source}: settings already saved (or not to be saved); skipping.");
            return;
        }

        _storageService.SaveSettings(_mainViewModel.Settings, source: source);
        _settingsSavedOnExit = true;
        LoggingService.Info("App", $"Settings saved during shutdown ({source}).");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LoggingService.Info("App", $"OnExit called. ExitCode={e.ApplicationExitCode}");
        FinalizePendingRemovalOnShutdown();
        try
        {
            SaveSettingsOnShutdown("App.OnExit");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("App", $"Failed to save settings during OnExit: {ex.Message}");
        }
        _hotkeyManager?.Dispose();
        _launchPopup?.Dispose();
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
