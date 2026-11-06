using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

/// <summary>
/// The filter button and flyout of a list page: the Library's games and the Tools page's tools. Its
/// DataContext is the page's <see cref="FilterFlyoutViewModel"/>; the dialogs it asks for a name and a yes
/// with live here, so the view model stays free of them.
/// </summary>
public partial class FilterFlyout : UserControl
{
    public static readonly DependencyProperty HelpTopicProperty = DependencyProperty.Register(
        nameof(HelpTopic), typeof(string), typeof(FilterFlyout),
        new PropertyMetadata(string.Empty, (d, e) => ((FilterFlyout)d).HelpLink.CommandParameter = e.NewValue));

    /// <summary>The Help topic the flyout's "Learn more" link opens (a path such as "library/filtering").</summary>
    public string HelpTopic
    {
        get => (string)GetValue(HelpTopicProperty);
        set => SetValue(HelpTopicProperty, value);
    }

    public FilterFlyout()
    {
        InitializeComponent();

        // Not settable from XAML - it's a delegate, not a value.
        FilterPopup.CustomPopupPlacementCallback = PlaceFilterPopup;

        // The data context arrives after construction.
        DataContextChanged += (_, _) => WirePrompts();
        Loaded += (_, _) => WirePrompts();
    }

    /// <summary>True while the flyout is open, so the window can leave Escape to it.</summary>
    public bool IsOpen => FilterPopup.IsOpen;

    internal Button Button => FilterButton;
    internal Border Badge => FilterBadge;

    private void WirePrompts()
    {
        if (DataContext is not FilterFlyoutViewModel filter) return;
        string things = filter.NounPlural;
        filter.PromptForName = () =>
        {
            var dialog = new QuickInputDialog("Save Filter", "Save this filter",
                "Name it. The ticked options are saved; the category tab and filter text are not.", string.Empty);
            return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ResultValue) ? dialog.ResultValue : null;
        };
        filter.ConfirmReplace = (name, saved, now) => ModernDialog.Confirm(
            WindowHelper.ActiveOwner(), "Replace Saved Filter", $"Replace the saved filter \"{name}\"?",
            $"Saved: {saved}.\nNow: {now}.", confirmText: "Replace", cancelText: "Cancel");
        filter.ConfirmDelete = name => ModernDialog.ConfirmDelete(
            WindowHelper.ActiveOwner(), "Delete Saved Filter", $"Delete the saved filter \"{name}\"?",
            $"Your {things} and your other filters are not affected.", confirmText: "Delete");
    }

    private FilterFlyoutViewModel? Filter => DataContext as FilterFlyoutViewModel;

    /// <summary>
    /// Closes the flyout before the name box opens over it. Click runs ahead of the button's Command,
    /// which is what asks for the name.
    /// </summary>
    private void FilterSave_Click(object sender, RoutedEventArgs e)
    {
        if (Filter is { } filter) filter.IsOpen = false;
    }

    /// <summary>
    /// Right-aligns the flyout under its button. The panel's width varies with its content
    /// (MinWidth 270, MaxWidth 300, and the longest label decides where in between it lands), so a
    /// fixed HorizontalOffset can only ever be correct at one of those widths. Measuring at placement
    /// time is correct at every width.
    /// </summary>
    private static CustomPopupPlacement[] PlaceFilterPopup(Size popupSize, Size targetSize, Point offset) =>
    [
        new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width, targetSize.Height + 4), PopupPrimaryAxis.Horizontal)
    ];

    /// <summary>
    /// Puts focus on the flyout itself, so Escape works and Tab goes to its first control. A Popup does
    /// not take focus on its own, which left the panel unusable without a mouse. Focus goes to the panel
    /// and not to its first option: an option with focus draws a blue outline, which on opening read as
    /// that option being picked.
    /// </summary>
    private void FilterPopup_Opened(object? sender, EventArgs e)
    {
        if (FilterPopup.Child is FrameworkElement child)
        {
            child.Focus();
        }
    }

    /// <summary>
    /// Escape closes the flyout and puts focus back on the button that opened it, per the ARIA
    /// authoring practices - without the second half, focus is left orphaned on a panel that is
    /// no longer on screen.
    /// </summary>
    private void FilterPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (Filter is { } filter) filter.IsOpen = false;
        FilterButton.Focus();
        e.Handled = true;
    }

    /// <summary>
    /// Clear leaves the flyout open, but the button hides itself once nothing is ticked, and a
    /// hidden button can't keep keyboard focus. Click runs ahead of the Command that clears, so
    /// focus moves once that has happened: back onto the panel, as when it opened.
    /// </summary>
    private void FilterClear_Click(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (FilterPopup.IsOpen && FilterPopup.Child is FrameworkElement child)
            {
                child.Focus();
            }
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>
    /// Closes the flyout before the help window opens over it. Click runs ahead of the button's
    /// Command, which is what actually shows the topic.
    /// </summary>
    private void FilterHelp_Click(object sender, RoutedEventArgs e)
    {
        if (Filter is { } filter) filter.IsOpen = false;
    }
}
