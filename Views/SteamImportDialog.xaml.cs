using System.Linq;
using System.Windows;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

public partial class SteamImportDialog : Window
{
    public SteamImportDialog(MainViewModel mainViewModel)
    {
        InitializeComponent();
        WindowThemeService.PrepareForFirstShow(this);

        var existingAppIds = mainViewModel.Games
            .Where(g => g.IsSteamGame && !string.IsNullOrEmpty(g.Game.SteamAppId))
            .Select(g => g.Game.SteamAppId!)
            .ToList();

        var scanner = new SteamScannerService();
        var vm = new SteamImportViewModel(scanner, existingAppIds);

        vm.ImportConfirmed += selectedGames =>
        {
            mainViewModel.ImportSteamGames(selectedGames);
        };

        if (Application.Current?.MainWindow is { IsVisible: true } main)
        {
            Owner = main;
        }

        Loaded += (s, e) =>
        {
            WindowThemeService.CenterOverOwner(this);
            Activate();
        };

        vm.RequestClose += () => Close();
        DataContext = vm;
    }
}
