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
            if (Owner != null)
            {
                Left = Owner.Left + (Owner.ActualWidth - ActualWidth) / 2;
                Top = Owner.Top + (Owner.ActualHeight - ActualHeight) / 2;
            }
            Activate();
        };

        vm.RequestClose += () => Close();
        DataContext = vm;
    }
}
