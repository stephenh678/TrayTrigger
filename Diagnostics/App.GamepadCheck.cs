#if DEBUG
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;
using TrayTrigger.Views;

namespace TrayTrigger;

/// <summary>
/// --test-gamepad &lt;out.txt&gt;: drives <see cref="GamepadNavigator"/> with made-up controller readings (no controller
/// needed, and a real one is not read) in the Poster Grid and Details List, opens a game's right-click menu and Game
/// Details with it and closes them with B, and lists after each press what has focus. Never presses X or A on a
/// Play button, so nothing is launched. Settings are put back as they were.
/// </summary>
public partial class App
{
    private void RunGamepadCheck(string outPath)
    {
        _skipSettingsSaveOnExit = true;
        var report = new StringBuilder();
        string settingsFile = Path.Combine(_storageService.BaseDirectory, "settings.json");
        var settingsBefore = new[] { settingsFile, settingsFile + ".bak" }.ToDictionary(f => f, f => File.Exists(f) ? File.ReadAllBytes(f) : null);
        var window = _mainWindow!;
        var main = _mainViewModel!;
        string modeBefore = main.SettingsVM.LibraryViewMode;
        PadState pad = default;
        // The setting stays off, so the navigator's own timer never runs: each reading is one Tick below.
        var navigator = new GamepadNavigator(main, () => pad);
        try
        {
            window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();
            window.Width = 1400;
            window.Height = 860;
            main.CurrentSection = NavSection.Library;
            main.SettingsVM.LibraryViewMode = SettingsViewModel.ViewModePosterGrid;
            Settle(window);
            report.AppendLine($"main window active: {window.IsActive}");

            var games = main.FilteredGames.Cast<GameCardViewModel>().ToList();
            string Focus()
            {
                var f = Keyboard.FocusedElement as FrameworkElement;
                if (f == null) return "nothing focused";
                string where = Window.GetWindow(f)?.GetType().Name ?? "a popup";
                string game = f.DataContext is GameCardViewModel g ? $" game {games.IndexOf(g)}" : "";
                string text = (f as ContentControl)?.Content as string ?? System.Windows.Automation.AutomationProperties.GetName(f);
                // The focus ring is an adorner WPF draws only while the keyboard is the last input: without it nothing shows where focus is.
                var nav = typeof(KeyboardNavigation).GetProperty("Current", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null);
                var adorner = nav?.GetType().GetField("_focusVisualAdornerCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(nav) as System.Windows.Documents.Adorner;
                bool ring = adorner != null && ReferenceEquals(adorner.AdornedElement, f) && adorner.IsVisible;
                string last = InputManager.Current.MostRecentInputDevice?.GetType().Name ?? "none";
                return $"{f.GetType().Name} '{f.Name}' {text}{game} in {where} [{(ring ? "ring" : "NO RING")}, last {last}]";
            }
            void Press(string label, ushort buttons, short lx = 0)
            {
                pad = new PadState(buttons, lx, 0);
                navigator.Tick();
                Settle(window);
                pad = default;
                navigator.Tick();
                Settle(window);
                report.AppendLine($"  {label,-12} -> {Focus()}");
            }

            navigator.Tick(); // the first reading only sets where it starts from
            report.AppendLine("## Poster Grid");
            Press("Down", XInputReader.DPadDown);
            Press("Right", XInputReader.DPadRight);
            Press("stick right", 0, 30000);
            Press("Left", XInputReader.DPadLeft);
            string tab = main.SelectedCategory;
            Press("RB", XInputReader.RightShoulder);
            report.AppendLine($"  tab {tab} -> {main.SelectedCategory}");
            Press("LB", XInputReader.LeftShoulder);
            report.AppendLine($"  tab -> {main.SelectedCategory}");
            Press("Down", XInputReader.DPadDown);

            var menu = (ContextMenu)window.LibraryPage.FindResource("GameItemContextMenu");
            Press("Menu", XInputReader.Start);
            report.AppendLine($"  menu open: {menu.IsOpen}");
            Press("Down", XInputReader.DPadDown);
            Press("B", XInputReader.B);
            report.AppendLine($"  menu open: {menu.IsOpen}");
            Press("Right", XInputReader.DPadRight);

            // A on a card opens Game Details, modally: what happens inside it is read from a timer in its loop.
            var inside = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            inside.Tick += (_, _) =>
            {
                inside.Stop();
                var active = Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
                report.AppendLine($"  in front: {active?.GetType().Name ?? "none"}");
                // Down the Overview: nothing out of the Tab order (the pencils, the info button) should be landed on.
                for (int i = 0; i < 10; i++) Press("Down", XInputReader.DPadDown);
                Press("Right", XInputReader.DPadRight);
                Press("B", XInputReader.B);
                // A dialog that stayed open (it didn't take Esc) is closed here so the run can finish.
                foreach (var dlg in Current.Windows.OfType<GameDetailsDialog>().ToList())
                {
                    report.AppendLine("  Game Details still open after B: closing it");
                    dlg.Close();
                }
            };
            inside.Start();
            Press("A", XInputReader.A);
            report.AppendLine($"  Game Details open: {Current.Windows.OfType<GameDetailsDialog>().Any()}");
            Press("Down", XInputReader.DPadDown); // back to the game Game Details was opened from

            report.AppendLine("## Details List");
            main.SettingsVM.LibraryViewMode = SettingsViewModel.ViewModeDetailsList;
            Settle(window);
            Keyboard.Focus(window);
            Press("Down", XInputReader.DPadDown);
            Press("Down", XInputReader.DPadDown);
            pad = new PadState(0, 0, -30000);
            navigator.Tick(); Settle(window); pad = default; navigator.Tick(); Settle(window);
            report.AppendLine($"  stick down   -> {Focus()}");
            Press("Up", XInputReader.DPadUp);

            // A dialog the Library opens (Change Category): the controller has to work in it too, and B closes it.
            report.AppendLine("## Change Category, opened from the Library");
            var input = new QuickInputDialog("Change Category", "Change Category", "Select or enter a category:", "Action", new[] { "Action", "RPG" }, showAdd: true) { Owner = window };
            var inDialog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            inDialog.Tick += (_, _) =>
            {
                inDialog.Stop();
                report.AppendLine($"  in front: {Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)?.GetType().Name ?? "none"}");
                Press("Down", XInputReader.DPadDown);
                Press("B", XInputReader.B);
                if (input.IsVisible) { report.AppendLine("  still open after B: closing it"); input.Close(); }
            };
            inDialog.Start();
            navigator.Tick(); // the reading before the dialog, so its first press is a fresh one
            input.ShowDialog();
            report.AppendLine($"  closed, DialogResult={(input.DialogResult?.ToString() ?? "null")}");

            // Up from the top row of games into the bar above them, around it, into the filter list and the sort, and back.
            report.AppendLine("## The bar above the games");
            main.SettingsVM.LibraryViewMode = SettingsViewModel.ViewModePosterGrid;
            Settle(window);
            window.LibraryPage.FocusGames();
            Settle(window);
            report.AppendLine($"  start        -> {Focus()}");
            Press("Up", XInputReader.DPadUp);
            Press("Left", XInputReader.DPadLeft);
            Press("Left", XInputReader.DPadLeft);
            if (Keyboard.FocusedElement is ComboBox)
            {
                string sortBefore = main.SelectedSortOption;
                Press("A (Sort)", XInputReader.A);
                report.AppendLine($"  sort open: {(Keyboard.FocusedElement as FrameworkElement)?.GetType().Name}");
                Press("Down", XInputReader.DPadDown);
                Press("B", XInputReader.B);
                report.AppendLine($"  sort unchanged: {main.SelectedSortOption == sortBefore}");
            }
            Press("Right", XInputReader.DPadRight);
            Press("Right", XInputReader.DPadRight);
            Press("A (Filters)", XInputReader.A);
            report.AppendLine($"  filter list open: {window.LibraryPage.FilterFlyoutControl.FilterPopup.IsOpen}");
            Press("Down", XInputReader.DPadDown);
            Press("Down", XInputReader.DPadDown);
            Press("Down", XInputReader.DPadDown);
            Press("B", XInputReader.B);
            report.AppendLine($"  filter list open: {window.LibraryPage.FilterFlyoutControl.FilterPopup.IsOpen}");
            Press("Up", XInputReader.DPadUp);
            Press("Down", XInputReader.DPadDown);
            for (int i = 0; i < 6; i++) Press("Left", XInputReader.DPadLeft); // never on into the sidebar
            Press("B", XInputReader.B);
            Press("Up", XInputReader.DPadUp);
            report.AppendLine("  (back to the control last used)");
            Press("Down", XInputReader.DPadDown);

            // Held directions, through the navigator's own timer (a repeat needs real time between readings): Up held
            // to scroll to the top stops on the top row, and in the bar Up held stays on its row while Left held runs
            // along it.
            report.AppendLine("## Holding the D-pad");
            void HoldFor(ushort buttons, int ms)
            {
                pad = new PadState(buttons, 0, 0);
                PumpDispatcher(TimeSpan.FromMilliseconds(ms));
                pad = default;
                PumpDispatcher(TimeSpan.FromMilliseconds(200));
            }
            window.Width = 700; // narrow, so the games take more than one row
            Settle(window);
            window.LibraryPage.FocusGames(games.LastOrDefault());
            Settle(window);
            navigator.RunTimerForTest(true);
            PumpDispatcher(TimeSpan.FromMilliseconds(200));
            report.AppendLine($"  start        -> {Focus()}");
            HoldFor(XInputReader.DPadUp, 1500);
            report.AppendLine($"  Up held      -> {Focus()}");
            HoldFor(XInputReader.DPadUp, 150);
            report.AppendLine($"  Up again     -> {Focus()}");
            HoldFor(XInputReader.DPadUp, 1200);
            report.AppendLine($"  Up held      -> {Focus()}");
            HoldFor(XInputReader.DPadLeft, 1000);
            report.AppendLine($"  Left held    -> {Focus()}");
            HoldFor(XInputReader.DPadDown, 150);
            report.AppendLine($"  Down         -> {Focus()}");

            // Two buttons in one reading, the first opening Game Details, which is then closed without the controller
            // (as by the mouse): the second (Menu) is stale by then and must not open the game's menu.
            window.Width = 1400;
            Settle(window);
            // RB acts wherever focus is (it steps the category tab), so a stale RB shows as a changed tab.
            report.AppendLine("## A and RB at once, Game Details closed by the mouse");
            window.LibraryPage.FocusGames();
            Settle(window);
            string tabBefore = main.SelectedCategory;
            var closer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            closer.Tick += (_, _) =>
            {
                closer.Stop();
                foreach (var dlg in Current.Windows.OfType<GameDetailsDialog>().ToList()) dlg.Close();
            };
            closer.Start();
            pad = new PadState((ushort)(XInputReader.A | XInputReader.RightShoulder), 0, 0);
            PumpDispatcher(TimeSpan.FromSeconds(4), () => !closer.IsEnabled && !Current.Windows.OfType<GameDetailsDialog>().Any());
            pad = default;
            PumpDispatcher(TimeSpan.FromMilliseconds(300));
            navigator.RunTimerForTest(false);
            report.AppendLine($"  tab {tabBefore} -> {main.SelectedCategory} (should stay {tabBefore})");
            main.SelectedCategory = tabBefore;
            Settle(window);

            // The same through the navigator's own timer, as a real controller is read: Game Details opened with A must
            // go on being read (a DispatcherTimer doesn't fire again until its Tick returns, and A opens it modally).
            report.AppendLine("## Game Details through the timer");
            main.SettingsVM.LibraryViewMode = SettingsViewModel.ViewModePosterGrid;
            Settle(window);
            window.LibraryPage.FocusGames();
            Settle(window);
            void Hold(ushort buttons)
            {
                pad = new PadState(buttons, 0, 0);
                PumpDispatcher(TimeSpan.FromMilliseconds(200));
                pad = default;
                PumpDispatcher(TimeSpan.FromMilliseconds(200));
            }
            var timed = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timed.Tick += (_, _) =>
            {
                timed.Stop();
                report.AppendLine($"  in front: {Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)?.GetType().Name ?? "none"}");
                report.AppendLine($"  on open      -> {Focus()} (should be Play)");
                for (int i = 0; i < 3; i++)
                {
                    Hold(XInputReader.DPadDown);
                    report.AppendLine($"  Down         -> {Focus()}");
                }
                // The Overview, walked down and back up with the D-pad: it must scroll to the end (text with no controls
                // included) before Down leaves it for the window's own buttons, and back to the top.
                if (Current.Windows.OfType<GameDetailsDialog>().FirstOrDefault() is { } overview)
                {
                    var page = overview.DetailsScroll;
                    void Step(ushort dir) { pad = new PadState(dir, 0, 0); PumpDispatcher(TimeSpan.FromMilliseconds(90)); pad = default; PumpDispatcher(TimeSpan.FromMilliseconds(90)); }
                    double? leftAt = null;
                    for (int i = 0; i < 40 && leftAt == null; i++)
                    {
                        Step(XInputReader.DPadDown);
                        if (Keyboard.FocusedElement is System.Windows.Media.Visual v && !v.IsDescendantOf(page)) leftAt = page.VerticalOffset;
                    }
                    report.AppendLine($"  Overview down: left the page at {leftAt:F0} of {page.ScrollableHeight:F0} (should be the end) -> {Focus()}");
                    for (int i = 0; i < 40 && page.VerticalOffset > 0.5; i++) Step(XInputReader.DPadUp);
                    report.AppendLine($"  Overview up: back to {page.VerticalOffset:F0} (should be 0) -> {Focus()}");
                }
                // The Settings tab, walked with the D-pad: it must never land on a Browse... or New script button
                // (they open Windows' file pickers, which a controller can't work or close).
                if (Current.Windows.OfType<GameDetailsDialog>().FirstOrDefault() is { DataContext: GameDetailsViewModel detailsVm })
                {
                    detailsVm.SelectedTab = GameDetailsTab.Settings;
                    PumpDispatcher(TimeSpan.FromMilliseconds(400));
                    var reached = new System.Collections.Generic.HashSet<object>();
                    int skipped = 0;
                    foreach (var dir in new[] { XInputReader.DPadDown, XInputReader.DPadRight, XInputReader.DPadDown, XInputReader.DPadLeft })
                        for (int i = 0; i < 12; i++)
                        {
                            pad = new PadState(dir, 0, 0);
                            PumpDispatcher(TimeSpan.FromMilliseconds(90));
                            pad = default;
                            PumpDispatcher(TimeSpan.FromMilliseconds(90));
                            if (Keyboard.FocusedElement is DependencyObject now)
                            {
                                reached.Add(now);
                                if (GamepadNavigator.GetSkip(now)) skipped++;
                            }
                        }
                    report.AppendLine($"  Settings tab: {reached.Count} controls reached, on a Browse/New button {skipped} times (should be 0)");
                    // A drop-down (the Performance Profile): A opens it, Down moves in it, B closes it as it was.
                    var details = Current.Windows.OfType<GameDetailsDialog>().First();
                    foreach (bool editable in new[] { false, true })
                    {
                    if (FindVisual<ComboBox>(details, c => c.IsEditable == editable) is not { } profile) continue;
                    report.AppendLine($"  {(editable ? "editable drop-down" : "drop-down")} '{System.Windows.Automation.AutomationProperties.GetName(profile)}'");
                    {
                        object? before = profile.SelectedItem;
                        profile.Focus();
                        PumpDispatcher(TimeSpan.FromMilliseconds(150));
                        Hold(XInputReader.A);
                        report.AppendLine($"  drop-down A  -> open {profile.IsDropDownOpen}, {Focus()}");
                        Hold(XInputReader.DPadDown);
                        report.AppendLine($"  Down         -> {Focus()}");
                        Hold(XInputReader.B);
                        report.AppendLine($"  B            -> open {profile.IsDropDownOpen}, unchanged {Equals(profile.SelectedItem, before)}");
                    }
                    }
                    detailsVm.SelectedTab = GameDetailsTab.Overview;
                    PumpDispatcher(TimeSpan.FromMilliseconds(300));
                }
                Hold(XInputReader.B);
                foreach (var dlg in Current.Windows.OfType<GameDetailsDialog>().ToList())
                {
                    report.AppendLine("  Game Details still open after B: closing it");
                    dlg.Close();
                }
            };
            navigator.RunTimerForTest(true);
            PumpDispatcher(TimeSpan.FromMilliseconds(200));
            timed.Start();
            Hold(XInputReader.A);
            PumpDispatcher(TimeSpan.FromSeconds(4), () => !Current.Windows.OfType<GameDetailsDialog>().Any() && !timed.IsEnabled);
            navigator.RunTimerForTest(false);
            report.AppendLine($"  Game Details open: {Current.Windows.OfType<GameDetailsDialog>().Any()}");

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

    private static T? FindVisual<T>(DependencyObject parent, Func<T, bool> match) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T found && (child as UIElement)?.IsVisible != false && match(found)) return found;
            if (FindVisual(child, match) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>
    /// --test-controllers &lt;out.txt&gt; [seconds]: reads the real controllers for a few seconds (5 by default), XInput
    /// and PlayStation on their own and as the navigator combines them, and lists each change. Press buttons while it
    /// runs to see what they read as.
    /// </summary>
    private void RunControllersCheck(string outPath, int seconds)
    {
        var report = new StringBuilder();
        var xinput = new XInputReader();
        var playStation = new PlayStationReader();
        var combined = GamepadNavigator.Combine(xinput.Read, playStation.Read);
        string? last = null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            string Show(PadState? s) => s is { } p ? $"buttons {p.Buttons:X4} stick {p.LeftX},{p.LeftY}" : "none";
            string line = $"XInput {Show(xinput.Read())} | PlayStation {Show(playStation.Read())} | combined {Show(combined())}";
            if (line != last) report.AppendLine($"{clock.ElapsedMilliseconds,6} ms  {line}");
            last = line;
            System.Threading.Thread.Sleep(33);
        }
        File.WriteAllText(outPath, report.ToString());
        ExitApplication();
    }
}
#endif
