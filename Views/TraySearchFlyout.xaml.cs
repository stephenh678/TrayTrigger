using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

public partial class TraySearchFlyout : Window
{
    private readonly List<GameCardViewModel> _allGames;
    private readonly Action<GameCardViewModel> _onLaunch;
    private readonly ObservableCollection<GameCardViewModel> _filteredGames = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(POINT pt, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    public TraySearchFlyout(IEnumerable<GameCardViewModel> games, Action<GameCardViewModel> onLaunch)
    {
        InitializeComponent();

        _allGames = games.ToList();
        _onLaunch = onLaunch;

        ResultsListBox.ItemsSource = _filteredGames;

        Loaded += TraySearchFlyout_Loaded;
        Deactivated += (s, e) => Close();
        PreviewKeyDown += TraySearchFlyout_PreviewKeyDown;

        RefreshFilter();
    }

    private void TraySearchFlyout_Loaded(object sender, RoutedEventArgs e)
    {
        PositionAboveTray();

        SearchTextBox.Focus();
        Keyboard.Focus(SearchTextBox);
    }

    private void PositionAboveTray()
    {
        try
        {
            if (GetCursorPos(out POINT cursorPt))
            {
                nint hMon = MonitorFromPoint(cursorPt, MONITOR_DEFAULTTONEAREST);
                if (hMon != 0)
                {
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(hMon, ref mi))
                    {
                        var dpi = VisualTreeHelper.GetDpi(this);
                        double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
                        double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

                        double workLeft = mi.rcWork.Left / scaleX;
                        double workRight = mi.rcWork.Right / scaleX;
                        double workTop = mi.rcWork.Top / scaleY;
                        double workBottom = mi.rcWork.Bottom / scaleY;

                        Left = Math.Max(workLeft, workRight - Width - 14);
                        Top = Math.Max(workTop, workBottom - Height - 14);
                        return;
                    }
                }
            }
        }
        catch
        {
            // Fallback to primary work area if monitor detection fails
        }

        // Default primary monitor positioning
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(0, workArea.Right - Width - 14);
        Top = Math.Max(0, workArea.Bottom - Height - 14);
    }

    public void SetSearchQuery(string query)
    {
        SearchTextBox.Text = query;
        SearchTextBox.CaretIndex = query.Length;
    }

    private void RefreshFilter()
    {
        string query = SearchTextBox.Text.Trim();
        _filteredGames.Clear();

        IEnumerable<GameCardViewModel> matches;
        if (string.IsNullOrWhiteSpace(query))
        {
            matches = _allGames.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            matches = _allGames.Where(g =>
                g.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                g.Category.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var game in matches)
        {
            _filteredGames.Add(game);
        }

        ResultCountText.Text = $"{_filteredGames.Count} {(_filteredGames.Count == 1 ? "game" : "games")}";
        EmptyStatePanel.Visibility = _filteredGames.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultsListBox.Visibility = _filteredGames.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_filteredGames.Count > 0)
        {
            ResultsListBox.SelectedIndex = 0;
        }
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        PlaceholderTextBlock.Visibility = string.IsNullOrEmpty(SearchTextBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = string.IsNullOrEmpty(SearchTextBox.Text) ? Visibility.Collapsed : Visibility.Visible;
        RefreshFilter();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        SearchTextBox.Text = string.Empty;
        SearchTextBox.Focus();
    }

    private void SearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            if (_filteredGames.Count > 0)
            {
                int nextIndex = (ResultsListBox.SelectedIndex + 1) % _filteredGames.Count;
                ResultsListBox.SelectedIndex = nextIndex;
                ResultsListBox.ScrollIntoView(ResultsListBox.SelectedItem);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            if (_filteredGames.Count > 0)
            {
                int prevIndex = ResultsListBox.SelectedIndex <= 0 ? _filteredGames.Count - 1 : ResultsListBox.SelectedIndex - 1;
                ResultsListBox.SelectedIndex = prevIndex;
                ResultsListBox.ScrollIntoView(ResultsListBox.SelectedItem);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            LaunchSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void TraySearchFlyout_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void ResultsListBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            LaunchSelected();
            e.Handled = true;
        }
    }

    private void ResultsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        LaunchSelected();
    }

    private void LaunchSelected()
    {
        if (ResultsListBox.SelectedItem is GameCardViewModel selected)
        {
            Close();
            _onLaunch(selected);
        }
    }
}
