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
        ScriptLibraryService? scriptLibrary = null)
    {
        InitializeComponent();
        WindowThemeService.PrepareForFirstShow(this);
        WindowHelper.RemoveMinimizeAndMaximize(this);
        _viewModel = new GameEditViewModel(game, categories, iconExtractorService, isNewGame, steamGridDbApiKey, minConfidence, scriptsEnabled, scriptDefaults, scriptLibrary);
        DataContext = _viewModel;

        Owner = WindowHelper.ActiveOwner();

        // Esc cancels through the Cancel button (IsCancel), but not over unsaved edits. This
        // listens on the bubbling KeyDown, so an Esc a child already used - the hotkey recorder
        // while it is recording, a combo box closing its drop-down - never gets here.
        KeyDown += (s, e) =>
        {
            if (e.Key != Key.Escape || e.Handled || !_viewModel.HasUnsavedChanges) return;
            e.Handled = true;
            if (ModernDialog.Confirm(this, "Discard Changes", "Discard your changes?",
                    "Nothing you changed here has been saved.", "Discard", "Keep Editing"))
            {
                _viewModel.CancelCommand.Execute(null);
            }
        };

        Loaded += (s, e) =>
        {
            WindowThemeService.CenterOverOwner(this);
            Activate();
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
