using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

public partial class GameDetailsDialog : Window
{
    private readonly GameDetailsViewModel _viewModel;

    public GameDetailsDialog(GameDetailsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        WindowThemeService.PrepareForFirstShow(this);
        DataContext = viewModel;

        viewModel.RequestClose += () =>
        {
            Dispatcher.Invoke(Close);
        };

        if (WindowHelper.ActiveOwner() is { } main && main != this)
        {
            Owner = main;
        }

        Loaded += (s, e) =>
        {
            WindowThemeService.CenterOverOwner(this);
            Activate();
        };
    }

    /// <summary>
    /// Drop straight into the match picker once the dialog's first load settles. Used by the
    /// library card's "Change Match..." item, which opens this dialog only to reach the picker -
    /// having to click Change match again on arrival would defeat the point.
    ///
    /// It has to wait: ChangeMatchCommand is gated on CanChangeMatch, which is false while the
    /// details are still on their way in, so firing it at Loaded would silently do nothing.
    /// </summary>
    public void OpenMatchPickerWhenReady()
    {
        if (_viewModel.CanChangeMatch)
        {
            QueueMatchPicker();
            return;
        }

        void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(GameDetailsViewModel.CanChangeMatch) || !_viewModel.CanChangeMatch)
                return;

            _viewModel.PropertyChanged -= OnPropertyChanged;
            QueueMatchPicker();
        }

        _viewModel.PropertyChanged += OnPropertyChanged;

        // The picker is modal, so it must not open from inside the load-completion callback that
        // raised CanChangeMatch - that would block the rest of that method behind the dialog.
        Closed += (s, e) => _viewModel.PropertyChanged -= OnPropertyChanged;
    }

    private void QueueMatchPicker() => Dispatcher.BeginInvoke(
        DispatcherPriority.Background,
        () =>
        {
            if (IsLoaded && _viewModel.ChangeMatchCommand.CanExecute(null))
                _viewModel.ChangeMatchCommand.Execute(null);
        });

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
