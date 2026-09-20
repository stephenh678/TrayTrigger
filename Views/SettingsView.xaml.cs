using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

/// <summary>
/// The Settings page: the tab strip, the card search and every settings card, over
/// <see cref="SettingsViewModel"/> (set by the window, as the System and Tools pages are).
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        // The DLSS card's game count changes in Edit Game, so it is re-read when the page is shown.
        IsVisibleChanged += (_, _) => { if (IsVisible) (DataContext as SettingsViewModel)?.Dlss?.Refresh(); };
    }

    /// <summary>The page's card search box, for the window's Ctrl+F / Escape handling.</summary>
    public SearchBox PageSearchBox => SettingsSearchBox;

    /// <summary>
    /// The undo toast is held while the pointer is over it or keyboard focus is inside it, and its
    /// 10-second window pauses. Also re-read when the toast shows or hides: a hidden toast can keep
    /// keyboard focus (Undo pressed with Enter), which must not leave the next toast's window paused.
    /// </summary>
    private void OnUndoToastHoldChanged(object sender, RoutedEventArgs e) => UpdateUndoToastHold(sender);

    private void OnUndoToastFocusChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateUndoToastHold(sender);

    private void UpdateUndoToastHold(object sender)
    {
        if (sender is not FrameworkElement toast) return;
        if (toast.DataContext is not SettingsViewModel settings) return;
        settings.SetUndoToastHeld(toast.IsVisible && (toast.IsMouseOver || toast.IsKeyboardFocusWithin));
    }

    /// <summary>Blocks non-digit keystrokes on tray "max items" TextBoxes (e.g. MaxRecentInTray).</summary>
    private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // ASCII only: char.IsDigit also passes Arabic-Indic and other Unicode digits, which the
        // int binding then rejects with a validation error instead of the keystroke being blocked.
        e.Handled = !e.Text.All(char.IsAsciiDigit);
    }

    /// <summary>Blocks pasting non-numeric text into tray "max items" TextBoxes.</summary>
    private void NumericTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(typeof(string)) && e.DataObject.GetData(typeof(string)) is string text && text.All(char.IsAsciiDigit))
        {
            return;
        }
        e.CancelCommand();
    }
}
