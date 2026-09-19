#if DEBUG
#pragma warning disable CS8602, CS8604
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TrayTrigger;

/// <summary>
/// Capture and test modes for the tray menu's search row (UX-16).
///
/// --screenshot-tray-search &lt;query&gt; &lt;out.png&gt; [grouped] [compact] [noicons] [nosearch]: the
/// tray menu's rows, laid out as --screenshot-tray-menu does, with &lt;query&gt; typed into the box
/// ("-" for an empty box). Nothing is saved.
///
/// --test-tray-search &lt;out.txt&gt;: opens the real tray ContextMenu on screen (not from the tray
/// icon, which a test cannot click), types into the box through WPF's text input, and checks
/// what the roadmap's spike asks - opened through the open-tray-menu hotkey's handler: the box holds focus, letters reach it rather than the menu,
/// Space is typed rather than activating a row, the menu stays open, Enter picks the first match
/// (its launch is swapped for a recorder, so no game starts), Esc clears then closes.
/// </summary>
public partial class App
{
    private bool TryHandleTraySearchDevArgs(StartupEventArgs e, int i)
    {
        if (e.Args[i].Equals("--screenshot-tray-search", StringComparison.OrdinalIgnoreCase) && i + 2 < e.Args.Length)
        {
            string query = e.Args[i + 1] == "-" ? string.Empty : e.Args[i + 1];
            string targetPng = e.Args[i + 2];
            var flags = e.Args.Skip(i + 3).Select(a => a.ToLowerInvariant()).ToHashSet();
            _skipSettingsSaveOnExit = true;
            if (_trayIcon == null) InitializeTrayIcon();
            _mainViewModel.Settings.GroupTrayMenuByCategory = flags.Contains("grouped");
            _mainViewModel.Settings.CompactTrayMenu = flags.Contains("compact");
            _mainViewModel.Settings.ShowTrayMenuIcons = !flags.Contains("noicons");
            _mainViewModel.Settings.ShowTraySearch = !flags.Contains("nosearch");
            UpdateTrayContextMenu();

            var trayMenu = _trayIcon.ContextMenu ?? throw new Exception("ContextMenu was null!");
            var box = FindTraySearchBox(trayMenu);
            if (box != null) box.Text = query;

            var items = trayMenu.Items.OfType<UIElement>().ToList();
            trayMenu.Items.Clear();
            var panel = new StackPanel { Width = 300 };
            Grid.SetIsSharedSizeScope(panel, true);
            foreach (var item in items) panel.Children.Add(item);
            var menuBorder = new Border
            {
                Background = (Brush)FindResource("BrushSurfaceDark"),
                BorderBrush = (Brush)FindResource("BrushBorderDark"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6),
                Child = panel,
            };
            var window = new Window
            {
                Title = "Tray Menu Preview",
                Background = (Brush)FindResource("BrushBgDark"),
                Content = new Border { Padding = new Thickness(20), Child = menuBorder, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left },
            };
            CaptureVisual(window, 380, 640, targetPng);
            ExitApplication();
            return true;
        }

        if (e.Args[i].Equals("--test-tray-search", StringComparison.OrdinalIgnoreCase) && i + 1 < e.Args.Length)
        {
            RunTraySearchTest(e.Args[i + 1]);
            return true;
        }
        return false;
    }

    private static TextBox? FindTraySearchBox(ContextMenu menu) =>
        menu.Items.OfType<MenuItem>().Select(m => m.Header).OfType<Grid>()
            .SelectMany(g => g.Children.OfType<TextBox>()).FirstOrDefault();

    private void RunTraySearchTest(string outPath)
    {
        _skipSettingsSaveOnExit = true;
        if (_trayIcon == null) InitializeTrayIcon();
        _mainViewModel.Settings.ShowTraySearch = true;
        UpdateTrayContextMenu();
        var menu = _trayIcon.ContextMenu ?? throw new Exception("No tray menu.");
        var box = FindTraySearchBox(menu) ?? throw new Exception("No search box in the tray menu.");
        var report = new StringBuilder();
        void Check(string what, bool ok, string detail = "")
        {
            report.AppendLine($"{(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
        }

        var firstGame = _mainViewModel.Games.Where(g => !g.Game.IsHidden).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        string query = firstGame == null ? "a" : firstGame.Name[..Math.Min(3, firstGame.Name.Length)].ToLowerInvariant();

        int ownRowsBefore = menu.Items.Count;
        // Opened the way the open-tray-menu hotkey opens it (the key itself is global to Windows).
        OnTrayMenuHotkeyTriggered();

        void Type(string text) =>
            TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, Keyboard.FocusedElement ?? box, text));

        var steps = new Queue<Action>();
        steps.Enqueue(() =>
        {
            Check("the tray menu hotkey opens the menu", menu.IsOpen);
            if (TryGetTrayAnchor(out var anchor))
            {
                var topLeft = menu.PointToScreen(new Point(0, 0));
                var bottomRight = menu.PointToScreen(new Point(menu.ActualWidth, menu.ActualHeight));
                // Touching the icon on one side (within a few pixels), overlapping it along that side.
                bool touches = Math.Abs(bottomRight.Y - anchor.Top) <= 4 || Math.Abs(topLeft.Y - anchor.Bottom) <= 4
                    || Math.Abs(bottomRight.X - anchor.Left) <= 4 || Math.Abs(topLeft.X - anchor.Right) <= 4;
                bool overlaps = (topLeft.X <= anchor.Right && bottomRight.X >= anchor.Left) || (topLeft.Y <= anchor.Bottom && bottomRight.Y >= anchor.Top);
                Check("the menu opens against the tray icon", touches && overlaps,
                    $"icon {anchor.Left},{anchor.Top}-{anchor.Right},{anchor.Bottom}; menu {topLeft.X:0},{topLeft.Y:0}-{bottomRight.X:0},{bottomRight.Y:0}; {menu.Placement}");
            }
            else Check("the tray icon's position is known", false);
            Check("box has keyboard focus when the menu opens", box.IsKeyboardFocused,
                $"focused: {Keyboard.FocusedElement?.GetType().Name}");
            menu.Placement = PlacementMode.AbsolutePoint;
            menu.HorizontalOffset = 200;
            menu.VerticalOffset = 200;
        });
        steps.Enqueue(() => Type(query));
        steps.Enqueue(() =>
        {
            Check($"typed \"{query}\" reaches the box", box.Text == query, $"box: \"{box.Text}\"");
            Check("menu stays open while typing", menu.IsOpen);
            var visibleRows = menu.Items.OfType<MenuItem>().Where(m => m.IsVisible).Select(m => m.Header?.ToString()).ToList();
            Check("results replace the normal rows", visibleRows.Count > 1 && visibleRows.Contains(firstGame?.Name),
                $"visible: {string.Join(" | ", visibleRows.Skip(1).Take(6))}");
        });
        // The synthesized key press produces its own text input, as a real one does.
        steps.Enqueue(() => { Keyboard.Focus(box); SendTestKey(Key.Space); });
        steps.Enqueue(() =>
        {
            Check("Space is typed into the box, the menu stays open", box.Text == query + " " && menu.IsOpen, $"box: \"{box.Text}\", open: {menu.IsOpen}");
            box.Text = query;
        });
        steps.Enqueue(() => { Keyboard.Focus(box); SendTestKey(Key.Down); });
        steps.Enqueue(() =>
        {
            var focused = Keyboard.FocusedElement as MenuItem;
            Check("Down moves from the box to the first result", focused != null && (focused.Header as string) == firstGame?.Name,
                $"focused: {(focused?.Header as string) ?? Keyboard.FocusedElement?.GetType().Name}");
            Keyboard.Focus(box);
        });
        string? launched = null;
        steps.Enqueue(() =>
        {
            // Swap the result rows' launch for a recorder, then press Enter in the box.
            foreach (var item in menu.Items.OfType<MenuItem>().Where(m => m.IsVisible && m.Command != null && m.Header is string))
            {
                string name = (string)item.Header;
                item.Command = new ViewModels.RelayCommand(() => launched = name);
            }
            Keyboard.Focus(box);
            SendTestKey(Key.Enter);
        });
        steps.Enqueue(() =>
        {
            Check("Enter launches the first match and closes the menu", launched == firstGame?.Name && !menu.IsOpen,
                $"launched: {launched ?? "nothing"}, open: {menu.IsOpen}");
            Check("closing clears the query and restores the menu", box.Text.Length == 0 && menu.Items.Count == ownRowsBefore,
                $"box: \"{box.Text}\", rows {menu.Items.Count} of {ownRowsBefore}");
            menu.IsOpen = true;
        });
        steps.Enqueue(() => Type("zzqq"));
        steps.Enqueue(() =>
        {
            Check("no match shows one disabled row", menu.Items.OfType<MenuItem>().Any(m => m.IsVisible && (m.Header as string) == "No games match"));
            Keyboard.Focus(box);
            SendTestKey(Key.Escape);
        });
        steps.Enqueue(() =>
        {
            Check("first Esc clears the query, menu stays open", box.Text.Length == 0 && menu.IsOpen, $"box: \"{box.Text}\", open: {menu.IsOpen}");
            Keyboard.Focus(box);
            SendTestKey(Key.Escape);
        });
        steps.Enqueue(() =>
        {
            Check("second Esc closes the menu", !menu.IsOpen);
            // A closed menu may be rebuilt at any time (poster loads, sessions), so pick up the
            // one the tray icon has now before the next check.
            menu = _trayIcon.ContextMenu;
            box = FindTraySearchBox(menu)!;
            menu.Placement = PlacementMode.AbsolutePoint;
            menu.HorizontalOffset = 200;
            menu.VerticalOffset = 200;
            menu.IsOpen = true;
        });
        steps.Enqueue(() =>
        {
            Keyboard.Focus(box);
            Type(query);
            UpdateTrayContextMenu(); // a library or session update arriving mid-search
        });
        steps.Enqueue(() =>
        {
            Check("an update while open leaves the menu and the query alone",
                ReferenceEquals(_trayIcon.ContextMenu, menu) && menu.IsOpen && box.Text == query,
                $"same menu: {ReferenceEquals(_trayIcon.ContextMenu, menu)}, box: \"{box.Text}\"");
            menu.IsOpen = false;
        });
        steps.Enqueue(() =>
        {
            Check("the deferred rebuild runs once the menu closes", !ReferenceEquals(_trayIcon.ContextMenu, menu));
            OnTrayMenuHotkeyTriggered();
        });
        steps.Enqueue(() =>
        {
            bool opened = _trayIcon.ContextMenu.IsOpen;
            OnTrayMenuHotkeyTriggered();
            Check("pressing the tray menu hotkey again closes the menu", opened && !_trayIcon.ContextMenu.IsOpen,
                $"opened: {opened}, open after second press: {_trayIcon.ContextMenu.IsOpen}");
        });

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) =>
        {
            if (steps.Count == 0)
            {
                timer.Stop();
                File.WriteAllText(outPath, report.ToString());
                Console.WriteLine(report.ToString());
                ExitApplication();
                return;
            }
            try { steps.Dequeue()(); }
            catch (Exception ex) { report.AppendLine("ERROR " + ex.Message); }
        };
        timer.Start();
    }
}
#endif
