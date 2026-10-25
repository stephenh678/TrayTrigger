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

    /// <summary>Checks whether an automatic backup is due: shortly after start, then once a day.</summary>
    private System.Windows.Threading.DispatcherTimer? _autoBackupTimer;

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
        // Before anything else can fail: a crash from here on is written to crash.log and the person is told.
        CrashReport.Install();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        CrashReport.Quiet = IsUnattendedRun(e.Args);
        try
        {
            StartApp(e);
        }
        catch (Exception ex)
        {
            string? file = CrashReport.Write("startup", ex);
            LoggingService.Error("App", "TrayTrigger failed while starting.", ex);

            // With the tray icon up the app is usable: the dispatcher handler below logs this one and says so, as it always did.
            if (_trayIcon != null) throw;

            // No tray icon means no window and no way back into the app, so a process left running would only be a hidden
            // instance that the next launch hands over to. Say what happened, and end it.
            CrashReport.Show(atStart: true, file);
            _isShuttingDown = true;
            Shutdown(1);
            Environment.Exit(1);
        }
    }

    /// <summary>
    /// A run nobody is watching, which must not wait on a message box: a capture or a test (the screenshot and test switches), or the
    /// uninstaller's call that puts the DLSS overrides back.
    /// </summary>
    internal static bool IsUnattendedRun(string[] args) =>
        IsReleaseScreenshotRequest(args) || args.Any(a =>
            a.StartsWith("--screenshot", StringComparison.OrdinalIgnoreCase) || a.StartsWith("-screenshot", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("--test", StringComparison.OrdinalIgnoreCase) || a.StartsWith("-test", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--restore-dlss", StringComparison.OrdinalIgnoreCase) ||
            a.Equals(ElevatedHelper.Switch, StringComparison.Ordinal));

    /// <summary>The theme, text size and font, before any window exists. A command-line choice (a capture or a test) wins over the setting and is not saved.</summary>
    private static void ApplyStartupAppearance(AppSettings startupSettings, string[] args)
    {
        // Before any window exists, so the first frame already has the chosen colors. "--theme <id>" (a
        // capture or a test) wins over the setting and is not saved.
        TrayTrigger.Services.Theming.ThemeService.Instance.ShowBackground = startupSettings.ShowThemeBackground;
        // The text size: the one in Settings, unless the theme sets its own. "--text-scale <percent>" (a capture) wins over both and is not saved.
        TrayTrigger.Services.AppTypography.SystemPercent = Math.Clamp(startupSettings.TextSizePercent,
            TrayTrigger.Services.AppTypography.MinScalePercent, TrayTrigger.Services.AppTypography.MaxScalePercent);
        int scaleArg = Array.FindIndex(args, a => a.Equals("--text-scale", StringComparison.OrdinalIgnoreCase));
        if (scaleArg >= 0 && scaleArg + 1 < args.Length
            && double.TryParse(args[scaleArg + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double scalePercent))
            TrayTrigger.Services.AppTypography.OverrideScale = scalePercent / 100.0;
        // The font: the one in Settings. "--font <name>" (a capture) wins over it and is not saved.
        TrayTrigger.Services.AppFonts.SystemName = TrayTrigger.Services.AppFonts.Find(startupSettings.FontName) ?? string.Empty;
        int fontArg = Array.FindIndex(args, a => a.Equals("--font", StringComparison.OrdinalIgnoreCase));
        if (fontArg >= 0 && fontArg + 1 < args.Length) TrayTrigger.Services.AppFonts.OverrideName = args[fontArg + 1];
        int themeArg = Array.FindIndex(args, a => a.Equals("--theme", StringComparison.OrdinalIgnoreCase));
        TrayTrigger.Services.Theming.ThemeService.Instance.Apply(
            themeArg >= 0 && themeArg + 1 < args.Length ? args[themeArg + 1] : startupSettings.ThemeId);
    }

    /// <summary>The last-resort save on process exit, the exit log line, and what an unhandled exception on the UI thread does.</summary>
    private void HookProcessLifetime()
    {
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
                ShowTrayNotification("TrayTrigger", "Too many errors occurred; TrayTrigger is closing. Check the log for details.");
                args.Handled = true;
                ExitApplication();
                return;
            }

            // Keep the tray app alive after an isolated exception rather than letting WPF
            // terminate the process - but a silently swallowed exception is invisible to the
            // user, so it goes in Activity & History, whose notification says so once no game is
            // running. See L-24. Without the history (very early in startup) the old toast stands in.
            if (ActivityService.Current != null)
            {
                ActivityService.Add(ActivityLevel.Problem, "Something went wrong inside TrayTrigger; it's still running",
                    detail: $"{args.Exception.GetType().Name}: {args.Exception.Message}. Show in log has the details.",
                    groupKey: $"app.error|{args.Exception.GetType().Name}");
            }
            else
            {
                ShowTrayNotification("TrayTrigger", "An error was logged. The app is still running - check the log if something looks wrong.");
            }
            args.Handled = true;
        };
    }

    /// <summary>The launch popup and the tools that start with a game or close for it, hooked to the launcher's session events.</summary>
    private void WireLaunchPopupAndCompanionTools()
    {
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

        // Stutter Check runs once, a few seconds after startup and off the UI thread, so the System
        // page opens with a verdict rather than an empty card. Not while a game is running.
        _ = RunStutterCheckAfterStartupAsync();
        _launcherService.SessionStarted += s => Dispatcher.BeginInvoke(() => _launchPopup?.OnSessionStarted(s.GameId, s.PlatformLabel));
        _launcherService.SessionGameStarted += s => Dispatcher.BeginInvoke(() => _launchPopup?.OnGameStarted(s.GameId));
        _launcherService.SessionEnded += s => Dispatcher.BeginInvoke(() => _launchPopup?.OnSessionEnded(s.GameId));

        // Tools that start with games or close for them (Edit Tool). The popup is updated before the
        // tool starts or closes, not queued, so it already names the tool when Windows' administrator
        // prompt dims the screen; the timeout keeps a busy UI thread from holding up the launch.
        var companionTools = new CompanionToolService(
            tools: () => _mainViewModel?.Tools.ToolsSnapshot ?? [],
            isEnabled: () => _mainViewModel?.Settings.EnableTools == true);
        companionTools.Starting += (game, tool) =>
            Dispatcher.Invoke(() => _launchPopup?.OnStartingTool(game.Id, tool?.Name),
                System.Windows.Threading.DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(2));
        companionTools.Closing += (game, tool) =>
            Dispatcher.Invoke(() => _launchPopup?.OnClosingTool(game.Id, tool?.Name),
                System.Windows.Threading.DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(2));
        // A pre-launch script's "TT:" line, in the popup while the game waits. Queued: the script's
        // output thread mustn't wait on the UI, and a line a moment late is no loss.
        _gameScriptService!.Said += (game, text) => Dispatcher.BeginInvoke(() => _launchPopup?.OnScriptSaid(game.Id, text));
        companionTools.StartFailed += message => Dispatcher.BeginInvoke(() =>
        {
            if (_mainViewModel != null) _mainViewModel.Library.StatusMessage = message;
        });
        companionTools.ClosingAsAdministrator += (tool, asking) =>
        {
            if (asking)
            {
                Dispatcher.Invoke(() => _launchPopup?.ShowClosing(new LaunchTarget(tool.Id, tool.Name)),
                    System.Windows.Threading.DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(2));
            }
            else
            {
                Dispatcher.BeginInvoke(() => _launchPopup?.EndClosing(tool.Id));
            }
        };
        _launcherService.CompanionTools = companionTools;
        // A note that arrives after a game's Played row was written goes onto that row.
        LaunchRecord.LateNote = _launcherService.NoteOnPlayedRow;
    }

    private void StartApp(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);

        LoggingService.EnsureLogFileExists();
        _storageService = new StorageService();
        // Started by RestartApplication: the TrayTrigger that started this one still holds the
        // single-instance mutex, and is still writing its settings on the way out.
        WaitForPreviousInstanceToExit(e.Args);
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

        TrayTrigger.Views.ComboBoxWheelGuard.Register();

        ApplyStartupAppearance(startupSettings, e.Args);

        // Drop installers left in %TEMP% by earlier in-app updates (off the UI thread: it's
        // file I/O against a folder that may hold several 50 MB files).
        _ = Task.Run(UpdateService.CleanupDownloadedInstallers);

        HookProcessLifetime();

        Log("Application starting...");

        // The uninstaller's last call: the override lives in NVIDIA's driver, not in TrayTrigger,
        // so removing the program would otherwise leave every game's setting behind for good.
        if (e.Args.Any(a => a.Equals("--restore-dlss", StringComparison.OrdinalIgnoreCase)))
        {
            RestoreDlssOverridesAndExit();
            return;
        }

        // The elevated step of a Performance Profile, started under the one administrator prompt: this same program run
        // again with administrator rights to make the changes the NVIDIA driver won't take from a normal process (and
        // to import the registry changes in the same prompt), then it exits. It never gets as far as a window or the
        // single-instance check. See ElevatedHelper.
        if (e.Args.Any(a => a.Equals(ElevatedHelper.Switch, StringComparison.Ordinal)))
        {
            Shutdown(ElevatedHelper.RunAsHelper(e.Args));
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

            // A restore unpacked by the last run goes in place now: this is the only instance, and
            // nothing has read the library yet. The settings read above were the old ones.
            if (BackupService.HasPendingRestore(_storageService.BaseDirectory))
            {
                _restoreResult = new BackupService(_storageService).ApplyPendingRestore();
                startupSettings = _storageService.LoadSettings();
                LoggingService.Initialize(startupSettings.VerboseLoggingEnabled);
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
        // Activity & History, before anything that records into it (crash recovery, resuming a
        // suspended game). A capture run reads the history but writes none of it.
        ActivityService.Current = new ActivityService(_storageService.BaseDirectory, readOnly: isScreenshot,
            retentionDays: () => (_mainViewModel?.Settings ?? startupSettings).ActivityRetentionDays);
        if (!isScreenshot) NoteVersionChange(startupSettings);
        _performanceProfileService = new PerformanceProfileService(
            _storageService,
            () => _mainViewModel?.Settings ?? startupSettings,
            new WindowsTweakBackend(),
            new NvApiDrsBackend());
        // A screenshot run skips the single-instance check, so the snapshot on disk may belong to
        // a game the real instance is running right now - not a crash to recover from.
        if (!isScreenshot)
        {
            _performanceProfileService.RecoverFromCrashIfNeeded();
            DisplayModeService.Shared.RecoverIfNeeded();
            foreach (var audio in AudioOutputService.Both) audio.RecoverIfNeeded();
            // An Ultimate plan made before 1.5.1 still unparks every core on a dual-CCD Ryzen X3D;
            // a plan left on all the time may not be applied again for months. Returns at once elsewhere.
            _ = Task.Run(SystemTweaksService.KeepX3dCoreParkingInExistingPlans);
        }
        // The Settings "Enable game scripts" switch is enforced here, not just in the edit dialog.
        _gameScriptService = new GameScriptService(
            () => (_mainViewModel?.Settings ?? startupSettings).EnableGameScripts,
            () => (_mainViewModel?.Settings ?? startupSettings).ScriptDefaults);
        _launcherService = new ProcessLauncherService(_storageService, _performanceProfileService, _gameScriptService, _steamScannerService, _gogScannerService, _eaScannerService, _epicScannerService, _ubisoftScannerService, _xboxScannerService, _battleNetScannerService);
        // A game left frozen by Suspend when TrayTrigger last stopped without exiting has nothing
        // else that will ever resume it. Same screenshot-run exception as the profile recovery.
        if (!isScreenshot)
        {
            _launcherService.ResumeLeftSuspended();
        }
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
        // One-time changes this release makes to an existing settings file. Not for a capture run,
        // which must not write settings.json.
        if (!isScreenshot && startupSettings.ApplyOneTimeUpgrades())
            _storageService.SaveSettings(startupSettings);

        // The dialogs that can be resized open at the size they were last left at.
        TrayTrigger.Services.WindowSizeMemory.Configure(() => _mainViewModel?.Settings ?? startupSettings, s => { if (!_skipSettingsSaveOnExit) _storageService.SaveSettings(s); });
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

        // The examples and README land in the scripts folder once the feature is on, and a stale
        // one is replaced with this build's copy, so a correction to an example reaches a folder
        // that already exists. The user's own scripts are not bundled
        // names and are never touched. After the library and tools load, so an example this build
        // no longer ships is kept while anything still runs it. Off the UI thread: file I/O.
        if (startupSettings.EnableGameScripts)
        {
            var scriptLibrary = new ScriptLibraryService(_storageService.BaseDirectory);
            // A capture run's library is not the user's, so it must not decide what is unused.
            var scriptsInUse = isScreenshot ? null : ScriptPathsInUse(_mainViewModel, startupSettings);
            _ = Task.Run(() => scriptLibrary.EnsureInstalled(scriptsInUse));
        }

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
        _hotkeyManager.SuspendHotkeyTriggered += OnSuspendHotkeyTriggered;

        // Auto-refresh tray menu when games change
        _mainViewModel.LibraryUpdated += UpdateTrayContextMenu;
        // The "Now Playing" tray section follows the launcher's session registry directly.
        _launcherService.SessionStarted += _ => { UpdateTrayContextMenu(); UpdateTrayToolTip(); };
        // Without this the tray stays on the text SessionStarted painted - "Starting <game> via
        // Steam" - for as long as the game runs, because nothing else re-reads GameStarted.
        _launcherService.SessionGameStarted += _ => { UpdateTrayContextMenu(); UpdateTrayToolTip(); };
        _launcherService.SessionEnded += _ => { UpdateTrayContextMenu(); UpdateTrayToolTip(); };
        _launcherService.SessionSuspendChanged += _ => { UpdateTrayContextMenu(); UpdateTrayToolTip(); };

        WireLaunchPopupAndCompanionTools();

        // "Keep game launchers minimized when launching a game" (Settings > Launch & Performance > Launching Games).
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
                _launcherService?.ResumeAllOnShutdown();
                _performanceProfileService?.RestoreActiveSessionOnShutdown(skipElevated: true);
                DisplayModeService.Shared.ReleaseAll();
                CursorLockService.Shared.ReleaseAll();
                ScreenDimService.Shared.ReleaseAll();
                foreach (var audio in AudioOutputService.Both) audio.ReleaseAll();
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
        _mainViewModel.RequestExitApplication += RequestExit;
        _mainViewModel.SettingsVM.RequestRestart += RestartApplication;
        _mainViewModel.RequestTrayNotification += (title, message) => ShowTrayNotification(title, message);

        // Setup Taskbar Tray Icon
        Log("Initializing Tray Icon...");
        InitializeTrayIcon();
        InitializeActivityNotifications();
        if (!isScreenshot) StartAutoBackupChecks();

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
        if (!isScreenshot) NotePutBackWaitingAtStart(windowWillShow: !startMinimized);
        if (!startMinimized)
        {
            Log("Showing MainWindow...");
            ShowMainWindow();
        }

        if (_restoreResult != null)
        {
            var result = _restoreResult;
            _restoreResult = null;
            Dispatcher.BeginInvoke(() => ShowRestoreResult(result), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        Log("OnStartup completed successfully.");
    }

    private void StartAutoBackupChecks()
    {
        var settingsVm = _mainViewModel.SettingsVM;
        // A moment after start, so the backup doesn't compete with the library loading; then once a day.
        _autoBackupTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _autoBackupTimer.Tick += (_, _) =>
        {
            _autoBackupTimer!.Interval = TimeSpan.FromHours(24);
            settingsVm.RunAutoBackupIfDue();
        };
        _autoBackupTimer.Start();
        // A backup that waited for a game is taken when the last one exits.
        _launcherService.SessionEnded += _ => Dispatcher.BeginInvoke(() => settingsVm.RunAutoBackupIfDue(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Every script path something could run: each game's, the defaults, and each tool's
    /// target. Read on the UI thread, which owns the library and tools collections.</summary>
    private static List<string> ScriptPathsInUse(MainViewModel vm, AppSettings settings)
    {
        var paths = new List<string>
        {
            settings.ScriptDefaults.PreLaunchScriptPath,
            settings.ScriptDefaults.PostExitScriptPath,
        };
        foreach (var card in vm.Library.Games)
        {
            paths.Add(card.Game.PreLaunchScriptPath);
            paths.Add(card.Game.PostExitScriptPath);
        }
        foreach (var tool in vm.Tools.ToolsSnapshot) paths.Add(tool.TargetPath);
        return paths;
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
        ShowPutBackPromptIfPending();

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
                // Coming back to a window left on Activity & History: what's on it has now been seen.
                if (_mainViewModel?.IsActivityView == true) _mainViewModel.ActivityVM.MarkViewed();
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
    /// The first Stutter Check: a few seconds in, once the window is idle, and not while a game is
    /// running. Nothing awaits it, so a failure is logged here or it would vanish.
    /// </summary>
    private async System.Threading.Tasks.Task RunStutterCheckAfterStartupAsync()
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(8));
            if (_isShuttingDown || _mainViewModel == null) return;
            // Let whatever the window is drawing finish first.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (_isShuttingDown) return;
            bool gameRunning = _launcherService.GetActiveSessions().Count > 0;
            await _mainViewModel.SystemVM.RunStutterCheckIfDueAsync(gameRunning, atStartup: true);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("StutterCheck", $"The startup check didn't run: {ex.Message}");
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
                Dispatcher.BeginInvoke(() => ShowTrayNotification("TrayTrigger", LibraryViewModel.DescribeCloseGame(result, gameName)));
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
            // First: a game left frozen would stay frozen, with no tray icon left to resume it.
            _launcherService?.ResumeAllOnShutdown();
            _performanceProfileService?.RestoreActiveSessionOnShutdown();
            DisplayModeService.Shared.ReleaseAll();
            CursorLockService.Shared.ReleaseAll();
            ScreenDimService.Shared.ReleaseAll();
            foreach (var audio in AudioOutputService.Both) audio.ReleaseAll();
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
