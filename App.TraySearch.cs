using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger;

/// <summary>
/// The optional search row at the top of the tray menu (UX-16, Settings › Tray Menu › "Show a
/// search box at the top of the tray menu"). It is one more row of the existing menu, not a window:
/// typing filters in place by hiding the menu's own rows and inserting result rows built exactly
/// like the menu's game rows, so icons, compact layout and launching are unchanged. Clearing the box
/// (or closing the menu) puts the menu back as it was.
/// </summary>
public partial class App
{
    /// <summary>A rebuild that arrived while the tray menu was open (a session starting, the library
    /// changing). Replacing the menu would close it and lose the query, so it waits for Closed.</summary>
    private bool _trayMenuRebuildPending;

    /// <summary>
    /// The container for the search box. A ContextMenu wraps anything that isn't a MenuItem or
    /// Separator in a MenuItem of its own, and a plain MenuItem treats Enter and Space as a click and
    /// closes the menu on a mouse click. This one never activates: keys go to the box inside it.
    /// </summary>
    private sealed class TraySearchRow : MenuItem
    {
        public TraySearchRow()
        {
            StaysOpenOnClick = true;
            Focusable = false;
        }

        protected override void OnClick() { }
        protected override void OnKeyDown(KeyEventArgs e) { }
    }

    /// <summary>
    /// Adds the search row at the top of <paramref name="menu"/> (above Now Playing) and wires the
    /// filtering. <paramref name="navStart"/> is the index of the separator before the navigation
    /// rows (Games Library, Settings, Exit), which stay put while a query is showing.
    /// </summary>
    private void AttachTraySearch(ContextMenu menu, int navStart, IReadOnlyList<GameCardViewModel> games)
    {
        var box = new TextBox
        {
            Style = (Style)FindResource("TraySearchBox"),
            FontSize = TrayCompact ? 12 : 12.5,
        };
        AutomationProperties.SetName(box, "Search games");
        var placeholder = new TextBlock
        {
            Text = "Search games",
            Foreground = (Brush)FindResource("BrushTextMuted"),
            FontSize = box.FontSize,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        placeholder.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(TextBox.Text))
        {
            Source = box,
            Converter = EmptyTextToVisibility.Instance,
        });
        var header = new Grid { MinWidth = 200 };
        header.Children.Add(placeholder);
        header.Children.Add(box);

        var row = new TraySearchRow
        {
            Header = header,
            Style = TrayItemStyle,
            Icon = CreateTrayGlyph("", (Brush)FindResource("BrushTextMuted")),
        };
        AutomationProperties.SetName(row, "Search games");

        // The menu's own rows, and whether each was showing, so an empty query restores them exactly.
        var ownRows = menu.Items.Cast<object>().Take(navStart).OfType<UIElement>()
            .Select(e => (Element: e, Visibility: e.Visibility)).ToList();
        menu.Items.Insert(0, row);
        menu.Items.Insert(1, new Separator());
        var results = new List<UIElement>();

        void ShowQuery(string query)
        {
            foreach (var r in results) menu.Items.Remove(r);
            results.Clear();

            bool searching = query.Trim().Length > 0;
            foreach (var (element, visibility) in ownRows)
            {
                element.Visibility = searching ? Visibility.Collapsed : visibility;
            }
            if (!searching) return;

            var gameMatches = TraySearch.Match(games, g => g.Name, g => g.Category, query, TraySearch.MaxGames);
            var toolMatches = _mainViewModel.Settings.EnableTools && _mainViewModel.Settings.ShowToolsInTray
                ? TraySearch.Match(_mainViewModel.Tools.TrayTools(), t => t.Name, t => t.Category, query, TraySearch.MaxTools)
                : [];

            foreach (var card in gameMatches) results.Add(CreateGameMenuItem(card));
            if (gameMatches.Count > 0 && toolMatches.Count > 0) results.Add(new Separator());
            foreach (var tool in toolMatches) results.Add(CreateToolMenuItem(tool));
            if (results.Count == 0)
            {
                results.Add(new MenuItem
                {
                    Header = "No games match",
                    Style = TrayItemStyle,
                    IsEnabled = false,
                    IsHitTestVisible = false,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                });
            }

            int at = 2; // after the search row and its separator
            foreach (var r in results) menu.Items.Insert(at++, r);
        }

        MenuItem? FirstResult() => results.OfType<MenuItem>().FirstOrDefault(m => m.IsEnabled);

        box.TextChanged += (_, _) => ShowQuery(box.Text);

        box.PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Enter:
                    // Launch the top match. With nothing typed, Enter does nothing here.
                    e.Handled = true;
                    if (FirstResult() is { Command: { } command } first && command.CanExecute(null))
                    {
                        menu.IsOpen = false;
                        command.Execute(null);
                    }
                    break;
                case Key.Down:
                    // Into the results, or into the menu's own first row when nothing is typed.
                    var target = results.Count > 0
                        ? FirstResult()
                        : menu.Items.OfType<MenuItem>().Skip(1).FirstOrDefault(m => m.IsVisible && m.IsEnabled && m.Focusable);
                    if (target != null)
                    {
                        target.Focus();
                        e.Handled = true;
                    }
                    break;
                case Key.Escape when box.Text.Length > 0:
                    // First Esc clears the query; the next one closes the menu as usual.
                    box.Clear();
                    e.Handled = true;
                    break;
            }
        };

        // A letter typed while a row has focus goes to the box, so typing always searches.
        menu.PreviewTextInput += (_, e) =>
        {
            if (box.IsKeyboardFocusWithin || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
            box.Focus();
            box.Text += e.Text;
            box.CaretIndex = box.Text.Length;
            e.Handled = true;
        };

        menu.Opened += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            Keyboard.Focus(box);
        }, DispatcherPriority.Input);

        // Closing always clears the query, so the next open starts from the normal menu.
        menu.Closed += (_, _) => box.Clear();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// The open-tray-menu hotkey (default Ctrl+Alt+T): the same menu the tray icon's right-click
    /// opens, at the mouse pointer, with the search box focused by its Opened handler. Pressed
    /// again while it is open, it closes it.
    /// </summary>
    private void OnTrayMenuHotkeyTriggered()
    {
        if (_isShuttingDown || _trayIcon?.ContextMenu is not { } menu) return;
        if (menu.IsOpen)
        {
            menu.IsOpen = false;
            return;
        }

        menu.PlacementTarget = null;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;

        // A popup opened by a background app doesn't get the keyboard, so typing would go to
        // whatever was in front. Receiving the hotkey lets TrayTrigger take the foreground, which is
        // what the tray icon's own right-click does too. Activating the popup's window moves WPF's
        // keyboard focus out of the menu, which closes it; a popup keeps its window for a moment
        // after closing, so opening it again at once reuses the window that now has the foreground.
        if (PresentationSource.FromVisual(menu) is System.Windows.Interop.HwndSource source)
        {
            SetForegroundWindow(source.Handle);
            if (!menu.IsOpen) menu.IsOpen = true;
        }
    }

    /// <summary>After the tray menu closes: the rebuild it deferred, if any.</summary>
    private void OnTrayMenuClosed(object? sender, RoutedEventArgs e)
    {
        if (!_trayMenuRebuildPending) return;
        _trayMenuRebuildPending = false;
        Dispatcher.BeginInvoke(UpdateTrayContextMenu, DispatcherPriority.Background);
    }

    private sealed class EmptyTextToVisibility : IValueConverter
    {
        public static readonly EmptyTextToVisibility Instance = new();

        public object Convert(object value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
