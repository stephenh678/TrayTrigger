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

/// <summary>The tray icon: its tooltip, the right-click menu (games, Now Playing, tools), the icon tiles, and keeping the icon promoted and registered with Explorer.</summary>
public partial class App
{
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
            // Some of the menu's colors are set in code (icons, muted labels), so a theme switch builds it again.
            TrayTrigger.Services.Theming.ThemeService.Instance.ThemeChanged += (_, _) => UpdateTrayContextMenu();
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
                _trayIcon.ToolTipText = BuildTrayToolTipText(sessions, DateTime.Now, _mainViewModel?.Settings.GlobalManageHotkey, ActivityService.Current?.AttentionCount ?? 0);

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
    internal static string BuildTrayToolTipText(IReadOnlyList<ActiveGameSession> sessions, DateTime now, string? windowHotkey = null, int attentionCount = 0)
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
            text = !session.GameStarted
                ? $"Starting {session.Game.Name} via {session.PlatformLabel}"
                : session.IsSuspended
                    ? $"Suspended: {session.Game.Name} · {FormatPlayingElapsed(session.PlayedTime(now.ToUniversalTime()))}"
                    : $"Playing {session.Game.Name} · {FormatPlayingElapsed(session.PlayedTime(now.ToUniversalTime()))}";
        }
        else
        {
            text = $"Playing {sessions.Count} games · {string.Join(", ", sessions.Select(s => s.Game.Name))}";
        }

        // First, so truncation never cuts it: the tray is where a hidden window says something needs you.
        if (attentionCount > 0)
        {
            text = (attentionCount == 1 ? "1 thing needs attention" : $"{attentionCount} things need attention") + "\n" + text;
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

            // Activity & History first, while something needs attention: the one row that is about TrayTrigger itself.
            if (AttentionMenuRow() is { } attention)
            {
                menu.Items.Add(CreateNavMenuItem(attention.Header, "", attention.OnClick, (Brush)FindResource("BrushDanger")));
                menu.Items.Add(new Separator());
            }

            var games = _mainViewModel.Games.Where(g => !g.Game.IsHidden).ToList();

            if (games.Count == 0)
            {
                var emptyItem = new MenuItem
                {
                    Header = "No games in library",
                    Style = TrayItemStyle,
                    IsEnabled = false,
                    Foreground = AppPalette.Brush("BrushTextMuted")
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
                        string elapsed = !session.GameStarted
                            ? $"starting via {session.PlatformLabel}"
                            : session.IsSuspended
                                ? "suspended"
                                : $"{session.PlayedTime(DateTime.UtcNow).TotalMinutes:0}m";
                        var sessionItem = card != null
                            ? CreateGameMenuItem(card)
                            : new MenuItem { Header = session.Game.Name, Style = TrayItemStyle };
                        // The one row in the menu that earns bold: it's the game you're in.
                        sessionItem.FontWeight = FontWeights.SemiBold;
                        sessionItem.Header = $"{session.Game.Name}  ·  {elapsed}";
                        sessionItem.Command = null;

                        string gameId = session.GameId;
                        string gameName = session.Game.Name;
                        // Suspend / Resume first: the tray is the way back to a suspended game, which
                        // can't be switched to while it's frozen.
                        if (session.IsSuspended)
                        {
                            var resume = CreateNavMenuItem("Resume Game", "", () => ResumeFromTray(gameId), (Brush)FindResource("BrushAccentText"));
                            resume.ToolTip = "Unfreezes the game where it stopped, unmutes it and brings it back to the front. Its playtime clock starts again.";
                            sessionItem.Items.Add(resume);
                        }
                        else if (session.GameStarted)
                        {
                            var suspend = CreateNavMenuItem("Suspend Game", "", () => SuspendFromTray(gameId));
                            suspend.ToolTip = "Minimizes the game, freezes it where it is and mutes it, and stops its playtime clock until you resume it. Online games disconnect; games with anti-cheat are never suspended.";
                            sessionItem.Items.Add(suspend);
                        }
                        var closeGame = CreateNavMenuItem("Close Game", "", () => CloseGameFromTray(gameId, gameName));
                        closeGame.ToolTip = "Asks the game to quit, as its own close button does. Once it exits, TrayTrigger restores the Performance Profile and runs the post-exit script.";
                        sessionItem.Items.Add(closeGame);
                        var restoreNow = CreateNavMenuItem("Restore Settings Now", "", () =>
                            Task.Run(() => _launcherService!.EndSessionNow(gameId, forceCloseGame: false)));
                        restoreNow.ToolTip = "Leaves the game running and puts Windows back the way it was: the Performance Profile is restored, the post-exit script runs and tools set to close after the game close. The game is no longer tracked, so its playtime stops counting.";
                        sessionItem.Items.Add(restoreNow);
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
            menu.Items.Add(CreateNavMenuItem("Games Library", "\uE7FC", () => OpenSection(NavSection.Library), (Brush)FindResource("BrushAccentText")));
            menu.Items.Add(CreateNavMenuItem("Scan for Games...", "\uE721", () =>
            {
                OpenSection(NavSection.Library);
                _mainViewModel.OpenScanForGamesCommand.Execute(null);
            }));
            menu.Items.Add(CreateNavMenuItem("Settings", "\uE713", () => OpenSection(NavSection.Settings)));

            menu.Items.Add(new Separator());

            var exitBrush = (Brush)FindResource("BrushDanger");
            var exitItem = CreateNavMenuItem("Exit TrayTrigger", "\uE7E8", RequestExit, exitBrush, exitBrush);
            menu.Items.Add(exitItem);

            // The optional search row at the very top (UX-16); with the setting off the menu is
            // exactly as before. Nothing to search in an empty library.
            // The search covers tools as well as games, so a library of tools alone gets the box too.
            bool toolsInTray = _mainViewModel.Settings.EnableTools && _mainViewModel.Settings.ShowToolsInTray;
            if (_mainViewModel.Settings.ShowTraySearch && (games.Count > 0 || (toolsInTray && _mainViewModel.Tools.TrayTools().Any())))
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
            Header = card.TrayMenuLabel,
            Style = TrayItemStyle,
            Command = new RelayCommand(() => _mainViewModel.LaunchGame(card))
        };

        // Greyed out, never disabled: a disabled row says nothing about why, and the launcher that
        // reports a game uninstalled is also where it is reinstalled from - pressing it says so and,
        // for Steam, offers to open it there. A wrongly-marked game stays playable too.
        if (card.IsUnavailable)
        {
            item.Foreground = (Brush)FindResource("BrushTextMuted");
        }

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

        // The icon carries most of a row's weight, so the label alone going grey barely reads.
        if (card.IsUnavailable && item.Icon is UIElement icon)
        {
            icon.Opacity = 0.4;
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
}
