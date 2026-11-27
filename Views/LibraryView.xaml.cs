using System;
using System.Collections.Generic;
using TrayTrigger.Services;
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

        // Both card menus are shared resources; the card they were opened on is remembered in
        // OnCardContextMenuOpening and its highlight cleared here when the menu goes away.
        ((ContextMenu)FindResource("GameItemContextMenu")).Closed += OnCardContextMenuClosed;
        ((ContextMenu)FindResource("GameBatchContextMenu")).Closed += OnCardContextMenuClosed;
        InitializeTabRow();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    /// <summary>True while the filter flyout is open, so the window can leave Escape to it.</summary>
    public bool IsFilterFlyoutOpen => FilterFlyoutControl.IsOpen;

    /// <summary>The filter button and its count badge, for the dev diagnostics that drive them.</summary>
    internal Button LibraryFilterButton => FilterFlyoutControl.Button;
    internal Border LibraryFilterBadge => FilterFlyoutControl.Badge;

    /// <summary>Puts the caret in the library search box and selects what is there (Ctrl+F).</summary>
    public void FocusSearchBox() => LibrarySearchBox.FocusBox();

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        (Window.GetWindow(this) as MainWindow)?.HandleDragOverFromPage(e);
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            // As references, not brushes: the drop zone has to keep following a theme switch.
            DropZoneBorder.SetResourceReference(Border.BorderBrushProperty, "BrushAccent");
            DropZoneBorder.SetResourceReference(Border.BackgroundProperty, "BrushAccentWash");
        }
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        DropZoneBorder.SetResourceReference(Border.BorderBrushProperty, "BrushBorder");
        DropZoneBorder.SetResourceReference(Border.BackgroundProperty, "BrushDialogCard");
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

    private static CategoryTabItem? ChipOf(object sender)
        => (sender as MenuItem)?.Parent is ContextMenu { PlacementTarget: FrameworkElement { DataContext: CategoryTabItem tab } } ? tab : null;

    /// <summary>The "All" chip is not a collection: it has no menu.</summary>
    private void CollectionChip_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CategoryTabItem { Name.Length: 0 } }) e.Handled = true;
    }

    private void CollectionRename_Click(object sender, RoutedEventArgs e)
    {
        if (ChipOf(sender) is not { } tab) return;
        var library = ViewModel.Library;
        string initial = tab.Name;
        while (true)
        {
            var dialog = new QuickInputDialog("Rename Collection", "Rename Collection", $"New name for \"{tab.Name}\":", initial);
            dialog.Owner = Window.GetWindow(this);
            if (dialog.ShowDialog() != true) return;
            string? problem = library.RenameCollection(tab.Name, dialog.ResultValue);
            if (problem == null) return;
            ModernDialog.ShowInfo(Window.GetWindow(this), "Rename Collection", problem);
            initial = dialog.ResultValue;
        }
    }

    private void CollectionDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ChipOf(sender) is not { } tab) return;
        var tools = ViewModel.Tools.ToolsLimitedTo(tab.Name);
        string detail = "The games stay in your library; they are only taken out of this collection.";
        if (tools.Count > 0)
        {
            detail += tools.Count == 1
                ? $" {tools[0]} is limited to it and will stop starting or closing with games, unless it has another collection."
                : $" {string.Join(", ", tools)} are limited to it and will stop starting or closing with games, unless they have another collection.";
        }
        bool ok = ModernDialog.ConfirmDelete(Window.GetWindow(this), "Delete Collection",
            $"Delete the collection \"{tab.Name}\"?", detail,
            confirmText: "Delete", cancelText: "Cancel");
        if (ok) ViewModel.Library.DeleteCollection(tab.Name);
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
            // Edit Game > Performance writes the same records, so the tick is re-read here rather
            // than trusted from the last time this menu was open.
            card.NotifyDlssOverrideChanged();
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
        // The selection bar's More opens the same menu above itself; a right-click opens it at the mouse,
        // and the menu key or Shift+F10 (CursorLeft is -1) under the card.
        menu.Placement = e.CursorLeft < 0 ? PlacementMode.Bottom : PlacementMode.MousePoint;
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

    /// <summary>
    /// Arrow keys, Home, End, Page Up and Page Down move between the cards of the Poster Grid, Extra Large and
    /// Compact Icons views the way File Explorer's icon views do: Left and Right step through the list, carrying
    /// on to the next or previous row; Up and Down go to the card above or below, and Down on the row before a
    /// shorter last row goes to its last card. Only when a card (or its Play button) has focus and no modifier is
    /// held, so the scroll bar and Shift/Ctrl keys keep doing what they did. WPF's own guess at a direction got
    /// stuck at row ends and landed on the Play buttons in Compact Icons.
    /// </summary>
    private void OnCardGridKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)) return;
        if (e.OriginalSource is not DependencyObject source || CardOf(source) is not { } card) return;

        var cards = VisibleCards();
        int index = cards.IndexOf(card);
        if (index < 0) return;
        e.Handled = true;

        int target = CardGridNavigation.Target(e.Key, index, cards.Count, CardsPerRow(cards), RowsPerPage(card));
        if (target == index) return;
        cards[target].Focus();
        cards[target].BringIntoView();
    }

    /// <summary>True when focus is on a game: a card or its Play button, or a Details List row.</summary>
    internal bool IsGameFocused(DependencyObject? focused)
    {
        if (focused == null) return false;
        if (CardOf(focused) != null) return true;
        return focused is ListBoxItem item && ItemsControl.ItemsControlFromItemContainer(item) == GamesListBox;
    }

    /// <summary>
    /// Puts focus on a game in the view on screen: <paramref name="preferred"/> when it is in the view, else the
    /// selected Details List row (or the first row), or the first card.
    /// </summary>
    internal void FocusGames(GameCardViewModel? preferred = null)
    {
        if (GamesListBox.IsVisible)
        {
            if (GamesListBox.Items.Count == 0) return;
            int index = preferred != null ? GamesListBox.Items.IndexOf(preferred) : -1;
            if (index < 0) index = Math.Max(0, GamesListBox.SelectedIndex);
            GamesListBox.SelectedIndex = index;
            GamesListBox.ScrollIntoView(GamesListBox.Items[index]);
            GamesListBox.UpdateLayout();
            (GamesListBox.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem)?.Focus();
            return;
        }
        if (!CardScroller.IsVisible) return;
        var cards = VisibleCards();
        if (cards.Count == 0) return;
        var card = cards.Find(c => c.DataContext == preferred) ?? cards[0];
        card.Focus();
        card.BringIntoView();
    }

    /// <summary>The card a focused element belongs to: the card itself, or the card around its Play button.</summary>
    private static Border? CardOf(DependencyObject element)
    {
        for (var d = element; d != null && d is not ItemsControl; d = VisualTreeHelper.GetParent(d))
            if (d is Border { Name: "CardBorder" or "IconCardBorder" } border) return border;
        return null;
    }

    /// <summary>The cards of the view on screen, in the list's order (the WrapPanels don't virtualise, so every card exists).</summary>
    private List<Border> VisibleCards()
    {
        var items = PosterItems.IsVisible ? PosterItems : IconItems;
        var cards = new List<Border>(items.Items.Count);
        for (int i = 0; i < items.Items.Count; i++)
            if (items.ItemContainerGenerator.ContainerFromIndex(i) is ContentPresenter presenter
                && VisualTreeHelper.GetChildrenCount(presenter) > 0
                && VisualTreeHelper.GetChild(presenter, 0) is Border border)
                cards.Add(border);
        return cards;
    }

    /// <summary>How many cards the WrapPanel fits on its first row.</summary>
    private int CardsPerRow(List<Border> cards)
    {
        double top = cards[0].TranslatePoint(default, CardScroller).Y;
        int perRow = 1;
        while (perRow < cards.Count && Math.Abs(cards[perRow].TranslatePoint(default, CardScroller).Y - top) < 1) perRow++;
        return perRow;
    }

    /// <summary>Whole rows of cards that fit in the scroller, at least one: how far Page Up and Page Down go.</summary>
    private int RowsPerPage(Border card)
    {
        double rowHeight = card.ActualHeight + card.Margin.Top + card.Margin.Bottom;
        return rowHeight > 0 ? Math.Max(1, (int)(CardScroller.ViewportHeight / rowHeight)) : 1;
    }
}
