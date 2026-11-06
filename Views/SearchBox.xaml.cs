using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace TrayTrigger.Views;

/// <summary>
/// The search box beside a page's tabs (Settings, About, System) and in the Library and Tools search
/// bars: a ModernTextBox with the hotkey box's clear x inside its right edge. <see cref="Text"/> is the search, bound two-way by default; typing reaches
/// it 150 ms after the last keystroke, so a word typed quickly runs one search, while clearing it -
/// the x, Escape, or the host setting it - is immediate.
/// <para>
/// With <see cref="ResultsHost"/> set (a page's card panel, see <see cref="CardSearch"/>) the box also
/// lists the cards that match, from every tab, in a dropdown under it: Up and Down pick, Enter or a
/// click shows the card on its tab. The page itself never shrinks to the matches.
/// </para>
/// </summary>
public partial class SearchBox : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SearchBox),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(SearchBox), new PropertyMetadata("Search..."));

    public static readonly DependencyProperty ResultsHostProperty = DependencyProperty.Register(
        nameof(ResultsHost), typeof(Panel), typeof(SearchBox),
        new PropertyMetadata(null, (d, _) => ((SearchBox)d).RefreshResults()));

    public static readonly DependencyProperty NoResultsHintProperty = DependencyProperty.Register(
        nameof(NoResultsHint), typeof(string), typeof(SearchBox), new PropertyMetadata(string.Empty));

    /// <summary>Said under "No matches" when the page has nothing for the search: where else to look
    /// (the other pages have a search of their own).</summary>
    public string NoResultsHint
    {
        get => (string)GetValue(NoResultsHintProperty);
        set => SetValue(NoResultsHintProperty, value);
    }

    /// <summary>The page's card panel to list matches from; null leaves this a plain search box.</summary>
    public Panel? ResultsHost
    {
        get => (Panel?)GetValue(ResultsHostProperty);
        set => SetValue(ResultsHostProperty, value);
    }

    public static readonly DependencyProperty ClearTextProperty = DependencyProperty.Register(
        nameof(ClearText), typeof(string), typeof(SearchBox), new PropertyMetadata("Clear search"));

    /// <summary>What the clear x is called (tooltip and screen reader): "Clear filter" on a filter box.</summary>
    public string ClearText
    {
        get => (string)GetValue(ClearTextProperty);
        set => SetValue(ClearTextProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public SearchBox()
    {
        InitializeComponent();
        Box.SetBinding(TextBox.TextProperty, new Binding(nameof(Text))
        {
            Source = this,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            Delay = 150,
        });
        // ModernTextBox shows its Tag as the hint.
        Box.SetBinding(TagProperty, new Binding(nameof(Placeholder)) { Source = this });
        Box.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(Placeholder)) { Source = this });

        Box.PreviewKeyDown += OnBoxKeyDown;
        Box.LostKeyboardFocus += (_, _) => Results.IsOpen = false;

        // The popup is not StaysOpen=False: that captures the mouse, and a list left open when the page
        // is switched away would still hold it on return, so the app would ignore clicks until the
        // header was clicked. It is shut by hand instead - on leaving the page, on the window losing
        // focus, and on a click anywhere but the box.
        IsVisibleChanged += (_, _) => { if (!IsVisible) Results.IsOpen = false; };
        Loaded += (_, _) => HookWindow(Window.GetWindow(this));
        Unloaded += (_, _) =>
        {
            Results.IsOpen = false;
            HookWindow(null);
        };
    }

    private Window? _window;

    /// <summary>Listens to the window the box is in, and to no other: Loaded can come twice, and Unloaded
    /// comes after the box has left the window, when asking it for its window gives nothing.</summary>
    private void HookWindow(Window? window)
    {
        if (ReferenceEquals(window, _window)) return;
        if (_window != null)
        {
            _window.Deactivated -= OnWindowDeactivated;
            _window.PreviewMouseDown -= OnWindowMouseDown;
        }
        _window = window;
        if (_window != null)
        {
            _window.Deactivated += OnWindowDeactivated;
            _window.PreviewMouseDown += OnWindowMouseDown;
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => Results.IsOpen = false;

    /// <summary>A click in the window outside the box and outside the list closes the list. A click on a
    /// result also routes through here (a popup's events bubble to its owner's window), and closing the
    /// list on the mouse-down would remove the row before its click completed.</summary>
    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Results.IsOpen && !Box.IsMouseOver && Results.Child?.IsMouseOver != true) Results.IsOpen = false;
    }

    /// <summary>One line of the dropdown; IsSelected is the row Enter would choose.</summary>
    internal sealed class ResultRow(CardHit hit) : INotifyPropertyChanged
    {
        private bool _isSelected;
        public CardHit Hit { get; } = hit;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private List<ResultRow> _rows = [];
    private int _selected;

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == TextProperty) RefreshResults();
    }

    /// <summary>Lists what matches the text now. Only while the cursor is here: a search carried over from
    /// another tab shouldn't open a list over a page the reader is using.</summary>
    private void RefreshResults()
    {
        if (ResultsHost == null || !Box.IsKeyboardFocused || CardSearch.SplitTerms(Text).Length == 0)
        {
            if (Results != null) Results.IsOpen = false;
            return;
        }

        _rows = CardSearch.Find(ResultsHost!, Text, out int total).Select(hit => new ResultRow(hit)).ToList();
        // A list cut short says so, and what to do about it.
        MoreNote.Text = $"Showing the first {_rows.Count} of {total}. Add a word to narrow the list.";
        MoreNote.Visibility = total > _rows.Count ? Visibility.Visible : Visibility.Collapsed;
        _selected = 0;
        if (_rows.Count > 0) _rows[0].IsSelected = true;
        ResultsList.ItemsSource = _rows;
        NoResults.Text = NoResultsHint.Length > 0 ? "No matches on this page. " + NoResultsHint : "No matches";
        NoResults.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultsScroll.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        // The box sits at the right of its page, so the list hangs from the box's right edge (its
        // 440 wide card) and stays inside the window instead of reaching past it.
        Results.HorizontalOffset = Box.ActualWidth - 440;
        Results.IsOpen = true;
    }

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        // A key pressed within the typing delay of the last letter: bring Text up to what is in the box
        // first, so Enter picks from the list for what was typed and not for the word before it.
        if (e.Key is Key.Enter or Key.Down or Key.Up)
            Box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

        // Enter or Down on a closed list with a search in the box opens it again: the list closes when a
        // result is chosen or on a click away, and the search is still there to run once more.
        if (!Results.IsOpen && e.Key is Key.Down or Key.Enter && ResultsHost != null)
        {
            RefreshResults();
            e.Handled = Results.IsOpen;
            return;
        }
        if (!Results.IsOpen || _rows.Count == 0) return;
        switch (e.Key)
        {
            case Key.Down: Select(_selected + 1); e.Handled = true; break;
            case Key.Up: Select(_selected - 1); e.Handled = true; break;
            case Key.Enter: Choose(_rows[_selected].Hit); e.Handled = true; break;
        }
    }

    private void Select(int index)
    {
        _rows[_selected].IsSelected = false;
        _selected = (index % _rows.Count + _rows.Count) % _rows.Count;
        _rows[_selected].IsSelected = true;
        (ResultsList.ItemContainerGenerator.ContainerFromIndex(_selected) as FrameworkElement)?.BringIntoView();
    }

    private void OnResultClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is CardHit hit) Choose(hit);
    }

    private void Choose(CardHit hit)
    {
        Results.IsOpen = false;
        CardSearch.GoTo(hit);
    }

    /// <summary>True while the cursor is in the box.</summary>
    public bool IsBoxFocused => Box.IsKeyboardFocused;

    /// <summary>Puts the cursor in the box with its text selected (Ctrl+F).</summary>
    public void FocusBox()
    {
        Box.Focus();
        Box.SelectAll();
    }

    /// <summary>Ends the search. SetCurrentValue keeps the host's binding and passes the change on.</summary>
    public void Clear() => SetCurrentValue(TextProperty, string.Empty);

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        Clear();
        Box.Focus();
    }
}
