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
                return $"{f.GetType().Name} '{f.Name}' {text}{game} in {where}";
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
                Press("Down", XInputReader.DPadDown);
                Press("Down", XInputReader.DPadDown);
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
