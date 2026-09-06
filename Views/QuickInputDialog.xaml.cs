using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace TrayTrigger.Views;

public partial class QuickInputDialog : Window
{
    public string ResultValue { get; private set; } = string.Empty;

    public QuickInputDialog(string title, string heading, string prompt, string initialValue, IEnumerable<string>? suggestions = null)
    {
        InitializeComponent();

        Title = title;
        HeadingTextBlock.Text = heading;
        PromptTextBlock.Text = prompt;

        if (Application.Current?.MainWindow is { IsVisible: true } main)
        {
            Owner = main;
        }

        Loaded += (s, e) =>
        {
            if (Owner != null)
            {
                Left = Owner.Left + (Owner.ActualWidth - ActualWidth) / 2;
                Top = Owner.Top + (Owner.ActualHeight - ActualHeight) / 2;
            }
            Activate();
        };

        if (suggestions != null && suggestions.Any())
        {
            ValueTextBox.Visibility = Visibility.Collapsed;
            ValueComboBox.Visibility = Visibility.Visible;
            ValueComboBox.ItemsSource = suggestions.ToList();
            ValueComboBox.Text = initialValue;
            Loaded += (s, e) => ValueComboBox.Focus();
        }
        else
        {
            ValueTextBox.Visibility = Visibility.Visible;
            ValueComboBox.Visibility = Visibility.Collapsed;
            ValueTextBox.Text = initialValue;
            Loaded += (s, e) =>
            {
                ValueTextBox.Focus();
                ValueTextBox.SelectAll();
            };
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ValueComboBox.Visibility == Visibility.Visible)
        {
            ResultValue = ValueComboBox.Text?.Trim() ?? string.Empty;
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
