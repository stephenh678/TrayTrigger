using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

public partial class GameDetailsDialog : Window
{
    private readonly GameDetailsViewModel _viewModel;
    private readonly double _defaultWidth;
    private readonly double _defaultHeight;

    public GameDetailsDialog(GameDetailsViewModel viewModel)
    {
        InitializeComponent();
        _defaultWidth = Width;
        _defaultHeight = Height;
        _viewModel = viewModel;
        WindowThemeService.PrepareForFirstShow(this);
        WindowHelper.RemoveMinimizeAndMaximize(this);
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
            _ = _viewModel.LoadInstallDriveAsync();
        };
    }

    /// <summary>
    /// Use the size the player last left this window at, in place of the XAML default. Ignored
    /// when it is missing, below the window's minimum, or larger than the work area, so a size
    /// saved on a bigger display can't come back too big to reach its buttons.
    /// </summary>
    public void ApplySavedSize(double? width, double? height)
    {
        if (width is not double w || height is not double h || double.IsNaN(w) || double.IsNaN(h)) return;
        var workArea = SystemParameters.WorkArea;
        if (w < MinWidth || h < MinHeight || w > workArea.Width || h > workArea.Height) return;
        Width = w;
        Height = h;
    }

    /// <summary>
    /// The size to remember, read once the window has closed; null when it is at the XAML default,
    /// so a player who never resized it keeps following the default if that is ever changed.
    /// </summary>
    public (double Width, double Height)? SizeToRemember
    {
        get
        {
            double width = Math.Round(ActualWidth), height = Math.Round(ActualHeight);
            if (width <= 0 || height <= 0) return null;
            return width == _defaultWidth && height == _defaultHeight ? null : (width, height);
        }
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
