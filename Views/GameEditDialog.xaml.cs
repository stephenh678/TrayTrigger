using System.Collections.Generic;
using System.Windows;
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
        ScriptDefaults? scriptDefaults = null)
    {
        InitializeComponent();
        WindowThemeService.PrepareForFirstShow(this);
        _viewModel = new GameEditViewModel(game, categories, iconExtractorService, isNewGame, steamGridDbApiKey, minConfidence, scriptsEnabled, scriptDefaults);
        DataContext = _viewModel;

        Owner = WindowHelper.ActiveOwner();

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
