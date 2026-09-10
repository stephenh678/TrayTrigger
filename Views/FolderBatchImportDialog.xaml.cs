using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

public partial class FolderBatchImportDialog : Window
{
    private readonly FolderBatchImportViewModel _viewModel;

    public List<GameCandidate> SelectedGames { get; private set; } = new();

    /// <summary>True if the user checked "remember this folder as a scan location" on confirm.</summary>
    public bool RememberAsScanLocation { get; private set; }

    public FolderBatchImportDialog(string folderPath, List<GameCandidate> candidates, Func<GameCandidate, bool> isAlreadyImported, bool isAlreadyScanLocation, Action<GameCandidate> onIgnoreCandidate)
    {
        InitializeComponent();
        WindowThemeService.PrepareForFirstShow(this);

        // A real, still-existing folder is one genuine library root (the case this checkbox is
        // for). ProcessFolderAddBatchAsync instead passes a synthetic "N folders" label for a
        // multi-folder drop, which isn't a single path worth remembering as one scan location.
        bool canRememberAsScanLocation = Directory.Exists(folderPath);

        _viewModel = new FolderBatchImportViewModel(folderPath, candidates, isAlreadyImported, canRememberAsScanLocation, isAlreadyScanLocation, onIgnoreCandidate);
        DataContext = _viewModel;

        Owner = WindowHelper.ActiveOwner();

        Loaded += (s, e) =>
        {
            WindowThemeService.CenterOverOwner(this);
            Activate();
        };

        _viewModel.ImportConfirmed += (selected, rememberAsScanLocation) =>
        {
            SelectedGames = selected;
            RememberAsScanLocation = rememberAsScanLocation;
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
