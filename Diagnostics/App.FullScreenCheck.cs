#if DEBUG
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;
using TrayTrigger.Views;

namespace TrayTrigger;

/// <summary>
/// --test-fullscreen &lt;out.txt&gt;: turns the full-screen Library on with the controller's View button (a made-up
/// reading), lists what it changed (the window, the sidebar, the Library's header, the view, the text size) and saves a
/// picture of it beside out.txt, then turns it off with View again and lists that all of it went back, the saved
/// window placement and view included. F11 is tried the same way. Settings are put back as they were.
/// </summary>
public partial class App
{
    private void RunFullScreenCheck(string outPath)
    {
        _skipSettingsSaveOnExit = true;
        var report = new StringBuilder();
        string settingsFile = Path.Combine(_storageService.BaseDirectory, "settings.json");
        string gamesFile = Path.Combine(_storageService.BaseDirectory, "games.json");
        // The settings and the library (a collection is added below) are put back as they were.
        var settingsBefore = new[] { settingsFile, settingsFile + ".bak", gamesFile, gamesFile + ".bak" }
            .ToDictionary(f => f, f => File.Exists(f) ? File.ReadAllBytes(f) : null);
        List<GameCardViewModel>? collected = null;
        var window = _mainWindow!;
        var main = _mainViewModel!;
        var settings = main.SettingsVM.Settings;
        PadState pad = default;
        var navigator = new GamepadNavigator(main, () => pad);
        try
        {
            window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();
            window.Width = 1200;
            window.Height = 760;
            main.CurrentSection = NavSection.Library;
            main.SettingsVM.LibraryViewMode = SettingsViewModel.ViewModePosterGrid;
            // A collection, as most libraries have one, so the tab row shares its width between two strips.
            var cards = main.FilteredGames.Cast<GameCardViewModel>().Take(2).ToList();
            if (!main.Library.HasCollections && cards.Count > 0)
            {
                main.Library.ApplyCollections(cards, new[] { "Multiplayer" }, Array.Empty<string>());
                collected = cards;
            }
            Settle(window);
            int Relayouts(int ms)
            {
                int count = 0;
                // A strip's width or its arrows changing. Past 300 it is a loop: the row is hidden to end it, so the
                // check can finish and say so instead of hanging.
                ScrollChangedEventHandler counter = (_, e) =>
                {
                    if (e.ExtentWidthChange == 0 && e.ViewportWidthChange == 0) return;
                    if (++count == 300) window.LibraryPage.TabRow.Visibility = Visibility.Collapsed;
                };
                window.LibraryPage.TabRow.AddHandler(ScrollViewer.ScrollChangedEvent, counter);
                PumpDispatcher(TimeSpan.FromMilliseconds(ms));
                window.LibraryPage.TabRow.RemoveHandler(ScrollViewer.ScrollChangedEvent, counter);
                return count;
            }
            report.AppendLine($"collections {main.Library.CollectionTabs.Count}; tab row width changes at rest: {Relayouts(1500)}");
            File.WriteAllText(outPath, report.ToString());
            var sidebar = FindVisualChildren<Border>(window).First(b => b.Width > 0 && Grid.GetColumn(b) == 0 && b.Parent == window.Content);
            string State() =>
                $"window {window.WindowState}/{window.WindowStyle}/{window.ResizeMode} {window.ActualWidth:F0}x{window.ActualHeight:F0}, " +
                $"sidebar {(sidebar.IsVisible ? "shown" : "hidden")}, header {(window.LibraryPage.FindName("LibraryAddGame") is UIElement h ? h.IsVisible.ToString() : HeaderShown(window))}, " +
                $"view {main.SettingsVM.LibraryViewMode} (saved {settings.LibraryViewMode}), body text {Current.Resources["FontSizeBody"]}, " +
                $"saved placement {settings.MainWindowWidth:F0}x{settings.MainWindowHeight:F0} max {settings.MainWindowMaximized}, " +
                $"controller reading {main.IsFullScreenLibrary || main.SettingsVM.ControllerNavigation}";
            string placementBefore = $"{settings.MainWindowLeft:F0},{settings.MainWindowTop:F0} {settings.MainWindowWidth:F0}x{settings.MainWindowHeight:F0} max {settings.MainWindowMaximized}";
            report.AppendLine($"before: {State()}");

            void Press(ushort buttons)
            {
                pad = new PadState(buttons, 0, 0);
                navigator.Tick();
                Settle(window);
                pad = default;
                navigator.Tick();
                Settle(window);
            }
            window.LibraryPage.FocusGames();
            Settle(window);
            navigator.Tick(); // the first reading only sets where it starts from
            Press(XInputReader.Back); // View on an Xbox controller, Create on a PlayStation one
            PumpDispatcher(TimeSpan.FromMilliseconds(500));
            report.AppendLine($"View: full screen {main.IsFullScreenLibrary}: {State()}");
            report.AppendLine($"  focus on a game: {window.LibraryPage.IsGameFocused(Keyboard.FocusedElement as DependencyObject)}");
            SaveWindowContentPng(window, Path.ChangeExtension(outPath, ".png"));

            // A top-row card zoomed as the pointer does it (1.05): it must stay inside the games' scroll area, not be cut at its top.
            if (FindVisualChildren<Border>(window.LibraryPage).FirstOrDefault(b => b.Name == "CardBorder" && b.IsVisible) is { } zoomed
                && zoomed.RenderTransform is ScaleTransform)
            {
                // The card's own transform is frozen by its template: a stand-in at the zoom's size, then the original back.
                var scroller = window.LibraryPage.CardScroller;
                var original = zoomed.RenderTransform;
                zoomed.RenderTransform = new ScaleTransform(1.05, 1.05);
                Settle(window);
                var box = zoomed.TransformToAncestor(scroller).TransformBounds(new Rect(zoomed.RenderSize));
                report.AppendLine($"  zoomed top-row card: top {box.Top:F1} (should be 0 or more, else it is cut)");
                zoomed.RenderTransform = original;
            }

            // The game's right-click menu, opened with Menu: it must fit without scrolling, with room to spare for a 1080p TV
            // at 150% (720 tall).
            window.LibraryPage.FocusGames();
            Settle(window);
            var gameMenu = (ContextMenu)window.LibraryPage.FindResource("GameItemContextMenu");
            Press(XInputReader.Start);
            PumpDispatcher(TimeSpan.FromMilliseconds(400));
            if (gameMenu.IsOpen && FindVisualChildren<ScrollViewer>(gameMenu).FirstOrDefault() is { } menuScroll)
                report.AppendLine($"  game menu: {menuScroll.ExtentHeight:F0} tall, room for {menuScroll.MaxHeight:F0}, scrolls {menuScroll.ScrollableHeight > 0.5} (should be False)");
            else report.AppendLine($"  game menu: open {gameMenu.IsOpen}");
            gameMenu.IsOpen = false;
            Settle(window);

            // Game Details from the full-screen Library: a card with no title bar over the Library dimmed behind it.
            window.LibraryPage.FocusGames();
            Settle(window);
            var shot = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            shot.Tick += (_, _) =>
            {
                shot.Stop();
                var details = Current.Windows.OfType<GameDetailsDialog>().FirstOrDefault();
                report.AppendLine($"  Game Details: {details?.WindowStyle}/{details?.ResizeMode}, border {details?.BorderThickness}, Library dimmed {window.DialogDim.IsVisible}");
                CaptureScreen(window, Path.ChangeExtension(outPath, ".details.png"));
                details?.Close();
            };
            shot.Start();
            Press(XInputReader.A);
            PumpDispatcher(TimeSpan.FromSeconds(4), () => !shot.IsEnabled && !Current.Windows.OfType<GameDetailsDialog>().Any());
            report.AppendLine($"  closed: Library dimmed {window.DialogDim.IsVisible}");

            // Another page leaves it (the Library's view stays a desktop page's business).
            main.CurrentSection = NavSection.Settings;
            Settle(window);
            report.AppendLine($"Settings page: full screen {main.IsFullScreenLibrary}: {State()}");
            main.CurrentSection = NavSection.Library;
            Settle(window);

            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.F11) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            PumpDispatcher(TimeSpan.FromMilliseconds(400));
            report.AppendLine($"F11: full screen {main.IsFullScreenLibrary}");
            window.LibraryPage.FocusGames();
            Settle(window);
            Press(XInputReader.Back);
            PumpDispatcher(TimeSpan.FromMilliseconds(400));
            report.AppendLine($"View again: full screen {main.IsFullScreenLibrary}: {State()}");
            report.AppendLine($"  tab row width changes in the 3 s after (should be a few, then none; 300 is a loop): {Relayouts(1500)} then {Relayouts(1500)}");
            window.LibraryPage.TabRow.Visibility = Visibility.Visible;
            string placementAfter = $"{settings.MainWindowLeft:F0},{settings.MainWindowTop:F0} {settings.MainWindowWidth:F0}x{settings.MainWindowHeight:F0} max {settings.MainWindowMaximized}";
            report.AppendLine($"saved placement kept: {placementBefore == placementAfter} ({placementBefore} -> {placementAfter})");
            File.WriteAllText(outPath, report.ToString());
        }
        catch (Exception ex)
        {
            File.WriteAllText(outPath, report + Environment.NewLine + "FAILED: " + ex);
        }
        finally
        {
            main.IsFullScreenLibrary = false;
            if (collected != null) main.Library.ApplyCollections(collected, Array.Empty<string>(), new[] { "Multiplayer" });
            foreach (var (file, bytes) in settingsBefore)
            {
                try
                {
                    if (bytes != null) File.WriteAllBytes(file, bytes);
                    else File.Delete(file);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not put {file} back as it was: {ex.Message}");
                }
            }
        }
        ExitApplication();
    }

    /// <summary>Whether the Library's header (its title and Add Game) shows: the first DockPanel on the page.</summary>
    private static string HeaderShown(MainWindow window) =>
        FindVisualChildren<TextBlock>(window.LibraryPage).FirstOrDefault(t => t.Text == "Library") is { } title ? (title.IsVisible ? "shown" : "hidden") : "?";
}
#endif
