using System;
using System.Collections.Generic;
using System.Windows;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

public partial class FolderBatchImportDialog : Window
{
    private readonly FolderBatchImportViewModel _viewModel;

    public List<GameCandidate> SelectedGames { get; private set; } = new();

    public FolderBatchImportDialog(string folderPath, List<GameCandidate> candidates, IEnumerable<string> existingExePaths)
    {
        InitializeComponent();
        _viewModel = new FolderBatchImportViewModel(folderPath, candidates, existingExePaths);
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

        _viewModel.ImportConfirmed += selected =>
        {
            SelectedGames = selected;
            DialogResult = true;
            Close();
        };

        _viewModel.RequestClose += () =>
        {
            if (DialogResult != true)
            {
                DialogResult = false;
            }
            Close();
        };
    }
}
