using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

public partial class ThemeColorRowControl : UserControl
{
    public ThemeColorRowControl() => InitializeComponent();

    private void HexBox_LostFocus(object sender, RoutedEventArgs e) => (DataContext as ThemeColorRow)?.CommitHex();

    private void HexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        (DataContext as ThemeColorRow)?.CommitHex();
        e.Handled = true;
    }

    // The keyboard goes into the picker as it opens, so the arrow keys work straight away.
    private void PickerPopup_Opened(object sender, System.EventArgs e) => Picker.FocusSquare();

    // Esc closes the picker only: it must not also reach the window and ask to discard the edits.
    private void Picker_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        SwatchButton.IsChecked = false;
        SwatchButton.Focus();
        e.Handled = true;
    }
}
