using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

public partial class ToolsView : UserControl
{
    public ToolsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private ToolsViewModel? ViewModel => DataContext as ToolsViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ToolsViewModel oldViewModel) oldViewModel.SelectionClearRequested -= ClearSelection;
        if (e.NewValue is ToolsViewModel newViewModel) newViewModel.SelectionClearRequested += ClearSelection;
    }

    /// <summary>Drops every selected tool: a click away from the tools, a search or a tab change.</summary>
    public void ClearSelection()
    {
        if (ToolsList.SelectedItems.Count > 0) ToolsList.UnselectAll();
    }

    /// <summary>
    /// Every tool the current tab and search leave showing, for Ctrl+A. Driven from MainWindow's
    /// tunnelling window handler rather than this page's own KeyDown, as the library's is: a press
    /// on blank space leaves keyboard focus outside the page, and a bubbling handler here only ever
    /// sees keys that start inside it - which is why Ctrl+A used to need a tool clicked first.
    /// </summary>
    public void SelectAllTools() => ToolsList.SelectAll();

    /// <summary>Puts the caret in the tools search box and selects what is there (Ctrl+F).</summary>
    public void FocusSearchBox()
    {
        ToolsSearchTextBox.Focus();
        ToolsSearchTextBox.SelectAll();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(ShellAppResolver.IdListFormat)
            ? DropEffectFor(e.AllowedEffects)
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// Copy when the source allows it, else Link. An app dragged from shell:AppsFolder only allows Link
    /// (there is no file to copy, so the desktop makes a shortcut of it), and asking for Copy there makes
    /// Windows refuse the drop before it arrives. Adding a tool changes nothing at the source either way.
    /// </summary>
    internal static DragDropEffects DropEffectFor(DragDropEffects allowed) =>
        allowed.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy
        : allowed.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link
        : DragDropEffects.None;

    /// <summary>
    /// Handled here so the drop never bubbles to the window's game import. Deferred to a fresh
    /// dispatcher cycle for the same reason as MainWindow.Window_Drop: a dialog opened inside the
    /// OS drag loop can fail to repaint or close. Files are used when the drop has them; apps dragged
    /// from shell:AppsFolder come with no file path, only shell items, which are read before the
    /// deferral because the drag's data isn't readable once it ends.
    /// </summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (ViewModel is not { } viewModel) return;

        // A source can offer file names that aren't on disk alongside its shell items; those go to the shell items.
        string[]? files = ReadDroppedFiles(e.Data);
        if (files != null && files.Any(f => File.Exists(f) || Directory.Exists(f)))
        {
            Dispatcher.BeginInvoke(new Action(() => viewModel.HandleDrop(files)), DispatcherPriority.Background);
            return;
        }

        var apps = ReadDroppedShellItems(e.Data);
        if (apps.Count > 0)
        {
            Dispatcher.BeginInvoke(new Action(() => viewModel.HandleShellDrop(apps)), DispatcherPriority.Background);
        }
    }

    private static string[]? ReadDroppedFiles(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)) return null;
        try
        {
            return data.GetData(DataFormats.FileDrop) as string[];
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Tools", $"Could not read the dropped files: {ex.Message}");
            return null;
        }
    }

    private static List<ShellApp> ReadDroppedShellItems(IDataObject data)
    {
        if (!data.GetDataPresent(ShellAppResolver.IdListFormat)) return [];
        try
        {
            return data.GetData(ShellAppResolver.IdListFormat) is MemoryStream stream
                ? ShellAppResolver.FromIdListArray(stream.ToArray())
                : [];
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Tools", $"Could not read the dropped apps: {ex.Message}");
            return [];
        }
    }

    /// <summary>The list's selection (Ctrl+click, Shift+click, Ctrl+A) is what the batch menu edits.</summary>
    private void OnToolsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel?.SetSelection(ToolsList.SelectedItems.OfType<ToolCardViewModel>());
    }

    /// <summary>
    /// Right-clicking a tool that is part of a multi-selection opens the batch menu for the whole
    /// selection, as in the Library; anything else opens the tool's own menu.
    /// </summary>
    private void OnCardContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ToolCardViewModel card || ViewModel is not { } viewModel) return;

        bool batch = viewModel.HasMultipleSelection && viewModel.SelectedTools.Contains(card);
        var menu = (ContextMenu)Resources[batch ? "ToolBatchContextMenu" : "ToolItemContextMenu"];
        menu.DataContext = batch ? viewModel : card;
        menu.PlacementTarget = element;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>The selection bar's More: the batch right-click menu, opened above the bar.</summary>
    private void OnSelectionBarMore(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;
        var menu = (ContextMenu)Resources["ToolBatchContextMenu"];
        menu.DataContext = viewModel;
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void OnSelectionBarClear(object sender, RoutedEventArgs e) => ClearSelection();

    /// <summary>
    /// The two keys that need the list itself focused, bubbling so a focused control that uses the
    /// key (a button taking Enter, an open dropdown taking Esc) gets it first: Enter or Ctrl+Enter
    /// launches, F2 renames, each when exactly one tool is selected. The library's Enter works the
    /// same way, through its ListBox InputBindings.
    ///
    /// <para>Ctrl+F, Ctrl+A, Delete and Escape are not here. They are the library's page-wide keys
    /// and are handled beside it in MainWindow.Window_PreviewKeyDown, which sees the key wherever
    /// focus is - a bubbling handler on this page misses every press made after a click on blank
    /// space, which is how Ctrl+A came to need a tool clicked first.</para>
    /// </summary>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;
        if (Keyboard.FocusedElement is TextBox) return;

        var selected = viewModel.SelectedTools;
        if (selected.Count != 1 || !ToolsList.IsKeyboardFocusWithin) return;

        if (e.Key == Key.Enter && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Control)
        {
            viewModel.Launch(selected[0]);
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None)
        {
            viewModel.Rename(selected[0]);
            e.Handled = true;
        }
    }
}
