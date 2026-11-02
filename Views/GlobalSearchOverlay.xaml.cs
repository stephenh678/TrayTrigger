using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace TrayTrigger.Views;

/// <summary>
/// The window's search: a box over the page (Ctrl+K, or the magnifier in the sidebar) with the matches
/// from every page under it, grouped by what they are. Up and Down pick, Enter or a click chooses, Esc or a
/// click outside closes. It is part of the window rather than a popup, so it can't capture the mouse or be
/// left open over another page, and it shows in screenshots. What is searched, and where a choice goes, is
/// the window's (<see cref="Search"/> and <see cref="Chosen"/>); the matching is <see cref="GlobalSearch"/>.
/// </summary>
public partial class GlobalSearchOverlay : UserControl
{
    private readonly DispatcherTimer _typing = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private List<(GlobalHit Hit, Button Row)> _rows = [];
    private int _selected;
    private IInputElement? _previousFocus;

    internal Func<string, IReadOnlyList<GlobalGroup>>? Search { get; set; }
    internal Action<GlobalHit, string>? Chosen { get; set; }

    public GlobalSearchOverlay()
    {
        InitializeComponent();
        _typing.Tick += (_, _) => { _typing.Stop(); Refresh(); };
        SizeChanged += (_, _) => ResultsScroll.MaxHeight = Math.Max(160, ActualHeight - 48 - 24 - 90);
        Unloaded += (_, _) => _typing.Stop();
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>Shows the box, empty, with the cursor in it. A box already open is put back to empty with the cursor in it.</summary>
    public void Open()
    {
        if (!IsOpen) _previousFocus = Keyboard.FocusedElement;
        Visibility = Visibility.Visible;
        Box.Text = string.Empty;
        Refresh();
        Dispatcher.BeginInvoke(() =>
        {
            Box.Focus();
            Keyboard.Focus(Box);
        }, DispatcherPriority.Input);
    }

    /// <summary>
    /// Hides the box and gives the cursor back to where it was, or - when that is gone, or the search is
    /// closing because a result took you elsewhere - to the window. The cursor has to be somewhere before
    /// the box collapses: a box that held it and then vanished leaves it nowhere, and with nothing focused
    /// the window gets no key presses, so Ctrl+K would stop working (after a game's details closed, say).
    /// </summary>
    public void Close(bool restoreFocus = true)
    {
        if (!IsOpen) return;
        IInputElement? previous = restoreFocus ? _previousFocus : null;
        _previousFocus = null;
        if (previous == null || !previous.Focus()) FocusWindow();
        Visibility = Visibility.Collapsed;
        Box.Text = string.Empty;
        // After the text: emptying it starts the typing delay, and nothing is left to list.
        _typing.Stop();
    }

    /// <summary>For dev captures: types a query and lists its matches now.</summary>
    internal void SetQuery(string query)
    {
        Box.Text = query;
        _typing.Stop();
        Refresh();
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        _typing.Stop();
        _typing.Start();
    }

    private void Refresh()
    {
        ResultsList.Children.Clear();
        _rows = [];
        _selected = 0;

        string query = Box.Text.Trim();
        if (query.Length == 0)
        {
            Message.Text = "Type to find a game, a tool, a setting or a help topic. Enter opens the top result.";
            Message.Visibility = Visibility.Visible;
            ResultsScroll.Visibility = Visibility.Collapsed;
            return;
        }

        var groups = Search?.Invoke(query) ?? [];
        if (groups.Count == 0)
        {
            Message.Text = $"No matches for “{query}”.";
            Message.Visibility = Visibility.Visible;
            ResultsScroll.Visibility = Visibility.Collapsed;
            return;
        }

        Message.Visibility = Visibility.Collapsed;
        ResultsScroll.Visibility = Visibility.Visible;
        foreach (var group in groups)
        {
            var heading = new TextBlock { Text = group.Name.ToUpperInvariant(), Margin = new Thickness(10, 8, 10, 2), FontWeight = FontWeights.SemiBold };
            heading.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeBadge");
            heading.SetResourceReference(TextBlock.ForegroundProperty, "BrushAccentText");
            AutomationProperties.SetName(heading, group.Name);
            ResultsList.Children.Add(heading);
            foreach (var hit in group.Hits)
            {
                var row = BuildRow(hit);
                _rows.Add((hit, row));
                ResultsList.Children.Add(row);
            }
        }
        ResultsScroll.ScrollToTop();
        Paint();
    }

    private Button BuildRow(GlobalHit hit)
    {
        var title = new TextBlock { Text = hit.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeBody");
        title.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextPrimary");

        var where = new TextBlock { Text = hit.Detail, Margin = new Thickness(12, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220 };
        where.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeBody");
        where.SetResourceReference(TextBlock.ForegroundProperty, "BrushAccentText");
        DockPanel.SetDock(where, Dock.Right);

        var line = new DockPanel { LastChildFill = true };
        line.Children.Add(where);
        line.Children.Add(title);

        var content = new StackPanel();
        content.Children.Add(line);
        if (hit.Snippet.Length > 0)
        {
            var snippet = new TextBlock { Text = hit.Snippet, TextWrapping = TextWrapping.Wrap, MaxHeight = 48, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            snippet.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeBody");
            snippet.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextMuted");
            content.Children.Add(snippet);
        }

        var button = new Button
        {
            Content = content,
            Template = (ControlTemplate)Resources["ResultRowTemplate"],
            Focusable = false,
            Cursor = Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = System.Windows.Media.Brushes.Transparent,
        };
        AutomationProperties.SetName(button, hit.Detail.Length > 0 ? $"{hit.Title}, {hit.Detail}" : hit.Title);
        button.Click += (_, _) => Choose(hit);
        return button;
    }

    /// <summary>The row Enter would choose gets the hover wash.</summary>
    private void Paint()
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (i == _selected) _rows[i].Row.SetResourceReference(Control.BackgroundProperty, "BrushSurfaceHover");
            else _rows[i].Row.Background = System.Windows.Media.Brushes.Transparent;
        }
    }

    private void Select(int index)
    {
        if (_rows.Count == 0) return;
        _selected = (index % _rows.Count + _rows.Count) % _rows.Count;
        Paint();
        _rows[_selected].Row.BringIntoView();
    }

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: Select(_selected + 1); e.Handled = true; break;
            case Key.Up: Select(_selected - 1); e.Handled = true; break;
            case Key.Enter:
                // A key pressed within the typing delay of the last letter: list for what is in the box now.
                if (_typing.IsEnabled) { _typing.Stop(); Refresh(); }
                if (_rows.Count > 0) Choose(_rows[_selected].Hit);
                e.Handled = true;
                break;
            case Key.Escape: Close(); e.Handled = true; break;
        }
    }

    private void OnScrimClick(object sender, MouseButtonEventArgs e)
    {
        Close();
        e.Handled = true;
    }

    private void Choose(GlobalHit hit)
    {
        string query = Box.Text.Trim();
        // Closed before the move, so the page that opens gets the keyboard, not this box.
        Close(restoreFocus: false);
        Chosen?.Invoke(hit, query);
    }

    /// <summary>
    /// The window itself holds the cursor. Its logical focus is set too, not just the keyboard's: a window
    /// that is deactivated (a dialog opens) and activated again goes back to its logical focus, and one
    /// with none comes back with the cursor nowhere.
    /// </summary>
    private void FocusWindow()
    {
        if (Window.GetWindow(this) is not { } window) return;
        FocusManager.SetFocusedElement(window, window);
        window.Focus();
    }

    /// <summary>The box takes the cursor again, with what is in it selected (Ctrl+K while it is open).</summary>
    public void FocusBox()
    {
        Box.Focus();
        Box.SelectAll();
    }
}
