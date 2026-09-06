using System.Windows;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

public partial class GameDetailsDialog : Window
{
    public GameDetailsDialog(GameDetailsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.RequestClose += () =>
        {
            Dispatcher.Invoke(Close);
        };

        // Apply dark titlebar
        WindowThemeService.ApplyDarkTitleBar(this);

        if (Application.Current?.MainWindow is { IsVisible: true } main && main != this)
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
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
