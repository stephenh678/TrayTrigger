using System.Collections.Generic;
using System.Windows;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

/// <summary>
/// The bulk install prompt shown after a "Scan for Games" run finds one or more new games -
/// see <see cref="ImportCoordinator.ScanForGamesAsync"/>, which runs the scan and only opens this
/// dialog when there's something to show.
/// </summary>
public partial class ScanForGamesDialog : Window
{
    public ScanForGamesDialog(MainViewModel mainViewModel, List<DiscoveredSteamGame> steamGames, List<GameCandidate> folderCandidates)
    {
        InitializeComponent();
        WindowThemeService.PrepareForFirstShow(this);

        var vm = new ScanForGamesViewModel(steamGames, folderCandidates,
            onIgnoreCandidate: c => mainViewModel.IgnoreGamePath(c.ExePath, c.Name),
            onIgnoreSteamGame: g => mainViewModel.IgnoreSteamGame(g.AppId, g.Name));

        vm.ImportConfirmed += (selectedSteamGames, selectedFolderCandidates) =>
        {
            _ = mainViewModel.ImportScanResultsAsync(selectedSteamGames, selectedFolderCandidates);
        };

        Owner = WindowHelper.ActiveOwner();

        Loaded += (s, e) =>
        {
            WindowThemeService.CenterOverOwner(this);
            Activate();
        };

        vm.RequestClose += () => Close();
        DataContext = vm;
    }
}
