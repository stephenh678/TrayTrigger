using System.Collections.Generic;
using System.Windows;
using TrayTrigger.Models;
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
    public ScanForGamesDialog(MainViewModel mainViewModel, List<DiscoveredSteamGame> steamGames, List<DiscoveredGogGame> gogGames, List<DiscoveredEaGame> eaGames, List<DiscoveredEpicGame> epicGames, List<DiscoveredUbisoftGame> ubisoftGames, List<DiscoveredXboxGame> xboxGames, List<GameCandidate> folderCandidates)
    {
        InitializeComponent();
        WindowThemeService.PrepareForFirstShow(this);

        var vm = new ScanForGamesViewModel(steamGames, gogGames, eaGames, epicGames, ubisoftGames, xboxGames, folderCandidates,
            onIgnoreCandidate: c => mainViewModel.IgnoreGamePath(c.ExePath, c.Name),
            onIgnoreSteamGame: g => mainViewModel.IgnoreSteamGame(g.AppId, g.Name),
            onIgnoreGogGame: g => mainViewModel.IgnoreGogGame(g.GameId, g.Name),
            onIgnoreEaGame: g => mainViewModel.IgnoreEaGame(g.ContentId, g.Name),
            onIgnoreEpicGame: g => mainViewModel.IgnoreEpicGame(g.AppName, g.Name),
            onIgnoreUbisoftGame: g => mainViewModel.IgnoreUbisoftGame(g.GameId, g.Name),
            onIgnoreXboxGame: g => mainViewModel.IgnoreXboxGame(g.Aumid, g.Name));

        vm.ImportConfirmed += (selectedSteamGames, selectedGogGames, selectedEaGames, selectedEpicGames, selectedUbisoftGames, selectedXboxGames, selectedFolderCandidates) =>
        {
            _ = mainViewModel.ImportScanResultsAsync(selectedSteamGames, selectedGogGames, selectedEaGames, selectedEpicGames, selectedUbisoftGames, selectedXboxGames, selectedFolderCandidates);
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
