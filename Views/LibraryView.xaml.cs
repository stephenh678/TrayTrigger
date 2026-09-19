using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

/// <summary>
/// The Games Library page: the header and toolbar rows, the four view modes, the filter flyout,
/// the selection bar and the undo toast. It inherits the window's <see cref="MainViewModel"/> as
/// its DataContext, so every binding here reads exactly as it did inside MainWindow.
/// <para>
/// Window-level behaviour stays with the window: drag-and-drop anywhere on it, the global key
/// handling and the toasts that float over every page. What the window needs from this page it
/// reaches through the small surface below, rather than through the page's named children.
/// </para>
/// </summary>
public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();

        // Not settable from XAML - it's a delegate, not a value.
        LibraryFilterPopup.CustomPopupPlacementCallback = PlaceLibraryFilterPopup;

        // Both card menus are shared resources; the card they were opened on is remembered in
        // OnCardContextMenuOpening and its highlight cleared here when the menu goes away.
        ((ContextMenu)FindResource("GameItemContextMenu")).Closed += OnCardContextMenuClosed;
        ((ContextMenu)FindResource("GameBatchContextMenu")).Closed += OnCardContextMenuClosed;
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    /// <summary>True while the filter flyout is open, so the window can leave Escape to it.</summary>
    public bool IsFilterFlyoutOpen => LibraryFilterPopup.IsOpen;

    /// <summary>Puts the caret in the library search box and selects what is there (Ctrl+F).</summary>
    public void FocusSearchBox()
    {
        LibrarySearchTextBox.Focus();
        LibrarySearchTextBox.SelectAll();
    }

    /// <summary>
    /// Right-aligns the filter flyout under its button. The panel's width varies with its content
    /// (MinWidth 270, MaxWidth 300, and the Launcher group's labels decide where in between it
    /// lands), so a fixed HorizontalOffset can only ever be correct at one of those widths -
    /// the previous -236 drifted visibly off the button as soon as a long launcher name widened
    /// the panel. Measuring at placement time is correct at every width.
    /// </summary>
    private static CustomPopupPlacement[] PlaceLibraryFilterPopup(Size popupSize, Size targetSize, Point offset) =>
    [
        new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width, targetSize.Height + 4), PopupPrimaryAxis.Horizontal)
    ];

    /// <summary>
    /// Moves focus into the flyout so its tick boxes are reachable by keyboard. A Popup does not
    /// take focus on its own, which left the whole panel unusable without a mouse.
    /// </summary>
    private void LibraryFilterPopup_Opened(object? sender, EventArgs e)
    {
        if (LibraryFilterPopup.Child is FrameworkElement child)
        {
            child.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }
    }

    /// <summary>
    /// Escape closes the flyout and puts focus back on the button that opened it, per the ARIA
    /// authoring practices - without the second half, focus is left orphaned on a panel that is
    /// no longer on screen.
    /// </summary>
    private void LibraryFilterPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        ViewModel.Library.Filter.IsOpen = false;
        LibraryFilterButton.Focus();
        e.Handled = true;
    }

    /// <summary>
    /// Clear leaves the flyout open, but the button hides itself once nothing is ticked, and a
    /// hidden button can't keep keyboard focus. Click runs ahead of the Command that clears, so
    /// focus moves once that has happened: onto the first option, which is now the first
    /// focusable element in the panel.
    /// </summary>
    private void LibraryFilterClear_Click(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (LibraryFilterPopup.IsOpen && LibraryFilterPopup.Child is FrameworkElement child)
            {
                child.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>
    /// Closes the flyout before the help window opens over it. Click runs ahead of the button's
    /// Command, which is what actually shows the topic.
    /// </summary>
    private void LibraryFilterHelp_Click(object sender, RoutedEventArgs e) =>
        ViewModel.Library.Filter.IsOpen = false;

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        (Window.GetWindow(this) as MainWindow)?.HandleDragOverFromPage(e);
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            DropZoneBorder.BorderBrush = (Brush)FindResource("BrushAccent");
            DropZoneBorder.Background = new SolidColorBrush(Color.FromArgb(50, 0, 122, 204));
        }
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        DropZoneBorder.BorderBrush = (Brush)FindResource("BrushBorderDark");
        DropZoneBorder.Background = new SolidColorBrush(Color.FromRgb(22, 22, 25));
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropZone_DragLeave(sender, e);
        (Window.GetWindow(this) as MainWindow)?.HandleDropFromPage(e);

        // Drop is a bubbling routed event. Without this, the same drop continues bubbling
        // past this element up to the Window's own separate Drop="Window_Drop" handler,
        // running HandleFileDrop a second time for the one physical drop (which is what
        // produced the "processed the folder twice" symptom).
        e.Handled = true;
    }

    /// <summary>
    /// The category strip only scrolls horizontally (vertical is disabled), but a mouse wheel
    /// by default only ever raises vertical scroll requests - so without this, hovering the
    /// category tabs and scrolling does nothing. Redirects the wheel delta to a horizontal scroll.
    /// </summary>
    private void CategoryScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scrollViewer = (ScrollViewer)sender;
        scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>
    /// An undo toast is held while the pointer is over it or keyboard focus is inside it, and its
    /// 10-second window pauses. Also re-read when the toast shows or hides: a hidden toast can keep
    /// keyboard focus (Undo pressed with Enter), which must not leave the next toast's window paused.
    /// </summary>
    private void OnUndoToastHoldChanged(object sender, RoutedEventArgs e) => UpdateUndoToastHold(sender);

    private void OnUndoToastFocusChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateUndoToastHold(sender);

    private void UpdateUndoToastHold(object sender)
    {
        if (sender is not FrameworkElement toast) return;
        ViewModel.Library.SetUndoToastHeld(toast.IsVisible && (toast.IsMouseOver || toast.IsKeyboardFocusWithin));
    }

    /// <summary>The selection bar's More: the batch right-click menu, opened above the bar.</summary>
    private void SelectionBarMore_Click(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)FindResource("GameBatchContextMenu");
        menu.DataContext = ViewModel;
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    /// <summary>
    /// Explorer-style right-click on a card: if the card is one of two or more selected cards,
    /// open the batch menu for the whole selection instead of the single-game menu; if it is
    /// not selected, the selection is dropped first and the normal menu opens for that card.
    /// </summary>
    private void OnCardContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not GameCardViewModel card) return;

        // A previous card is cleared first in case a menu closed without raising Closed.
        if (_contextMenuCard != null) _contextMenuCard.IsContextMenuOpen = false;
        _contextMenuCard = null;

        bool batch = card.IsSelected && ViewModel.SelectedCount >= 2;
        if (!batch)
        {
            // Keep the card lit while its menu is open (see GameCardViewModel.IsContextMenuOpen).
            // Not for the batch menu: the selection outline already marks every game it applies
            // to, and zooming the one under the cursor would read as if the menu were about it alone.
            _contextMenuCard = card;
            card.IsContextMenuOpen = true;
        }

        if (!card.IsSelected)
        {
            ViewModel.ClearSelection();
            return;
        }
        if (!batch) return;

        e.Handled = true;
        var menu = (ContextMenu)FindResource("GameBatchContextMenu");
        menu.DataContext = ViewModel;
        menu.PlacementTarget = element;
        // The selection bar's More opens the same menu above itself; a right-click opens it here.
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>The card whose right-click menu is open, if any - see <see cref="OnCardContextMenuOpening"/>.</summary>
    private GameCardViewModel? _contextMenuCard;

    private void OnCardContextMenuClosed(object sender, RoutedEventArgs e)
    {
        if (_contextMenuCard == null) return;
        _contextMenuCard.IsContextMenuOpen = false;
        _contextMenuCard = null;
    }
}
