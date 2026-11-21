using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using TrayTrigger.Services;

namespace TrayTrigger.Views;

public partial class QuickInputDialog : Window
{
    public string ResultValue { get; private set; } = string.Empty;

    public QuickInputDialog(string title, string heading, string prompt, string initialValue, IEnumerable<string>? suggestions = null, bool showAdd = false)
    {
        InitializeComponent();
        WindowThemeService.PrepareForFirstShow(this);

        Title = title;
        HeadingTextBlock.Text = heading;
        PromptTextBlock.Text = prompt;

        Owner = WindowHelper.ActiveOwner();

        bool useComboBox = suggestions != null && suggestions.Any();
        if (useComboBox)
        {
            ValueTextBox.Visibility = Visibility.Collapsed;
            ValueComboBox.Visibility = Visibility.Visible;
            _items = new ObservableCollection<string>(suggestions!);
            ValueComboBox.ItemsSource = _items;
            AddButton.Visibility = showAdd ? Visibility.Visible : Visibility.Collapsed;
            if (showAdd)
            {
                // The box is for picking or typing another; what the game has now is the chip below it.
                _current = initialValue;
                ShowChip();
            }
            else
            {
                ValueComboBox.Text = initialValue;
            }
        }
        else
        {
            ValueTextBox.Visibility = Visibility.Visible;
            ValueComboBox.Visibility = Visibility.Collapsed;
            ValueTextBox.Text = initialValue;
        }

        Loaded += (s, e) =>
        {
            WindowThemeService.CenterOverOwner(this);
            Activate();
            if (useComboBox)
            {
                ValueComboBox.Focus();
            }
            else
            {
                ValueTextBox.Focus();
                ValueTextBox.SelectAll();
            }
        };
    }

    private readonly ObservableCollection<string> _items = new();

    /// <summary>Change Category's Add: the typed name joins the list and is selected, ready to Save.</summary>
    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ValueComboBox.Text)) return;
        string name = LibraryConstants.NormalizeCategory(ValueComboBox.Text);
        string? same = _items.FirstOrDefault(i => string.Equals(i, name, StringComparison.OrdinalIgnoreCase));
        if (same == null)
        {
            _items.Add(name);
            same = name;
        }
        _current = same;
        ValueComboBox.SelectedItem = null;
        ValueComboBox.Text = string.Empty;
        ShowChip();
    }

    private string _current = string.Empty;

    private void ShowChip()
    {
        ChipText.Text = _current;
        ChipBorder.Visibility = _current.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ChipRemove.Visibility = string.Equals(_current, LibraryConstants.Uncategorized, StringComparison.OrdinalIgnoreCase) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ChipRemove_Click(object sender, RoutedEventArgs e)
    {
        _current = LibraryConstants.Uncategorized;
        ShowChip();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ValueComboBox.Visibility == Visibility.Visible)
        {
            string typed = ValueComboBox.Text?.Trim() ?? string.Empty;
            // With the chip (Change Category), a name left in the box is the one meant; else the chip stands.
            bool withChip = AddButton.Visibility == Visibility.Visible;
            ResultValue = withChip && typed.Length == 0 ? _current : typed;
            // Nothing chosen (a selection of games with different categories, and no pick made): not "set all to Uncategorized".
            if (withChip && ResultValue.Length == 0)
            {
                DialogResult = false;
                Close();
                return;
            }
        }
        else
        {
            ResultValue = ValueTextBox.Text?.Trim() ?? string.Empty;
        }

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
