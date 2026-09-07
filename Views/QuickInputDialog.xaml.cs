using System.Collections.Generic;
using System.Linq;
using System.Windows;
using TrayTrigger.Services;

namespace TrayTrigger.Views;

public partial class QuickInputDialog : Window
{
    public string ResultValue { get; private set; } = string.Empty;

    public QuickInputDialog(string title, string heading, string prompt, string initialValue, IEnumerable<string>? suggestions = null)
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
            ValueComboBox.ItemsSource = suggestions!.ToList();
            ValueComboBox.Text = initialValue;
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
