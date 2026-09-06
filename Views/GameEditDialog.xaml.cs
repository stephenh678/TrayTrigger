using System.Collections.Generic;
using System.Windows;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

public partial class GameEditDialog : Window
{
    private readonly GameEditViewModel _viewModel;

    public GameEditDialog(GameEntry game, IEnumerable<string> categories, IconExtractorService iconExtractorService, bool isNewGame = false, string? steamGridDbApiKey = null)
    {
        InitializeComponent();
        _viewModel = new GameEditViewModel(game, categories, iconExtractorService, isNewGame, steamGridDbApiKey);
        DataContext = _viewModel;

        if (Application.Current?.MainWindow is { IsVisible: true } main)
        {
            Owner = main;
        }

        Loaded += (s, e) =>
        {
            if (Owner != null)
            {
                Left = Owner.Left + (Owner.ActualWidth - ActualWidth) / 2;
                Top = Owner.Top + (Owner.ActualHeight - ActualHeight) / 2;
            }
            Activate();
        };

        _viewModel.RequestClose += success =>
        {
            DialogResult = success;
            Close();
        };
    }
}
