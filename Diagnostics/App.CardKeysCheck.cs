#if DEBUG
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TrayTrigger.ViewModels;
using TrayTrigger.Views;

namespace TrayTrigger;

/// <summary>
/// --test-card-keys &lt;out.txt&gt;: in each card view (Poster Grid, Extra Large, Compact Icons) puts keyboard focus on
/// the first card, sends arrow keys, Home, End and Page Down through WPF's input pipeline, and lists after each key
/// which element has focus: the card (by its game's place in the list and its row and column on screen) or something
/// else. Settings are put back as they were.
/// </summary>
public partial class App
{
    private void RunCardKeysCheck(string outPath)
    {
        _skipSettingsSaveOnExit = true;
        var report = new StringBuilder();
        string settingsFile = Path.Combine(_storageService.BaseDirectory, "settings.json");
        var settingsBefore = new[] { settingsFile, settingsFile + ".bak" }.ToDictionary(f => f, f => File.Exists(f) ? File.ReadAllBytes(f) : null);
        var window = _mainWindow!;
        var main = _mainViewModel!;
        string modeBefore = main.SettingsVM.LibraryViewMode;
        try
        {
            window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();
            window.Width = 1400;
            window.Height = 860;
            main.CurrentSection = NavSection.Library;
            var page = (LibraryView)window.FindName("LibraryPage");
            var games = main.FilteredGames.Cast<GameCardViewModel>().ToList();
            report.AppendLine($"{games.Count} games in the list");

            foreach (var mode in new[] { SettingsViewModel.ViewModePosterGrid, SettingsViewModel.ViewModeExtraLarge, SettingsViewModel.ViewModeCompactIcons })
            {
                main.SettingsVM.LibraryViewMode = mode;
                Settle(window);
                var cards = Descendants(page).OfType<Border>()
                    .Where(b => b.IsVisible && b.Focusable && b.DataContext is GameCardViewModel && (b.Name == "CardBorder" || b.Name == "IconCardBorder"))
                    .ToList();
                report.AppendLine($"## {mode}: {cards.Count} cards realised");
                if (cards.Count == 0) continue;
                double firstTop = cards[0].TranslatePoint(new Point(), page).Y;
                int perRow = cards.Count(c => Math.Abs(c.TranslatePoint(new Point(), page).Y - firstTop) < 2);
                report.AppendLine($"  {perRow} cards per row");

                string Where()
                {
                    var f = Keyboard.FocusedElement as FrameworkElement;
                    if (f == null) return "nothing focused";
                    int index = f.DataContext is GameCardViewModel g ? games.IndexOf(g) : -1;
                    string at = index >= 0 ? $"game {index} (row {index / perRow}, col {index % perRow})" : "not a game";
                    string what = f is Border { Name: "CardBorder" or "IconCardBorder" } ? "card" : $"{f.GetType().Name} '{f.Name}' {(f as ContentControl)?.Content}";
                    return $"{what}, {at}";
                }

                Keyboard.Focus(cards[0]);
                Settle(window);
                report.AppendLine($"  start: {Where()}");
                foreach (var key in new[] { Key.Right, Key.Right, Key.Down, Key.Down, Key.Left, Key.Left, Key.Left, Key.Up, Key.End, Key.Home, Key.PageDown, Key.PageUp, Key.Tab, Key.Right })
                {
                    SendTestKey(key);
                    Settle(window);
                    report.AppendLine($"  {key,-9} -> {Where()}");
                }

                // A click between the cards (on the scroller, as on its scroll bar), then Right: the keys carry on in the
                // games instead of scrolling the page.
                var scroller = Descendants(page).OfType<ScrollViewer>().First(v => v.Name == "CardScroller");
                Keyboard.Focus(scroller);
                report.AppendLine($"  scroller focusable: {scroller.Focusable}");
                foreach (var routed in new[] { UIElement.PreviewMouseLeftButtonDownEvent, UIElement.MouseLeftButtonDownEvent })
                    scroller.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = routed });
                Settle(window);
                report.AppendLine($"  click     -> {Where()}");
                SendTestKey(Key.Right);
                Settle(window);
                report.AppendLine($"  Right     -> {Where()}");
            }
            File.WriteAllText(outPath, report.ToString());
        }
        catch (Exception ex)
        {
            File.WriteAllText(outPath, report + Environment.NewLine + "FAILED: " + ex);
        }
        finally
        {
            main.SettingsVM.LibraryViewMode = modeBefore;
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
}
#endif
