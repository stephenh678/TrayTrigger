using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
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
    private ProcessLauncherService _launcherService = null!;
    private HotkeyManager _hotkeyManager = null!;
    private StartupManager _startupManager = null!;
    private TrayPromotionService _trayPromotionService = null!;
    private MainViewModel _mainViewModel = null!;

    private TaskbarIcon? _trayIcon;
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
        try
        {
            var startupSettings = _storageService.LoadSettings();
            LoggingService.Initialize(startupSettings.VerboseLoggingEnabled);
        }
        catch
        {
            LoggingService.Initialize(false);
        }
        void Log(string msg) => LoggingService.Info("App", msg);
        _logger = Log;

        AppDomain.CurrentDomain.ProcessExit += (s, args) =>
        {
            try
            {
                if (_mainViewModel != null && _storageService != null)
                {
                    _storageService.SaveSettings(_mainViewModel.Settings);
                }
            }
            catch { }
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
        _launcherService = new ProcessLauncherService(_storageService);
        _hotkeyManager = new HotkeyManager();
        _startupManager = new StartupManager();
        _startupManager.ReconcilePath();
        _trayPromotionService = new TrayPromotionService();
        TrayPromotionService.CleanStaleRegistrations();

        // Initialize ViewModel & MainWindow
        Log("Initializing ViewModel...");
        _mainViewModel = new MainViewModel(
            _storageService,
            _shortcutService,
            _iconExtractorService,
            _steamScannerService,
            _launcherService,
            _hotkeyManager,
            _startupManager,
            _trayPromotionService);

        Log("Initializing MainWindow...");
        _mainWindow = new MainWindow(_mainViewModel);
        MainWindow = _mainWindow;

#if DEBUG
        ProcessDevArguments(e);
        if (_isShuttingDown) return;
#endif

        // Global Hotkey Trigger
        _hotkeyManager.ManageHotkeyTriggered += OnManageHotkeyTriggered;

        // Auto-refresh tray menu when games change
        _mainViewModel.LibraryUpdated += UpdateTrayContextMenu;
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
                ToolTipText = "TrayTrigger - Game Launcher",
                LeftClickCommand = new RelayCommand(ToggleMainWindow),
                DoubleClickCommand = new RelayCommand(ShowMainWindow)
            };

            // Attempt to load native Icon from application resources, fallback to file
            System.Drawing.Icon? loadedIcon = null;
            try
            {
                var iconUri = new Uri("pack://application:,,,/Assets/app_icon.ico", UriKind.Absolute);
                var resInfo = GetResourceStream(iconUri);
                if (resInfo?.Stream != null)
                {
                    using var stream = resInfo.Stream;
                    loadedIcon = new System.Drawing.Icon(stream);
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
                    loadedIcon = new System.Drawing.Icon(diskPath);
                }
            }

            if (loadedIcon != null)
            {
                _trayIcon.Icon = loadedIcon;
            }

            UpdateTrayContextMenu();
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

    public void UpdateTrayContextMenu()
    {
        if (_trayIcon == null) return;

        Dispatcher.Invoke(() =>
        {
            var menu = new ContextMenu();

            var games = _mainViewModel.Games.ToList();

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
                // 1. Persistent "Recent" section directly in root menu
                if (_mainViewModel.Settings.ShowRecentInTray)
                {
                    int maxRecent = _mainViewModel.Settings.MaxRecentInTray > 0 ? _mainViewModel.Settings.MaxRecentInTray : LibraryConstants.DefaultTrayItemCount;
                    var recentGames = ApplySortOption(
                        games
                            .Where(g => g.Game.LastPlayed.HasValue)
                            .OrderByDescending(g => g.Game.LastPlayed!.Value)
                            .Take(maxRecent),
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

                // 1b. Persistent "Favorites" section directly in root menu
                if (_mainViewModel.Settings.ShowFavoritesInTray)
                {
                    int maxFavorites = _mainViewModel.Settings.MaxFavoritesInTray > 0 ? _mainViewModel.Settings.MaxFavoritesInTray : LibraryConstants.DefaultTrayItemCount;
                    var favoriteGames = ApplySortOption(
                        games.Where(g => g.Game.IsFavorite),
                        _mainViewModel.Settings.FavoritesTraySortOption)
                        .Take(maxFavorites)
                        .ToList();

                    menu.Items.Add(CreateSectionHeader(LibraryConstants.FavoritesCategory));

                    if (favoriteGames.Count > 0)
                    {
                        foreach (var card in favoriteGames)
                        {
                            menu.Items.Add(CreateGameMenuItem(card));
                        }
                    }
                    else
                    {
                        menu.Items.Add(new MenuItem
                        {
                            Header = "(No favorites yet)",
                            IsEnabled = false,
                            IsHitTestVisible = false,
                            FontSize = 12,
                            Foreground = (Brush)FindResource("BrushTextMuted")
                        });
                    }

                    menu.Items.Add(new Separator());
                }

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
                    if (_mainViewModel.Settings.ShowRecentInTray)
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
                        if (_mainViewModel.Settings.ShowRecentInTray)
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
                        if (_mainViewModel.Settings.ShowRecentInTray)
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

            // Navigation & exit items
            menu.Items.Add(CreateNavMenuItem("Games Library", "\uE7FC", () => OpenSection(NavSection.Library), (Brush)FindResource("BrushAccentHover")));

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
            Style = (Style)FindResource("TrayMenuSectionHeaderStyle"),
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
            ToolTip = string.IsNullOrWhiteSpace(card.Game.ExecutablePath) ? card.Name : $"{card.Name}\n{card.Game.ExecutablePath}",
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
            if (_mainViewModel != null && _storageService != null)
            {
                _storageService.SaveSettings(_mainViewModel.Settings);
                LoggingService.Info("App", "Settings successfully saved during application exit.");
            }

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
            catch { }

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

    protected override void OnExit(ExitEventArgs e)
    {
        LoggingService.Info("App", $"OnExit called. ExitCode={e.ApplicationExitCode}");
        try
        {
            if (_mainViewModel != null && _storageService != null)
            {
                _storageService.SaveSettings(_mainViewModel.Settings);
            }
        }
        catch { }
        _hotkeyManager?.Dispose();
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
