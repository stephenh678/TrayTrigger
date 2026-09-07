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
        double minConfidence = SteamSearchService.DefaultMinConfidence)
    {
        InitializeComponent();
        WindowThemeService.PrepareForFirstShow(this);
        _viewModel = new GameEditViewModel(game, categories, iconExtractorService, isNewGame, steamGridDbApiKey, minConfidence);
        DataContext = _viewModel;

        if (Application.Current?.MainWindow is { IsVisible: true } main)
        {
            Owner = main;
        }

        Loaded += (s, e) =>
        {
            WindowThemeService.CenterOverOwner(this);
            Activate();
        };

        _viewModel.RequestClose += success =>
        {
            DialogResult = success;
            Close();
        };
    }
}
