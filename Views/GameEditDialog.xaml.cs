using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

public partial class GameEditDialog : Window
{
    private readonly GameEditViewModel _viewModel;

    public GameEditDialog(
        GameEntry game, 
        IEnumerable<string> categories, 
        IconExtractorService iconExtractorService, 
        bool isNewGame = false, 
        string? steamGridDbApiKey = null,
        double minConfidence = SteamSearchService.DefaultMinConfidence,
        bool scriptsEnabled = false,
        ScriptDefaults? scriptDefaults = null,
        ScriptLibraryService? scriptLibrary = null,
        Func<PerformanceProfileMode, IReadOnlyList<ProfileTweakToggleViewModel>>? profileTweaks = null)
    {
        InitializeComponent();
        WindowThemeService.PrepareForFirstShow(this);
        WindowHelper.RemoveMinimizeAndMaximize(this);
        _viewModel = new GameEditViewModel(game, categories, iconExtractorService, isNewGame, steamGridDbApiKey, minConfidence, scriptsEnabled, scriptDefaults, scriptLibrary, profileTweaks);
        DataContext = _viewModel;

        Owner = WindowHelper.ActiveOwner();

        // Esc cancels through the Cancel button (IsCancel), but not over unsaved edits. This
        // listens on the bubbling KeyDown, so an Esc a child already used - the hotkey recorder
        // while it is recording, a combo box closing its drop-down - never gets here.
        KeyDown += (s, e) =>
        {
            if (e.Handled) return;

            // Ctrl+Tab / Ctrl+Shift+Tab step through the tabs, as in a browser. Also bubbling, so a
            // recording hotkey box still gets the combination.
            if (e.Key == Key.Tab && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                _viewModel.MoveSection(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Escape || !_viewModel.HasUnsavedChanges) return;
            e.Handled = true;
            if (ModernDialog.Confirm(this, "Discard Changes", "Discard your changes?",
                    "Nothing you changed here has been saved.", "Discard", "Keep Editing"))
            {
                _viewModel.CancelCommand.Execute(null);
            }
        };

        // Save refused a value: the view model has already switched to a tab that shows it, so
        // scroll the field into view and put the cursor in it. The message is in the footer.
        _viewModel.ValidationFailed += field =>
        {
            var box = field switch
            {
                GameEditViewModel.EditField.SteamAppId => SteamAppIdBox,
                GameEditViewModel.EditField.PreLaunchScript => PreLaunchScriptBox,
                GameEditViewModel.EditField.PostExitScript => PostExitScriptBox,
                _ => PreLaunchTimeoutBox,
            };
            Dispatcher.BeginInvoke(() =>
            {
                box.BringIntoView();
                box.Focus();
                box.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        };

        Loaded += (s, e) =>
        {
            WindowThemeService.CenterOverOwner(this);
            Activate();
            // The DLSS probe reads the driver and the game folder, so it runs here rather than in
            // the view model's constructor: the dialog opens immediately and the card appears when
            // there is something true to put in it.
            _ = _viewModel.Dlss.LoadAsync();
        };

        _viewModel.ScriptTestCompleted += report =>
        {
            var dialog = new ScriptTestResultDialog(report) { Owner = this };
            dialog.ShowDialog();
        };

        _viewModel.RequestClose += success =>
        {
            DialogResult = success;
            Close();
        };
    }
}
