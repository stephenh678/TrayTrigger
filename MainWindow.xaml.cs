using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;
using TrayTrigger.Views;

namespace TrayTrigger;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _isExplicitExit = false;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        try
        {
            var iconUri = new Uri("pack://application:,,,/TrayTrigger;component/Assets/app_icon.ico", UriKind.RelativeOrAbsolute);
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(iconUri);
        }
        catch { }

        // Dark title bar is applied at SourceInitialized and the window stays cloaked
        // until its first frame renders, so the user never sees an unpainted white frame.
        WindowThemeService.PrepareForFirstShow(this);

        RestoreWindowPlacement();

        SourceInitialized += (s, e) =>
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            LoggingService.Verbose("MainWindow", $"SourceInitialized. HWnd={handle}");
        };

        // Track placement continuously (in memory only) so whichever save runs last - the hide
        // in OnClosing, the tray "Exit", or the ProcessExit fallback - persists the final size.
        SizeChanged += (s, e) => CaptureWindowPlacement();
        LocationChanged += (s, e) => CaptureWindowPlacement();
        StateChanged += (s, e) => CaptureWindowPlacement();

        Loaded += (s, e) =>
        {
            LoggingService.Verbose("MainWindow", "Loaded.");
            MaybeShowWelcomePrompt();
            MaybeShowPerformanceProfileMigrationPrompt();
        };

        _viewModel.RequestScanResultsPicker += OnRequestScanResultsPicker;
        _viewModel.RequestLauncherDetectionPrompt += OnRequestLauncherDetectionPrompt;
        _viewModel.RequestEditGameDialog += OnRequestEditGameDialog;
        _viewModel.RequestCandidatePicker += OnRequestCandidatePicker;
        _viewModel.RequestFolderBatchImport += OnRequestFolderBatchImport;
        _viewModel.RequestQuickRename += OnRequestQuickRename;
        _viewModel.RequestQuickCategory += OnRequestQuickCategory;
        _viewModel.RequestBatchCategory += OnRequestBatchCategory;
        _viewModel.RequestEditSteamAppId += OnRequestEditSteamAppId;
        _viewModel.RequestMinimizeToTray += OnRequestMinimizeToTray;

        Closed += (s, e) =>
        {
            _viewModel.RequestScanResultsPicker -= OnRequestScanResultsPicker;
            _viewModel.RequestLauncherDetectionPrompt -= OnRequestLauncherDetectionPrompt;
            _viewModel.RequestEditGameDialog -= OnRequestEditGameDialog;
            _viewModel.RequestCandidatePicker -= OnRequestCandidatePicker;
            _viewModel.RequestFolderBatchImport -= OnRequestFolderBatchImport;
            _viewModel.RequestQuickRename -= OnRequestQuickRename;
            _viewModel.RequestQuickCategory -= OnRequestQuickCategory;
            _viewModel.RequestBatchCategory -= OnRequestBatchCategory;
            _viewModel.RequestEditSteamAppId -= OnRequestEditSteamAppId;
            _viewModel.RequestMinimizeToTray -= OnRequestMinimizeToTray;
        };
    }

    public void MarkExplicitExit()
    {
        _isExplicitExit = true;
    }

    /// <summary>
    /// Apply the placement saved by <see cref="CaptureWindowPlacement"/> before the window is
    /// first shown. Falls back to the XAML default size and CenterScreen when nothing has been
    /// saved yet, or when the saved rectangle no longer touches any monitor (a display was
    /// unplugged or the resolution changed), so the window can never come back off-screen.
    /// </summary>
    private void RestoreWindowPlacement()
    {
        var settings = _viewModel.SettingsVM.Settings;
        if (settings.MainWindowWidth is not double width || settings.MainWindowHeight is not double height ||
            settings.MainWindowLeft is not double left || settings.MainWindowTop is not double top)
        {
            return;
        }

        if (double.IsNaN(width) || double.IsNaN(height) || double.IsNaN(left) || double.IsNaN(top) ||
            width < MinWidth || height < MinHeight)
        {
            LoggingService.Warn("MainWindow", $"Ignoring invalid saved window placement {width}x{height} at ({left},{top}).");
            return;
        }

        // Require a usable slice of the window (enough to grab the title bar) to be inside the
        // virtual desktop; otherwise let CenterScreen place it.
        const double minVisible = 120;
        var saved = new Rect(left, top, width, height);
        var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                               SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var visible = Rect.Intersect(saved, desktop);
        if (visible.IsEmpty || visible.Width < minVisible || visible.Height < minVisible)
        {
            LoggingService.Info("MainWindow", $"Saved window placement {width}x{height} at ({left},{top}) is off-screen; using the default placement.");
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        if (settings.MainWindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
        LoggingService.Verbose("MainWindow", $"Restored window placement {width}x{height} at ({left},{top}), maximized={settings.MainWindowMaximized}.");
    }

    /// <summary>
    /// Copy the current placement into settings (no disk write). Only the Normal-state rectangle
    /// is recorded: a maximized or minimized window's Left/Top/Width/Height describe the
    /// maximized frame or an off-screen point, and RestoreBounds already holds the last Normal
    /// rectangle for those cases.
    /// </summary>
    private void CaptureWindowPlacement()
    {
        if (!IsLoaded) return;
        var settings = _viewModel.SettingsVM.Settings;

        if (WindowState == WindowState.Normal)
        {
            settings.MainWindowLeft = Left;
            settings.MainWindowTop = Top;
            settings.MainWindowWidth = ActualWidth;
            settings.MainWindowHeight = ActualHeight;
        }
        else if (WindowState == WindowState.Maximized && !RestoreBounds.IsEmpty)
        {
            settings.MainWindowLeft = RestoreBounds.Left;
            settings.MainWindowTop = RestoreBounds.Top;
            settings.MainWindowWidth = RestoreBounds.Width;
            settings.MainWindowHeight = RestoreBounds.Height;
        }

        // Minimized is transient (the window is about to be hidden or restored), so keep
        // whichever of Normal/Maximized it was minimized from.
        if (WindowState != WindowState.Minimized)
        {
            settings.MainWindowMaximized = WindowState == WindowState.Maximized;
        }
    }

    /// <summary>
    /// The first time the user ever presses "Scan for Games" (see
    /// ImportCoordinator.ScanForGamesAsync), and only if at least one platform's own scanner
    /// found an installed game: lets the user confirm which of them TrayTrigger should manage,
    /// then reports the choice back so the real scan can run - or does nothing further if they
    /// close/skip the dialog, since HasSeenLauncherDetectionPrompt is already marked seen either
    /// way and every later press just scans normally.
    /// </summary>
    private void OnRequestLauncherDetectionPrompt(List<DetectedLauncherOption> detected)
    {
        var dialog = new LauncherDetectionDialog(detected) { Owner = this };
        bool? shown = dialog.ShowDialog();

        if (shown == true && dialog.Confirmed)
        {
            // Applied through SettingsVM (not ImportCoordinator) so the toggles' own setters
            // fire property-changed and Settings > Library's checkboxes don't show a stale
            // value - see ImportCoordinator.CompleteFirstTimeLauncherDetection's doc comment.
            // Confirming with nothing ticked is honoured too (every probed launcher off), and
            // reported, instead of being treated as a silent skip.
            _viewModel.SettingsVM.ApplyDetectedLauncherChoices(dialog.EnabledLaunchers, _viewModel.Import.LastProbedLaunchers);
            _viewModel.Import.CompleteFirstTimeLauncherDetection(anyLauncherEnabled: dialog.EnabledLaunchers.Count > 0);
        }
        else
        {
            _viewModel.Import.SkipFirstTimeLauncherDetection();
        }
    }

    /// <summary>
    /// Shows the one-time "Welcome to TrayTrigger" dialog, first among the one-time prompts here
    /// so a brand-new user sees it before anything else. Gated like the other one-time prompts
    /// below - the first time the window is actually shown, not on every process start.
    /// </summary>
    private void MaybeShowWelcomePrompt()
    {
        var settingsVm = _viewModel.SettingsVM;
        if (settingsVm.Settings.HasSeenWelcomePrompt)
            return;

        settingsVm.Settings.HasSeenWelcomePrompt = true;
        settingsVm.AutoSaveSettings();

        var welcome = new WelcomeDialog { Owner = this };
        welcome.ShowDialog();

        // "Scan for Games" straight from the welcome: the single most useful first step for
        // anyone with a launcher installed, so it shouldn't need a second hunt for the button.
        if (welcome.ScanRequested)
        {
            _viewModel.OpenScanForGamesCommand.Execute(null);
        }
    }

    /// <summary>
    /// One-time prompt (gated like <see cref="MaybeShowWelcomePrompt"/>) offering to bulk-set
    /// every existing game to the Optimized performance profile - the new recommended default
    /// applied automatically to any game added from here on. Only relevant to users upgrading
    /// from before this feature existed; skipped entirely if no game is still sitting at Off
    /// (a fresh install has no games yet, and every game added since gets Optimized already).
    /// </summary>
    private void MaybeShowPerformanceProfileMigrationPrompt()
    {
        var settingsVm = _viewModel.SettingsVM;
        if (settingsVm.Settings.HasSeenPerformanceProfileMigrationPrompt)
            return;

        bool hasUnmigratedGame = _viewModel.Games.Any(g => g.Game.PerformanceProfile == PerformanceProfileMode.Off);
        if (!hasUnmigratedGame)
            return;

        settingsVm.Settings.HasSeenPerformanceProfileMigrationPrompt = true;
        settingsVm.AutoSaveSettings();

        bool setAllToOptimized = ModernDialog.PromptOptimizedProfileMigration(this);
        if (setAllToOptimized)
        {
            int migratedCount = 0;
            foreach (var card in _viewModel.Games)
            {
                if (card.Game.PerformanceProfile == PerformanceProfileMode.Off)
                {
                    card.Game.PerformanceProfile = PerformanceProfileMode.Optimized;
                    migratedCount++;
                }
            }
            LoggingService.Info("GameEdit", $"Performance Profile migration prompt: switched {migratedCount} game(s) from Off to Optimized.");
            _viewModel.SaveLibrary();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isExplicitExit)
        {
            e.Cancel = true;
            CaptureWindowPlacement();
            Hide();
            _viewModel.SettingsVM.AutoSaveSettings();

            // First hide via the title-bar X: tell the user the app is still running, once.
            // Without this the X looked like an exit and the tray icon went unnoticed.
            var settings = _viewModel.SettingsVM.Settings;
            if (!settings.HasSeenTrayHideNotice)
            {
                settings.HasSeenTrayHideNotice = true;
                _viewModel.NotifyTray("TrayTrigger is still running",
                    "Your hotkeys and tray menu stay active. Left-click the tray icon to reopen, or right-click it to exit.");
            }
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// Library-only shortcuts documented in the About page's Quick Reference: Ctrl+F jumps focus
    /// to the search box, Escape clears an active search filter. Both are no-ops outside the
    /// Library section so they don't steal keystrokes while e.g. editing a Settings text field.
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel.CurrentSection != NavSection.Library) return;

        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            LibrarySearchTextBox.Focus();
            LibrarySearchTextBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control &&
                 Keyboard.FocusedElement is not System.Windows.Controls.TextBox)
        {
            // Select every visible card. Inside a text box Ctrl+A keeps its normal
            // select-all-text meaning.
            _viewModel.SelectAllVisible();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None && _viewModel.HasSelection &&
                 Keyboard.FocusedElement is not System.Windows.Controls.TextBox)
        {
            // Same path as the batch menu's Remove from Library: one confirmation for the
            // whole selection, undoable for six seconds afterwards.
            _viewModel.BatchRemoveCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _viewModel.HasSelection)
        {
            _viewModel.ClearSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !string.IsNullOrEmpty(_viewModel.SearchText))
        {
            _viewModel.SearchText = string.Empty;
            e.Handled = true;
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        Activate();
        Focus();
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0)
            {
                // Defer to a fresh dispatcher cycle: this Drop handler still runs inside the
                // OS drag-and-drop operation's own nested message loop, and HandleFileDrop can
                // open a modal dialog (folder batch import / candidate picker) for multi-game
                // folders. Showing a modal dialog from inside that nested loop is a known WPF
                // reentrancy hazard - the dialog's state can end up correct while it visually
                // fails to close/repaint. Running it after the drop operation unwinds avoids that.
                Dispatcher.BeginInvoke(new Action(() => _viewModel.HandleFileDrop(files)),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        Window_DragOver(sender, e);
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
        Window_Drop(sender, e);

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
    private void CategoryScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        var scrollViewer = (System.Windows.Controls.ScrollViewer)sender;
        scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void OnRequestScanResultsPicker(List<DiscoveredSteamGame> steamGames, List<DiscoveredGogGame> gogGames, List<DiscoveredEaGame> eaGames, List<DiscoveredEpicGame> epicGames, List<DiscoveredUbisoftGame> ubisoftGames, List<DiscoveredXboxGame> xboxGames, List<GameCandidate> folderCandidates)
    {
        var dialog = new ScanForGamesDialog(_viewModel, steamGames, gogGames, eaGames, epicGames, ubisoftGames, xboxGames, folderCandidates);
        dialog.Owner = this;
        dialog.ShowDialog();
    }

    private void OnRequestMinimizeToTray()
    {
        CaptureWindowPlacement();
        Hide();
        _viewModel.SettingsVM.AutoSaveSettings();
    }



    private void OnRequestEditGameDialog(GameCardViewModel card)
    {
        var editDialog = new GameEditDialog(
            card.Game, 
            _viewModel.Categories, 
            _viewModel.IconExtractorService, 
            steamGridDbApiKey: _viewModel.SettingsVM.SteamGridDbApiKeyOrNull,
            minConfidence: _viewModel.Settings.OnlineMatchConfidenceThreshold,
            scriptsEnabled: _viewModel.Settings.EnableGameScripts);
        editDialog.Owner = this;
        if (editDialog.ShowDialog() == true)
        {
            card.RefreshProperties();
            _viewModel.RebuildCategories();
            _viewModel.SaveLibrary();
            _viewModel.UpdateHotkeys();
            _viewModel.FilteredGames.Refresh();
        }
    }

    private void OnRequestCandidatePicker(string folderPath, List<GameCandidate> candidates)
    {
        var picker = new GameCandidatePickerDialog(folderPath, candidates);
        picker.Owner = this;
        if (picker.ShowDialog() == true && picker.SelectedCandidate != null)
        {
            _viewModel.AddCandidate(picker.SelectedCandidate);
        }
    }

    private void OnRequestFolderBatchImport(string folderPath, List<GameCandidate> candidates)
    {
        var existingPaths = new HashSet<string>(_viewModel.Games.Select(g => g.Game.ExecutablePath), StringComparer.OrdinalIgnoreCase);
        bool isAlreadyScanLocation = _viewModel.IsScanLocation(folderPath);
        // "Already in library" by exe path for a local candidate, by platform ID for one that
        // resolved to a launcher game (a Steam entry's ExecutablePath is a steam:// URL, so the
        // path comparison alone would pre-select every already-imported Steam game).
        var dialog = new FolderBatchImportDialog(folderPath, candidates,
            isAlreadyImported: c => existingPaths.Contains(c.ExePath) || _viewModel.Library.IsPlatformGameInLibrary(c.Platform),
            isAlreadyScanLocation,
            onIgnoreCandidate: _viewModel.IgnoreCandidate);
        dialog.Owner = this;
        if (dialog.ShowDialog() == true && dialog.SelectedGames != null && dialog.SelectedGames.Count > 0)
        {
            _viewModel.ImportBatchGames(dialog.SelectedGames);
            if (dialog.RememberAsScanLocation)
            {
                _viewModel.AddManualScanLocationIfNew(folderPath);
            }
        }
    }

    private void OnRequestQuickRename(GameCardViewModel card)
    {
        var dialog = new QuickInputDialog("Rename Game", "Rename Game", $"Enter a new title for \"{card.Name}\":", card.Name);
        dialog.Owner = this;
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ResultValue))
        {
            _viewModel.ApplyRename(card, dialog.ResultValue);
        }
    }

    private void OnRequestQuickCategory(GameCardViewModel card)
    {
        var suggestions = _viewModel.Categories.Where(c => c != "All");
        var dialog = new QuickInputDialog("Change Category", "Change Category", $"Select or enter a category for \"{card.Name}\":", card.Category, suggestions);
        dialog.Owner = this;
        if (dialog.ShowDialog() == true)
        {
            _viewModel.ApplyCategory(card, dialog.ResultValue);
        }
    }

    /// <summary>
    /// Explorer-style click-away: a mouse press that lands outside every game card (empty
    /// library space, headers, the toolbar, the sidebar) drops the multi-selection. This is the
    /// tunneling event on purpose: the library's ScrollViewer marks the bubbling MouseDown
    /// handled when it takes focus, so a press on the blank space between cards would never
    /// reach a Window.MouseDown handler. Presses on a card never clear here - its own input
    /// bindings decide (plain click clears and opens Details, Ctrl/Shift+click toggle), and a
    /// right-click on a selected card must keep the selection so
    /// <see cref="OnCardContextMenuOpening"/> can offer the batch menu for it. A press inside a
    /// text box (the search field) keeps the selection too, as Explorer does.
    /// </summary>
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.HasSelection) return;
        var source = e.OriginalSource as DependencyObject;
        if (IsWithin(source, static fe => fe.DataContext is GameCardViewModel)) return;
        if (IsWithin(source, static fe => fe is System.Windows.Controls.TextBox)) return;
        _viewModel.ClearSelection();
    }

    private static bool IsWithin(DependencyObject? node, Func<FrameworkElement, bool> predicate)
    {
        while (node != null)
        {
            if (node is FrameworkElement fe && predicate(fe)) return true;
            node = node is Visual || node is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>
    /// Explorer-style right-click on a card: if the card is one of two or more selected cards,
    /// open the batch menu for the whole selection instead of the single-game menu; if it is
    /// not selected, the selection is dropped first and the normal menu opens for that card.
    /// </summary>
    private void OnCardContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not GameCardViewModel card) return;

        if (!card.IsSelected)
        {
            _viewModel.ClearSelection();
            return;
        }
        if (_viewModel.SelectedCount < 2) return;

        e.Handled = true;
        var menu = (System.Windows.Controls.ContextMenu)FindResource("GameBatchContextMenu");
        menu.DataContext = _viewModel;
        menu.PlacementTarget = element;
        menu.IsOpen = true;
    }

    private void OnRequestBatchCategory(List<GameCardViewModel> cards)
    {
        var suggestions = _viewModel.Categories.Where(c => c != "All");
        // Pre-fill only when every selected game already shares one category.
        string initial = cards.Select(c => c.Category).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? cards[0].Category : string.Empty;
        string prompt = cards.Count == 1
            ? $"Select or enter a category for \"{cards[0].Name}\":"
            : $"Select or enter a category for the {cards.Count} selected games:";
        var dialog = new QuickInputDialog("Change Category", "Change Category", prompt, initial, suggestions);
        dialog.Owner = this;
        if (dialog.ShowDialog() == true)
        {
            _viewModel.ApplyCategoryToMany(cards, dialog.ResultValue);
        }
    }

    private void OnRequestEditSteamAppId(GameCardViewModel card)
    {
        var dialog = new QuickInputDialog(
            "Link Steam App ID",
            "Link Steam App ID",
            $"Enter numeric Steam App ID for \"{card.Name}\":\n(Found in store.steampowered.com/app/<id>/ - does not require Steam to run)",
            card.Game.SteamAppId ?? "");
        dialog.Owner = this;
        if (dialog.ShowDialog() == true)
        {
            _ = _viewModel.UpdateGameSteamAppIdAsync(card, dialog.ResultValue);
        }
    }

    /// <summary>Blocks non-digit keystrokes on tray "max items" TextBoxes (e.g. MaxRecentInTray).</summary>
    private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    /// <summary>Blocks pasting non-numeric text into tray "max items" TextBoxes.</summary>
    private void NumericTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(typeof(string)) && ((string)e.DataObject.GetData(typeof(string))!).All(char.IsDigit))
        {
            return;
        }
        e.CancelCommand();
    }
}