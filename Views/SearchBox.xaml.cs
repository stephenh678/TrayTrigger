using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

namespace TrayTrigger.Views;

/// <summary>
/// The search box beside a page's tabs (Settings, About): a ModernTextBox with the hotkey box's clear
/// x inside its right edge. <see cref="Text"/> is the search, bound two-way by default; typing reaches
/// it 150 ms after the last keystroke, so a word typed quickly runs one search, while clearing it -
/// the x, Escape, or the host setting it - is immediate.
/// </summary>
public partial class SearchBox : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SearchBox),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(SearchBox), new PropertyMetadata("Search..."));

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
