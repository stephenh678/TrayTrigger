using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
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

        SourceInitialized += (s, e) =>
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            WindowThemeService.ApplyDarkTitleBar(this);
            LoggingService.Verbose("MainWindow", $"SourceInitialized. HWnd={handle}");
        };

        Loaded += (s, e) =>
        {
            LoggingService.Verbose("MainWindow", "Loaded.");
            MaybeShowSteamGridDbPrompt();
        };

        _viewModel.RequestOpenSteamDialog += OnRequestOpenSteamDialog;
        _viewModel.RequestEditGameDialog += OnRequestEditGameDialog;
        _viewModel.RequestCandidatePicker += OnRequestCandidatePicker;
        _viewModel.RequestFolderBatchImport += OnRequestFolderBatchImport;
        _viewModel.RequestQuickRename += OnRequestQuickRename;
        _viewModel.RequestQuickCategory += OnRequestQuickCategory;
        _viewModel.RequestEditSteamAppId += OnRequestEditSteamAppId;
        _viewModel.RequestMinimizeToTray += OnRequestMinimizeToTray;

        Closed += (s, e) =>
        {
            _viewModel.RequestOpenSteamDialog -= OnRequestOpenSteamDialog;
            _viewModel.RequestEditGameDialog -= OnRequestEditGameDialog;
            _viewModel.RequestCandidatePicker -= OnRequestCandidatePicker;
            _viewModel.RequestFolderBatchImport -= OnRequestFolderBatchImport;
            _viewModel.RequestQuickRename -= OnRequestQuickRename;
            _viewModel.RequestQuickCategory -= OnRequestQuickCategory;
            _viewModel.RequestEditSteamAppId -= OnRequestEditSteamAppId;
            _viewModel.RequestMinimizeToTray -= OnRequestMinimizeToTray;
        };
    }

    public void MarkExplicitExit()
    {
        _isExplicitExit = true;
    }

    /// <summary>
    /// Shows a one-time reminder recommending SteamGridDB setup for better poster art. Gated by
    /// a persisted flag so it only ever appears once, the first time the window is actually
    /// shown (not on every process start - a --minimized launch skips Show() entirely, so this
    /// naturally defers to the next time the user actually opens the window instead of being
    /// lost). Skipped entirely if SteamGridDB is already enabled.
    /// </summary>
    private void MaybeShowSteamGridDbPrompt()
    {
        var settingsVm = _viewModel.SettingsVM;
        if (settingsVm.Settings.HasSeenSteamGridDbPrompt || settingsVm.UseSteamGridDbArt)
            return;

        settingsVm.Settings.HasSeenSteamGridDbPrompt = true;
        settingsVm.AutoSaveSettings();

        bool setUpNow = ModernDialog.PromptSteamGridDbSetup(this);
        if (setUpNow)
        {
            _viewModel.CurrentSection = NavSection.Settings;
            settingsVm.SelectedTab = SettingsCategoryTab.Library;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isExplicitExit)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
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

    private void OnRequestOpenSteamDialog()
    {
        var steamDialog = new SteamImportDialog(_viewModel);
        steamDialog.Owner = this;
        steamDialog.ShowDialog();
    }

    private void OnRequestMinimizeToTray()
    {
        Hide();
    }



    private void OnRequestEditGameDialog(GameCardViewModel card)
    {
        var editDialog = new GameEditDialog(
            card.Game, 
            _viewModel.Categories, 
            _viewModel.IconExtractorService, 
            steamGridDbApiKey: _viewModel.SteamGridDbApiKeyOrNull,
            minConfidence: _viewModel.Settings.OnlineMatchConfidenceThreshold);
        editDialog.Owner = this;
        if (editDialog.ShowDialog() == true)
        {
            card.RefreshProperties();
            _viewModel.RebuildCategories();
            _viewModel.SaveLibrary();
            _viewModel.UpdateHotkeys();
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
        var existingPaths = _viewModel.Games.Select(g => g.Game.ExecutablePath);
        var dialog = new FolderBatchImportDialog(folderPath, candidates, existingPaths);
        dialog.Owner = this;
        if (dialog.ShowDialog() == true && dialog.SelectedGames != null && dialog.SelectedGames.Count > 0)
        {
            _viewModel.ImportBatchGames(dialog.SelectedGames);
        }
    }

    private void OnRequestQuickRename(GameCardViewModel card)
    {
        var dialog = new QuickInputDialog("Rename Game", "Quick Rename", $"Enter a new title for \"{card.Name}\":", card.Name);
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

    private void OnRequestEditSteamAppId(GameCardViewModel card)
    {
        var dialog = new QuickInputDialog(
            "Link Steam App ID",
            "Steam Store & Artwork Match",
            $"Enter numeric Steam App ID for \"{card.Name}\":\n(Found in store.steampowered.com/app/<id>/ - does not require Steam to run)",
            card.Game.SteamAppId ?? "");
        dialog.Owner = this;
        if (dialog.ShowDialog() == true)
        {
            _ = _viewModel.UpdateGameSteamAppIdAsync(card, dialog.ResultValue);
        }
    }
}